// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Waits for host socket readiness on one background thread and reports each
/// armed descriptor once when it becomes readable or writable, so read/write
/// knotes and poll/select waiters follow socket state changes instead of
/// being polled. An arm is one-shot: whoever still needs the descriptor after
/// the report (a knote that is still not ready, a poll waiter) arms it again.
/// </summary>
internal static class DescriptorReadinessMonitor
{
    // A descriptor re-armed right after a report that did not make it ready
    // (data below the low-water mark) is held back this long, so a socket the
    // host keeps reporting as readable cannot spin the monitor.
    private static readonly long RearmHoldoffTicks = Stopwatch.Frequency * 2 / 1000;

    private readonly record struct Key(int Fd, bool Write);

    private sealed class Entry
    {
        public required Socket Socket { get; init; }
        public long NotBefore;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<Key, Entry> _armed = new();
    private static readonly Dictionary<Key, long> _lastReported = new();
    private static Thread? _thread;
    private static Socket? _wakeReceiver;
    private static Socket? _wakeSender;
    private static EndPoint? _wakeEndpoint;
    private static readonly byte[] _wakeByte = [1];

    /// <summary>Raised on the monitor thread with each ready descriptor.</summary>
    public static event Action<int>? DescriptorReady;

    public static void Arm(int fd, bool write, Socket socket)
    {
        lock (_gate)
        {
            var key = new Key(fd, write);
            if (_armed.TryGetValue(key, out var existing) && ReferenceEquals(existing.Socket, socket))
            {
                return;
            }

            var notBefore = 0L;
            if (_lastReported.TryGetValue(key, out var reported))
            {
                var holdoff = reported + RearmHoldoffTicks;
                if (holdoff > Stopwatch.GetTimestamp())
                {
                    notBefore = holdoff;
                }
            }

            _armed[key] = new Entry { Socket = socket, NotBefore = notBefore };
            EnsureThreadLocked();
        }

        Wake();
    }

    public static void Disarm(int fd)
    {
        lock (_gate)
        {
            _armed.Remove(new Key(fd, false));
            _armed.Remove(new Key(fd, true));
            _lastReported.Remove(new Key(fd, false));
            _lastReported.Remove(new Key(fd, true));
        }
    }

    private static void EnsureThreadLocked()
    {
        if (_thread is not null)
        {
            return;
        }

        var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _wakeReceiver = receiver;
        _wakeEndpoint = receiver.LocalEndPoint;
        _wakeSender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu descriptor readiness",
        };
        _thread.Start();
    }

    private static void Wake()
    {
        Socket? sender;
        EndPoint? endpoint;
        lock (_gate)
        {
            sender = _wakeSender;
            endpoint = _wakeEndpoint;
        }

        if (sender is null || endpoint is null)
        {
            return;
        }

        try
        {
            sender.SendTo(_wakeByte, endpoint);
        }
        catch (SocketException)
        {
            // The receive buffer is full of wake bytes: the monitor is already awake.
        }
    }

    private static void Run()
    {
        var read = new List<Socket>();
        var write = new List<Socket>();
        var ready = new List<int>();
        var drain = new byte[64];
        while (true)
        {
            read.Clear();
            write.Clear();
            ready.Clear();
            var timeoutMicroseconds = -1;
            lock (_gate)
            {
                read.Add(_wakeReceiver!);
                var now = Stopwatch.GetTimestamp();
                foreach (var (key, entry) in _armed)
                {
                    if (entry.NotBefore > now)
                    {
                        var waitTicks = entry.NotBefore - now;
                        var waitMicroseconds = (int)Math.Max(1, waitTicks * 1_000_000 / Stopwatch.Frequency);
                        timeoutMicroseconds = timeoutMicroseconds < 0
                            ? waitMicroseconds
                            : Math.Min(timeoutMicroseconds, waitMicroseconds);
                        continue;
                    }

                    var list = key.Write ? write : read;
                    if (!list.Contains(entry.Socket))
                    {
                        list.Add(entry.Socket);
                    }
                }
            }

            try
            {
                Socket.Select(read, write.Count == 0 ? null : write, null, timeoutMicroseconds);
            }
            catch (ObjectDisposedException)
            {
                ReportDisposedSockets(ready);
                Dispatch(ready);
                continue;
            }
            catch (SocketException)
            {
                ReportDisposedSockets(ready);
                Dispatch(ready);
                continue;
            }

            if (read.Remove(_wakeReceiver!))
            {
                try
                {
                    while (_wakeReceiver!.Available > 0)
                    {
                        _ = _wakeReceiver.Receive(drain);
                    }
                }
                catch (SocketException)
                {
                }
            }

            lock (_gate)
            {
                CollectLocked(read, write: false, ready);
                CollectLocked(write, write: true, ready);
            }

            Dispatch(ready);
        }
    }

    private static void CollectLocked(List<Socket> signalled, bool write, List<int> ready)
    {
        if (signalled.Count == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        List<Key>? fired = null;
        foreach (var (key, entry) in _armed)
        {
            if (key.Write == write && signalled.Contains(entry.Socket))
            {
                (fired ??= []).Add(key);
            }
        }

        if (fired is null)
        {
            return;
        }

        foreach (var key in fired)
        {
            _armed.Remove(key);
            _lastReported[key] = now;
            if (!ready.Contains(key.Fd))
            {
                ready.Add(key.Fd);
            }
        }
    }

    // A socket closed while armed: report it so its owners see the descriptor
    // vanish (knotes drop, poll reports POLLNVAL).
    private static void ReportDisposedSockets(List<int> ready)
    {
        lock (_gate)
        {
            List<Key>? dead = null;
            foreach (var (key, entry) in _armed)
            {
                try
                {
                    _ = entry.Socket.Available;
                }
                catch (ObjectDisposedException)
                {
                    (dead ??= []).Add(key);
                }
                catch (SocketException)
                {
                    (dead ??= []).Add(key);
                }
            }

            // Select failed without a disposed socket to blame: report every arm,
            // and the re-arm holdoff paces the owners' retries.
            dead ??= [.. _armed.Keys];
            var now = Stopwatch.GetTimestamp();
            foreach (var key in dead)
            {
                _armed.Remove(key);
                _lastReported[key] = now;
                if (!ready.Contains(key.Fd))
                {
                    ready.Add(key.Fd);
                }
            }
        }
    }

    private static void Dispatch(List<int> ready)
    {
        var handler = DescriptorReady;
        if (handler is null)
        {
            return;
        }

        foreach (var fd in ready)
        {
            handler(fd);
        }
    }
}
