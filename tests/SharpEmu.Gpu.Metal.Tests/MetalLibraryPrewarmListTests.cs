// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The graphics library list holds MSL text, which only the translator build that wrote it
// asks for again, so each build keeps its own list.
public sealed class MetalLibraryPrewarmListTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public MetalLibraryPrewarmListTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void EachStampHasItsOwnListFile()
    {
        var a = MetalLibraryPrewarm.FileName("translator-a|device|os");
        Assert.Equal(a, MetalLibraryPrewarm.FileName("translator-a|device|os"));
        Assert.NotEqual(a, MetalLibraryPrewarm.FileName("translator-b|device|os"));
        Assert.StartsWith(MetalLibraryPrewarm.FilePrefix + "-", a, StringComparison.Ordinal);
        Assert.EndsWith(".bin", a, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceRecordedUnderAnotherStampIsNotQueued()
    {
        var older = Path.Combine(_directory, MetalLibraryPrewarm.FileName("translator-a"));
        Write(older, 0x1111);
        var current = Path.Combine(_directory, MetalLibraryPrewarm.FileName("translator-b"));

        var (entries, _) = MetalLibraryPrewarm.Load(current, out _);

        Assert.Empty(entries);
        Assert.Single(MetalLibraryPrewarm.Load(older, out _).Entries);
    }

    [Fact]
    public void PruningKeepsTheCurrentListAndTheMostRecentOthers()
    {
        var current = Path.Combine(_directory, MetalLibraryPrewarm.FileName("current"));
        Write(current, 1);
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddDays(-30));
        var legacy = Path.Combine(_directory, MetalLibraryPrewarm.FilePrefix + ".bin");
        Write(legacy, 2);
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddDays(-20));
        var others = new List<string>();
        for (var index = 0; index < MetalLibraryPrewarm.MaxOtherLists + 1; index++)
        {
            var path = Path.Combine(_directory, MetalLibraryPrewarm.FileName($"other-{index}"));
            Write(path, (ulong)(10 + index));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-index));
            others.Add(path);
        }

        var unrelated = Path.Combine(_directory, "shader-prewarm-12345678.bin");
        File.WriteAllBytes(unrelated, [1, 2, 3]);

        MetalLibraryPrewarm.PruneOtherLists(_directory, current);

        Assert.True(File.Exists(current));
        Assert.True(File.Exists(unrelated));
        Assert.False(File.Exists(legacy));
        Assert.All(others.Take(MetalLibraryPrewarm.MaxOtherLists), path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(others[^1]));
    }

    private static void Write(string path, ulong key)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> header = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C4C534D);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        file.Write(header);
        MetalLibraryPrewarm.WriteRecord(file, [1]);
        MetalLibraryPrewarm.WriteRecord(file, MetalLibraryPrewarm.SourceBody(key, false, MetalLibraryPrewarm.Compress("kernel void k() {}")));
    }
}
