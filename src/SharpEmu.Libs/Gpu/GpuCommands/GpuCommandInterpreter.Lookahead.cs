// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The pipelines of the dispatches after one that suspended on its own. A dispatch finds its
// pipeline missing only when it reaches it, and waits for that one compile before the next
// dispatch can find the next one missing: a scene that brings in a few dozen new compute programs
// (Black Myth: Wukong's fight effects) compiled them one after another, a second or more each, with
// the frame stopped at 0 fps for half a minute and most compiler threads idle. While the dispatch
// waits, the packets after it - to the end of its submission and on into the submissions queued
// behind it - are read for their shader registers alone, on a copy of the banks,
// and each dispatch there has its pipeline asked for (ICommandStreamHost.PrefetchDispatchPipeline),
// so the compiles run side by side. Nothing is executed or written: the stream still runs every
// packet when it gets there, and a dispatch whose registers or data turn out different from what
// was read ahead asks for its own pipeline as before. SHARPEMU_GPU_PIPELINE_LOOKAHEAD=0 turns it off.
public sealed partial class GpuCommandInterpreter
{
    internal static readonly bool PipelineLookahead =
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_PIPELINE_LOOKAHEAD") != "0";

    // A look-ahead stops at whichever comes first; the next dispatch that suspends past where it
    // stopped reads on from there. The time limit applies only after the first block, so a first
    // scan slowed by JIT or a busy host still reads the packets right after the waiting dispatch.
    private const uint LookaheadMaxDwords = 1u << 20;
    private const int LookaheadMaxDispatches = 2048;
    private const int LookaheadMaxDepth = 4;
    private static readonly long LookaheadMaxTicks = Stopwatch.Frequency / 20;
    private const int LookaheadBlockDwords = 2048;

    private static long _lookaheadScans;
    private static long _lookaheadDispatches;
    private static long _lookaheadTicks;

    // How far the last look-ahead of this queue read into the buffer the stream was in, so the
    // dispatches it already covered do not each read the same packets again.
    private ulong _lookaheadSubmitId;
    private ulong _lookaheadBuffer;
    private uint _lookaheadEnd;

    // The submissions queued behind the running one on this queue; the look-ahead reads on into
    // them once it reaches the end of the running one. The queue sets it.
    internal QueuedSubmissionReader? QueuedSubmissionsLookup { get; init; }

    internal delegate int QueuedSubmissionReader(int queueId, Span<QueuedSubmission> destination);

    internal readonly record struct QueuedSubmission(ulong Address, uint DwordCount, bool ResetInterpreter, ulong AdmissionOrdinal);

    // The queue's order of the submission running now (CommandSubmission.AdmissionOrdinal).
    internal ulong AdmissionOrdinal { get; set; }

    // Queued submissions a look-ahead read to their end: one of them that suspends once it runs
    // does not read its packets again. Command buffers are reused frame after frame, so they are
    // told apart by their place in the queue's order rather than by address.
    private readonly HashSet<ulong> _lookaheadCovered = new();

    private const int LookaheadMaxQueuedSubmissions = 64;

    private uint[]? _lookaheadBlock;
    private ulong _lookaheadBlockAddress;
    private int _lookaheadBlockDwords;

    /// <summary>Look-aheads run, dispatches they handed to the host, and their time, since the last call.</summary>
    public static (long Scans, long Dispatches, double Milliseconds) TakeLookaheadCounters() =>
        (Interlocked.Exchange(ref _lookaheadScans, 0), Interlocked.Exchange(ref _lookaheadDispatches, 0),
            Interlocked.Exchange(ref _lookaheadTicks, 0) * 1000.0 / Stopwatch.Frequency);

    private ref struct Lookahead
    {
        public RegisterBanks Banks;
        public uint Dwords;
        public int Dispatches;
        public long Deadline;
        public bool Stopped;
    }

    // Called with the dispatch that just suspended on its pipeline as the current packet of the
    // top cursor.
    private void PrefetchPipelinesAhead()
    {
        if (!PipelineLookahead || !_host.PrefetchesPipelines || _execution is not { IsEmpty: false } execution)
        {
            return;
        }

        var topIndex = execution.Depth - 1;
        ref var top = ref execution.At(topIndex);
        if ((top.Address == _lookaheadBuffer && SubmitId == _lookaheadSubmitId && top.Offset < _lookaheadEnd) ||
            (topIndex == 0 && _lookaheadCovered.Contains(AdmissionOrdinal)))
        {
            return;
        }

        if (_lookaheadCovered.Count > 1024)
        {
            _lookaheadCovered.RemoveWhere(ordinal => ordinal < AdmissionOrdinal);
        }

        var started = Stopwatch.GetTimestamp();
        var state = new Lookahead
        {
            Banks = TypedRegisters.Clone(),
            Deadline = started + LookaheadMaxTicks,
        };
        var previousThrowOnFatal = GpuFatal.ThrowOnFatal;
        // A fatal condition met while reading ahead abandons the look-ahead; the stream meets the
        // same packet itself later and decides then.
        GpuFatal.ThrowOnFatal = true;
        var reached = top.Offset;
        try
        {
            if (TryReadAheadPacketLength(top.Address, top.DwordCount, top.Offset, out var length))
            {
                reached = ScanAhead(ref state, top.Address, top.DwordCount, top.Offset + length, depth: 0);
                // The buffers that called this one continue after their calling packets.
                for (var index = topIndex - 1; index >= 0 && !state.Stopped; index--)
                {
                    ref var caller = ref execution.At(index);
                    var advance = caller.DeferredAdvance;
                    if (advance == 0 && !TryReadAheadPacketLength(caller.Address, caller.DwordCount, caller.Offset, out advance))
                    {
                        break;
                    }

                    _ = ScanAhead(ref state, caller.Address, caller.DwordCount, caller.Offset + advance, depth: 0);
                }

                // A frame is dozens of submissions: the ones queued behind this one run with the
                // registers it leaves, unless they start from a reset interpreter.
                if (!state.Stopped && QueuedSubmissionsLookup is { } lookup)
                {
                    Span<QueuedSubmission> queued = stackalloc QueuedSubmission[LookaheadMaxQueuedSubmissions];
                    var count = lookup(QueueId, queued);
                    for (var index = 0; index < count && !state.Stopped; index++)
                    {
                        if (queued[index].ResetInterpreter)
                        {
                            state.Banks.Reset();
                        }

                        _ = ScanAhead(ref state, queued[index].Address, queued[index].DwordCount, 0, depth: 0);
                        if (!state.Stopped)
                        {
                            _lookaheadCovered.Add(queued[index].AdmissionOrdinal);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Speculative: whatever stopped it, the stream runs the real packets, and the rest of
            // this buffer is not read ahead again (each retry of the waiting dispatch would).
            reached = uint.MaxValue;
        }
        finally
        {
            GpuFatal.ThrowOnFatal = previousThrowOnFatal;
            _lookaheadBlockDwords = 0;
        }

        _lookaheadSubmitId = SubmitId;
        _lookaheadBuffer = top.Address;
        _lookaheadEnd = reached;
        Interlocked.Increment(ref _lookaheadScans);
        Interlocked.Add(ref _lookaheadDispatches, state.Dispatches);
        Interlocked.Add(ref _lookaheadTicks, Stopwatch.GetTimestamp() - started);
    }

    // Reads the packets of one buffer from offset on; a call recurses, a chain continues in its
    // target. Returns the offset reached in the buffer it started in (its end once it chained or
    // finished), and sets Stopped when the look-ahead is to go no further.
    private uint ScanAhead(ref Lookahead state, ulong address, uint dwordCount, uint offset, int depth)
    {
        var startAddress = address;
        Span<uint> header = stackalloc uint[1];
        Span<uint> small = stackalloc uint[16];
        while (true)
        {
            if (offset >= dwordCount)
            {
                return address == startAddress ? dwordCount : uint.MaxValue;
            }

            if (state.Dwords >= LookaheadMaxDwords || state.Dispatches >= LookaheadMaxDispatches ||
                (state.Dwords >= LookaheadBlockDwords && Stopwatch.GetTimestamp() > state.Deadline))
            {
                state.Stopped = true;
                return address == startAddress ? offset : uint.MaxValue;
            }

            var packetAddress = address + (ulong)offset * sizeof(uint);
            if (!TryReadAhead(packetAddress, dwordCount - offset, header))
            {
                state.Stopped = true;
                return address == startAddress ? offset : uint.MaxValue;
            }

            var word = header[0];
            if (word == PacketHeader.FillerHeader)
            {
                offset++;
                state.Dwords++;
                continue;
            }

            var length = PacketHeader.Length(word);
            if (word == 0 || PacketHeader.PacketType(word) != 3 || length > dwordCount - offset)
            {
                state.Stopped = true;
                return address == startAddress ? offset : uint.MaxValue;
            }

            state.Dwords += length;
            var opcode = PacketHeader.Opcode(word);
            var payloadCount = (int)length - 1;
            switch (opcode)
            {
                case PacketOpcode.SetShaderRegister:
                {
                    var payload = payloadCount <= small.Length ? small[..payloadCount] : new uint[payloadCount];
                    if (!TryReadAhead(packetAddress + sizeof(uint), dwordCount - offset - 1, payload))
                    {
                        state.Stopped = true;
                        return address == startAddress ? offset : uint.MaxValue;
                    }

                    var registerOffset = payload[0];
                    var count = (uint)payloadCount - 1;
                    if (count != 0 && registerOffset != RegisterBankLayout.ShaderRegisterNop &&
                        registerOffset < RegisterBankLayout.ShaderRegisterCount &&
                        registerOffset + count <= RegisterBankLayout.ShaderRegisterCount)
                    {
                        var packet = new PacketContext(word & ~1u, packetAddress, offset, dwordCount - offset, dwordCount);
                        RegisterWriteTable.WriteShaderPacket(state.Banks, in packet, registerOffset, payload.Slice(1, (int)count));
                    }

                    break;
                }

                case PacketOpcode.SetShaderRegisterIndirect when length == 5:
                {
                    var payload = small[..4];
                    if (!TryReadAhead(packetAddress + sizeof(uint), dwordCount - offset - 1, payload))
                    {
                        state.Stopped = true;
                        return address == startAddress ? offset : uint.MaxValue;
                    }

                    ApplyAheadShaderTable(ref state, (payload[0] & 0xFFFF_FFFCu) | ((ulong)payload[1] << 32), payload[3] & 0x3FFFu);
                    break;
                }

                case PacketOpcode.Nop when payloadCount >= 3 && PacketHeader.CustomCode(word) == PacketCustomCode.ShaderRegisterTable:
                {
                    var payload = small[..3];
                    if (!TryReadAhead(packetAddress + sizeof(uint), dwordCount - offset - 1, payload))
                    {
                        state.Stopped = true;
                        return address == startAddress ? offset : uint.MaxValue;
                    }

                    ApplyAheadShaderTable(ref state, Address(payload[1], payload[2]), payload[0]);
                    break;
                }

                case PacketOpcode.Nop when PacketHeader.CustomCode(word) is PacketCustomCode.DispatchReset or PacketCustomCode.DrawReset:
                    state.Banks.Reset();
                    break;

                case PacketOpcode.DispatchDirect when length == 5:
                {
                    var payload = small[..4];
                    if (TryReadAhead(packetAddress + sizeof(uint), dwordCount - offset - 1, payload))
                    {
                        state.Dispatches++;
                        _host.PrefetchDispatchPipeline(state.Banks, payload[0], payload[1], payload[2], payload[3]);
                    }

                    break;
                }

                case PacketOpcode.DispatchIndirect when length is 3 or 4:
                {
                    var payload = small[..(payloadCount)];
                    if (TryReadAhead(packetAddress + sizeof(uint), dwordCount - offset - 1, payload))
                    {
                        // The group counts are not part of what the pipeline is built from; the
                        // arguments may not be written yet, so they are not read.
                        state.Dispatches++;
                        var initiator = length == 4 ? payload[2] : payload[1];
                        _host.PrefetchDispatchPipeline(state.Banks, 1, 1, 1, initiator);
                    }

                    break;
                }

                case PacketOpcode.IndirectBuffer or PacketOpcode.IndirectBufferConstant when length == 4:
                {
                    var payload = small[..3];
                    if (!TryReadAhead(packetAddress + sizeof(uint), dwordCount - offset - 1, payload))
                    {
                        state.Stopped = true;
                        return address == startAddress ? offset : uint.MaxValue;
                    }

                    var target = payload[0] | ((ulong)(payload[1] & 0xFFFFu) << 32);
                    var targetDwords = payload[2] & 0xFFFFFu;
                    var call = ((payload[2] >> 20) & 0x1u) == 0 && dwordCount - offset > length;
                    if (target == 0 || targetDwords == 0)
                    {
                        // A ring chunk the guest is still appending to is not read ahead.
                        if (target == 1)
                        {
                            state.Stopped = true;
                            return address == startAddress ? offset : uint.MaxValue;
                        }

                        break;
                    }

                    if (call)
                    {
                        if (depth + 1 >= LookaheadMaxDepth)
                        {
                            state.Stopped = true;
                            return address == startAddress ? offset : uint.MaxValue;
                        }

                        _ = ScanAhead(ref state, target, targetDwords, 0, depth + 1);
                        if (state.Stopped)
                        {
                            return address == startAddress ? offset : uint.MaxValue;
                        }

                        break;
                    }

                    // A chain ends this buffer: the walk continues in its target.
                    if (address == startAddress)
                    {
                        startAddress = 0;
                    }

                    address = target;
                    dwordCount = targetDwords;
                    offset = 0;
                    _lookaheadBlockDwords = 0;
                    continue;
                }

                case PacketOpcode.IndirectBuffer:
                    // The branch form decides on a value the GPU may not have written yet.
                    state.Stopped = true;
                    return address == startAddress ? offset : uint.MaxValue;
            }

            offset += length;
        }
    }

    private void ApplyAheadShaderTable(ref Lookahead state, ulong tableAddress, uint count)
    {
        if (tableAddress == 0 || count == 0 || count > 0x3FFF)
        {
            return;
        }

        var entries = new uint[count * 2];
        if (!_host.TryReadGuest(tableAddress, MemoryMarshal.AsBytes(entries.AsSpan())))
        {
            state.Stopped = true;
            return;
        }

        for (var index = 0; index < entries.Length; index += 2)
        {
            var rawOffset = entries[index];
            var offset = RegisterBankLayout.Normalize(rawOffset);
            if (rawOffset == RegisterTableSentinel || offset == RegisterBankLayout.ShaderRegisterNop ||
                offset >= RegisterBankLayout.ShaderRegisterCount)
            {
                continue;
            }

            RegisterWriteTable.WriteShaderEntry(state.Banks, offset, entries[index + 1], tableAddress);
        }
    }

    private bool TryReadAheadPacketLength(ulong address, uint dwordCount, uint offset, out uint length)
    {
        length = 0;
        Span<uint> header = stackalloc uint[1];
        if (offset >= dwordCount || !TryReadAhead(address + (ulong)offset * sizeof(uint), dwordCount - offset, header))
        {
            return false;
        }

        length = header[0] == PacketHeader.FillerHeader ? 1 : PacketHeader.Length(header[0]);
        return (header[0] == PacketHeader.FillerHeader || PacketHeader.PacketType(header[0]) == 3) && length <= dwordCount - offset;
    }

    // Reads packet words through a block of the buffer, never past the words left in it.
    private bool TryReadAhead(ulong address, uint wordsLeftInBuffer, Span<uint> destination)
    {
        if ((uint)destination.Length > wordsLeftInBuffer)
        {
            return false;
        }

        var block = _lookaheadBlock ??= new uint[LookaheadBlockDwords];
        if (address >= _lookaheadBlockAddress &&
            address + (ulong)destination.Length * sizeof(uint) <= _lookaheadBlockAddress + (ulong)_lookaheadBlockDwords * sizeof(uint))
        {
            var start = (int)((address - _lookaheadBlockAddress) / sizeof(uint));
            block.AsSpan(start, destination.Length).CopyTo(destination);
            return true;
        }

        if (destination.Length > LookaheadBlockDwords)
        {
            return _host.TryReadGuest(address, MemoryMarshal.AsBytes(destination));
        }

        var words = (int)Math.Min(LookaheadBlockDwords, wordsLeftInBuffer);
        if (!_host.TryReadGuest(address, MemoryMarshal.AsBytes(block.AsSpan(0, words))))
        {
            _lookaheadBlockDwords = 0;
            return false;
        }

        _lookaheadBlockAddress = address;
        _lookaheadBlockDwords = words;
        block.AsSpan(0, destination.Length).CopyTo(destination);
        return true;
    }
}
