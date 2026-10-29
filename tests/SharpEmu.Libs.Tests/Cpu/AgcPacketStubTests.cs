// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Cpu.Native.Platform;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Tests.Agc;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// Runs the emitted AGC packet stubs as machine code against the exports they stand in for: the same
/// arguments, the same command buffer bytes in, and the same bytes, cursor and return value out - or
/// a decline (the fallback answers a sentinel) exactly where the export would have to run the buffer's
/// callback or refuse.
/// </summary>
[Collection(AgcCommandBufferChainCollection.Name)]
public sealed unsafe class AgcPacketStubTests
{
    private const ulong Sentinel = 0x5EEDFACE_0BADF00DUL;
    private const int BlockBytes = 0x1000;
    private const int PacketOffset = 0x100;

    public static IEnumerable<object[]> Nids() =>
        new[]
        {
            "pFLArOT53+w", "LHFXRrlTPD8", "w4-d0n60hdo", "-HOOCn0JY48", "ZvwO9euwYzc", "hvUfkUIQcOE", "tSBxhAPyytQ",
            "8N2tmT3jmC8", "GIIW2J37e70", "l4fM9K-Lyks", "Yw0jKSqop+E", "B+aG9DUnTKA", "q88lQ+GP5Yk", "k3GhuSNmBLU",
        }.Select(nid => new object[] { nid });

    [Theory]
    [MemberData(nameof(Nids))]
    public void TheStubWritesWhatTheExportWritesAndDeclinesWhereTheExportWouldCallBack(string nid)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        Assert.True(AgcExports.TryGetPacketStubSpec(nid, out var spec));
        using var harness = new StubHarness(spec);
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var random = new System.Random(unchecked(nid.GetHashCode() ^ 0x5A17));
        var interesting = new ulong[]
        {
            0, 1, 2, 3, 7, 0x28, 0x40, 0xA038, 0xFFFF, 0x1_0000, 0xFFFF_FFFC, 0xFFFF_FFFF, 0x1_0000_0000, 0x1_0000_0001, 0x2_6000_0A08,
            0x7FFF_FFFF_FFFF_FFFF, 0xFFFF_FFFF_FFFF_FFFF, 0x8000_0000_0000_0000, 0xDEAD_BEEF_CAFE_F00D,
        };
        ulong Pick() => random.Next(3) == 0 ? (ulong)random.NextInt64() | ((ulong)random.Next(2) << 63) : interesting[random.Next(interesting.Length)];

        var packetBytes = (int)spec.SizeDwords * 4;
        var stubRan = 0;
        for (var iteration = 0; iteration < 600; iteration++)
        {
            var reserved = (uint)random.Next(3) * 3;
            // Room: exactly the packet and the reserve, one dword short, one dword over, plenty, none, or an inverted buffer.
            var room = random.Next(6) switch
            {
                0 => packetBytes + (int)reserved * 4,
                1 => packetBytes + (int)reserved * 4 - 4,
                2 => packetBytes + (int)reserved * 4 + 4,
                3 => 0x700,
                4 => 0,
                _ => -64,
            };
            var arguments = new[] { Pick(), Pick(), Pick(), Pick() };
            var nullBuffer = random.Next(25) == 0;

            using var managedBlock = new Block();
            using var stubBlock = new Block();
            foreach (var block in new[] { managedBlock, stubBlock })
            {
                block.Reset(reserved, room, random.Next(1 << 30));
            }

            // Both blocks start byte for byte alike (Reset is seeded the same way).
            stubBlock.CopyFrom(managedBlock);

            var buffer = nullBuffer ? 0UL : (ulong)managedBlock.Pointer;
            var context = new CpuContext(new RawMemory(managedBlock), Generation.Gen5);
            context[CpuRegister.Rdi] = buffer;
            context[CpuRegister.Rsi] = arguments[0];
            context[CpuRegister.Rdx] = arguments[1];
            context[CpuRegister.Rcx] = arguments[2];
            context[CpuRegister.R8] = arguments[3];
            // The block's cursor words are absolute addresses: rebase them for each block.
            managedBlock.Rebase();
            var previousScheduler = GuestThreadExecution.Scheduler;
            GuestThreadExecution.Scheduler = null;
            try
            {
                context.DeclineGuestCallbacks = true;
                manager.Dispatch(nid, context);
                context.DeclineGuestCallbacks = false;
            }
            finally
            {
                GuestThreadExecution.Scheduler = previousScheduler;
            }

            stubBlock.Rebase();
            var stubRax = harness.Call(nullBuffer ? 0UL : (ulong)stubBlock.Pointer, arguments[0], arguments[1], arguments[2], arguments[3]);
            if (stubRax == Sentinel)
            {
                Assert.True(
                    context.GuestCallbackDeclined || nullBuffer,
                    $"{nid}: the stub declined with room (room={room} reserved={reserved}) where the export built the packet");
                Assert.Equal(managedBlock.Normalized(), stubBlock.Normalized());
                continue;
            }

            stubRan++;
            Assert.False(context.GuestCallbackDeclined, $"{nid}: the stub built a packet where the export had to call back (room={room} reserved={reserved})");
            Assert.False(nullBuffer, nid + ": the stub accepted a null buffer");
            Assert.Equal(context[CpuRegister.Rax] - (ulong)managedBlock.Pointer, stubRax - (ulong)stubBlock.Pointer);
            Assert.Equal(managedBlock.Normalized(), stubBlock.Normalized());
        }

        Assert.True(stubRan > 100, $"{nid}: the stub took the fast path only {stubRan} times");
    }

    private const int ValuesOffset = 0xC00;

    public static IEnumerable<object[]> VariableNids() =>
        new[] { "n2fD4A+pb+g", "UZbQjYAwwXM" }.Select(nid => new object[] { nid });

    [Theory]
    [MemberData(nameof(VariableNids))]
    public void TheVariableLengthStubWritesWhatTheExportWritesAndLeavesTheBufferAloneWhenItDeclines(string nid)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        Assert.True(AgcExports.TryGetVariablePacketStub(nid, out var kind));
        var isRange = kind == AgcVariablePacketStub.ShRegisterRange;
        using var harness = new StubHarness(kind);
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var random = new System.Random(unchecked(nid.GetHashCode() ^ 0x77A1));
        var stubRan = 0;
        var declinedWithRoom = 0;
        for (var iteration = 0; iteration < 1500; iteration++)
        {
            var reserved = (uint)random.Next(3) * 3;
            var count = random.Next(12) switch
            {
                0 => 0,
                1 => 1,
                2 => 65,
                3 => 64,
                4 => 33,
                5 => 32,
                _ => random.Next(1, 20),
            };
            var packetDwords = (isRange ? 4 : 2) + count;
            var room = random.Next(6) switch
            {
                0 => packetDwords * 4 + (int)reserved * 4,
                1 => packetDwords * 4 + (int)reserved * 4 - 4,
                2 => packetDwords * 4 + (int)reserved * 4 + 4,
                3 => 0x700,
                4 => 0,
                _ => -64,
            };
            var nullBuffer = random.Next(30) == 0;

            using var managedBlock = new Block();
            using var stubBlock = new Block();
            managedBlock.Reset(reserved, room, random.Next(1 << 30));

            // The source: the block's tail (clear of the packet area) or, now and then, inside the packet area.
            var valuesInside = random.Next(8) == 0;
            var valuesNull = random.Next(40) == 0;
            var sourceOffset = valuesInside ? PacketOffset + 4 * random.Next(0, 24) : ValuesOffset;
            var firstOffset = random.Next(6) switch
            {
                0 => 0u,
                1 => 0x3FFu,
                2 => 0x400u,
                3 => 0xFFFF_FFF0u,
                _ => (uint)random.Next(1, 0x3F0),
            };
            var consecutive = random.Next(4) != 0;
            unsafe
            {
                var source = (uint*)(managedBlock.Pointer + ValuesOffset);
                if (!isRange)
                {
                    for (var index = 0; index < 64; index++)
                    {
                        source[index * 2] = consecutive ? firstOffset + (uint)index : (uint)random.Next(0, 0x400);
                        source[index * 2 + 1] = (uint)random.NextInt64();
                    }

                    if (consecutive && random.Next(5) == 0 && count > 1)
                    {
                        source[2 * random.Next(1, count)] += 1;
                        consecutive = false;
                    }

                    // The stub leaves a run that would wrap past 32 bits to the export.
                    consecutive &= (ulong)firstOffset + (ulong)count < 0x1_0000_0000UL;
                }
            }

            stubBlock.CopyFrom(managedBlock);
            managedBlock.Rebase();
            stubBlock.Rebase();
            var pristine = stubBlock.Normalized();

            ulong Source(Block block) => valuesNull ? 0UL : (ulong)(block.Pointer + sourceOffset);
            var offsetArgument = isRange ? (ulong)(random.Next(8) == 0 ? random.Next(0, 0x500) : random.Next(1, 0x3FF)) : 0UL;
            var buffer = nullBuffer ? 0UL : (ulong)managedBlock.Pointer;
            var context = new CpuContext(new RawMemory(managedBlock), Generation.Gen5);
            context[CpuRegister.Rdi] = buffer;
            if (isRange)
            {
                context[CpuRegister.Rsi] = offsetArgument;
                context[CpuRegister.Rdx] = Source(managedBlock);
                context[CpuRegister.Rcx] = (ulong)count;
            }
            else
            {
                context[CpuRegister.Rsi] = Source(managedBlock);
                context[CpuRegister.Rdx] = (ulong)count;
            }

            var previousScheduler = GuestThreadExecution.Scheduler;
            GuestThreadExecution.Scheduler = null;
            try
            {
                context.DeclineGuestCallbacks = true;
                manager.Dispatch(nid, context);
                context.DeclineGuestCallbacks = false;
            }
            finally
            {
                GuestThreadExecution.Scheduler = previousScheduler;
            }

            var stubSource = valuesNull ? 0UL : (ulong)(stubBlock.Pointer + sourceOffset);
            var stubRax = isRange
                ? harness.Call(nullBuffer ? 0UL : (ulong)stubBlock.Pointer, offsetArgument, stubSource, (ulong)count, 0)
                : harness.Call(nullBuffer ? 0UL : (ulong)stubBlock.Pointer, stubSource, (ulong)count, 0, 0);
            if (stubRax == Sentinel)
            {
                // A decline never touches the buffer; the export then runs.
                Assert.Equal(pristine, stubBlock.Normalized());
                if (!nullBuffer && room >= packetDwords * 4 + (int)reserved * 4 && count is >= 1 and <= 32 && !valuesNull && !valuesInside &&
                    (isRange ? offsetArgument is >= 1 and <= 0x3FF : consecutive))
                {
                    declinedWithRoom++;
                }

                continue;
            }

            stubRan++;
            Assert.False(context.GuestCallbackDeclined, $"{nid}: the stub built a packet where the export had to call back");
            Assert.False(nullBuffer, nid + ": the stub accepted a null buffer");
            Assert.Equal(context[CpuRegister.Rax] - (ulong)managedBlock.Pointer, stubRax - (ulong)stubBlock.Pointer);
            Assert.Equal(managedBlock.Normalized(), stubBlock.Normalized());
        }

        Assert.True(stubRan > 150, $"{nid}: the stub took the fast path only {stubRan} times");
        Assert.True(declinedWithRoom < 40, $"{nid}: the stub declined {declinedWithRoom} calls the export builds as one in-place packet");
    }

    [Fact]
    public void EveryStubbedBuilderIsAlsoALeafBuilder()
    {
        foreach (var nid in Nids().Select(row => (string)row[0]))
        {
            Assert.True(DirectExecutionBackend.IsAgcBuilderLeafImport(nid), nid);
            Assert.Equal(DirectExecutionBackend.GuestFastPathStub.AgcPacket, DirectExecutionBackend.ClassifyGuestFastPathStub(nid));
        }

        Assert.True(DirectExecutionBackend.IsAgcBuilderLeafImport("n2fD4A+pb+g"));
        foreach (var nid in VariableNids().Select(row => (string)row[0]))
        {
            Assert.Equal(DirectExecutionBackend.GuestFastPathStub.AgcPacket, DirectExecutionBackend.ClassifyGuestFastPathStub(nid));
        }
    }

    /// <summary>A guest "command buffer" in native memory: the buffer structure at +0, the packet area at +0x100.</summary>
    private sealed class Block : IDisposable
    {
        private readonly byte* _memory = (byte*)NativeMemory.AlignedAlloc(BlockBytes, 16);
        private uint _reserved;
        private int _room;

        public Block() => NativeMemory.Clear(_memory, BlockBytes);

        public byte* Pointer => _memory;

        public void Reset(uint reserved, int room, int seed)
        {
            NativeMemory.Clear(_memory, BlockBytes);
            var random = new System.Random(seed);
            // Garbage in the packet area, so a stub that writes too little or too much shows.
            for (var offset = PacketOffset; offset < BlockBytes; offset++)
            {
                _memory[offset] = (byte)random.Next(256);
            }

            _reserved = reserved;
            _room = room;
        }

        public void CopyFrom(Block other)
        {
            Buffer.MemoryCopy(other._memory, _memory, BlockBytes, BlockBytes);
            _reserved = other._reserved;
            _room = other._room;
        }

        // The cursor, the end and the reserve, relative to this block.
        public void Rebase()
        {
            *(ulong*)(_memory + 0x10) = (ulong)(_memory + PacketOffset);
            *(ulong*)(_memory + 0x18) = (ulong)((long)(_memory + PacketOffset) + _room);
            *(ulong*)(_memory + 0x20) = 0x1234;
            *(ulong*)(_memory + 0x28) = 0x5678;
            *(uint*)(_memory + 0x30) = _reserved;
        }

        // The block with the absolute addresses it holds made relative, so two blocks compare.
        public byte[] Normalized()
        {
            var bytes = new byte[BlockBytes];
            Marshal.Copy((nint)_memory, bytes, 0, BlockBytes);
            foreach (var field in new[] { 0x10, 0x18 })
            {
                var value = BitConverter.ToUInt64(bytes, field) - (ulong)_memory;
                BitConverter.TryWriteBytes(bytes.AsSpan(field), value);
            }

            return bytes;
        }

        public void Dispose() => NativeMemory.AlignedFree(_memory);
    }

    private sealed class RawMemory(Block block) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            var offset = virtualAddress - (ulong)block.Pointer;
            if (offset > BlockBytes || (ulong)destination.Length > BlockBytes - offset)
            {
                return false;
            }

            new ReadOnlySpan<byte>(block.Pointer + offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            var offset = virtualAddress - (ulong)block.Pointer;
            if (offset > BlockBytes || (ulong)source.Length > BlockBytes - offset)
            {
                return false;
            }

            source.CopyTo(new Span<byte>(block.Pointer + offset, source.Length));
            return true;
        }
    }

    private sealed class StubHarness : IDisposable
    {
        private readonly byte* _page;
        private readonly delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong> _entry;

        public StubHarness(AgcPacketStubSpec spec)
            : this(spec, null)
        {
        }

        public StubHarness(AgcVariablePacketStub kind)
            : this(null, kind)
        {
        }

        private StubHarness(AgcPacketStubSpec? spec, AgcVariablePacketStub? kind)
        {
            _page = (byte*)HostMemory.Alloc(null, 4096, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_EXECUTE_READWRITE);
            Assert.True(_page != null);
            // Fallback trampoline at +0x100: mov rax, sentinel / ret.
            _page[0x100] = 0x48;
            _page[0x101] = 0xB8;
            *(ulong*)(_page + 0x102) = Sentinel;
            _page[0x10A] = 0xC3;
            var stub = spec is not null
                ? DirectExecutionBackend.EmitGuestAgcPacketStub(spec, (nint)(_page + 0x100))
                : DirectExecutionBackend.EmitGuestAgcVariablePacketStub(kind!.Value, (nint)(_page + 0x100));
            Assert.True(stub.Count < 0x400);
            for (var i = 0; i < stub.Count; i++)
            {
                _page[0x200 + i] = stub[i];
            }

            Assert.True(HostMemory.Protect(_page, 4096, HostMemory.PAGE_EXECUTE_READ, out _));
            HostMemory.FlushInstructionCache(_page, 4096);
            _entry = (delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong>)(_page + 0x200);
        }

        public ulong Call(ulong buffer, ulong rsi, ulong rdx, ulong rcx, ulong r8) => _entry(buffer, rsi, rdx, rcx, r8);

        public void Dispose() => HostMemory.Free(_page, 0, HostMemory.MEM_RELEASE);
    }
}
