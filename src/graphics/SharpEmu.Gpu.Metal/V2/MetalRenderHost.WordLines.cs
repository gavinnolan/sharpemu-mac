// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Gpu.Metal.V2;

// A resource materialization reads its descriptor tables one dword at a time, and each
// dword read through TryReadGuest asks the pending-write overlay, the image cache and the
// recorded GPU writes (twice, with a message send for the completed serial each time)
// whether it may have to wait: measured at about 2.5 us a word with 250 live write
// records, which made the walk a third of the Metal worker in Demon's Souls character
// creation. Inside one materialization a word is instead served from a 256-byte line read
// once, after the whole line passed the same tests a word read makes:
//   - no pending command-processor bytes over it (overlay coverage None), and
//   - no GPU-modified image bytes and no pending recorded write over it,
// so every word in it would have been read straight from memory without waiting. A line
// that fails is remembered as refused and its words take the ordinary path, which waits
// exactly as before. The tests only get weaker over time (the GPU completes, entries
// land) except through a recorded write, an owed CP write, a host write that forgets owed
// bytes, or a wait: each of those drops every line. Lines live only between
// BeginGuestWordReads and EndGuestWordReads on the thread that began them.
internal sealed partial class MetalRenderHost
{
    // SHARPEMU_RESOURCE_WIDE_LINES=0 keeps the 64-byte lines of 16 slots. A line costs the same
    // tests however wide it is, and a descriptor table or pointer ring is read from a few lines.
    private static readonly bool WideLines = SharpEmu.ShaderCompiler.PerfSwitches.Enabled("SHARPEMU_RESOURCE_WIDE_LINES");
    private static readonly int WordLineBytes = WideLines ? 256 : 64;
    private static readonly int WordLineSlots = WideLines ? 32 : 16;

    private readonly ulong[] _wordLineAddress = new ulong[WordLineSlots];
    private readonly ulong[] _wordLineGeneration = new ulong[WordLineSlots];
    private readonly bool[] _wordLineReadable = new bool[WordLineSlots];
    private readonly byte[] _wordLineBytes = new byte[WordLineSlots * WordLineBytes];
    private ulong _wordLineGenerationCurrent = 1;
    private ulong _wordLineGpuVersion;
    private long _wordLineOverlayVersion;
    private int _wordLineThread;

    private long _wordReads;
    private long _wordLineHits;
    private long _wordLineFills;
    private long _wordLineRefused;
    private long _wordReadScopes;

    public void BeginGuestWordReads()
    {
        // The word lines belong to the worker; a resolution on another thread reads each word.
        if (_concurrentAssumptions is not null)
        {
            return;
        }

        InvalidateWordLines();
        _wordLineGpuVersion = _gpuWrites.Version;
        _wordLineOverlayVersion = _cpOverlay.Version;
        _wordLineThread = Environment.CurrentManagedThreadId;
        _wordReadScopes++;
    }

    public void EndGuestWordReads()
    {
        if (_concurrentAssumptions is not null)
        {
            return;
        }

        _wordLineThread = 0;
        InvalidateWordLines();
    }

    private void InvalidateWordLines() => _wordLineGenerationCurrent++;

    private bool TryReadWordFromLine(ulong address, out uint word)
    {
        word = 0;
        var offset = (int)(address & (ulong)(WordLineBytes - 1));
        if (offset > WordLineBytes - sizeof(uint))
        {
            return false;
        }

        var gpuVersion = _gpuWrites.Version;
        var overlayVersion = _cpOverlay.Version;
        if (gpuVersion != _wordLineGpuVersion || overlayVersion != _wordLineOverlayVersion)
        {
            _wordLineGpuVersion = gpuVersion;
            _wordLineOverlayVersion = overlayVersion;
            InvalidateWordLines();
        }

        var line = address - (ulong)offset;
        var slot = (int)((line / (ulong)WordLineBytes) & (ulong)(WordLineSlots - 1));
        var bytes = _wordLineBytes.AsSpan(slot * WordLineBytes, WordLineBytes);
        if (_wordLineAddress[slot] != line || _wordLineGeneration[slot] != _wordLineGenerationCurrent)
        {
            _wordLineAddress[slot] = line;
            _wordLineGeneration[slot] = _wordLineGenerationCurrent;
            _wordLineReadable[slot] = TryFillWordLine(line, bytes);
            _wordLineFills++;
        }

        if (!_wordLineReadable[slot])
        {
            _wordLineRefused++;
            return false;
        }

        _wordLineHits++;
        word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        return true;
    }

    // True when every word of the line would be read straight from memory, without
    // overlay bytes and without a wait. Every test is an overlap test, so passing for the
    // line implies passing for each word in it.
    private bool TryFillWordLine(ulong line, Span<byte> bytes) =>
        TryReadUnownedBytes(line, (ulong)WordLineBytes, bytes);

    /// <summary>The word-line fields of the WAITS line; resets them.</summary>
    private string TakeWordLineCounters() =>
        $" word_reads={Interlocked.Exchange(ref _wordReads, 0)}" +
        $" word_read_scopes={Interlocked.Exchange(ref _wordReadScopes, 0)}" +
        $" word_line_hits={Interlocked.Exchange(ref _wordLineHits, 0)}" +
        $" word_line_fills={Interlocked.Exchange(ref _wordLineFills, 0)}" +
        $" word_line_refused={Interlocked.Exchange(ref _wordLineRefused, 0)}";

    internal (long Reads, long Hits, long Fills, long Refused) WordLineCounters =>
        (Interlocked.Read(ref _wordReads), Interlocked.Read(ref _wordLineHits),
            Interlocked.Read(ref _wordLineFills), Interlocked.Read(ref _wordLineRefused));
}
