// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.GpuRemote.Tests;

/// <summary>
/// Synchronous calls made from inside a signal handler (see <see cref="SignalContextProbe"/>).
/// One class, so the process-wide SIGUSR2 handler is never installed by two tests at once.
/// </summary>
public sealed unsafe class SignalContextTests(ITestOutputHelper output)
{
    [MacOSFact]
    public void CallsCompleteFromASignalHandlerInProcess()
    {
        using var block = new NativeBlock(SyncCallTable.BlockSize(4, 128));
        var table = new SyncCallTable(block.Pointer, 4, 128, block.PeerState);
        var stop = false;
        var server = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (table.WaitForRequests(100_000_000, 5_000))
                {
                    _ = table.ServePending(static (opcode, request, response, out status) =>
                    {
                        status = 0;
                        request.CopyTo(response);
                        return request.Length;
                    });
                }
            }
        });
        server.Start();
        try
        {
            var (completed, roundTrips) = SignalContextProbe.Run(table, 2000, spinNanoseconds: 0);
            Assert.Equal(2000, completed);
            output.WriteLine($"in-process, from a signal handler: {LatencyStats.From(roundTrips)}");
        }
        finally
        {
            Volatile.Write(ref stop, true);
            table.WakeAll();
            server.Join();
        }
    }

    [MacOSFact]
    public void CallsCompleteFromASignalHandlerAcrossProcesses()
    {
        using var host = GpuProcessHost.Start();
        var (spinning, spinTrips) = SignalContextProbe.Run(host.Channel.SyncCalls, 5000, SyncCallTable.DefaultSpinNanoseconds);
        var (sleeping, sleepTrips) = SignalContextProbe.Run(host.Channel.SyncCalls, 2000, spinNanoseconds: 0);
        Assert.Equal(5000, spinning);
        Assert.Equal(2000, sleeping);
        output.WriteLine($"arm64 -> arm64 from a signal handler, caller spins: {LatencyStats.From(spinTrips)}");
        output.WriteLine($"arm64 -> arm64 from a signal handler, caller sleeps: {LatencyStats.From(sleepTrips)}");
        Assert.True(host.TrySyncPing(TimeSpan.FromSeconds(5), out _));
    }
}
