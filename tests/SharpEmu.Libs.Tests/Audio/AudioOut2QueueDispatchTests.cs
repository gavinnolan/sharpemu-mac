// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed partial class AudioOut2QueueTests
{
    [NativeX64Theory]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "full")]
    [InlineData(true, "full")]
    [InlineData(false, "available-only")]
    [InlineData(true, "available-only")]
    [InlineData(false, "aliased")]
    [InlineData(true, "aliased")]
    [InlineData(false, "invalid-level")]
    [InlineData(true, "invalid-level")]
    [InlineData(false, "invalid-available")]
    [InlineData(true, "invalid-available")]
    [InlineData(false, "missing-context")]
    [InlineData(true, "missing-context")]
    public void QueueQueryDispatcherMatchesFullGateway(bool lowLatency, string scenario)
    {
        using var gateway = new AudioQueueGateway(_ctx, lowLatency);
        var context = CreateContext(4, LongGrain);
        if (scenario == "full") for (var i = 0; i < 4; i++) Assert.Equal(0, Push(context, NonBlocking));
        var level = scenario == "available-only" ? 0UL : scenario == "invalid-level" ? MemoryBase + MemorySize : LevelAddress;
        var available = scenario == "aliased" ? LevelAddress : scenario == "invalid-available" ? MemoryBase + MemorySize : AvailableAddress;
        gateway.Arguments(scenario == "missing-context" ? 0 : context, level, available);
        var expected = gateway.Dispatch(1); // Same export, forced through the full production gateway.
        var expectedLevel = ReadUInt32(LevelAddress);
        var expectedAvailable = ReadUInt32(AvailableAddress);
        _memory.TryWrite(LevelAddress, new byte[8]);
        _ctx[CpuRegister.Rax] = 0xBAD;
        Assert.Equal(expected, gateway.Dispatch(0));
        Assert.Equal(expected, _ctx[CpuRegister.Rax]);
        Assert.Equal(expectedLevel, ReadUInt32(LevelAddress));
        Assert.Equal(expectedAvailable, ReadUInt32(AvailableAddress));
        Assert.Equal(0x800010000L, Marshal.ReadInt64(gateway.Pack, 96));
    }

    [NativeX64Fact]
    public void QueueQueryDispatchMeasurement()
    {
        // Explicit opt-in: ordinary correctness runs do not collect noisy timing data.
        var outputPath = Environment.GetEnvironmentVariable("SHARPEMU_AUDIO_QUEUE_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(outputPath)) return;
        using var gateway = new AudioQueueGateway(_ctx, false);
        gateway.Arguments(CreateContext(4, LongGrain), 0, AvailableAddress);
        for (var i = 0; i < 20000; i++) { gateway.Dispatch(0); gateway.Dispatch(1); }
        const int count = 200000;
        var rows = new List<object>();
        foreach (var index in new[] { 1, 0, 0, 1, 0, 1, 1, 0 })
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            ulong result = 0;
            for (var i = 0; i < count; i++) result |= gateway.Dispatch(index);
            var elapsed = Stopwatch.GetElapsedTime(start);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(0UL, result);
            Assert.Equal(4U, ReadUInt32(AvailableAddress));
            rows.Add(new { path = index == 0 ? "classified" : "full", nsPerCall = elapsed.TotalNanoseconds / count, bytesPerCall = (double)bytes / count, count });
        }
        File.WriteAllText(outputPath, System.Text.Json.JsonSerializer.Serialize(new { enabled = Environment.GetEnvironmentVariable("SHARPEMU_AUDIO_OUT2_QUEUE_LEAF"), rows }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class AudioQueueGateway : IDisposable
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly DirectExecutionBackend _backend;
        private readonly nint _allocation = Marshal.AllocHGlobal(512);
        private readonly Func<int, nint, ulong> _dispatch;
        internal nint Pack => _allocation + 192;

        internal AudioQueueGateway(CpuContext context, bool lowLatency)
        {
            var modules = new ModuleManager(); modules.Freeze();
            _backend = new DirectExecutionBackend(modules);
            var type = typeof(DirectExecutionBackend);
            type.GetField("_cpuContext", Instance)!.SetValue(_backend, context);
            Marshal.Copy(new byte[512], 0, _allocation, 512);
            Marshal.WriteInt64(Pack, 96, 0x800010000L);
            var nid = lowLatency ? "Q8DZkKQ-SYc" : "R7d0F1g2qsU";
            var entryType = type.GetNestedType("ImportStubEntry", BindingFlags.NonPublic)!;
            var entries = Array.CreateInstance(entryType, 2);
            for (var index = 0; index < 2; index++)
            {
                var dispatchNid = index == 0 ? nid : "test-full-audio-queue-query";
                var export = new ExportedFunction("libSceAudioOut2", dispatchNid, "queue query", Generation.Gen5,
                    lowLatency ? SharpEmu.Libs.Audio.AudioOut2Exports.AudioOut2LoContextGetQueueLevel : SharpEmu.Libs.Audio.AudioOut2Exports.AudioOut2ContextGetQueueLevel);
                entries.SetValue(Activator.CreateInstance(entryType, [0x200000UL, dispatchNid, export, false, false, false, false, 0UL]), index);
            }
            type.GetField("_importEntries", Instance)!.SetValue(_backend, entries);
            Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_AUDIO_OUT2_QUEUE_LEAF") != "0", DirectExecutionBackend.IsTrivialLeafImport(nid));
            Assert.False(DirectExecutionBackend.IsTrivialLeafImport("test-full-audio-queue-query"));
            Assert.False((bool)type.GetField("_disableTrivialLeafDispatch", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);
            _dispatch = type.GetMethod("DispatchImportCore", Instance)!.CreateDelegate<Func<int, nint, ulong>>(_backend);
        }

        internal void Arguments(ulong context, ulong level, ulong available)
        {
            Marshal.WriteInt64(Pack, 0, unchecked((long)context));
            Marshal.WriteInt64(Pack, 8, unchecked((long)level));
            Marshal.WriteInt64(Pack, 16, unchecked((long)available));
        }
        internal ulong Dispatch(int index) => _dispatch(index, Pack);
        public void Dispose() { _backend.Dispose(); Marshal.FreeHGlobal(_allocation); }
    }
}
