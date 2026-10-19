// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Threading;

namespace SharpEmu.Libs.Kernel;

// SyncOnAddress waits only if the guest value equals the expected value.
// Wake selects a fixed number of registered waiters at the same address.
// _umtx_op implements the FreeBSD 11 umtx operations (sys/kern/kern_umtx.c) on the
// shared address-keyed wait table; see UmtxOp.
public static class KernelSyncOnAddressCompatExports
{
    // sys/sys/umtx.h
    private const int UmtxOpWait = 2;
    private const int UmtxOpWake = 3;
    private const int UmtxOpMutexTrylock = 4;
    private const int UmtxOpMutexLock = 5;
    private const int UmtxOpMutexUnlock = 6;
    private const int UmtxOpSetCeiling = 7;
    private const int UmtxOpCvWait = 8;
    private const int UmtxOpCvSignal = 9;
    private const int UmtxOpCvBroadcast = 10;
    private const int UmtxOpWaitUint = 11;
    private const int UmtxOpRwRdlock = 12;
    private const int UmtxOpRwWrlock = 13;
    private const int UmtxOpRwUnlock = 14;
    private const int UmtxOpWaitUintPrivate = 15;
    private const int UmtxOpWakePrivate = 16;
    private const int UmtxOpMutexWait = 17;
    private const int UmtxOpMutexWake = 18;
    private const int UmtxOpSemWait = 19;
    private const int UmtxOpSemWake = 20;
    private const int UmtxOpNwakePrivate = 21;
    private const int UmtxOpMutexWake2 = 22;
    private const int UmtxOpSem2Wait = 23;
    private const int UmtxOpSem2Wake = 24;

    private const uint UmutexUnowned = 0;
    private const uint UmutexContested = 0x80000000;
    private const uint UrwlockPreferReader = 0x02;
    private const uint UrwlockWriteOwner = 0x80000000;
    private const uint UrwlockWriteWaiters = 0x40000000;
    private const uint UrwlockReadWaiters = 0x20000000;
    private const uint UrwlockMaxReaders = 0x1fffffff;
    private const uint UsemHasWaiters = 0x80000000;
    private const uint UmtxAbstime = 0x01;
    private const uint CvwaitAbstime = 0x02;
    private const uint CvwaitClockid = 0x04;
    private const ulong TimespecSize = 16;

    private const int KindValue = 0;
    private const int KindMutexLock = 1;
    private const int KindMutexWait = 2;
    private const int KindReader = 3;
    private const int KindWriter = 4;
    private const int KindCond = 5;
    private const int KindSem = 6;

    private static AddressWaitTable Table => AddressWaitTable.Shared;

    /// <summary>A value-compare wait (SyncOnAddress, UMTX_OP_WAIT*).</summary>
    private sealed class ValueWaiter : AddressWaiter
    {
        private readonly CpuContext _context;
        private readonly ulong _expected;
        private readonly bool _is64Bit;
        private readonly long _registeredTicks;
        private readonly int _registeredManagedThread;

        public ValueWaiter(CpuContext context, ulong address, ulong expected, bool is64Bit)
            : base(address, KindValue)
        {
            _context = context;
            _expected = expected;
            _is64Bit = is64Bit;
            _registeredTicks = KernelSyncOnAddressProfile.Enabled ? Stopwatch.GetTimestamp() : 0L;
            _registeredManagedThread = KernelSyncOnAddressProfile.Enabled ? Environment.CurrentManagedThreadId : 0;
        }

        // A guest signal handler may have changed the word; end the wait as a kernel
        // sleep interrupted by a signal would, so the guest re-checks.
        protected override void AfterHostInterrupt(CpuContext ctx)
        {
            if (!TryReadValue(_context, Address, _is64Bit, out var value) || value != _expected)
            {
                _ = CompleteAndWake(GuestWaitStatus.Interrupted);
            }
        }

        // Explicit wake → 0. Expiry → 0 when the value already changed, else ETIMEDOUT.
        public int ResultErrno()
        {
            var explicitWake = Status is GuestWaitStatus.Satisfied or GuestWaitStatus.Interrupted;
            if (explicitWake)
            {
                RecordResume(explicitWake: true, valueChanged: false, timedOut: false, faulted: false);
                return 0;
            }

            if (!TryReadValue(_context, Address, _is64Bit, out var value))
            {
                RecordResume(explicitWake: false, valueChanged: false, timedOut: false, faulted: true);
                return GuestErrno.EFAULT;
            }

            var valueChanged = value != _expected;
            RecordResume(explicitWake: false, valueChanged, timedOut: !valueChanged, faulted: false);
            return valueChanged ? 0 : GuestErrno.ETIMEDOUT;
        }

        private void RecordResume(bool explicitWake, bool valueChanged, bool timedOut, bool faulted)
        {
            if (!KernelSyncOnAddressProfile.Enabled)
            {
                return;
            }

            KernelSyncOnAddressProfile.RecordResume(
                Address,
                _registeredManagedThread,
                Stopwatch.GetTimestamp() - _registeredTicks,
                explicitWake,
                valueChanged,
                timedOut,
                faulted);
        }
    }

    /// <summary>A umutex lock wait: completed only once ownership was handed to it.</summary>
    private sealed class MutexWaiter : AddressWaiter
    {
        public MutexWaiter(ulong address, int kind, uint threadId)
            : base(address, kind)
        {
            ThreadId = threadId;
        }

        public uint ThreadId { get; }
    }

    /// <summary>An urwlock wait: completed once the lock was granted to it.</summary>
    private sealed class RwlockWaiter : AddressWaiter
    {
        private readonly CpuContext _context;

        public RwlockWaiter(CpuContext context, ulong address, int kind)
            : base(address, kind)
        {
            _context = context;
        }

        // A waiter that leaves unsatisfied takes its blocked count and, if it was the
        // last of its class, the waiter bit with it (kern_umtx.c do_rw_rdlock/wrlock).
        protected internal override void OnDeadlineLocked(WaitQueue queue, ref WakeBatch batch)
        {
            if (!TryCompleteLocked(GuestWaitStatus.TimedOut))
            {
                return;
            }

            batch.Add(this);
            LeaveRwlockLocked(_context, queue, Address, Kind, ref batch);
        }
    }

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx) => Wait(ctx, is64Bit: false);

    [SysAbiExport(
        Nid = "B2n8aDorSH4",
        ExportName = "sceKernelSyncOnAddressWait32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait32(CpuContext ctx) => Wait(ctx, is64Bit: false);

    [SysAbiExport(
        Nid = "PZQhiiLXRFs",
        ExportName = "sceKernelSyncOnAddressWait64",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait64(CpuContext ctx) => Wait(ctx, is64Bit: true);

    /// <summary>
    /// <c>_umtx_op(obj, op, val, uaddr1, uaddr2)</c>. Returns 0, or -1 with errno.
    /// Timeouts follow kern_umtx.c: for the WAIT/LOCK/SEM ops <c>uaddr1</c> is the size of
    /// the object at <c>uaddr2</c>, a plain relative timespec up to 16 bytes, otherwise a
    /// <c>struct _umtx_time</c> {timeout, flags (UMTX_ABSTIME), clockid}. Priority-inherit
    /// and priority-protect umutexes are handled as normal ones (no boosting on the host);
    /// UMTX_OP_SET_CEILING reports EINVAL.
    /// </summary>
    [SysAbiExport(
        Nid = "04AjkP0jO9U",
        ExportName = "_umtx_op",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int UmtxOp(CpuContext ctx)
    {
        var obj = ctx[CpuRegister.Rdi];
        var operation = unchecked((int)ctx[CpuRegister.Rsi]);
        var value = ctx[CpuRegister.Rdx];
        var uaddr1 = ctx[CpuRegister.Rcx];
        var uaddr2 = ctx[CpuRegister.R8];

        return operation switch
        {
            UmtxOpWait => UmtxValueWait(ctx, obj, value, is64Bit: true, uaddr1, uaddr2, "_umtx_op.wait"),
            UmtxOpWaitUint or UmtxOpWaitUintPrivate =>
                UmtxValueWait(ctx, obj, unchecked((uint)value), is64Bit: false, uaddr1, uaddr2, "_umtx_op.wait_uint"),
            UmtxOpWake or UmtxOpWakePrivate => UmtxWake(ctx, obj, value),
            UmtxOpNwakePrivate => UmtxNwake(ctx, obj, value),
            UmtxOpMutexTrylock => UmutexLock(ctx, obj, trylock: true, waitOnly: false, uaddr1, uaddr2),
            UmtxOpMutexLock => UmutexLock(ctx, obj, trylock: false, waitOnly: false, uaddr1, uaddr2),
            UmtxOpMutexWait => UmutexLock(ctx, obj, trylock: false, waitOnly: true, uaddr1, uaddr2),
            UmtxOpMutexUnlock => Libc(ctx, UmutexUnlock(ctx, obj)),
            UmtxOpMutexWake or UmtxOpMutexWake2 => Libc(ctx, UmutexWake(ctx, obj)),
            UmtxOpSetCeiling => Libc(ctx, GuestErrno.EINVAL),
            UmtxOpCvWait => UcondWait(ctx, obj, unchecked((uint)value), uaddr1, uaddr2),
            UmtxOpCvSignal => Libc(ctx, UcondWake(ctx, obj, broadcast: false)),
            UmtxOpCvBroadcast => Libc(ctx, UcondWake(ctx, obj, broadcast: true)),
            UmtxOpRwRdlock => UrwlockLock(ctx, obj, writer: false, unchecked((uint)value), uaddr1, uaddr2),
            UmtxOpRwWrlock => UrwlockLock(ctx, obj, writer: true, unchecked((uint)value), uaddr1, uaddr2),
            UmtxOpRwUnlock => Libc(ctx, UrwlockUnlock(ctx, obj)),
            UmtxOpSemWait => UsemWait(ctx, obj, legacy: true, uaddr1, uaddr2),
            UmtxOpSemWake => Libc(ctx, UsemWake(ctx, obj, legacy: true)),
            UmtxOpSem2Wait => UsemWait(ctx, obj, legacy: false, uaddr1, uaddr2),
            UmtxOpSem2Wake => Libc(ctx, UsemWake(ctx, obj, legacy: false)),
            _ => Libc(ctx, GuestErrno.EINVAL),
        };
    }

    private static int Libc(CpuContext ctx, int errno) => GuestErrno.ReturnLibc(ctx, errno);

    // Parks a waiter queued under its (now released) bucket gate. A host thread returns
    // the final value here; a guest thread parks and reports through the resume handler.
    private static int ParkUmtx(CpuContext ctx, GuestWaiter waiter, string reason, Func<GuestWaiter, int> toErrno) =>
        waiter.Block(ctx, reason, completed => GuestErrno.ReturnLibc(ctx, toErrno(completed)));

    private static int StatusErrno(GuestWaiter waiter) => waiter.Status == GuestWaitStatus.Interrupted
        ? GuestErrno.EINTR
        : GuestErrno.FromWaitStatus(waiter.Status);

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var requested = unchecked((int)ctx[CpuRegister.Rsi]);
        if (address == 0 || (address & 3) != 0 || requested < 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        WakeAddress(ctx, address, requested);
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int WakeAddress(CpuContext ctx, ulong address, int requested)
    {
        var woken = requested == 0 ? 0 : Table.Wake(address, requested, KindValue);
        var returnRip = 0UL;
        var outerCallerRip = 0UL;
        var parentCallerRip = 0UL;
        var ancestorCallerRip = 0UL;
        var outerCallerRip1 = 0UL;
        var outerCallerRip2 = 0UL;
        var outerCallerRip3 = 0UL;
        if (KernelSyncOnAddressProfile.DetailedEnabled)
        {
            CaptureCallStack(
                ctx,
                out returnRip,
                out outerCallerRip,
                out parentCallerRip,
                out ancestorCallerRip,
                out outerCallerRip1,
                out outerCallerRip2,
                out outerCallerRip3);
        }
        KernelSyncOnAddressProfile.RecordWake(
            address,
            requested,
            woken,
            returnRip,
            outerCallerRip,
            parentCallerRip,
            ancestorCallerRip,
            outerCallerRip1,
            outerCallerRip2,
            outerCallerRip3,
            GuestThreadExecution.CurrentGuestThreadHandle,
            Environment.CurrentManagedThreadId);
        return woken;
    }

    private static void CaptureCallStack(
        CpuContext ctx,
        out ulong returnRip,
        out ulong callerRip,
        out ulong parentRip,
        out ulong ancestorRip,
        out ulong outerRip1,
        out ulong outerRip2,
        out ulong outerRip3)
    {
        returnRip = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var immediateReturnRip)
            ? immediateReturnRip
            : 0UL;
        var framePointer = ctx[CpuRegister.Rbp];
        callerRip = TryReadFrameReturnRip(ctx, framePointer, out var parentFramePointer);
        parentRip = TryReadFrameReturnRip(ctx, parentFramePointer, out var ancestorFramePointer);
        ancestorRip = TryReadFrameReturnRip(ctx, ancestorFramePointer, out var outerFramePointer1);
        outerRip1 = TryReadFrameReturnRip(ctx, outerFramePointer1, out var outerFramePointer2);
        outerRip2 = TryReadFrameReturnRip(ctx, outerFramePointer2, out var outerFramePointer3);
        outerRip3 = TryReadFrameReturnRip(ctx, outerFramePointer3, out _);
    }

    private static ulong TryReadFrameReturnRip(
        CpuContext ctx,
        ulong framePointer,
        out ulong parentFramePointer)
    {
        parentFramePointer = 0;
        if (framePointer == 0 || framePointer > ulong.MaxValue - sizeof(ulong))
        {
            return 0;
        }

        _ = ctx.TryReadUInt64(framePointer, out parentFramePointer);
        return ctx.TryReadUInt64(framePointer + sizeof(ulong), out var returnRip)
            ? returnRip
            : 0UL;
    }

    private static int Wait(CpuContext ctx, bool is64Bit)
    {
        var address = ctx[CpuRegister.Rdi];
        var expected = is64Bit
            ? ctx[CpuRegister.Rsi]
            : unchecked((uint)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];
        var alignmentMask = is64Bit ? 7UL : 3UL;
        if (address == 0 || (address & alignmentMask) != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        long deadline = 0;
        if (timeoutAddress != 0)
        {
            if (!ctx.TryReadUInt32(timeoutAddress, out var timeoutMicroseconds))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            deadline = GuestTime.DeadlineAfterMicroseconds(timeoutMicroseconds);
        }

        return ValueWaitCore(
            ctx,
            address,
            expected,
            is64Bit,
            deadline,
            libc: false,
            is64Bit ? "sceKernelSyncOnAddressWait64" : "sceKernelSyncOnAddressWait32");
    }

    private static int UmtxValueWait(
        CpuContext ctx,
        ulong address,
        ulong expected,
        bool is64Bit,
        ulong timeoutSize,
        ulong timeoutAddress,
        string reason)
    {
        if (address == 0 || (address & (is64Bit ? 7UL : 3UL)) != 0)
        {
            return Libc(ctx, GuestErrno.EFAULT);
        }

        if (!TryReadUmtxTimeout(ctx, timeoutSize, timeoutAddress, out var deadline, out var errno))
        {
            return Libc(ctx, errno);
        }

        return ValueWaitCore(ctx, address, expected, is64Bit, deadline, libc: true, reason);
    }

    // Value check and enqueue happen under the address bucket's gate, which every wake of
    // that address takes: a guest that changes the word and then wakes cannot slip
    // between them.
    private static int ValueWaitCore(
        CpuContext ctx,
        ulong address,
        ulong expected,
        bool is64Bit,
        long deadline,
        bool libc,
        string reason)
    {
        var bucket = Table.BucketFor(address);
        ValueWaiter? waiter = null;
        var immediateErrno = 0;
        lock (bucket.Gate)
        {
            if (!TryReadValue(ctx, address, is64Bit, out var current))
            {
                immediateErrno = GuestErrno.EFAULT;
            }
            else if (current != expected)
            {
                KernelSyncOnAddressProfile.RecordImmediate(address);
            }
            else if (deadline != 0 && deadline <= Stopwatch.GetTimestamp())
            {
                immediateErrno = GuestErrno.ETIMEDOUT;
            }
            else
            {
                waiter = new ValueWaiter(ctx, address, expected, is64Bit);
                bucket.EnqueueLocked(waiter);
                waiter.ArmDeadlineLocked(deadline);
            }
        }

        if (waiter is null)
        {
            return Finish(ctx, immediateErrno, libc);
        }

        var returnRip = 0UL;
        var callerRip = 0UL;
        var parentRip = 0UL;
        if (KernelSyncOnAddressProfile.DetailedEnabled)
        {
            CaptureCallStack(ctx, out returnRip, out callerRip, out parentRip, out _, out _, out _, out _);
        }
        KernelSyncOnAddressProfile.RecordBlocked(
            address,
            returnRip,
            callerRip,
            parentRip,
            GuestThreadExecution.CurrentGuestThreadHandle,
            Environment.CurrentManagedThreadId);

        return waiter.Block(ctx, reason, completed => Finish(ctx, ((ValueWaiter)completed).ResultErrno(), libc));
    }

    private static int Finish(CpuContext ctx, int errno, bool libc) =>
        libc ? GuestErrno.ReturnLibc(ctx, errno) : GuestErrno.ReturnSce(ctx, errno);

    private static int UmtxWake(CpuContext ctx, ulong address, ulong count)
    {
        var requested = count > int.MaxValue ? int.MaxValue : (int)count;
        _ = WakeAddress(ctx, address, requested);
        return Libc(ctx, 0);
    }

    // UMTX_OP_NWAKE_PRIVATE: obj is an array of `count` addresses; wake all on each.
    private static int UmtxNwake(CpuContext ctx, ulong arrayAddress, ulong count)
    {
        for (ulong i = 0; i < count; i++)
        {
            if (!ctx.TryReadUInt64(arrayAddress + i * sizeof(ulong), out var address))
            {
                return Libc(ctx, GuestErrno.EFAULT);
            }

            _ = WakeAddress(ctx, address, int.MaxValue);
        }

        return Libc(ctx, 0);
    }

    /// <summary>
    /// Reads a umtx timeout: none when <paramref name="address"/> is 0; a relative timespec
    /// when <paramref name="size"/> ≤ 16; otherwise <c>struct _umtx_time</c>, absolute on
    /// its clock with UMTX_ABSTIME.
    /// </summary>
    private static bool TryReadUmtxTimeout(CpuContext ctx, ulong size, ulong address, out long deadline, out int errno)
    {
        deadline = 0;
        errno = 0;
        if (address == 0)
        {
            return true;
        }

        if (size <= TimespecSize)
        {
            return GuestTime.TryReadRelativeDeadline(ctx, address, out deadline, out errno);
        }

        if (!ctx.TryReadUInt32(address + TimespecSize, out var flags) ||
            !ctx.TryReadUInt32(address + TimespecSize + 4, out var clockId))
        {
            errno = GuestErrno.EFAULT;
            return false;
        }

        return (flags & UmtxAbstime) != 0
            ? GuestTime.TryReadAbsoluteDeadline(ctx, address, unchecked((int)clockId), out deadline, out errno)
            : GuestTime.TryReadRelativeDeadline(ctx, address, out deadline, out errno);
    }

    private static uint CurrentUmtxThreadId() => unchecked((uint)KernelPthreadState.GetCurrentThreadUniqueId());

    // UMTX_OP_MUTEX_LOCK / TRYLOCK / WAIT (kern_umtx.c do_lock_normal). A lock waiter is
    // completed only after the lock was handed to it, so a parked thread resumes owning it.
    private static int UmutexLock(CpuContext ctx, ulong mutex, bool trylock, bool waitOnly, ulong timeoutSize, ulong timeoutAddress)
    {
        if (mutex == 0 || (mutex & 3) != 0)
        {
            return Libc(ctx, GuestErrno.EFAULT);
        }

        long deadline = 0;
        if (!trylock && !TryReadUmtxTimeout(ctx, timeoutSize, timeoutAddress, out deadline, out var timeoutErrno))
        {
            return Libc(ctx, timeoutErrno);
        }

        var id = CurrentUmtxThreadId();
        var bucket = Table.BucketFor(mutex);
        MutexWaiter? waiter = null;
        int errno;
        lock (bucket.Gate)
        {
            while (true)
            {
                if (!ctx.TryReadUInt32(mutex, out var owner))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                if ((owner & ~UmutexContested) == UmutexUnowned)
                {
                    if (waitOnly)
                    {
                        errno = 0;
                        break;
                    }

                    if (!GuestAtomic.TryCompareExchange32(ctx, mutex, owner, id | (owner & UmutexContested), out var observed))
                    {
                        errno = GuestErrno.EFAULT;
                        break;
                    }

                    if (observed == owner)
                    {
                        errno = 0;
                        break;
                    }

                    continue;
                }

                if (!waitOnly && (owner & ~UmutexContested) == id)
                {
                    errno = GuestErrno.EDEADLK;
                    break;
                }

                if (trylock)
                {
                    errno = GuestErrno.EBUSY;
                    break;
                }

                if (deadline != 0 && deadline <= Stopwatch.GetTimestamp())
                {
                    errno = GuestErrno.ETIMEDOUT;
                    break;
                }

                // Mark contested so the owner's unlock comes here, then queue.
                if ((owner & UmutexContested) == 0)
                {
                    if (!GuestAtomic.TryCompareExchange32(ctx, mutex, owner, owner | UmutexContested, out var observed))
                    {
                        errno = GuestErrno.EFAULT;
                        break;
                    }

                    if (observed != owner)
                    {
                        continue;
                    }
                }

                waiter = new MutexWaiter(mutex, waitOnly ? KindMutexWait : KindMutexLock, id);
                bucket.EnqueueLocked(waiter);
                waiter.ArmDeadlineLocked(deadline);
                errno = 0;
                break;
            }
        }

        if (waiter is null)
        {
            return Libc(ctx, errno);
        }

        return ParkUmtx(ctx, waiter, waitOnly ? "_umtx_op.mutex_wait" : "_umtx_op.mutex_lock", StatusErrno);
    }

    // UMTX_OP_MUTEX_UNLOCK (do_unlock_normal): only the owner may unlock; a contested
    // mutex goes straight to the first lock waiter.
    private static int UmutexUnlock(CpuContext ctx, ulong mutex)
    {
        if (mutex == 0 || (mutex & 3) != 0)
        {
            return GuestErrno.EFAULT;
        }

        var id = CurrentUmtxThreadId();
        var bucket = Table.BucketFor(mutex);
        var batch = default(WakeBatch);
        int errno;
        lock (bucket.Gate)
        {
            while (true)
            {
                if (!ctx.TryReadUInt32(mutex, out var owner))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                if ((owner & ~UmutexContested) != id)
                {
                    errno = GuestErrno.EPERM;
                    break;
                }

                if (!TryReleaseUmutexLocked(ctx, bucket, mutex, owner, ref batch, out var released))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                if (released)
                {
                    errno = 0;
                    break;
                }
            }
        }

        batch.WakeAll();
        return errno;
    }

    // Moves the mutex from `owner` to the next waiter (or unowned). False on a fault;
    // `released` false when the word changed under us (caller retries).
    private static bool TryReleaseUmutexLocked(
        CpuContext ctx,
        WaitQueue bucket,
        ulong mutex,
        uint owner,
        ref WakeBatch batch,
        out bool released)
    {
        released = false;
        var first = FirstWaiter(bucket, mutex, out var waiting);
        uint next;
        if (first is null)
        {
            next = UmutexUnowned;
        }
        else if (first.Kind == KindMutexLock)
        {
            next = ((MutexWaiter)first).ThreadId | (waiting > 1 ? UmutexContested : 0);
        }
        else
        {
            next = waiting > 1 ? UmutexContested : UmutexUnowned;
        }

        if (!GuestAtomic.TryCompareExchange32(ctx, mutex, owner, next, out var observed))
        {
            return false;
        }

        if (observed != owner)
        {
            return true;
        }

        released = true;
        if (first is not null && first.TryCompleteLocked(GuestWaitStatus.Satisfied))
        {
            batch.Add(first);
        }

        return true;
    }

    // UMTX_OP_MUTEX_WAKE / WAKE2: userland released the mutex itself; hand it to the first
    // waiter if still free, otherwise keep it contested so its owner unlocks through here.
    private static int UmutexWake(CpuContext ctx, ulong mutex)
    {
        if (mutex == 0 || (mutex & 3) != 0)
        {
            return GuestErrno.EFAULT;
        }

        var bucket = Table.BucketFor(mutex);
        var batch = default(WakeBatch);
        var errno = 0;
        lock (bucket.Gate)
        {
            while (true)
            {
                if (!ctx.TryReadUInt32(mutex, out var owner))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                var first = FirstWaiter(bucket, mutex, out _);
                if ((owner & ~UmutexContested) != UmutexUnowned)
                {
                    if (first is not null && (owner & UmutexContested) == 0 &&
                        GuestAtomic.TryCompareExchange32(ctx, mutex, owner, owner | UmutexContested, out var seen) &&
                        seen != owner)
                    {
                        continue;
                    }

                    break;
                }

                if (!TryReleaseUmutexLocked(ctx, bucket, mutex, owner, ref batch, out var released))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                if (released)
                {
                    break;
                }
            }
        }

        batch.WakeAll();
        return errno;
    }

    private static AddressWaiter? FirstWaiter(WaitQueue bucket, ulong address, out int count, int kind = AddressWaitTable.KindAny)
    {
        AddressWaiter? first = null;
        count = 0;
        for (var waiter = bucket.First; waiter is not null; waiter = waiter.NextInQueue)
        {
            if (waiter is AddressWaiter candidate &&
                candidate.Address == address &&
                (kind == AddressWaitTable.KindAny || candidate.Kind == kind))
            {
                first ??= candidate;
                count++;
            }
        }

        return first;
    }

    // UMTX_OP_CV_WAIT (do_cv_wait): queue on the ucond, mark it, release the umutex, sleep.
    // The mutex is not re-acquired here; userland relocks it.
    private static int UcondWait(CpuContext ctx, ulong cond, uint flags, ulong mutex, ulong timeoutAddress)
    {
        if (cond == 0 || (cond & 3) != 0)
        {
            return Libc(ctx, GuestErrno.EFAULT);
        }

        long deadline = 0;
        if (timeoutAddress != 0)
        {
            int errno;
            bool ok;
            if ((flags & CvwaitAbstime) != 0)
            {
                var clockId = GuestTime.ClockRealtime;
                if ((flags & CvwaitClockid) != 0 && ctx.TryReadUInt32(cond + 8, out var condClock))
                {
                    clockId = unchecked((int)condClock);
                }

                ok = GuestTime.TryReadAbsoluteDeadline(ctx, timeoutAddress, clockId, out deadline, out errno);
            }
            else
            {
                ok = GuestTime.TryReadRelativeDeadline(ctx, timeoutAddress, out deadline, out errno);
            }

            if (!ok)
            {
                return Libc(ctx, errno);
            }
        }

        var bucket = Table.BucketFor(cond);
        var waiter = new AddressWaiter(cond, KindCond);
        lock (bucket.Gate)
        {
            bucket.EnqueueLocked(waiter);
            _ = ctx.TryWriteUInt32(cond, 1);
            waiter.ArmDeadlineLocked(deadline);
        }

        var unlockErrno = UmutexUnlock(ctx, mutex);
        if (unlockErrno != 0)
        {
            lock (bucket.Gate)
            {
                _ = bucket.RemoveLocked(waiter);
                if (AddressWaitTable.CountLocked(bucket, cond, KindCond) == 0)
                {
                    _ = ctx.TryWriteUInt32(cond, 0);
                }
            }

            _ = waiter.TryCompleteLocked(GuestWaitStatus.Canceled);
            return Libc(ctx, unlockErrno);
        }

        return ParkUmtx(ctx, waiter, "_umtx_op.cv_wait", completed =>
        {
            if (completed.Status != GuestWaitStatus.Satisfied)
            {
                // Not signalled: drop the has-waiters mark if nobody else waits.
                lock (bucket.Gate)
                {
                    if (AddressWaitTable.CountLocked(bucket, cond, KindCond) == 0)
                    {
                        _ = ctx.TryWriteUInt32(cond, 0);
                    }
                }
            }

            return StatusErrno(completed);
        });
    }

    private static int UcondWake(CpuContext ctx, ulong cond, bool broadcast)
    {
        if (cond == 0 || (cond & 3) != 0)
        {
            return GuestErrno.EFAULT;
        }

        var bucket = Table.BucketFor(cond);
        var batch = default(WakeBatch);
        lock (bucket.Gate)
        {
            var waiting = AddressWaitTable.CountLocked(bucket, cond, KindCond);
            var woken = AddressWaitTable.WakeLocked(bucket, cond, broadcast ? int.MaxValue : 1, ref batch, KindCond);
            if (waiting - woken <= 0)
            {
                _ = ctx.TryWriteUInt32(cond, 0);
            }
        }

        batch.WakeAll();
        return 0;
    }

    private static uint ReaderCount(uint state) => state & UrwlockMaxReaders;

    // UMTX_OP_RW_RDLOCK / WRLOCK (do_rw_rdlock / do_rw_wrlock). Waiters are granted the
    // lock by the unlocker (state updated for them), so a resumed waiter holds it.
    private static int UrwlockLock(CpuContext ctx, ulong rwlock, bool writer, uint fflag, ulong timeoutSize, ulong timeoutAddress)
    {
        if (rwlock == 0 || (rwlock & 3) != 0)
        {
            return Libc(ctx, GuestErrno.EFAULT);
        }

        if (!TryReadUmtxTimeout(ctx, timeoutSize, timeoutAddress, out var deadline, out var timeoutErrno))
        {
            return Libc(ctx, timeoutErrno);
        }

        var bucket = Table.BucketFor(rwlock);
        RwlockWaiter? waiter = null;
        int errno;
        lock (bucket.Gate)
        {
            while (true)
            {
                if (!ctx.TryReadUInt32(rwlock, out var state) || !ctx.TryReadUInt32(rwlock + 4, out var flags))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                uint desired;
                bool available;
                if (writer)
                {
                    available = (state & UrwlockWriteOwner) == 0 && ReaderCount(state) == 0;
                    desired = state | UrwlockWriteOwner;
                }
                else
                {
                    var blockers = ((fflag | flags) & UrwlockPreferReader) != 0
                        ? UrwlockWriteOwner
                        : UrwlockWriteOwner | UrwlockWriteWaiters;
                    available = (state & blockers) == 0;
                    if (available && ReaderCount(state) == UrwlockMaxReaders)
                    {
                        errno = GuestErrno.EAGAIN;
                        break;
                    }

                    desired = state + 1;
                }

                if (available)
                {
                    if (!GuestAtomic.TryCompareExchange32(ctx, rwlock, state, desired, out var observed))
                    {
                        errno = GuestErrno.EFAULT;
                        break;
                    }

                    if (observed == state)
                    {
                        errno = 0;
                        break;
                    }

                    continue;
                }

                if (deadline != 0 && deadline <= Stopwatch.GetTimestamp())
                {
                    errno = GuestErrno.ETIMEDOUT;
                    break;
                }

                var waiterBit = writer ? UrwlockWriteWaiters : UrwlockReadWaiters;
                if ((state & waiterBit) == 0)
                {
                    if (!GuestAtomic.TryCompareExchange32(ctx, rwlock, state, state | waiterBit, out var observed))
                    {
                        errno = GuestErrno.EFAULT;
                        break;
                    }

                    if (observed != state)
                    {
                        continue;
                    }
                }

                _ = GuestAtomic.TryAdd32(ctx, rwlock + (writer ? 12UL : 8UL), 1, out _);
                waiter = new RwlockWaiter(ctx, rwlock, writer ? KindWriter : KindReader);
                bucket.EnqueueLocked(waiter);
                waiter.ArmDeadlineLocked(deadline);
                errno = 0;
                break;
            }
        }

        if (waiter is null)
        {
            return Libc(ctx, errno);
        }

        return ParkUmtx(ctx, waiter, writer ? "_umtx_op.rw_wrlock" : "_umtx_op.rw_rdlock", StatusErrno);
    }

    // UMTX_OP_RW_UNLOCK (do_rw_unlock): release, then grant a waiting writer (unless the
    // lock prefers readers) or every waiting reader.
    private static int UrwlockUnlock(CpuContext ctx, ulong rwlock)
    {
        if (rwlock == 0 || (rwlock & 3) != 0)
        {
            return GuestErrno.EFAULT;
        }

        var bucket = Table.BucketFor(rwlock);
        var batch = default(WakeBatch);
        int errno;
        lock (bucket.Gate)
        {
            while (true)
            {
                if (!ctx.TryReadUInt32(rwlock, out var state))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                uint desired;
                if ((state & UrwlockWriteOwner) != 0)
                {
                    desired = state & ~UrwlockWriteOwner;
                }
                else if (ReaderCount(state) > 0)
                {
                    desired = state - 1;
                }
                else
                {
                    errno = GuestErrno.EPERM;
                    break;
                }

                if (!GuestAtomic.TryCompareExchange32(ctx, rwlock, state, desired, out var observed))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                if (observed != state)
                {
                    continue;
                }

                GrantRwlockLocked(ctx, bucket, rwlock, ref batch);
                errno = 0;
                break;
            }
        }

        batch.WakeAll();
        return errno;
    }

    // Grants the free lock to waiters (bucket gate held): one writer first unless the lock
    // prefers readers, else all readers. Blocked counts and waiter bits follow the grant.
    private static void GrantRwlockLocked(CpuContext ctx, WaitQueue bucket, ulong rwlock, ref WakeBatch batch)
    {
        if (!ctx.TryReadUInt32(rwlock, out var state) || !ctx.TryReadUInt32(rwlock + 4, out var flags) ||
            (state & UrwlockWriteOwner) != 0)
        {
            return;
        }

        var firstWriter = FirstWaiter(bucket, rwlock, out var writers, KindWriter);
        var preferReader = (flags & UrwlockPreferReader) != 0;
        var readers = AddressWaitTable.CountLocked(bucket, rwlock, KindReader);
        if (firstWriter is not null && ReaderCount(state) == 0 && (!preferReader || readers == 0))
        {
            var granted = state | UrwlockWriteOwner;
            if (writers == 1)
            {
                granted &= ~UrwlockWriteWaiters;
            }

            if (GuestAtomic.TryCompareExchange32(ctx, rwlock, state, granted, out var observed) && observed == state &&
                firstWriter.TryCompleteLocked(GuestWaitStatus.Satisfied))
            {
                _ = GuestAtomic.TryAdd32(ctx, rwlock + 12, -1, out _);
                batch.Add(firstWriter);
            }

            return;
        }

        if (readers == 0 || (!preferReader && firstWriter is not null))
        {
            return;
        }

        var grantedState = (state & ~UrwlockReadWaiters) + (uint)readers;
        if (!GuestAtomic.TryCompareExchange32(ctx, rwlock, state, grantedState, out var seen) || seen != state)
        {
            return;
        }

        var woken = AddressWaitTable.WakeLocked(bucket, rwlock, readers, ref batch, KindReader);
        _ = GuestAtomic.TryAdd32(ctx, rwlock + 8, -woken, out _);
    }

    // A waiter left without a grant (timeout): drop its blocked count and, as the last of
    // its class, the waiter bit; a departing last writer lets waiting readers in.
    private static void LeaveRwlockLocked(CpuContext ctx, WaitQueue bucket, ulong rwlock, int kind, ref WakeBatch batch)
    {
        var writer = kind == KindWriter;
        _ = GuestAtomic.TryAdd32(ctx, rwlock + (writer ? 12UL : 8UL), -1, out _);
        if (AddressWaitTable.CountLocked(bucket, rwlock, kind) == 0)
        {
            _ = GuestAtomic.TryAndNot32(ctx, rwlock, writer ? UrwlockWriteWaiters : UrwlockReadWaiters, out _);
            if (writer)
            {
                GrantRwlockLocked(ctx, bucket, rwlock, ref batch);
            }
        }
    }

    // UMTX_OP_SEM2_WAIT / SEM_WAIT (do_sem2_wait / do_sem_wait): mark has-waiters and sleep
    // unless a count is available; userland retries the decrement after waking.
    private static int UsemWait(CpuContext ctx, ulong sem, bool legacy, ulong timeoutSize, ulong timeoutAddress)
    {
        if (sem == 0 || (sem & 3) != 0)
        {
            return Libc(ctx, GuestErrno.EFAULT);
        }

        if (!TryReadUmtxTimeout(ctx, timeoutSize, timeoutAddress, out var deadline, out var timeoutErrno))
        {
            return Libc(ctx, timeoutErrno);
        }

        var bucket = Table.BucketFor(sem);
        AddressWaiter? waiter = null;
        int errno;
        lock (bucket.Gate)
        {
            while (true)
            {
                uint count;
                if (legacy)
                {
                    // struct _usem { u32 _has_waiters; u32 _count; u32 _flags; }
                    if (!ctx.TryWriteUInt32(sem, 1) || !ctx.TryReadUInt32(sem + 4, out count))
                    {
                        errno = GuestErrno.EFAULT;
                        break;
                    }
                }
                else if (!ctx.TryReadUInt32(sem, out count))
                {
                    errno = GuestErrno.EFAULT;
                    break;
                }

                if ((count & ~UsemHasWaiters) != 0)
                {
                    errno = 0;
                    break;
                }

                if (deadline != 0 && deadline <= Stopwatch.GetTimestamp())
                {
                    errno = GuestErrno.ETIMEDOUT;
                    break;
                }

                if (!legacy && (count & UsemHasWaiters) == 0)
                {
                    if (!GuestAtomic.TryCompareExchange32(ctx, sem, count, count | UsemHasWaiters, out var observed))
                    {
                        errno = GuestErrno.EFAULT;
                        break;
                    }

                    if (observed != count)
                    {
                        continue;
                    }
                }

                waiter = new AddressWaiter(sem, KindSem);
                bucket.EnqueueLocked(waiter);
                waiter.ArmDeadlineLocked(deadline);
                errno = 0;
                break;
            }
        }

        if (waiter is null)
        {
            return Libc(ctx, errno);
        }

        return ParkUmtx(ctx, waiter, legacy ? "_umtx_op.sem_wait" : "_umtx_op.sem2_wait", StatusErrno);
    }

    private static int UsemWake(CpuContext ctx, ulong sem, bool legacy)
    {
        if (sem == 0 || (sem & 3) != 0)
        {
            return GuestErrno.EFAULT;
        }

        var bucket = Table.BucketFor(sem);
        var batch = default(WakeBatch);
        lock (bucket.Gate)
        {
            var waiting = AddressWaitTable.CountLocked(bucket, sem, KindSem);
            if (waiting <= 1)
            {
                _ = legacy
                    ? ctx.TryWriteUInt32(sem, 0)
                    : GuestAtomic.TryAndNot32(ctx, sem, UsemHasWaiters, out _);
            }

            _ = AddressWaitTable.WakeLocked(bucket, sem, 1, ref batch, KindSem);
        }

        batch.WakeAll();
        return 0;
    }

    private static bool TryReadValue(
        CpuContext ctx,
        ulong address,
        bool is64Bit,
        out ulong value)
    {
        if (is64Bit)
        {
            return ctx.TryReadUInt64(address, out value);
        }

        if (ctx.TryReadUInt32(address, out var value32))
        {
            value = value32;
            return true;
        }

        value = 0;
        return false;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)value);
        return value;
    }
}
