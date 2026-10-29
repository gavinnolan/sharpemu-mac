// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterBenchTests
{
    private const ulong Base = RecordingCommandStreamHost.MemoryBase;
    private const ulong CommandAddress = Base + 0x1000;
    private const ulong ContextTable = Base + 0x4_0000;
    private const ulong ShaderTable = Base + 0x4_1000;
    private const ulong LabelBase = Base + 0x5_0000;
    private const ulong DataBase = Base + 0x6_0000;
    private const ulong QueryBase = Base + 0x7_0000;
    private const ulong IndexBase = Base + 0x8_0000;

    // A host with no GPU: every host call is counted and does no work, so the time is the
    // interpreter's own. Dumps take the native path, as the Metal host's do.
    private sealed class NullHost(FakeCpuMemory memory, CountingSink sink) :
        ManagedCommandStreamHost(memory, sink, sink), ICommandStreamHost
    {
        public int Draws;
        public int Dumps;

        // The Metal host hands over command blocks and says whether a block is still current at every packet.
        public long BlockVersion;

        public bool SupportsCommandBlocks => true;

        public bool TryReadCommandBlock(ulong address, Span<byte> block, out CommandReadToken token)
        {
            token = new CommandReadToken(BlockVersion, 0, 0, 0);
            return Memory.TryRead(address, block);
        }

        public bool IsCommandBlockCurrent(in CommandReadToken token) => token.First == BlockVersion;

        public override void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments) => Draws++;

        public bool DumpOcclusionCounter(int queueId, ulong address, uint instanceMask, uint strideBytes)
        {
            Dumps++;
            return true;
        }
    }

    private sealed class CountingSink : IEndOfPipeSink, ICommandStreamFlipTarget
    {
        public int Interrupts;

        public void TriggerInterrupt(int eventId, uint contextId) => Interrupts++;

        public ulong Prepare(int handle, int index, int flipMode, long flipArgument) => 1;

        public void Complete(ulong requestId) { }

        public void Presented(ulong requestId) { }

        public bool IsDone(int handle, int index) => true;
    }

    private static uint[] Packet(uint opcode, params uint[] payload) => StreamRunner.Packet(opcode, payload);

    private static uint[] Custom(uint code, params uint[] payload) =>
        StreamRunner.CustomPacket(PacketOpcode.Nop, code, payload);

    private static uint[] ReleaseMemory(ulong label, uint value) => Packet(PacketOpcode.ReleaseMemory,
        0x28u | (5u << 8), (2u << 24) | (1u << 29), StreamRunner.Low(label), StreamRunner.High(label), value, 0, 0);

    private static uint[] Dump(ulong address) => Packet(PacketOpcode.EventWrite,
        0x139, StreamRunner.Low(address), StreamRunner.High(address));

    private static uint[] SelectQueries() => Packet(PacketOpcode.EventWrite,
        0x138, (2u << 9) | (0xFFFFu << 11), 0);

    // Context registers a table or packet can write with a zero value, found by writing each.
    private static uint[] WritableContextRegisters()
    {
        var found = new List<uint>();
        for (var offset = 0u; offset < RegisterBankLayout.ContextRegisterCount && found.Count < 32; offset++)
        {
            if (RegisterWriteTable.ContextIndirect[offset] is null || offset == RegisterBankLayout.ContextNop) continue;
            var scratch = new RegisterBanks(message => new InvalidOperationException(message));
            try
            {
                RegisterWriteTable.WriteContextEntry(scratch, offset, 0, 0);
                found.Add(offset);
            }
            catch (Exception)
            {
            }
        }

        return found.ToArray();
    }

    private static void Table(FakeCpuMemory memory, ulong address, uint[] registers, int count)
    {
        var words = new uint[count * 2];
        for (var index = 0; index < count; index++)
        {
            words[index * 2] = registers[index % registers.Length];
            words[index * 2 + 1] = 0;
        }

        Assert.True(memory.TryWrite(address, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan())));
    }

    private static uint[] ShaderRegisters(uint first, int count)
    {
        var registers = new uint[count];
        for (var index = 0; index < count; index++) registers[index] = first + (uint)index;
        return registers;
    }

    private sealed record Workload(string Name, int PacketCount, uint[] Words);

    private static Workload Repeat(string name, int times, Func<int, uint[][]> body)
    {
        var all = new List<uint[]>();
        var packets = 0;
        for (var index = 0; index < times; index++)
        {
            foreach (var packet in body(index))
            {
                all.Add(packet);
                packets++;
            }
        }

        return new Workload(name, packets, StreamRunner.Concat(all.ToArray()));
    }

    // SHARPEMU_INTERPRETER_BENCH_OUTPUT=<path>: ns per packet for each packet type and for a stream
    // shaped like a frame, and the bytes the worker thread allocates per frame.
    [Fact]
    public void MeasureInterpreterWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_INTERPRETER_BENCH_OUTPUT");
        if (path is null) return;

        var memory = new FakeCpuMemory(Base, RecordingCommandStreamHost.MemorySize);
        var sink = new CountingSink();
        var host = new NullHost(memory, sink);
        var interpreter = new GpuCommandInterpreter(host, 0, 0);
        var contextRegisters = WritableContextRegisters();
        Assert.NotEmpty(contextRegisters);
        Table(memory, ContextTable, contextRegisters, 24);
        Table(memory, ShaderTable, ShaderRegisters(ShaderRegisterOffset.SpiShaderUserDataPs0, 16), 16);

        const int Reps = 400;
        var workloads = new List<Workload>
        {
            Repeat("set_context_reg_1", Reps, i => [Packet(PacketOpcode.SetContextRegister, contextRegisters[i % contextRegisters.Length], 0)]),
            Repeat("set_context_reg_run4", Reps, i => [Packet(PacketOpcode.SetContextRegister, ContextRegisterOffset.PaScViewportScissor0Tl, 0, 0, 0, 0)]),
            Repeat("set_sh_reg_run6", Reps, i => [Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.SpiShaderUserDataPs0, 1, 2, 3, 4, 5, 6)]),
            Repeat("set_sh_reg_run16", Reps, i => [Packet(PacketOpcode.SetShaderRegister, [ShaderRegisterOffset.SpiShaderUserDataVs0, .. Enumerable.Range(0, 16).Select(v => (uint)v)])]),
            Repeat("context_table_24", Reps, i => [Custom(PacketCustomCode.ContextRegisterTable, 24, StreamRunner.Low(ContextTable), StreamRunner.High(ContextTable))]),
            Repeat("shader_table_16", Reps, i => [Custom(PacketCustomCode.ShaderRegisterTable, 16, StreamRunner.Low(ShaderTable), StreamRunner.High(ShaderTable))]),
            Repeat("draw_index_2", Reps, i => [Packet(PacketOpcode.DrawIndex2, 100, StreamRunner.Low(IndexBase), StreamRunner.High(IndexBase), 60, 0x20)]),
            Repeat("write_data_2", Reps, i => [Packet(PacketOpcode.WriteData, 0x100, StreamRunner.Low(DataBase), StreamRunner.High(DataBase), 1, 2)]),
            Repeat("release_mem_write32_interrupt", Reps, i => [ReleaseMemory(LabelBase + (ulong)(i % 64) * 16, (uint)i)]),
            Repeat("zpass_dump_16_blocks", Reps, i => [i == 0 ? SelectQueries() : [], Dump(QueryBase)]),
            Repeat("marker_push_pop", Reps, i => [Custom(PacketCustomCode.PushMarker, 0, 0), Custom(PacketCustomCode.PopMarker, 0, 0)]),
            Repeat("nop_filler", Reps, i => [Packet(PacketOpcode.Nop, 0, 0)]),
        };

        // A frame: per draw, a few context packets, shader packets and tables, then the draw;
        // a release or write every few draws, and a pair of occlusion dumps around some.
        workloads.Add(Repeat("frame_1000_draws", 1000, i =>
        {
            var packets = new List<uint[]>
            {
                Packet(PacketOpcode.SetContextRegister, contextRegisters[i % contextRegisters.Length], 0),
                Packet(PacketOpcode.SetContextRegister, ContextRegisterOffset.PaScViewportScissor0Tl, 0, 0),
                Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.SpiShaderUserDataPs0, 1, 2, 3, 4, 5, 6),
                Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.SpiShaderUserDataVs0, 1, 2, 3, 4),
                Custom(PacketCustomCode.ShaderRegisterTable, 16, StreamRunner.Low(ShaderTable), StreamRunner.High(ShaderTable)),
                Packet(PacketOpcode.DrawIndex2, 100, StreamRunner.Low(IndexBase), StreamRunner.High(IndexBase), 60, 0x20),
            };
            if (i % 4 == 0)
                packets.Insert(0, Custom(PacketCustomCode.ContextRegisterTable, 24, StreamRunner.Low(ContextTable), StreamRunner.High(ContextTable)));
            if (i % 10 == 0)
            {
                packets.Add(Packet(PacketOpcode.WriteData, 0x100, StreamRunner.Low(DataBase), StreamRunner.High(DataBase), 1, 2));
                packets.Add(ReleaseMemory(LabelBase + (ulong)(i % 64) * 16, (uint)i));
                packets.Add(Custom(PacketCustomCode.PushMarker, 0, 0));
                packets.Add(Custom(PacketCustomCode.PopMarker, 0, 0));
            }

            if (i % 25 == 0)
            {
                packets.Add(SelectQueries());
                packets.Add(Dump(QueryBase));
                packets.Add(Dump(QueryBase + 8));
            }

            return packets.ToArray();
        }));

        var results = new List<object>();
        foreach (var workload in workloads)
        {
            Assert.True(memory.TryWrite(CommandAddress, System.Runtime.InteropServices.MemoryMarshal.AsBytes(workload.Words.AsSpan())));
            var dwords = (uint)workload.Words.Length;
            Assert.True(CommandAddress + dwords * 4UL < ContextTable);
            double Pass()
            {
                var commands = new PacketCursorStack();
                var started = Stopwatch.GetTimestamp();
                interpreter.Process(commands, CommandAddress, dwords);
                return (Stopwatch.GetTimestamp() - started) * 1e9 / Stopwatch.Frequency;
            }

            // Long enough for the tiered JIT to promote what the stream runs through.
            var warmUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * (workload.PacketCount > 3000 ? 3 : 1) / 2;
            while (Stopwatch.GetTimestamp() < warmUntil) Pass();
            var samples = new double[21];
            long allocated = 0;
            for (var pass = 0; pass < samples.Length; pass++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                samples[pass] = Pass();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Array.Sort(samples);
            results.Add(new
            {
                name = workload.Name,
                packets = workload.PacketCount,
                dwords,
                nsPerPacketMedian = samples[samples.Length / 2] / workload.PacketCount,
                nsPerPacketBest = samples[0] / workload.PacketCount,
                allocatedBytesPerPass = allocated,
                allocatedBytesPerPacket = (double)allocated / workload.PacketCount,
            });
        }

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            draws = host.Draws,
            dumps = host.Dumps,
            workloads = results,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
