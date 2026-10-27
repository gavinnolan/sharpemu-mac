// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

public sealed class IdenticalScalarWriteTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("SHARPEMU_REGISTER_IDENTICAL_SCALARS") == "1";
    private static RegisterBanks Banks() => new(static message => new InvalidOperationException(message));
    private static uint Write(RegisterBanks banks, uint offset, ReadOnlySpan<uint> values)
    {
        var packet = new PacketContext(PacketHeader.Make((uint)values.Length + 2, PacketOpcode.SetShaderRegister), 0x10000, 0, 100, 100);
        return RegisterWriteTable.WriteShaderPacket(banks, in packet, offset, values);
    }

    private static UserScalarRegisters Scalars(RegisterBanks banks, uint first) => first switch
    {
        SpiShaderUserDataPs0 => banks.Shader.Pixel.UserScalars,
        SpiShaderUserDataGs0 => banks.Shader.Vertex.GeometryUserScalars,
        SpiShaderUserDataHs0 => banks.Shader.Vertex.HullUserScalars,
        ComputeUserData0 => banks.Shader.Compute.UserScalars,
        SpiShaderUserDataVs0 => banks.Shader.Vertex.LegacyVertexUserScalars,
        SpiShaderUserDataEs0 => banks.Shader.Vertex.ExportUserScalars,
        _ => throw new ArgumentOutOfRangeException(nameof(first)),
    };

    [Theory]
    [InlineData(SpiShaderUserDataPs0, 32)]
    [InlineData(SpiShaderUserDataGs0, 32)]
    [InlineData(SpiShaderUserDataHs0, 32)]
    [InlineData(ComputeUserData0, 16)]
    public void RepeatedPacketPreservesSnapshotUntilValueKindOrCountChanges(uint first, int capacity)
    {
        var banks = Banks();
        var offset = first + (uint)capacity - 2;
        banks.UserDataMarker = UserScalarKind.Region;
        Write(banks, offset, [17, 19]);
        var original = banks.Snapshot();
        var oldScalars = Scalars(original, first);
        banks.UserDataMarker = UserScalarKind.Region;
        Assert.Equal(2u, Write(banks, offset, [17, 19]));
        Assert.Equal(UserScalarKind.Unknown, banks.UserDataMarker);
        if (Enabled)
        {
            Assert.Same(oldScalars, Scalars(banks, first));
            if (Environment.GetEnvironmentVariable("SHARPEMU_REGISTER_SNAPSHOT_REUSE") != "0")
                Assert.Same(original, banks.Snapshot());
        }

        // Equal values with a different marker still require a copy.
        Write(banks, offset, [17, 19]);
        Assert.NotSame(oldScalars, Scalars(banks, first));
        Assert.Equal(UserScalarKind.Unknown, Scalars(banks, first).Kinds[capacity - 1]);
        Assert.Equal(UserScalarKind.Region, oldScalars.Kinds[capacity - 1]);
        var changedKind = banks.Snapshot();
        Write(banks, offset + 1, [23]);
        Assert.Equal(19u, Scalars(changedKind, first).Values[capacity - 1]);
        Assert.Equal(23u, Scalars(banks, first).Values[capacity - 1]);

        // A zero write extends Count even if the uninitialized storage already contains zero.
        banks = Banks();
        var empty = banks.Snapshot();
        Write(banks, offset, [0, 0]);
        Assert.Equal((uint)capacity, Scalars(banks, first).Count);
        Assert.Equal(0u, Scalars(empty, first).Count);
    }

    [Theory]
    [InlineData(SpiShaderUserDataPs0, 32)]
    [InlineData(SpiShaderUserDataGs0, 32)]
    [InlineData(SpiShaderUserDataHs0, 32)]
    [InlineData(ComputeUserData0, 16)]
    public void InvalidPacketsStillFailWithoutChangingScalarContents(uint first, int capacity)
    {
        var banks = Banks();
        Write(banks, first + (uint)capacity - 1, [19]);
        var original = banks.Snapshot();
        banks.UserDataMarker = UserScalarKind.BufferResource;
        Assert.Contains("outside the stage window", Assert.Throws<InvalidOperationException>(
            () => Write(banks, first + (uint)capacity - 1, [19, 0])).Message);
        Assert.Equal(UserScalarKind.BufferResource, banks.UserDataMarker);
        Assert.Equal(19u, Scalars(banks, first).Values[capacity - 1]);
        Assert.Equal(19u, Scalars(original, first).Values[capacity - 1]);
        Assert.Contains("consumed no values", Assert.Throws<InvalidOperationException>(
            () => Write(banks, first, [])).Message);
        // The original empty direct writer clears its marker before reporting zero consumption.
        Assert.Equal(UserScalarKind.Unknown, banks.UserDataMarker);
    }

    [Theory]
    [InlineData(SpiShaderUserDataVs0)]
    [InlineData(SpiShaderUserDataEs0)]
    public void LegacyIndirectPacketsStillApplyMarkerOnlyToFirstEntry(uint first)
    {
        var banks = Banks();
        Write(banks, first, [7, 9]);
        var original = banks.Snapshot();
        banks.UserDataMarker = UserScalarKind.Region;
        Write(banks, first, [7, 9]);
        Assert.Equal(UserScalarKind.Region, Scalars(banks, first).Kinds[0]);
        Assert.Equal(UserScalarKind.Unknown, Scalars(banks, first).Kinds[1]);
        Assert.Equal(UserScalarKind.Unknown, Scalars(original, first).Kinds[0]);
        Assert.Equal(UserScalarKind.Unknown, banks.UserDataMarker);
    }

    [Theory]
    [InlineData(SpiShaderUserDataPs0, 32)]
    [InlineData(SpiShaderUserDataGs0, 32)]
    [InlineData(SpiShaderUserDataHs0, 32)]
    [InlineData(ComputeUserData0, 16)]
    public void MixedPartialWritesAgreeWithIndependentStateAndPreserveAllEarlierSnapshots(uint first, int capacity)
    {
        var banks = Banks();
        var random = new System.Random(4729);
        var words = new uint[capacity];
        var kinds = new UserScalarKind[capacity];
        uint count = 0;
        var history = new List<(RegisterBanks Banks, uint[] Words, UserScalarKind[] Kinds, uint Count)>();
        for (var step = 0; step < 200; step++)
        {
            var slot = random.Next(capacity);
            var length = random.Next(1, capacity - slot + 1);
            var marker = (UserScalarKind)random.Next(3);
            var values = words.AsSpan(slot, length).ToArray();
            if (step % 3 == 0) values[^1] = (uint)random.Next();
            banks.UserDataMarker = marker;
            Assert.Equal((uint)length, Write(banks, first + (uint)slot, values));
            values.CopyTo(words, slot);
            Array.Fill(kinds, marker, slot, length);
            count = Math.Max(count, (uint)(slot + length));
            history.Add((banks.Snapshot(), (uint[])words.Clone(), (UserScalarKind[])kinds.Clone(), count));
        }
        foreach (var saved in history)
        {
            var scalars = Scalars(saved.Banks, first);
            Assert.Equal(saved.Count, scalars.Count);
            Assert.Equal(saved.Words, ((ReadOnlySpan<uint>)scalars.Values)[..capacity].ToArray());
            Assert.Equal(saved.Kinds, ((ReadOnlySpan<UserScalarKind>)scalars.Kinds)[..capacity].ToArray());
            Assert.Equal(UserScalarKind.Unknown, saved.Banks.UserDataMarker);
        }
    }

    [Fact]
    public void MeasureScalarSnapshotWritesWhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("SHARPEMU_SCALAR_REUSE_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        const int count = 100000;
        var rows = new List<object>();
        foreach (var length in new[] { 2, 16, 32 })
        foreach (var pattern in new[] { "identical", "alternating", "mostly-changing" })
        {
            var banks = Banks();
            var values = Enumerable.Range(0, length).Select(index => (uint)index).ToArray();
            Exercise(banks, values, pattern, 10000);
            for (var trial = 0; trial < 7; trial++)
            {
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var checksum = Exercise(banks, values, pattern, count);
                var duration = Stopwatch.GetElapsedTime(start);
                rows.Add(new { length, pattern, trial, checksum, nsPerWrite = duration.TotalNanoseconds / count,
                    bytesPerWrite = (double)(GC.GetAllocatedBytesForCurrentThread() - bytes) / count });
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { enabled = Enabled,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), rows }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Exercise(RegisterBanks banks, uint[] values, string pattern, int count)
    {
        ulong checksum = 0;
        for (var i = 0; i < count; i++)
        {
            if (pattern == "alternating") values[^1] = (uint)i;
            else if (pattern == "mostly-changing" && i % 8 != 0) values[^1] = (uint)i;
            Write(banks, SpiShaderUserDataPs0, values);
            var snapshot = banks.Snapshot();
            checksum += snapshot.Shader.Pixel.UserScalars.Values[values.Length - 1];
        }
        return checksum;
    }
}
