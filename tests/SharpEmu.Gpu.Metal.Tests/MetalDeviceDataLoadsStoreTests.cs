// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// Device data loads a start learned are seeded into the next one, so the shader is planned
// with them from its first compile instead of being compiled again once they are learned.
public sealed class MetalDeviceDataLoadsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public MetalDeviceDataLoadsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void LinesLoadAsTheUnionPerShaderAndTornLinesAreSkipped()
    {
        var path = Path.Combine(_directory, MetalDeviceDataLoadsStore.FileName("build"));
        File.WriteAllText(path,
            MetalDeviceDataLoadsStore.Line(0xABCD, [3, 1]) + "\n" +
            MetalDeviceDataLoadsStore.Line(0xABCD, [1, 3, 7]) + "\n" +
            MetalDeviceDataLoadsStore.Line(0x1234, [0]) + "\n" +
            "00000000000056");

        var learned = MetalDeviceDataLoadsStore.Load(path, out var lines);

        Assert.Equal(4, lines);
        Assert.Equal(2, learned.Count);
        Assert.Equal([1, 3, 7], learned[0xABCD].Order());
        Assert.Equal([0], learned[0x1234]);
    }

    [Fact]
    public void EachBuildHasItsOwnFileAndOthersArePruned()
    {
        Assert.NotEqual(MetalDeviceDataLoadsStore.FileName("a"), MetalDeviceDataLoadsStore.FileName("b"));
        var current = Path.Combine(_directory, MetalDeviceDataLoadsStore.FileName("current"));
        File.WriteAllText(current, string.Empty);
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddDays(-30));
        var others = new List<string>();
        for (var index = 0; index <= MetalDeviceDataLoadsStore.MaxOtherFiles; index++)
        {
            var path = Path.Combine(_directory, MetalDeviceDataLoadsStore.FileName($"other-{index}"));
            File.WriteAllText(path, string.Empty);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-index));
            others.Add(path);
        }

        MetalDeviceDataLoadsStore.PruneOtherFiles(_directory, current);

        Assert.True(File.Exists(current));
        Assert.All(others.Take(MetalDeviceDataLoadsStore.MaxOtherFiles), path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(others[^1]));
    }

    [Fact]
    public void ASeededShaderPlansWithItsLoadsWithoutCountingThemLearned()
    {
        const ulong hash = 0x5EED_0000_0000_0001;
        var learned = new List<ulong>();
        void OnLearned(ulong learnedHash, int[] _) => learned.Add(learnedHash);
        DeviceDataLoads.Learned += OnLearned;
        try
        {
            var generation = DeviceDataLoads.Generation;
            var slots = DeviceDataLoads.LearnedSlots;
            DeviceDataLoads.Seed(hash, [4, 9]);

            Assert.Equal(2, DeviceDataLoads.CountFor(hash));
            Assert.True(DeviceDataLoads.Contains(hash, 9));
            Assert.NotEqual(generation, DeviceDataLoads.Generation);
            Assert.Equal(slots, DeviceDataLoads.LearnedSlots);
            Assert.DoesNotContain(hash, learned);

            Assert.False(DeviceDataLoads.Learn(hash, [4]));
            Assert.True(DeviceDataLoads.Learn(hash, [5]));
            Assert.Contains(hash, learned);
        }
        finally
        {
            DeviceDataLoads.Learned -= OnLearned;
        }
    }
}
