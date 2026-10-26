// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SharpEmu.Gpu.Metal.V2;

// ZPASS_DONE occlusion queries counted on the device. A query is a begin dump (the even qword of each depth
// block's pair), the draws it measures, and an end dump (the odd qword); the guest adds up end minus begin over
// the blocks. Metal counts the samples that pass the depth and stencil tests of the draws a render encoder
// sends while its visibility result mode is counting, into an offset of the pass's visibility result buffer,
// written when the pass ends. So a begin dump takes a zeroed slot that the draws count into until the end
// dump; the begin pairs read zero, and the end dump copies the slot's count into its first block once the pass
// that counted it has ended (the ordered copies land at the next encoder boundary). The buffer accumulates, so a
// query spanning passes adds them up.
//
// Without this every query read visible: Ghost of Tsushima's lens glows of the lights Jin and the cliff hide
// were drawn at full strength, as white orbs over the scene.
internal sealed partial class MetalRenderHost
{
    // SHARPEMU_METAL_V2_OCCLUSION=0 turns the device counts off, and every query reads visible.
    internal static bool DeviceOcclusionQueries { get; set; } =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_OCCLUSION") != "0";

    private const int OcclusionSlotCount = 1 << 18;
    private const ulong OcclusionReady = 1UL << 63;
    private const nuint VisibilityResultModeDisabled = 0;
    private const nuint VisibilityResultModeCounting = 2;
    private const nuint VisibilityResultTypeAccumulate = 1;

    private static readonly nint SelSetVisibilityResultBuffer = MetalNative.Selector("setVisibilityResultBuffer:");
    private static readonly nint SelSetVisibilityResultType = MetalNative.Selector("setVisibilityResultType:");
    private static readonly nint SelSetVisibilityResultMode = MetalNative.Selector("setVisibilityResultMode:offset:");
    private static readonly nint SelOcclusionNewBuffer = MetalNative.Selector("newBufferWithLength:options:");
    private static readonly nint SelOcclusionContents = MetalNative.Selector("contents");

    private nint _occlusionBuffer;
    private nint _occlusionContents;
    private readonly ulong[] _occlusionSlotSerials = new ulong[OcclusionSlotCount];
    private int _occlusionNextSlot;

    // The slot the draws count into while a query is open, -1 between queries; and what the open render encoder
    // was last told.
    private int _occlusionSlot = -1;
    private int _encoderOcclusionSlot = -1;
    private long _occlusionDumps;

    /// <summary>Occlusion dumps written from device counts since the last read.</summary>
    public long TakeOcclusionDumps() => Interlocked.Exchange(ref _occlusionDumps, 0);

    public bool ResetOcclusionCounter(int queueId) => DeviceOcclusionQueries;

    public bool DumpOcclusionCounter(int queueId, ulong address, uint instanceMask, uint strideBytes)
    {
        if (!DeviceOcclusionQueries)
        {
            return false;
        }

        if (OnCommandThread)
        {
            _commandThread!.Post(() => RecordOcclusionDump(address, instanceMask, strideBytes));
            return true;
        }

        RecordOcclusionDump(address, instanceMask, strideBytes);
        return true;
    }

    private nint OcclusionBuffer()
    {
        if (_occlusionBuffer != 0)
        {
            return _occlusionBuffer;
        }

        _occlusionBuffer = MetalNative.SendNewBuffer(
            _device, SelOcclusionNewBuffer, (nuint)OcclusionSlotCount * sizeof(ulong), Mtl.ResourceStorageModeShared);
        if (_occlusionBuffer == 0)
        {
            throw Fatal("The occlusion query buffer could not be created.");
        }

        SetLabel(_occlusionBuffer, "SharpEmu occlusion queries");
        _occlusionContents = MetalNative.Send(_occlusionBuffer, SelOcclusionContents);
        return _occlusionBuffer;
    }

    // Every render pass that draws guest geometry can count: the buffer has to be the pass's from its start.
    private void AttachOcclusionBuffer(nint descriptor)
    {
        if (!DeviceOcclusionQueries)
        {
            return;
        }

        MetalNative.SendVoid(descriptor, SelSetVisibilityResultBuffer, OcclusionBuffer());
        MetalV2Native.SendVoidValue(descriptor, SelSetVisibilityResultType, VisibilityResultTypeAccumulate);
    }

    // Before a draw: the open render encoder counts into the open query's slot, or not at all.
    private void ApplyOcclusionCounting()
    {
        if (!DeviceOcclusionQueries || _renderEncoder == 0 || _encoderOcclusionSlot == _occlusionSlot)
        {
            return;
        }

        MetalV2Native.SendRange(
            _renderEncoder,
            SelSetVisibilityResultMode,
            _occlusionSlot >= 0 ? VisibilityResultModeCounting : VisibilityResultModeDisabled,
            _occlusionSlot >= 0 ? (nuint)_occlusionSlot * sizeof(ulong) : 0);
        _encoderOcclusionSlot = _occlusionSlot;
    }

    private int AllocateOcclusionSlot()
    {
        OcclusionBuffer();
        var slot = _occlusionNextSlot;
        _occlusionNextSlot = (slot + 1) % OcclusionSlotCount;
        if (_occlusionSlotSerials[slot] > CompletedSerial)
        {
            MetalV2Diagnostics.WarnOnce(
                "occlusion-ring",
                $"An occlusion query slot is reused before the submission that last counted into it completed: " +
                $"slots={OcclusionSlotCount} serial={_occlusionSlotSerials[slot]} completed={CompletedSerial}.");
        }

        Marshal.WriteInt64(_occlusionContents, slot * sizeof(ulong), 0);
        _occlusionSlotSerials[slot] = SubmissionSerial;
        return slot;
    }

    // On the command thread, in order with the draws: a begin dump opens a query, an end dump closes it.
    private void RecordOcclusionDump(ulong address, uint instanceMask, uint strideBytes)
    {
        DrainDraws();
        var begin = (address & 0x8) == 0;
        int slot;
        if (begin)
        {
            if (_occlusionSlot >= 0)
            {
                MetalV2Diagnostics.WarnOnce(
                    "occlusion-nested",
                    "An occlusion query begins while another is open; the open one counts only up to here.");
            }

            slot = AllocateOcclusionSlot();
            _occlusionSlot = slot;
        }
        else
        {
            slot = _occlusionSlot;
            _occlusionSlot = -1;
            if (slot >= 0)
            {
                _occlusionSlotSerials[slot] = SubmissionSerial;
            }
        }

        ApplyOcclusionCounting();
        var countedBlock = instanceMask == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(instanceMask);
        for (var block = 0; block < 24; block++)
        {
            if ((instanceMask & (1u << block)) != 0)
            {
                EncodeOcclusionQword(address + ((ulong)block * strideBytes), !begin && block == countedBlock ? slot : -1);
            }
        }

        _occlusionDumps++;
    }

    // One dump qword: ready with no samples, and the slot's count in its low word for the end dump's counted block.
    private void EncodeOcclusionQword(ulong guestAddress, int slot)
    {
        var destination = _guestMemory.ResolveWritable(guestAddress, sizeof(ulong));
        if (!destination.IsValid)
        {
            MetalV2Diagnostics.WarnOnce(
                $"occlusion-window:{guestAddress >> 20:X}",
                $"An occlusion dump has no writable guest window: address=0x{guestAddress:X16}.");
            return;
        }

        var staging = _guestMemory.Allocate(sizeof(ulong), sizeof(ulong), out var payload);
        if (!staging.IsValid)
        {
            throw Fatal($"An occlusion dump could not be staged: address=0x{guestAddress:X16}.");
        }

        BinaryPrimitives.WriteUInt64LittleEndian(payload, OcclusionReady);
        Span<DeferredLabelCopy> copies = stackalloc DeferredLabelCopy[2];
        copies[0] = new DeferredLabelCopy(
            staging.Buffer, staging.Offset, destination.Buffer, destination.Offset, sizeof(ulong), guestAddress);
        var count = 1;
        if (slot >= 0)
        {
            // Visibility results count in 64 bits; a query's samples fit the low word with the ready bit above.
            copies[1] = new DeferredLabelCopy(
                OcclusionBuffer(), (ulong)slot * sizeof(ulong), destination.Buffer, destination.Offset, sizeof(uint), guestAddress);
            count = 2;
        }

        EncodeOrderedCopies(copies[..count], guestAddress, null, OcclusionReady, MetalGpuWriteOrigin.EndOfPipeLabel,
            "occlusion dump", null, sizeof(ulong));
    }
}
