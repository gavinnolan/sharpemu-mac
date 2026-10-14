// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE.DsTrace;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The packet a handler executes: header fields plus its position in the cursor.
public readonly record struct PacketContext(uint Header, ulong PacketAddress, uint Offset, uint Remaining, uint Total)
{
    public uint Length => PacketHeader.Length(Header);

    public uint Opcode => PacketHeader.Opcode(Header);

    public uint CustomCode => PacketHeader.CustomCode(Header);
}

public readonly record struct FlipRequest(int Handle, int Index, int FlipMode, long FlipArgument);

// Parses one queue's command stream and keeps that queue's register and packet state.
public sealed partial class GpuCommandInterpreter
{
    public const int ComputeQueueBase = 0x20;
    public const uint ConstantRamDwords = 0x3000;
    public const uint RingChunkBytes = 0x10000;

    // SHARPEMU_GPU_STRICT_HEADERS: fail at the first header that is not a type-3 packet.
    internal bool StrictHeaders { get; set; } = GpuStreamDiagnostics.StrictHeaders;

    private readonly ICommandStreamHost _host;
    private readonly List<uint[]> _payloadScratchByDepth = new();
    private readonly uint[] _constantRam = new uint[ConstantRamDwords];
    private PacketCursorStack? _execution;
    private bool _chainRequested;
    private ulong _packetSerial;

    // DSTRACE §4.7 SLICE: packets run so far, read by the queue around a slice.
    internal ulong DsPacketSerial => _packetSerial;
    private ulong _lastWriteAddress;
    private ulong _lastWriteValue;
    private int _lastWriteLength;
    private ulong _lastWritePacket;

    // The last packets this interpreter walked, so a stream that leaves its
    // packet boundaries says how it got there instead of only where it stopped.
    private readonly PacketTraceEntry[] _packetTrace = new PacketTraceEntry[32];
    private int _packetTraceCount;

    private struct PacketTraceEntry
    {
        public ulong Serial;
        public ulong Address;
        public uint Offset;
        public uint Header;
        public uint Length;
        public uint Consumed;
        public int Depth;
        public PacketTraceOutcome Outcome;
    }

    private enum PacketTraceOutcome : byte
    {
        Advanced,
        Filler,
        Predicated,
        DeferredAdvance,
        Suspended,
        Chained,
        Popped,
    }

    private void Trace(PacketTraceOutcome outcome, in PacketCursor cursor, uint offset,
        uint header, uint length, uint consumed, int depth)
    {
        _packetTrace[_packetTraceCount++ % _packetTrace.Length] = new PacketTraceEntry
        {
            Serial = _packetSerial,
            Address = cursor.Address,
            Offset = offset,
            Header = header,
            Length = length,
            Consumed = consumed,
            Depth = depth,
            Outcome = outcome,
        };
    }

    // An unknown opcode means either this interpreter walked off the packet grid
    // or the words under the cursor are not the ones it was handed. Reading the
    // header again separates the two: a guest that rebuilds a command buffer it
    // has already submitted changes it between two reads microseconds apart, and
    // the trace above then describes a stream that no longer exists.
    private string DescribeConcurrentRewrite(ulong packetAddress, uint header)
    {
        Span<byte> word = stackalloc byte[sizeof(uint)];
        if (!_host.TryReadGuest(packetAddress, word))
        {
            return string.Empty;
        }

        var reread = BinaryPrimitives.ReadUInt32LittleEndian(word);
        return reread == header
            ? string.Empty
            : $" The guest rewrote this packet while it was being read: header=0x{header:X8} is now 0x{reread:X8}.";
    }

    private void DumpPacketTrace()
    {
        var count = Math.Min(_packetTraceCount, _packetTrace.Length);
        Console.Error.WriteLine($"[GPU][FATAL] The last {count} packets this queue walked, oldest first:");
        var first = _packetTraceCount - count;
        for (var index = first; index < _packetTraceCount; index++)
        {
            ref var entry = ref _packetTrace[index % _packetTrace.Length];
            Console.Error.WriteLine(
                $"\tserial={entry.Serial} depth={entry.Depth} buffer=0x{entry.Address:X16} " +
                $"offset=0x{entry.Offset:X5} header=0x{entry.Header:X8} length={entry.Length} " +
                $"consumed={entry.Consumed} {entry.Outcome}");
        }
    }

    public GpuCommandInterpreter(ICommandStreamHost host, int queueId, int interruptEventId)
    {
        _host = host;
        QueueId = queueId;
        InterruptEventId = interruptEventId;
        TypedRegisters = new RegisterBanks(host.Fatal);
    }

    public int QueueId { get; }

    public int InterruptEventId { get; }

    public bool IsComputeQueue => InterruptEventId >= ComputeQueueBase;

    public CommandRegisterBanks Registers { get; } = new();

    // The typed banks the render executor reads; the dictionaries feed the translation they replace.
    public RegisterBanks TypedRegisters { get; }

    public ulong SubmitId { get; set; }

    public uint IndexTypeAndSize { get; private set; }

    public uint IndexBufferSize { get; private set; }

    public ulong IndexBaseAddress { get; private set; }

    public ulong DrawIndirectArgumentsBase { get; private set; }

    public ulong DispatchIndirectArgumentsBase { get; private set; }

    // Persistent draw state: indirect draws update it for later draws. An indirect draw
    // resolved on the GPU leaves the count in guest memory; it is read only if a later
    // draw needs it.
    public uint InstanceCount
    {
        get
        {
            if (_deferredInstanceCountAddress != 0)
            {
                _instanceCount = ReadDword(_deferredInstanceCountAddress);
                _deferredInstanceCountAddress = 0;
            }

            return _instanceCount;
        }
        private set
        {
            _instanceCount = value;
            _deferredInstanceCountAddress = 0;
        }
    }

    private uint _instanceCount = 1;
    private ulong _deferredInstanceCountAddress;

    public uint DrawIndexOffset { get; private set; }

    public uint UserDataMarker { get; private set; }

    public bool PredicateSkip { get; private set; }

    public bool ConditionalWaitEnabled { get; private set; }

    public FlipRequest PendingFlip { get; private set; }

    public uint ConstantEngineCount { get; private set; }

    public uint DrawEngineCount { get; private set; }

    public bool ConstantEngineComplete { get; set; }

    public ulong SyntheticOcclusionCounter { get; private set; }

    public ulong AtomicReturnMeData { get; private set; }

    public bool AtomicReturnMeValid { get; private set; }

    public ulong AtomicReturnPfpData { get; private set; }

    public bool AtomicReturnPfpValid { get; private set; }

    public ReadOnlySpan<uint> ConstantRam => _constantRam;

    public void Reset()
    {
        _lastWriteLength = 0;
        Registers.Reset();
        TypedRegisters.Reset();
        IndexTypeAndSize = 0;
        IndexBufferSize = 0;
        UserDataMarker = 0;
        DrawIndirectArgumentsBase = 0;
        DispatchIndirectArgumentsBase = 0;
        Array.Clear(_constantRam);
    }

    // The queue-reset packets also drop the index base, the instance count and the wait condition.
    public void ResetForQueueReset()
    {
        Reset();
        IndexBaseAddress = 0;
        InstanceCount = 1;
        DrawIndexOffset = 0;
        ConditionalWaitEnabled = false;
        _host.OnQueueReset(QueueId);
    }

    public void ResetCounters()
    {
        DrawEngineCount = 0;
        ConstantEngineCount = 0;
        ConstantEngineComplete = false;
    }

    public void SetFlip(FlipRequest flip) => PendingFlip = flip;

    public SubmissionProgress Process(PacketCursorStack execution, ulong address, uint dwordCount)
    {
        if (_execution is not null)
        {
            throw _host.Fatal($"The command interpreter is already running a stream: queue={QueueId}.");
        }

        if (execution.IsEmpty && dwordCount != 0)
        {
            execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address));
        }

        execution.Suspended = false;
        execution.SuspendCondition = CommandWaitCondition.Unknown;
        execution.MadeProgress = false;
        // Forwarding is local to this slice, never a cached signal from an older submission.
        _lastWriteLength = 0;
        _execution = execution;
        try
        {
            RunPackets(execution, 0);
        }
        finally
        {
            _execution = null;
        }

        return execution.IsEmpty ? SubmissionProgress.Complete : SubmissionProgress.Blocked;
    }

    // Runs a nested buffer to completion before the calling packet finishes.
    public void RunIndirectBuffer(ulong address, uint dwordCount)
    {
        var execution = RequireExecution();
        if (dwordCount == 0)
        {
            return;
        }

        if (execution.Depth >= PacketCursorStack.MaxDepth)
        {
            throw _host.Fatal($"The indirect buffer nesting is too deep: depth={execution.Depth} address=0x{address:X16} dwords={dwordCount}.");
        }

        var stopDepth = execution.Depth;
        execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address));
        RunPackets(execution, stopDepth);
    }

    // Replaces the current buffer with another one at the same depth; the chaining packet ends its buffer.
    private void ChainToBuffer(ulong address, uint dwordCount, bool followedChunkAdvance = false)
    {
        var execution = RequireExecution();
        execution.Pop();
        execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address, followedChunkAdvance));
        _chainRequested = true;
    }

    public void Suspend() => SuspendOn(CommandWaitCondition.Unknown);

    /// <summary>
    /// Suspends this submission and records what it is waiting for, so the queue can
    /// wake it when that alone changes instead of re-running every blocked head.
    /// </summary>
    public void SuspendOn(in CommandWaitCondition condition)
    {
        var execution = RequireExecution();
        execution.Suspended = true;
        execution.SuspendCondition = condition;
        if (DsTrace.Gpu)
        {
            DsEmitSuspend(in condition);
        }
    }

    // DSTRACE §4.7 CPS: the compare operands of the memory wait about to suspend. Only the
    // queue's own (worker) thread runs this interpreter, so plain fields suffice.
    private bool _dsHasWaitOperands;
    private ulong _dsWaitReference;
    private ulong _dsWaitMask;
    private uint _dsWaitFunction;
    private ulong _dsWaitCurrent;

    private void DsNoteWaitOperands(ulong reference, ulong mask, uint function, ulong current)
    {
        _dsWaitReference = reference;
        _dsWaitMask = mask;
        _dsWaitFunction = function;
        _dsWaitCurrent = current;
        _dsHasWaitOperands = true;
    }

    private void DsEmitSuspend(in CommandWaitCondition condition)
    {
        var hasOperands = _dsHasWaitOperands;
        _dsHasWaitOperands = false;
        if (!DsTrace.Armed)
        {
            return;
        }

        DsTrace.Emit(
            DsEv.CPS,
            (ulong)QueueId,
            (ulong)condition.Kind,
            condition.Address,
            hasOperands ? _dsWaitReference : 0,
            hasOperands ? _dsWaitMask : 0,
            hasOperands ? _dsWaitFunction : 0,
            hasOperands ? _dsWaitCurrent : 0);
    }

    // A paired wait must still observe this queue's immediate label store when the CPU
    // resets the live address for the next frame between the two packets (Dead Cells).
    private bool TryReadOwnWrite(ulong address, bool is64Bit, out ulong value)
    {
        value = 0;
        if (address != _lastWriteAddress || _packetSerial - _lastWritePacket > 2 ||
            _lastWriteLength < (is64Bit ? sizeof(ulong) : sizeof(uint)))
        {
            return false;
        }
        value = is64Bit ? _lastWriteValue : (uint)_lastWriteValue;
        return true;
    }

    private PacketCursorStack RequireExecution() =>
        _execution ?? throw _host.Fatal($"No command stream is running: queue={QueueId}.");

    private void RunPackets(PacketCursorStack execution, int stopDepth)
    {
        while (execution.Depth > stopDepth)
        {
            _host.RunPendingCommands();
            var cursorIndex = execution.Depth - 1;
            ref var cursor = ref execution.At(cursorIndex);
            if (cursor.Offset > cursor.DwordCount)
            {
                DumpPacketTrace();
                throw _host.Fatal($"The packet cursor is past its buffer: offset={cursor.Offset} dwords={cursor.DwordCount} address=0x{cursor.Address:X16}.");
            }

            if (cursor.DeferredAdvance != 0)
            {
                if (cursor.DeferredAdvance > cursor.Remaining)
                {
                    DumpPacketTrace();
                    throw _host.Fatal($"The deferred advance is past the buffer: advance={cursor.DeferredAdvance} remaining={cursor.Remaining} address=0x{cursor.Address:X16}.");
                }

                Trace(PacketTraceOutcome.DeferredAdvance, in cursor, cursor.Offset, 0, 0,
                    cursor.DeferredAdvance, cursorIndex);
                cursor.Offset += cursor.DeferredAdvance;
                cursor.DeferredAdvance = 0;
                execution.MadeProgress = true;
                continue;
            }

            if (cursor.Offset == cursor.DwordCount)
            {
                Trace(PacketTraceOutcome.Popped, in cursor, cursor.Offset, 0, 0, 0, cursorIndex);
                execution.Pop();
                continue;
            }

            var packetAddress = cursor.Address + ((ulong)cursor.Offset * sizeof(uint));
            // The header and the body of a packet are consecutive, and almost every
            // packet is short, so one read of a small window covers both. Reading the
            // header on its own first doubled the number of guest reads the command
            // stream makes, and a guest read is not free: the backend has to decide
            // whether the GPU may still be writing those bytes.
            var window = ReadPacketWindow(cursorIndex, packetAddress, cursor.Remaining);
            var header = window[0];
            if (PacketProfile.Enabled)
            {
                PacketProfile.RecordHeaderRead();
            }
            _packetSerial++;
            var total = cursor.DwordCount;
            var remaining = cursor.Remaining;
            var offset = cursor.Offset;

            if (header == PacketHeader.FillerHeader)
            {
                Trace(PacketTraceOutcome.Filler, in cursor, offset, header, 1, 1, cursorIndex);
                cursor.Offset++;
                execution.MadeProgress = true;
                continue;
            }

            // Ring memory the guest has not written yet; retry after it appends more.
            if (header == 0 && cursor.FollowedChunkAdvance)
            {
                GpuStreamDiagnostics.RecordRingSuspend();
                execution.Suspended = true;
                execution.SuspendCondition = new CommandWaitCondition(CommandWaitKind.RingAppend);
                return;
            }

            if (StrictHeaders && PacketHeader.PacketType(header) != 3)
            {
                ReportStreamContext("Strict header check");
                throw _host.Fatal(
                    $"The packet header is not type 3: offset=0x{offset:X5} header=0x{header:X8} " +
                    $"type={PacketHeader.PacketType(header)} address=0x{packetAddress:X16}.");
            }

            if (remaining < 2)
            {
                DumpPacketTrace();
                throw _host.Fatal($"The packet is shorter than two dwords: offset=0x{offset:X5} header=0x{header:X8} address=0x{packetAddress:X16}.");
            }

            var length = PacketHeader.Length(header);
            if (length > remaining)
            {
                DumpPacketTrace();
                throw _host.Fatal($"The packet is longer than the buffer: offset=0x{offset:X5} header=0x{header:X8} length={length} remaining={remaining} address=0x{packetAddress:X16}.");
            }

            if (PacketHeader.IsPredicated(header) && PredicateSkip)
            {
                Trace(PacketTraceOutcome.Predicated, in cursor, offset, header, length, length, cursorIndex);
                cursor.Offset += length;
                execution.MadeProgress = true;
                continue;
            }

            var opcode = PacketHeader.Opcode(header);
            // Only a wait or an inert NOP/marker may separate the store and its paired wait.
            // Draws, dispatches and transfers may produce a newer GPU value at the same address.
            if (opcode is not (PacketOpcode.WaitRegisterMemory or PacketOpcode.WaitRegisterMemory64) &&
                !(opcode == PacketOpcode.Nop && PacketHeader.CustomCode(header) is
                    0 or PacketCustomCode.PushMarker or PacketCustomCode.PopMarker or
                    PacketCustomCode.WaitMemory32 or PacketCustomCode.WaitMemory64))
            {
                _lastWriteLength = 0;
            }
            var handler = PacketDispatchTable.Opcodes[opcode];
            if (handler is null)
            {
                DumpUnknownPacket(cursor, offset);
                DumpPacketTrace();
                throw _host.Fatal(
                    $"The packet opcode is unknown: offset=0x{offset:X5} header=0x{header:X8} " +
                    $"opcode=0x{opcode:X2} address=0x{packetAddress:X16}.{DescribeConcurrentRewrite(packetAddress, header)}");
            }

            // The window already holds the body unless the packet is a long one.
            var payload = length <= (uint)window.Length
                ? window.Slice(1, (int)(length - 1))
                : ReadPayload(cursorIndex, packetAddress, length - 1);
            var packet = new PacketContext(header & ~1u, packetAddress, offset, remaining, total);
            uint consumed;
            if (PacketProfile.Enabled)
            {
                var packetStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                consumed = handler(this, in packet, payload) + 1;
                PacketProfile.RecordPacket(opcode, System.Diagnostics.Stopwatch.GetTimestamp() - packetStarted);
            }
            else
            {
                consumed = handler(this, in packet, payload) + 1;
            }

            // The header's count is the size of the packet, so a handler that
            // reports fewer dwords than its own header declares has not consumed
            // the whole packet: advancing by its count would resume inside the
            // body and read an operand as the next header. Several handlers
            // return a fixed size for the variant they know (the register NOP
            // offsets, the semaphore, the atomic), and a longer encoding of the
            // same packet would walk the stream off its boundaries from there on.
            // COND_EXEC is the one packet that reports more on purpose, because
            // it skips the packets it covers.
            if (consumed < length)
            {
                consumed = length;
            }

            if (consumed > remaining)
            {
                DumpPacketTrace();
                throw _host.Fatal($"The handler consumed more than the buffer holds: consumed={consumed} remaining={remaining} header=0x{header:X8} address=0x{packetAddress:X16}.");
            }

            if (execution.Suspended)
            {
                Trace(PacketTraceOutcome.Suspended, in execution.At(cursorIndex), offset, header,
                    length, consumed, cursorIndex);
                if (execution.Depth > cursorIndex + 1 && !_chainRequested)
                {
                    execution.At(cursorIndex).DeferredAdvance = consumed;
                }

                _chainRequested = false;
                return;
            }

            if (_chainRequested)
            {
                Trace(PacketTraceOutcome.Chained, in execution.At(cursorIndex), offset, header,
                    length, consumed, cursorIndex);
                _chainRequested = false;
                execution.MadeProgress = true;
                continue;
            }

            if (execution.Depth != cursorIndex + 1)
            {
                throw _host.Fatal($"The cursor stack changed under a packet: depth={execution.Depth} expected={cursorIndex + 1} header=0x{header:X8}.");
            }

            Trace(PacketTraceOutcome.Advanced, in execution.At(cursorIndex), offset, header,
                length, consumed, cursorIndex);
            execution.At(cursorIndex).Offset += consumed;
            execution.MadeProgress = true;
        }
    }

    // The payload lives in a per-depth scratch buffer for the handler call only.
    // How much of a packet is read in one go. Long packets (large register writes,
    // constant-RAM dumps) fall back to a second read for the rest of their body.
    private const uint PacketWindowDwords = 16;

    // The header and as much of the body as the window holds, in one guest read.
    private ReadOnlySpan<uint> ReadPacketWindow(int depth, ulong packetAddress, uint remaining)
    {
        var dwords = Math.Min(remaining, PacketWindowDwords);
        while (_payloadScratchByDepth.Count <= depth)
        {
            _payloadScratchByDepth.Add(new uint[PacketHeader.MaxLength]);
        }

        var scratch = _payloadScratchByDepth[depth].AsSpan(0, (int)dwords);
        RenderPhaseProfile.RecordCommandRead(
            RenderPhaseProfile.CommandReadKind.Header, scratch.Length * sizeof(uint));
        if (!_host.TryReadGuest(packetAddress, MemoryMarshal.AsBytes(scratch)))
        {
            throw _host.Fatal(
                $"The command stream cannot read a packet header: address=0x{packetAddress:X16} dwords={dwords}.");
        }

        return scratch;
    }

    private ReadOnlySpan<uint> ReadPayload(int depth, ulong packetAddress, uint payloadDwords)
    {
        while (_payloadScratchByDepth.Count <= depth)
        {
            _payloadScratchByDepth.Add(new uint[PacketHeader.MaxLength]);
        }

        var scratch = _payloadScratchByDepth[depth].AsSpan(0, (int)payloadDwords);
        if (payloadDwords != 0 && PacketProfile.Enabled)
        {
            PacketProfile.RecordPayloadRead(payloadDwords);
        }

        if (payloadDwords != 0)
        {
            RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Payload, scratch.Length * sizeof(uint));
        }
        if (payloadDwords != 0 && !_host.TryReadGuest(packetAddress + sizeof(uint), MemoryMarshal.AsBytes(scratch)))
        {
            throw _host.Fatal($"The command stream cannot read a packet: address=0x{packetAddress:X16} dwords={payloadDwords + 1}.");
        }

        return scratch;
    }

    private void DumpUnknownPacket(in PacketCursor cursor, uint offset)
    {
        var begin = offset > 8 ? offset - 8 : 0;
        var end = Math.Min(cursor.DwordCount, offset + 16);
        Console.Error.WriteLine($"[GPU][FATAL] The packet stream near the unknown packet: buffer=0x{cursor.Address:X16} dwords={cursor.DwordCount} offset=0x{offset:X5}");
        Span<byte> word = stackalloc byte[sizeof(uint)];
        for (var index = begin; index < end; index++)
        {
            var text = _host.TryReadGuest(cursor.Address + ((ulong)index * sizeof(uint)), word)
                ? BinaryPrimitives.ReadUInt32LittleEndian(word).ToString("X8")
                : "????????";
            Console.Error.WriteLine($"\t{index:X5}{(index == offset ? ":" : " ")} {text}");
        }
    }

    internal uint ReadDword(ulong address, RenderPhaseProfile.CommandReadKind readKind = RenderPhaseProfile.CommandReadKind.Operand32)
    {
        RenderPhaseProfile.RecordCommandRead(readKind, sizeof(uint));
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        // The packets themselves are command data; anything else is an operand earlier work may write.
        var read = readKind is RenderPhaseProfile.CommandReadKind.Header or RenderPhaseProfile.CommandReadKind.Payload or
            RenderPhaseProfile.CommandReadKind.RegisterTable
            ? _host.TryReadGuest(address, bytes)
            : _host.TryReadGuestOperand(address, bytes);
        if (!read)
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size=4.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal ulong ReadQword(ulong address)
    {
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Operand64, sizeof(ulong));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (!_host.TryReadGuestOperand(address, bytes))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size=8.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    /// <summary>
    /// Reads the bytes a wait packet polls, as they are in memory right now.
    /// </summary>
    /// <remarks>
    /// A poll is not an ordinary read: the hardware's command processor reads what memory
    /// holds and stalls on the value it finds, so waiting here for work that may be about
    /// to write those bytes only moves the stall onto the worker thread. An unmet compare
    /// suspends the submission instead, and the host wakes it when the write lands.
    /// </remarks>
    private ulong ReadWaitOperand(ulong address, bool is64Bit, bool commitPendingWrite)
    {
        RenderPhaseProfile.RecordCommandRead(is64Bit ? RenderPhaseProfile.CommandReadKind.Operand64 : RenderPhaseProfile.CommandReadKind.Operand32,
            is64Bit ? sizeof(ulong) : sizeof(uint));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        var size = is64Bit ? sizeof(ulong) : sizeof(uint);
        if (!_host.TryReadGuestWaitOperand(address, bytes[..size], commitPendingWrite))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size={size}.");
        }

        return is64Bit ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal void ReadBytes(ulong address, Span<byte> destination)
    {
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Other, destination.Length);
        if (!_host.TryReadGuestOperand(address, destination))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size={destination.Length}.");
        }
    }

    internal void WriteDword(ulong address, uint value, string kind = "WRITE32")
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteBytes(address, bytes, kind);
    }

    internal void WriteQword(ulong address, ulong value, string kind = "WRITE64")
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteBytes(address, bytes, kind);
    }

    /// <summary>
    /// Told when this interpreter writes guest memory, so the queue can wake exactly
    /// the submissions whose compare reads those bytes. The queue sets it.
    /// </summary>
    internal Action<ulong, ulong>? GuestMemoryWritten { get; init; }

    /// <summary>Told when this interpreter defers a label to the GPU; the queue keeps it
    /// visible to other queues' waits until it lands.</summary>
    internal Action<ulong, int, ulong>? PendingLabelRecorded { get; init; }

    internal PendingLabelReader? PendingLabelLookup { get; init; }

    internal delegate bool PendingLabelReader(ulong address, bool is64Bit, out ulong value);

    internal void WriteBytes(ulong address, ReadOnlySpan<byte> source, string kind = "WRITE_BYTES")
    {
        if (!_host.TryWriteGuest(address, source, kind))
        {
            throw _host.Fatal($"The command stream cannot write guest memory: address=0x{address:X16} size={source.Length}.");
        }

        GuestMemoryWritten?.Invoke(address, (ulong)source.Length);
        if (source.Length is sizeof(uint) or sizeof(ulong))
        {
            CommandProcessorWriteHistory.Record(QueueId, address, source.Length,
                source.Length == sizeof(uint)
                    ? BinaryPrimitives.ReadUInt32LittleEndian(source)
                    : BinaryPrimitives.ReadUInt64LittleEndian(source),
                kind, deferred: false);
        }

        _lastWriteLength = 0;
        if (source.Length is sizeof(uint) or sizeof(ulong))
        {
            _lastWriteAddress = address;
            _lastWriteValue = source.Length == sizeof(uint)
                ? BinaryPrimitives.ReadUInt32LittleEndian(source)
                : BinaryPrimitives.ReadUInt64LittleEndian(source);
            _lastWriteLength = source.Length;
            _lastWritePacket = _packetSerial;
        }
    }

    private static ulong Address(uint low, uint high) => low | ((ulong)high << 32);
}
