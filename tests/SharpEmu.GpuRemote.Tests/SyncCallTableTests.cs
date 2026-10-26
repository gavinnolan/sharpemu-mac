// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.GpuRemote.Tests;

public sealed unsafe class SyncCallTableTests
{
    private const long Second = 1_000_000_000;

    [MacOSFact]
    public void ServerAnswersInPlace()
    {
        using var table = new TableUnderTest(slots: 4);
        using var server = table.StartServer(static (opcode, request, response, out status) =>
        {
            status = (int)opcode * 10;
            for (var i = 0; i < request.Length; i++)
            {
                response[i] = (byte)(request[i] + 1);
            }

            return request.Length;
        });

        var response = new byte[8];
        var result = table.Calls.Call(7, [1, 2, 3], response, out var length, out var status, 5 * Second);
        Assert.Equal(SyncCallStatus.Completed, result);
        Assert.Equal(3, length);
        Assert.Equal(70, status);
        Assert.Equal(new byte[] { 2, 3, 4 }, response[..3]);
    }

    [MacOSFact]
    public void ConcurrentCallersEachGetTheirOwnAnswer()
    {
        using var table = new TableUnderTest(slots: 8);
        using var server = table.StartServer(Echo);
        var failures = 0;
        var callers = Enumerable.Range(0, 12).Select(caller => new Thread(() =>
        {
            Span<byte> request = stackalloc byte[8];
            Span<byte> response = stackalloc byte[8];
            for (var i = 0; i < 4000; i++)
            {
                var value = ((long)caller << 32) | (uint)i;
                BinaryPrimitives.WriteInt64LittleEndian(request, value);
                // Alternate between spinning and sleeping for the answer.
                var result = table.Calls.Call(1, request, response, out var length, out _, 10 * Second, i % 2 == 0 ? 0 : 20_000);
                if (result != SyncCallStatus.Completed || length != 8 || BinaryPrimitives.ReadInt64LittleEndian(response) != value)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToList();
        callers.ForEach(thread => thread.Start());
        callers.ForEach(thread => thread.Join());
        Assert.Equal(0, failures);
    }

    [MacOSFact]
    public void TimedOutCallLeavesTheSlotToTheServer()
    {
        using var table = new TableUnderTest(slots: 1);
        using var release = new ManualResetEventSlim(false);
        using var server = table.StartServer((opcode, request, response, out status) =>
        {
            if (opcode == 99)
            {
                release.Wait(10_000);
            }

            status = 0;
            return Echo(opcode, request, response, out status);
        });

        var response = new byte[8];
        Assert.Equal(SyncCallStatus.TimedOut, table.Calls.Call(99, [1], response, out _, out _, 20_000_000, 0));

        // The one slot is still the server's, so the next caller cannot claim it yet.
        Assert.Equal(SyncCallStatus.NoFreeSlot, table.Calls.Call(1, [2], response, out _, out _, 20_000_000, 0));

        // Once the server finishes the abandoned call it frees the slot itself.
        release.Set();
        Assert.Equal(SyncCallStatus.Completed, table.Calls.Call(1, [3], response, out var length, out _, 5 * Second));
        Assert.Equal(1, length);
        Assert.Equal(3, response[0]);
    }

    [MacOSFact]
    public void CallAbandonedBeforeTheServerLooksIsFreedByIt()
    {
        using var table = new TableUnderTest(slots: 1);
        var response = new byte[8];
        Assert.Equal(SyncCallStatus.TimedOut, table.Calls.Call(1, [1], response, out _, out _, 10_000_000, 0));
        Assert.Equal(0, table.Calls.ServePending(Echo));
        Assert.Equal(SyncCallStatus.Completed, CallServedInline(table, [5], response));
        Assert.Equal(5, response[0]);
    }

    [MacOSFact]
    public void PeerExitReleasesABlockedCaller()
    {
        using var table = new TableUnderTest(slots: 2);
        SyncCallStatus? result = null;
        var caller = new Thread(() => result = table.Calls.Call(1, [1], new byte[8], out _, out _, -1, 0));
        caller.Start();
        Thread.Sleep(50);
        Assert.True(caller.IsAlive);
        table.Block.MarkPeerExited();
        table.Calls.WakeAll();
        Assert.True(caller.Join(5_000));
        Assert.Equal(SyncCallStatus.PeerExited, result);
        Assert.Equal(SyncCallStatus.PeerExited, table.Calls.Call(1, [1], new byte[8], out _, out _, Second));
    }

    [MacOSFact]
    public void OversizedRequestIsRefusedWithoutClaimingASlot()
    {
        using var table = new TableUnderTest(slots: 1);
        Assert.Equal(
            SyncCallStatus.RequestTooLarge,
            table.Calls.Call(1, new byte[table.Calls.PayloadCapacity + 1], new byte[8], out _, out _, Second));
    }

    [MacOSFact]
    public void CallsAllocateNothingOnceWarm()
    {
        // The signal-context contract: no managed allocation on the caller's path.
        using var table = new TableUnderTest(slots: 4);
        using var server = table.StartServer(Echo);
        var request = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(16);
        var response = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(16);
        try
        {
            SyncCallTable.PrepareForSignalContext();
            for (var i = 0; i < 100; i++)
            {
                _ = table.Calls.Call(1, request, 16, response, 16, out _, out _, 5 * Second, i % 2 == 0 ? 0 : 20_000);
            }

            // No assertion inside the measured loop: xunit's own comparers allocate.
            var failures = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 2000; i++)
            {
                if (table.Calls.Call(1, request, 16, response, 16, out _, out _, 5 * Second, i % 2 == 0 ? 0 : 20_000) != SyncCallStatus.Completed)
                {
                    failures++;
                }
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, failures);
            Assert.Equal(0, allocated);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(request);
            System.Runtime.InteropServices.NativeMemory.Free(response);
        }
    }

    [MacOSFact]
    public void SleepingServerPoolNeverLosesACallUnderContention()
    {
        using var table = new TableUnderTest(slots: 16);
        using var pool = new ServerPool(table.Calls, threads: 8);
        const int Callers = 6;
        const int CallsEach = 3000;
        var failures = 0;
        var callers = Enumerable.Range(0, Callers).Select(caller => new Thread(() =>
        {
            Span<byte> request = stackalloc byte[8];
            Span<byte> response = stackalloc byte[8];
            for (var i = 0; i < CallsEach; i++)
            {
                var value = ((long)caller << 32) | (uint)i;
                BinaryPrimitives.WriteInt64LittleEndian(request, value);
                // Servers never spin and never time out, so a wake-up that is lost is a call that
                // is not answered; the call's own limit turns that into a failure, not a hang.
                var result = table.Calls.Call(1, request, response, out var length, out _, 30 * Second, i % 3 == 0 ? 0 : 20_000);
                if (result != SyncCallStatus.Completed || length != 8 || BinaryPrimitives.ReadInt64LittleEndian(response) != value)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToList();
        callers.ForEach(thread => thread.Start());
        callers.ForEach(thread => thread.Join());
        Assert.Equal(0, failures);
        Assert.Equal(Callers * CallsEach, pool.Served);
    }

    [MacOSFact]
    public void OneCallWakesOneSleepingServerNotTheWholePool()
    {
        using var table = new TableUnderTest(slots: 4);
        using var pool = new ServerPool(table.Calls, threads: 8);
        // Let every server go to sleep.
        Thread.Sleep(200);
        const int Calls = 400;
        var response = new byte[8];
        for (var i = 0; i < Calls; i++)
        {
            Assert.Equal(SyncCallStatus.Completed, table.Calls.Call(1, [1], response, out _, out _, 30 * Second, 0));
            // Let the woken server go back to sleep, as it does between a game's calls.
            Thread.Sleep(1);
        }

        Assert.Equal(Calls, pool.Served);
        // Waking every sleeper would make about seven of the eight find nothing, per call.
        Assert.True(pool.EmptyWakes < Calls, $"{pool.EmptyWakes} wake-ups found nothing to serve for {Calls} calls.");
    }

    private static int Echo(uint opcode, ReadOnlySpan<byte> request, Span<byte> response, out int status)
    {
        status = 0;
        request.CopyTo(response);
        return request.Length;
    }

    private static SyncCallStatus CallServedInline(TableUnderTest table, byte[] request, byte[] response)
    {
        var serving = new Thread(() =>
        {
            for (var i = 0; i < 1000 && table.Calls.ServePending(Echo) == 0; i++)
            {
                Thread.Sleep(1);
            }
        });
        serving.Start();
        var result = table.Calls.Call(1, request, response, out _, out _, 5 * Second);
        serving.Join();
        return result;
    }

    private sealed class TableUnderTest : IDisposable
    {
        public TableUnderTest(int slots)
        {
            Block = new NativeBlock(SyncCallTable.BlockSize(slots, 128));
            Calls = new SyncCallTable(Block.Pointer, slots, 128, Block.PeerState);
        }

        public NativeBlock Block { get; }

        public SyncCallTable Calls { get; }

        public Server StartServer(SyncCallHandler handler) => new(Calls, handler);

        public void Dispose() => Block.Dispose();
    }

    // The serving pool of the split (SplitGpuSession.ServeReverseCalls, GpuProcessClient.ServeSyncCalls):
    // several threads asleep on one table, each serving one call at a time.
    private sealed class ServerPool : IDisposable
    {
        private readonly SyncCallTable _calls;
        private readonly List<Thread> _threads = new();
        private volatile bool _stop;
        private long _served;
        private long _emptyWakes;

        public ServerPool(SyncCallTable calls, int threads)
        {
            _calls = calls;
            for (var i = 0; i < threads; i++)
            {
                var thread = new Thread(Serve) { IsBackground = true };
                _threads.Add(thread);
                thread.Start();
            }
        }

        public long Served => Interlocked.Read(ref _served);

        public long EmptyWakes => Interlocked.Read(ref _emptyWakes);

        private void Serve()
        {
            SyncCallHandler handler = (uint opcode, ReadOnlySpan<byte> request, Span<byte> response, out int status) =>
            {
                Interlocked.Increment(ref _served);
                return Echo(opcode, request, response, out status);
            };
            while (!_stop)
            {
                if (_calls.WaitForRequests(-1, 0))
                {
                    while (!_stop && _calls.ServeOne(handler))
                    {
                    }
                }
                else if (!_stop)
                {
                    Interlocked.Increment(ref _emptyWakes);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            // A server between its stop check and its sleep misses one wake-up: ring until all are gone.
            foreach (var thread in _threads)
            {
                while (!thread.Join(20))
                {
                    _calls.WakeAll();
                }
            }
        }
    }

    private sealed class Server : IDisposable
    {
        private readonly SyncCallTable _calls;
        private readonly Thread _thread;
        private volatile bool _stop;

        public Server(SyncCallTable calls, SyncCallHandler handler)
        {
            _calls = calls;
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    if (_calls.WaitForRequests(Second / 10, 5_000))
                    {
                        _ = _calls.ServePending(handler);
                    }
                }
            });
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _calls.WakeAll();
            _thread.Join();
        }
    }
}
