// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// Compute scenes: atomics, LDS and barriers, multi-dimensional and indirect dispatch,
/// GPU-driven indirect draws, and the hierarchical-Z chain a UE4 title builds for GPU
/// occlusion culling. Every expectation is derived in C# from the hardware rules.
/// </summary>
public sealed partial class ComputeSceneTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint BufferFormat = 20;
    private const uint OpDrawIndirectMulti = 0x2C;
    private const uint OpDrawIndexIndirectMulti = 0x38;

    /// <summary>Compute registers and the user data for one dispatch, without the dispatch packet.</summary>
    private static ScenePm4 Setup(SceneRig rig, ScenePm4 pm4, uint[] program, uint[] userData, uint threadsX,
        uint threadsY = 1, uint threadsZ = 1, bool groupIdY = false, bool groupIdZ = false, uint threadIdComponents = 0,
        uint ldsBytes = 0)
    {
        var state = new SceneState(1, 1);
        state.Context.Clear();
        state.Uconfig.Clear();
        state.ComputeShader(rig.Program(program), threadsX, threadsY, threadsZ, userData, true, groupIdY, groupIdZ,
            threadIdComponents, ldsBytes);
        return state.Emit(pm4);
    }

    private static uint[] Vsharp(ulong address, uint stride, uint records) => Descriptors.Buffer(address, stride, records, BufferFormat);

    private static ulong Fill(SceneRig rig, uint dwords, uint value)
    {
        var address = rig.Memory.Allocate(dwords * 4ul, 256);
        rig.Memory.Write<uint>(address, Enumerable.Repeat(value, (int)dwords).ToArray());
        return address;
    }

    private static ScenePm4 CsFlush(ScenePm4 pm4) => pm4.Event(ScenePm4.EventCsPartialFlush);

    /// <summary>SET_BASE index 1 for the compute (header bit 1) or draw (clear) indirect arguments.</summary>
    private static ScenePm4 SetBase(ScenePm4 pm4, ulong address, bool compute) =>
        pm4.Raw(ScenePm4.Header(4, ScenePm4.OpSetBase) | (compute ? 2u : 0u), 1u, (uint)address, (uint)(address >> 32));

    // ------------------------------------------------------------------ atomics

    public static TheoryData<string, uint, uint, bool> AtomicCases => new()
    {
        { nameof(ComputeScenePrograms.AtomicAdd), 0u, 192u, false },
        { nameof(ComputeScenePrograms.AtomicAddReturn), 0u, 192u, true },
        { nameof(ComputeScenePrograms.AtomicUmax), 0u, 191u, false },
        { nameof(ComputeScenePrograms.AtomicUmin), 0xFFFFFFFFu, 5u, false },
        { nameof(ComputeScenePrograms.AtomicSmax), 0x80000000u, 91u, false },
        { nameof(ComputeScenePrograms.AtomicSmin), 0x7FFFFFFFu, unchecked((uint)-100), false },
        { nameof(ComputeScenePrograms.AtomicAnd), 0xFFFFFFFFu, 0u, false },
        { nameof(ComputeScenePrograms.AtomicOr), 0u, 0xFFFFFFFFu, false },
        { nameof(ComputeScenePrograms.AtomicXor), 0u, 0xFFFFFFFFu, false },
        { nameof(ComputeScenePrograms.AtomicInc), 0u, IncExpected(0, 192), false },
        { nameof(ComputeScenePrograms.AtomicDec), 0u, DecExpected(0, 192), false },
        { nameof(ComputeScenePrograms.AtomicAddReturnUmax), 0u, 191u, true },
        { nameof(ComputeScenePrograms.AtomicSwapReturn), 0xDEADu, 0u, true },
        { nameof(ComputeScenePrograms.AtomicCmpSwapReturn), 0u, 0u, true },
        { nameof(ComputeScenePrograms.AtomicCmpSwapNoReturn), 0u, 0u, false },
    };

    private static uint IncExpected(uint value, int times)
    {
        for (var index = 0; index < times; index++) value = value >= 9 ? 0 : value + 1;
        return value;
    }

    private static uint DecExpected(uint value, int times)
    {
        for (var index = 0; index < times; index++) value = value == 0 || value > 9 ? 9 : value - 1;
        return value;
    }

    private static uint[] Program(string name) =>
        (uint[])typeof(ComputeScenePrograms).GetField(name)!.GetValue(null)!;

    /// <summary>Three groups of 64 threads all hit one word of the counter buffer.</summary>
    [Theory]
    [MemberData(nameof(AtomicCases))]
    public void BufferAtomicsAccumulateEveryThread(string program, uint initial, uint expected, bool returns)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint groups = 3;
        const uint threads = groups * 64;
        var counter = Fill(rig, 16, 0);
        rig.Memory.Write32(counter, initial);
        var returned = Fill(rig, threads, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), Program(program), [.. Vsharp(counter, 4, 16), .. Vsharp(returned, 4, threads)], 64)
            .DispatchDirect(groups, 1, 1);
        rig.Submit(pm4);
        var final = rig.Read<uint>(counter, 1)[0];
        var back = rig.Read<uint>(returned, (int)threads);

        switch (program)
        {
            case nameof(ComputeScenePrograms.AtomicAddReturn):
                Assert.Equal(expected, final);
                Assert.Equal(Enumerable.Range(0, (int)threads).Select(i => (uint)i), back.OrderBy(v => v));
                break;
            case nameof(ComputeScenePrograms.AtomicAddReturnUmax):
                Assert.Equal(expected, final);
                // Each returned value is the maximum so far: never above the final, and the thread that
                // wrote 191 saw something below it.
                Assert.All(back, value => Assert.True(value <= 191, $"returned {value}"));
                Assert.True(back[191] < 191, $"thread 191 returned {back[191]}");
                break;
            case nameof(ComputeScenePrograms.AtomicSwapReturn):
                // The chain of swaps is a permutation: old values plus the final word are {initial, 1..192}.
                var seen = back.Append(final).OrderBy(v => v).ToArray();
                var want = new[] { initial }.Concat(Enumerable.Range(1, (int)threads).Select(i => (uint)i)).OrderBy(v => v).ToArray();
                Assert.Equal(want, seen);
                break;
            case nameof(ComputeScenePrograms.AtomicCmpSwapReturn):
                Assert.InRange(final, 1u, threads);
                Assert.Single(back, 0u);
                var winner = Array.IndexOf(back, 0u);
                Assert.Equal((uint)winner + 1, final);
                Assert.All(back.Where((_, i) => i != winner), value => Assert.Equal(final, value));
                break;
            case nameof(ComputeScenePrograms.AtomicCmpSwapNoReturn):
                Assert.InRange(final, 1u, threads);
                break;
            default:
                Assert.Equal(expected, final);
                break;
        }
    }

    [Fact]
    public void GlobalAtomicAddThroughAGuestPointer()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint threads = 192;
        var counter = Fill(rig, 16, 0);
        var returned = Fill(rig, threads, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), ComputeScenePrograms.GlobalAtomicAdd,
                [Descriptors.Lo(counter), Descriptors.Hi(counter), 0, 0, .. Vsharp(returned, 4, threads)], 64)
            .DispatchDirect(3, 1, 1);
        rig.Submit(pm4);
        Assert.Equal(threads, rig.Read<uint>(counter, 1)[0]);
        Assert.Equal(Enumerable.Range(0, (int)threads).Select(i => (uint)i), rig.Read<uint>(returned, (int)threads).OrderBy(v => v));
    }

    [Fact]
    public void GlobalLoadsAndStoresThroughAGuestPointer()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint threads = 128;
        var data = rig.Memory.Upload<uint>(Enumerable.Range(0, 512).Select(i => (uint)i * 3).ToArray());
        var returned = Fill(rig, threads, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), ComputeScenePrograms.GlobalLoadStore,
                [Descriptors.Lo(data), Descriptors.Hi(data), 0, 0, .. Vsharp(returned, 4, threads)], 64)
            .DispatchDirect(2, 1, 1);
        rig.Submit(pm4);
        var loaded = rig.Read<uint>(returned, (int)threads);
        var stored = rig.Read<uint>(data + 1024, (int)threads);
        for (var index = 0; index < threads; index++)
        {
            Assert.Equal((uint)(index * 3) + 1000, loaded[index]);
            Assert.Equal((uint)(index * 3) + 1000, stored[index]);
        }
    }

    [Fact]
    public void AScalarLoadWithAnSgprOffsetReadsTheAddressedRecord()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint records = 6;
        var table = rig.Memory.Upload<uint>(Enumerable.Range(0, (int)records * 4).Select(i => 0x1000u + (uint)i).ToArray(), 16);
        var output = Fill(rig, records * 4, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), ComputeScenePrograms.ScalarLoadSgprOffset,
                [Descriptors.Lo(table), Descriptors.Hi(table), 0, 0, .. Vsharp(output, 4, records * 4)], 64)
            .DispatchDirect(records, 1, 1);
        rig.Submit(pm4);
        Assert.Equal(Enumerable.Range(0, (int)records * 4).Select(i => 0x1000u + (uint)i), rig.Read<uint>(output, (int)records * 4));
    }

    [Fact]
    public void AScalarLoadWithAnImmediateOffsetReadsTheAddressedRecord()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var table = rig.Memory.Upload<uint>(Enumerable.Range(0, 24).Select(i => 0x1000u + (uint)i).ToArray(), 16);
        var output = Fill(rig, 8, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), ComputeScenePrograms.ScalarLoadImmediateOffset,
                [Descriptors.Lo(table), Descriptors.Hi(table), 0, 0, .. Vsharp(output, 4, 8)], 64)
            .DispatchDirect(1, 1, 1);
        rig.Submit(pm4);
        Assert.Equal(new[] { 0x1004u, 0x1005u, 0x1006u, 0x1007u }, rig.Read<uint>(output, 4));
    }

    // ------------------------------------------------------------------ LDS and barriers

    [Theory]
    [InlineData(false, 1024u)]
    [InlineData(true, 0xF400u)]
    public void ALdsTreeReductionWithBarriersSumsEveryGroup(bool high, uint ldsBytes)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint groups = 3;
        var random = new Random(7);
        var values = Enumerable.Range(0, (int)(groups * 256)).Select(_ => (uint)random.Next(0, 1 << 20)).ToArray();
        var input = rig.Memory.Upload<uint>(values);
        var output = Fill(rig, groups, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), high ? ComputeScenePrograms.LdsReduceHigh : ComputeScenePrograms.LdsReduce,
                [.. Vsharp(input, 4, groups * 256), .. Vsharp(output, 4, groups)], 256, ldsBytes: ldsBytes)
            .DispatchDirect(groups, 1, 1);
        rig.Submit(pm4);
        var sums = rig.Read<uint>(output, (int)groups);
        for (var group = 0; group < groups; group++)
        {
            var expected = 0u;
            for (var index = 0; index < 256; index++) expected += values[(group * 256) + index];
            Assert.Equal(expected, sums[group]);
        }
    }

    [Fact]
    public void LdsAtomicsAreSharedInAGroupAndPrivateBetweenGroups()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint groups = 2;
        var results = Fill(rig, groups * 8, 0xFFFFFFFF);
        var returned = Fill(rig, groups * 256, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), ComputeScenePrograms.LdsAtomics,
                [.. Vsharp(results, 4, groups * 8), .. Vsharp(returned, 4, groups * 256)], 256, ldsBytes: 28)
            .DispatchDirect(groups, 1, 1);
        rig.Submit(pm4);
        var words = rig.Read<uint>(results, (int)(groups * 8));
        var rtn = rig.Read<uint>(returned, (int)(groups * 256));
        for (var group = 0; group < groups; group++)
        {
            var w = words.Skip(group * 8).Take(8).ToArray();
            Assert.Equal(new[] { 256u, 255u, 7u, 256u, 0u, 0xFFFFFFFFu, IncExpected(0, 256) }, w.Take(7));
            Assert.Equal(Enumerable.Range(0, 256).Select(i => (uint)i), rtn.Skip(group * 256).Take(256).OrderBy(v => v));
        }
    }

    // ------------------------------------------------------------------ multi-dimensional dispatch

    public static TheoryData<string, uint, uint, uint, uint, uint, uint, uint, bool, bool> MultiDimCases => new()
    {
        { nameof(ComputeScenePrograms.MultiDimX), 64, 1, 1, 0, 5, 1, 1, false, false },
        { nameof(ComputeScenePrograms.MultiDimXY), 8, 8, 1, 1, 3, 4, 1, true, false },
        { nameof(ComputeScenePrograms.MultiDimXY), 16, 4, 1, 1, 2, 3, 1, true, false },
        { nameof(ComputeScenePrograms.MultiDimXYZ), 4, 4, 4, 2, 3, 2, 5, true, true },
        { nameof(ComputeScenePrograms.MultiDimXZ), 4, 4, 2, 2, 2, 1, 3, false, true },
    };

    private static (uint Global, uint Local) MultiDimExpected(uint x, uint y, uint z, uint tx, uint ty, uint tz, bool gy, bool gz)
    {
        var groupX = x / tx;
        var groupY = gy ? y / ty : 0;
        var groupZ = gz ? z / tz : 0;
        var lx = x % tx;
        var ly = gy ? y % ty : y;
        var lz = gz ? z % tz : z;
        return (x | (y << 10) | (z << 20), lx | (ly << 6) | (lz << 12) | (groupX << 16) | (groupY << 22) | (groupZ << 26));
    }

    [Theory]
    [MemberData(nameof(MultiDimCases))]
    public void EveryThreadSeesItsGlobalLocalAndGroupIds(string program, uint tx, uint ty, uint tz, uint comps, uint gx,
        uint gy, uint gz, bool enableY, bool enableZ)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        uint totalX = gx * tx, totalY = gy * ty, totalZ = gz * tz;
        var total = totalX * totalY * totalZ;
        var output = Fill(rig, total * 2, 0xFFFFFFFF);
        var pm4 = Setup(rig, new ScenePm4(), Program(program),
                [.. Vsharp(output, 8, total), totalX, totalY, tx, ty, tz], tx, ty, tz, enableY, enableZ, comps)
            .DispatchDirect(gx, gy, gz);
        rig.Submit(pm4);
        var records = rig.Read<uint>(output, (int)(total * 2));
        for (uint z = 0; z < totalZ; z++)
        for (uint y = 0; y < totalY; y++)
        for (uint x = 0; x < totalX; x++)
        {
            var index = ((z * totalY) + y) * totalX + x;
            var (global, local) = MultiDimExpected(x, y, z, tx, ty, tz, enableY, enableZ);
            Assert.True(global == records[index * 2] && local == records[(index * 2) + 1],
                $"thread ({x},{y},{z}): global 0x{records[index * 2]:X8} vs 0x{global:X8}, local/group 0x{records[(index * 2) + 1]:X8} vs 0x{local:X8}");
        }
    }

    // ------------------------------------------------------------------ indirect dispatch

    private static void CheckMultiDimThreads(SceneRig rig, ulong output, uint gx, uint gy, uint gz, uint tx, uint ty, uint tz,
        uint totalX, uint totalY, uint maxTotal)
    {
        var records = rig.Read<uint>(output, (int)(maxTotal * 2));
        var written = 0;
        for (var index = 0; index < maxTotal; index++)
        {
            if (records[index * 2] != 0xFFFFFFFF || records[(index * 2) + 1] != 0xFFFFFFFF) written++;
        }

        Assert.Equal(gx * gy * gz * tx * ty * tz, (uint)written);
        for (uint z = 0; z < gz * tz; z++)
        for (uint y = 0; y < gy * ty; y++)
        for (uint x = 0; x < gx * tx; x++)
        {
            var index = ((z * totalY) + y) * totalX + x;
            var (global, local) = MultiDimExpected(x, y, z, tx, ty, tz, true, true);
            Assert.True(global == records[index * 2] && local == records[(index * 2) + 1],
                $"thread ({x},{y},{z}): global 0x{records[index * 2]:X8} vs 0x{global:X8}, local/group 0x{records[(index * 2) + 1]:X8} vs 0x{local:X8}");
        }
    }

    public enum IndirectForm { SetBaseAndOffset, InlineAddress }

    /// <summary>A dispatch whose group counts were written by an earlier dispatch in the same stream.</summary>
    [Theory]
    [InlineData(IndirectForm.SetBaseAndOffset, 16u, 3u, 2u, 2u)]
    [InlineData(IndirectForm.SetBaseAndOffset, 0u, 1u, 1u, 1u)]
    [InlineData(IndirectForm.SetBaseAndOffset, 32u, 5u, 1u, 3u)]
    [InlineData(IndirectForm.InlineAddress, 16u, 3u, 2u, 2u)]
    public void ADispatchTakesItsGroupCountsFromAGpuWrittenBuffer(IndirectForm form, uint offset, uint gx, uint gy, uint gz)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint tx = 4, ty = 4, tz = 4;
        uint totalX = 6 * tx, totalY = 3 * ty, maxTotal = totalX * totalY * 6 * tz;
        var arguments = Fill(rig, 32, 0xAAAAAAAA);
        var output = Fill(rig, maxTotal * 2, 0xFFFFFFFF);
        var pm4 = new ScenePm4();
        // Producer: one group of 64 threads writes {gx, gy, gz} at arguments + offset.
        Setup(rig, pm4, ComputeScenePrograms.WriteDispatchArgs, [.. Vsharp(arguments + offset, 4, 3), gx, gy, gz], 64)
            .DispatchDirect(1, 1, 1);
        CsFlush(pm4);
        Setup(rig, pm4, ComputeScenePrograms.MultiDimXYZ,
            [.. Vsharp(output, 8, maxTotal), totalX, totalY, tx, ty, tz], tx, ty, tz, true, true, 2);
        if (form == IndirectForm.SetBaseAndOffset)
        {
            SetBase(pm4, arguments, compute: true);
            pm4.Packet(ScenePm4.OpDispatchIndirect, offset, 1u);
        }
        else
        {
            var address = arguments + offset;
            pm4.Packet(ScenePm4.OpDispatchIndirect, (uint)address, (uint)(address >> 32), 1u);
        }

        rig.Submit(pm4);
        Assert.Equal(new[] { gx, gy, gz }, rig.Read<uint>(arguments + offset, 3));
        CheckMultiDimThreads(rig, output, gx, gy, gz, tx, ty, tz, totalX, totalY, maxTotal);
    }

    [Fact]
    public void AnIndirectDispatchOfZeroGroupsRunsNothing()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var arguments = Fill(rig, 4, 0xAAAAAAAA);
        var output = Fill(rig, 64 * 2, 0xFFFFFFFF);
        var pm4 = new ScenePm4();
        Setup(rig, pm4, ComputeScenePrograms.WriteDispatchArgs, [.. Vsharp(arguments, 4, 3), 0u, 0u, 0u], 64).DispatchDirect(1, 1, 1);
        CsFlush(pm4);
        Setup(rig, pm4, ComputeScenePrograms.MultiDimX, [.. Vsharp(output, 8, 64), 64u, 1u, 64u, 1u, 1u], 64);
        SetBase(pm4, arguments, compute: true);
        pm4.Packet(ScenePm4.OpDispatchIndirect, 0u, 1u);
        rig.Submit(pm4);
        Assert.All(rig.Read<uint>(output, 128), value => Assert.Equal(0xFFFFFFFFu, value));
    }

    // ------------------------------------------------------------------ hierarchical Z

    private sealed record Rect(int X0, int Y0, int X1, int Y1, float Z);

    /// <summary>Occluders as fractions of the target, so one scene serves every target size. Exclusive max edges.</summary>
    private static Rect[] Occluders(int width, int height)
    {
        static int At(double fraction, int size) => (int)Math.Round(fraction * size);
        return
        [
            new Rect(At(0.10, width), At(0.17, height), At(0.50, width), At(0.75, height), 0.5f),
            new Rect(At(0.25, width), At(0.33, height), At(0.85, width), At(0.92, height), 0.25f),
            new Rect(0, 0, width, At(0.125, height), 0.75f),
            new Rect(At(0.75, width), At(0.04, height), At(0.79, width), At(0.10, height), 0.125f),
        ];
    }

    /// <summary>The depth buffer the occluders leave behind: cleared to 1, LESS_EQUAL keeps the nearest.</summary>
    private static float[] DepthMap(int width, int height)
    {
        var depth = Enumerable.Repeat(1f, width * height).ToArray();
        foreach (var rect in Occluders(width, height))
        {
            for (var y = rect.Y0; y < rect.Y1; y++)
            for (var x = rect.X0; x < rect.X1; x++)
            {
                depth[(y * width) + x] = Math.Min(depth[(y * width) + x], rect.Z);
            }
        }

        return depth;
    }

    /// <summary>The mip chain: every level is the 2x2 max (or min) of the previous one; odd edges clamp, sizes round up.</summary>
    private static List<(int Width, int Height, float[] Texels)> HzbOracle(float[] depth, int width, int height, bool max)
    {
        var levels = new List<(int, int, float[])> { (width, height, depth) };
        while (width > 1 || height > 1)
        {
            var source = levels[^1].Item3;
            var sourceWidth = width;
            var sourceHeight = height;
            width = (width + 1) / 2;
            height = (height + 1) / 2;
            var next = new float[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var x1 = Math.Min((2 * x) + 1, sourceWidth - 1);
                var y1 = Math.Min((2 * y) + 1, sourceHeight - 1);
                float[] taps =
                [
                    source[(2 * y * sourceWidth) + (2 * x)], source[(2 * y * sourceWidth) + x1],
                    source[(y1 * sourceWidth) + (2 * x)], source[(y1 * sourceWidth) + x1],
                ];
                next[(y * width) + x] = max ? taps.Max() : taps.Min();
            }

            levels.Add((width, height, next));
        }

        return levels;
    }

    private static float NdcX(int pixel, int width) => (pixel * 2f / width) - 1f;

    private static float NdcY(int pixel, int height) => 1f - (pixel * 2f / height);

    /// <summary>Draws the occluders into a D32F target (a full-target far quad first) and returns the depth address.</summary>
    private static ulong RenderDepth(SceneRig rig, ScenePm4 pm4, int width, int height)
    {
        var depth = rig.Memory.Allocate(0x40000, 0x10000);
        var color = rig.ColorTarget((uint)width, (uint)height);
        var always = new SceneState((uint)width, (uint)height).ColorTarget(0, color, (uint)width, (uint)height)
            .DepthTarget(depth, (uint)width, (uint)height, zFormat: 3).DepthControl(test: true, write: true, CompareFunc.Always);
        rig.DrawMesh(pm4, always, SceneDraws.Quad(1f, 0, 0, 0));
        foreach (var rect in Occluders(width, height))
        {
            var state = new SceneState((uint)width, (uint)height).ColorTarget(0, color, (uint)width, (uint)height)
                .DepthTarget(depth, (uint)width, (uint)height, zFormat: 3).DepthControl(test: true, write: true, CompareFunc.LessEqual);
            rig.DrawMesh(pm4, state, SceneDraws.Quad(rect.Z, 1, 1, 1, 1,
                NdcX(rect.X0, width), NdcY(rect.Y1, height), NdcX(rect.X1, width), NdcY(rect.Y0, height)));
        }

        return depth;
    }

    private sealed record HzbLevel(ulong Address, uint Width, uint Height);

    /// <summary>Appends the copy and reduction dispatches that build the chain; returns the levels' images.</summary>
    private static List<HzbLevel> BuildHzb(SceneRig rig, ScenePm4 pm4, ulong depth, uint width, uint height, bool max)
    {
        var levels = new List<HzbLevel>();
        uint levelWidth = width, levelHeight = height;
        var sourceTexture = Descriptors.Texture(depth, 22, width, height, tileMode: 24);
        var sourceWidth = width;
        var sourceHeight = height;
        for (var level = 0; ; level++)
        {
            var image = rig.ColorTarget(levelWidth, levelHeight, 4);
            levels.Add(new HzbLevel(image, levelWidth, levelHeight));
            var table = rig.Memory.Upload<uint>(
                [.. Descriptors.Texture(image, 22, levelWidth, levelHeight), sourceWidth, sourceHeight, levelWidth, levelHeight], 16);
            Setup(rig, pm4, level == 0 ? ComputeScenePrograms.HzbCopy : max ? ComputeScenePrograms.HzbReduceMax : ComputeScenePrograms.HzbReduceMin,
                [.. sourceTexture, Descriptors.Lo(table), Descriptors.Hi(table)], 8, 8, 1, groupIdY: true, threadIdComponents: 1)
                .DispatchDirect((levelWidth + 7) / 8, (levelHeight + 7) / 8, 1);
            CsFlush(pm4);
            if (levelWidth == 1 && levelHeight == 1)
            {
                break;
            }

            sourceTexture = Descriptors.Texture(image, 22, levelWidth, levelHeight);
            sourceWidth = levelWidth;
            sourceHeight = levelHeight;
            levelWidth = (levelWidth + 1) / 2;
            levelHeight = (levelHeight + 1) / 2;
        }

        return levels;
    }

    private static void PrepareDepthForCompute(ScenePm4 pm4)
    {
        pm4.Event(ScenePm4.EventPsPartialFlush);
        pm4.Event(ScenePm4.EventCacheFlushAndInvalidate);
    }

    [Fact]
    public void TheDepthMapOfTheOccludersReadsBackThroughAComputeImageLoad()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const int width = 80, height = 48;
        var pm4 = new ScenePm4();
        var depth = RenderDepth(rig, pm4, width, height);
        PrepareDepthForCompute(pm4);
        var levels = BuildHzb(rig, pm4, depth, width, height, max: true);
        rig.Submit(pm4);
        var expected = DepthMap(width, height);
        var actual = rig.ReadImage<float>(levels[0].Address, width, height);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(80, 48, true)]
    [InlineData(80, 48, false)]
    [InlineData(37, 23, true)]
    [InlineData(37, 23, false)]
    public void TheHzbChainMatchesTheReductionOfTheDepthMap(int width, int height, bool max)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pm4 = new ScenePm4();
        var depth = RenderDepth(rig, pm4, width, height);
        PrepareDepthForCompute(pm4);
        var levels = BuildHzb(rig, pm4, depth, (uint)width, (uint)height, max);
        rig.Submit(pm4);
        var oracle = HzbOracle(DepthMap(width, height), width, height, max);
        Assert.Equal(oracle.Count, levels.Count);
        for (var level = 0; level < oracle.Count; level++)
        {
            Assert.Equal((uint)oracle[level].Width, levels[level].Width);
            var actual = rig.ReadImage<float>(levels[level].Address, levels[level].Width, levels[level].Height);
            var mismatches = Enumerable.Range(0, actual.Length).Where(i => actual[i] != oracle[level].Texels[i]).ToArray();
            Assert.True(mismatches.Length == 0,
                $"level {level} ({oracle[level].Width}x{oracle[level].Height}) differs at {mismatches.Length} texels, first {mismatches.FirstOrDefault()}: " +
                $"{(mismatches.Length > 0 ? actual[mismatches[0]] : 0)} vs {(mismatches.Length > 0 ? oracle[level].Texels[mismatches[0]] : 0)}");
        }
    }

    private sealed record Candidate(int X0, int Y0, int X1, int Y1, float NearestZ);

    /// <summary>Handcrafted and seeded screen-space bounds (inclusive pixels) for the occlusion test.</summary>
    private static List<Candidate> Candidates(int width, int height)
    {
        var list = new List<Candidate>();
        int Px(double f) => Math.Clamp((int)(f * width), 0, width - 1);
        int Py(double f) => Math.Clamp((int)(f * height), 0, height - 1);
        // Small bounds well inside the near occluder: behind it (culled) and in front of it (visible).
        list.Add(new Candidate(Px(0.4), Py(0.5), Px(0.4) + 1, Py(0.5) + 1, 0.5f));
        list.Add(new Candidate(Px(0.4), Py(0.5), Px(0.4) + 1, Py(0.5) + 1, 0.125f));
        list.Add(new Candidate(Px(0.4), Py(0.5), Px(0.4) + 1, Py(0.5) + 1, 0.25f));
        // A single pixel and a single column.
        list.Add(new Candidate(Px(0.5), Py(0.5), Px(0.5), Py(0.5), 0.375f));
        list.Add(new Candidate(Px(0.5), Py(0.4), Px(0.5), Py(0.6), 0.375f));
        // The whole target, behind everything and in front of everything.
        list.Add(new Candidate(0, 0, width - 1, height - 1, 1f));
        list.Add(new Candidate(0, 0, width - 1, height - 1, 0.0625f));
        // Straddling the near occluder's edge: the far background shows through, so visible.
        list.Add(new Candidate(Px(0.7), Py(0.8), Px(0.95), Py(0.98), 0.5f));
        // Behind the top strip.
        list.Add(new Candidate(1, 0, Math.Min(5, width - 1), 1, 0.875f));
        var random = new Random(1234);
        float[] depths = [0.0625f, 0.125f, 0.1875f, 0.375f, 0.5f, 0.625f, 0.875f, 1f];
        while (list.Count < 40)
        {
            var x0 = random.Next(0, width);
            var y0 = random.Next(0, height);
            var x1 = Math.Min(width - 1, x0 + random.Next(0, 24));
            var y1 = Math.Min(height - 1, y0 + random.Next(0, 24));
            list.Add(new Candidate(x0, y0, x1, y1, depths[random.Next(depths.Length)]));
        }

        return list;
    }

    private static int BitLength(int value) => value == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);

    /// <summary>
    /// The shader's rule: the level whose texels span the bounds in at most 2x2 taps, the max of those taps,
    /// visible when the nearest depth is not behind it. Returns the flag.
    /// </summary>
    private static bool HzbVisible(Candidate candidate, List<(int Width, int Height, float[] Texels)> levels)
    {
        var d = Math.Max(candidate.X1 - candidate.X0, candidate.Y1 - candidate.Y0);
        var level = Math.Min(BitLength(d), levels.Count - 1);
        var (w, h, texels) = levels[level];
        int Tx(int x) => Math.Min(x >> level, w - 1);
        int Ty(int y) => Math.Min(y >> level, h - 1);
        var maxDepth = new[]
        {
            texels[(Ty(candidate.Y0) * w) + Tx(candidate.X0)], texels[(Ty(candidate.Y0) * w) + Tx(candidate.X1)],
            texels[(Ty(candidate.Y1) * w) + Tx(candidate.X0)], texels[(Ty(candidate.Y1) * w) + Tx(candidate.X1)],
        }.Max();
        return candidate.NearestZ <= maxDepth;
    }

    private static ulong UploadHzbTables(SceneRig rig, List<HzbLevel> levels)
    {
        var words = new List<uint>();
        foreach (var level in levels)
        {
            words.AddRange(Descriptors.Texture(level.Address, 22, level.Width, level.Height));
            words.AddRange([level.Width, level.Height, 0, 0, 0, 0, 0, 0]);
        }

        return rig.Memory.Upload<uint>(words.ToArray(), 16);
    }

    private static ulong UploadCandidates(SceneRig rig, List<Candidate> candidates) =>
        rig.Memory.Upload<uint>(candidates.SelectMany(c => new uint[]
            { (uint)c.X0, (uint)c.Y0, (uint)c.X1, (uint)c.Y1, SceneState.F(c.NearestZ), 0, 0, 0 }).ToArray(), 16);

    [Theory]
    [InlineData(80, 48, true)]
    [InlineData(37, 23, true)]
    [InlineData(80, 48, false)]
    [InlineData(37, 23, false)]
    public void TheOcclusionTestFlagsMatchTheHzbRuleAndNeverCullAVisibleObject(int width, int height, bool staticLevels)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var candidates = Candidates(width, height);
        var pm4 = new ScenePm4();
        var depth = RenderDepth(rig, pm4, width, height);
        PrepareDepthForCompute(pm4);
        var levels = BuildHzb(rig, pm4, depth, (uint)width, (uint)height, max: true);
        var flags = Fill(rig, (uint)candidates.Count, 0xFFFFFFFF);
        var objects = UploadCandidates(rig, candidates);
        var tables = UploadHzbTables(rig, levels);
        Setup(rig, pm4, staticLevels ? ComputeScenePrograms.HzbOcclusionStaticLevels : ComputeScenePrograms.HzbOcclusionDynamicTable,
            [Descriptors.Lo(tables), Descriptors.Hi(tables), 0, 0, .. Vsharp(objects, 32, (uint)candidates.Count),
             .. Vsharp(flags, 4, (uint)candidates.Count), (uint)levels.Count - 1], 64)
            .DispatchDirect((uint)candidates.Count, 1, 1);
        rig.Submit(pm4);
        var oracle = HzbOracle(DepthMap(width, height), width, height, true);
        var depthMap = DepthMap(width, height);
        var actual = rig.Read<uint>(flags, candidates.Count);
        var culledCount = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var visible = HzbVisible(candidate, oracle);
            // Ground truth: the bounds are really hidden when every pixel inside holds something nearer.
            var hidden = true;
            for (var y = candidate.Y0; y <= candidate.Y1; y++)
            for (var x = candidate.X0; x <= candidate.X1; x++)
            {
                hidden &= depthMap[(y * width) + x] < candidate.NearestZ;
            }

            Assert.True(!(actual[index] == 0 && !hidden), $"candidate {index} {candidate} was culled but is visible: flag {actual[index]}");
            Assert.True(actual[index] == (visible ? 1u : 0u), $"candidate {index} {candidate}: flag {actual[index]}, the rule says {visible}");
            culledCount += visible ? 0 : 1;
        }

        Assert.InRange(culledCount, 1, candidates.Count - 1);
    }

    // ------------------------------------------------------------------ indirect draws

    private const uint DrawSize = 64;

    private static uint ObjectColor(int index) => (uint)(index % 7) + 1;

    private static uint ObjectPixel(int index)
    {
        var c = ObjectColor(index);
        return 0xFF000000u | ((c >> 2 & 1) * 0xFF0000u) | ((c >> 1 & 1) * 0xFF00u) | ((c & 1) * 0xFFu);
    }

    /// <summary>A quad over pixel rect [x0,x1) x [y0,y1) whose four vertices are listed bottom-left, bottom-right, top-left, top-right.</summary>
    private static SceneVertex[] QuadVertices(int index, float z, int x0, int y0, int x1, int y1, int width, int height)
    {
        var c = ObjectColor(index);
        float r = c & 1, g = c >> 1 & 1, b = c >> 2 & 1;
        float l = NdcX(x0, width), rt = NdcX(x1, width), bt = NdcY(y1, height), tp = NdcY(y0, height);
        return
        [
            SceneVertex.At(l, bt, z, r, g, b), SceneVertex.At(rt, bt, z, r, g, b),
            SceneVertex.At(l, tp, z, r, g, b), SceneVertex.At(rt, tp, z, r, g, b),
        ];
    }

    private static SceneVertex[] AsTriangles(SceneVertex[] quad) => [quad[0], quad[1], quad[2], quad[2], quad[1], quad[3]];

    private static SceneState ObjectState(SceneRig rig, ulong target, uint width, uint height, ulong vertexProgram, SceneVertex[] mesh)
    {
        var state = new SceneState(width, height).ColorTarget(0, target, width, height);
        state.VertexShader(vertexProgram, rig.UploadMesh(mesh));
        state.PixelShader(rig.Program(ScenePrograms.InterpolatedColor));
        return state;
    }

    /// <summary>Dispatches the culling program: args[k] = { count, flags[k] ? instances : 0, 0, 0 (, 0) }.</summary>
    private static void WriteDrawArguments(SceneRig rig, ScenePm4 pm4, ulong arguments, ulong flags, uint count, uint instances,
        uint draws, uint recordDwords)
    {
        Setup(rig, pm4, ComputeScenePrograms.CullToDrawArgs,
                [.. Vsharp(arguments, 4, draws * recordDwords), .. Vsharp(flags, 4, draws), count, instances, draws, recordDwords], 64)
            .DispatchDirect(1, 1, 1);
        CsFlush(pm4);
    }

    private static uint[] ExpectedPixels(IEnumerable<(int Index, int X0, int Y0, int X1, int Y1)> visible, int instanceStepPixels, Func<int, uint> instances)
    {
        var pixels = new uint[DrawSize * DrawSize];
        foreach (var (index, x0, y0, x1, y1) in visible)
        {
            for (var instance = 0; instance < instances(index); instance++)
            for (var y = y0; y < y1; y++)
            for (var x = x0 + (instance * instanceStepPixels); x < x1 + (instance * instanceStepPixels) && x < DrawSize; x++)
            {
                pixels[(y * DrawSize) + x] = ObjectPixel(index);
            }
        }

        return pixels;
    }

    private static (int Index, int X0, int Y0, int X1, int Y1)[] DrawObjects =>
        [(0, 0, 24, 8, 40), (1, 20, 24, 28, 40), (2, 40, 24, 48, 40)];

    private static void AssertImage(uint[] expected, uint[] actual, string what)
    {
        var bad = Enumerable.Range(0, expected.Length).Where(i => expected[i] != actual[i]).ToArray();
        Assert.True(bad.Length == 0,
            $"{what}: {bad.Length} pixels differ, first at ({bad.FirstOrDefault() % DrawSize},{bad.FirstOrDefault() / DrawSize}): " +
            $"0x{(bad.Length > 0 ? actual[bad[0]] : 0):X8} vs 0x{(bad.Length > 0 ? expected[bad[0]] : 0):X8}");
    }

    /// <summary>
    /// GPU-driven draws: a compute dispatch writes each draw's instance count (0 for the culled object), then
    /// DRAW_INDIRECT draws read them through SET_BASE + a data offset.
    /// </summary>
    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 2u)]
    public void IndirectDrawsDrawOnlyTheObjectsTheComputePassKept(bool instancedProgram, uint instances)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var objects = DrawObjects;
        var flags = rig.Memory.Upload<uint>([1u, 0u, 1u]);
        var arguments = Fill(rig, 16, 0xFFFFFFFF);
        var target = rig.ColorTarget(DrawSize, DrawSize);
        var pm4 = new ScenePm4();
        WriteDrawArguments(rig, pm4, arguments, flags, 6, instances, 3, 4);
        SetBase(pm4, arguments, compute: false);
        var vertexProgram = rig.Program(instancedProgram ? ComputeScenePrograms.FetchVertexInstanced : ScenePrograms.FetchVertex);
        foreach (var (index, x0, y0, x1, y1) in objects)
        {
            ObjectState(rig, target, DrawSize, DrawSize, vertexProgram,
                AsTriangles(QuadVertices(index, 0.5f, x0, y0, x1, y1, (int)DrawSize, (int)DrawSize))).Emit(pm4);
            pm4.Packet(ScenePm4.OpDrawIndirect, (uint)index * 16, 0u, 0u, 2u);
        }

        rig.Submit(pm4);
        Assert.Equal(new[] { 6u, instances, 0u, 0u, 6u, 0u, 0u, 0u, 6u, instances, 0u, 0u }, rig.Read<uint>(arguments, 12));
        var expected = ExpectedPixels(objects.Where(o => o.Index != 1), 16, _ => instancedProgram ? instances : 1);
        AssertImage(expected, rig.ReadImage<uint>(target, DrawSize, DrawSize), "indirect draws");
    }

    /// <summary>DRAW_INDEX_INDIRECT with the arguments written by compute; hardware issues indexed indirect draws with the DMA source select (0).</summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(0u)]
    public void IndexedIndirectDrawsDrawOnlyTheObjectsTheComputePassKept(uint drawInitiator)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var objects = DrawObjects;
        var flags = rig.Memory.Upload<uint>([1u, 0u, 1u]);
        var arguments = Fill(rig, 16, 0xFFFFFFFF);
        var indices = rig.Memory.Upload<ushort>([0, 1, 2, 2, 1, 3], 16);
        var target = rig.ColorTarget(DrawSize, DrawSize);
        var pm4 = new ScenePm4();
        WriteDrawArguments(rig, pm4, arguments, flags, 6, 1, 3, 5);
        SetBase(pm4, arguments, compute: false);
        pm4.IndexType(0).Packet(ScenePm4.OpIndexBase, (uint)indices, (uint)(indices >> 32)).Packet(ScenePm4.OpIndexBufferSize, 6u);
        var vertexProgram = rig.Program(ScenePrograms.FetchVertex);
        foreach (var (index, x0, y0, x1, y1) in objects)
        {
            ObjectState(rig, target, DrawSize, DrawSize, vertexProgram,
                QuadVertices(index, 0.5f, x0, y0, x1, y1, (int)DrawSize, (int)DrawSize)).Emit(pm4);
            pm4.Packet(ScenePm4.OpDrawIndexIndirect, (uint)index * 20, 0u, 0u, drawInitiator);
        }

        rig.Submit(pm4);
        Assert.Equal(new[] { 6u, 1u, 0u, 0u, 0u, 6u, 0u, 0u, 0u, 0u }, rig.Read<uint>(arguments, 10));
        AssertImage(ExpectedPixels(objects.Where(o => o.Index != 1), 16, _ => 1), rig.ReadImage<uint>(target, DrawSize, DrawSize), "indexed indirect draws");
    }

    /// <summary>
    /// DRAW_INDIRECT_MULTI / DRAW_INDEX_INDIRECT_MULTI: one packet walks <c>count</c> argument records at the
    /// stride; instance counts [1, 0, 2, 0] draw instances 0 and 1 of one quad. With a count address the draw
    /// count is min(*address, count).
    /// </summary>
    [Theory]
    [InlineData(false, false, 4u, 2u)]
    [InlineData(true, false, 4u, 2u)]
    [InlineData(true, false, 4u, 0u)]
    [InlineData(false, true, 4u, 2u)]
    [InlineData(false, true, 2u, 2u)]
    [InlineData(false, true, 9u, 2u)]
    [InlineData(false, false, 2u, 2u)]
    public void MultiDrawWalksTheArgumentRecords(bool indexed, bool countInMemory, uint countValue, uint initiator)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var recordDwords = indexed ? 5u : 4u;
        var arguments = Fill(rig, 32, 0xFFFFFFFF);
        var records = new uint[4 * recordDwords];
        uint[] instanceCounts = [1, 0, 2, 0];
        for (var draw = 0; draw < 4; draw++)
        {
            records[(draw * recordDwords)] = 6;
            records[(draw * recordDwords) + 1] = instanceCounts[draw];
        }

        rig.Memory.Write<uint>(arguments, records);
        var countAddress = rig.Memory.Upload<uint>([countValue], 16);
        var target = rig.ColorTarget(DrawSize, DrawSize);
        var indices = rig.Memory.Upload<ushort>([0, 1, 2, 2, 1, 3], 16);
        var pm4 = new ScenePm4();
        SetBase(pm4, arguments, compute: false);
        var quad = QuadVertices(0, 0.5f, 0, 24, 8, 40, (int)DrawSize, (int)DrawSize);
        ObjectState(rig, target, DrawSize, DrawSize, rig.Program(ComputeScenePrograms.FetchVertexInstanced),
            indexed ? quad : AsTriangles(quad)).Emit(pm4);
        if (indexed)
        {
            pm4.IndexType(0).Packet(ScenePm4.OpIndexBase, (uint)indices, (uint)(indices >> 32)).Packet(ScenePm4.OpIndexBufferSize, 6u);
        }

        // data offset, base vertex loc, start instance loc, count-indirect flag, max count, count address, stride, initiator.
        pm4.Packet(indexed ? OpDrawIndexIndirectMulti : OpDrawIndirectMulti, 0u, 0u, 0u,
            countInMemory ? 1u << 30 : 0u, countInMemory ? 4u : countValue, countInMemory ? (uint)countAddress : 0u,
            countInMemory ? (uint)(countAddress >> 32) : 0u, recordDwords * 4, initiator);
        rig.Submit(pm4);
        var drawn = countInMemory ? Math.Min(countValue, 4u) : countValue;
        var live = Enumerable.Range(0, (int)drawn).Where(draw => instanceCounts[draw] != 0).ToArray();
        // Union of the drawn instances of the one quad: instance step 16 px.
        var maxInstances = live.Length == 0 ? 0u : live.Max(draw => instanceCounts[draw]);
        var expected = ExpectedPixels([(0, 0, 24, 8, 40)], 16, _ => maxInstances);
        AssertImage(expected, rig.ReadImage<uint>(target, DrawSize, DrawSize), "multi draw");
    }

    /// <summary>
    /// The whole GPU-driven culling chain a UE4 title runs: depth -> HZB -> per-object occlusion flags ->
    /// draw arguments -> indirect draws. Occluded objects must vanish and no visible object may be culled.
    /// </summary>
    [Fact]
    public void GpuDrivenCullingDrawsExactlyTheObjectsTheHzbKeeps()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const int width = 80, height = 48;
        var candidates = Candidates(width, height).Take(9).ToList();
        var pm4 = new ScenePm4();
        var depth = RenderDepth(rig, pm4, width, height);
        PrepareDepthForCompute(pm4);
        var levels = BuildHzb(rig, pm4, depth, width, height, max: true);
        var flags = Fill(rig, (uint)candidates.Count, 0xFFFFFFFF);
        var tables = UploadHzbTables(rig, levels);
        var objectData = UploadCandidates(rig, candidates);
        Setup(rig, pm4, ComputeScenePrograms.HzbOcclusionStaticLevels,
                [Descriptors.Lo(tables), Descriptors.Hi(tables), 0, 0, .. Vsharp(objectData, 32, (uint)candidates.Count),
                 .. Vsharp(flags, 4, (uint)candidates.Count), (uint)levels.Count - 1], 64)
            .DispatchDirect((uint)candidates.Count, 1, 1);
        CsFlush(pm4);
        var arguments = Fill(rig, (uint)candidates.Count * 4, 0xFFFFFFFF);
        WriteDrawArguments(rig, pm4, arguments, flags, 6, 1, (uint)candidates.Count, 4);
        SetBase(pm4, arguments, compute: false);
        var target = rig.ColorTarget(width, height);
        var vertexProgram = rig.Program(ScenePrograms.FetchVertex);
        for (var index = 0; index < candidates.Count; index++)
        {
            var c = candidates[index];
            ObjectState(rig, target, width, height, vertexProgram,
                AsTriangles(QuadVertices(index, c.NearestZ, c.X0, c.Y0, c.X1 + 1, c.Y1 + 1, width, height))).Emit(pm4);
            pm4.Packet(ScenePm4.OpDrawIndirect, (uint)index * 16, 0u, 0u, 2u);
        }

        rig.Submit(pm4);
        var oracle = HzbOracle(DepthMap(width, height), width, height, true);
        var expected = new uint[width * height];
        for (var index = 0; index < candidates.Count; index++)
        {
            var c = candidates[index];
            if (!HzbVisible(c, oracle)) continue;
            for (var y = c.Y0; y <= c.Y1; y++)
            for (var x = c.X0; x <= c.X1; x++) expected[(y * width) + x] = ObjectPixel(index);
        }

        var actual = rig.ReadImage<uint>(target, width, height);
        var bad = Enumerable.Range(0, expected.Length).Where(i => expected[i] != actual[i]).ToArray();
        Assert.True(bad.Length == 0,
            $"{bad.Length} pixels differ, first at ({bad.FirstOrDefault() % width},{bad.FirstOrDefault() / width}): " +
            $"0x{(bad.Length > 0 ? actual[bad[0]] : 0):X8} vs 0x{(bad.Length > 0 ? expected[bad[0]] : 0):X8}");
    }
}
