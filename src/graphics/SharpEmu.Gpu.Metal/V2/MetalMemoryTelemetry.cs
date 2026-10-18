// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Numerics;
using SharpEmu.Logging;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// SHARPEMU_LOG_MEMORY=1: the Metal side of the "[MEM]" lines. Counters of command buffers
/// created and committed and of encoders created (to set against malloc growth: every one
/// leaves ObjC objects behind when nothing drains an autorelease pool), and the guest windows
/// each command buffer named. Naming a window with useResource makes the whole window resident
/// (wired and faulted in), so the bytes named per command buffer are what the GPU path costs.
/// </summary>
internal static class MetalMemoryTelemetry
{
    public static readonly MemoryCounter CommandBuffers = MemoryTelemetry.Counter("metal.command_buffers");
    public static readonly MemoryCounter Commits = MemoryTelemetry.Counter("metal.commits");
    public static readonly MemoryCounter RenderEncoders = MemoryTelemetry.Counter("metal.encoders_render");
    public static readonly MemoryCounter ComputeEncoders = MemoryTelemetry.Counter("metal.encoders_compute");
    public static readonly MemoryCounter BlitEncoders = MemoryTelemetry.Counter("metal.encoders_blit");

    private static readonly object Gate = new();
    private static long _buffers;
    private static ulong _sumBytes;
    private static ulong _maxBytes;
    private static ulong _sumDeviceAddressOnlyBytes;
    private static ulong _maxDeviceAddressOnlyBytes;
    private static long _sumWindows;
    private static int _maxWindows;
    private static ulong _distinct;
    private static ulong _distinctBytes;
    private static long _buffersWithNone;
    private static readonly long[] PerWindow = new long[64];

    /// <summary>
    /// One command buffer is being committed: <paramref name="touched"/> are the windows its
    /// bindings resolved in, <paramref name="deviceAddressNamed"/> those a device-address
    /// encoder named for reach alone.
    /// </summary>
    public static void NoteCommit(MetalGuestMemory memory, ulong deviceAddressNamed)
    {
        if (!MemoryTelemetry.Enabled)
        {
            return;
        }

        var touched = memory.TouchedWindows;
        var named = touched | deviceAddressNamed;
        var bytes = memory.WindowBytes(named);
        var reachOnlyBytes = memory.WindowBytes(deviceAddressNamed & ~touched);
        var count = BitOperations.PopCount(named);
        lock (Gate)
        {
            _buffers++;
            _sumBytes += bytes;
            _maxBytes = Math.Max(_maxBytes, bytes);
            _sumDeviceAddressOnlyBytes += reachOnlyBytes;
            _maxDeviceAddressOnlyBytes = Math.Max(_maxDeviceAddressOnlyBytes, reachOnlyBytes);
            _sumWindows += count;
            _maxWindows = Math.Max(_maxWindows, count);
            if (count == 0)
            {
                _buffersWithNone++;
            }

            _distinct |= named;
            for (var mask = named; mask != 0; mask &= mask - 1)
            {
                PerWindow[BitOperations.TrailingZeroCount(mask)]++;
            }
        }
    }

    /// <summary>The windows named per command buffer over the interval, and resets it.</summary>
    public static string TakeWindowReport(MetalGuestMemory memory)
    {
        const double Mb = 1048576.0;
        var culture = CultureInfo.InvariantCulture;
        lock (Gate)
        {
            var buffers = (ulong)Math.Max(_buffers, 1L);
            _distinctBytes = memory.WindowBytes(_distinct);
            var perWindow = string.Join(',', Enumerable.Range(0, 64).Where(index => PerWindow[index] != 0)
                .Select(index => $"{index}:{PerWindow[index]}"));
            var text = string.Create(culture,
                $"command_buffers={_buffers} none_named={_buffersWithNone} " +
                $"named_mb_avg={_sumBytes / buffers / Mb:F0} named_mb_max={_maxBytes / Mb:F0} " +
                $"windows_avg={_sumWindows / (double)Math.Max(_buffers, 1L):F1} windows_max={_maxWindows} " +
                $"distinct_windows={BitOperations.PopCount(_distinct)} distinct_window_mb={_distinctBytes / Mb:F0} " +
                $"reach_only_mb_avg={_sumDeviceAddressOnlyBytes / buffers / Mb:F0} reach_only_mb_max={_maxDeviceAddressOnlyBytes / Mb:F0} " +
                $"window_mb={memory.WindowBytes(memory.AllWindows) / Mb:F0} named_by_window={{{perWindow}}}");
            _buffers = 0;
            _sumBytes = _maxBytes = _sumDeviceAddressOnlyBytes = _maxDeviceAddressOnlyBytes = 0;
            _sumWindows = 0;
            _maxWindows = 0;
            _distinct = 0;
            _buffersWithNone = 0;
            Array.Clear(PerWindow);
            return text;
        }
    }
}
