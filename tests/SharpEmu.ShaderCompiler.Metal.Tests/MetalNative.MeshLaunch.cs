// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A mesh draw launched from a GPU-written grid, as the render host launches an indirect draw
/// of a primitive shader: <see cref="Gen5MslMeshLaunch.KernelSource"/> reads the guest record
/// and writes one launch slot, and the draw binds the slot's draw-info block and reads its grid
/// with drawMeshThreadgroupsWithIndirectBuffer.
/// </summary>
/// <param name="Records">The guest records the kernel reads (buffer 0).</param>
/// <param name="Slot">The launch slot with its parameters filled (buffer 1).</param>
/// <param name="DrawInfoIndex">The mesh slot the draw-info block is bound at.</param>
internal sealed record MeshIndirectLaunch(byte[] Records, byte[] Slot, uint DrawInfoIndex)
{
    /// <summary>The slot as the kernel left it, after the draw.</summary>
    public byte[] Written { get; set; } = [];
}

internal static partial class MetalNative
{
    // MTLRenderStageVertex | MTLRenderStageObject | MTLRenderStageMesh.
    private const nuint MeshLaunchStages = 1 | 8 | 16;

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendWaitForFence(nint receiver, nint selector, nint fence, nuint stages);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendDrawMeshIndirect(nint receiver, nint selector, nint indirectBuffer,
        nuint indirectBufferOffset, MtlSize threadsPerObjectThreadgroup, MtlSize threadsPerMeshThreadgroup);

    /// <summary>Runs the launch kernel once per slot over shared copies of the two buffers and
    /// returns the slots afterwards.</summary>
    public static bool TryRunMeshLaunchKernel(byte[] records, byte[] slots, uint launches, out byte[] result, out string error)
    {
        result = [];
        if (!TryCompileLibrary(Gen5MslMeshLaunch.KernelSource, out var library, out error))
        {
            return false;
        }

        var recordBuffer = SharedBuffer(records);
        var slotBuffer = SharedBuffer(slots);
        if (recordBuffer == 0 || slotBuffer == 0)
        {
            error = "failed to create the launch buffers";
            return false;
        }

        return RunKernel(
            library,
            Gen5MslMeshLaunch.KernelFunction,
            [(recordBuffer, 0), (slotBuffer, 1)],
            [],
            launches,
            slotBuffer,
            slots.Length,
            out result,
            out error);
    }

    // The compute half of an indirect launch, encoded ahead of the draw's pass on the same
    // command buffer; the pass waits on the fence it updates. Returns the slot buffer.
    private static nint EncodeMeshLaunch(nint commandBuffer, MeshIndirectLaunch launch, out nint fence, out string error)
    {
        fence = 0;
        if (!TryCompileLibrary(Gen5MslMeshLaunch.KernelSource, out var library, out error))
        {
            return 0;
        }

        var function = Send(library, Selector("newFunctionWithName:"), NsString(Gen5MslMeshLaunch.KernelFunction));
        nint nsError = 0;
        var pipeline = function == 0
            ? 0
            : Send(Device.Value, Selector("newComputePipelineStateWithFunction:error:"), function, ref nsError);
        var records = SharedBuffer(launch.Records);
        var slot = SharedBuffer(launch.Slot);
        fence = Send(Device.Value, Selector("newFence"));
        if (pipeline == 0 || records == 0 || slot == 0 || fence == 0)
        {
            error = $"the launch kernel could not be set up: {DescribeError(nsError)}";
            return 0;
        }

        var encoder = Send(commandBuffer, Selector("computeCommandEncoder"));
        SendVoid(encoder, Selector("setComputePipelineState:"), pipeline);
        SendSetBuffer(encoder, Selector("setBuffer:offset:atIndex:"), records, 0, 0);
        SendSetBuffer(encoder, Selector("setBuffer:offset:atIndex:"), slot, 0, 1);
        var one = new MtlSize { Width = 1, Height = 1, Depth = 1 };
        SendDispatch(encoder, Selector("dispatchThreadgroups:threadsPerThreadgroup:"), one, one);
        SendVoid(encoder, Selector("updateFence:"), fence);
        SendVoid(encoder, Selector("endEncoding"));
        return slot;
    }

    // The draw half: the slot's block at the draw-info index and its grid as the launch.
    private static void DrawMeshLaunch(nint encoder, MeshIndirectLaunch launch, nint slot, nint fence, uint threadsPerThreadgroup)
    {
        SendWaitForFence(encoder, Selector("waitForFence:beforeStages:"), fence, MeshLaunchStages);
        SendSetBuffer(encoder, Selector("setMeshBuffer:offset:atIndex:"), slot, 0, launch.DrawInfoIndex);
        SendUseResource(encoder, Selector("useResource:usage:"), slot, ReadWriteUsage);
        SendDrawMeshIndirect(
            encoder,
            Selector("drawMeshThreadgroupsWithIndirectBuffer:indirectBufferOffset:threadsPerObjectThreadgroup:threadsPerMeshThreadgroup:"),
            slot,
            Gen5MslMeshLaunch.GridByteOffset,
            new MtlSize { Width = 1, Height = 1, Depth = 1 },
            new MtlSize { Width = threadsPerThreadgroup, Height = 1, Depth = 1 });
    }

    private static byte[] ReadSharedBuffer(nint buffer, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(Send(buffer, Selector("contents")), bytes, 0, length);
        return bytes;
    }
}
