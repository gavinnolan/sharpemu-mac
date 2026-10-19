// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Memory the loader and the runtime map for the guest answers VirtualQuery,
// QueryMemoryProtection and IsStack like the kernel's own mappings.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelHostMappingTests
{
    private const int Eacces = unchecked((int)0x8002000D);
    private const ulong TextAddress = 0x0;
    private const ulong DataAddress = 0x4000;
    private const ulong EntryOffset = 0x100;

    private static (ulong Start, ulong End, int Protection, byte State, string Name) Query(BackedKernelMemory test, ulong address)
    {
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = 0;
        test.Context[CpuRegister.Rdx] = test.Output + 0x100;
        test.Context[CpuRegister.Rcx] = 0x48;
        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(test.Context));
        Span<byte> info = stackalloc byte[0x48];
        Assert.True(test.Memory.TryRead(test.Output + 0x100, info));
        var name = Encoding.UTF8.GetString(info.Slice(33, 32)).TrimEnd('\0');
        return (BinaryPrimitives.ReadUInt64LittleEndian(info), BinaryPrimitives.ReadUInt64LittleEndian(info[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(info[24..]), info[32], name);
    }

    private static int IsStack(BackedKernelMemory test, ulong address, out ulong start, out ulong end)
    {
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = test.Output + 0x200;
        test.Context[CpuRegister.Rdx] = test.Output + 0x208;
        var result = KernelMemoryCompatExports.KernelIsStack(test.Context);
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x200, out start));
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x208, out end));
        return result;
    }

    // A two-segment module: text (R+X) at 0 and data (R+W, no file bytes) at 0x4000.
    private static byte[] BuildModule()
    {
        var image = new byte[0x200];
        var elf = image.AsSpan();
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        elf[7] = 9;
        elf[8] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x10..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x12..], 62);
        BinaryPrimitives.WriteUInt32LittleEndian(elf[0x14..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elf[0x18..], EntryOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(elf[0x20..], 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x34..], 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x36..], 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x38..], 2);

        void Segment(int index, uint flags, ulong address, ulong fileSize, ulong memorySize)
        {
            var header = image.AsSpan(0x40 + index * 0x38, 0x38);
            BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], flags);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], address);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], address);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], fileSize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x28..], memorySize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x30..], 0x4000);
        }

        Segment(0, 5, TextAddress, (ulong)image.Length, 0x4000);
        Segment(1, 6, DataAddress, 0, 0x4000);
        image[EntryOffset] = 0xC3;
        return image;
    }

    [Fact]
    public void ModuleSegmentsAnswerVirtualQueryWithTheirProtectionAndName()
    {
        using var test = new BackedKernelMemory();
        // The image goes into a kernel-tracked space without host execute permission, which
        // this test host may not grant.
        var moduleMemory = new TrackedModuleMemory();
        var image = new SelfLoader().LoadAdditional(BuildModule(), moduleMemory, new ModuleManager(), null, null);
        var imageBase = image.EntryPoint - image.ElfHeader.EntryPoint;

        // The loader records the segments as soon as it maps them; the runtime then names them.
        var text = Query(test, imageBase + TextAddress + 0x100);
        Assert.Equal((imageBase + TextAddress, imageBase + TextAddress + 0x4000, 0x5), (text.Start, text.End, text.Protection));
        GuestHostMappings.RegisterModuleSegments(moduleMemory, image.ProgramHeaders, imageBase, "libSceTest.prx");

        text = Query(test, imageBase + TextAddress);
        Assert.Equal(0x5, text.Protection);
        Assert.Equal("libSceTest.prx", text.Name);
        // Neither flexible, direct nor a stack.
        Assert.Equal(0, text.State & 0x07);

        var data = Query(test, imageBase + DataAddress + 0x3FF8);
        Assert.Equal((imageBase + DataAddress, imageBase + DataAddress + 0x4000, 0x3, "libSceTest.prx"),
            (data.Start, data.End, data.Protection, data.Name));

        test.Context[CpuRegister.Rdi] = imageBase + DataAddress;
        test.Context[CpuRegister.Rsi] = test.Output + 0x200;
        test.Context[CpuRegister.Rdx] = test.Output + 0x208;
        test.Context[CpuRegister.Rcx] = test.Output + 0x210;
        Assert.Equal(0, KernelMemoryCompatExports.KernelQueryMemoryProtection(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x208, out var end));
        Assert.True(test.Context.TryReadUInt32(test.Output + 0x210, out var protection));
        Assert.Equal((imageBase + DataAddress + 0x4000, 3u), (end, protection));

        Assert.Equal(0, IsStack(test, imageBase + TextAddress, out var start, out end));
        Assert.Equal((0UL, 0UL), (start, end));
        KernelMemoryCompatExports.UnregisterHostMapping(imageBase, 0x8000);
    }

    // Plain guest memory standing in for the kernel's address space.
    private sealed class TrackedModuleMemory : IVirtualMemory, IGuestBackedSpace
    {
        private readonly VirtualMemory _inner = new();

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => _inner.TryRead(virtualAddress, destination);
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => _inner.TryWrite(virtualAddress, source);
        public void Clear() => _inner.Clear();
        public bool IsBackedView(ulong address) => false;
        public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData,
            ProgramHeaderFlags protection) => _inner.Map(virtualAddress, memorySize, fileOffset, fileData, protection);
        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions() => _inner.SnapshotRegions();
        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection,
            out SharpEmu.HLE.Host.HostViewFailure failure)
        {
            failure = default;
            return false;
        }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedRange(ulong address, ulong size) => false;
        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => false;
        public bool TryReadBacking(ulong address, Span<byte> data) => false;
    }

    [Fact]
    public void RegisteredStackAnswersAsAStackUntilItsThreadExits()
    {
        using var test = new BackedKernelMemory();
        var stack = SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport.ProbeGuestAddress(test.Host, 0x40000);
        KernelMemoryCompatExports.RegisterHostMapping(stack, 0x40000, 0x3, KernelMemoryCompatExports.HostMappingKind.Stack);

        var query = Query(test, stack + 0x3FFF8);
        Assert.Equal((stack, stack + 0x40000, 0x3, "stack"), (query.Start, query.End, query.Protection, query.Name));
        Assert.Equal(0x04, query.State & 0x07);
        Assert.Equal(0, IsStack(test, stack + 0x100, out var start, out var end));
        Assert.Equal((stack, stack + 0x40000), (start, end));

        KernelMemoryCompatExports.UnregisterHostMapping(stack, 0x40000);
        test.Context[CpuRegister.Rdi] = stack;
        test.Context[CpuRegister.Rsi] = 0;
        test.Context[CpuRegister.Rdx] = test.Output + 0x100;
        test.Context[CpuRegister.Rcx] = 0x48;
        Assert.Equal(Eacces, KernelMemoryCompatExports.KernelVirtualQuery(test.Context));
    }

    [NativeX64Fact]
    public void GuestThreadStackAndTlsAreRegisteredWhenTheThreadIsCreated()
    {
        using var test = new BackedKernelMemory();
        var moduleManager = new ModuleManager();
        moduleManager.Freeze();
        using var backend = new DirectExecutionBackend(moduleManager);
        var create = typeof(DirectExecutionBackend).GetMethod("TryCreateGuestThreadState",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = new GuestThreadStartRequest(0x4242, 0x1000, 0, 0, "stack-test", 700, ulong.MaxValue);
        object?[] arguments = [test.Context, request, null, null];
        Assert.True((bool)create.Invoke(backend, arguments)!, arguments[3] as string);
        var thread = arguments[2]!;
        var stackBase = (ulong)thread.GetType().GetProperty("StackBase")!.GetValue(thread)!;
        var stackSize = (ulong)thread.GetType().GetProperty("StackSize")!.GetValue(thread)!;
        var context = (CpuContext)thread.GetType().GetProperty("Context")!.GetValue(thread)!;

        var stack = Query(test, stackBase + stackSize - 8);
        Assert.Equal(0x04, stack.State & 0x07);
        Assert.Equal(0, IsStack(test, stackBase, out var start, out var end));
        Assert.Equal((stackBase, stackBase + stackSize), (start, end));
        var tls = Query(test, context.FsBase);
        Assert.Equal((0x3, 0), (tls.Protection, tls.State & 0x07));
    }
}
