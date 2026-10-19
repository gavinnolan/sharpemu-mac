// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>
/// The switches of the packet-block reads: the command stream copies a run of command memory
/// once, under the host's checks, and serves its packets from the copy until the host's state or
/// its own writes move.
/// </summary>
public static class PacketBlockReads
{
    /// <summary>SHARPEMU_GPU_PACKET_BLOCK=0 reads every packet from the host, as before.</summary>
    public static readonly bool Enabled = PerfSwitches.Enabled("SHARPEMU_GPU_PACKET_BLOCK");

    /// <summary>
    /// SHARPEMU_GPU_PACKET_BLOCK_VERIFY=1 (or SHARPEMU_PERF_VERIFY=1): every packet served from a
    /// block is read again through the host and a difference is logged.
    /// </summary>
    public static readonly bool Verify = PerfSwitches.Verify("SHARPEMU_GPU_PACKET_BLOCK_VERIFY");

    private static long _verifyMismatches;

    /// <summary>Packets the verify mode found different from a direct read, since startup.</summary>
    public static long VerifyMismatches => Interlocked.Read(ref _verifyMismatches);

    internal static long NoteVerifyMismatch() => Interlocked.Increment(ref _verifyMismatches);
}
