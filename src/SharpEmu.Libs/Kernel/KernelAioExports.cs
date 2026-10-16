// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Asynchronous file I/O (sceKernelAio*). Submission validates the batch,
/// marks every result SUBMITTED and returns; one worker per priority runs the
/// batches in submission order. Each request publishes in a fixed order: the
/// transferred bytes, then its result record (return value, state), then the
/// request state seen by Poll/Wait, then a wake of the waiters.
/// </summary>
public static partial class KernelMemoryCompatExports
{
    // SCE_KERNEL_AIO_STATE_*.
    private const uint AioStateSubmitted = 1;
    private const uint AioStateProcessing = 2;
    private const uint AioStateCompleted = 3;
    private const uint AioStateAborted = 4;

    private const uint AioWaitAnd = 1;
    private const uint AioWaitOr = 2;

    private const int SizeofAioRwRequest = 40;
    private const int SizeofAioSchedulingParam = 20;
    private const int AioMaxBatch = 128;
    private const int AioPriorityCount = 4;
    private const int AioMaxLiveRequests = 0x10000;
    private const int AioTransferChunk = 1 << 20;

    private const int SceAioEnoent = unchecked((int)0x80020002);
    private const int SceAioEbadf = unchecked((int)0x80020009);
    private const int SceAioEfault = unchecked((int)0x8002000E);
    private const int SceAioEbusy = unchecked((int)0x80020010);
    private const int SceAioEinval = unchecked((int)0x80020016);
    private const int SceAioEagain = unchecked((int)0x80020023);
    private const int SceAioEtimedout = unchecked((int)0x8002003C);
    private const int SceAioEcanceled = unchecked((int)0x80020055);
    private const int SceAioEio = unchecked((int)0x80020005);

    internal const string AioWakeKey = "sceKernelAioWait";

    private readonly record struct AioTransfer(
        long Offset,
        ulong Length,
        ulong Buffer,
        ulong ResultAddress,
        int Fd);

    private sealed class AioRequest
    {
        public required int Id { get; init; }
        public required AioTransfer[] Transfers { get; init; }
        public required bool Write { get; init; }
        public required ICpuMemory Memory { get; init; }
        public long Sequence;

        // Guarded by _aioGate.
        public uint State = AioStateSubmitted;
        public bool Started;
        public bool CancelRequested;

        public bool Done => State is AioStateCompleted or AioStateAborted;
    }

    private sealed class AioWorker
    {
        public readonly Queue<AioRequest[]> Jobs = new();
        public Thread? Thread;
    }

    private static readonly object _aioGate = new();
    private static readonly Dictionary<int, AioRequest> _aioRequests = new();
    private static readonly AioWorker[] _aioWorkers = CreateAioWorkers();
    private static int _nextAioId;
    private static long _aioSequence;

    private static AioWorker[] CreateAioWorkers()
    {
        var workers = new AioWorker[AioPriorityCount];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = new AioWorker();
        }

        return workers;
    }

    [SysAbiExport(Nid = "HgX7+AORI58", ExportName = "sceKernelAioSubmitReadCommands",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitReadCommands(CpuContext ctx) => KernelAioSubmit(ctx, write: false, multiple: false);

    [SysAbiExport(Nid = "lXT0m3P-vs4", ExportName = "sceKernelAioSubmitReadCommandsMultiple",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitReadCommandsMultiple(CpuContext ctx) => KernelAioSubmit(ctx, write: false, multiple: true);

    [SysAbiExport(Nid = "XQ8C8y+de+E", ExportName = "sceKernelAioSubmitWriteCommands",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitWriteCommands(CpuContext ctx) => KernelAioSubmit(ctx, write: true, multiple: false);

    [SysAbiExport(Nid = "xT3Cpz0yh6Y", ExportName = "sceKernelAioSubmitWriteCommandsMultiple",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitWriteCommandsMultiple(CpuContext ctx) => KernelAioSubmit(ctx, write: true, multiple: true);

    // submit(SceKernelAioRWRequest* req, int size, int prio, SceKernelAioSubmitId* id):
    // the plain form returns one id for the batch, the Multiple form one id per request.
    private static int KernelAioSubmit(CpuContext ctx, bool write, bool multiple)
    {
        var requestsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((int)ctx[CpuRegister.Rsi]);
        var priority = Math.Clamp(unchecked((int)ctx[CpuRegister.Rdx]), 0, AioPriorityCount - 1);
        var idAddress = ctx[CpuRegister.Rcx];
        if (requestsAddress == 0 || idAddress == 0)
        {
            return SceAioEfault;
        }

        if (count <= 0 || count > AioMaxBatch)
        {
            return SceAioEinval;
        }

        var transfers = new AioTransfer[count];
        Span<byte> raw = stackalloc byte[SizeofAioRwRequest];
        for (var i = 0; i < count; i++)
        {
            if (!ctx.Memory.TryRead(requestsAddress + (ulong)(i * SizeofAioRwRequest), raw))
            {
                return SceAioEfault;
            }

            transfers[i] = new AioTransfer(
                BinaryPrimitives.ReadInt64LittleEndian(raw),
                BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(raw[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(raw[24..]),
                BinaryPrimitives.ReadInt32LittleEndian(raw[32..]));
            if (multiple && transfers[i].ResultAddress == 0)
            {
                return SceAioEfault;
            }
        }

        var memory = ctx.Memory;
        AioRequest[] job;
        lock (_aioGate)
        {
            var needed = multiple ? count : 1;
            if (!TryReserveAioCapacityLocked(needed))
            {
                return SceAioEagain;
            }

            job = new AioRequest[needed];
            for (var i = 0; i < needed; i++)
            {
                job[i] = new AioRequest
                {
                    Id = AllocateAioIdLocked(),
                    Transfers = multiple ? [transfers[i]] : transfers,
                    Write = write,
                    Memory = memory,
                    Sequence = ++_aioSequence,
                };
                _aioRequests[job[i].Id] = job[i];
            }
        }

        // The guest sees every result as submitted before the call returns.
        foreach (var transfer in transfers)
        {
            WriteAioResult(memory, transfer.ResultAddress, 0, AioStateSubmitted);
        }

        Span<byte> idBytes = stackalloc byte[sizeof(int)];
        for (var i = 0; i < job.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(idBytes, job[i].Id);
            if (!memory.TryWrite(idAddress + (ulong)(i * sizeof(int)), idBytes))
            {
                // The ids could not be returned; the guest cannot observe the batch.
                CancelUnpublishedAioJob(job);
                return SceAioEfault;
            }
        }

        EnqueueAioJob(priority, job);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void CancelUnpublishedAioJob(AioRequest[] job)
    {
        lock (_aioGate)
        {
            foreach (var request in job)
            {
                _aioRequests.Remove(request.Id);
            }
        }
    }

    private static bool TryReserveAioCapacityLocked(int needed)
    {
        if (_aioRequests.Count + needed <= AioMaxLiveRequests)
        {
            return true;
        }

        // Evict the oldest finished requests the guest never deleted.
        var finished = _aioRequests.Values
            .Where(request => request.Done)
            .OrderBy(request => request.Sequence)
            .Take(_aioRequests.Count + needed - AioMaxLiveRequests)
            .ToArray();
        foreach (var request in finished)
        {
            _aioRequests.Remove(request.Id);
        }

        return _aioRequests.Count + needed <= AioMaxLiveRequests;
    }

    private static int AllocateAioIdLocked()
    {
        while (true)
        {
            var id = Interlocked.Increment(ref _nextAioId) & int.MaxValue;
            if (id != 0 && !_aioRequests.ContainsKey(id))
            {
                return id;
            }
        }
    }

    private static void EnqueueAioJob(int priority, AioRequest[] job)
    {
        var worker = _aioWorkers[priority];
        lock (worker)
        {
            worker.Jobs.Enqueue(job);
            if (worker.Thread is null)
            {
                worker.Thread = new Thread(() => RunAioWorker(worker))
                {
                    IsBackground = true,
                    Name = $"SharpEmu aio p{priority}",
                };
                worker.Thread.Start();
            }

            Monitor.Pulse(worker);
        }
    }

    private static void RunAioWorker(AioWorker worker)
    {
        while (true)
        {
            AioRequest[] job;
            lock (worker)
            {
                while (worker.Jobs.Count == 0)
                {
                    Monitor.Wait(worker);
                }

                job = worker.Jobs.Dequeue();
            }

            foreach (var request in job)
            {
                ExecuteAioRequest(request);
            }
        }
    }

    private static void ExecuteAioRequest(AioRequest request)
    {
        bool cancelled;
        lock (_aioGate)
        {
            cancelled = request.CancelRequested;
            if (cancelled)
            {
                foreach (var transfer in request.Transfers)
                {
                    WriteAioResult(request.Memory, transfer.ResultAddress, SceAioEcanceled, AioStateAborted);
                }

                request.State = AioStateAborted;
                Monitor.PulseAll(_aioGate);
            }
            else
            {
                request.Started = true;
                request.State = AioStateProcessing;
            }
        }

        if (!cancelled)
        {
            var failed = false;
            foreach (var transfer in request.Transfers)
            {
                var returnValue = PerformAioTransfer(request.Memory, transfer, request.Write);
                failed |= returnValue < 0;
                WriteAioResult(
                    request.Memory,
                    transfer.ResultAddress,
                    returnValue,
                    returnValue < 0 ? AioStateAborted : AioStateCompleted);
            }

            lock (_aioGate)
            {
                // A batch fails as a whole when any of its requests failed.
                request.State = failed ? AioStateAborted : AioStateCompleted;
                Monitor.PulseAll(_aioGate);
            }
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(AioWakeKey);
    }

    private static long PerformAioTransfer(ICpuMemory memory, AioTransfer transfer, bool write)
    {
        if (transfer.Offset < 0 || transfer.Length > long.MaxValue)
        {
            return SceAioEinval;
        }

        var stream = GetOpenFile(transfer.Fd);
        if (stream is null)
        {
            return SceAioEbadf;
        }

        if (transfer.Length == 0)
        {
            return 0;
        }

        if (transfer.Buffer == 0)
        {
            return SceAioEfault;
        }

        var scratch = ArrayPool<byte>.Shared.Rent((int)Math.Min(transfer.Length, AioTransferChunk));
        try
        {
            var done = 0UL;
            while (done < transfer.Length)
            {
                var chunk = (int)Math.Min((ulong)scratch.Length, transfer.Length - done);
                var span = scratch.AsSpan(0, chunk);
                var position = transfer.Offset + (long)done;
                if (write)
                {
                    if (!memory.TryRead(transfer.Buffer + done, span))
                    {
                        return done == 0 ? SceAioEfault : (long)done;
                    }

                    WriteOpenFileAt(stream, span, position);
                    done += (ulong)chunk;
                    continue;
                }

                var read = ReadOpenFileAt(stream, span, position);
                if (read > 0 && !memory.TryWrite(transfer.Buffer + done, span[..read]))
                {
                    return done == 0 ? SceAioEfault : (long)done;
                }

                done += (ulong)Math.Max(read, 0);
                if (read < chunk)
                {
                    break;
                }
            }

            return (long)done;
        }
        catch (ObjectDisposedException)
        {
            return SceAioEbadf;
        }
        catch (IOException)
        {
            return SceAioEio;
        }
        catch (UnauthorizedAccessException)
        {
            return SceAioEbadf;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void WriteAioResult(ICpuMemory memory, ulong address, long returnValue, uint state)
    {
        if (address == 0)
        {
            return;
        }

        // return_value first, then state: a guest that sees the final state
        // also sees the value.
        Span<byte> value = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(value, returnValue);
        _ = memory.TryWrite(address, value);
        Thread.MemoryBarrier();
        Span<byte> stateBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(stateBytes, state);
        _ = memory.TryWrite(address + 8, stateBytes);
    }

    private static bool TryGetAioStateLocked(int id, out uint state)
    {
        if (_aioRequests.TryGetValue(id, out var request))
        {
            state = request.State;
            return true;
        }

        state = 0;
        return false;
    }

    private static bool IsAioDone(uint state) => state is AioStateCompleted or AioStateAborted;

    [SysAbiExport(Nid = "2pOuoWoCxdk", ExportName = "sceKernelAioPollRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioPollRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0)
        {
            return SceAioEfault;
        }

        uint state;
        lock (_aioGate)
        {
            if (!TryGetAioStateLocked(id, out state))
            {
                return SceAioEinval;
            }
        }

        return WriteAioState(ctx.Memory, stateAddress, state) ? 0 : SceAioEfault;
    }

    [SysAbiExport(Nid = "o7O4z3jwKzo", ExportName = "sceKernelAioPollRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioPollRequests(CpuContext ctx)
    {
        var result = ReadAioIds(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]), ctx[CpuRegister.Rdx], out var ids);
        if (result != 0 || ids.Length == 0)
        {
            return result;
        }

        var states = new uint[ids.Length];
        lock (_aioGate)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (!TryGetAioStateLocked(ids[i], out states[i]))
                {
                    return SceAioEinval;
                }
            }
        }

        return WriteAioStates(ctx.Memory, ctx[CpuRegister.Rdx], states) ? 0 : SceAioEfault;
    }

    // wait(SceKernelAioSubmitId id, int* state, SceKernelUseconds* usec)
    [SysAbiExport(Nid = "KOF-oJbQVvc", ExportName = "sceKernelAioWaitRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioWaitRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        var timeoutAddress = ctx[CpuRegister.Rdx];
        if (stateAddress == 0)
        {
            return SceAioEfault;
        }

        lock (_aioGate)
        {
            if (!TryGetAioStateLocked(id, out _))
            {
                return SceAioEinval;
            }
        }

        return WaitAio(ctx, [id], AioWaitAnd, timeoutAddress, stateAddress);
    }

    // wait(SceKernelAioSubmitId* ids, int num, int* states, uint mode, SceKernelUseconds* usec)
    [SysAbiExport(Nid = "lgK+oIWkJyA", ExportName = "sceKernelAioWaitRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioWaitRequests(CpuContext ctx)
    {
        var mode = unchecked((uint)ctx[CpuRegister.Rcx]);
        var statesAddress = ctx[CpuRegister.Rdx];
        var result = ReadAioIds(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]), statesAddress, out var ids);
        if (result != 0)
        {
            return result;
        }

        if (mode is not (AioWaitAnd or AioWaitOr))
        {
            return SceAioEinval;
        }

        if (ids.Length == 0)
        {
            return 0;
        }

        lock (_aioGate)
        {
            foreach (var id in ids)
            {
                if (!TryGetAioStateLocked(id, out _))
                {
                    return SceAioEinval;
                }
            }
        }

        return WaitAio(ctx, ids, mode, ctx[CpuRegister.R8], statesAddress);
    }

    private sealed class AioWaiter : IGuestThreadBlockWaiter
    {
        public required CpuContext Ctx { get; init; }
        public required int[] Ids { get; init; }
        public required ulong StateArrayAddress { get; init; }
        public required uint Mode { get; init; }
        public required ulong TimeoutAddress { get; init; }
        public required long DeadlineTimestamp { get; init; }
        private uint[]? _states;

        public bool TryWake()
        {
            lock (_aioGate)
            {
                if (_states is not null)
                {
                    return true;
                }

                if (TryCompleteAioWaitLocked(Ids, Mode, out var states))
                {
                    _states = states;
                    return true;
                }

                return false;
            }
        }

        public int Resume()
        {
            uint[] states;
            var satisfied = _states is not null;
            lock (_aioGate)
            {
                if (!satisfied)
                {
                    satisfied = TryCompleteAioWaitLocked(Ids, Mode, out states);
                }
                else
                {
                    states = _states!;
                }
            }

            return FinishAioWait(Ctx.Memory, Ids, StateArrayAddress, states, satisfied, TimeoutAddress, DeadlineTimestamp);
        }
    }

    private static int WaitAio(
        CpuContext ctx,
        int[] ids,
        uint mode,
        ulong timeoutAddress,
        ulong stateArrayAddress)
    {
        long deadline = 0;
        var hasTimeout = false;
        if (timeoutAddress != 0)
        {
            Span<byte> timeoutBytes = stackalloc byte[sizeof(uint)];
            if (!ctx.Memory.TryRead(timeoutAddress, timeoutBytes))
            {
                return SceAioEfault;
            }

            hasTimeout = true;
            deadline = GuestThreadExecution.ComputeDeadlineTimestamp(
                TimeSpan.FromMicroseconds(BinaryPrimitives.ReadUInt32LittleEndian(timeoutBytes)));
        }

        uint[] states;
        bool satisfied;
        lock (_aioGate)
        {
            satisfied = TryCompleteAioWaitLocked(ids, mode, out states);
        }

        if (satisfied || (hasTimeout && deadline <= Stopwatch.GetTimestamp()))
        {
            return FinishAioWait(ctx.Memory, ids, stateArrayAddress, states, satisfied, timeoutAddress, deadline);
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelAioWaitRequest",
                AioWakeKey,
                new AioWaiter
                {
                    Ctx = ctx,
                    Ids = ids,
                    StateArrayAddress = stateArrayAddress,
                    Mode = mode,
                    TimeoutAddress = timeoutAddress,
                    DeadlineTimestamp = deadline,
                },
                deadline))
        {
            return 0;
        }

        lock (_aioGate)
        {
            while (!(satisfied = TryCompleteAioWaitLocked(ids, mode, out states)))
            {
                if (!hasTimeout)
                {
                    Monitor.Wait(_aioGate);
                    continue;
                }

                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    break;
                }

                Monitor.Wait(_aioGate, TimeSpan.FromSeconds(Math.Max((double)remaining / Stopwatch.Frequency, 0.001)));
            }
        }

        return FinishAioWait(ctx.Memory, ids, stateArrayAddress, states, satisfied, timeoutAddress, deadline);
    }

    private static bool TryCompleteAioWaitLocked(int[] ids, uint mode, out uint[] states)
    {
        states = new uint[ids.Length];
        var done = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            if (!TryGetAioStateLocked(ids[i], out states[i]))
            {
                // Deleted while waited on: nothing will change it any more.
                states[i] = AioStateAborted;
            }

            if (IsAioDone(states[i]))
            {
                done++;
            }
        }

        return mode == AioWaitOr ? done > 0 : done == ids.Length;
    }

    private static int FinishAioWait(
        ICpuMemory memory,
        int[] ids,
        ulong stateArrayAddress,
        uint[] states,
        bool satisfied,
        ulong timeoutAddress,
        long deadline)
    {
        if (!WriteAioStates(memory, stateArrayAddress, states))
        {
            return SceAioEfault;
        }

        // The timeout is an input only; it is not updated with the remaining time.
        return satisfied ? 0 : SceAioEtimedout;
    }

    [SysAbiExport(Nid = "fR521KIGgb8", ExportName = "sceKernelAioCancelRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioCancelRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0)
        {
            return SceAioEfault;
        }

        uint state;
        lock (_aioGate)
        {
            if (!TryCancelAioLocked(id, out state))
            {
                return SceAioEinval;
            }
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(AioWakeKey);
        return WriteAioState(ctx.Memory, stateAddress, state) ? 0 : SceAioEfault;
    }

    [SysAbiExport(Nid = "3Lca1XBrQdY", ExportName = "sceKernelAioCancelRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioCancelRequests(CpuContext ctx)
    {
        var statesAddress = ctx[CpuRegister.Rdx];
        var result = ReadAioIds(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]), statesAddress, out var ids);
        if (result != 0 || ids.Length == 0)
        {
            return result;
        }

        var states = new uint[ids.Length];
        lock (_aioGate)
        {
            foreach (var id in ids)
            {
                if (id != 0 && !TryGetAioStateLocked(id, out _))
                {
                    return SceAioEinval;
                }
            }

            for (var i = 0; i < ids.Length; i++)
            {
                _ = TryCancelAioLocked(ids[i], out states[i]);
            }
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(AioWakeKey);
        return WriteAioStates(ctx.Memory, statesAddress, states) ? 0 : SceAioEfault;
    }

    /// <summary>
    /// A request that has not started is aborted (its results report
    /// ECANCELED); one being transferred cannot be stopped and reports
    /// PROCESSING; a finished one is reported ABORTED from then on. Id 0 names
    /// no request and reports PROCESSING.
    /// </summary>
    private static bool TryCancelAioLocked(int id, out uint state)
    {
        if (id == 0)
        {
            state = AioStateProcessing;
            return true;
        }

        if (!_aioRequests.TryGetValue(id, out var request))
        {
            state = 0;
            return false;
        }

        if (request.Done)
        {
            request.State = AioStateAborted;
        }
        else if (!request.Started)
        {
            request.CancelRequested = true;
            request.State = AioStateAborted;
            Monitor.PulseAll(_aioGate);
        }

        state = request.State;
        return true;
    }

    // delete(SceKernelAioSubmitId id, int* ret)
    [SysAbiExport(Nid = "5TgME6AYty4", ExportName = "sceKernelAioDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioDeleteRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var retAddress = ctx[CpuRegister.Rsi];
        if (retAddress == 0)
        {
            return SceAioEfault;
        }

        int ret;
        lock (_aioGate)
        {
            if (!TryGetAioStateLocked(id, out _))
            {
                return SceAioEinval;
            }

            ret = DeleteAioLocked(id);
        }

        return WriteAioStates(ctx.Memory, retAddress, [unchecked((uint)ret)]) ? 0 : SceAioEfault;
    }

    [SysAbiExport(Nid = "Ft3EtsZzAoY", ExportName = "sceKernelAioDeleteRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioDeleteRequests(CpuContext ctx)
    {
        var retsAddress = ctx[CpuRegister.Rdx];
        var result = ReadAioIds(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]), retsAddress, out var ids);
        if (result != 0 || ids.Length == 0)
        {
            return result;
        }

        var rets = new uint[ids.Length];
        lock (_aioGate)
        {
            foreach (var id in ids)
            {
                if (!TryGetAioStateLocked(id, out _))
                {
                    return SceAioEinval;
                }
            }

            for (var i = 0; i < ids.Length; i++)
            {
                rets[i] = unchecked((uint)DeleteAioLocked(ids[i]));
            }
        }

        return WriteAioStates(ctx.Memory, retsAddress, rets) ? 0 : SceAioEfault;
    }

    /// <summary>
    /// Frees a request. A pending one is cancelled first; one being transferred
    /// stays until it finishes and the delete reports EBUSY.
    /// </summary>
    private static int DeleteAioLocked(int id)
    {
        if (!_aioRequests.TryGetValue(id, out var request))
        {
            return 0;
        }

        if (!request.Done && request.Started)
        {
            return SceAioEbusy;
        }

        if (!request.Started)
        {
            request.CancelRequested = true;
            request.State = AioStateAborted;
        }

        _aioRequests.Remove(id);

        Monitor.PulseAll(_aioGate);
        return 0;
    }

    // setParam(SceKernelAioSchedulingParam* param, int window, int delayed,
    //          uint enableSplit, uint splitSize, uint splitChunkSize)
    [SysAbiExport(Nid = "9WK-vhNXimw", ExportName = "sceKernelAioSetParam",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var window = unchecked((int)ctx[CpuRegister.Rsi]);
        var delayed = unchecked((int)ctx[CpuRegister.Rdx]);
        var enableSplit = unchecked((uint)ctx[CpuRegister.Rcx]);
        var splitSize = unchecked((uint)ctx[CpuRegister.R8]);
        var splitChunkSize = unchecked((uint)ctx[CpuRegister.R9]);
        if (paramAddress == 0)
        {
            return SceAioEfault;
        }

        if (window is < 0 or > 128 || delayed is < 0 or > 128 || enableSplit > 1 ||
            splitSize > 0x1000000 || splitChunkSize > 0x1000000)
        {
            return SceAioEinval;
        }

        Span<byte> param = stackalloc byte[SizeofAioSchedulingParam];
        BinaryPrimitives.WriteInt32LittleEndian(param, window);
        BinaryPrimitives.WriteInt32LittleEndian(param[4..], delayed);
        BinaryPrimitives.WriteUInt32LittleEndian(param[8..], enableSplit);
        BinaryPrimitives.WriteUInt32LittleEndian(param[12..], splitSize);
        BinaryPrimitives.WriteUInt32LittleEndian(param[16..], splitChunkSize);
        return ctx.Memory.TryWrite(paramAddress, param) ? 0 : SceAioEfault;
    }

    private static int ReadAioIds(CpuContext ctx, ulong idsAddress, int count, ulong outAddress, out int[] ids)
    {
        ids = [];
        if (count < 0 || count > AioMaxBatch)
        {
            return SceAioEinval;
        }

        if (count == 0)
        {
            return 0;
        }

        if (idsAddress == 0 || outAddress == 0)
        {
            return SceAioEfault;
        }

        var bytes = new byte[count * sizeof(int)];
        if (!ctx.Memory.TryRead(idsAddress, bytes))
        {
            return SceAioEfault;
        }

        ids = new int[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * sizeof(int)));
        }

        return 0;
    }

    private static bool WriteAioState(ICpuMemory memory, ulong address, uint state) =>
        WriteAioStates(memory, address, [state]);

    private static bool WriteAioStates(ICpuMemory memory, ulong address, uint[] values)
    {
        var bytes = new byte[values.Length * sizeof(uint)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), values[i]);
        }

        return memory.TryWrite(address, bytes);
    }
}
