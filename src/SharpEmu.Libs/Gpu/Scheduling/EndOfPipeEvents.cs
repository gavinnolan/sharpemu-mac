// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Gpu.Scheduling;

// Delivers a GPU interrupt to every queue that registered the event id; one entry per trigger.
public sealed class EndOfPipeEvents : IEndOfPipeSink
{
    private static IEndOfPipeSink? _redirect;

    // The two-process split's GPU process has no event queues: its interrupts go to the guest
    // process through this sink instead. Null, the default, delivers them here.
    public static IEndOfPipeSink? Redirect
    {
        get => Volatile.Read(ref _redirect);
        set => Volatile.Write(ref _redirect, value);
    }

    public void TriggerInterrupt(int eventId, uint contextId)
    {
        SplitCrossingStats.Count(SplitCrossing.Interrupt);
        if (Redirect is { } redirect)
        {
            redirect.TriggerInterrupt(eventId, contextId);
            return;
        }

        Deliver(eventId, contextId);
    }

    // The guest process's half of a redirected interrupt.
    public static void Deliver(int eventId, uint contextId) =>
        _ = KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            contextId,
            unchecked((ulong)eventId));
}
