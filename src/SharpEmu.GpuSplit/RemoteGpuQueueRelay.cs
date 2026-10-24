// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.GpuRemote;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.GpuSplit;

/// <summary>
/// The guest process's <see cref="IGpuQueueRelay"/>: work that runs on the GPU worker in one
/// process runs here, on the calling thread, while the GPU process's worker is parked
/// (<see cref="GpuRemoteSyncOpcode.BracketBegin"/> / <see cref="GpuRemoteSyncOpcode.BracketEnd"/>).
/// </summary>
/// <remarks>
/// Inside the work the calling thread counts as the GPU queue thread
/// (<see cref="IsGpuQueueThread"/>), as the worker does in one process, so what the work calls
/// back into (an unregistration inside a mapping change) runs at once instead of asking for a
/// second bracket; those calls carry the bracket's id, and the GPU process runs them on its parked
/// worker. The end carries the view log as the change left it: the GPU process's replica syncs
/// that far before the worker goes on. A bracket the GPU process cannot begin (it is gone) is a
/// rejected relay, which makes the memory manager wait for the detach that follows.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class RemoteGpuQueueRelay(SplitGpuSession session) : IGpuQueueRelay
{
    [ThreadStatic]
    private static ulong t_bracket;

    /// <summary>The bracket the calling thread runs inside, or 0.</summary>
    internal static ulong CurrentBracket => t_bracket;

    public bool IsGpuQueueThread => t_bracket != 0;

    public void Post(Action work) => RunOnGpuQueue(work);

    public void RunOnGpuQueue(Action work)
    {
        if (!TryRunOnGpuQueue(work))
        {
            throw new GpuProcessException("The GPU process is gone; the work for its GPU worker cannot run.");
        }
    }

    public bool TryRunOnGpuQueue(Action work) => TryRun(GpuSplitBracket.MappingChange, work);

    public bool TryRunAfterPendingWork(Action work) => TryRun(GpuSplitBracket.MappingChange, work);

    internal bool TryRun(GpuSplitBracket kind, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (t_bracket != 0)
        {
            work();
            return true;
        }

        if (!session.TryBeginBracket(kind, out var bracket))
        {
            return false;
        }

        t_bracket = bracket;
        try
        {
            work();
        }
        finally
        {
            t_bracket = 0;
            session.EndBracket(bracket);
        }

        return true;
    }
}
