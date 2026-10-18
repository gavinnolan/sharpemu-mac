// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
/// passes the idle threshold naps (a real short host sleep). A gap between two <c>pause</c>es
/// longer than the reset window restarts the streak, so a lock backoff that resolves quickly never
/// sleeps. The probe saves and restores every general register and the flags, and around a nap the
/// x87/SSE state, so it is transparent to the guest: it runs in the middle of guest code, not at a
/// call boundary. A nap calls the host's sleep function directly and never managed code, because
/// the probe runs on the guest stack and managed code only runs on the host stack (the import
/// thunks and the fault bridge switch to it first).
/// </summary>
internal static unsafe class SpinNapRuntime
{
    public const string EnvironmentVariable = "SHARPEMU_SPIN_NAP";
    public const string IdleEnvironmentVariable = "SHARPEMU_SPIN_NAP_IDLE_US";
    public const string NapEnvironmentVariable = "SHARPEMU_SPIN_NAP_US";
    public const string GapEnvironmentVariable = "SHARPEMU_SPIN_NAP_GAP_US";
    public const string AbEnvironmentVariable = "SHARPEMU_SPIN_NAP_AB_S";
    public const string LockedEnvironmentVariable = "SHARPEMU_SPIN_NAP_LOCKED";
    public const string LockedSitesEnvironmentVariable = "SHARPEMU_SPIN_NAP_LOCKED_SITES";
    public const string LockedDensityEnvironmentVariable = "SHARPEMU_SPIN_NAP_LOCKED_DENSITY_US";

    internal const uint DefaultIdleMicroseconds = 500;
    internal const uint DefaultNapMicroseconds = 250;
    internal const uint DefaultGapMicroseconds = 50;
    internal const uint DefaultLockedDensityMicroseconds = 5;
    internal const int DefaultLockedSites = 4;
    internal const int MaxLockedSites = 8;

    // Locked-op probe table: 1024 buckets x 256 bytes. entry: key(0) start(8) last(16) hits(24)
    // other(32) gen(40) | sites[8](64) | siteHits[8](128). The HLE generation array is one qword
    // per bucket, bumped by the import dispatcher on the calling guest thread's stack bucket.
    private const int LockedBucketCount = 1024;
    internal const int LockedBucketMask = LockedBucketCount - 1;
    private const int LockedBucketStride = 256;

    // 8192 buckets × 32 bytes = 256 KiB. A collision between two live stacks only costs a streak
    // reset (the key check fails), so the table never needs to be exact, just large enough that
    // collisions are rare. The bucket key is rsp >> StackKeyShift.
    private const int BucketCount = 8192;
    private const int BucketMask = BucketCount - 1;
    private const int BucketStride = 32; // key(8) epoch(8) last(8) pad(8)
    internal const int StackKeyShift = 26; // 64 MiB guest stacks

    // Native state the probes share (qword slots): the A/B epoch in TSC ticks (0 until the first
    // nap) and the nap counters the reporter logs.
    private const int ControlAbEpoch = 0;
    private const int ControlLockedNaps = 1;
    private const int ControlSlots = 2;

    // fxsave64 image of the x87/SSE state: 512 bytes, 16-byte aligned.
    private const int FxSaveBytes = 512;

    private static readonly object Gate = new();
    private static byte[]? _probeBytes;
    private static byte* _table;

    private static byte[]? _lockedProbeBytes;
    private static byte* _lockedTable;
    private static long* _lockedGenerations;

    private static NapTarget? _napTarget;
    private static long* _control;
    private static Timer? _reporter;
    private static bool _abEpochReported;
    private static long _reportedLockedNaps;

    /// <summary>What a nap calls and with which argument, plus the A/B period in TSC ticks (0 = off).
    /// <paramref name="Control"/> is the shared native state (see ControlAbEpoch).</summary>
    private sealed record NapTarget(ulong SleepAddress, uint SleepArgument, int AbSeconds, long AbPeriodTicks, ulong Control);

    internal static bool IsEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "0", StringComparison.Ordinal);

    /// <summary>Opt-in (<c>SHARPEMU_SPIN_NAP_LOCKED=1</c>): also instrument short backward loops
    /// that poll with <c>xchg mem</c> / <c>lock</c>-prefixed read-modify-write ops.</summary>
    internal static bool LockedEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(LockedEnvironmentVariable), "1", StringComparison.Ordinal);

    internal static int LockedSiteLimit =>
        int.TryParse(Environment.GetEnvironmentVariable(LockedSitesEnvironmentVariable), out var value) &&
        value is >= 1 and <= MaxLockedSites
            ? value
            : DefaultLockedSites;

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
            if (table == null || EnsureNapTargetLocked(tscFrequency) is not { } target)
            {
                return [];
            }

            var thresholdTicks = TicksFor(IdleMicroseconds, tscFrequency);
            var gapTicks = TicksFor(GapMicroseconds, tscFrequency);
            _table = table;
            _probeBytes = BuildProbe((ulong)table, thresholdTicks, gapTicks, target);
            return _probeBytes;
        }
    }

    /// <summary>Builds the locked-op probe (see <see cref="BuildLockedProbe"/>) once. Same contract as
    /// <see cref="EnsureProbeBytes"/>. It naps a thread only when the thread is demonstrably polling:
    /// over the last idle-threshold window every inter-hit gap was under the gap threshold, the mean
    /// gap was under the density threshold, at least 90% of hits came from the first
    /// <see cref="LockedSiteLimit"/> distinct sites, and the thread made no HLE call in the window.</summary>
    internal static byte[] EnsureLockedProbeBytes(ulong tscFrequency)
    {
        lock (Gate)
        {
            if (_lockedProbeBytes is not null)
            {
                return _lockedProbeBytes;
            }

            var table = (byte*)NativeMemory.AllocZeroed((nuint)(LockedBucketCount * LockedBucketStride));
            var generations = (long*)NativeMemory.AllocZeroed((nuint)(LockedBucketCount * sizeof(long)));
            if (table == null || generations == null || EnsureNapTargetLocked(tscFrequency) is not { } target)
            {
                return [];
            }

            _lockedTable = table;
            _lockedGenerations = generations;
            _lockedProbeBytes = BuildLockedProbe(
                (ulong)table,
                (ulong)generations,
                TicksFor(IdleMicroseconds, tscFrequency),
                TicksFor(GapMicroseconds, tscFrequency),
                TicksFor(ReadMicroseconds(LockedDensityEnvironmentVariable, DefaultLockedDensityMicroseconds), tscFrequency),
                LockedSiteLimit,
                target);
            return _lockedProbeBytes;
        }
    }

    // Resolves the host sleep a nap calls, allocates the shared native state and, when there is
    // something to report (A/B phases or locked-op naps), starts the reporter. Caller holds Gate.
    private static NapTarget? EnsureNapTargetLocked(ulong tscFrequency)
    {
        if (_napTarget is not null)
        {
            return _napTarget;
        }

        var control = (long*)NativeMemory.AllocZeroed((nuint)(ControlSlots * sizeof(long)));
        if (control == null || !TryResolveHostSleep(NapMicroseconds, out var sleepAddress, out var sleepArgument))
        {
            return null;
        }

        var abSeconds =
            int.TryParse(Environment.GetEnvironmentVariable(AbEnvironmentVariable), out var seconds) && seconds > 0 ? seconds : 0;
        var abPeriodTicks = (long)UInt128.Min((UInt128)(uint)abSeconds * tscFrequency, long.MaxValue);
        _control = control;
        _napTarget = new NapTarget(sleepAddress, sleepArgument, abSeconds, abPeriodTicks, (ulong)control);
        if (abPeriodTicks != 0 || LockedEnabled)
        {
            _reporter = new Timer(static _ => Report(), null, 100, 100);
        }

        return _napTarget;
    }

    // usleep(us) on POSIX; Sleep(ms) on Windows, where Sleep counts whole timer periods (with
    // timeBeginPeriod(1) a sub-millisecond nap rounds to about 1 ms, which an idle worker can afford).
    private static bool TryResolveHostSleep(uint microseconds, out ulong address, out uint argument)
    {
        address = 0;
        argument = microseconds;
        nint library;
        nint export;
        if (OperatingSystem.IsWindows())
        {
            argument = Math.Max(1, (microseconds + 999) / 1000);
            if (!NativeLibrary.TryLoad("kernel32.dll", out library) ||
                !NativeLibrary.TryGetExport(library, "Sleep", out export))
            {
                return false;
            }
        }
        else if (!NativeLibrary.TryLoad("libc", typeof(SpinNapRuntime).Assembly, null, out library) ||
                 !NativeLibrary.TryGetExport(library, "usleep", out export))
        {
            return false;
        }

        address = (ulong)export;
        return true;
    }

    // The probes never call managed code, so the A/B epoch and the nap count are logged from here.
    private static void Report()
    {
        var target = _napTarget;
        var control = _control;
        if (target is null || control == null)
        {
            return;
        }

        var epoch = Volatile.Read(ref control[ControlAbEpoch]);
        if (target.AbPeriodTicks != 0 && epoch != 0 && !_abEpochReported)
        {
            _abEpochReported = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] spin nap A/B: epoch_ts={epoch} period_s={target.AbSeconds} (even phases nap)");
        }

        var naps = Volatile.Read(ref control[ControlLockedNaps]);
        if (naps != 0 && (_reportedLockedNaps == 0 || naps / 100_000 != _reportedLockedNaps / 100_000))
        {
            _reportedLockedNaps = naps;
            Console.Error.WriteLine($"[SPIN-NAP] locked-op naps={naps}");
        }
    }

    /// <summary>Called by the HLE import dispatcher with an address on the calling guest thread's
    /// stack: any HLE entry ends that thread's polling streak in the locked-op probe. A no-op
    /// until the locked probe exists.</summary>
    internal static void NoteHleEntry(nint guestStackAddress)
    {
        var generations = _lockedGenerations;
        if (generations != null)
        {
            generations[(int)(((ulong)guestStackAddress >> StackKeyShift) & LockedBucketMask)]++;
        }
    }

    // Test hook: a locked-op probe over a fresh table whose thresholds are zero, so it naps on
    // every hit after the first.
    internal static byte[] BuildNappingLockedProbeForTest(ulong tscFrequency)
    {
        var table = (byte*)NativeMemory.AllocZeroed((nuint)(LockedBucketCount * LockedBucketStride));
        var generations = (long*)NativeMemory.AllocZeroed((nuint)(LockedBucketCount * sizeof(long)));
        lock (Gate)
        {
            var target = EnsureNapTargetLocked(tscFrequency) ?? throw new InvalidOperationException("No host sleep to nap with.");
            return BuildLockedProbe((ulong)table, (ulong)generations, 0, int.MaxValue, int.MaxValue, MaxLockedSites, target);
        }
    }

    /// <summary>Naps the locked-op probes have taken so far, across modules.</summary>
    internal static long LockedNapCount => _control == null ? 0 : Volatile.Read(ref _control[ControlLockedNaps]);

    internal static long TicksFor(uint microseconds, ulong tscFrequency) =>
        (long)UInt128.Min((UInt128)microseconds * tscFrequency / 1_000_000, int.MaxValue);

    // The probe, assembled position-independently (absolute imm64 operands, only self-relative
    // internal branches). Entered by `call` from a site trampoline whose rsp already sits below
    // the guest red zone; it preserves every register and flag.
    private static byte[] BuildProbe(ulong tableAddress, long thresholdTicks, long gapTicks, NapTarget target)
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
        EmitNap(a, target, napCounter: -1, skip: done);
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

    // The locked-op probe, assembled position-independently like BuildProbe. Per-thread window
    // (keyed rsp >> 26): a hit restarts the window when the thread changed, the gap since the
    // previous hit reached gapTicks, or the thread's HLE generation moved. Otherwise it counts the
    // hit against the first `siteLimit` distinct call-site return addresses (the rest count as
    // "other"). Once the window is thresholdTicks old: nap if hits are dense (hits * densityTicks
    // >= elapsed) and concentrated (other * 10 <= hits); either way start a fresh window.
    private static byte[] BuildLockedProbe(
        ulong tableAddress, ulong generationsAddress, long thresholdTicks, long gapTicks, long densityTicks, int siteLimit,
        NapTarget target)
    {
        var a = new Assembler(64);
        var reset = a.CreateLabel();
        var restart = a.CreateLabel();
        var clear = a.CreateLabel();
        var scan = a.CreateLabel();
        var claim = a.CreateLabel();
        var found = a.CreateLabel();
        var other = a.CreateLabel();
        var evaluate = a.CreateLabel();
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

        a.mov(r11, rsp);
        a.shr(r11, StackKeyShift); // r11 = key
        a.rdtsc();
        a.shl(rdx, 32);
        a.or(rdx, rax);
        a.mov(r10, rdx); // r10 = now

        a.mov(rax, r11);
        a.and(rax, LockedBucketMask);
        a.mov(rcx, generationsAddress);
        a.mov(r9, __qword_ptr[rcx + rax * 8]); // r9 = this thread's HLE generation
        a.shl(rax, 8);
        a.mov(rbx, tableAddress);
        a.add(rbx, rax); // rbx = &entry
        a.mov(r8, __qword_ptr[rsp + 128]); // r8 = site (return address into the site trampoline)

        a.cmp(__qword_ptr[rbx], r11);
        a.jne(reset);
        a.mov(rax, r10);
        a.sub(rax, __qword_ptr[rbx + 16]); // gap since the previous hit
        a.mov(__qword_ptr[rbx + 16], r10);
        a.mov(rcx, gapTicks);
        a.cmp(rax, rcx);
        a.jae(reset);
        a.cmp(r9, __qword_ptr[rbx + 40]);
        a.jne(reset);

        a.inc(__qword_ptr[rbx + 24]); // hits
        a.xor(ecx, ecx);
        a.Label(ref scan);
        a.cmp(rcx, siteLimit);
        a.jae(other);
        a.mov(rax, __qword_ptr[rbx + rcx * 8 + 64]);
        a.cmp(rax, r8);
        a.je(found);
        a.test(rax, rax);
        a.jz(claim);
        a.inc(rcx);
        a.jmp(scan);
        a.Label(ref claim);
        a.mov(__qword_ptr[rbx + rcx * 8 + 64], r8);
        a.Label(ref found);
        a.inc(__qword_ptr[rbx + rcx * 8 + 128]);
        a.jmp(evaluate);
        a.Label(ref other);
        a.inc(__qword_ptr[rbx + 32]);

        a.Label(ref evaluate);
        a.mov(rax, r10);
        a.sub(rax, __qword_ptr[rbx + 8]); // elapsed
        a.mov(rcx, thresholdTicks);
        a.cmp(rax, rcx);
        a.jb(done);
        a.mov(rcx, __qword_ptr[rbx + 24]);
        a.mov(rdx, densityTicks);
        a.imul(rcx, rdx);
        a.cmp(rcx, rax); // hits * density < elapsed: too sparse to be a poll
        a.jb(restart);
        a.mov(rcx, __qword_ptr[rbx + 32]);
        a.lea(rcx, __[rcx + rcx * 4]);
        a.add(rcx, rcx); // other * 10
        a.cmp(rcx, __qword_ptr[rbx + 24]);
        a.ja(restart); // under 90% of hits from the tracked sites

        EmitNap(a, target, ControlLockedNaps, skip: restart);

        a.Label(ref restart);
        a.mov(__qword_ptr[rbx + 8], r10);
        a.jmp(clear);

        a.Label(ref reset);
        a.mov(__qword_ptr[rbx], r11);
        a.mov(__qword_ptr[rbx + 8], r10);
        a.mov(__qword_ptr[rbx + 16], r10);
        a.mov(__qword_ptr[rbx + 40], r9);

        a.Label(ref clear);
        a.xor(eax, eax);
        a.mov(__qword_ptr[rbx + 24], rax);
        a.mov(__qword_ptr[rbx + 32], rax);
        for (var slot = 0; slot < MaxLockedSites; slot++)
        {
            a.mov(__qword_ptr[rbx + 64 + slot * 8], rax);
            a.mov(__qword_ptr[rbx + 128 + slot * 8], rax);
        }

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

    // A nap, emitted inline in a probe. On entry r10 = now (TSC) and the probe has already saved
    // every general register; rbx and r10 survive it. SHARPEMU_SPIN_NAP_AB_S=N (diagnostic): the
    // first nap anywhere sets the shared epoch, and naps are skipped in odd N-second periods from
    // it, so one run carries interleaved nap/spin phases over the same scenes. The host sleep is a
    // plain C call: x87/SSE state saved with fxsave64 (the guest is mid-function, so no vector
    // register is the callee's to clobber), DF clear, stack 16-byte aligned. With napCounter >= 0
    // the nap is counted in that control slot. A skipped nap jumps to `skip`; a taken one falls
    // through.
    private static void EmitNap(Assembler a, NapTarget target, int napCounter, Label skip)
    {
        a.mov(rsi, target.Control);
        if (target.AbPeriodTicks != 0)
        {
            var haveEpoch = a.CreateLabel();
            var napPhase = a.CreateLabel();
            a.mov(rdx, __qword_ptr[rsi + ControlAbEpoch * sizeof(long)]);
            a.test(rdx, rdx);
            a.jnz(haveEpoch);
            a.xor(eax, eax);
            a.@lock.cmpxchg(__qword_ptr[rsi + ControlAbEpoch * sizeof(long)], r10); // first nap: epoch = now
            a.mov(rdx, r10);
            a.test(rax, rax);
            a.cmovne(rdx, rax); // another thread set it first
            a.Label(ref haveEpoch);
            a.mov(rax, r10);
            a.sub(rax, rdx);
            a.jb(napPhase); // this core's TSC is a hair behind the epoch: still period 0
            a.xor(edx, edx);
            a.mov(rcx, (ulong)target.AbPeriodTicks);
            a.div(rcx);
            a.test(al, 1);
            a.jnz(skip); // odd period: spin
            a.Label(ref napPhase);
        }

        if (napCounter >= 0)
        {
            a.@lock.inc(__qword_ptr[rsi + napCounter * sizeof(long)]);
        }

        a.mov(r13, r10);
        a.mov(rbp, rsp);
        a.and(rsp, -16);
        a.sub(rsp, FxSaveBytes);
        a.fxsave64(__[rsp]);
        a.mov(r12, rsp);
        a.sub(rsp, 32); // Win64 shadow space; harmless on SysV
        a.cld();
        a.mov(edi, target.SleepArgument); // SysV first argument
        a.mov(ecx, target.SleepArgument); // Win64 first argument
        a.mov(rax, target.SleepAddress);
        a.call(rax);
        a.fxrstor64(__[r12]);
        a.mov(rsp, rbp);
        a.mov(r10, r13);
    }

    private static uint ReadMicroseconds(string variable, uint fallback) =>
        uint.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value is > 0 and <= 100_000
            ? value
            : fallback;
}
