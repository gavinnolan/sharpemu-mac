// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.GpuRemote.Tests;

/// <summary>
/// The real GPU host executable, started from this (native) test process. The tests in this
/// class share nothing, but each starts a process, so xunit's per-class serialization keeps
/// them from piling up.
/// </summary>
public sealed class GpuProcessHostTests(ITestOutputHelper output)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    [MacOSFact]
    public void HostStartsNativeAnswersAndExitsCleanly()
    {
        var host = GpuProcessHost.Start();
        try
        {
            Assert.Equal(host.ProcessId, host.Peer.ProcessId);
            Assert.Equal(GpuRemoteProtocol.Version, host.Peer.ProtocolVersion);
            Assert.Equal(Architecture.Arm64, host.Peer.Architecture);
            Assert.Equal(0, host.Peer.Translated);
            Assert.Equal(host.ProcessId, host.Channel.GpuProcessId);
            Assert.Equal(Environment.ProcessId, host.Channel.HostProcessId);
            Assert.True(host.TryPing(CallTimeout, out var ring));
            Assert.True(host.TrySyncPing(CallTimeout, out var sync));
            output.WriteLine($"startup {host.StartupTime.TotalMilliseconds:F1} ms, ring {ring.TotalMicroseconds:F1} us, sync {sync.TotalMicroseconds:F1} us");

            // The name is gone once the host has attached; only the two mappings keep it alive.
            Assert.Throws<IOException>(() => SharedMemoryRegion.Open(host.Channel.Name));
        }
        finally
        {
            host.Dispose();
        }

        Assert.True(host.HasExited);
        Assert.Equal(GpuProcessClient.ExitClean, host.ExitCode);
    }

    [MacOSFact]
    public void HostDeathFailsPendingAndLaterCallsPromptly()
    {
        using var host = GpuProcessHost.Start();
        Assert.Equal(0, RemoteNative.kill(host.ProcessId, RemoteNative.SIGKILL));
        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.False(host.Channel.IsPeerAlive);
        Assert.Null(host.ExitCode);

        var watch = Stopwatch.StartNew();
        Assert.False(host.TryPing(TimeSpan.FromSeconds(30), out _));
        Assert.False(host.TrySyncPing(TimeSpan.FromSeconds(30), out _));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"calls to a dead host took {watch.Elapsed}");
    }

    [MacOSFact]
    public void AHostThatExitsDuringTheHandshakeIsReported()
    {
        var exception = Assert.Throws<GpuProcessException>(() =>
            GpuProcessHost.Start(new GpuProcessHostOptions { ExecutablePath = "/usr/bin/true", HandshakeTimeout = TimeSpan.FromSeconds(10) }));
        Assert.Contains("exited during the handshake", exception.Message);
    }

    [MacOSFact]
    public void AMissingExplicitExecutableIsNotReplacedByAnotherBuild()
    {
        Assert.Null(GpuProcessHost.FindExecutable("/nonexistent/SharpEmu.GpuHost"));
        Assert.NotNull(GpuProcessHost.FindExecutable());
    }

    [MacOSFact]
    public void ManyPingsFromSeveralThreadsAllComeBack()
    {
        using var host = GpuProcessHost.Start();
        var failures = 0;
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                if (!(i % 2 == 0 ? host.TryPing(CallTimeout, out var _) : host.TrySyncPing(CallTimeout, out var _)))
                {
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        Assert.Equal(0, failures);
    }

    [MacOSFact]
    public void NativeToNativeLatencyAndThroughput()
    {
        // Reported, loosely bounded: the numbers depend on the machine and its load.
        using (var host = GpuProcessHost.Start())
        {
            var ring = GpuRemoteBenchmark.MeasureRingPing(host, 20_000, SpscRing.DefaultSpinNanoseconds);
            var sync = GpuRemoteBenchmark.MeasureSyncPing(host, 20_000, SyncCallTable.DefaultSpinNanoseconds);
            var outbound = GpuRemoteBenchmark.MeasureOutboundThroughput(host, 1_000_000, 16);
            var inbound = GpuRemoteBenchmark.MeasureInboundThroughput(host, 1_000_000, 16);
            output.WriteLine($"arm64 -> arm64, doorbell {GpuProcessHost.DoorbellMechanism}");
            output.WriteLine($"ring (spin):  {ring}");
            output.WriteLine($"sync (spin):  {sync}");
            output.WriteLine($"16-byte records: A->B {outbound / 1e6:F2} M/s, B->A {inbound / 1e6:F2} M/s");
            Assert.True(ring.P50Ns < 1_000_000);
            Assert.True(sync.P50Ns < 1_000_000);
        }

        using (var host = GpuProcessHost.Start(new GpuProcessHostOptions { HostSpinNanoseconds = 0 }))
        {
            var ring = GpuRemoteBenchmark.MeasureRingPing(host, 5_000, 0);
            var sync = GpuRemoteBenchmark.MeasureSyncPing(host, 5_000, 0);
            output.WriteLine($"ring (no spin): {ring}");
            output.WriteLine($"sync (no spin): {sync}");
            Assert.True(ring.P50Ns < 1_000_000);
            Assert.True(sync.P50Ns < 1_000_000);
        }
    }
}
