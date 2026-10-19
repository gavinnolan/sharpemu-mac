// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Ports are process-wide (at most four open), so this suite runs with the other port users.
[Collection(SchedulingStateCollection.Name)]
public sealed class VideoOutExtendedExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ScratchAddress = MemoryBase + 0x100;
    private const ulong EventAddress = MemoryBase + 0x200;
    private const ulong OutCountAddress = MemoryBase + 0x300;
    private const ulong TimeoutAddress = MemoryBase + 0x380;
    private const ulong StructAddress = MemoryBase + 0x400;
    private const ulong SecondStructAddress = MemoryBase + 0x600;

    private const int InvalidValue = unchecked((int)0x80290001);
    private const int InvalidAddress = unchecked((int)0x80290002);
    private const int ResourceBusy = unchecked((int)0x80290009);
    private const int InvalidHandle = unchecked((int)0x8029000B);
    private const int InvalidEventQueue = unchecked((int)0x8029000C);
    private const int InvalidEvent = unchecked((int)0x8029000D);
    private const int UnsupportedOperation = unchecked((int)0x8029001C);
    private const int TimedOut = unchecked((int)0x8002003C);
    private const int NeverOpenedHandle = 0x7FFF_0000;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10000);
    private readonly CpuContext _context;
    private readonly int _handle;

    public VideoOutExtendedExportsTests()
    {
        _context = new CpuContext(_memory, Generation.Gen5);
        _handle = OpenPort();
    }

    public void Dispose() => ClosePort(_handle);

    private int OpenPort()
    {
        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = 0;
        var handle = VideoOutExports.VideoOutOpen(_context);
        Assert.True(handle > 0);
        return handle;
    }

    private void ClosePort(int handle)
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ = VideoOutExports.VideoOutClose(_context);
    }

    private ulong CreateQueue()
    {
        _context[CpuRegister.Rdi] = ScratchAddress;
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelCreateEqueue(_context));
        Assert.True(_context.TryReadUInt64(ScratchAddress, out var queue));
        return queue;
    }

    private void DeleteQueue(ulong queue)
    {
        _context[CpuRegister.Rdi] = queue;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelDeleteEqueue(_context));
    }

    private int Call(Func<CpuContext, int> export, ulong rdi, ulong rsi = 0, ulong rdx = 0)
    {
        _context[CpuRegister.Rdi] = rdi;
        _context[CpuRegister.Rsi] = rsi;
        _context[CpuRegister.Rdx] = rdx;
        return export(_context);
    }

    // Waits up to timeoutUsec for one event; returns the wait result.
    private int WaitOne(ulong queue, uint timeoutUsec)
    {
        Assert.True(_memory.TryWrite(TimeoutAddress, BitConverter.GetBytes(timeoutUsec)));
        _context[CpuRegister.Rdi] = queue;
        _context[CpuRegister.Rsi] = EventAddress;
        _context[CpuRegister.Rdx] = 1;
        _context[CpuRegister.Rcx] = OutCountAddress;
        _context[CpuRegister.R8] = TimeoutAddress;
        return KernelEventQueueCompatExports.KernelWaitEqueue(_context);
    }

    private static object GetPort(int handle)
    {
        var ports = (System.Collections.IDictionary)typeof(VideoOutExports)
            .GetField("_ports", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return ports[handle]!;
    }

    private byte[] Fill(ulong address, int length, byte value = 0xCC)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
        return bytes;
    }

    private byte[] Read(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    [Fact]
    public void DeleteFlipEvent_AddDeleteRedeleteLifecycle()
    {
        var queue = CreateQueue();
        try
        {
            Assert.Equal(0, Call(VideoOutExports.VideoOutAddFlipEvent, queue, (ulong)_handle, 0x55));

            // Registered: a completed flip is delivered.
            Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, -1, 1, 7, false, out var first));
            VideoOutExports.CompleteFlip(first);
            VideoOutExports.MarkFlipPresented(first);
            Assert.Equal(0, WaitOne(queue, 100_000));
            Assert.Equal(-13, BinaryPrimitives.ReadInt16LittleEndian(Read(EventAddress + 8, 2)));

            // A flip completed before the delete is still queued; deletion drops it.
            Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, -1, 1, 8, false, out var second));
            VideoOutExports.CompleteFlip(second);
            VideoOutExports.MarkFlipPresented(second);
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteFlipEvent, queue, (ulong)_handle));
            Assert.Equal(TimedOut, WaitOne(queue, 1_000));

            // Unregistered: later flips are not delivered.
            Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, -1, 1, 9, false, out var third));
            VideoOutExports.CompleteFlip(third);
            VideoOutExports.MarkFlipPresented(third);
            Assert.Equal(TimedOut, WaitOne(queue, 1_000));

            // Deleting again: the kernel's ENOENT maps to success.
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteFlipEvent, queue, (ulong)_handle));

            // Re-adding works after deletion.
            Assert.Equal(0, Call(VideoOutExports.VideoOutAddFlipEvent, queue, (ulong)_handle, 0x66));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteFlipEvent, queue, (ulong)_handle));

            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutDeleteFlipEvent, queue, NeverOpenedHandle));
            Assert.Equal(InvalidEventQueue, Call(VideoOutExports.VideoOutDeleteFlipEvent, 0, (ulong)_handle));
            Assert.Equal(InvalidEventQueue, Call(VideoOutExports.VideoOutDeleteFlipEvent, 0xDEAD_BEEF, (ulong)_handle));
            // An unknown handle is reported before the queue is looked at.
            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutDeleteFlipEvent, 0, NeverOpenedHandle));
        }
        finally
        {
            DeleteQueue(queue);
        }
    }

    [Fact]
    public void DeleteOutputModeEvent_DropsTheQueuedEventAndStopsNotifications()
    {
        var queue = CreateQueue();
        try
        {
            // Subscribing queues the current mode at once; deleting drops it.
            Assert.Equal(0, Call(VideoOutExports.VideoOutAddOutputModeEvent, queue, (ulong)_handle, 1));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, queue, (ulong)_handle));
            Assert.Equal(TimedOut, WaitOne(queue, 1_000));

            _context[CpuRegister.Rdi] = (ulong)_handle;
            _context[CpuRegister.Rsi] = 1;
            _context[CpuRegister.Rdx] = 0;
            _context[CpuRegister.Rcx] = 0;
            _context[CpuRegister.R8] = 0;
            Assert.Equal(0, VideoOutExports.VideoOutConfigureOutput(_context));
            Assert.Equal(TimedOut, WaitOne(queue, 1_000));

            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, queue, (ulong)_handle));

            Assert.Equal(0, Call(VideoOutExports.VideoOutAddOutputModeEvent, queue, (ulong)_handle, 2));
            Assert.Equal(0, WaitOne(queue, 100_000));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, queue, (ulong)_handle));

            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, queue, NeverOpenedHandle));
            Assert.Equal(InvalidEventQueue, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, 0, (ulong)_handle));
        }
        finally
        {
            DeleteQueue(queue);
        }
    }

    [Fact]
    public void DeleteFlipEvent_KeepsTheQueuedEventAnotherPortStillFeeds()
    {
        var other = OpenPort();
        var queue = CreateQueue();
        try
        {
            Assert.Equal(0, Call(VideoOutExports.VideoOutAddOutputModeEvent, queue, (ulong)_handle, 1));
            Assert.Equal(0, Call(VideoOutExports.VideoOutAddOutputModeEvent, queue, (ulong)other, 2));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, queue, (ulong)_handle));
            Assert.Equal(0, WaitOne(queue, 100_000));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, queue, (ulong)other));
        }
        finally
        {
            DeleteQueue(queue);
            ClosePort(other);
        }
    }

    [Fact]
    public void VrrActiveStatusEvent_AddDeleteRedeleteAndErrors()
    {
        var queue = CreateQueue();
        try
        {
            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutAddVrrActiveStatusEvent, queue, NeverOpenedHandle));
            Assert.Equal(InvalidEventQueue, Call(VideoOutExports.VideoOutAddVrrActiveStatusEvent, 0, (ulong)_handle));

            Assert.Equal(0, Call(VideoOutExports.VideoOutAddVrrActiveStatusEvent, queue, (ulong)_handle, 3));
            Assert.Equal(0, Call(VideoOutExports.VideoOutAddVrrActiveStatusEvent, queue, (ulong)_handle, 4));
            // The output never changes VRR state here, so nothing is signalled.
            Assert.Equal(TimedOut, WaitOne(queue, 1_000));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteVrrActiveStatusEvent, queue, (ulong)_handle));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteVrrActiveStatusEvent, queue, (ulong)_handle));

            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutDeleteVrrActiveStatusEvent, queue, NeverOpenedHandle));
            Assert.Equal(InvalidEventQueue, Call(VideoOutExports.VideoOutDeleteVrrActiveStatusEvent, 0, (ulong)_handle));
        }
        finally
        {
            DeleteQueue(queue);
        }
    }

    [Fact]
    public void GetEventCount_DecodesBits12To15AndValidatesTheEvent()
    {
        var kevent = new byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(kevent, 6);
        BinaryPrimitives.WriteInt16LittleEndian(kevent.AsSpan(0x08), -13);
        BinaryPrimitives.WriteUInt64LittleEndian(kevent.AsSpan(0x10), 0x1234_0000_0000_0000UL | (0xBUL << 12) | 0xFFF);
        Assert.True(_memory.TryWrite(StructAddress, kevent));
        Assert.Equal(0xB, Call(VideoOutExports.VideoOutGetEventCount, StructAddress));

        BinaryPrimitives.WriteUInt64LittleEndian(kevent.AsSpan(0x10), 0xFUL << 12);
        Assert.True(_memory.TryWrite(StructAddress, kevent));
        Assert.Equal(15, Call(VideoOutExports.VideoOutGetEventCount, StructAddress));

        BinaryPrimitives.WriteInt16LittleEndian(kevent.AsSpan(0x08), -11);
        Assert.True(_memory.TryWrite(StructAddress, kevent));
        Assert.Equal(InvalidEvent, Call(VideoOutExports.VideoOutGetEventCount, StructAddress));

        Assert.Equal(InvalidAddress, Call(VideoOutExports.VideoOutGetEventCount, 0));
    }

    [Fact]
    public void GetEventCount_ReadsTheCountOfADeliveredEvent()
    {
        var queue = CreateQueue();
        try
        {
            Assert.Equal(0, Call(VideoOutExports.VideoOutAddOutputModeEvent, queue, (ulong)_handle, 1));
            Assert.Equal(0, WaitOne(queue, 100_000));
            Assert.Equal(1, Call(VideoOutExports.VideoOutGetEventCount, EventAddress));
            Assert.Equal(0, Call(VideoOutExports.VideoOutDeleteOutputModeEvent, queue, (ulong)_handle));
        }
        finally
        {
            DeleteQueue(queue);
        }
    }

    [Fact]
    public void GetResolutionStatus_WritesExactly48Bytes()
    {
        const int size = 0x30;
        Fill(StructAddress, size + 1);
        Assert.Equal(0, Call(VideoOutExports.VideoOutGetResolutionStatus, (ulong)_handle, StructAddress));

        var status = Read(StructAddress, size + 1);
        var port = GetPort(_handle);
        var width = (uint)port.GetType().GetProperty("OutputWidth")!.GetValue(port)!;
        var height = (uint)port.GetType().GetProperty("OutputHeight")!.GetValue(port)!;
        Assert.Equal(width, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0x00)));
        Assert.Equal(height, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0x04)));
        Assert.Equal(width, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0x08)));
        Assert.Equal(height, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0x0C)));

        // refreshRate matches sceVideoOutGetOutputStatus's.
        Fill(SecondStructAddress, 0x30);
        Assert.Equal(0, Call(VideoOutExports.VideoOutGetOutputStatus, (ulong)_handle, SecondStructAddress));
        var output = Read(SecondStructAddress, 0x30);
        Assert.Equal(BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0x08)),
            BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x10)));

        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(status.AsSpan(0x18)));
        Assert.All(status[0x1C..size], value => Assert.Equal(0, value));
        Assert.Equal(0xCC, status[size]);

        Assert.Equal(InvalidAddress, Call(VideoOutExports.VideoOutGetResolutionStatus, (ulong)_handle, 0));
        Fill(StructAddress, size);
        Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutGetResolutionStatus, NeverOpenedHandle, StructAddress));
        Assert.All(Read(StructAddress, size), value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void GetOutputStatusWithVrr_IsTheOutputStatusPaddedTo80Bytes()
    {
        const int size = 0x50;
        Fill(StructAddress, size + 0x10);
        Assert.Equal(0, Call(VideoOutExports.VideoOutGetOutputStatusWithVrr, (ulong)_handle, StructAddress));
        Assert.Equal(0, Call(VideoOutExports.VideoOutGetOutputStatus, (ulong)_handle, SecondStructAddress));

        var status = Read(StructAddress, size + 0x10);
        Assert.Equal(Read(SecondStructAddress, 0x30), status[..0x30]);
        Assert.All(status[0x30..size], value => Assert.Equal(0, value));
        Assert.All(status[size..], value => Assert.Equal(0xCC, value));

        Assert.Equal(InvalidAddress, Call(VideoOutExports.VideoOutGetOutputStatusWithVrr, (ulong)_handle, 0));
        Fill(StructAddress, size);
        Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutGetOutputStatusWithVrr, NeverOpenedHandle, StructAddress));
        Assert.All(Read(StructAddress, size), value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void GetBufferLabelAddress_ExposesSixteenLabelsReleasedByReplacementFlips()
    {
        Fill(ScratchAddress, 8, 0x5A);
        Assert.Equal(InvalidAddress, Call(VideoOutExports.VideoOutGetBufferLabelAddress, (ulong)_handle, 0));
        Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutGetBufferLabelAddress, NeverOpenedHandle, ScratchAddress));
        Assert.All(Read(ScratchAddress, 8), value => Assert.Equal(0x5A, value));

        // An unaligned output is fine.
        Fill(ScratchAddress, 10, 0x5A);
        Assert.Equal(16, Call(VideoOutExports.VideoOutGetBufferLabelAddress, (ulong)_handle, ScratchAddress + 1));
        var raw = Read(ScratchAddress, 10);
        Assert.Equal(0x5A, raw[0]);
        Assert.Equal(0x5A, raw[9]);
        var labels = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(1));
        Assert.NotEqual(0UL, labels);
        Assert.Equal(0UL, labels % 8);
        Assert.All(Read(labels, 16 * 8), value => Assert.Equal(0, value));

        // The same array on every query.
        Assert.Equal(16, Call(VideoOutExports.VideoOutGetBufferLabelAddress, (ulong)_handle, ScratchAddress));
        Assert.True(_context.TryReadUInt64(ScratchAddress, out var repeated));
        Assert.Equal(labels, repeated);

        // Ports have their own arrays.
        var other = OpenPort();
        try
        {
            Assert.Equal(16, Call(VideoOutExports.VideoOutGetBufferLabelAddress, (ulong)other, ScratchAddress));
            Assert.True(_context.TryReadUInt64(ScratchAddress, out var otherLabels));
            Assert.NotEqual(labels, otherLabels);
        }
        finally
        {
            ClosePort(other);
        }

        var port = GetPort(_handle);
        foreach (var index in new[] { 0, 1, 15 })
        {
            Assert.True(_context.TryWriteUInt64(labels + (ulong)(index * 8), 55));
        }

        // Registration clears exactly the registered buffers' labels.
        typeof(VideoOutExports).GetMethod("ResetBufferLabelsLocked", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [port, 0, 2]);
        Assert.True(_context.TryReadUInt64(labels, out var label0));
        Assert.True(_context.TryReadUInt64(labels + 8, out var label1));
        Assert.True(_context.TryReadUInt64(labels + 15 * 8, out var label15));
        Assert.Equal((0UL, 0UL, 55UL), (label0, label1, label15));

        // Make slots 0 and 1 flippable without a GPU-backed registration.
        var slots = (Array)port.GetType().GetProperty("BufferSlots")!.GetValue(port)!;
        foreach (var index in new[] { 0, 1 })
        {
            var slot = slots.GetValue(index)!;
            slot.GetType().GetProperty("GroupIndex")!.SetValue(slot, 0);
        }

        Assert.True(_context.TryWriteUInt64(labels, 1));
        Assert.True(_context.TryWriteUInt64(labels + 8, 1));

        void Flip(int index)
        {
            Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, index, 1, index, false, out var request));
            VideoOutExports.CompleteFlip(request);
            VideoOutExports.MarkFlipPresented(request);
        }

        Flip(0);
        Assert.True(_context.TryReadUInt64(labels, out label0));
        Assert.True(_context.TryReadUInt64(labels + 8, out label1));
        Assert.Equal((1UL, 1UL), (label0, label1));

        Flip(1);
        Assert.True(_context.TryReadUInt64(labels, out label0));
        Assert.True(_context.TryReadUInt64(labels + 8, out label1));
        Assert.Equal((0UL, 1UL), (label0, label1));

        Flip(-1);
        Assert.True(_context.TryReadUInt64(labels + 8, out label1));
        Assert.True(_context.TryReadUInt64(labels + 15 * 8, out label15));
        Assert.Equal((0UL, 55UL), (label1, label15));

        foreach (var index in new[] { 0, 1 })
        {
            var slot = slots.GetValue(index)!;
            slot.GetType().GetProperty("GroupIndex")!.SetValue(slot, -1);
        }
    }

    [Fact]
    public void FlipMaster_LinksValidatesAndClears()
    {
        var master = OpenPort();
        var third = OpenPort();
        try
        {
            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutSetFlipMaster, NeverOpenedHandle, (ulong)master));
            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)_handle, NeverOpenedHandle));
            Assert.Equal(UnsupportedOperation, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)_handle, (ulong)_handle));

            // A pending flip on either port keeps them from linking.
            Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(master, -1, 1, 1, false, out var pending));
            Assert.Equal(ResourceBusy, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)_handle, (ulong)master));
            VideoOutExports.CompleteFlip(pending);
            VideoOutExports.MarkFlipPresented(pending);

            Assert.Equal(0, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)_handle, (ulong)master));
            Assert.Equal(master, VideoOutExports.GetFlipMasterForTests(_handle));

            // A slave cannot be relinked, have slaves, and a slave cannot be a master.
            Assert.Equal(UnsupportedOperation, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)_handle, (ulong)third));
            Assert.Equal(UnsupportedOperation, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)third, (ulong)_handle));
            Assert.Equal(UnsupportedOperation, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)master, (ulong)third));
            // A master can lead several slaves.
            Assert.Equal(0, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)third, (ulong)master));

            Assert.Equal(0, Call(VideoOutExports.VideoOutClearFlipMaster, (ulong)_handle));
            Assert.Equal(0, VideoOutExports.GetFlipMasterForTests(_handle));
            Assert.Equal(0, Call(VideoOutExports.VideoOutClearFlipMaster, (ulong)_handle));
            Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutClearFlipMaster, NeverOpenedHandle));

            // Closing the master dissolves the remaining link.
            ClosePort(master);
            Assert.Equal(0, VideoOutExports.GetFlipMasterForTests(third));
            Assert.Equal(0, Call(VideoOutExports.VideoOutSetFlipMaster, (ulong)third, (ulong)_handle));
        }
        finally
        {
            ClosePort(third);
            ClosePort(master);
        }
    }

    [Fact]
    public void AllowOutputResolutionWqhdDetection_ValidatesTheHandle()
    {
        Assert.Equal(0, Call(VideoOutExports.VideoOutAllowOutputResolutionWqhdDetection, (ulong)_handle));
        Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutAllowOutputResolutionWqhdDetection, NeverOpenedHandle));
    }

    [Fact]
    public void ColorSettingsSetGamma_WritesGammaAndRejectsOutOfRangeValues()
    {
        Fill(StructAddress, 0x10);
        _context[CpuRegister.Rdi] = StructAddress;
        _context.SetXmmRegister(0, (ulong)(uint)BitConverter.SingleToInt32Bits(1.5f), 0);
        Assert.Equal(0, VideoOutExports.VideoOutColorSettingsSetGammaUnsized(_context));
        var settings = Read(StructAddress, 0x10);
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(settings));
        Assert.All(settings[4..], value => Assert.Equal(0xCC, value));

        _context.SetXmmRegister(0, (ulong)(uint)BitConverter.SingleToInt32Bits(2.5f), 0);
        Assert.Equal(InvalidValue, VideoOutExports.VideoOutColorSettingsSetGammaUnsized(_context));
        _context.SetXmmRegister(0, (ulong)(uint)BitConverter.SingleToInt32Bits(float.NaN), 0);
        Assert.Equal(InvalidValue, VideoOutExports.VideoOutColorSettingsSetGammaUnsized(_context));

        _context[CpuRegister.Rdi] = 0;
        _context.SetXmmRegister(0, (ulong)(uint)BitConverter.SingleToInt32Bits(1.0f), 0);
        Assert.Equal(InvalidAddress, VideoOutExports.VideoOutColorSettingsSetGammaUnsized(_context));
    }

    [Fact]
    public void AdjustColor_AppliesTheSettingsGammaToThePort()
    {
        var settings = new byte[0x10];
        BinaryPrimitives.WriteSingleLittleEndian(settings, 0.75f);
        Assert.True(_memory.TryWrite(StructAddress, settings));

        Assert.Equal(0, Call(VideoOutExports.VideoOutAdjustColorUnsized, (ulong)_handle, StructAddress));
        var port = GetPort(_handle);
        Assert.Equal(0.75f, (float)port.GetType().GetProperty("Gamma")!.GetValue(port)!);

        Assert.Equal(InvalidAddress, Call(VideoOutExports.VideoOutAdjustColorUnsized, (ulong)_handle, 0));
        Assert.Equal(InvalidHandle, Call(VideoOutExports.VideoOutAdjustColorUnsized, NeverOpenedHandle, StructAddress));
    }

    [Fact]
    public void AddBufferAttributeOption_OrsIntoTheOptionWordOnly()
    {
        Fill(StructAddress, 0x50, 0);
        Assert.True(_context.TryWriteUInt64(StructAddress + 0x18, 0x8));
        Assert.True(_context.TryWriteUInt64(StructAddress + 0x20, 0x8000_0000_0000_0000UL));

        Assert.Equal(0, Call(VideoOutExports.VideoOutAddBufferAttributeOption, StructAddress, 0x20));
        Assert.True(_context.TryReadUInt64(StructAddress + 0x18, out var option));
        Assert.Equal(0x28UL, option);
        Assert.True(_context.TryReadUInt64(StructAddress + 0x20, out var pixelFormat));
        Assert.Equal(0x8000_0000_0000_0000UL, pixelFormat);
        var attribute = Read(StructAddress, 0x50);
        Assert.All(attribute[..0x18], value => Assert.Equal(0, value));
        Assert.All(attribute[0x28..], value => Assert.Equal(0, value));

        Assert.Equal(InvalidAddress, Call(VideoOutExports.VideoOutAddBufferAttributeOption, 0, 0x20));
    }

    [Fact]
    public void NewExportsAreRegisteredUnderTheirNids()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        foreach (var (nid, name) in new[]
                 {
                     ("-Ozn0F1AFRg", "sceVideoOutDeleteFlipEvent"),
                     ("vwtVpHQKelI", "sceVideoOutDeleteOutputModeEvent"),
                     ("LibwuIonIBw", "sceVideoOutAddVrrActiveStatusEvent"),
                     ("-q-vbO859Tw", "sceVideoOutDeleteVrrActiveStatusEvent"),
                     ("gWT7X8H0bYs", "sceVideoOutGetOutputStatusWithVrr"),
                     ("Mt4QHHkxkOc", "sceVideoOutGetEventCount"),
                     ("6kPnj51T62Y", "sceVideoOutGetResolutionStatus"),
                     ("OcQybQejHEY", "sceVideoOutGetBufferLabelAddress"),
                     ("WkYtyOg30do", "sceVideoOutSetFlipMaster"),
                     ("T0ynQY3mH-0", "sceVideoOutClearFlipMaster"),
                     ("w7Ipp9Xl7hg", "sceVideoOutAllowOutputResolutionWqhdDetection"),
                     ("7wEmHVceFNg", "sceVideoOutColorSettingsSetGamma"),
                     ("ipf9hrW6kBY", "sceVideoOutAdjustColor"),
                     ("MBlv8YWoyOk", "sceVideoOutAddBufferAttributeOption"),
                 })
        {
            Assert.True(manager.TryGetExport(nid, out var export), name);
            Assert.Equal(name, export.Name);
        }
    }
}
