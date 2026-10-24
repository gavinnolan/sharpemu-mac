// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.GpuRemote.Tests;

/// <summary>The transport is Darwin-only (shm names, ulock, posix_spawn arch preference).</summary>
public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "The GPU process transport exists only on macOS.";
        }
    }
}

/// <summary>Needs the osx-x64 smoke tool and Rosetta, i.e. an Apple-silicon Mac.</summary>
public sealed class CrossArchitectureFactAttribute : FactAttribute
{
    public CrossArchitectureFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.OSArchitecture != Architecture.Arm64)
        {
            Skip = "Crossing the architecture line needs an Apple-silicon Mac.";
        }
        else if (!File.Exists(SmokeTool.Path))
        {
            Skip = $"The osx-x64 smoke tool is not built at {SmokeTool.Path}.";
        }
        else if (!SmokeTool.RosettaAvailable)
        {
            Skip = "Rosetta is not installed.";
        }
    }
}

internal static class SmokeTool
{
    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "osx-x64", "SharpEmu.Tools.GpuProcessSmoke");

    public static bool RosettaAvailable { get; } = ProbeRosetta();

    private static bool ProbeRosetta()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("/usr/bin/arch", "-x86_64 /usr/bin/true")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            return probe is not null && probe.WaitForExit(10_000) && probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Zeroed, cache-line-aligned native memory standing in for the shared region in in-process
/// tests, plus a peer-state word.
/// </summary>
internal sealed unsafe class NativeBlock : IDisposable
{
    public NativeBlock(int size)
    {
        Size = size;
        Pointer = (byte*)NativeMemory.AlignedAlloc((nuint)size, 128);
        NativeMemory.Clear(Pointer, (nuint)size);
        PeerState = (uint*)NativeMemory.AlignedAlloc(128, 128);
        *PeerState = GpuRemoteChannel.PeerRunning;
    }

    public byte* Pointer { get; }

    public int Size { get; }

    public uint* PeerState { get; }

    public void MarkPeerExited() => Volatile.Write(ref *PeerState, GpuRemoteChannel.PeerExited);

    public void Dispose()
    {
        NativeMemory.AlignedFree(Pointer);
        NativeMemory.AlignedFree(PeerState);
    }
}
