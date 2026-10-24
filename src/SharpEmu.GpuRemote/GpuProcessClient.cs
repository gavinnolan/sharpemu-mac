// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>
/// Process B's end of the split: attaches to the region A created, answers the handshake,
/// watches A, and runs the message loop. For now the loop only answers pings and the
/// throughput probes; it is where the Metal backend's command intake will go.
/// </summary>
/// <remarks>
/// Two threads serve A: the caller of <see cref="Run"/> drains the A → B ring, and a dedicated
/// thread serves the synchronous call table, so a call never queues behind ring traffic. Both
/// spin for <see cref="SpinNanoseconds"/> after each piece of work before sleeping on their
/// doorbells. When A exits - cleanly, by <see cref="GpuRemoteMessage.Shutdown"/>, or by dying,
/// which the parent watch reports - both loops end and <see cref="Run"/> returns.
/// </remarks>
public sealed class GpuProcessClient : IDisposable
{
    /// <summary>Exit codes of the GPU host process.</summary>
    public const int ExitClean = 0;
    public const int ExitUsage = 2;
    public const int ExitHostGone = 3;
    public const int ExitProtocolError = 4;

    public const long DefaultSpinNanoseconds = 50_000;

    private readonly int _parentProcessId;
    private readonly ManualResetEventSlim _parentExited = new(false);
    private Thread? _syncThread;
    private volatile bool _stopRequested;
    private int _disposed;

    private GpuProcessClient(GpuRemoteChannel channel, int parentProcessId, GpuPeerInfo host, long spinNanoseconds)
    {
        Channel = channel;
        _parentProcessId = parentProcessId;
        Host = host;
        SpinNanoseconds = spinNanoseconds;
    }

    public GpuRemoteChannel Channel { get; }

    /// <summary>What process A reported in its handshake.</summary>
    public GpuPeerInfo Host { get; }

    public long SpinNanoseconds { get; }

    /// <summary>Records consumed by <see cref="GpuRemoteMessage.Sink"/>; diagnostics.</summary>
    public long SinkRecords { get; private set; }

    /// <summary>
    /// Attaches to <paramref name="regionName"/>, reads A's handshake, answers it, and starts
    /// watching <paramref name="parentProcessId"/> (pass 0 to skip the watch, for in-process tests).
    /// </summary>
    public static GpuProcessClient Attach(string regionName, int parentProcessId, TimeSpan handshakeTimeout, long spinNanoseconds = DefaultSpinNanoseconds)
    {
        var channel = GpuRemoteChannel.Attach(regionName);
        try
        {
            var inbound = channel.Inbound;
            var timeout = (long)(handshakeTimeout.Ticks * 100.0);
            var start = RemoteNative.MonotonicNanoseconds();
            uint type;
            ReadOnlySpan<byte> payload;
            while (!inbound.TryRead(out type, out payload))
            {
                if (!inbound.WaitForData(RemoteWait.Remaining(start, timeout), spinNanoseconds: 0) &&
                    (!channel.IsPeerAlive || RemoteWait.Remaining(start, timeout) == 0))
                {
                    throw new InvalidDataException($"No handshake from the host process within {handshakeTimeout.TotalSeconds:F0} s.");
                }
            }

            if (type != (uint)GpuRemoteMessage.Hello)
            {
                throw new InvalidDataException($"The host process opened with record type {type} instead of a handshake.");
            }

            var host = GpuPeerInfo.From(RingReader.Read<HelloMessage>(payload));
            inbound.Advance();

            // Answered even on a version mismatch, so A can report both versions.
            var reply = GpuPeerInfo.DescribeThisProcess();
            if (!channel.Outbound.TryWrite((uint)GpuRemoteMessage.HelloAck, in reply))
            {
                throw new InvalidDataException("The handshake answer does not fit the ring.");
            }

            if (host.ProtocolVersion != GpuRemoteProtocol.Version)
            {
                throw new InvalidDataException(
                    $"The host process speaks protocol {host.ProtocolVersion}; this GPU host speaks {GpuRemoteProtocol.Version}.");
            }

            var client = new GpuProcessClient(channel, parentProcessId, host, spinNanoseconds);
            if (parentProcessId > 0)
            {
                if (RemoteNative.getppid() != parentProcessId)
                {
                    Console.Error.WriteLine(
                        $"[GPUHOST][WARN] Parent is {RemoteNative.getppid()}, not the host process {parentProcessId}; watching {parentProcessId} anyway.");
                }

                ProcessExitWatch.Start(parentProcessId, "SharpEmu GPU host parent watch", client.OnParentExited, reapChild: false);
            }

            return client;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>Set once the parent watch has seen process A exit.</summary>
    public bool ParentExited => _parentExited.IsSet;

    /// <summary>
    /// Runs the message loop until A asks to shut down (<see cref="ExitClean"/>) or goes away
    /// (<see cref="ExitHostGone"/>); <see cref="ExitProtocolError"/> on a corrupt ring.
    /// </summary>
    public int Run()
    {
        _syncThread = new Thread(ServeSyncCalls, 256 * 1024)
        {
            IsBackground = true,
            Name = "SharpEmu GPU host sync calls",
        };
        _syncThread.Start();

        try
        {
            return RunRing();
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"[GPUHOST][ERROR] {exception.Message}");
            return ExitProtocolError;
        }
        finally
        {
            _stopRequested = true;
            Channel.SyncCalls.WakeAll();
            _ = _syncThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Makes <see cref="Run"/> return at its next wake-up.</summary>
    public void RequestStop()
    {
        _stopRequested = true;
        Channel.WakeAll();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopRequested = true;
        Channel.WakeAll();
        _ = _syncThread?.Join(TimeSpan.FromSeconds(2));
        Channel.Dispose();
    }

    private int RunRing()
    {
        var inbound = Channel.Inbound;
        var outbound = Channel.Outbound;
        while (true)
        {
            if (!inbound.WaitForData(-1, SpinNanoseconds))
            {
                if (!Channel.IsPeerAlive)
                {
                    return ExitHostGone;
                }

                if (_stopRequested)
                {
                    return ExitClean;
                }

                continue;
            }

            while (inbound.TryRead(out var type, out var payload))
            {
                switch ((GpuRemoteMessage)type)
                {
                    case GpuRemoteMessage.Ping:
                    {
                        var ping = RingReader.Read<PingMessage>(payload);
                        inbound.Advance();
                        if (!outbound.Write((uint)GpuRemoteMessage.Pong, in ping, -1))
                        {
                            return ExitHostGone;
                        }

                        break;
                    }

                    case GpuRemoteMessage.Shutdown:
                        inbound.Advance();
                        return ExitClean;

                    case GpuRemoteMessage.Sink:
                        inbound.Advance();
                        SinkRecords++;
                        break;

                    case GpuRemoteMessage.Burst:
                    {
                        var burst = RingReader.Read<BurstMessage>(payload);
                        inbound.Advance();
                        if (!WriteBurst(outbound, burst))
                        {
                            return ExitHostGone;
                        }

                        break;
                    }

                    default:
                        Console.Error.WriteLine($"[GPUHOST][WARN] Ignoring unknown record type {type} ({payload.Length} bytes).");
                        inbound.Advance();
                        break;
                }
            }
        }
    }

    private static bool WriteBurst(RingWriter outbound, BurstMessage burst)
    {
        var length = Math.Clamp(burst.PayloadLength, 0, outbound.MaxPayloadLength);
        Span<byte> payload = stackalloc byte[Math.Min(length, 4096)];
        if (length > payload.Length)
        {
            payload = new byte[length];
        }

        for (var i = 0; i < burst.Count; i++)
        {
            MemoryMarshal.Write(payload, in i);
            if (!outbound.Write((uint)GpuRemoteMessage.BurstItem, payload, -1))
            {
                return false;
            }
        }

        return true;
    }

    private void ServeSyncCalls()
    {
        var calls = Channel.SyncCalls;
        SyncCallHandler handler = Serve;
        while (!_stopRequested)
        {
            if (calls.WaitForRequests(-1, SpinNanoseconds))
            {
                _ = calls.ServePending(handler);
            }
            else if (!Channel.IsPeerAlive)
            {
                return;
            }
        }
    }

    private static int Serve(uint opcode, ReadOnlySpan<byte> request, Span<byte> response, out int status)
    {
        switch ((GpuRemoteSyncOpcode)opcode)
        {
            case GpuRemoteSyncOpcode.Ping:
                // Request and response share the slot's payload, so the echo is already in place.
                status = 0;
                return request.Length;
            default:
                status = -1;
                return 0;
        }
    }

    private void OnParentExited(int status)
    {
        _parentExited.Set();
        Console.Error.WriteLine($"[GPUHOST][INFO] Host process {_parentProcessId} is gone; exiting.");
        Channel.MarkPeerExited();
    }
}
