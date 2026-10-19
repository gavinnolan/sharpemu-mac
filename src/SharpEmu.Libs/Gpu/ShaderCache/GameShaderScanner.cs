// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Libs.Gpu.ShaderCache;

public readonly record struct ShaderScanResult(int FilesScanned, int FilesSkipped, int Programs, long BytesScanned);

/// <summary>
/// Finds the AGC shaders a game ships in its files (Demon's Souls: 72,016 headers in
/// materials/!cookedshaders/*.csdr, 1,756 compute and 35,130 vertex/pixel pairs) by their
/// header signature, and records each with the code beside it in the inventory. A
/// geometry program followed by a pixel program in one file is a pair. Files whose size
/// and last write match the inventory's record of them are not read again.
/// </summary>
internal static class GameShaderScanner
{
    public const int Version = 2;
    private const int ChunkBytes = 8 << 20;
    private const int CodeAlignment = 256;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm", ".bk2", ".bik", ".usm", ".at9", ".wem", ".bnk", ".ogg", ".wav", ".mp3", ".opus", ".flac",
    };

    private readonly record struct FoundProgram(AgcShaderType Type, ulong Identity, bool New);

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".elf", ".self", ".prx", ".sprx",
    };

    /// <summary>
    /// Whether a file below app0 is one the default scan reads: a path that names shaders,
    /// or an executable (programs embed theirs). The full scan (--precompile-only) reads
    /// everything but media; Demon's Souls keeps its 151 MB of shaders in !cookedshaders
    /// beside 83 GB of other data.
    /// </summary>
    public static bool IsLikelyShaderFile(string relative) =>
        relative.Contains("shader", StringComparison.OrdinalIgnoreCase) ||
        (ExecutableExtensions.Contains(Path.GetExtension(relative)) && !relative.Contains('/', StringComparison.Ordinal)) ||
        (ExecutableExtensions.Contains(Path.GetExtension(relative)) && relative.StartsWith("sce_module/", StringComparison.OrdinalIgnoreCase));

    public static ShaderScanResult Scan(string root, ShaderInventoryFile file, int threads, Action<long, long>? progress,
        CancellationToken cancellation, Func<string, bool>? include = null)
    {
        // Paths that name shaders first, so a scan cut short has read those.
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(path => !MediaExtensions.Contains(Path.GetExtension(path)))
            .Where(path => include is null || include(Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Select(path => new FileInfo(path))
            .Where(info => info.Length >= AgcShaderHeader.FixedBytes)
            .OrderBy(info => info.FullName.Contains("shader", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(info => info.FullName, StringComparer.Ordinal)
            .ToArray();
        var pending = new List<(FileInfo Info, string Relative)>();
        var skipped = 0;
        foreach (var info in files)
        {
            var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            if (file.TryGetScannedFile(relative, out var known) && known.Size == info.Length &&
                known.WriteTicks == info.LastWriteTimeUtc.Ticks && known.Scanner == Version)
            {
                skipped++;
                continue;
            }

            pending.Add((info, relative));
        }

        var total = pending.Sum(entry => entry.Info.Length);
        var scanned = 0L;
        var programs = 0;
        progress?.Invoke(0, total);
        Parallel.ForEach(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(threads, 1), CancellationToken = cancellation },
            entry =>
            {
                var found = ScanFile(entry.Info, file, bytes =>
                    progress?.Invoke(Interlocked.Add(ref scanned, bytes), total), cancellation);
                Interlocked.Add(ref programs, found);
                file.RecordScannedFile(new ScannedFile(entry.Relative, entry.Info.Length, entry.Info.LastWriteTimeUtc.Ticks, found, Version));
            });
        return new ShaderScanResult(pending.Count, skipped, programs, total);
    }

    private static int ScanFile(FileInfo info, ShaderInventoryFile file, Action<long> progress, CancellationToken cancellation)
    {
        using var handle = File.OpenHandle(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        var length = RandomAccess.GetLength(handle);
        var signature = AgcShaderHeader.Signature;
        var buffer = new byte[Math.Min(ChunkBytes, Math.Max(length, signature.Length))];
        var found = new List<FoundProgram>();
        var reported = 0L;
        for (var chunkStart = 0L; chunkStart < length;)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = ReadAt(handle, buffer, chunkStart);
            if (read <= 0)
            {
                break;
            }

            var window = buffer.AsSpan(0, read);
            for (var position = window.IndexOf(signature); position >= 0;)
            {
                if (TryRecord(handle, length, chunkStart + position, file) is { } program)
                {
                    found.Add(program);
                }

                var next = window[(position + 1)..].IndexOf(signature);
                position = next < 0 ? -1 : position + 1 + next;
            }

            var completed = Math.Min(chunkStart + read, info.Length);
            progress(completed - reported);
            reported = completed;
            if (chunkStart + read >= length)
            {
                break;
            }

            chunkStart += read - (signature.Length - 1);
        }

        for (var index = 1; index < found.Count; index++)
        {
            if (found[index - 1].Type == AgcShaderType.Geometry && found[index].Type == AgcShaderType.Pixel)
            {
                file.RecordPair(found[index - 1].Identity, found[index].Identity);
            }
        }

        return found.Count(entry => entry.New);
    }

    private static FoundProgram? TryRecord(SafeFileHandle handle, long length, long offset, ShaderInventoryFile file)
    {
        Span<byte> head = stackalloc byte[AgcShaderHeader.FixedBytes];
        if (offset + head.Length > length || ReadAt(handle, head, offset) != head.Length ||
            !AgcShaderHeader.TryPeekSizes(head, out var headerBytes, out var codeBytes, out var type) ||
            !AgcShaderHeader.IsSupported(type) || offset + headerBytes > length)
        {
            return null;
        }

        var headerData = new byte[headerBytes];
        if (ReadAt(handle, headerData, offset) != headerBytes || !AgcShaderHeader.TryParse(headerData, 0, out var header))
        {
            return null;
        }

        foreach (var codeOffset in CodeCandidates(offset, headerBytes, codeBytes))
        {
            if (codeOffset < 0 || codeOffset + codeBytes > length)
            {
                continue;
            }

            var code = new byte[codeBytes + ShaderInventoryFile.CodeSlackBytes];
            var read = ReadAt(handle, code, codeOffset);
            if (read < codeBytes || !ShaderInventory.TryHash(code, (uint)codeBytes, ShaderInventoryFile.CodeAddress, out var hash))
            {
                continue;
            }

            var identity = ShaderInventoryFile.ProgramIdentity(hash, (uint)codeBytes, header.Bytes);
            if (file.Contains(identity))
            {
                return new FoundProgram(header.Type, identity, false);
            }

            var bytes = code.AsSpan(0, Math.Max(read, codeBytes));
            if (!ShaderInventory.Decodes(bytes, ShaderInventoryFile.CodeAddress))
            {
                continue;
            }

            file.RecordProgram(header, hash, (uint)codeBytes, bytes, InventorySource.Scanned);
            return new FoundProgram(header.Type, identity, true);
        }

        return null;
    }

    private static IEnumerable<long> CodeCandidates(long headerOffset, int headerBytes, int codeBytes)
    {
        yield return headerOffset - codeBytes;
        yield return headerOffset + headerBytes;
        var aligned = (headerOffset + headerBytes + CodeAlignment - 1) / CodeAlignment * CodeAlignment;
        if (aligned != headerOffset + headerBytes)
        {
            yield return aligned;
        }
    }

    private static int ReadAt(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
