// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.PerfAudit;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed partial class VertexInputResolverTests
{
    // Optional paired component benchmark. No elapsed-time assertions or game-FPS claims.
    [Fact]
    public void BenchmarkVertexLayoutHistory()
    {
        var output = Environment.GetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var (shader, userData) = PrepareHistoryFixture();
        for (uint index = 0; index < 1024; index++)
        {
            var address = MemoryBase + 0x2000 + index * 16;
            WriteWord(address, unchecked((uint)(StreamBase + index * 256)));
            WriteWord(address + 4, (uint)(StreamBase >> 32) | (16u << 16));
            WriteWord(address + 8, index + 3);
            WriteWord(address + 12, IdentitySelect | (BufferFormat32x4Float << 12));
        }
        const int iterations = 200_000;
        var results = new List<object>();
        foreach (var scenario in new[] { "last", "cycle2", "cycle8", "cycle64", "cycle1024", "unique", "cold" })
        {
            var variants = scenario switch { "cycle2" => 2, "cycle8" => 8, "cycle64" => 64, "cycle1024" => 1024, _ => 1 };
            VertexTableMetadataCache CreateCache(bool enabled)
            {
                var prior = Environment.GetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_HISTORY");
                try
                {
                    Environment.SetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_HISTORY", enabled ? "1" : "0");
                    return new VertexTableMetadataCache();
                }
                finally { Environment.SetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_HISTORY", prior); }
            }
            var caches = new[] { CreateCache(false), CreateCache(true) };
            long sequence = 0;
            (double WallNs, double CpuNs, double Bytes, ulong Checksum) Run(int arm, int count)
            {
                Span<byte> record = stackalloc byte[4];
                ulong checksum = 0;
                var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var cpuStart = OperatingSystem.IsMacOS() ? DarwinThreadCpuClock.Nanoseconds() : 0;
                for (var index = 0; index < count; index++)
                {
                    var variant = index & (variants - 1);
                    userData[10] = (uint)(0x2000 + variant * 16);
                    if (scenario == "unique")
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)++sequence);
                        if (!_memory.TryWrite(MemoryBase + 0x2008, record)) throw new InvalidOperationException();
                    }
                    var hash = scenario == "cold" ? (ulong)++sequence : 1;
                    var resolved = VertexInputResolver.ResolveVertexInputs(_context, shader, userData,
                        metadataCache: caches[arm], codeHash: hash);
                    checksum += resolved.Buffers[0].Address + (uint)resolved.Attributes[1].RegisterStart;
                }
                var cpuEnd = OperatingSystem.IsMacOS() ? DarwinThreadCpuClock.Nanoseconds() : 0;
                var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
                return (elapsed / count, (cpuEnd - cpuStart) / (double)count, allocated / (double)count, checksum);
            }
            // Warm both configurations with ordinary tiering before alternating order.
            var warmup = Stopwatch.StartNew();
            do { Run(0, iterations); Run(1, iterations); } while (warmup.ElapsedMilliseconds < (scenario == "last" ? 10_000 : 5_000));
            for (var round = 0; round < 9; round++)
            {
                foreach (var arm in round % 2 == 0 ? new[] { 0, 1 } : new[] { 1, 0 })
                {
                    var sample = Run(arm, iterations);
                    ulong expected = 0;
                    for (var index = 0; index < iterations; index++) expected += StreamBase + (ulong)(index & (variants - 1)) * 256 + 4;
                    Assert.Equal(expected, sample.Checksum);
                    results.Add(new { scenario, enabled = arm == 1, round, iterations,
                        wall_ns = sample.WallNs, cpu_ns = sample.CpuNs, bytes = sample.Bytes, checksum = sample.Checksum });
                }
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription, results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
