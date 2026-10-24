// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.GpuRemote;

namespace SharpEmu.GpuSplit;

/// <summary>
/// The writing end of one ring for every thread of a process: the ring has one producer, so
/// writers take <see cref="Gate"/> (the lock the transport's own writer in this process takes
/// too: <c>GpuProcessHost.SyncRoot</c> in A, <c>GpuProcessClient.OutboundLock</c> in B).
/// </summary>
/// <remarks>
/// A full ring is waited out for at most <see cref="TimeoutNanoseconds"/>; a write that still
/// does not fit, or whose reader has exited, fails (the reader's death is reported elsewhere, a
/// stalled reader here, once). <see cref="Written"/> counts the records written, which A's calls
/// that must come after its earlier records carry as their barrier.
/// </remarks>
internal sealed class SplitWriter
{
    public const long TimeoutNanoseconds = 30_000_000_000;

    private readonly RingWriter _ring;
    private readonly GpuRemoteChannel _channel;
    private ulong _written;
    private int _stallReported;

    public SplitWriter(GpuRemoteChannel channel, RingWriter ring, object gate)
    {
        _channel = channel;
        _ring = ring;
        Gate = gate;
    }

    public object Gate { get; }

    /// <summary>Records written so far.</summary>
    public ulong Written => Volatile.Read(ref _written);

    /// <summary>The largest payload one record carries.</summary>
    public int MaxPayloadLength => _ring.MaxPayloadLength;

    public bool Write(GpuRemoteMessage type, ReadOnlySpan<byte> payload)
    {
        lock (Gate)
        {
            return WriteLocked(type, payload);
        }
    }

    public bool Write<T>(GpuRemoteMessage type, in T value)
        where T : unmanaged =>
        Write(type, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>A record of <paramref name="header"/> followed by <paramref name="tail"/>.</summary>
    public bool Write<T>(GpuRemoteMessage type, in T header, ReadOnlySpan<byte> tail)
        where T : unmanaged
    {
        var length = Unsafe.SizeOf<T>() + tail.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            MemoryMarshal.Write(buffer, in header);
            tail.CopyTo(buffer.AsSpan(Unsafe.SizeOf<T>()));
            return Write(type, buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes with <see cref="Gate"/> already held (a caller that numbers its records under it).</summary>
    public bool WriteLocked(GpuRemoteMessage type, ReadOnlySpan<byte> payload)
    {
        if (_ring.Write((uint)type, payload, TimeoutNanoseconds))
        {
            _written++;
            return true;
        }

        if (_channel.IsPeerAlive && Interlocked.Exchange(ref _stallReported, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[GPU-SPLIT][ERROR] The other process has not read its ring for {TimeoutNanoseconds / 1_000_000_000} s; " +
                $"a {type} record was dropped.");
        }

        return false;
    }
}
