// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.PerfAudit;

/// <summary>Primitive costs under the host the emulator runs on (Rosetta x64 on Apple silicon): lock, Interlocked, timestamp.</summary>
internal static class MiscBenches
{
    public static void Run()
    {
        Console.WriteLine("--- 9. Primitive costs ---");
        var thin = new object();
        var inflated = new object();
        Monitor.Enter(inflated); Monitor.Wait(inflated, 1); Monitor.Exit(inflated); // Wait inflates the lock into a sync block
        _ = inflated.GetHashCode();
        long counter = 0;
        var perDraw = 14.0 * 6331; // ~ lock acquisitions per draw is unknown; this is the 6331 x 14 sizing used for the frame column
        Harness.Measure("9a", "uncontended lock(obj) { } (fresh object, thin lock)", 1, perDraw, _ => { lock (thin) { counter++; } }, unit: "lock");
        Harness.Measure("9b", "uncontended lock(obj) { } on an object whose lock was inflated (Monitor.Wait used once)", 1, perDraw, _ => { lock (inflated) { counter++; } }, unit: "lock");
        Harness.Measure("9c", "Interlocked.Increment", 1, perDraw, _ => Interlocked.Increment(ref counter), unit: "op");
        Harness.Measure("9d", "Stopwatch.GetTimestamp()", 1, perDraw, _ => counter += Stopwatch.GetTimestamp(), unit: "call");
        Harness.Measure("9e", "Environment.CurrentManagedThreadId", 1, perDraw, _ => counter += Environment.CurrentManagedThreadId, unit: "call");
        GC.KeepAlive(counter);
    }
}
