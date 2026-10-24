// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.GpuRemote;

namespace SharpEmu.Tools.GpuProcessSmoke;

/// <summary>
/// Cross-architecture smoke test of the GPU process transport. Prints <c>key=value</c> lines.
/// </summary>
/// <remarks>
/// <c>bench [iterations]</c> (default): start the GPU host, report both processes'
/// architectures, measure ring and sync-slot round trips with and without spinning and
/// small-record throughput each way, shut the host down, print <c>result=ok</c>.
/// <c>orphan</c>: start the host, print its pid, and wait to be killed - the test checks the host
/// then exits by itself.
/// Usage: <c>artifacts/bin/Release/net10.0/osx-x64/SharpEmu.Tools.GpuProcessSmoke [bench [n] | orphan]</c>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "bench";
        var iterations = args.Length > 1 && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 20_000;
        var self = GpuPeerInfo.ThisProcess;
        Emit("smoke.pid", self.ProcessId);
        Emit("smoke.arch", self.Architecture);
        Emit("smoke.translated", self.Translated);
        try
        {
            return mode switch
            {
                "bench" => Bench(iterations),
                "orphan" => Orphan(),
                _ => Fail($"unknown mode {mode}"),
            };
        }
        catch (Exception exception)
        {
            return Fail(exception.ToString().ReplaceLineEndings(" | "));
        }
    }

    private static int Bench(int iterations)
    {
        using (var host = GpuProcessHost.Start())
        {
            Describe(host);
            Emit("ring.spin", GpuRemoteBenchmark.MeasureRingPing(host, iterations, SpscRing.DefaultSpinNanoseconds));
            Emit("sync.spin", GpuRemoteBenchmark.MeasureSyncPing(host, iterations, SyncCallTable.DefaultSpinNanoseconds));
            EmitSignal("signal.sync.spin", host, Math.Max(1000, iterations / 4), SyncCallTable.DefaultSpinNanoseconds);
            Emit("throughput.outbound.16b", Rate(GpuRemoteBenchmark.MeasureOutboundThroughput(host, 1_000_000, 16)));
            Emit("throughput.inbound.16b", Rate(GpuRemoteBenchmark.MeasureInboundThroughput(host, 1_000_000, 16)));
            Emit("throughput.outbound.256b", Rate(GpuRemoteBenchmark.MeasureOutboundThroughput(host, 500_000, 256)));
        }

        // Neither side spins: every round trip pays two sleeping wake-ups.
        using (var host = GpuProcessHost.Start(new GpuProcessHostOptions { HostSpinNanoseconds = 0 }))
        {
            var sleeping = Math.Max(1000, iterations / 4);
            Emit("ring.nospin", GpuRemoteBenchmark.MeasureRingPing(host, sleeping, 0));
            Emit("sync.nospin", GpuRemoteBenchmark.MeasureSyncPing(host, sleeping, 0));
            EmitSignal("signal.sync.nospin", host, sleeping, 0);
            host.Dispose();
            Emit("host.exitcode", host.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none");
        }

        Emit("result", "ok");
        return 0;
    }

    // Sync calls made from inside a SIGUSR2 handler on this (Rosetta) thread; delivery included.
    private static void EmitSignal(string key, GpuProcessHost host, int iterations, long spinNanoseconds)
    {
        var (completed, roundTrips) = SignalContextProbe.Run(host.Channel.SyncCalls, iterations, spinNanoseconds);
        Emit(key, LatencyStats.From(roundTrips));
        Emit(key + ".completed", completed == iterations ? "all" : $"{completed}/{iterations}");
    }

    private static int Orphan()
    {
        var host = GpuProcessHost.Start();
        Describe(host);
        Emit("orphan.ready", 1);
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static void Describe(GpuProcessHost host)
    {
        Emit("host.pid", host.Peer.ProcessId);
        Emit("host.arch", host.Peer.Architecture);
        Emit("host.translated", host.Peer.Translated);
        Emit("host.protocol", host.Peer.ProtocolVersion);
        Emit("host.startup.ms", host.StartupTime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
        Emit("host.path", host.ExecutablePath);
        Emit("doorbell", GpuProcessHost.DoorbellMechanism);
    }

    private static string Rate(double perSecond) => string.Create(CultureInfo.InvariantCulture, $"{perSecond / 1e6:F2}M/s");

    private static int Fail(string message)
    {
        Emit("result", "error " + message);
        return 1;
    }

    private static void Emit(string key, object value)
    {
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}={value}"));
        Console.Out.Flush();
    }
}
