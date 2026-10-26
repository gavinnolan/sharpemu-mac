// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SharpEmu.GpuRemote;

/// <summary>
/// Wait-on-address over a 32-bit word in the shared region: the one blocking primitive every
/// doorbell, ring and call slot of the transport ends in.
/// </summary>
/// <remarks>
/// <para>Darwin's <c>__ulock_wait2</c>/<c>__ulock_wake</c> with <c>UL_COMPARE_AND_WAIT_SHARED</c>
/// key a wait on the page's VM object and offset, so a waiter in one process is woken by a
/// waker in the other although each maps the region at its own address - and across the
/// architecture line too, since the x86-64 process under Rosetta makes the same system call.
/// It is the mechanism under libc++'s atomic wait and macOS 14.4's public
/// <c>os_sync_wait_on_address</c>. <c>ULF_NO_ERRNO</c> returns errors as negative values, so a
/// wait made from a signal handler leaves the interrupted code's <c>errno</c> alone.</para>
///
/// <para>Measured on an M5 Pro, x86-64 parent under Rosetta and arm64 child, blocking ping-pong
/// round trips (two wakes each): ulock 7.8 us, POSIX named semaphores 4.8 us, a pipe pair
/// 5.3 us, ulock with a 4000-iteration spin first 0.67 us, pure spinning 0.13 us. (Native arm64
/// on both sides: 5.6, 4.0, 4.4, 0.58, 0.17 us; the shared ulock key costs ~1.5 us a hop over
/// the private one.) Named semaphores block faster but have no timed wait on macOS, live in a
/// global namespace that a crash leaks, and need the variadic <c>sem_open</c>; Mach semaphores
/// are faster still but cannot be shared by name at all. The blocking path is the rare one -
/// every wait in the transport spins first - so ulock's timeouts and lack of kernel objects
/// win.</para>
///
/// <para>The fallback, when the symbols are missing or <c>SHARPEMU_GPU_DOORBELL=poll</c>, polls
/// the word with an exponential <c>nanosleep</c> backoff up to <see cref="PollCeilingNanoseconds"/>:
/// no kernel object, still async-signal-safe, but that much added latency. Wakers always call
/// <c>__ulock_wake</c> when it exists, so a polling side and a ulock side interoperate.</para>
///
/// <para>Everything here is async-signal-safe: atomics, and at most one system call through a
/// pointer resolved at start-up. The methods are <see cref="MethodImplOptions.AggressiveOptimization"/>
/// so they are compiled once, fully optimized, and never re-jitted by tiering while a signal
/// handler might be running them.</para>
/// </remarks>
internal static unsafe class RemoteWait
{
    public const string MechanismEnvironment = "SHARPEMU_GPU_DOORBELL";
    public const long PollCeilingNanoseconds = 200_000;

    private const uint UL_COMPARE_AND_WAIT_SHARED = 3;
    private const uint ULF_WAKE_ALL = 0x0000_0100;
    private const uint ULF_NO_ERRNO = 0x0100_0000;
    private const long PollFloorNanoseconds = 1_000;
    private const int SpinChecksPerClockRead = 16;

    /// <summary>True when waits block in the kernel; false when they poll.</summary>
    public static readonly bool UsesUlock =
        RemoteNative.UlockWait2 != null &&
        !string.Equals(Environment.GetEnvironmentVariable(MechanismEnvironment), "poll", StringComparison.Ordinal);

    public static string MechanismName => UsesUlock ? "ulock" : "poll";

    /// <summary>
    /// Blocks while <paramref name="word"/> still holds <paramref name="expected"/>. Returns false
    /// only when <paramref name="timeoutNanoseconds"/> elapsed (0 means do not block, negative
    /// means no limit); true means the word changed, a wake arrived, or the wait ended
    /// spuriously - callers always re-check their condition.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool Wait(uint* word, uint expected, long timeoutNanoseconds)
    {
        if (timeoutNanoseconds == 0)
        {
            return Volatile.Read(ref *word) != expected;
        }

        if (UsesUlock)
        {
            // A zero timeout means "forever" to the kernel.
            var timeout = timeoutNanoseconds < 0 ? 0UL : (ulong)timeoutNanoseconds;
            var result = RemoteNative.UlockWait2(UL_COMPARE_AND_WAIT_SHARED | ULF_NO_ERRNO, word, expected, timeout, 0);
            return result != -RemoteNative.ETIMEDOUT;
        }

        return Poll(word, expected, timeoutNanoseconds);
    }

    /// <summary>Wakes every waiter blocked on <paramref name="word"/>, in any process.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void WakeAll(uint* word)
    {
        // Called even when this side polls: the other side may be blocked in the kernel.
        // -ENOENT (no waiter) is the common, harmless result.
        if (RemoteNative.UlockWake != null)
        {
            _ = RemoteNative.UlockWake(UL_COMPARE_AND_WAIT_SHARED | ULF_WAKE_ALL | ULF_NO_ERRNO, word, 0);
        }
    }

    /// <summary>
    /// Wakes one waiter blocked on <paramref name="word"/>, in any process. For a word several
    /// threads of one process serve alike (the call tables' service bell): one request needs one
    /// thread, and waking the rest only makes each of them spin and go back to sleep.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void WakeOne(uint* word)
    {
        if (RemoteNative.UlockWake != null)
        {
            _ = RemoteNative.UlockWake(UL_COMPARE_AND_WAIT_SHARED | ULF_NO_ERRNO, word, 0);
        }
    }

    /// <summary>One spin-loop pause instruction (PAUSE on x86-64, translated by Rosetta; YIELD on arm64).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pause()
    {
        if (X86Base.IsSupported)
        {
            X86Base.Pause();
        }
        else if (ArmBase.IsSupported)
        {
            ArmBase.Yield();
        }
    }

    /// <summary>
    /// Spins until <paramref name="word"/> no longer holds <paramref name="expected"/> or
    /// <paramref name="spinNanoseconds"/> pass; returns whether it changed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool SpinWhileEqual(uint* word, uint expected, long spinNanoseconds)
    {
        if (spinNanoseconds <= 0)
        {
            return Volatile.Read(ref *word) != expected;
        }

        var deadline = RemoteNative.MonotonicNanoseconds() + spinNanoseconds;
        while (true)
        {
            for (var i = 0; i < SpinChecksPerClockRead; i++)
            {
                if (Volatile.Read(ref *word) != expected)
                {
                    return true;
                }

                Pause();
            }

            if (RemoteNative.MonotonicNanoseconds() >= deadline)
            {
                return false;
            }
        }
    }

    /// <summary>Deadline helper: the remaining time of a wait that started at <paramref name="start"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Remaining(long start, long timeoutNanoseconds)
    {
        if (timeoutNanoseconds < 0)
        {
            return -1;
        }

        var left = timeoutNanoseconds - (RemoteNative.MonotonicNanoseconds() - start);
        return left > 0 ? left : 0;
    }

    /// <summary>The polling fallback of <see cref="Wait"/>; internal for its tests.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Poll(uint* word, uint expected, long timeoutNanoseconds)
    {
        var start = RemoteNative.MonotonicNanoseconds();
        var sleep = PollFloorNanoseconds;
        while (Volatile.Read(ref *word) == expected)
        {
            var left = Remaining(start, timeoutNanoseconds);
            if (left == 0)
            {
                return false;
            }

            var interval = left > 0 && left < sleep ? left : sleep;
            RemoteNative.Timespec request;
            request.Seconds = (nint)(interval / 1_000_000_000);
            request.Nanoseconds = (nint)(interval % 1_000_000_000);
            if (RemoteNative.NanoSleep != null)
            {
                _ = RemoteNative.NanoSleep(&request, null);
            }
            else
            {
                Pause();
            }

            sleep = Math.Min(sleep * 2, PollCeilingNanoseconds);
        }

        return true;
    }
}
