// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// poll(2) and select(2) over the same descriptor readiness the read/write
/// knotes use. A call that finds nothing ready parks until a descriptor
/// changes (socket readiness monitor, close) or its timeout passes.
/// </summary>
public static partial class KernelMemoryCompatExports
{
    private const short POLLIN = 0x0001;
    private const short POLLPRI = 0x0002;
    private const short POLLOUT = 0x0004;
    private const short POLLERR = 0x0008;
    private const short POLLHUP = 0x0010;
    private const short POLLNVAL = 0x0020;
    private const short POLLRDNORM = 0x0040;
    private const short POLLRDBAND = 0x0080;
    private const short POLLWRBAND = 0x0100;

    private const int PollFdSize = 8;
    private const int MaxPollDescriptors = 4096;
    private const int FdSetBits = 1024;

    private struct PollEntry
    {
        public int Fd;
        public bool WantRead;
        public bool WantWrite;
        public bool Known;
        public bool Readable;
        public bool Writable;
        public bool Hangup;
    }

    // Evaluates every entry; returns how many descriptors have something to report.
    private static int EvaluatePollEntries(PollEntry[] entries, bool countUnknown, bool arm)
    {
        var ready = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            entry.Readable = entry.Writable = entry.Hangup = false;
            if (entry.Fd < 0)
            {
                entry.Known = true;
                continue;
            }

            entry.Known = KernelEventQueueCompatExports.TryGetDescriptorState(
                entry.Fd,
                KernelEventQueueCompatExports.KernelEventFilterRead,
                1,
                out var readable,
                out _,
                out var readFlags);
            if (!entry.Known)
            {
                if (countUnknown)
                {
                    ready++;
                }

                continue;
            }

            if (entry.WantRead)
            {
                entry.Readable = readable;
                entry.Hangup = readable && (readFlags & KernelEventQueueCompatExports.KernelEventFlagEof) != 0 &&
                               !IsRegularFileDescriptor(entry.Fd);
            }

            if (entry.WantWrite)
            {
                _ = KernelEventQueueCompatExports.TryGetDescriptorState(
                    entry.Fd,
                    KernelEventQueueCompatExports.KernelEventFilterWrite,
                    1,
                    out entry.Writable,
                    out _,
                    out _);
            }

            if (entry.Readable || entry.Writable || entry.Hangup)
            {
                ready++;
            }
            else if (arm)
            {
                if (entry.WantRead)
                {
                    KernelEventQueueCompatExports.ArmDescriptorMonitor(entry.Fd, write: false);
                }

                if (entry.WantWrite)
                {
                    KernelEventQueueCompatExports.ArmDescriptorMonitor(entry.Fd, write: true);
                }
            }
        }

        return ready;
    }

    private static bool IsRegularFileDescriptor(int fd)
    {
        lock (_fdGate)
        {
            return _openFiles.ContainsKey(fd);
        }
    }

    private sealed class PollWaiter : IGuestThreadBlockWaiter
    {
        public required PollEntry[] Entries { get; init; }
        public required bool CountUnknown { get; init; }
        public required Func<int, int> Complete { get; init; }
        private int _ready = -1;

        public bool TryWake()
        {
            if (_ready > 0)
            {
                return true;
            }

            var ready = EvaluatePollEntries(Entries, CountUnknown, arm: true);
            if (ready == 0)
            {
                return false;
            }

            _ready = ready;
            return true;
        }

        public int Resume()
        {
            // A timed-out wait reports the state at expiry.
            var ready = _ready > 0 ? _ready : EvaluatePollEntries(Entries, CountUnknown, arm: false);
            return Complete(ready);
        }
    }

    /// <summary>
    /// Blocks until an entry is ready or the deadline (0 = none, -1 = already
    /// expired) passes, then calls <paramref name="complete"/> with the count.
    /// </summary>
    private static int WaitForPollEntries(
        CpuContext ctx,
        string reason,
        PollEntry[] entries,
        bool countUnknown,
        long deadline,
        Func<int, int> complete)
    {
        var generation = KernelEventQueueCompatExports.DescriptorChangeGeneration;
        var ready = EvaluatePollEntries(entries, countUnknown, arm: deadline >= 0);
        if (ready > 0 || deadline < 0)
        {
            return complete(ready);
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                reason,
                KernelEventQueueCompatExports.DescriptorReadinessWakeKey,
                new PollWaiter { Entries = entries, CountUnknown = countUnknown, Complete = complete },
                deadline))
        {
            return 0;
        }

        while (true)
        {
            KernelEventQueueCompatExports.WaitForDescriptorChange(generation, deadline);
            generation = KernelEventQueueCompatExports.DescriptorChangeGeneration;
            ready = EvaluatePollEntries(entries, countUnknown, arm: true);
            if (ready > 0 || (deadline != 0 && Stopwatch.GetTimestamp() >= deadline))
            {
                return complete(ready);
            }
        }
    }

    // poll(struct pollfd* fds, nfds_t nfds, int timeout_ms)
    [SysAbiExport(Nid = "ku7D4q1Y9PI", ExportName = "poll",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPoll(CpuContext ctx)
    {
        var fdsAddress = ctx[CpuRegister.Rdi];
        var count = ctx[CpuRegister.Rsi];
        var timeoutMilliseconds = unchecked((int)ctx[CpuRegister.Rdx]);
        if (count > MaxPollDescriptors)
        {
            return PollFailure(ctx, Einval);
        }

        var entries = new PollEntry[count];
        var raw = new byte[(int)count * PollFdSize];
        var events = new short[count];
        if (count != 0 && (fdsAddress == 0 || !ctx.Memory.TryRead(fdsAddress, raw)))
        {
            return PollFailure(ctx, Efault);
        }

        for (var i = 0; i < entries.Length; i++)
        {
            var record = raw.AsSpan(i * PollFdSize);
            events[i] = BinaryPrimitives.ReadInt16LittleEndian(record[4..]);
            entries[i] = new PollEntry
            {
                Fd = BinaryPrimitives.ReadInt32LittleEndian(record),
                WantRead = (events[i] & (POLLIN | POLLRDNORM | POLLRDBAND | POLLPRI)) != 0,
                WantWrite = (events[i] & (POLLOUT | POLLWRBAND)) != 0,
            };
        }

        var memory = ctx.Memory;
        int Complete(int _)
        {
            var ready = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                short revents = 0;
                if (entry.Fd >= 0)
                {
                    if (!entry.Known)
                    {
                        revents = POLLNVAL;
                    }
                    else
                    {
                        if (entry.Readable)
                        {
                            revents |= (short)(events[i] & (POLLIN | POLLRDNORM));
                        }

                        if (entry.Writable)
                        {
                            revents |= (short)(events[i] & (POLLOUT | POLLWRBAND));
                        }

                        if (entry.Hangup)
                        {
                            revents |= POLLHUP;
                        }
                    }
                }

                BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * PollFdSize + 6), revents);
                if (revents != 0)
                {
                    ready++;
                }
            }

            if (raw.Length != 0 && !memory.TryWrite(fdsAddress, raw))
            {
                return PollFailure(ctx, Efault);
            }

            ctx[CpuRegister.Rax] = unchecked((ulong)ready);
            return ready;
        }

        long deadline = timeoutMilliseconds switch
        {
            0 => -1,
            < 0 => 0,
            _ => GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMilliseconds(timeoutMilliseconds)),
        };
        return WaitForPollEntries(ctx, "poll", entries, countUnknown: true, deadline, Complete);
    }

    // select(int nfds, fd_set* readfds, fd_set* writefds, fd_set* exceptfds, struct timeval* timeout)
    [SysAbiExport(Nid = "T8fER+tIGgk", ExportName = "select",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSelect(CpuContext ctx)
    {
        var nfds = unchecked((int)ctx[CpuRegister.Rdi]);
        var readAddress = ctx[CpuRegister.Rsi];
        var writeAddress = ctx[CpuRegister.Rdx];
        var exceptAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];
        if (nfds < 0 || nfds > FdSetBits)
        {
            return PollFailure(ctx, Einval);
        }

        var byteCount = (nfds + 7) / 8;
        var readSet = new byte[byteCount];
        var writeSet = new byte[byteCount];
        var exceptSet = new byte[byteCount];
        if ((readAddress != 0 && !ctx.Memory.TryRead(readAddress, readSet)) ||
            (writeAddress != 0 && !ctx.Memory.TryRead(writeAddress, writeSet)) ||
            (exceptAddress != 0 && !ctx.Memory.TryRead(exceptAddress, exceptSet)))
        {
            return PollFailure(ctx, Efault);
        }

        long deadline = 0;
        if (timeoutAddress != 0)
        {
            if (!ctx.TryReadUInt64(timeoutAddress, out var secondsBits) ||
                !ctx.TryReadUInt64(timeoutAddress + 8, out var microsecondsBits))
            {
                return PollFailure(ctx, Efault);
            }

            var seconds = unchecked((long)secondsBits);
            var microseconds = unchecked((long)microsecondsBits);
            if (seconds < 0 || microseconds < 0 || microseconds >= 1_000_000)
            {
                return PollFailure(ctx, Einval);
            }

            deadline = seconds == 0 && microseconds == 0
                ? -1
                : GuestThreadExecution.ComputeDeadlineTimestamp(
                    TimeSpan.FromSeconds(Math.Min(seconds, 100_000_000)) + TimeSpan.FromMicroseconds(microseconds));
        }

        var entries = new List<PollEntry>();
        for (var fd = 0; fd < nfds; fd++)
        {
            var mask = (byte)(1 << (fd & 7));
            var wantRead = (readSet[fd >> 3] & mask) != 0;
            var wantWrite = (writeSet[fd >> 3] & mask) != 0;
            var wantExcept = (exceptSet[fd >> 3] & mask) != 0;
            if (wantRead || wantWrite || wantExcept)
            {
                entries.Add(new PollEntry { Fd = fd, WantRead = wantRead, WantWrite = wantWrite });
            }
        }

        var entryArray = entries.ToArray();
        var memory = ctx.Memory;
        int Complete(int _)
        {
            foreach (var entry in entryArray)
            {
                if (!entry.Known)
                {
                    return PollFailure(ctx, Ebadf);
                }
            }

            Array.Clear(readSet);
            Array.Clear(writeSet);
            Array.Clear(exceptSet);
            var ready = 0;
            foreach (var entry in entryArray)
            {
                var mask = (byte)(1 << (entry.Fd & 7));
                if (entry.Readable || entry.Hangup)
                {
                    readSet[entry.Fd >> 3] |= mask;
                    ready++;
                }

                if (entry.Writable)
                {
                    writeSet[entry.Fd >> 3] |= mask;
                    ready++;
                }
            }

            if ((readAddress != 0 && !memory.TryWrite(readAddress, readSet)) ||
                (writeAddress != 0 && !memory.TryWrite(writeAddress, writeSet)) ||
                (exceptAddress != 0 && !memory.TryWrite(exceptAddress, exceptSet)))
            {
                return PollFailure(ctx, Efault);
            }

            ctx[CpuRegister.Rax] = unchecked((ulong)ready);
            return ready;
        }

        // An unknown descriptor fails the call at once with EBADF.
        return WaitForPollEntries(ctx, "select", entryArray, countUnknown: true, deadline, Complete);
    }

    private static int PollFailure(CpuContext ctx, int errno)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }
}
