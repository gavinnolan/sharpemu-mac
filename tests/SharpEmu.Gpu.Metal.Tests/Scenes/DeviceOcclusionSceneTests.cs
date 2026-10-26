// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// Occlusion queries counted on the device (MetalRenderHost.DeviceOcclusionQueries): the end dump carries the
/// samples the query's draws passed the depth test with, so a draw hidden behind nearer depth reads zero and the
/// draws predicated on it are skipped. Ghost of Tsushima draws the lens glows of hidden lights this way.
/// </summary>
public sealed class DeviceOcclusionSceneTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint Size = 32;
    private const int DepthBlocks = 16;
    private const ulong Ready = 1UL << 63;
    private const uint Red = 0xFF0000FF;
    private const uint Green = 0xFF00FF00;

    private void Run(Action<DepthRig> body)
    {
        var rig = CreateRig();
        if (rig is null) return;
        var previous = MetalRenderHost.DeviceOcclusionQueries;
        MetalRenderHost.DeviceOcclusionQueries = true;
        try
        {
            using (rig)
            {
                // The whole target at depth 0.5: nearer draws pass, farther ones are hidden.
                var d = new DepthRig(rig, Size, Size, zFormat: 3);
                d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Full(0.5f, 1, 0, 0));
                body(d);
            }
        }
        finally
        {
            MetalRenderHost.DeviceOcclusionQueries = previous;
        }
    }

    private static ulong Query(SceneRig rig) => rig.Memory.Allocate(DepthBlocks * 16, 64);

    private static void Begin(ScenePm4 pm4, ulong query) =>
        pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query);

    private static void End(ScenePm4 pm4, ulong query) =>
        pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query + 8);

    private static SceneState Tested(DepthRig d) => d.State().DepthControl(true, false, CompareFunc.Less);

    /// <summary>The samples passed, as the guest adds them up: end minus begin over the depth blocks, all ready.</summary>
    private static ulong Passed(SceneRig rig, ulong query)
    {
        var words = rig.Read<ulong>(query, DepthBlocks * 2);
        ulong passed = 0;
        for (var block = 0; block < DepthBlocks; block++)
        {
            Assert.NotEqual(0UL, words[block * 2] & Ready);
            Assert.NotEqual(0UL, words[(block * 2) + 1] & Ready);
            passed += (words[(block * 2) + 1] & ~Ready) - (words[block * 2] & ~Ready);
        }

        return passed;
    }

    [Fact]
    public void AQueryCountsTheSamplesThatPassedAndAHiddenDrawsQueryReadsZero()
    {
        Run(d =>
        {
            var visible = Query(d.Rig);
            var hidden = Query(d.Rig);
            Begin(d.Pm4, visible);
            d.Draw(Tested(d), d.Rect(0.25f, 0, 1, 0, 0, 0, 16, 32));
            End(d.Pm4, visible);
            Begin(d.Pm4, hidden);
            d.Draw(Tested(d), d.Rect(0.75f, 0, 0, 1, 16, 0, 32, 32));
            End(d.Pm4, hidden);
            d.Rig.Submit(d.Pm4);
            Assert.Equal(16UL * 32, Passed(d.Rig, visible));
            Assert.Equal(0UL, Passed(d.Rig, hidden));
        });
    }

    [Theory]
    [InlineData(0.25f, true)]
    [InlineData(0.75f, false)]
    public void ADrawPredicatedOnAQueryRunsOnlyWhenTheQueriedDrawWasVisible(float queriedZ, bool expectDrawn)
    {
        Run(d =>
        {
            var query = Query(d.Rig);
            Begin(d.Pm4, query);
            d.Draw(Tested(d), d.Rect(queriedZ, 0, 0, 1, 0, 0, 16, 32));
            End(d.Pm4, query);
            d.Pm4.SetPredication(1, true, false, false, query);
            d.Rig.DrawMesh(d.Pm4, d.State().DepthControl(false, false, CompareFunc.Always),
                d.Rect(0.1f, 0, 1, 0, 16, 0, 32, 32), predicated: true);
            d.Rig.Submit(d.Pm4);
            var pixel = SceneDraws.Pixel(d.Rig.ReadImage(d.Color, Size, Size), Size, Size * 3 / 4, Size / 2);
            Assert.Equal(expectDrawn ? Green : Red, pixel);
        });
    }

    [Fact]
    public void AQuerySpanningRenderPassesAddsTheirSamples()
    {
        Run(d =>
        {
            var other = d.Rig.ColorTarget(Size, Size);
            var query = Query(d.Rig);
            Begin(d.Pm4, query);
            d.Draw(Tested(d), d.Rect(0.25f, 0, 1, 0, 0, 0, 16, 32));
            // Another colour target and no depth: a pass of its own.
            d.Rig.DrawMesh(d.Pm4, new SceneState(Size, Size).ColorTarget(0, other, Size, Size),
                d.Rect(0.5f, 0, 0, 1, 0, 0, 8, 32));
            End(d.Pm4, query);
            d.Rig.Submit(d.Pm4);
            Assert.Equal((16UL * 32) + (8UL * 32), Passed(d.Rig, query));
        });
    }

    [Fact]
    public void AQueryPoolReusedFrameAfterFrameReadsEachFramesCount()
    {
        Run(d =>
        {
            d.Rig.Submit(d.Pm4);
            var query = Query(d.Rig);
            for (var frame = 1; frame <= 3; frame++)
            {
                var pm4 = new ScenePm4();
                Begin(pm4, query);
                d.Rig.DrawMesh(pm4, Tested(d), d.Rect(0.25f, 0, 1, 0, 0, 0, frame * 4, 32));
                End(pm4, query);
                d.Rig.Submit(pm4);
                Assert.Equal((ulong)(frame * 4 * 32), Passed(d.Rig, query));
            }
        });
    }
}
