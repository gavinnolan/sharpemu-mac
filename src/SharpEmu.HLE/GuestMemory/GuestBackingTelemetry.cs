// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.HLE.Host;
using SharpEmu.Logging;

namespace SharpEmu.HLE.GuestMemory;

/// <summary>
/// What the chunked guest backing gave back to the OS, cumulative since startup: the
/// "[MEM] guest" line (SHARPEMU_LOG_MEMORY=1). released_mb and chunks_empty count the chunks
/// emptied because the guest freed their bytes (direct memory released, flexible memory and
/// pool pages unmapped or decommitted); clear_purged_mb the bytes an allocation's clear
/// emptied instead of writing zeros over them; zero_filled_mb the bytes either path had to
/// zero-fill because their pages stayed resident (wired by a GPU command buffer);
/// release_skipped_mapped_mb freed bytes left alone because a view still mapped their chunk.
/// </summary>
public static class GuestBackingTelemetry
{
    private static long _chunkBytes;
    private static long _backingBytes;
    private static long _releaseCalls;
    private static long _releasedBytes;
    private static long _releasedChunks;
    private static long _skippedMappedBytes;
    private static long _clearPurgedBytes;
    private static long _clearPurgedChunks;
    private static long _zeroFilledBytes;

    public static long ReleasedBytes => Interlocked.Read(ref _releasedBytes);
    public static long ReleasedChunks => Interlocked.Read(ref _releasedChunks);
    public static long ClearPurgedBytes => Interlocked.Read(ref _clearPurgedBytes);
    public static long ZeroFilledBytes => Interlocked.Read(ref _zeroFilledBytes);
    public static long SkippedMappedBytes => Interlocked.Read(ref _skippedMappedBytes);

    internal static void NoteChunkedBacking(ulong chunkBytes, ulong backingBytes)
    {
        Interlocked.Exchange(ref _chunkBytes, (long)chunkBytes);
        Interlocked.Exchange(ref _backingBytes, (long)backingBytes);
        MemoryTelemetry.Register("guest", Describe);
    }

    internal static void NoteRelease(HostBackingDiscard result, ulong skippedMappedBytes)
    {
        Interlocked.Increment(ref _releaseCalls);
        Interlocked.Add(ref _releasedBytes, (long)result.EmptiedBytes);
        Interlocked.Add(ref _releasedChunks, (long)result.EmptiedChunks);
        Interlocked.Add(ref _zeroFilledBytes, (long)result.ZeroFilledBytes);
        Interlocked.Add(ref _skippedMappedBytes, (long)skippedMappedBytes);
    }

    internal static void NoteClear(HostBackingDiscard result)
    {
        Interlocked.Add(ref _clearPurgedBytes, (long)result.EmptiedBytes);
        Interlocked.Add(ref _clearPurgedChunks, (long)result.EmptiedChunks);
        Interlocked.Add(ref _zeroFilledBytes, (long)result.ZeroFilledBytes);
    }

    private static string Describe()
    {
        const double Mb = 1024.0 * 1024.0;
        var culture = CultureInfo.InvariantCulture;
        return string.Create(culture,
            $"backing=chunked chunk_kb={Interlocked.Read(ref _chunkBytes) / 1024} " +
            $"backing_mb={Interlocked.Read(ref _backingBytes) / Mb:F0} " +
            $"released_mb={ReleasedBytes / Mb:F1} chunks_empty={ReleasedChunks} " +
            $"release_calls={Interlocked.Read(ref _releaseCalls)} " +
            $"release_skipped_mapped_mb={SkippedMappedBytes / Mb:F1} " +
            $"clear_purged_mb={ClearPurgedBytes / Mb:F1} clear_chunks_empty={Interlocked.Read(ref _clearPurgedChunks)} " +
            $"zero_filled_mb={ZeroFilledBytes / Mb:F1}");
    }
}
