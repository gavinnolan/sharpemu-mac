// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Loader;

/// <summary>
/// The host-side half of the generic spin-loop nap (see <see cref="GuestRedZonePatcher"/>'s spin
/// pass). Every patched guest <c>pause</c> jumps through one shared, position-independent native
/// probe this class emits. The probe keys a direct-mapped table on the guest stack pointer
/// (<c>rsp &gt;&gt; 26</c>, one bucket per 64&#160;MiB guest stack, computed without a memory read so it
/// can never fault), tracks how long the thread has spun without interruption, and once that
/// passes the idle threshold calls <see cref="SpinNap"/> (a real short host sleep). A gap between
/// two <c>pause</c>es longer than the reset window restarts the streak, so a lock backoff that
/// resolves quickly never sleeps. The probe saves and restores every register and the flags, so
/// it is transparent to the guest. The managed-call pattern mirrors the POSIX signal trampoline.
/// </summary>
internal static unsafe class SpinNapRuntime
{
    public const string EnvironmentVariable = "SHARPEMU_SPIN_NAP";
    public const string IdleEnvironmentVariable = "SHARPEMU_SPIN_NAP_IDLE_US";
    public const string NapEnvironmentVariable = "SHARPEMU_SPIN_NAP_US";
    public const string GapEnvironmentVariable = "SHARPEMU_SPIN_NAP_GAP_US";
    public const string AbEnvironmentVariable = "SHARPEMU_SPIN_NAP_AB_S";

    internal const uint DefaultIdleMicroseconds = 500;
    internal const uint DefaultNapMicroseconds = 250;
    internal const uint DefaultGapMicroseconds = 50;

    // 8192 buckets × 32 bytes = 256 KiB. A collision between two live stacks only costs a streak
    // reset (the key check fails), so the table never needs to be exact, just large enough that
    // collisions are rare. The bucket key is rsp >> StackKeyShift.
    private const int BucketCount = 8192;
    private const int BucketMask = BucketCount - 1;
    private const int BucketStride = 32; // key(8) epoch(8) last(8) pad(8)
    internal const int StackKeyShift = 26; // 64 MiB guest stacks

    private static readonly object Gate = new();
    private static byte[]? _probeBytes;
    private static byte* _table;
    private static uint _napMicroseconds;

    private static long _abPeriodTicks;
    private static long _abEpoch;

    internal static bool IsEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "0", StringComparison.Ordinal);

    internal static uint IdleMicroseconds => ReadMicroseconds(IdleEnvironmentVariable, DefaultIdleMicroseconds);

    internal static uint NapMicroseconds => ReadMicroseconds(NapEnvironmentVariable, DefaultNapMicroseconds);

    internal static uint GapMicroseconds => ReadMicroseconds(GapEnvironmentVariable, DefaultGapMicroseconds);

    /// <summary>Builds the shared probe's machine code once and returns it. The probe is
    /// position-independent (absolute imm64 operands, only self-relative branches), so the patcher
    /// copies it into each module's trampoline arena, which keeps it in <c>call rel32</c> reach of
    /// every site. <paramref name="tscFrequency"/> converts the microsecond thresholds to the host
    /// TSC ticks the probe's <c>rdtsc</c> measures. Returns an empty array only if allocation
    /// fails.</summary>
    internal static byte[] EnsureProbeBytes(ulong tscFrequency)
    {
        lock (Gate)
        {
            if (_probeBytes is not null)
            {
                return _probeBytes;
            }

            var table = (byte*)NativeMemory.AllocZeroed((nuint)(BucketCount * BucketStride));
            if (table == null)
            {
                return [];
            }

            _napMicroseconds = NapMicroseconds;
            _abPeriodTicks =
                int.TryParse(Environment.GetEnvironmentVariable(AbEnvironmentVariable), out var abSeconds) && abSeconds > 0
                    ? abSeconds * Stopwatch.Frequency
                    : 0;

            var thresholdTicks = TicksFor(IdleMicroseconds, tscFrequency);
            var gapTicks = TicksFor(GapMicroseconds, tscFrequency);
            _table = table;
            _probeBytes = BuildProbe((ulong)table, thresholdTicks, gapTicks);
            return _probeBytes;
        }
    }

    internal static long TicksFor(uint microseconds, ulong tscFrequency) =>
        (long)UInt128.Min((UInt128)microseconds * tscFrequency / 1_000_000, int.MaxValue);

    // The probe, assembled position-independently (absolute imm64 operands, only self-relative
    // internal branches). Entered by `call` from a site trampoline whose rsp already sits below
    // the guest red zone; it preserves every register and flag.
    private static byte[] BuildProbe(ulong tableAddress, long thresholdTicks, long gapTicks)
    {
        var a = new Assembler(64);
        var newStreak = a.CreateLabel();
        var sameStreak = a.CreateLabel();
        var done = a.CreateLabel();

        a.pushfq();
        a.push(rax);
        a.push(rcx);
        a.push(rdx);
        a.push(rbx);
        a.push(rbp);
        a.push(rsi);
        a.push(rdi);
        a.push(r8);
        a.push(r9);
        a.push(r10);
        a.push(r11);
        a.push(r12);
        a.push(r13);
        a.push(r14);
        a.push(r15);

        // key = rsp >> 26. rsp has shifted by a few KiB since guest code ran, far less than the
        // 64 MiB bucket, so the top bits are the guest thread's.
        a.mov(r11, rsp);
        a.shr(r11, StackKeyShift);

        // now = rdtsc (edx:eax) combined into r10.
        a.rdtsc();
        a.shl(rdx, 32);
        a.or(rdx, rax);
        a.mov(r10, rdx); // r10 = now

        // entry = table + (key & mask) * 32
        a.mov(rax, r11);
        a.and(rax, BucketMask);
        a.shl(rax, 5);
        a.mov(rbx, tableAddress);
        a.add(rbx, rax); // rbx = &entry

        a.mov(rax, __qword_ptr[rbx]); // entry.key
        a.cmp(rax, r11);
        a.jne(newStreak);

        // same thread: if now - last > gap, the streak broke; restart the epoch.
        a.mov(rcx, r10);
        a.sub(rcx, __qword_ptr[rbx + 16]); // now - last
        a.mov(rax, gapTicks);
        a.cmp(rcx, rax);
        a.jb(sameStreak);
        a.mov(__qword_ptr[rbx + 8], r10); // epoch = now

        a.Label(ref sameStreak);
        a.mov(__qword_ptr[rbx + 16], r10); // last = now
        a.mov(rcx, r10);
        a.sub(rcx, __qword_ptr[rbx + 8]); // now - epoch
        a.mov(rax, thresholdTicks);
        a.cmp(rcx, rax);
        a.jb(done);

        // Spun past the threshold: nap. Keep the epoch so it keeps napping while it stays idle.
        a.mov(rbp, rsp);
        a.and(rsp, -16);
        a.sub(rsp, 32); // Win64 shadow space; harmless on SysV
        a.mov(rax, (ulong)(nint)(delegate* unmanaged<void>)&SpinNap);
        a.call(rax);
        a.mov(rsp, rbp);
        a.jmp(done);

        a.Label(ref newStreak);
        a.mov(__qword_ptr[rbx], r11); // key
        a.mov(__qword_ptr[rbx + 8], r10); // epoch
        a.mov(__qword_ptr[rbx + 16], r10); // last

        a.Label(ref done);
        a.pop(r15);
        a.pop(r14);
        a.pop(r13);
        a.pop(r12);
        a.pop(r11);
        a.pop(r10);
        a.pop(r9);
        a.pop(r8);
        a.pop(rdi);
        a.pop(rsi);
        a.pop(rbp);
        a.pop(rbx);
        a.pop(rdx);
        a.pop(rcx);
        a.pop(rax);
        a.popfq();
        a.ret();

        using var stream = new MemoryStream();
        a.Assemble(new StreamCodeWriter(stream), 0);
        return stream.ToArray();
    }

    [UnmanagedCallersOnly]
    private static void SpinNap()
    {
        if (_abPeriodTicks != 0 && !AbNapPhase())
        {
            return;
        }

        Sleep(_napMicroseconds);
    }

    /// <summary>Blocks the calling thread for about <paramref name="microseconds"/>.</summary>
    internal static void Sleep(uint microseconds)
    {
        if (microseconds == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Sleep counts whole timer periods; with timeBeginPeriod(1) a sub-millisecond nap
            // rounds to about 1 ms, which an idle worker can afford.
            Thread.Sleep((int)Math.Max(1, (microseconds + 999) / 1000));
            return;
        }

        _ = HostUsleep(microseconds);
    }

    internal static bool IsNapPhase(long now, long epoch, long periodTicks) =>
        (now - epoch) / periodTicks % 2 == 0;

    // SHARPEMU_SPIN_NAP_AB_S=N (diagnostic): naps and pure spins alternate every N seconds from
    // the first nap, so one run carries interleaved A/B phases over the same scenes.
    private static bool AbNapPhase()
    {
        var now = Stopwatch.GetTimestamp();
        var epoch = Volatile.Read(ref _abEpoch);
        if (epoch == 0)
        {
            var prior = Interlocked.CompareExchange(ref _abEpoch, now, 0);
            epoch = prior != 0 ? prior : now;
            if (prior == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] spin nap A/B: epoch_ts={now} period_s={_abPeriodTicks / Stopwatch.Frequency} (even phases nap)");
            }
        }

        return IsNapPhase(now, epoch, _abPeriodTicks);
    }

    private static uint ReadMicroseconds(string variable, uint fallback) =>
        uint.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value is > 0 and <= 100_000
            ? value
            : fallback;

    [DllImport("libc", EntryPoint = "usleep")]
    private static extern int HostUsleep(uint microseconds);
}
