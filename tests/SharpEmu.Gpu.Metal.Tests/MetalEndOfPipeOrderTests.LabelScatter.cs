// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalEndOfPipeOrderTests
{
    // Held copies land through one compute dispatch (MetalRenderHost.LabelScatter): every word
    // ends up with what the last copy into it carried, as the blits left it.
    [Fact]
    public void ManyHeldCopiesLandThroughOneDispatchAndTheLastCopyIntoAWordWins()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        Assert.True(host.DefersCopiesInCompute);

        _ = host.EnsureComputeEncoder();
        for (var index = 0; index < 48; index++)
        {
            host.RecordCommandProcessorWrite(LaterLabel + (ulong)index * 16, BitConverter.GetBytes(0x1000UL + (ulong)index), "WRITE_DATA");
        }

        // Rewrites of earlier qwords, one of them twice, and a dword over half of another.
        host.RecordCommandProcessorWrite(LaterLabel + 3 * 16, BitConverter.GetBytes(0xAAAA_0003UL), "WRITE_DATA");
        host.RecordCommandProcessorWrite(LaterLabel + 7 * 16, BitConverter.GetBytes(0xBBBB_0007UL), "WRITE_DATA");
        host.RecordCommandProcessorWrite(LaterLabel + 7 * 16, BitConverter.GetBytes(0xCCCC_0007UL), "WRITE_DATA");
        host.RecordCommandProcessorWrite(LaterLabel + 9 * 16 + 4, BitConverter.GetBytes(0xDDDDu), "WRITE_DATA");
        var before = host.LabelScatterTotals;

        host.OrderAfterDeferredCopies(0, ulong.MaxValue);
        Assert.Equal(0, host.DeferredLabelCopyCount);
        Assert.Equal(before.Batches + 1, host.LabelScatterTotals.Batches);

        host.FlushCommandBuffer();
        WaitForGpu(host, host.LastWorkSerial);
        WaitForCompletions(host);
        for (var index = 0; index < 48; index++)
        {
            var expected = index switch
            {
                3 => 0xAAAA_0003UL,
                7 => 0xCCCC_0007UL,
                9 => (0xDDDDUL << 32) | (0x1000UL + 9),
                _ => 0x1000UL + (ulong)index,
            };
            Assert.Equal(expected, rig.Backing.Read64(LaterLabel + (ulong)index * 16));
        }
    }

    // A copy that is not whole 32-bit words keeps the blits for its batch.
    [Fact]
    public void AHeldBatchWithAnUnalignedCopyLandsThroughBlits()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;

        _ = host.EnsureComputeEncoder();
        for (var index = 0; index < 32; index++)
        {
            host.RecordCommandProcessorWrite(LaterLabel + (ulong)index * 8, BitConverter.GetBytes(0x2000UL + (ulong)index), "WRITE_DATA");
        }

        host.RecordCommandProcessorWrite(LaterLabel + 0x400, [0x11, 0x22, 0x33], "WRITE_DATA");
        var before = host.LabelScatterTotals;

        host.OrderAfterDeferredCopies(0, ulong.MaxValue);
        Assert.Equal(before.Batches, host.LabelScatterTotals.Batches);
        Assert.Equal(before.Declined + 1, host.LabelScatterTotals.Declined);

        host.FlushCommandBuffer();
        WaitForGpu(host, host.LastWorkSerial);
        WaitForCompletions(host);
        for (var index = 0; index < 32; index++)
        {
            Assert.Equal(0x2000UL + (ulong)index, rig.Backing.Read64(LaterLabel + (ulong)index * 8));
        }

        Assert.Equal(0x332211UL, rig.Backing.Read64(LaterLabel + 0x400) & 0xFF_FFFF);
    }

    // A binding that no held copy touched is not scanned again until another copy is held, and
    // the copy held afterwards is found: the second answer starts where the first left off.
    [Fact]
    public void AnUntouchedRangeIsFoundTouchedOnceACopyOverItIsHeld()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;

        _ = host.EnsureComputeEncoder();
        for (var index = 0; index < 64; index++)
        {
            host.RecordCommandProcessorWrite(LaterLabel + (ulong)index * 16, BitConverter.GetBytes(0x3000UL + (ulong)index), "WRITE_DATA");
        }

        // Between two held qwords, and inside the span and the pages the held copies cover.
        var between = LaterLabel + 3 * 16 + 8;
        host.OrderAfterDeferredCopies(between, 8);
        host.OrderAfterDeferredCopies(between, 8);
        Assert.Equal(64, host.DeferredLabelCopyCount);

        host.RecordCommandProcessorWrite(LaterLabel + 70 * 16, BitConverter.GetBytes(0x4000UL), "WRITE_DATA");
        host.OrderAfterDeferredCopies(between, 8);
        Assert.Equal(65, host.DeferredLabelCopyCount);

        host.RecordCommandProcessorWrite(between, BitConverter.GetBytes(0x5000UL), "WRITE_DATA");
        host.OrderAfterDeferredCopies(between, 8);
        Assert.Equal(0, host.DeferredLabelCopyCount);

        // The copies landed: a new batch starts from nothing, and the old answer is not reused.
        _ = host.EnsureComputeEncoder();
        for (var index = 0; index < 8; index++)
        {
            host.RecordCommandProcessorWrite(LaterLabel + (ulong)index * 32, BitConverter.GetBytes(0x6000UL + (ulong)index), "WRITE_DATA");
        }

        host.OrderAfterDeferredCopies(between, 8);
        Assert.Equal(8, host.DeferredLabelCopyCount);
        host.RecordCommandProcessorWrite(between, BitConverter.GetBytes(0x7000UL), "WRITE_DATA");
        host.OrderAfterDeferredCopies(between, 8);
        Assert.Equal(0, host.DeferredLabelCopyCount);

        host.FlushCommandBuffer();
        WaitForGpu(host, host.LastWorkSerial);
        WaitForCompletions(host);
        Assert.Equal(0x7000UL, rig.Backing.Read64(between));
    }

    // SHARPEMU_LABEL_SCATTER_BENCH_OUTPUT=<path>: the worker's cost of landing a held batch, and the
    // GPU's time to run it, through blits and through the scatter, alternating per round.
    [Fact]
    public void MeasureLabelScatterWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_SCATTER_BENCH_OUTPUT");
        if (path is null || !RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        var rounds = new List<object>();
        var value = new byte[8];
        // Past the page no window reaches.
        const ulong benchBase = GuestBase + 0x10000;
        foreach (var copies in new[] { 64, 1024, 4096 })
        {
            for (var round = 0; round < 11; round++)
            {
                foreach (var scatter in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    host.LabelScatterEnabled = scatter;
                    _ = host.EnsureComputeEncoder();
                    for (var index = 0; index < copies; index++)
                    {
                        BitConverter.TryWriteBytes(value, (ulong)(round * 100_000 + index));
                        host.RecordCommandProcessorWrite(benchBase + (ulong)index * 16, value, "WRITE_DATA");
                    }

                    var started = Stopwatch.GetTimestamp();
                    host.OrderAfterDeferredCopies(0, ulong.MaxValue);
                    var encoded = Stopwatch.GetTimestamp();
                    var serial = host.FlushCommandBuffer();
                    WaitForGpu(host, serial);
                    var landed = Stopwatch.GetTimestamp();
                    WaitForCompletions(host);
                    Assert.Equal((ulong)(round * 100_000 + copies - 1), rig.Backing.Read64(benchBase + (ulong)(copies - 1) * 16));
                    if (round == 0) continue;
                    rounds.Add(new { copies, round, scatter,
                        encodeMicroseconds = (encoded - started) * 1e6 / Stopwatch.Frequency,
                        landMicroseconds = (landed - encoded) * 1e6 / Stopwatch.Frequency });
                }
            }
        }

        host.LabelScatterEnabled = true;
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), rounds },
            new JsonSerializerOptions { WriteIndented = true }));
    }
    // SHARPEMU_LABEL_SCAN_BENCH_OUTPUT=<path>: nanoseconds per binding lookup against held copies, for
    // ranges that sit between them (so no filter rejects them and each lookup scans), with a copy
    // held now and then. Run once per setting of SHARPEMU_METAL_V2_LABEL_SCAN_MEMO.
    [Fact]
    public void MeasureLabelScanWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_SCAN_BENCH_OUTPUT");
        if (path is null || !RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        var rounds = new List<object>();
        var value = new byte[8];
        const ulong benchBase = GuestBase + 0x10000;
        foreach (var held in new[] { 256, 2048 })
        {
            for (var round = 0; round < 14; round++)
            {
                _ = host.EnsureComputeEncoder();
                for (var index = 0; index < held; index++)
                {
                    host.RecordCommandProcessorWrite(benchBase + (ulong)index * 16, value, "WRITE_DATA");
                }

                const int queries = 200_000;
                var started = Stopwatch.GetTimestamp();
                for (var query = 0; query < queries; query++)
                {
                    // Sixty-four distinct 8-byte gaps between held qwords, asked in turn.
                    host.OrderAfterDeferredCopies(benchBase + (ulong)(query % 64) * 16 * (ulong)(held / 64) + 8, 8);
                    if (query % 4096 == 4095)
                    {
                        host.RecordCommandProcessorWrite(benchBase + 0x8000 + (ulong)(query / 4096) * 16, value, "WRITE_DATA");
                    }
                }

                var elapsed = Stopwatch.GetTimestamp() - started;
                Assert.True(host.DeferredLabelCopyCount >= held);
                host.OrderAfterDeferredCopies(0, ulong.MaxValue);
                if (round < 8) continue;
                rounds.Add(new { held, round, nsPerQuery = elapsed * 1e9 / Stopwatch.Frequency / queries });
            }
        }

        File.WriteAllText(path, JsonSerializer.Serialize(new {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            scanMemo = Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_LABEL_SCAN_MEMO") != "0", rounds },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
