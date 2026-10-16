// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>A stencil face: DB_STENCIL_CONTROL ops plus DB_STENCILREFMASK(_BF) fields.</summary>
internal readonly record struct StencilFace(uint Fail, uint Pass, uint ZFail, byte Ref = 0, byte Mask = 0xFF,
    byte Write = 0xFF, byte OpValue = 0);

/// <summary>
/// A colour + depth (+ stencil) scene: one PM4 stream, depth read back through a texture
/// into an R32F target, stencil read back bit by bit through colour channels.
/// </summary>
internal sealed class DepthRig
{
    public SceneRig Rig { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint ZFormat { get; }
    public ulong Color { get; }
    public ulong Depth { get; }
    public ulong Stencil { get; }
    public ScenePm4 Pm4 { get; } = new();

    public DepthRig(SceneRig rig, uint width, uint height, uint zFormat, bool stencil = false)
    {
        Rig = rig;
        Width = width;
        Height = height;
        ZFormat = zFormat;
        Color = rig.ColorTarget(width, height);
        Depth = rig.Memory.Allocate(0x100000, 0x10000);
        Stencil = stencil ? rig.Memory.Allocate(0x100000, 0x10000) : 0;
    }

    public SceneState State() =>
        new SceneState(Width, Height).ColorTarget(0, Color, Width, Height).DepthTarget(Depth, Width, Height, ZFormat, Stencil);

    public void Draw(SceneState state, IReadOnlyList<SceneVertex> mesh) => Rig.DrawMesh(Pm4, state, mesh);

    /// <summary>A pixel-aligned rectangle at depth z.</summary>
    public SceneVertex[] Rect(float z, float r, float g, float b, int x0, int y0, int x1, int y1) =>
        SceneDraws.Quad(z, r, g, b, 1f,
            -1f + (2f * x0 / Width), 1f - (2f * y1 / Height), -1f + (2f * x1 / Width), 1f - (2f * y0 / Height));

    public SceneVertex[] Full(float z, float r, float g, float b) => Rect(z, r, g, b, 0, 0, (int)Width, (int)Height);

    /// <summary>
    /// A full-screen quad whose depth is the plane z0 + sx*u + sy*v (u right, v down, both 0..1 over the target).
    /// </summary>
    public static SceneVertex[] Plane(float z0, float sx, float sy, float r = 1, float g = 1, float b = 1) =>
    [
        SceneVertex.At(-1, -1, z0 + sy, r, g, b),
        SceneVertex.At(1, -1, z0 + sx + sy, r, g, b),
        SceneVertex.At(-1, 1, z0, r, g, b),
        SceneVertex.At(-1, 1, z0, r, g, b),
        SceneVertex.At(1, -1, z0 + sx + sy, r, g, b),
        SceneVertex.At(1, 1, z0 + sx, r, g, b),
    ];

    /// <summary>The same triangles with the opposite winding.</summary>
    public static SceneVertex[] Reverse(SceneVertex[] mesh)
    {
        var result = (SceneVertex[])mesh.Clone();
        for (var index = 0; index + 2 < result.Length; index += 3)
        {
            (result[index + 1], result[index + 2]) = (result[index + 2], result[index + 1]);
        }

        return result;
    }

    /// <summary>Samples the depth surface into an R32F target; the result is valid after the stream ran.</summary>
    public Func<float[]> QueueDepthReadback()
    {
        var readback = Rig.ColorTarget(Width, Height);
        var copy = new SceneState(Width, Height).ColorTarget(0, readback, Width, Height, format: 4, numberType: 7);
        // GFX10 IMG_FORMAT 16_UNORM = 7 for a Z16 surface, 32_FLOAT = 22 for a Z32F surface; SW mode 24 = 64KB_Z_X.
        uint[] userData = [.. Descriptors.Texture(Depth, ZFormat == 1 ? 7u : 22u, Width, Height, tileMode: 24), .. Descriptors.Sampler()];
        Rig.DrawMesh(Pm4, copy, SceneDraws.TexturedQuad(), ScenePrograms.SampledTexture, userData);
        return () => Rig.ReadImage<float>(readback, Width, Height);
    }

    public Func<uint[]> QueueColorReadback() => () => Rig.ReadImage<uint>(Color, Width, Height);

    /// <summary>
    /// Reads the stencil plane exactly: eight draws test one bit each (EQUAL on a one-bit compare mask) and
    /// set one colour channel of two targets, so every pixel's stencil value is decoded without a candidate list.
    /// </summary>
    public Func<byte[]> QueueStencilReadback()
    {
        var targets = new[] { Rig.ColorTarget(Width, Height), Rig.ColorTarget(Width, Height) };
        for (var bit = 0; bit < 8; bit++)
        {
            var state = new SceneState(Width, Height).ColorTarget(0, targets[bit / 4], Width, Height)
                .DepthTarget(Depth, Width, Height, ZFormat, Stencil);
            state.Context[Cx.CbTargetMask] = 1u << (bit % 4);
            DepthSceneTests.SetStencil(state, new StencilFace(0, 0, 0, (byte)(1 << bit), (byte)(1 << bit), 0));
            state.DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Equal);
            Draw(state, Full(0.5f, 1, 1, 1));
        }

        return () =>
        {
            var low = Rig.ReadImage<uint>(targets[0], Width, Height);
            var high = Rig.ReadImage<uint>(targets[1], Width, Height);
            var result = new byte[Width * Height];
            for (var index = 0; index < result.Length; index++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    var word = bit < 4 ? low[index] : high[index];
                    if (((word >> ((bit % 4) * 8)) & 0xFF) != 0)
                    {
                        result[index] |= (byte)(1 << bit);
                    }
                }
            }

            return result;
        };
    }
}

/// <summary>
/// Depth and stencil scenes: compare functions, clears, formats, bias, reversed Z, depth bounds,
/// stencil operations and the tiled depth layout, each against an analytic oracle.
/// </summary>
public sealed class DepthSceneTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint Size = 64;
    private const uint Red = 0xFF0000FF;
    private const uint Green = 0xFF00FF00;
    private const uint Blue = 0xFFFF0000;
    private const uint Black = 0xFF000000;

    internal static void SetStencil(SceneState state, StencilFace front, StencilFace? back = null)
    {
        var rear = back ?? front;
        state.Context[Cx.DbStencilControl] = front.Fail | (front.Pass << 4) | (front.ZFail << 8) |
            (rear.Fail << 12) | (rear.Pass << 16) | (rear.ZFail << 20);
        state.Context[Cx.DbStencilRefMask] = front.Ref | ((uint)front.Mask << 8) | ((uint)front.Write << 16) | ((uint)front.OpValue << 24);
        state.Context[Cx.DbStencilRefMaskBf] = rear.Ref | ((uint)rear.Mask << 8) | ((uint)rear.Write << 16) | ((uint)rear.OpValue << 24);
    }

    private static bool Passes(uint func, double incoming, double stored) => func switch
    {
        0 => false,
        1 => incoming < stored,
        2 => incoming == stored,
        3 => incoming <= stored,
        4 => incoming > stored,
        5 => incoming != stored,
        6 => incoming >= stored,
        _ => true,
    };

    private static float Unorm16(double value) => (float)(Math.Round(Math.Clamp(value, 0, 1) * 65535.0, MidpointRounding.AwayFromZero) / 65535.0);

    // 1. ZFUNC ----------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> ZFuncCases()
    {
        for (uint func = 0; func < 8; func++)
        {
            yield return [func, true];
            yield return [func, false];
        }
    }

    [Theory]
    [MemberData(nameof(ZFuncCases))]
    public void ZFuncDecidesColourAndDepth(uint func, bool write)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3);
        d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Full(0.5f, 1, 0, 0));
        float[] zs = [0.25f, 0.5f, 0.75f];
        for (var i = 0; i < 3; i++)
        {
            d.Draw(d.State().DepthControl(true, write, (CompareFunc)func), d.Rect(zs[i], 0, 1, 0, i * 16, 0, (i * 16) + 16, 64));
        }

        var depth = d.QueueDepthReadback();
        var colour = d.QueueColorReadback();
        rig.Submit(d.Pm4);
        var depths = depth();
        var colours = colour();
        for (var i = 0; i < 3; i++)
        {
            var pass = Passes(func, zs[i], 0.5);
            var x = (i * 16) + 8;
            Assert.Equal(pass ? Green : Red, colours[(32 * Size) + x]);
            Assert.Equal(pass && write ? zs[i] : 0.5f, depths[(32 * Size) + x]);
        }

        // Outside the three test rectangles nothing changed.
        Assert.Equal(Red, colours[(32 * Size) + 56]);
        Assert.Equal(0.5f, depths[(32 * Size) + 56]);
    }

    // 2. Depth clears -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3u, 0f)]
    [InlineData(3u, 0.25f)]
    [InlineData(3u, 1f)]
    [InlineData(1u, 0f)]
    [InlineData(1u, 0.25f)]
    [InlineData(1u, 1f)]
    public void DepthClearEnableWritesTheClearValueEverywhere(uint zFormat, float clearValue)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat);
        d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Full(0.5f, 1, 0, 0));
        var clear = d.State().DepthControl(true, true, CompareFunc.Always);
        clear.Context[Cx.DbRenderControl] = 1;
        clear.Context[Cx.DbDepthClear] = SceneState.F(clearValue);
        clear.Context[Cx.CbTargetMask] = 0;
        d.Draw(clear, d.Full(0.9f, 0, 0, 0));
        // A normal draw after the clear compares against the cleared value: LESS at z=0.5.
        var after = d.State().DepthControl(true, true, CompareFunc.Less);
        d.Draw(after, d.Rect(0.5f, 0, 0, 1, 0, 0, 32, 64));
        var depth = d.QueueDepthReadback();
        var colour = d.QueueColorReadback();
        rig.Submit(d.Pm4);
        var depths = depth();
        var colours = colour();
        var stored = zFormat == 1 ? Unorm16(clearValue) : clearValue;
        var half = zFormat == 1 ? Unorm16(0.5) : 0.5f;
        var passes = 0.5f < stored;
        // The D16 clear conversion may land one LSB either side of round-to-nearest (observed: truncation), so D16 allows 1 LSB.
        var tolerance = zFormat == 1 ? 1.5f / 65535f : 1e-6f;
        Assert.Equal(passes ? half : stored, depths[(32 * Size) + 8], tolerance);
        Assert.Equal(passes ? Blue : Red, colours[(32 * Size) + 8]);
        Assert.Equal(stored, depths[(32 * Size) + 56], tolerance);
        Assert.Equal(Red, colours[(32 * Size) + 56]);
    }

    // 3. Formats ----------------------------------------------------------------------------------------------

    [Fact]
    public void D16QuantisesToUnorm16WithRounding()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 1);
        // k + 0.2 / 0.7 steps tell round-to-nearest from truncation.
        float[] zs = [0f, 100.7f / 65535f, 30000.2f / 65535f, 30000.7f / 65535f, 40000.45f / 65535f, 40000.55f / 65535f, 65534.7f / 65535f, 1f];
        for (var band = 0; band < zs.Length; band++)
        {
            d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Rect(zs[band], 1, 1, 1, 0, band * 8, 64, (band * 8) + 8));
        }

        var depth = d.QueueDepthReadback();
        rig.Submit(d.Pm4);
        var depths = depth();
        for (var band = 0; band < zs.Length; band++)
        {
            Assert.Equal(Unorm16(zs[band]), depths[(((band * 8) + 4) * Size) + 20], 7);
        }
    }

    [Fact]
    public void D32FloatStoresTheExactFloat()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3);
        float[] zs = [0f, 1e-7f, 0.1234567f, 0.3333333f, 0.5000001f, 0.9999999f, 1f, 0.7071068f];
        for (var band = 0; band < zs.Length; band++)
        {
            d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Rect(zs[band], 1, 1, 1, 0, band * 8, 64, (band * 8) + 8));
        }

        var depth = d.QueueDepthReadback();
        rig.Submit(d.Pm4);
        var depths = depth();
        for (var band = 0; band < zs.Length; band++)
        {
            Assert.Equal(zs[band], depths[(((band * 8) + 4) * Size) + 20]);
        }
    }

    [Fact]
    public void D32FloatWithStencilKeepsBothPlanes()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        var state = d.State().DepthControl(true, true, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Always);
        SetStencil(state, new StencilFace(0, 4, 0, OpValue: 0x5A));
        d.Draw(state, d.Rect(0.3f, 1, 1, 1, 0, 0, 32, 64));
        var depth = d.QueueDepthReadback();
        var stencil = d.QueueStencilReadback();
        rig.Submit(d.Pm4);
        var depths = depth();
        var stencils = stencil();
        Assert.Equal(0.3f, depths[(32 * Size) + 8]);
        Assert.Equal(0f, depths[(32 * Size) + 56]);
        Assert.Equal(0x5A, stencils[(32 * Size) + 8]);
        Assert.Equal(0, stencils[(32 * Size) + 56]);
    }

    // 4. Depth bias -------------------------------------------------------------------------------------------

    private const uint FrontOffsetEnable = 1u << 11;
    private const uint BackOffsetEnable = 1u << 12;

    private float BiasedDepth(SceneRig rig, uint zFormat, IReadOnlyList<SceneVertex> mesh, Action<SceneState> setup)
    {
        var d = new DepthRig(rig, Size, Size, zFormat);
        var state = d.State().DepthControl(true, true, CompareFunc.Always);
        setup(state);
        d.Draw(state, mesh);
        var depth = d.QueueDepthReadback();
        rig.Submit(d.Pm4);
        return depth()[(32 * Size) + 32];
    }

    private static void BiasRegisters(SceneState state, uint mode, float frontOffset, float backOffset, float frontScale = 0,
        float backScale = 0, float clamp = 0)
    {
        state.Context[Cx.PaSuScModeCntl] = mode;
        state.Context[Cx.PaSuPolyOffsetFrontOffset] = SceneState.F(frontOffset);
        state.Context[Cx.PaSuPolyOffsetBackOffset] = SceneState.F(backOffset);
        state.Context[Cx.PaSuPolyOffsetFrontScale] = SceneState.F(frontScale);
        state.Context[Cx.PaSuPolyOffsetBackScale] = SceneState.F(backScale);
        state.Context[Cx.PaSuPolyOffsetClamp] = SceneState.F(clamp);
    }

    /// <summary>The D32F bias unit r = 2^(exponent(z) - 23) of the largest z in the primitive.</summary>
    private static float FloatUnit(float z) => MathF.ScaleB(1f, Math.ILogB(z) - 23);

    [Theory]
    [InlineData(0.5f, 1000f)]
    [InlineData(0.3f, 1000f)]
    [InlineData(0.75f, 777f)]
    [InlineData(0.5f, -1000f)]
    [InlineData(0.05f, 4000f)]
    public void ConstantBiasOnAFloatDepthTargetIsInUnitsOfTheDepthExponent(float z, float units)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var biased = BiasedDepth(rig, 3, SceneDraws.Quad(z, 1, 1, 1), s => BiasRegisters(s, FrontOffsetEnable, units, units));
        Assert.Equal(z + (units * FloatUnit(z)), biased, 1e-6f);
    }

    [Fact]
    public void BiasIsOffWhenNeitherFaceEnablesIt()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var biased = BiasedDepth(rig, 3, SceneDraws.Quad(0.5f, 1, 1, 1), s => BiasRegisters(s, 0, 5000, 5000, 16, 16));
        Assert.Equal(0.5f, biased);
    }

    [Theory]
    [InlineData(0.75f, -24)]
    [InlineData(0.5f, -24)]
    [InlineData(0.75f, -16)]
    public void ConstantBiasOnAFixedPointViewOfAFloatTargetIsInFixedPointUnitsAboveOneHalf(float z, int negativeBits)
    {
        // POLY_OFFSET_DB_IS_FLOAT_FMT = 0 on a D32F target (a Z24 surface the host keeps as float):
        // r = 2^NEG_NUM_DB_BITS flat. The host's float unit matches it for depths in [0.5, 1).
        using var rig = CreateRig();
        if (rig is null) return;
        var biased = BiasedDepth(rig, 3, SceneDraws.Quad(z, 1, 1, 1), s =>
        {
            BiasRegisters(s, FrontOffsetEnable, 100, 100);
            s.Context[Cx.PaSuPolyOffsetDbFmtCntl] = unchecked((uint)negativeBits) & 0xFF;
        });
        Assert.Equal(z + (100 * MathF.ScaleB(1f, negativeBits)), biased, 1e-6f);
    }

    [Fact]
    public void ConstantFixedPointBiasOnAFloatTargetBelowOneHalfIsTheDocumentedFloatUnit()
    {
        // Documented deviation (RenderExecutor.DepthBiasConstantFactor): hardware would add
        // 1000 * 2^-24 at any depth; the host's float bias unit at z = 0.25 is 2^-25.
        using var rig = CreateRig();
        if (rig is null) return;
        const float z = 0.25f;
        var biased = BiasedDepth(rig, 3, SceneDraws.Quad(z, 1, 1, 1), s =>
        {
            BiasRegisters(s, FrontOffsetEnable, 1000, 1000);
            s.Context[Cx.PaSuPolyOffsetDbFmtCntl] = unchecked((uint)-24) & 0xFF;
        });
        Assert.Equal(z + (1000 * MathF.ScaleB(1f, -25)), biased, 1e-7f);
    }

    [Theory]
    [InlineData(256f)]
    [InlineData(-512f)]
    public void ConstantBiasOnD16IsInUnitsOfTwoToTheMinusSixteen(float units)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const float z = 0.5001f;
        var biased = BiasedDepth(rig, 1, SceneDraws.Quad(z, 1, 1, 1), s => BiasRegisters(s, FrontOffsetEnable, units, units));
        var expected = Unorm16(z + (units / 65536.0));
        Assert.True(MathF.Abs(expected - biased) <= 2f / 65535f, $"expected {expected} got {biased}");
    }

    [Theory]
    [InlineData(1f, 0f, 0.2f, 0.4f)]
    [InlineData(2f, 0f, 0.2f, 0.4f)]
    [InlineData(1f, 0f, 0.4f, 0.2f)]
    [InlineData(-1f, 0f, 0.2f, 0.4f)]
    [InlineData(1f, 500f, 0.2f, 0.4f)]
    public void SlopeBiasIsTheLargestScreenSpaceDepthSlopeTimesScaleOverSixteen(float slopeFactor, float units, float sx, float sy)
    {
        // Radv/radeonsi program register scale = 16 * the API slope factor.
        using var rig = CreateRig();
        if (rig is null) return;
        const float z0 = 0.25f;
        var biased = BiasedDepth(rig, 3, DepthRig.Plane(z0, sx, sy), s =>
            BiasRegisters(s, FrontOffsetEnable, units, units, slopeFactor * 16f, slopeFactor * 16f));
        var u = 32.5f / Size;
        var plane = z0 + (sx * u) + (sy * u);
        var slope = MathF.Max(MathF.Abs(sx), MathF.Abs(sy)) / Size;
        var zMax = z0 + sx + sy;
        var expected = plane + (slopeFactor * slope) + (units * FloatUnit(zMax));
        Assert.Equal(expected, biased, 1e-4f);
    }

    [Theory]
    [InlineData(100000f, 0.002f, 0.502f)]
    [InlineData(-100000f, -0.003f, 0.497f)]
    [InlineData(100000f, 0f, 0.5f + (100000f / 16777216f))]
    [InlineData(100000f, 1f, 0.5f + (100000f / 16777216f))]
    [InlineData(-100000f, 0.002f, 0.5f - (100000f / 16777216f))]
    public void BiasClampLimitsTheTotalOffset(float units, float clamp, float expected)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var biased = BiasedDepth(rig, 3, SceneDraws.Quad(0.5f, 1, 1, 1), s => BiasRegisters(s, FrontOffsetEnable, units, units, clamp: clamp));
        Assert.Equal(expected, biased, 1e-6f);
    }

    // The front enable applies only to front faces, the back enable only to back faces, each with its own offset.
    // FACE = 0: the quad as SceneDraws.Quad winds it (counter-clockwise in NDC y-up) is the front face.
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void PolygonOffsetFollowsTheFacingOfTheTriangle(bool frontEnable, bool backEnable, bool frontFacing)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var mesh = SceneDraws.Quad(0.5f, 1, 1, 1);
        if (!frontFacing)
        {
            mesh = DepthRig.Reverse(mesh);
        }

        var mode = (frontEnable ? FrontOffsetEnable : 0) | (backEnable ? BackOffsetEnable : 0);
        var biased = BiasedDepth(rig, 3, mesh, s => BiasRegisters(s, mode, 1000, 3000));
        var enabled = frontFacing ? frontEnable : backEnable;
        var units = frontFacing ? 1000 : 3000;
        Assert.Equal(0.5f + (enabled ? units * FloatUnit(0.5f) : 0f), biased, 1e-6f);
    }

    // 5. Reversed Z -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(1f, 0f)]
    [InlineData(0.25f, 0.75f)]
    [InlineData(0.75f, 0.25f)]
    [InlineData(0f, 0.5f)]
    public void ViewportZScaleAndOffsetMapClipZToDepth(float minZ, float maxZ)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3);
        var state = d.State().DepthControl(true, true, CompareFunc.Always).Viewport(0, 0, Size, Size, minZ, maxZ);
        d.Draw(state, d.Full(0.4f, 1, 1, 1));
        var depth = d.QueueDepthReadback();
        rig.Submit(d.Pm4);
        Assert.Equal(minZ + (0.4f * (maxZ - minZ)), depth()[(32 * Size) + 32], 1e-6f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversedZWithGreaterAndAZeroClearLetsNearObjectsWin(bool clearWithTheDepthClearPath)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3);
        State Reversed(CompareFunc func) => new(d, func);
        if (clearWithTheDepthClearPath)
        {
            var clear = d.State().DepthControl(true, true, CompareFunc.Always).Viewport(0, 0, Size, Size, 1, 0);
            clear.Context[Cx.DbRenderControl] = 1;
            clear.Context[Cx.DbDepthClear] = SceneState.F(0f);
            clear.Context[Cx.CbTargetMask] = 0;
            d.Draw(clear, d.Full(0.9f, 0, 0, 0));
        }

        // Far (clip z 0.8 -> depth 0.2) everywhere, near (0.3 -> 0.7) on the right, middle (0.6 -> 0.4) everywhere.
        d.Draw(Reversed(CompareFunc.Greater).Value, d.Full(0.8f, 1, 0, 0));
        d.Draw(Reversed(CompareFunc.Greater).Value, d.Rect(0.3f, 0, 1, 0, 32, 0, 64, 64));
        d.Draw(Reversed(CompareFunc.Greater).Value, d.Full(0.6f, 0, 0, 1));
        var depth = d.QueueDepthReadback();
        var colour = d.QueueColorReadback();
        rig.Submit(d.Pm4);
        var depths = depth();
        var colours = colour();
        Assert.Equal(Blue, colours[(32 * Size) + 8]);
        Assert.Equal(0.4f, depths[(32 * Size) + 8], 1e-6f);
        Assert.Equal(Green, colours[(32 * Size) + 56]);
        Assert.Equal(0.7f, depths[(32 * Size) + 56], 1e-6f);
    }

    private readonly struct State(DepthRig rig, CompareFunc func)
    {
        public SceneState Value { get; } = rig.State().DepthControl(true, true, func).Viewport(0, 0, rig.Width, rig.Height, 1, 0);
    }

    // 6. Depth bounds -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, 0.5f, 1f)]
    [InlineData(false, 0.5f, 1f)]
    // Both ends are inclusive: a stored depth exactly at a bound passes.
    [InlineData(true, 0.2f, 0.6f)]
    [InlineData(true, 0.2f, 0.5f)]
    [InlineData(true, 0.3f, 0.6f)]
    // Bounds beyond [0, 1] hold every stored depth.
    [InlineData(true, -1f, 2f)]
    // An empty range (inverted, or wholly beyond [0, 1]) holds no stored depth: every pixel is discarded.
    [InlineData(true, 0.7f, 0.3f)]
    [InlineData(true, 1.5f, 2f)]
    [InlineData(true, -1f, -0.5f)]
    public void DepthBoundsDiscardPixelsWhoseStoredDepthIsOutside(bool boundsEnable, float min, float max)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3);
        d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Rect(0.2f, 1, 0, 0, 0, 0, 32, 64));
        d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Rect(0.6f, 1, 0, 0, 32, 0, 64, 64));
        var state = d.State().DepthControl(true, true, CompareFunc.Always, bounds: boundsEnable);
        state.Context[Cx.DbDepthBoundsMin] = SceneState.F(min);
        state.Context[Cx.DbDepthBoundsMax] = SceneState.F(max);
        d.Draw(state, d.Full(0.1f, 0, 1, 0));
        var depth = d.QueueDepthReadback();
        var colour = d.QueueColorReadback();
        // A range the host test has to take (anything but [0, 1] or empty once clamped to it)
        // needs Metal's depth bounds test. A device without it (before Apple10) runs the draw
        // unbounded: the documented deviation in MetalRenderHost.ApplyDepthBounds.
        var needsHostTest = boundsEnable && Math.Max(min, 0f) <= Math.Min(max, 1f) &&
            (Math.Max(min, 0f) != 0f || Math.Min(max, 1f) != 1f);
        var unbounded = needsHostTest && !rig.Host.DepthBoundsTestSupported;
        rig.Submit(d.Pm4);
        var depths = depth();
        var colours = colour();
        // Left stored 0.2 and right stored 0.6 (both drawn red) each pass when inside [min, max] or the test is off.
        bool Passes(float stored) => !boundsEnable || unbounded || (min <= stored && stored <= max);
        Assert.Equal(Passes(0.2f) ? Green : Red, colours[(32 * Size) + 8]);
        Assert.Equal(Passes(0.2f) ? 0.1f : 0.2f, depths[(32 * Size) + 8]);
        Assert.Equal(Passes(0.6f) ? Green : Red, colours[(32 * Size) + 56]);
        Assert.Equal(Passes(0.6f) ? 0.1f : 0.6f, depths[(32 * Size) + 56]);
    }

    // 7. Stencil ----------------------------------------------------------------------------------------------

    private static byte ApplyStencilOp(uint op, byte stored, byte opValue, byte reference) => op switch
    {
        0 => stored,
        1 => 0,
        2 => 0xFF,
        3 => reference,
        4 => opValue,
        5 => (byte)Math.Min(stored + opValue, 255),
        6 => (byte)Math.Max(stored - opValue, 0),
        7 => (byte)~stored,
        8 => (byte)(stored + opValue),
        9 => (byte)(stored - opValue),
        10 => (byte)(stored & opValue),
        11 => (byte)(stored | opValue),
        12 => (byte)(stored ^ opValue),
        13 => (byte)~(stored & opValue),
        14 => (byte)~(stored | opValue),
        _ => (byte)~(stored ^ opValue),
    };

    /// <summary>Seeds the whole stencil plane with <paramref name="value"/> (replace with the operand, no depth test).</summary>
    private static void SeedStencil(DepthRig d, byte value)
    {
        var state = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Always);
        SetStencil(state, new StencilFace(0, 4, 0, OpValue: value));
        d.Draw(state, d.Full(0.5f, 0, 0, 0));
    }

    public static IEnumerable<object[]> StencilOpCases()
    {
        foreach (var back in new[] { false, true })
        {
            for (uint op = 0; op < 16; op++)
            {
                yield return [op, (byte)0xA6, (byte)0x3C, back];
            }

            yield return [5u, (byte)0xF0, (byte)0x3C, back];
            yield return [6u, (byte)0x10, (byte)0x3C, back];
            yield return [8u, (byte)0xF0, (byte)0x3C, back];
            yield return [9u, (byte)0x10, (byte)0x3C, back];
            yield return [5u, (byte)0xFF, (byte)1, back];
            yield return [6u, (byte)0x00, (byte)1, back];
            yield return [5u, (byte)0x07, (byte)1, back];
        }
    }

    [Theory]
    [MemberData(nameof(StencilOpCases))]
    public void StencilOperationsComputeTheHardwareResult(uint op, byte seed, byte opValue, bool backFace)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        SeedStencil(d, seed);
        var state = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Always,
            backFace: true, stencilFuncBack: CompareFunc.Always);
        var active = new StencilFace(op, op, op, 0x11, 0xFF, 0xFF, opValue);
        var inactive = new StencilFace(0, 0, 0, 0x11, 0xFF, 0xFF, opValue);
        SetStencil(state, backFace ? inactive : active, backFace ? active : inactive);
        var mesh = d.Full(0.5f, 0, 0, 0);
        d.Draw(state, backFace ? DepthRig.Reverse(mesh) : mesh);
        var stencil = d.QueueStencilReadback();
        if (HostRefusesStencilOp(op, opValue, 0xFF))
        {
            // Metal's stencil operations are keep/zero/replace/invert and steps of one; the rest
            // must be refused loudly rather than drawn wrong.
            Assert.Throws<SceneFatalException>(() => rig.Submit(d.Pm4));
            return;
        }

        rig.Submit(d.Pm4);
        Assert.Equal(ApplyStencilOp(op, seed, opValue, 0x11), stencil()[(32 * Size) + 32]);
    }

    // ONES, AND, OR, NAND, NOR, XNOR; XOR other than an invert of every written bit; add and
    // subtract by more than one.
    private static bool HostRefusesStencilOp(uint op, byte opValue, byte writeMask) => op switch
    {
        2 or 10 or 11 or 13 or 14 or 15 => true,
        12 => (writeMask & opValue) != 0 && (writeMask & ~opValue) != 0,
        5 or 6 or 8 or 9 => opValue > 1,
        _ => false,
    };

    // path 0 = stencil fail, 1 = depth pass, 2 = depth fail
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void StencilFailZPassAndZFailSelectTheirOperationPerFace(int path, bool backFace)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), d.Full(0.5f, 0, 0, 0));
        SeedStencil(d, 10);
        // Front: fail +1, zpass -1, zfail invert; back: fail -1, zpass invert, zfail +1.
        var front = new StencilFace(5, 6, 7, 0, 0xFF, 0xFF, 1);
        var back = new StencilFace(6, 7, 5, 0, 0xFF, 0xFF, 1);
        var stencilFunc = path == 0 ? CompareFunc.Never : CompareFunc.Always;
        var z = path == 2 ? 0.75f : 0.25f;
        var state = d.State().DepthControl(true, false, CompareFunc.Less, stencil: true, stencilFunc: stencilFunc,
            backFace: true, stencilFuncBack: stencilFunc);
        SetStencil(state, front, back);
        var mesh = d.Full(z, 0, 0, 0);
        d.Draw(state, backFace ? DepthRig.Reverse(mesh) : mesh);
        var stencil = d.QueueStencilReadback();
        rig.Submit(d.Pm4);
        var face = backFace ? back : front;
        var op = path switch { 0 => face.Fail, 1 => face.Pass, _ => face.ZFail };
        Assert.Equal(ApplyStencilOp(op, 10, 1, 0), stencil()[(32 * Size) + 32]);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    [InlineData(5u, false)]
    [InlineData(6u, false)]
    [InlineData(7u, false)]
    [InlineData(0u, true)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(5u, true)]
    [InlineData(6u, true)]
    [InlineData(7u, true)]
    public void StencilCompareFuncsUseTheReferenceAndTheCompareMask(uint func, bool backFace)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const byte seed = 5;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        SeedStencil(d, seed);
        // (reference, compare mask) per column: the comparison is (ref & mask) FUNC (stored & mask).
        (byte Ref, byte Mask)[] columns = [(4, 0xFF), (5, 0xFF), (6, 0xFF), (0x15, 0x0F)];
        for (var column = 0; column < columns.Length; column++)
        {
            // The pass operation inverts so the outcome is observable without a replace operand.
            var face = new StencilFace(0, 7, 0, columns[column].Ref, columns[column].Mask, 0xFF);
            var state = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: (CompareFunc)func,
                backFace: true, stencilFuncBack: (CompareFunc)func);
            SetStencil(state, backFace ? default : face, backFace ? face : default);
            var mesh = d.Rect(0.5f, 0, 0, 0, column * 16, 0, (column * 16) + 16, 64);
            d.Draw(state, backFace ? DepthRig.Reverse(mesh) : mesh);
        }

        var stencil = d.QueueStencilReadback();
        rig.Submit(d.Pm4);
        var values = stencil();
        for (var column = 0; column < columns.Length; column++)
        {
            var pass = Passes(func, columns[column].Ref & columns[column].Mask, seed & columns[column].Mask);
            Assert.Equal(pass ? (byte)(~seed & 0xFF) : seed, values[(32 * Size) + (column * 16) + 8]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StencilWriteMaskKeepsTheMaskedBits(bool backFace)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        SeedStencil(d, 0xA0);
        var face = new StencilFace(0, 4, 0, 0, 0xFF, 0x0F, 0x5C);
        var state = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Always,
            backFace: true, stencilFuncBack: CompareFunc.Always);
        SetStencil(state, backFace ? default : face, backFace ? face : default);
        var mesh = d.Full(0.5f, 0, 0, 0);
        d.Draw(state, backFace ? DepthRig.Reverse(mesh) : mesh);
        var stencil = d.QueueStencilReadback();
        rig.Submit(d.Pm4);
        Assert.Equal(0xAC, stencil()[(32 * Size) + 32]);
    }

    [Fact(Skip = "Refused loudly, not fixable on the host: Metal has one stencil reference per face for both the compare and " +
        "the replace, so STENCILOPVAL != STENCILTESTVAL under a compare is a fatal (DepthTargetResolver.ConvertFace).")]
    public void StencilReplaceWithAnOperandThatDiffersFromTheReferenceUnderACompare()
    {
        // Hardware keeps the compare reference (STENCILTESTVAL) and the replace operand (STENCILOPVAL) apart.
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        SeedStencil(d, 5);
        var state = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Equal);
        SetStencil(state, new StencilFace(0, 4, 0, 5, 0xFF, 0xFF, 100));
        d.Draw(state, d.Full(0.5f, 0, 0, 0));
        var stencil = d.QueueStencilReadback();
        rig.Submit(d.Pm4);
        Assert.Equal(100, stencil()[(32 * Size) + 32]);
    }

    [Fact]
    public void StencilClearWritesTheClearValue()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        SeedStencil(d, 0x11);
        var clear = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Always);
        SetStencil(clear, new StencilFace(0, 0, 0));
        clear.Context[Cx.DbRenderControl] = 2;
        clear.Context[Cx.DbStencilClear] = 0x37;
        clear.Context[Cx.CbTargetMask] = 0;
        d.Draw(clear, d.Full(0.5f, 0, 0, 0));
        var stencil = d.QueueStencilReadback();
        rig.Submit(d.Pm4);
        var values = stencil();
        Assert.Equal(0x37, values[(32 * Size) + 32]);
        Assert.Equal(0x37, values[(2 * Size) + 60]);
    }

    [Fact]
    public void IncrementThenTestPatternDrawsOnlyWhereTheStencilEquals()
    {
        // Two overlapping rectangles increment the stencil; a final draw with EQUAL 2 colours only the overlap.
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, Size, Size, zFormat: 3, stencil: true);
        SeedStencil(d, 0);
        var increment = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Always);
        SetStencil(increment, new StencilFace(0, 5, 0, OpValue: 1));
        d.Draw(increment, d.Rect(0.5f, 0, 0, 0, 0, 0, 40, 64));
        d.Draw(increment, d.Rect(0.5f, 0, 0, 0, 24, 0, 64, 64));
        var test = d.State().DepthControl(false, false, CompareFunc.Always, stencil: true, stencilFunc: CompareFunc.Equal);
        SetStencil(test, new StencilFace(0, 0, 0, 2, 0xFF, 0));
        d.Draw(test, d.Full(0.5f, 0, 1, 0));
        var colour = d.QueueColorReadback();
        rig.Submit(d.Pm4);
        var colours = colour();
        Assert.Equal(Black, colours[(32 * Size) + 8]);
        Assert.Equal(Green, colours[(32 * Size) + 32]);
        Assert.Equal(Black, colours[(32 * Size) + 56]);
    }

    // 8. Tiled depth layout -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(3u, 64u, 136u)]
    [InlineData(3u, 192u, 80u)]
    [InlineData(3u, 80u, 48u)]
    [InlineData(3u, 100u, 37u)]
    [InlineData(3u, 130u, 70u)]
    [InlineData(1u, 64u, 136u)]
    [InlineData(1u, 192u, 80u)]
    [InlineData(1u, 80u, 48u)]
    [InlineData(1u, 100u, 37u)]
    [InlineData(1u, 130u, 70u)]
    public void DepthWrittenByTheDepthBlockReadsBackAtTheSamePixelForNonTileSizes(uint zFormat, uint width, uint height)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var d = new DepthRig(rig, width, height, zFormat);
        const float z0 = 0.1f;
        const float sx = 0.5f;
        const float sy = 0.3f;
        d.Draw(d.State().DepthControl(true, true, CompareFunc.Always), DepthRig.Plane(z0, sx, sy));
        var depth = d.QueueDepthReadback();
        rig.Submit(d.Pm4);
        var depths = depth();
        var bad = 0;
        var first = "";
        for (var y = 0u; y < height; y++)
        {
            for (var x = 0u; x < width; x++)
            {
                var plane = z0 + (sx * (x + 0.5) / width) + (sy * (y + 0.5) / height);
                var tolerance = zFormat == 1 ? 1.5 / 65535 : 2e-5;
                var expected = zFormat == 1 ? Unorm16(plane) : (float)plane;
                if (Math.Abs(depths[(y * width) + x] - expected) > tolerance)
                {
                    bad++;
                    first = first.Length == 0 ? $"({x},{y}) expected {expected} got {depths[(y * width) + x]}" : first;
                }
            }
        }

        Assert.True(bad == 0, $"{bad} of {width * height} depth texels differ from the analytic plane; first {first}");
    }
}
