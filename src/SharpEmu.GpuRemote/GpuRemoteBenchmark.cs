// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.GpuRemote;

/// <summary>Latency distribution of a measured round trip, in nanoseconds.</summary>
public readonly record struct LatencyStats(int Samples, double MinNs, double P50Ns, double P90Ns, double P99Ns, double MaxNs, double MeanNs)
{
    public static LatencyStats From(double[] samples)
    {
        if (samples.Length == 0)
        {
            return default;
        }

        Array.Sort(samples);
        double Percentile(double fraction) => samples[Math.Min(samples.Length - 1, (int)(samples.Length * fraction))];
        return new LatencyStats(samples.Length, samples[0], Percentile(0.5), Percentile(0.9), Percentile(0.99), samples[^1], samples.Average());
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"p50={P50Ns / 1000:F2}us p90={P90Ns / 1000:F2}us p99={P99Ns / 1000:F2}us min={MinNs / 1000:F2}us max={MaxNs / 1000:F1}us mean={MeanNs / 1000:F2}us n={Samples}");
}

/// <summary>
/// Measurements of a running <see cref="GpuProcessHost"/>: ring and sync-slot round trips, and
/// small-record throughput each way. Shared by the tests and the cross-architecture smoke tool.
/// </summary>
public static class GpuRemoteBenchmark
{
    private const long CallTimeoutNanoseconds = 5_000_000_000;

    /// <summary>Ring round trips (Ping → Pong), with <paramref name="spinNanoseconds"/> of spinning on A's side.</summary>
    public static LatencyStats MeasureRingPing(GpuProcessHost host, int iterations, long spinNanoseconds, int warmup = 500)
    {
        var samples = new double[iterations];
        for (var i = -warmup; i < iterations; i++)
        {
            if (!host.TryPing(CallTimeoutNanoseconds, spinNanoseconds, out long roundTrip))
            {
                throw new GpuProcessException($"Ring ping {i} failed (host alive: {host.Channel.IsPeerAlive}).");
            }

            if (i >= 0)
            {
                samples[i] = roundTrip;
            }
        }

        return LatencyStats.From(samples);
    }

    /// <summary>Synchronous-slot round trips, with <paramref name="spinNanoseconds"/> of spinning on A's side.</summary>
    public static LatencyStats MeasureSyncPing(GpuProcessHost host, int iterations, long spinNanoseconds, int warmup = 500)
    {
        var samples = new double[iterations];
        for (var i = -warmup; i < iterations; i++)
        {
            if (!host.TrySyncPing(CallTimeoutNanoseconds, spinNanoseconds, out long roundTrip))
            {
                throw new GpuProcessException($"Sync ping {i} failed (host alive: {host.Channel.IsPeerAlive}).");
            }

            if (i >= 0)
            {
                samples[i] = roundTrip;
            }
        }

        return LatencyStats.From(samples);
    }

    /// <summary>
    /// A → B: writes <paramref name="records"/> <see cref="GpuRemoteMessage.Sink"/> records of
    /// <paramref name="payloadBytes"/>, then a ping to know B consumed them all. Records per second.
    /// </summary>
    public static double MeasureOutboundThroughput(GpuProcessHost host, int records, int payloadBytes)
    {
        Span<byte> payload = stackalloc byte[payloadBytes];
        var start = RemoteNative.MonotonicNanoseconds();
        lock (host.SyncRoot)
        {
            for (var i = 0; i < records; i++)
            {
                if (!host.Channel.Outbound.Write((uint)GpuRemoteMessage.Sink, payload, CallTimeoutNanoseconds))
                {
                    throw new GpuProcessException($"Sink record {i} could not be written.");
                }
            }
        }

        if (!host.TryPing(CallTimeoutNanoseconds, SpscRing.DefaultSpinNanoseconds, out long _))
        {
            throw new GpuProcessException("The ping after the sink records failed.");
        }

        return records / ((RemoteNative.MonotonicNanoseconds() - start) / 1e9);
    }

    /// <summary>B → A: asks B for <paramref name="records"/> records of <paramref name="payloadBytes"/> and drains them. Records per second.</summary>
    public static double MeasureInboundThroughput(GpuProcessHost host, int records, int payloadBytes)
    {
        lock (host.SyncRoot)
        {
            var start = RemoteNative.MonotonicNanoseconds();
            var request = new BurstMessage { Count = records, PayloadLength = payloadBytes };
            if (!host.Channel.Outbound.Write((uint)GpuRemoteMessage.Burst, in request, CallTimeoutNanoseconds))
            {
                throw new GpuProcessException("The burst request could not be written.");
            }

            var inbound = host.Channel.Inbound;
            var received = 0;
            while (received < records)
            {
                if (!inbound.WaitForData(CallTimeoutNanoseconds, SpscRing.DefaultSpinNanoseconds))
                {
                    if (!host.Channel.IsPeerAlive || RemoteNative.MonotonicNanoseconds() - start > CallTimeoutNanoseconds * 4)
                    {
                        throw new GpuProcessException($"Only {received} of {records} burst records arrived (host alive: {host.Channel.IsPeerAlive}).");
                    }

                    continue;
                }

                while (received < records && inbound.TryRead(out var type, out _))
                {
                    inbound.Advance();
                    if (type == (uint)GpuRemoteMessage.BurstItem)
                    {
                        received++;
                    }
                }
            }

            return records / ((RemoteNative.MonotonicNanoseconds() - start) / 1e9);
        }
    }
}
