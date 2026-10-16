// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// The fixed-function rasteriser front end: culling, the viewport transform, scissors,
/// clipping, PA_CL_VTE_CNTL, primitive types, instancing and indexed draws. Every
/// expectation is computed in C# from the hardware rules (D3D rasterisation with the
/// top-left fill rule on pixel centres, clip-space polygon clipping, the viewport
/// transform), never from the emulator's own output.
/// </summary>
public sealed class RasterSceneTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint W = 32;
    private const uint H = 32;
    private const uint Red = 0xFF0000FF;

    // PA_CL_CLIP_CNTL bits (gfx10.3): DX_CLIP_SPACE_DEF 19, CLIP_DISABLE 16, ZCLIP_NEAR/FAR_DISABLE 26/27.
    private const uint ClipDxSpace = 1u << 19;
    private const uint ClipDisable = 1u << 16;
    private const uint ZClipNearDisable = 1u << 26;
    private const uint ZClipFarDisable = 1u << 27;

    // VGT_PRIMITIVE_TYPE values beyond Prim's, and the primitive restart registers:
    // GE_MULTI_PRIM_IB_RESET_EN (uconfig) and VGT_MULTI_PRIM_IB_RESET_INDX (context).
    private const uint PrimQuadList = 19;
    private const uint PrimPolygon = 21;
    private const uint GeMultiPrimIbResetEn = 0x24B;
    private const uint VgtMultiPrimIbResetIndx = 0x103;

    // ---------------------------------------------------------------- programs

    // Instance id (v8) shifts the position's x by 0.5 per instance.
    private static readonly uint[] InstancedVertex =
    [
        0xE0382000, 0x80020005,    // buffer_load_dwordx4 v[0:3], v5, s[8:11], 0 idxen
        0xE0382000, 0x80030A05,    // buffer_load_dwordx4 v[10:13], v5, s[12:15], 0 idxen
        0x7E1C0D08,                // v_cvt_f32_u32_e32 v14, v8
        0x101C1CF0,                // v_mul_f32_e32 v14, 0.5, v14
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x06001D00,                // v_add_f32_e32 v0, v0, v14
        0xF80008CF, 0x03020100,    // exp pos0 v0, v1, v2, v3 done
        0xF800020F, 0x0D0C0B0A,    // exp param0 v10, v11, v12, v13
        0xBF810000,                // s_endpgm
    ];

    // POS1.x = 8.0 (the point size in pixels).
    private static readonly uint[] PointSizeVertex =
    [
        0xE0382000, 0x80020005,    // buffer_load_dwordx4 v[0:3], v5, s[8:11], 0 idxen
        0xE0382000, 0x80030A05,    // buffer_load_dwordx4 v[10:13], v5, s[12:15], 0 idxen
        0x7E1C02FF, 0x41000000,    // v_mov_b32_e32 v14, 0x41000000
        0x7E1E0280,                // v_mov_b32_e32 v15, 0
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF80000CF, 0x03020100,    // exp pos0 v0, v1, v2, v3
        0xF80008DF, 0x0F0F0F0E,    // exp pos1 v14, v15, v15, v15 done
        0xF800020F, 0x0D0C0B0A,    // exp param0 v10, v11, v12, v13
        0xBF810000,                // s_endpgm
    ];

    // POS1.x = POS0.x: clip distance 0 is the clip-space x.
    private static readonly uint[] ClipDistanceVertex =
    [
        0xE0382000, 0x80020005,    // buffer_load_dwordx4 v[0:3], v5, s[8:11], 0 idxen
        0xE0382000, 0x80030A05,    // buffer_load_dwordx4 v[10:13], v5, s[12:15], 0 idxen
        0x7E1E0280,                // v_mov_b32_e32 v15, 0
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF80000CF, 0x03020100,    // exp pos0 v0, v1, v2, v3
        0xF80008DF, 0x0F0F0F00,    // exp pos1 v0, v15, v15, v15 done
        0xF800020F, 0x0D0C0B0A,    // exp param0 v10, v11, v12, v13
        0xBF810000,                // s_endpgm
    ];

    // ---------------------------------------------------------------- viewport and geometry

    /// <summary>PA_CL_VPORT_{X,Y}{SCALE,OFFSET}: screen = ndc * scale + offset.</summary>
    private readonly record struct Vp(float Xs, float Xo, float Ys, float Yo)
    {
        public static Vp Default => new(W / 2f, W / 2f, -(H / 2f), H / 2f);

        public (double X, double Y) Screen(double x, double y, double w = 1) => (x / w * Xs + Xo, y / w * Ys + Yo);

        public (double X, double Y) Screen(V4 v) => Screen(v.X, v.Y, v.W);

        public (float X, float Y) Ndc(double sx, double sy) => ((float)((sx - Xo) / Xs), (float)((sy - Yo) / Ys));

        public void Apply(SceneState state)
        {
            state.Context[Cx.PaClVportXScale] = SceneState.F(Xs);
            state.Context[Cx.PaClVportXOffset] = SceneState.F(Xo);
            state.Context[Cx.PaClVportYScale] = SceneState.F(Ys);
            state.Context[Cx.PaClVportYOffset] = SceneState.F(Yo);
        }
    }

    private readonly record struct V4(double X, double Y, double Z, double W)
    {
        public static V4 Of(SceneVertex v) => new(v.X, v.Y, v.Z, v.W);

        public static V4 Lerp(V4 a, V4 b, double t) =>
            new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t), a.W + ((b.W - a.W) * t));
    }

    private static SceneVertex Vertex(Vp vp, double sx, double sy, float z = 0.5f, float w = 1f)
    {
        var (x, y) = vp.Ndc(sx, sy);
        return new SceneVertex(x * w, y * w, z, w, 1, 0, 0, 1);
    }

    private static SceneVertex[] Vertices(Vp vp, float z, params (double X, double Y)[] screen) =>
        [.. screen.Select(p => Vertex(vp, p.X, p.Y, z))];

    // ---------------------------------------------------------------- the coverage oracle

    /// <summary>
    /// D3D coverage of a screen-space triangle at (px, py): inside every edge, with the
    /// top-left fill rule on exact ties. Screen y grows downwards, so an edge is a top edge
    /// when horizontal with the interior below it and a left edge when it goes up.
    /// </summary>
    private static bool Covers((double X, double Y)[] t, double px, double py)
    {
        var (a, b, c) = (t[0], t[1], t[2]);
        var area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (area == 0)
        {
            return false;
        }

        if (area < 0)
        {
            (b, c) = (c, b);
        }

        return Edge(a, b) && Edge(b, c) && Edge(c, a);

        bool Edge((double X, double Y) p, (double X, double Y) q)
        {
            var e = ((q.X - p.X) * (py - p.Y)) - ((q.Y - p.Y) * (px - p.X));
            if (e != 0)
            {
                return e > 0;
            }

            return (q.Y == p.Y && q.X > p.X) || q.Y < p.Y;
        }
    }

    /// <summary>
    /// Per-pixel expectation: 1 covered, 0 not covered, 2 undecided. With a margin, a pixel
    /// whose centre is within the margin of an edge is undecided: the hardware snaps vertices
    /// to 1/256 pixel, so only a clipped edge can land that close.
    /// </summary>
    private static byte[] Coverage(IEnumerable<(double X, double Y)[]> triangles, double margin = 0)
    {
        var list = triangles.ToList();
        var offsets = margin == 0
            ? new[] { (0d, 0d) }
            : new[] { (0d, 0d), (margin, 0d), (-margin, 0d), (0d, margin), (0d, -margin) };
        var result = new byte[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var votes = offsets.Select(o => list.Any(t => Covers(t, x + 0.5 + o.Item1, y + 0.5 + o.Item2))).ToArray();
                result[(y * W) + x] = votes.All(v => v) ? (byte)1 : votes.Any(v => v) ? (byte)2 : (byte)0;
            }
        }

        return result;
    }

    private static IEnumerable<(double X, double Y)[]> ScreenTriangles(Vp vp, IReadOnlyList<SceneVertex> vertices)
    {
        for (var index = 0; index + 2 < vertices.Count; index += 3)
        {
            yield return [vp.Screen(V4.Of(vertices[index])), vp.Screen(V4.Of(vertices[index + 1])), vp.Screen(V4.Of(vertices[index + 2]))];
        }
    }

    private static byte[] Rect(int left, int top, int right, int bottom)
    {
        var result = new byte[W * H];
        for (var y = Math.Max(top, 0); y < Math.Min(bottom, (int)H); y++)
        {
            for (var x = Math.Max(left, 0); x < Math.Min(right, (int)W); x++)
            {
                result[(y * W) + x] = 1;
            }
        }

        return result;
    }

    private static byte[] Intersect(byte[] a, byte[] b) => [.. a.Zip(b, (x, y) => (byte)(x == 0 || y == 0 ? 0 : x == 2 || y == 2 ? 2 : 1))];

    private static void AssertCoverage(uint[] pixels, byte[] expected, string what)
    {
        var bad = new List<string>();
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var pixel = pixels[(y * W) + x];
                var want = expected[(y * W) + x];
                if ((want == 1 && pixel != Red) || (want == 0 && pixel != 0))
                {
                    bad.Add($"({x},{y}) got 0x{pixel:X8} want {(want == 1 ? "colour" : "empty")}");
                }
            }
        }

        // Rebuild a clean side-by-side map (actual | expected) for the failure message.
        var map = new StringBuilder();
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var pixel = pixels[(y * W) + x];
                map.Append(pixel == Red ? '#' : pixel == 0 ? '.' : '?');
            }

            map.Append(" | ");
            for (var x = 0; x < W; x++)
            {
                var want = expected[(y * W) + x];
                map.Append(want == 1 ? '#' : want == 0 ? '.' : '~');
            }

            map.AppendLine();
        }

        Assert.True(bad.Count == 0,
            $"{what}: {bad.Count} pixels differ (first: {string.Join("; ", bad.Take(8))}). actual | expected:\n{map}");
    }

    private static void AssertPixels(uint[] pixels, string what, IEnumerable<(int X, int Y)> lit, IEnumerable<(int X, int Y)> dark)
    {
        var bad = new List<string>();
        foreach (var (x, y) in lit.Where(p => pixels[(p.Y * W) + p.X] != Red))
        {
            bad.Add($"({x},{y}) should be lit, got 0x{pixels[(y * W) + x]:X8}");
        }

        foreach (var (x, y) in dark.Where(p => pixels[(p.Y * W) + p.X] != 0))
        {
            bad.Add($"({x},{y}) should be empty, got 0x{pixels[(y * W) + x]:X8}");
        }

        Assert.True(bad.Count == 0, $"{what}: {bad.Count} wrong (first: {string.Join("; ", bad.Take(10))}).");
    }

    // ---------------------------------------------------------------- drawing

    private static uint[] Render(SceneRig rig, IReadOnlyList<SceneVertex> vertices, Action<SceneState>? configure = null,
        Action<ScenePm4>? draw = null, uint[]? vertexProgram = null)
    {
        var target = rig.ColorTarget(W, H);
        var state = new SceneState(W, H).ColorTarget(0, target, W, H);
        configure?.Invoke(state);
        state.VertexShader(rig.Program(vertexProgram ?? ScenePrograms.FetchVertex), rig.UploadMesh(vertices));
        state.PixelShader(rig.Program(ScenePrograms.InterpolatedColor));
        var pm4 = new ScenePm4();
        state.Emit(pm4);
        if (draw is null)
        {
            pm4.NumInstances(1);
            pm4.DrawAuto((uint)vertices.Count);
        }
        else
        {
            draw(pm4);
        }

        rig.Submit(pm4);
        return rig.ReadImage<uint>(target, W, H);
    }

    /// <summary>Draws into a depth target, then samples it into an R32F target and returns the depth values.</summary>
    private static float[] RenderDepth(SceneRig rig, IReadOnlyList<SceneVertex> vertices, Action<SceneState>? configure = null)
    {
        var depth = rig.Memory.Allocate((W * H * 4) + 0x10000, 0x10000);
        var state = new SceneState(W, H).ColorTarget(0, rig.ColorTarget(W, H), W, H)
            .DepthTarget(depth, W, H, zFormat: 3).DepthControl(test: true, write: true, CompareFunc.Always);
        configure?.Invoke(state);
        var pm4 = rig.DrawMesh(new ScenePm4(), state, vertices);
        var readback = rig.ColorTarget(W, H);
        var copy = new SceneState(W, H).ColorTarget(0, readback, W, H, format: 4, numberType: 7);
        uint[] userData = [.. Descriptors.Texture(depth, 22, W, H, tileMode: 24), .. Descriptors.Sampler()];
        rig.DrawMesh(pm4, copy, SceneDraws.TexturedQuad(), ScenePrograms.SampledTexture, userData);
        rig.Submit(pm4);
        return rig.ReadImage<float>(readback, W, H);
    }

    // A triangle that covers a good part of the 32x32 target, with fractional corners.
    private static readonly (double X, double Y)[] BigTriangle = [(4, 4.25), (28, 6), (8, 28)];

    // ---------------------------------------------------------------- 1. culling

    public static IEnumerable<object[]> CullCases()
    {
        foreach (var negativeY in new[] { true, false })
        {
            foreach (var face in new uint[] { 0, 1 })
            {
                foreach (var counterClockwise in new[] { true, false })
                {
                    foreach (var (front, back) in new[] { (false, false), (true, false), (false, true), (true, true) })
                    {
                        yield return [front, back, face, counterClockwise, negativeY];
                    }
                }
            }
        }
    }

    /// <summary>
    /// The facing is the sign of the triangle's area in window coordinates after the viewport
    /// transform (numeric x right, y as the transform produced it, so independent of the
    /// y scale sign). FACE 0 makes a visually counter-clockwise triangle on the y-down
    /// screen the front (numeric cross product below zero), FACE 1 a clockwise one: FACE 1
    /// is the D3D default (clockwise front). The same screen triangle is drawn under both
    /// y scale signs, so its NDC winding flips with the sign.
    /// </summary>
    [Theory]
    [MemberData(nameof(CullCases))]
    public void CullingFollowsTheWindowSpaceWinding(bool cullFront, bool cullBack, uint face, bool windowCounterClockwise, bool negativeYScale)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = negativeYScale ? Vp.Default : Vp.Default with { Ys = H / 2f };
        var (a, b, c) = (BigTriangle[0], BigTriangle[1], BigTriangle[2]);
        if (windowCounterClockwise != Area(a, b, c) > 0)
        {
            (b, c) = (c, b);
        }

        var area = Area(a, b, c);
        Assert.Equal(windowCounterClockwise, area > 0);
        var isFront = face == 0 ? area < 0 : area > 0;
        var visible = isFront ? !cullFront : !cullBack;
        var vertices = Vertices(vp, 0.5f, a, b, c);
        var pixels = Render(rig, vertices, s =>
        {
            vp.Apply(s);
            s.Context[Cx.PaSuScModeCntl] = (cullFront ? 1u : 0u) | (cullBack ? 2u : 0u) | (face << 2);
        });
        AssertCoverage(pixels, visible ? Coverage([[a, b, c]]) : new byte[W * H],
            $"cull front={cullFront} back={cullBack} face={face} windowCcw={windowCounterClockwise} negY={negativeYScale} front-facing={isFront}");
    }

    private static double Area((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));

    // ---------------------------------------------------------------- 2. viewport transform

    [Theory]
    [InlineData(16f, 16f, -16f, 16f)] // default D3D viewport
    [InlineData(16f, 16f, 16f, 16f)] // positive y scale: NDC y up lands on the bottom rows
    [InlineData(8f, 16f, -10f, 14f)] // a sub-rect viewport (x 8..24, y 4..24)
    [InlineData(12f, 20f, -14f, 12f)] // off-centre offsets, larger scale
    [InlineData(-16f, 16f, -16f, 16f)] // mirrored in x
    public void TheViewportTransformMapsNdcToPixels(float xScale, float xOffset, float yScale, float yOffset)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = new Vp(xScale, xOffset, yScale, yOffset);
        // An NDC triangle with fractional corners, inside the clip volume.
        SceneVertex[] vertices =
        [
            SceneVertex.At(-0.8f, -0.7f, 0.5f, 1, 0, 0),
            SceneVertex.At(0.9f, -0.6f, 0.5f, 1, 0, 0),
            SceneVertex.At(-0.3f, 0.85f, 0.5f, 1, 0, 0),
        ];
        var pixels = Render(rig, vertices, vp.Apply);
        AssertCoverage(pixels, Coverage(ScreenTriangles(vp, vertices)), $"viewport {vp}");
    }

    [Fact]
    public void ASubRectViewportMapsAFullNdcQuadOntoExactlyThatRect()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pixels = Render(rig, SceneDraws.Quad(0.5f, 1, 0, 0), s => new Vp(8, 16, -10, 14).Apply(s));
        AssertCoverage(pixels, Rect(8, 4, 24, 24), "full NDC quad in the viewport x 8..24, y 4..24");
    }

    [Theory]
    [InlineData(0.5f, 1f, 0f, 0f, 1f, 0.5f)]
    [InlineData(0.5f, 0.5f, 0.25f, 0.25f, 0.75f, 0.5f)] // scale and offset
    [InlineData(0.25f, 0.5f, 0.25f, 0f, 1f, 0.375f)]
    [InlineData(0.9f, 1f, 0f, 0f, 0.6f, 0.6f)] // clamped by PA_SC_VPORT_ZMAX
    [InlineData(0.1f, 1f, 0f, 0.3f, 1f, 0.3f)] // clamped by PA_SC_VPORT_ZMIN
    public void TheDepthRangeTransformAndClamp(float z, float zScale, float zOffset, float zMin, float zMax, float expected)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var depth = RenderDepth(rig, SceneDraws.Quad(z, 1, 0, 0), s =>
        {
            s.Context[Cx.PaClVportZScale] = SceneState.F(zScale);
            s.Context[Cx.PaClVportZOffset] = SceneState.F(zOffset);
            s.Context[Cx.PaScVportZMin0] = SceneState.F(zMin);
            s.Context[Cx.PaScVportZMax0] = SceneState.F(zMax);
        });
        Assert.Equal(expected, depth[(16 * W) + 16], 1e-4f);
        Assert.Equal(expected, depth[(3 * W) + 3], 1e-4f);
    }

    // ---------------------------------------------------------------- 3. scissors

    private static uint Tl(int x, int y, bool noWindowOffset = false) =>
        (uint)(x & 0x7FFF) | ((uint)(y & 0x7FFF) << 16) | (noWindowOffset ? 1u << 31 : 0u);

    private static uint Br(int x, int y) => (uint)(x & 0x7FFF) | ((uint)(y & 0x7FFF) << 16);

    private static uint[] FullQuad(SceneRig rig, Action<SceneState> configure) =>
        Render(rig, SceneDraws.Quad(0.5f, 1, 0, 0), configure);

    [Fact]
    public void TheScreenScissorRestrictsTheDraw()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pixels = FullQuad(rig, s => s.ScreenScissor(5, 6, 21, 27));
        AssertCoverage(pixels, Rect(5, 6, 21, 27), "screen scissor (5,6)-(21,27)");
    }

    [Fact]
    public void TheWindowScissorWithTheWindowOffsetDisabledRestrictsTheDraw()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pixels = FullQuad(rig, s =>
        {
            s.Context[Cx.PaScWindowOffset] = 0;
            s.Context[Cx.PaScWindowScissorTl] = Tl(7, 3, noWindowOffset: true);
            s.Context[Cx.PaScWindowScissorBr] = Br(25, 20);
        });
        AssertCoverage(pixels, Rect(7, 3, 25, 20), "window scissor (7,3)-(25,20), WINDOW_OFFSET_DISABLE");
    }

    /// <summary>
    /// With WINDOW_OFFSET_DISABLE clear the scissor rectangle is relative to the window
    /// offset, so offset (3,2) moves it by (3,2). Hardware-semantic doubt: the offset is
    /// read from the PA_SC_WINDOW_OFFSET description, no title is known to set it.
    /// </summary>
    [Fact]
    public void TheWindowScissorIsOffsetByTheWindowOffsetUnlessDisabled()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pixels = FullQuad(rig, s =>
        {
            s.Context[Cx.PaScWindowOffset] = 3u | (2u << 16);
            s.Context[Cx.PaScWindowScissorTl] = Tl(4, 4);
            s.Context[Cx.PaScWindowScissorBr] = Br(20, 20);
        });
        AssertCoverage(pixels, Rect(7, 6, 23, 22), "window scissor (4,4)-(20,20) with window offset (3,2)");
    }

    [Fact]
    public void AWindowOffsetDoesNotMoveAScissorThatDisablesIt()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pixels = FullQuad(rig, s =>
        {
            s.Context[Cx.PaScWindowOffset] = 3u | (2u << 16);
            s.Context[Cx.PaScWindowScissorTl] = Tl(4, 4, noWindowOffset: true);
            s.Context[Cx.PaScWindowScissorBr] = Br(20, 20);
        });
        AssertCoverage(pixels, Rect(4, 4, 20, 20), "window scissor (4,4)-(20,20), offset disabled, window offset (3,2)");
    }

    [Fact]
    public void TheGenericScissorRestrictsTheDraw()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pixels = FullQuad(rig, s =>
        {
            s.Context[Cx.PaScGenericScissorTl] = Tl(9, 2, noWindowOffset: true);
            s.Context[Cx.PaScGenericScissorBr] = Br(30, 17);
        });
        AssertCoverage(pixels, Rect(9, 2, 30, 17), "generic scissor (9,2)-(30,17)");
    }

    [Fact]
    public void TheViewportScissorRestrictsTheDrawOnlyWhenEnabled()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var enabled = FullQuad(rig, s =>
        {
            s.Context[Cx.PaScModeCntl0] = 1u << 1;
            s.Context[Cx.PaScVportScissor0Tl] = Tl(6, 8, noWindowOffset: true);
            s.Context[Cx.PaScVportScissor0Br] = Br(19, 24);
        });
        AssertCoverage(enabled, Rect(6, 8, 19, 24), "viewport scissor 0 (6,8)-(19,24), VPORT_SCISSOR_ENABLE");

        using var second = CreateRig();
        var disabled = FullQuad(second!, s =>
        {
            s.Context[Cx.PaScModeCntl0] = 0;
            s.Context[Cx.PaScVportScissor0Tl] = Tl(6, 8, noWindowOffset: true);
            s.Context[Cx.PaScVportScissor0Br] = Br(19, 24);
        });
        AssertCoverage(disabled, Rect(0, 0, 32, 32), "viewport scissor 0 programmed but VPORT_SCISSOR_ENABLE clear");
    }

    [Fact]
    public void TheFourScissorsIntersect()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var pixels = FullQuad(rig, s =>
        {
            s.ScreenScissor(2, 1, 30, 31);
            s.Context[Cx.PaScWindowOffset] = 0;
            s.Context[Cx.PaScWindowScissorTl] = Tl(4, 3, noWindowOffset: true);
            s.Context[Cx.PaScWindowScissorBr] = Br(29, 26);
            s.Context[Cx.PaScGenericScissorTl] = Tl(3, 5, noWindowOffset: true);
            s.Context[Cx.PaScGenericScissorBr] = Br(27, 30);
            s.Context[Cx.PaScModeCntl0] = 1u << 1;
            s.Context[Cx.PaScVportScissor0Tl] = Tl(5, 2, noWindowOffset: true);
            s.Context[Cx.PaScVportScissor0Br] = Br(28, 24);
        });
        // x: max(2,4,3,5)=5 .. min(30,29,27,28)=27; y: max(1,3,5,2)=5 .. min(31,26,30,24)=24.
        AssertCoverage(pixels, Rect(5, 5, 27, 24), "intersection of screen, window, generic and viewport scissors");
    }

    // ---------------------------------------------------------------- 4. clipping

    private static List<(double X, double Y)[]> ClippedTriangles(Vp vp, IEnumerable<V4[]> triangles, params Func<V4, double>[] extraPlanes)
    {
        Func<V4, double>[] planes = [v => v.Z, v => v.W - v.Z, .. extraPlanes];
        var result = new List<(double X, double Y)[]>();
        foreach (var triangle in triangles)
        {
            var polygon = triangle.ToList();
            foreach (var plane in planes)
            {
                polygon = ClipPolygon(polygon, plane);
            }

            for (var index = 1; index + 1 < polygon.Count; index++)
            {
                result.Add([vp.Screen(polygon[0]), vp.Screen(polygon[index]), vp.Screen(polygon[index + 1])]);
            }
        }

        return result;
    }

    private static List<V4> ClipPolygon(List<V4> polygon, Func<V4, double> plane)
    {
        var result = new List<V4>();
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var next = polygon[(index + 1) % polygon.Count];
            var (dc, dn) = (plane(current), plane(next));
            if (dc >= 0)
            {
                result.Add(current);
            }

            if ((dc >= 0) != (dn >= 0))
            {
                result.Add(V4.Lerp(current, next, dc / (dc - dn)));
            }
        }

        return result;
    }

    [Theory]
    [InlineData(0.5f, false, false, true)]
    [InlineData(0.5f, true, true, true)]
    [InlineData(-0.5f, false, false, false)] // behind the near plane
    [InlineData(-0.5f, true, false, true)] // ZCLIP_NEAR_DISABLE: drawn (depth clamped)
    [InlineData(-0.5f, false, true, false)] // far disable does not rescue a near-clipped triangle
    [InlineData(-0.5f, true, true, true)]
    [InlineData(1.5f, false, false, false)] // beyond the far plane
    [InlineData(1.5f, false, true, true)] // ZCLIP_FAR_DISABLE
    [InlineData(1.5f, true, false, false)]
    [InlineData(1.5f, true, true, true)]
    public void TheNearAndFarPlanesClipUnlessDisabled(float z, bool nearDisable, bool farDisable, bool visible)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        var pixels = Render(rig, Vertices(vp, z, BigTriangle), s =>
            s.Context[Cx.PaClClipCntl] = ClipDxSpace | (nearDisable ? ZClipNearDisable : 0) | (farDisable ? ZClipFarDisable : 0));
        AssertCoverage(pixels, visible ? Coverage([BigTriangle]) : new byte[W * H],
            $"z={z} nearDisable={nearDisable} farDisable={farDisable}");
    }

    [Theory]
    [InlineData(-0.5f, 0.2f)] // near clip disabled: depth clamps to the viewport's minimum
    [InlineData(1.5f, 0.9f)] // far clip disabled: depth clamps to the viewport's maximum
    public void DisabledDepthClipClampsTheDepthToTheViewportRange(float z, float expected)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var depth = RenderDepth(rig, SceneDraws.Quad(z, 1, 0, 0), s =>
        {
            s.Viewport(0, 0, W, H, minZ: 0.2f, maxZ: 0.9f);
            s.Context[Cx.PaClClipCntl] = ClipDxSpace | ZClipNearDisable | ZClipFarDisable;
        });
        Assert.Equal(expected, depth[(16 * W) + 16], 1e-4f);
    }

    [Fact]
    public void ATriangleCrossingTheNearPlaneIsClippedAtZero()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // Screen-space corners as in BigTriangle, with z -0.6 at the first vertex, 0.8 at the others.
        var vertices = new[]
        {
            Vertex(vp, 4, 4.25, -0.6f), Vertex(vp, 28, 6, 0.8f), Vertex(vp, 8, 28, 0.8f),
        };
        var pixels = Render(rig, vertices);
        var expected = Coverage(ClippedTriangles(vp, [[.. vertices.Select(V4.Of)]]), margin: 0.04);
        AssertCoverage(pixels, expected, "triangle crossing z=0");
    }

    [Fact]
    public void ATriangleCrossingTheFarPlaneIsClippedAtW()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        var vertices = new[]
        {
            Vertex(vp, 4, 4.25, 0.3f), Vertex(vp, 28, 6, 1.4f), Vertex(vp, 8, 28, 1.4f),
        };
        var pixels = Render(rig, vertices);
        var expected = Coverage(ClippedTriangles(vp, [[.. vertices.Select(V4.Of)]]), margin: 0.04);
        AssertCoverage(pixels, expected, "triangle crossing z=w");
    }

    [Fact]
    public void ATriangleWithAVertexBehindTheCameraIsClippedAgainstTheNearPlane()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // Clip-space vertices; the last has w < 0. The z >= 0 plane leaves a trapezoid with w > 0.
        SceneVertex[] vertices =
        [
            new(-0.8f, -0.8f, 0.2f, 1f, 1, 0, 0, 1),
            new(0.8f, -0.8f, 0.2f, 1f, 1, 0, 0, 1),
            new(0f, 1f, -0.5f, -0.25f, 1, 0, 0, 1),
        ];
        var pixels = Render(rig, vertices);
        var expected = Coverage(ClippedTriangles(vp, [[.. vertices.Select(V4.Of)]]), margin: 0.04);
        AssertCoverage(pixels, expected, "triangle with w < 0 at one vertex");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipDisableStillDividesByW(bool clipDisable)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // w = 2 everywhere: the clip-space positions are NDC * 2.
        var vertices = new[]
        {
            Vertex(vp, 6, 6.25, 0.5f, w: 2f), Vertex(vp, 26, 9, 0.5f, w: 2f), Vertex(vp, 10, 26, 0.5f, w: 2f),
        };
        var pixels = Render(rig, vertices, s => s.Context[Cx.PaClClipCntl] = ClipDxSpace | (clipDisable ? ClipDisable : 0));
        AssertCoverage(pixels, Coverage(ScreenTriangles(vp, vertices)), $"w=2 triangle, CLIP_DISABLE={clipDisable}");
    }

    [Fact]
    public void ATriangleFarOutsideTheXyBoundsCoversTheWholeTarget()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // NDC (-3,-3), (5,-3), (-3,5): covers the whole target; x/y are not clipped (guard band) or clipped invisibly.
        SceneVertex[] vertices =
        [
            SceneVertex.At(-3, -3, 0.5f, 1, 0, 0), SceneVertex.At(5, -3, 0.5f, 1, 0, 0), SceneVertex.At(-3, 5, 0.5f, 1, 0, 0),
        ];
        var pixels = Render(rig, vertices);
        AssertCoverage(pixels, Rect(0, 0, 32, 32), "oversized triangle");
    }

    [Fact]
    public void ATrianglePartlyOutsideTheXyBoundsKeepsItsInsidePixels()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        SceneVertex[] vertices =
        [
            SceneVertex.At(-1.6f, -0.5f, 0.5f, 1, 0, 0), SceneVertex.At(0.7f, -1.4f, 0.5f, 1, 0, 0), SceneVertex.At(0.4f, 1.3f, 0.5f, 1, 0, 0),
        ];
        var pixels = Render(rig, vertices);
        AssertCoverage(pixels, Coverage(ScreenTriangles(vp, vertices), margin: 0.04), "triangle crossing the viewport edges");
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)] // plane 0 only
    [InlineData(2u, 2)] // plane 1 only
    [InlineData(3u, 3)] // both
    public void UserClipPlanesClipOnlyTheEnabledPlanes(uint enableMask, int planes)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        var pixels = Render(rig, SceneDraws.Quad(0.5f, 1, 0, 0), s =>
        {
            // Plane 0 keeps x >= -0.5 w; plane 1 keeps y <= 0.25 w (dot((x,y,z,w), plane) >= 0).
            s.Context[Cx.PaClUcp0X + 0] = SceneState.F(1);
            s.Context[Cx.PaClUcp0X + 1] = SceneState.F(0);
            s.Context[Cx.PaClUcp0X + 2] = SceneState.F(0);
            s.Context[Cx.PaClUcp0X + 3] = SceneState.F(0.5f);
            s.Context[Cx.PaClUcp0X + 4] = SceneState.F(0);
            s.Context[Cx.PaClUcp0X + 5] = SceneState.F(-1);
            s.Context[Cx.PaClUcp0X + 6] = SceneState.F(0);
            s.Context[Cx.PaClUcp0X + 7] = SceneState.F(0.25f);
            s.Context[Cx.PaClClipCntl] = ClipDxSpace | enableMask;
        });
        var extra = new List<Func<V4, double>>();
        if ((enableMask & 1) != 0) extra.Add(v => v.X + (0.5 * v.W));
        if ((enableMask & 2) != 0) extra.Add(v => -v.Y + (0.25 * v.W));
        var quad = SceneDraws.Quad(0.5f, 1, 0, 0);
        var expected = Coverage(ClippedTriangles(vp, [[.. quad.Take(3).Select(V4.Of)], [.. quad.Skip(3).Select(V4.Of)]], [.. extra]), margin: 0.01);
        AssertCoverage(pixels, expected, $"user clip planes enabled mask={enableMask} ({planes})");
    }

    [Fact]
    public void AClipDistanceExportedInPos1ClipsWhereItIsNegative()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        var pixels = Render(rig, SceneDraws.Quad(0.5f, 1, 0, 0), s =>
        {
            s.Context[Cx.SpiShaderPosFormat] = 4 | (4u << 4);
            // CLIP_DIST_ENA_0 and VS_OUT_CCDIST0_VEC_ENA.
            s.Context[Cx.PaClVsOutCntl] = 1u | (1u << 22);
        }, vertexProgram: ClipDistanceVertex);
        // Clip distance 0 = clip-space x: only x >= 0 survives.
        var quad = SceneDraws.Quad(0.5f, 1, 0, 0);
        var expected = Coverage(ClippedTriangles(vp, [[.. quad.Take(3).Select(V4.Of)], [.. quad.Skip(3).Select(V4.Of)]], v => v.X), margin: 0.01);
        AssertCoverage(pixels, expected, "clip distance 0 = x");
    }

    // ---------------------------------------------------------------- 5. PA_CL_VTE_CNTL

    // VPORT_{X,Y,Z}_{SCALE,OFFSET}_ENA bits 0..5, VTX_XY_FMT 8, VTX_Z_FMT 9, VTX_W0_FMT 10.
    [Theory]
    [InlineData(0x43Fu)] // default
    [InlineData(0x430u)] // x/y scale and offset off: positions are already pixels (clip disabled)
    [InlineData(0x43Au)] // x/y offsets only: pixel = position + offset
    [InlineData(0x435u)] // x/y scales only: pixel = position * scale
    public void TheXyViewportEnablesSelectWhichPartsOfTheTransformApply(uint vte)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        (double X, double Y)[] screen = [(6, 6.25), (26, 9), (10, 26)];
        // Position components x' with: pixel = (x' * (scaleEna ? scale : 1)) + (offsetEna ? offset : 0).
        var xScale = (vte & 1) != 0;
        var xOffset = (vte & 2) != 0;
        var yScale = (vte & 4) != 0;
        var yOffset = (vte & 8) != 0;
        var vertices = screen.Select(p => Vertex(vp, 0, 0, 0.5f)).ToArray();
        for (var index = 0; index < 3; index++)
        {
            var x = (screen[index].X - (xOffset ? vp.Xo : 0)) / (xScale ? vp.Xs : 1);
            var y = (screen[index].Y - (yOffset ? vp.Yo : 0)) / (yScale ? vp.Ys : 1);
            vertices[index] = new SceneVertex((float)x, (float)y, 0.5f, 1f, 1, 0, 0, 1);
        }

        var pixels = Render(rig, vertices, s =>
        {
            s.Context[Cx.PaClVteCntl] = vte;
            s.Context[Cx.PaClClipCntl] = ClipDxSpace | ClipDisable;
        });
        AssertCoverage(pixels, Coverage([screen]), $"PA_CL_VTE_CNTL=0x{vte:X}");
    }

    [Fact]
    public void VtxXyFmtSetMeansXyAreAlreadyDividedByW()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // x, y already divided by w (NDC), w = 2 carried separately: screen = ndc * scale + offset.
        (double X, double Y)[] screen = [(6, 6.25), (26, 9), (10, 26)];
        var vertices = screen.Select(p =>
        {
            var (x, y) = vp.Ndc(p.X, p.Y);
            return new SceneVertex(x, y, 1f, 2f, 1, 0, 0, 1);
        }).ToArray();
        var pixels = Render(rig, vertices, s => s.Context[Cx.PaClVteCntl] = 0x43Fu | (1u << 8));
        AssertCoverage(pixels, Coverage([screen]), "VTX_XY_FMT=1 with w=2");
    }

    [Theory]
    [InlineData(0x43Fu, 0.45f)] // z / w * scale + offset = (0.4 / 1) * 0.5 + 0.25
    [InlineData(0x42Fu, 0.65f)] // VPORT_Z_SCALE_ENA clear: z + offset
    [InlineData(0x41Fu, 0.20f)] // VPORT_Z_OFFSET_ENA clear: z * scale
    [InlineData(0x40Fu, 0.40f)] // both clear: z
    public void TheZViewportEnablesSelectWhichPartsOfTheDepthTransformApply(uint vte, float expected)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var depth = RenderDepth(rig, SceneDraws.Quad(0.4f, 1, 0, 0), s =>
        {
            s.Context[Cx.PaClVportZScale] = SceneState.F(0.5f);
            s.Context[Cx.PaClVportZOffset] = SceneState.F(0.25f);
            s.Context[Cx.PaClVteCntl] = vte;
        });
        Assert.Equal(expected, depth[(16 * W) + 16], 1e-4f);
    }

    [Theory]
    [InlineData(0x43Fu, 0.2f)] // z / w: 0.4 / 2
    [InlineData(0x63Fu, 0.4f)] // VTX_Z_FMT: z is already divided by w
    public void VtxZFmtSelectsWhetherZIsDividedByW(uint vte, float expected)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        SceneVertex[] vertices =
        [
            new(-1f, -1f, 0.4f, 2f, 1, 0, 0, 1), new(1f, -1f, 0.4f, 2f, 1, 0, 0, 1), new(-1f, 1f, 0.4f, 2f, 1, 0, 0, 1),
            new(-1f, 1f, 0.4f, 2f, 1, 0, 0, 1), new(1f, -1f, 0.4f, 2f, 1, 0, 0, 1), new(1f, 1f, 0.4f, 2f, 1, 0, 0, 1),
        ];
        var depth = RenderDepth(rig, vertices, s => s.Context[Cx.PaClVteCntl] = vte);
        // The quad spans NDC +-0.5 (x / w) when divided; the centre is covered either way.
        Assert.Equal(expected, depth[(16 * W) + 16], 1e-4f);
    }

    // ---------------------------------------------------------------- 6. primitive types

    [Fact]
    public void APointListDrawsSquaresOfTheExportedPointSize()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        (double X, double Y)[] centres = [(8, 8), (20, 10), (16, 24)];
        var vertices = Vertices(vp, 0.5f, centres);
        var pixels = Render(rig, vertices, s =>
        {
            s.Uconfig[Uc.VgtPrimitiveType] = Prim.PointList;
            s.Context[Cx.SpiShaderPosFormat] = 4 | (4u << 4);
            s.Context[Cx.PaClVsOutCntl] = (1u << 16) | (1u << 21); // USE_VTX_POINT_SIZE in the misc vector (POS1)
            s.Context[0x281] = 0x1000u << 16; // PA_SU_POINT_MINMAX: min 0, max 256 px radius (12.4)
        }, vertexProgram: PointSizeVertex);
        // POS1.x = 8: a point is an 8x8 pixel square around its centre.
        var expected = new byte[W * H];
        foreach (var (cx, cy) in centres)
        {
            foreach (var (x, y) in Enumerable.Range(0, 8).SelectMany(dy => Enumerable.Range(0, 8).Select(dx => ((int)cx - 4 + dx, (int)cy - 4 + dy))))
            {
                expected[(y * W) + x] = 1;
            }
        }

        AssertCoverage(pixels, expected, "three 8 pixel points");
    }

    [Theory]
    [InlineData(8u, 1)]
    [InlineData(24u, 3, Skip = "Wide lines: Metal has no wide lines, so a PA_SU_LINE_CNTL width above one pixel is drawn one pixel wide with a one-time warning.")]
    public void ALineListDrawsLinesOfThePaSuLineCntlWidth(uint widthRegister, int widthPixels)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // A horizontal line through the centre of row 16 (y = 16.5) from x 4 to x 28.
        var vertices = Vertices(vp, 0.5f, (4, 16.5), (28, 16.5));
        var pixels = Render(rig, vertices, s =>
        {
            s.Uconfig[Uc.VgtPrimitiveType] = Prim.LineList;
            s.Context[Cx.PaSuLineCntl] = widthRegister; // half width in 1/16 pixel: 8 = one pixel
        });
        var firstRow = 16 - ((widthPixels - 1) / 2);
        var lit = Enumerable.Range(firstRow, widthPixels).SelectMany(y => Enumerable.Range(6, 20).Select(x => (x, y)));
        var dark = new[] { firstRow - 1, firstRow + widthPixels }.SelectMany(y => Enumerable.Range(6, 20).Select(x => (x, y)));
        AssertPixels(pixels, $"line width {widthPixels}px", lit, dark);
    }

    [Fact]
    public void ALineStripJoinsItsSegments()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // Horizontal along y = 6.5 to x = 26.5, then vertical down x = 26.5.
        var vertices = Vertices(vp, 0.5f, (4, 6.5), (26.5, 6.5), (26.5, 28));
        var pixels = Render(rig, vertices, s => s.Uconfig[Uc.VgtPrimitiveType] = Prim.LineStrip);
        var lit = Enumerable.Range(6, 19).Select(x => (x, 6)).Concat(Enumerable.Range(9, 17).Select(y => (26, y)));
        var dark = Enumerable.Range(6, 15).Select(x => (x, 9)).Concat(Enumerable.Range(10, 14).Select(y => (22, y)));
        AssertPixels(pixels, "line strip", lit, dark);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)] // cull the facing the strip does not have: both triangles stay
    [InlineData(false, true)] // cull the facing the strip has: nothing is drawn
    public void ATriangleStripFlipsEveryOtherTriangleSoItsFacingStaysConsistent(bool cullOtherFacing, bool cullStripFacing)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // TL, BL, TR, BR: triangle 0 = (TL, BL, TR), triangle 1 = (BL, BR, TR) after the swap.
        (double X, double Y)[] corners = [(4, 4), (4, 28), (28, 4), (28, 28)];
        var area = Area(corners[0], corners[1], corners[2]);
        var face = area < 0 ? 0u : 1u; // makes the strip's facing the front face
        var pixels = Render(rig, Vertices(vp, 0.5f, corners), s =>
        {
            s.Uconfig[Uc.VgtPrimitiveType] = Prim.TriangleStrip;
            // Front = the strip's facing; cull back removes the other facing, cull front removes the strip's.
            s.Context[Cx.PaSuScModeCntl] = (cullOtherFacing ? 2u : 0u) | (cullStripFacing ? 1u : 0u) | (face << 2);
        });
        var expected = cullStripFacing ? new byte[W * H] : Rect(4, 4, 28, 28);
        AssertCoverage(pixels, expected, $"strip cullOther={cullOtherFacing} cullStrip={cullStripFacing}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATriangleFanSharesItsFirstVertex(bool cullOtherFacing)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // A hub and four rim vertices, one fan of three triangles (the rim covers 3/4 of a diamond).
        (double X, double Y)[] points = [(16, 16), (4, 16), (16, 4), (28, 16), (16, 28)];
        var area = Area(points[0], points[1], points[2]);
        var face = area < 0 ? 0u : 1u;
        var pixels = Render(rig, Vertices(vp, 0.5f, points), s =>
        {
            s.Uconfig[Uc.VgtPrimitiveType] = Prim.TriangleFan;
            s.Context[Cx.PaSuScModeCntl] = (cullOtherFacing ? 2u : 0u) | (face << 2);
        });
        var expected = Coverage(
        [
            [points[0], points[1], points[2]],
            [points[0], points[2], points[3]],
            [points[0], points[3], points[4]],
        ]);
        AssertCoverage(pixels, expected, $"fan cullOther={cullOtherFacing}");
    }

    /// <summary>
    /// DI_PT_RECTLIST: three vertices per rectangle, the fourth corner inferred as
    /// v3 = v1 + v2 - v0 in screen space. Two rectangles in one draw, with a different
    /// corner arrangement each, so one triangle per rectangle (a strip) is caught.
    /// </summary>
    [Fact]
    public void ARectangleListFillsEveryRectangleCompletely()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        var vertices = Vertices(vp, 0.5f,
            (4, 4), (16, 4), (4, 12),
            (28, 28), (20, 28), (28, 18));
        var pixels = Render(rig, vertices, s => s.Uconfig[Uc.VgtPrimitiveType] = Prim.RectangleList);
        var expected = Rect(4, 4, 16, 12);
        var second = Rect(20, 18, 28, 28);
        for (var index = 0; index < expected.Length; index++)
        {
            expected[index] |= second[index];
        }

        AssertCoverage(pixels, expected, "two rectangles from a RECTLIST draw");
    }

    [Fact]
    public void ASingleRectangleFromARectangleListIsFilledCompletely()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // v0 is the corner both edges leave from: v3 = v1 + v2 - v0 = (20, 18).
        var vertices = Vertices(vp, 0.5f, (28, 28), (20, 28), (28, 18));
        var pixels = Render(rig, vertices, s => s.Uconfig[Uc.VgtPrimitiveType] = Prim.RectangleList);
        AssertCoverage(pixels, Rect(20, 18, 28, 28), "one rectangle from a RECTLIST draw");
    }

    /// <summary>
    /// DI_PT_QUADLIST: every four vertices are one quad, (q0, q1, q2) and (q0, q2, q3). Two
    /// quads in one draw, the second not a rectangle, so a fan of all eight vertices or one
    /// triangle per quad is caught.
    /// </summary>
    [Fact]
    public void AQuadListFillsEveryQuadCompletely()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        (double X, double Y)[] points = [(4, 4), (14, 4), (14, 14), (4, 14), (18, 18), (28, 20), (26, 28), (18, 26)];
        var pixels = Render(rig, Vertices(vp, 0.5f, points), s => s.Uconfig[Uc.VgtPrimitiveType] = PrimQuadList);
        var expected = Coverage(
        [
            [points[0], points[1], points[2]],
            [points[0], points[2], points[3]],
            [points[4], points[5], points[6]],
            [points[4], points[6], points[7]],
        ]);
        AssertCoverage(pixels, expected, "two quads from a QUADLIST draw");
    }

    /// <summary>DI_PT_POLYGON: one fan around the first vertex.</summary>
    [Fact]
    public void APolygonIsOneFanAroundItsFirstVertex()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        (double X, double Y)[] points = [(16, 3), (28, 12), (23, 28), (9, 28), (4, 12)];
        var pixels = Render(rig, Vertices(vp, 0.5f, points), s => s.Uconfig[Uc.VgtPrimitiveType] = PrimPolygon);
        var expected = Coverage(
        [
            [points[0], points[1], points[2]],
            [points[0], points[2], points[3]],
            [points[0], points[3], points[4]],
        ]);
        AssertCoverage(pixels, expected, "pentagon from a POLYGON draw");
    }

    // Two squares of four vertices each, for indexed fans.
    private static readonly (double X, double Y)[] FanSquares =
        [(4, 4), (14, 4), (14, 14), (4, 14), (18, 18), (28, 18), (28, 28), (18, 28)];

    /// <summary>
    /// A restart index ends an indexed fan and the next index is the hub of a new one: two
    /// squares, where one fan of all eight indices would cover the space between them.
    /// </summary>
    [Theory]
    [InlineData(false, 0xFFFFu)]
    [InlineData(true, 0xFFFFFFFFu)]
    [InlineData(true, 0x1234u)] // a reset index other than all ones
    public void AnIndexedFanRestartsAtTheResetIndex(bool wideIndices, uint resetIndex)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        uint[] indices = [0, 1, 2, 3, resetIndex, 4, 5, 6, 7];
        var address = wideIndices
            ? rig.Memory.Upload<uint>(indices)
            : rig.Memory.Upload<ushort>([.. indices.Select(i => (ushort)i)]);
        var pixels = Render(rig, Vertices(vp, 0.5f, FanSquares), s =>
        {
            s.Uconfig[Uc.VgtPrimitiveType] = Prim.TriangleFan;
            s.Uconfig[GeMultiPrimIbResetEn] = 1;
            s.Context[VgtMultiPrimIbResetIndx] = resetIndex;
        }, draw: pm4 =>
        {
            pm4.NumInstances(1);
            pm4.IndexType(wideIndices ? 1u : 0u);
            pm4.DrawIndexed(address, (uint)indices.Length, (uint)indices.Length);
        });
        var expected = Rect(4, 4, 14, 14);
        var second = Rect(18, 18, 28, 28);
        for (var index = 0; index < expected.Length; index++)
        {
            expected[index] |= second[index];
        }

        AssertCoverage(pixels, expected, $"two indexed fans split by reset index 0x{resetIndex:X}, {(wideIndices ? 32 : 16)}-bit indices");
    }

    // ---------------------------------------------------------------- 7. instancing and indexed draws

    // A triangle at the left edge, translated by 8 pixels per instance by InstancedVertex.
    private static readonly (double X, double Y)[] InstanceTriangle = [(2, 4), (8, 4), (2, 12)];

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void EveryInstanceDrawsTheMeshWithItsOwnInstanceId(uint instances)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        var vertices = Vertices(vp, 0.5f, InstanceTriangle);
        var pixels = Render(rig, vertices, draw: pm4 =>
        {
            pm4.NumInstances(instances);
            pm4.DrawAuto(3);
        }, vertexProgram: InstancedVertex);
        var triangles = Enumerable.Range(0, (int)instances)
            .Select(i => InstanceTriangle.Select(p => (p.X + (8 * i), p.Y)).ToArray());
        AssertCoverage(pixels, Coverage(triangles), $"{instances} instances");
    }

    // Eight vertices: two triangles to draw (0,1,2 and 4,5,6) and decoys (3 and 7) that
    // sit elsewhere and must never be fetched.
    private static SceneVertex[] IndexedMesh(Vp vp) =>
        Vertices(vp, 0.5f, (4, 4), (12, 4), (4, 12), (30, 30), (18, 18), (28, 18), (18, 26), (2, 30));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIndexedDrawFetchesTheIndexedVertices(bool wideIndices)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        uint[] indices = [4, 6, 5, 0, 1, 2];
        var address = wideIndices
            ? rig.Memory.Upload<uint>(indices)
            : rig.Memory.Upload<ushort>([.. indices.Select(i => (ushort)i)]);
        var pixels = Render(rig, IndexedMesh(vp), draw: pm4 =>
        {
            pm4.NumInstances(1);
            pm4.IndexType(wideIndices ? 1u : 0u);
            pm4.DrawIndexed(address, (uint)indices.Length, (uint)indices.Length);
        });
        var expected = Coverage([[(18, 18), (18, 26), (28, 18)], [(4, 4), (12, 4), (4, 12)]]);
        AssertCoverage(pixels, expected, $"indexed draw, {(wideIndices ? 32 : 16)}-bit indices");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIndexedDrawStartsAtItsIndexBaseAddress(bool wideIndices)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // Three leading indices that would draw the decoys, then the real draw starts after them.
        uint[] indices = [3, 7, 3, 0, 1, 2, 4, 5, 6];
        var size = wideIndices ? 4UL : 2UL;
        var address = wideIndices
            ? rig.Memory.Upload<uint>(indices)
            : rig.Memory.Upload<ushort>([.. indices.Select(i => (ushort)i)]);
        var pixels = Render(rig, IndexedMesh(vp), draw: pm4 =>
        {
            pm4.NumInstances(1);
            pm4.IndexType(wideIndices ? 1u : 0u);
            pm4.DrawIndexed(address + (3 * size), 6, 6);
        });
        var expected = Coverage([[(4, 4), (12, 4), (4, 12)], [(18, 18), (28, 18), (18, 26)]]);
        AssertCoverage(pixels, expected, $"indexed draw from an INDEX_BASE offset, {(wideIndices ? 32 : 16)}-bit indices");
    }

    [Fact]
    public void AnIndexedDrawRepeatsForEveryInstance()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        // The triangle at vertices 0,1,2 only, three instances shifted by 8 pixels each.
        SceneVertex[] vertices = Vertices(vp, 0.5f, InstanceTriangle[0], InstanceTriangle[1], InstanceTriangle[2], (30, 30));
        ushort[] indices = [2, 0, 1];
        var address = rig.Memory.Upload<ushort>(indices);
        var pixels = Render(rig, vertices, draw: pm4 =>
        {
            pm4.NumInstances(3);
            pm4.IndexType(0);
            pm4.DrawIndexed(address, 3, 3);
        }, vertexProgram: InstancedVertex);
        var triangles = Enumerable.Range(0, 3).Select(i => InstanceTriangle.Select(p => (p.X + (8 * i), p.Y)).ToArray());
        AssertCoverage(pixels, Coverage(triangles), "indexed draw, three instances");
    }

    // A fan of two triangles at the left edge (a square), translated by 8 pixels per instance.
    private static readonly (double X, double Y)[] InstanceFan = [(2, 4), (8, 4), (8, 12), (2, 12)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryInstanceDrawsTheWholeFan(bool indexed)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var vp = Vp.Default;
        var address = rig.Memory.Upload<ushort>([0, 1, 2, 3]);
        var pixels = Render(rig, Vertices(vp, 0.5f, InstanceFan), s => s.Uconfig[Uc.VgtPrimitiveType] = Prim.TriangleFan,
            draw: pm4 =>
            {
                pm4.NumInstances(3);
                if (indexed)
                {
                    pm4.IndexType(0);
                    pm4.DrawIndexed(address, 4, 4);
                }
                else
                {
                    pm4.DrawAuto(4);
                }
            }, vertexProgram: InstancedVertex);
        var triangles = Enumerable.Range(0, 3).SelectMany(i => new[]
        {
            new[] { InstanceFan[0], InstanceFan[1], InstanceFan[2] }.Select(p => (p.X + (8 * i), p.Y)).ToArray(),
            new[] { InstanceFan[0], InstanceFan[2], InstanceFan[3] }.Select(p => (p.X + (8 * i), p.Y)).ToArray(),
        });
        AssertCoverage(pixels, Coverage(triangles), $"{(indexed ? "indexed" : "auto")} fan, three instances");
    }
}
