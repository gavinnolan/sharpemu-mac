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
}
