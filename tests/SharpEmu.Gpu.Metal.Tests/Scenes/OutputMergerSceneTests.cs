// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>The pixel and vertex programs of the output-merger scenes, assembled as the ScenePrograms are.</summary>
internal static class OmPrograms
{
    // Interpolated PARAM0 into v2..v5 (m0 = s0, no user SGPRs).
    private static readonly uint[] InterpolateParam0 =
    [
        0xBEFC0300,                // s_mov_b32 m0, s0
        0xC8080000,                // v_interp_p1_f32_e32 v2, v0, attr0.x
        0xC8090001,                // v_interp_p2_f32_e32 v2, v1, attr0.x
        0xC80C0100,                // v_interp_p1_f32_e32 v3, v0, attr0.y
        0xC80D0101,                // v_interp_p2_f32_e32 v3, v1, attr0.y
        0xC8100200,                // v_interp_p1_f32_e32 v4, v0, attr0.z
        0xC8110201,                // v_interp_p2_f32_e32 v4, v1, attr0.z
        0xC8140300,                // v_interp_p1_f32_e32 v5, v0, attr0.w
        0xC8150301,                // v_interp_p2_f32_e32 v5, v1, attr0.w
    ];

    private static uint[] Join(params uint[][] parts) => [.. parts.SelectMany(static part => part)];

    /// <summary>MRT0 = PARAM0 packed to two half2 VGPRs with v_cvt_pkrtz, exported COMPR (SPI_SHADER_FP16_ABGR).</summary>
    public static readonly uint[] Fp16Color = Join(InterpolateParam0,
    [
        0x5E0C0702,                // v_cvt_pkrtz_f16_f32_e32 v6, v2, v3
        0x5E0E0B04,                // v_cvt_pkrtz_f16_f32_e32 v7, v4, v5
        0xF8001C0F, 0x00000706,    // exp mrt0 v6, v7, off, off done compr vm   (EN=0xF patched by hand: llvm-mc emits EN=3 with src1=src0)
        0xBF810000,                // s_endpgm
    ]);

    /// <summary>
    /// mrt0 FP16_ABGR, mrt1 FP16_ABGR (same packed values), mrt2 UNORM16 (pknorm_u16 of r,g), mrt3 SNORM16
    /// (pknorm_i16 of r,g). The last two export one dword (EN=3).
    /// </summary>
    public static readonly uint[] MrtFloat = Join(InterpolateParam0,
    [
        0x5E0C0702,                // v_cvt_pkrtz_f16_f32_e32 v6, v2, v3
        0x5E0E0B04,                // v_cvt_pkrtz_f16_f32_e32 v7, v4, v5
        0xD7690008, 0x00020702,    // v_cvt_pknorm_u16_f32 v8, v2, v3
        0xD7680009, 0x00020702,    // v_cvt_pknorm_i16_f32 v9, v2, v3
        0xF800040F, 0x00000706,    // exp mrt0 v6, v7, off, off compr   (EN=0xF, src1=v7 patched by hand)
        0xF800041F, 0x00000706,    // exp mrt1 v6, v7, off, off compr
        0xF8000423, 0x00000008,    // exp mrt2 v8, off, off, off compr
        0xF8001C33, 0x00000009,    // exp mrt3 v9, off, off, off done compr vm
        0xBF810000,                // s_endpgm
    ]);

    /// <summary>
    /// mrt0 32_R (v_cvt_u32_f32 of r), mrt1 SINT16_ABGR (v_cvt_pk_i16_i32 of int(r), int(g)), mrt2 32_R (float b),
    /// mrt3 FP16_ABGR (r,g then b,a).
    /// </summary>
    public static readonly uint[] MrtInt = Join(InterpolateParam0,
    [
        0x7E0C0F02,                // v_cvt_u32_f32_e32 v6, v2
        0x7E0E1102,                // v_cvt_i32_f32_e32 v7, v2
        0x7E101103,                // v_cvt_i32_f32_e32 v8, v3
        0xD76B0009, 0x00021107,    // v_cvt_pk_i16_i32 v9, v7, v8
        0x5E140702,                // v_cvt_pkrtz_f16_f32_e32 v10, v2, v3
        0x5E160B04,                // v_cvt_pkrtz_f16_f32_e32 v11, v4, v5
        0xF8000001, 0x00000006,    // exp mrt0 v6, off, off, off
        0xF8000413, 0x00000009,    // exp mrt1 v9, off, off, off compr
        0xF8000021, 0x00000004,    // exp mrt2 v4, off, off, off
        0xF8001C3F, 0x00000B0A,    // exp mrt3 v10, v11, off, off done compr vm   (EN=0xF, src1=v11 patched by hand)
        0xBF810000,                // s_endpgm
    ]);

    /// <summary>MRT0 = (r, g) as two 32-bit channels (SPI_SHADER_32_GR).</summary>
    public static readonly uint[] Gr32 =
    [
        0xBEFC0300,                // s_mov_b32 m0, s0
        0xC8080000,                // v_interp_p1_f32_e32 v2, v0, attr0.x
        0xC8090001,                // v_interp_p2_f32_e32 v2, v1, attr0.x
        0xC80C0100,                // v_interp_p1_f32_e32 v3, v0, attr0.y
        0xC80D0101,                // v_interp_p2_f32_e32 v3, v1, attr0.y
        0xF8001803, 0x00000302,    // exp mrt0 v2, v3, off, off done vm
        0xBF810000,                // s_endpgm
    ];

    /// <summary>A fullscreen-triangle vertex program from the vertex id alone: (0,0), (2,0), (0,2) in clip space.</summary>
    public static readonly uint[] ProceduralTriangle =
    [
        0x34000A81,                // v_lshlrev_b32_e32 v0, 1, v5
        0x36000082,                // v_and_b32_e32 v0, 2, v0
        0x36020A82,                // v_and_b32_e32 v1, 2, v5
        0x7E000D00,                // v_cvt_f32_u32_e32 v0, v0
        0x7E020D01,                // v_cvt_f32_u32_e32 v1, v1
        0x7E0402F0,                // v_mov_b32_e32 v2, 0.5
        0x7E0602F2,                // v_mov_b32_e32 v3, 1.0
        0xF80008CF, 0x03020100,    // exp pos0 v0, v1, v2, v3 done
        0xBF810000,                // s_endpgm
    ];

    /// <summary>MRT0 = the four user SGPRs s0..s3.</summary>
    public static readonly uint[] ConstantColor =
    [
        0x7E000200,                // v_mov_b32_e32 v0, s0
        0x7E020201,                // v_mov_b32_e32 v1, s1
        0x7E040202,                // v_mov_b32_e32 v2, s2
        0x7E060203,                // v_mov_b32_e32 v3, s3
        0xF800180F, 0x03020100,    // exp mrt0 v0, v1, v2, v3 done vm
        0xBF810000,                // s_endpgm
    ];
}

/// <summary>
/// Output merger scenes: blending, per-MRT state, write masks, number types and the draw recognisers around them.
/// Every expected pixel is computed here from the hardware blend equations (CB_BLENDn_CONTROL, COMB_FCN) and
/// the format encodings, never from the emulator's output.
/// </summary>
public sealed class OutputMergerSceneTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint Size = 64;
    private const int CellSize = 4;
    private const int CellsPerRow = (int)Size / CellSize;

    // CB_COLOR_INFO FORMAT / NUMBER_TYPE.
    private const uint Fmt8 = 1, Fmt16_16 = 5, Fmt32 = 4, Fmt2_10_10_10 = 9, Fmt8888 = 10, Fmt32_32 = 11, Fmt16x4 = 12;
    private const uint NtUnorm = 0, NtSnorm = 1, NtUint = 4, NtSint = 5, NtSrgb = 6, NtFloat = 7;

    // SPI_SHADER_COL_FORMAT export formats.
    private const uint Exp32R = 1, Exp32GR = 2, ExpFp16 = 4, ExpUnorm16 = 5, ExpSnorm16 = 6, ExpSint16 = 8, Exp32Abgr = 9;

    // BLEND_FUNC factor codes and COMB_FCN codes.
    private const uint Zero = 0, One = 1, SrcColor = 2, InvSrcColor = 3, SrcAlpha = 4, InvSrcAlpha = 5, DstAlpha = 6, InvDstAlpha = 7,
        DstColor = 8, InvDstColor = 9, SrcAlphaSaturate = 10, ConstColor = 13, InvConstColor = 14, ConstAlpha = 19, InvConstAlpha = 20;
    private const uint Add = 0, SrcMinusDst = 1, Min = 2, Max = 3, DstMinusSrc = 4;

    private static readonly float[] Src = [0.8f, 0.4f, 0.2f, 0.6f];
    private static readonly byte[] DstBytes = [64, 128, 192, 230];
    private static readonly float[] Constant = [0.1f, 0.6f, 0.3f, 0.7f];

    // ---- blend oracle -------------------------------------------------------------------------------------

    private static uint Ctl(uint colorSrc, uint colorFunc, uint colorDst, uint alphaSrc = One, uint alphaFunc = Add, uint alphaDst = Zero,
        bool separateAlpha = false, bool enable = true) =>
        colorSrc | (colorFunc << 5) | (colorDst << 8) | (alphaSrc << 16) | (alphaFunc << 21) | (alphaDst << 24) |
        (separateAlpha ? 1u << 29 : 0u) | (enable ? 1u << 30 : 0u);

    private static float Factor(uint factor, float[] s, float[] d, float[] c, int k) => factor switch
    {
        Zero => 0f,
        One => 1f,
        SrcColor => s[k],
        InvSrcColor => 1f - s[k],
        SrcAlpha => s[3],
        InvSrcAlpha => 1f - s[3],
        DstAlpha => d[3],
        InvDstAlpha => 1f - d[3],
        DstColor => d[k],
        InvDstColor => 1f - d[k],
        SrcAlphaSaturate => k == 3 ? 1f : MathF.Min(s[3], 1f - d[3]),
        ConstColor => c[k],
        InvConstColor => 1f - c[k],
        ConstAlpha => c[3],
        InvConstAlpha => 1f - c[3],
        _ => throw new ArgumentOutOfRangeException(nameof(factor)),
    };

    /// <summary>
    /// The CB blend equation per channel. When SEPARATE_ALPHA_BLEND is clear the alpha channel uses the colour
    /// factors and function with its own channel in the "colour" factors (SRC_COLOR on alpha is src.a).
    /// MIN and MAX ignore the factors.
    /// </summary>
    private static float[] Blend(float[] s, float[] d, float[] c, uint ctl)
    {
        var separate = (ctl & (1u << 29)) != 0;
        var result = new float[4];
        for (var k = 0; k < 4; k++)
        {
            var alpha = k == 3 && separate;
            var sf = alpha ? (ctl >> 16) & 31 : ctl & 31;
            var fn = alpha ? (ctl >> 21) & 7 : (ctl >> 5) & 7;
            var df = alpha ? (ctl >> 24) & 31 : (ctl >> 8) & 31;
            var a = s[k] * Factor(sf, s, d, c, k);
            var b = d[k] * Factor(df, s, d, c, k);
            result[k] = fn switch
            {
                Add => a + b,
                SrcMinusDst => a - b,
                Min => MathF.Min(s[k], d[k]),
                Max => MathF.Max(s[k], d[k]),
                DstMinusSrc => b - a,
                _ => throw new ArgumentOutOfRangeException(nameof(ctl)),
            };
        }

        return result;
    }

    private static float Sat(float value) => Math.Clamp(value, 0f, 1f);

    private static int ToUnorm8(float value) => (int)MathF.Floor((Sat(value) * 255f) + 0.5f);

    private static float[] Normalized(byte[] bytes) => [bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f];

    private static float SrgbEncode(float linear) =>
        linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;

    private static float SrgbDecode(float encoded) =>
        encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    // ---- scene helpers ------------------------------------------------------------------------------------

    private static uint[] ConstantUserData(float r, float g, float b, float a) =>
        [SceneState.F(r), SceneState.F(g), SceneState.F(b), SceneState.F(a)];

    private static void SetBlendConstant(SceneState state, float[] c)
    {
        for (var k = 0u; k < 4; k++)
        {
            state.Context[Cx.CbBlendRed + k] = SceneState.F(c[k]);
        }
    }

    /// <summary>A target pre-filled with <paramref name="texel"/> repeated, rows at the linear pitch.</summary>
    private static ulong FilledTarget(SceneRig rig, uint width, uint height, byte[] texel)
    {
        var rows = new byte[width * height * texel.Length];
        for (var index = 0; index < width * height; index++)
        {
            texel.CopyTo(rows, index * texel.Length);
        }

        return rig.LinearImage(width, height, (uint)texel.Length, rows);
    }

    /// <summary>One draw of a full-viewport or cell-sized quad of constant colour <paramref name="color"/>.</summary>
    private static void DrawQuad(SceneRig rig, ScenePm4 pm4, SceneState state, uint[] program, float[] color, int cell = -1)
    {
        var vertices = cell < 0
            ? SceneDraws.Quad(0.5f, color[0], color[1], color[2], color[3])
            : CellQuad(cell, color);
        rig.DrawMesh(pm4, state, vertices, program);
    }

    private static SceneVertex[] CellQuad(int cell, float[] color)
    {
        var cx = cell % CellsPerRow;
        var cy = cell / CellsPerRow;
        var step = 2f * CellSize / Size;
        var left = -1f + (cx * step);
        var top = 1f - (cy * step);
        return SceneDraws.Quad(0.5f, color[0], color[1], color[2], color[3], left, top - step, left + step, top);
    }

    /// <summary>Compares every pixel of the 4x4 cell with the expected RGBA8 texel within <paramref name="tolerance"/> LSBs.</summary>
    private static void CheckCell8888(List<string> errors, byte[] pixels, int cell, float[] expected, int tolerance, string label, bool srgb = false)
    {
        var cx = cell % CellsPerRow * CellSize;
        var cy = cell / CellsPerRow * CellSize;
        for (var y = 0; y < CellSize; y++)
        {
            for (var x = 0; x < CellSize; x++)
            {
                var offset = (((cy + y) * (int)Size) + cx + x) * 4;
                for (var k = 0; k < 4; k++)
                {
                    var want = ToUnorm8(srgb && k < 3 ? SrgbEncode(Sat(expected[k])) : expected[k]);
                    var got = pixels[offset + k];
                    if (Math.Abs(got - want) > tolerance)
                    {
                        errors.Add($"{label}: channel {"RGBA"[k]} got {got} want {want} (expected float {expected[k]:F4})");
                        return;
                    }
                }
            }
        }
    }

    private static void AssertClean(List<string> errors)
    {
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Draws one quad per control word on an RGBA8 UNORM target holding <see cref="DstBytes"/> everywhere and compares each
    /// cell with the hardware equation. Tolerance 1 LSB: the CB blends in reduced-precision fixed point, the host in float.
    /// </summary>
    private List<string> RunUnormCells(IReadOnlyList<uint> controls, float[] src, bool useConstant = true)
    {
        var errors = new List<string>();
        using var rig = CreateRig();
        if (rig is null)
        {
            return errors;
        }

        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendClamp: true);
        SetBlendConstant(state, Constant);
        var pm4 = new ScenePm4();
        for (var cell = 0; cell < controls.Count; cell++)
        {
            state.Context[Cx.CbBlend0Control] = controls[cell];
            DrawQuad(rig, pm4, state, ScenePrograms.InterpolatedColor, src, cell);
        }

        rig.Submit(pm4);
        var pixels = rig.ReadImage(target, Size, Size);
        for (var cell = 0; cell < controls.Count; cell++)
        {
            var expected = Blend(src, Normalized(DstBytes), Constant, controls[cell]);
            CheckCell8888(errors, pixels, cell, expected, 1, $"ctl=0x{controls[cell]:X8}");
        }

        return errors;
    }

    // ---- 1. blending on RGBA8 UNORM -----------------------------------------------------------------------

    public static IEnumerable<object[]> Factors() =>
        new uint[] { Zero, One, SrcColor, InvSrcColor, SrcAlpha, InvSrcAlpha, DstAlpha, InvDstAlpha, DstColor, InvDstColor,
            SrcAlphaSaturate, ConstColor, InvConstColor, ConstAlpha, InvConstAlpha }.Select(static factor => new object[] { factor });

    private static readonly uint[] AllFactors =
        [Zero, One, SrcColor, InvSrcColor, SrcAlpha, InvSrcAlpha, DstAlpha, InvDstAlpha, DstColor, InvDstColor,
            SrcAlphaSaturate, ConstColor, InvConstColor, ConstAlpha, InvConstAlpha];

    [Theory]
    [MemberData(nameof(Factors))]
    public void EverySourceFactorWithEveryDestinationFactorAddsOnAnRgba8Target(uint sourceFactor)
    {
        // colour and alpha share the factors (SEPARATE_ALPHA_BLEND clear)
        var controls = AllFactors.Select(destination => Ctl(sourceFactor, Add, destination)).ToList();
        AssertClean(RunUnormCells(controls, Src));
    }

    [Theory]
    [InlineData(Add)]
    [InlineData(SrcMinusDst)]
    [InlineData(DstMinusSrc)]
    [InlineData(Min)]
    [InlineData(Max)]
    public void EveryCombineFunctionMatchesTheEquationOnAnRgba8Target(uint function)
    {
        (uint Source, uint Destination)[] pairs =
        [
            (One, One), (SrcAlpha, InvSrcAlpha), (One, Zero), (DstColor, Zero), (ConstColor, InvConstAlpha), (Zero, One),
            (SrcColor, DstColor), (InvDstAlpha, DstAlpha),
        ];
        AssertClean(RunUnormCells(pairs.Select(pair => Ctl(pair.Source, function, pair.Destination)).ToList(), Src));
    }

    [Fact]
    public void SeparateAlphaBlendUsesTheAlphaFactorsAndFunctionForAlphaOnly()
    {
        var controls = new List<uint>();
        foreach (var function in new[] { Add, SrcMinusDst, DstMinusSrc, Min, Max })
        {
            controls.Add(Ctl(SrcAlpha, Add, InvSrcAlpha, One, function, One, separateAlpha: true));
            controls.Add(Ctl(One, Add, One, SrcAlpha, function, InvSrcAlpha, separateAlpha: true));
            controls.Add(Ctl(DstColor, SrcMinusDst, Zero, ConstAlpha, function, InvConstAlpha, separateAlpha: true));
        }

        // SEPARATE_ALPHA_BLEND clear: the alpha-factor fields are ignored and alpha follows the colour equation.
        controls.Add(Ctl(SrcAlpha, Add, InvSrcAlpha, Zero, Max, Zero));
        controls.Add(Ctl(One, DstMinusSrc, SrcColor, Zero, Max, Zero));
        AssertClean(RunUnormCells(controls, Src));
    }

    [Fact]
    public void ABlendDisabledTargetIsOverwrittenWhateverTheFactorFields()
    {
        var controls = new List<uint> { Ctl(One, Add, One, enable: false), Ctl(SrcAlpha, Add, InvSrcAlpha, enable: false) };
        var errors = new List<string>();
        using var rig = CreateRig();
        if (rig is null) return;
        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendClamp: true);
        var pm4 = new ScenePm4();
        for (var cell = 0; cell < controls.Count; cell++)
        {
            state.Context[Cx.CbBlend0Control] = controls[cell];
            DrawQuad(rig, pm4, state, ScenePrograms.InterpolatedColor, Src, cell);
        }

        rig.Submit(pm4);
        var pixels = rig.ReadImage(target, Size, Size);
        for (var cell = 0; cell < controls.Count; cell++)
        {
            CheckCell8888(errors, pixels, cell, Src, 1, $"disabled ctl=0x{controls[cell]:X8}");
        }

        AssertClean(errors);
    }

    [Fact]
    public void BlendBypassOverwritesDespiteTheEnableBit()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendBypass: true);
        state.Context[Cx.CbBlend0Control] = Ctl(One, Add, One);
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, Src[0], Src[1], Src[2], Src[3])));
        var errors = new List<string>();
        var pixels = rig.ReadImage(target, Size, Size);
        for (var cell = 0; cell < CellsPerRow * CellsPerRow; cell += 37)
        {
            CheckCell8888(errors, pixels, cell, Src, 1, $"bypass cell {cell}");
        }

        AssertClean(errors);
    }

    // ---- 1b. 16-bit float accumulation --------------------------------------------------------------------

    private static ulong HalfTarget(SceneRig rig, float r, float g, float b, float a)
    {
        var texel = new byte[8];
        BitConverter.TryWriteBytes(texel.AsSpan(0), (Half)r);
        BitConverter.TryWriteBytes(texel.AsSpan(2), (Half)g);
        BitConverter.TryWriteBytes(texel.AsSpan(4), (Half)b);
        BitConverter.TryWriteBytes(texel.AsSpan(6), (Half)a);
        return FilledTarget(rig, Size, Size, texel);
    }

    private static void CheckHalfTexels(List<string> errors, SceneRig rig, ulong target, float[] expected, string label, float relative = 0f)
    {
        var halves = rig.ReadImage<Half>(target, Size * 4, Size);
        foreach (var (x, y) in new[] { (0, 0), (31, 31), (63, 63), (5, 40) })
        {
            for (var k = 0; k < 4; k++)
            {
                var got = (float)halves[((y * (int)Size) + x) * 4 + k];
                if (MathF.Abs(got - expected[k]) > MathF.Max(1e-6f, MathF.Abs(expected[k]) * relative))
                {
                    errors.Add($"{label}: pixel ({x},{y}) channel {"RGBA"[k]} got {got} want {expected[k]}");
                    return;
                }
            }
        }
    }

    [Theory]
    [InlineData(ExpFp16, true)]    // pixel program packs with v_cvt_pkrtz and exports COMPR as real compilers do
    [InlineData(Exp32Abgr, false)] // four full-precision channels converted by the CB
    public void ThreeOneOneDrawsAccumulateAboveOneInA16BitFloatTarget(uint exportFormat, bool packed)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        float[] start = [0.25f, 0.5f, 0.75f, 0.5f];
        float[] light = [0.625f, 0.25f, 1.5f, 0.5f];
        var target = HalfTarget(rig, start[0], start[1], start[2], start[3]);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt16x4, NtFloat);
        state.Context[Cx.SpiShaderColFormat] = exportFormat;
        state.Context[Cx.CbBlend0Control] = Ctl(One, Add, One, One, Add, One, separateAlpha: true);
        var pm4 = new ScenePm4();
        for (var pass = 0; pass < 3; pass++)
        {
            DrawQuad(rig, pm4, state, packed ? OmPrograms.Fp16Color : ScenePrograms.InterpolatedColor, light);
        }

        rig.Submit(pm4);
        var errors = new List<string>();
        CheckHalfTexels(errors, rig, target, [start[0] + (3 * light[0]), start[1] + (3 * light[1]), start[2] + (3 * light[2]), start[3] + (3 * light[3])],
            "3x ONE+ONE");
        AssertClean(errors);
    }

    [Fact]
    public void SrcAlphaOneLightAccumulationKeepsValuesAboveOneAndIsNotClamped()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        float[] start = [1.5f, 0.25f, 3f, 1f];
        float[] light = [2f, 1f, 4f, 0.5f];
        var target = HalfTarget(rig, start[0], start[1], start[2], start[3]);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt16x4, NtFloat);
        state.Context[Cx.SpiShaderColFormat] = ExpFp16;
        var control = Ctl(SrcAlpha, Add, One, Zero, Add, One, separateAlpha: true);
        state.Context[Cx.CbBlend0Control] = control;
        var pm4 = new ScenePm4();
        DrawQuad(rig, pm4, state, OmPrograms.Fp16Color, light);
        DrawQuad(rig, pm4, state, OmPrograms.Fp16Color, light);
        rig.Submit(pm4);
        var once = Blend(light, start, Constant, control);
        var twice = Blend(light, once, Constant, control);
        var errors = new List<string>();
        CheckHalfTexels(errors, rig, target, twice, "2x SRC_ALPHA+ONE");
        AssertClean(errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlendClampLimitsAFloatTargetToZeroOne(bool clamp)
    {
        // BLEND_CLAMP set: the CB clamps the exported colour to [0,1] before it is written; clear: the float survives.
        using var rig = CreateRig();
        if (rig is null) return;
        float[] colour = [2.5f, -1f, 0.5f, 1f];
        var target = HalfTarget(rig, 0f, 0f, 0f, 0f);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt16x4, NtFloat, blendClamp: clamp);
        state.Context[Cx.SpiShaderColFormat] = ExpFp16;
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, colour[0], colour[1], colour[2], colour[3]), OmPrograms.Fp16Color));
        var errors = new List<string>();
        CheckHalfTexels(errors, rig, target, clamp ? [1f, 0f, 0.5f, 1f] : colour, $"clamp={clamp}");
        AssertClean(errors);
    }

    // ---- 2. per-MRT formats, export formats and blend state ----------------------------------------------

    [Fact]
    public void FourMrtsWithDifferentFormatsExportsAndBlendStatesAllLand()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        float[] colour = [0.25f, -0.5f, 0.75f, 0.5f];
        var rgba8 = FilledTarget(rig, Size, Size, DstBytes);
        var half = HalfTarget(rig, 2f, 3f, 4f, 1f);
        var unorm16 = FilledTarget(rig, Size, Size, [0xFF, 0xFF, 0xFF, 0xFF]);
        var state = new SceneState(Size, Size)
            .ColorTarget(0, rgba8, Size, Size, Fmt8888, NtUnorm, blendClamp: true)
            .ColorTarget(1, half, Size, Size, Fmt16x4, NtFloat)
            .ColorTarget(2, unorm16, Size, Size, Fmt16_16, NtUnorm, blendClamp: true);
        // mrt3 (SNORM16 export) has no bound target here: its nibbles are empty (16_16 SNORM targets are in the format sweep).
        state.Context[Cx.SpiShaderColFormat] = ExpFp16 | (ExpFp16 << 4) | (ExpUnorm16 << 8);
        state.Context[Cx.CbShaderMask] = 0xF | (0xFu << 4) | (0x3u << 8);
        state.Context[Cx.CbTargetMask] = 0xF | (0xFu << 4) | (0x3u << 8);
        state.Context[Cx.CbBlend0Control] = Ctl(One, Add, One, One, Add, One, separateAlpha: true);
        var halfControl = Ctl(SrcAlpha, Add, InvSrcAlpha, SrcAlpha, Add, InvSrcAlpha, separateAlpha: true);
        state.Context[Cx.CbBlend0Control + 1] = halfControl;
        state.Context[Cx.CbBlend0Control + 2] = 0;
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, colour[0], colour[1], colour[2], colour[3]), OmPrograms.MrtFloat));

        var errors = new List<string>();
        // mrt0: the exported colour is clamped to [0,1] by BLEND_CLAMP, then ONE+ONE onto the 8-bit target.
        var clamped = colour.Select(Sat).ToArray();
        var want0 = Blend(clamped, Normalized(DstBytes), Constant, Ctl(One, Add, One));
        var pixels = rig.ReadImage(rgba8, Size, Size);
        for (var cell = 0; cell < CellsPerRow * CellsPerRow; cell += 53)
        {
            CheckCell8888(errors, pixels, cell, want0, 1, "mrt0 RGBA8 UNORM ONE+ONE fp16 export");
        }

        // mrt1: unclamped float blend src_alpha / inv_src_alpha.
        CheckHalfTexels(errors, rig, half, Blend(colour, [2f, 3f, 4f, 1f], Constant, halfControl), "mrt1 RGBA16F SRC_ALPHA blend");

        // mrt2: no blending, the export packs r and g.
        var u16 = rig.ReadImage<ushort>(unorm16, Size * 2, Size);
        var wantR = (int)MathF.Round(Sat(colour[0]) * 65535f);
        var wantG = (int)MathF.Round(Sat(colour[1]) * 65535f);
        foreach (var index in new[] { 0, 31 * (int)Size + 31, 63 * (int)Size + 63 })
        {
            if (Math.Abs(u16[index * 2] - wantR) > 1 || Math.Abs(u16[(index * 2) + 1] - wantG) > 1)
            {
                errors.Add($"mrt2 UNORM16: got ({u16[index * 2]},{u16[(index * 2) + 1]}) want ({wantR},{wantG})");
                break;
            }
        }

        AssertClean(errors);
    }

    [Fact]
    public void IntegerAndPackedTargetsReceiveTheirNativeEncodingsFromOneProgram()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        float[] colour = [7f, -3f, 0.25f, 0.75f];
        var uint32 = FilledTarget(rig, Size, Size, [0xAA, 0xAA, 0xAA, 0xAA]);
        var sint16 = FilledTarget(rig, Size, Size, [0xAA, 0xAA, 0xAA, 0xAA]);
        var unorm8 = FilledTarget(rig, Size, Size, [0xAA]);
        var packed = FilledTarget(rig, Size, Size, [0xAA, 0xAA, 0xAA, 0xAA]);
        var state = new SceneState(Size, Size)
            .ColorTarget(0, uint32, Size, Size, Fmt32, NtUint)
            .ColorTarget(1, sint16, Size, Size, Fmt16_16, NtSint)
            .ColorTarget(2, unorm8, Size, Size, Fmt8, NtUnorm, blendClamp: true)
            .ColorTarget(3, packed, Size, Size, Fmt2_10_10_10, NtUnorm, blendClamp: true);
        state.Context[Cx.SpiShaderColFormat] = Exp32R | (ExpSint16 << 4) | (Exp32R << 8) | (ExpFp16 << 12);
        state.Context[Cx.CbShaderMask] = 0x1 | (0x3u << 4) | (0x1u << 8) | (0xFu << 12);
        state.Context[Cx.CbTargetMask] = 0x1 | (0x3u << 4) | (0x1u << 8) | (0xFu << 12);
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, colour[0], colour[1], colour[2], colour[3]), OmPrograms.MrtInt));

        var errors = new List<string>();
        var u32 = rig.ReadImage<uint>(uint32, Size, Size);
        if (u32[0] != 7u || u32[(31 * Size) + 31] != 7u) errors.Add($"mrt0 R32_UINT: got {u32[0]} want 7");
        var s16 = rig.ReadImage<short>(sint16, Size * 2, Size);
        if (s16[0] != 7 || s16[1] != -3) errors.Add($"mrt1 R16G16_SINT: got ({s16[0]},{s16[1]}) want (7,-3)");
        var b8 = rig.ReadImage(unorm8, Size, Size, 1);
        if (Math.Abs(b8[0] - 64) > 1) errors.Add($"mrt2 R8_UNORM: got {b8[0]} want 64");
        var p = rig.ReadImage<uint>(packed, Size, Size)[0];
        // R in the low ten bits, then G, B and A in the top two (the DXGI R10G10B10A2 order of COLOR_2_10_10_10 with SWAP_STD).
        var r10 = p & 0x3FF;
        var g10 = (p >> 10) & 0x3FF;
        var b10 = (p >> 20) & 0x3FF;
        var a2 = p >> 30;
        if (r10 != 1023 || g10 != 0 || Math.Abs((int)b10 - 256) > 1 || a2 != 2)
        {
            errors.Add($"mrt3 2_10_10_10: got r={r10} g={g10} b={b10} a={a2} want r=1023 g=0 b=256 a=2 (word 0x{p:X8})");
        }

        AssertClean(errors);
    }

    [Fact]
    public void ATwoChannel32BitExportFillsA32BitTwoChannelTarget()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var target = FilledTarget(rig, Size, Size, [0, 0, 0, 0, 0, 0, 0, 0]);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt32_32, NtFloat);
        state.Context[Cx.SpiShaderColFormat] = Exp32GR;
        state.Context[Cx.CbShaderMask] = 0x3;
        state.Context[Cx.CbTargetMask] = 0x3;
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, 0.25f, 3.5f, 0f, 0f), OmPrograms.Gr32));
        var values = rig.ReadImage<float>(target, Size * 2, Size);
        Assert.Equal(0.25f, values[0]);
        Assert.Equal(3.5f, values[1]);
        Assert.Equal(0.25f, values[((63 * (int)Size) + 63) * 2]);
        Assert.Equal(3.5f, values[(((63 * (int)Size) + 63) * 2) + 1]);
    }

    // ---- 3. write masks ------------------------------------------------------------------------------------

    private List<string> RunMaskCells(Func<int, (uint TargetMask, uint ShaderMask)> masks, uint blendControl)
    {
        var errors = new List<string>();
        using var rig = CreateRig();
        if (rig is null)
        {
            return errors;
        }

        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendClamp: true);
        state.Context[Cx.CbBlend0Control] = blendControl;
        var pm4 = new ScenePm4();
        for (var cell = 0; cell < 16; cell++)
        {
            var (targetMask, shaderMask) = masks(cell);
            state.Context[Cx.CbTargetMask] = targetMask;
            state.Context[Cx.CbShaderMask] = shaderMask;
            DrawQuad(rig, pm4, state, ScenePrograms.InterpolatedColor, Src, cell);
        }

        rig.Submit(pm4);
        var pixels = rig.ReadImage(target, Size, Size);
        var dst = Normalized(DstBytes);
        for (var cell = 0; cell < 16; cell++)
        {
            var (targetMask, shaderMask) = masks(cell);
            var written = targetMask & shaderMask & 0xF;
            var blended = (blendControl & (1u << 30)) != 0 ? Blend(Src, dst, Constant, blendControl) : Src;
            var expected = new float[4];
            for (var k = 0; k < 4; k++)
            {
                expected[k] = (written & (1u << k)) != 0 ? blended[k] : dst[k];
            }

            CheckCell8888(errors, pixels, cell, expected, 1, $"target=0x{targetMask:X} shader=0x{shaderMask:X}");
        }

        return errors;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryTargetMaskKeepsTheMaskedChannelsOldValue(bool blending) =>
        AssertClean(RunMaskCells(cell => ((uint)cell, 0xF), blending ? Ctl(SrcAlpha, Add, InvSrcAlpha) : 0));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryShaderMaskKeepsTheChannelsTheProgramDidNotExport(bool blending) =>
        AssertClean(RunMaskCells(cell => (0xF, (uint)cell), blending ? Ctl(SrcAlpha, Add, InvSrcAlpha) : 0));

    [Fact]
    public void PerMrtTargetMaskNibblesAreIndependentAndUnboundExportsAreIgnored()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        float[] colour = [0.625f, 0.25f, 0.75f, 0.5f];
        var rgba8 = FilledTarget(rig, Size, Size, DstBytes);
        var half = HalfTarget(rig, 2f, 3f, 4f, 1f);
        var state = new SceneState(Size, Size)
            .ColorTarget(0, rgba8, Size, Size, Fmt8888, NtUnorm, blendClamp: true)
            .ColorTarget(1, half, Size, Size, Fmt16x4, NtFloat);
        // mrt2 and mrt3 are exported by the program but no target is bound (format ZERO, empty nibbles).
        state.Context[Cx.SpiShaderColFormat] = ExpFp16 | (ExpFp16 << 4);
        state.Context[Cx.CbShaderMask] = 0xFF;
        state.Context[Cx.CbTargetMask] = 0x5 | (0xAu << 4);
        var halfControl = Ctl(One, Add, One, One, Add, One, separateAlpha: true);
        state.Context[Cx.CbBlend0Control] = Ctl(SrcAlpha, Add, InvSrcAlpha);
        state.Context[Cx.CbBlend0Control + 1] = halfControl;
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, colour[0], colour[1], colour[2], colour[3]), OmPrograms.MrtFloat));

        var dst = Normalized(DstBytes);
        var blend0 = Blend(colour, dst, Constant, Ctl(SrcAlpha, Add, InvSrcAlpha));
        var want0 = new[] { blend0[0], dst[1], blend0[2], dst[3] };
        var errors = new List<string>();
        var pixels = rig.ReadImage(rgba8, Size, Size);
        for (var cell = 0; cell < CellsPerRow * CellsPerRow; cell += 41)
        {
            CheckCell8888(errors, pixels, cell, want0, 1, "mrt0 mask 0x5");
        }

        var blend1 = Blend(colour, [2f, 3f, 4f, 1f], Constant, halfControl);
        CheckHalfTexels(errors, rig, half, [2f, blend1[1], 4f, blend1[3]], "mrt1 mask 0xA");
        AssertClean(errors);
    }

    // ---- 4. number types -----------------------------------------------------------------------------------

    [Fact]
    public void SrgbTargetsEncodeOnWriteAndBlendInLinearSpace()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        byte[] stored = [100, 150, 200, 230];
        var target = FilledTarget(rig, Size, Size, stored);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtSrgb, blendClamp: true);
        float[] colour = [0.2f, 0.5f, 0.8f, 0.5f];
        var pm4 = new ScenePm4();
        // cell 0: overwrite; cell 1: SRC_ALPHA / INV_SRC_ALPHA blend of the linear values against the decoded destination.
        var controls = new[] { 0u, Ctl(SrcAlpha, Add, InvSrcAlpha) };
        for (var cell = 0; cell < controls.Length; cell++)
        {
            state.Context[Cx.CbBlend0Control] = controls[cell];
            DrawQuad(rig, pm4, state, ScenePrograms.InterpolatedColor, colour, cell);
        }

        rig.Submit(pm4);
        var pixels = rig.ReadImage(target, Size, Size);
        var linearDst = new[] { SrgbDecode(stored[0] / 255f), SrgbDecode(stored[1] / 255f), SrgbDecode(stored[2] / 255f), stored[3] / 255f };
        var errors = new List<string>();
        // 1 LSB: the CB's linear-to-sRGB conversion is a piecewise-linear approximation of the transfer curve.
        CheckCell8888(errors, pixels, 0, colour, 1, "sRGB overwrite", srgb: true);
        CheckCell8888(errors, pixels, 1, Blend(colour, linearDst, Constant, controls[1]), 1, "sRGB blend in linear space", srgb: true);
        AssertClean(errors);
    }

    // ---- 5. recognisers around the output merger -----------------------------------------------------------

    private byte[]? DrawProceduralFill(float[] colour, uint control, out bool ran)
    {
        using var rig = CreateRig();
        ran = rig is not null;
        if (rig is null)
        {
            return null;
        }

        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendClamp: true);
        // The vertex program emits (0,0), (2,0), (0,2); this viewport maps them over the whole target.
        state.Context[Cx.PaClVportXScale] = SceneState.F(Size);
        state.Context[Cx.PaClVportXOffset] = SceneState.F(0f);
        state.Context[Cx.PaClVportYScale] = SceneState.F(Size);
        state.Context[Cx.PaClVportYOffset] = SceneState.F(0f);
        state.Context[Cx.CbBlend0Control] = control;
        state.VertexShader(rig.Program(OmPrograms.ProceduralTriangle));
        state.PixelShader(rig.Program(OmPrograms.ConstantColor), ConstantUserData(colour[0], colour[1], colour[2], colour[3]));
        var pm4 = new ScenePm4();
        state.Emit(pm4);
        pm4.NumInstances(1);
        pm4.DrawAuto(3);
        rig.Submit(pm4);
        return rig.ReadImage(target, Size, Size);
    }

    [Fact]
    public void AProceduralFullscreenPassWithAConstantPixelProgramStillBlends()
    {
        // The shape of a UE4 fullscreen tint: vertex-id triangle, constant-colour pixel program, SRC_ALPHA blending.
        float[] colour = [0.25f, 0.5f, 0.75f, 0.5f];
        var control = Ctl(SrcAlpha, Add, InvSrcAlpha);
        var pixels = DrawProceduralFill(colour, control, out var ran);
        if (!ran) return;
        var errors = new List<string>();
        CheckCell8888(errors, pixels!, 0, Blend(colour, Normalized(DstBytes), Constant, control), 1, "fullscreen tint");
        CheckCell8888(errors, pixels!, 255, Blend(colour, Normalized(DstBytes), Constant, control), 1, "fullscreen tint last cell");
        AssertClean(errors);
    }

    [Fact]
    public void AProceduralFullscreenBlackPassIsBlackNotWhite()
    {
        float[] colour = [0f, 0f, 0f, 1f];
        var pixels = DrawProceduralFill(colour, 0, out var ran);
        if (!ran) return;
        var errors = new List<string>();
        CheckCell8888(errors, pixels!, 0, colour, 1, "opaque black fill");
        CheckCell8888(errors, pixels!, 255, colour, 1, "opaque black fill last cell");
        AssertClean(errors);
    }

    [Fact]
    public void AProceduralFullscreenColourUsesAllFourUserRegisters()
    {
        float[] colour = [0.25f, 0.5f, 0.75f, 0.5f];
        var pixels = DrawProceduralFill(colour, 0, out var ran);
        if (!ran) return;
        var errors = new List<string>();
        CheckCell8888(errors, pixels!, 100, colour, 1, "constant colour fill");
        AssertClean(errors);
    }

    [Fact]
    public void ATransparentPremultipliedFullscreenPassLeavesTheTargetUntouched()
    {
        // (0,0,0,0) through ONE / INV_SRC_ALPHA is dst * 1 + 0: the hardware keeps the destination.
        float[] colour = [0f, 0f, 0f, 0f];
        var pixels = DrawProceduralFill(colour, Ctl(One, Add, InvSrcAlpha), out var ran);
        if (!ran) return;
        var errors = new List<string>();
        CheckCell8888(errors, pixels!, 77, Normalized(DstBytes), 1, "premultiplied transparent fill, procedural vertices");
        AssertClean(errors);
    }

    [Fact]
    public void ATransparentPremultipliedMeshQuadLeavesTheTargetUntouched()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendClamp: true);
        state.Context[Cx.CbBlend0Control] = Ctl(One, Add, InvSrcAlpha);
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, 0, 0, 0, 0), OmPrograms.ConstantColor, ConstantUserData(0f, 0f, 0f, 0f)));
        var errors = new List<string>();
        CheckCell8888(errors, rig.ReadImage(target, Size, Size), 77, Normalized(DstBytes), 1, "premultiplied transparent fill, fetched vertices");
        AssertClean(errors);
    }

    [Fact]
    public void ATranslucentPremultipliedMeshQuadStillBlends()
    {
        // alpha 0.5 with premultiplied colour (0.25, 0.25, 0.25, 0.5): not a transparent fill, so it must add to the attenuated destination.
        using var rig = CreateRig();
        if (rig is null) return;
        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendClamp: true);
        var control = Ctl(One, Add, InvSrcAlpha);
        state.Context[Cx.CbBlend0Control] = control;
        float[] colour = [0.25f, 0.25f, 0.25f, 0.5f];
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, 0, 0, 0, 0), OmPrograms.ConstantColor,
            ConstantUserData(colour[0], colour[1], colour[2], colour[3])));
        var errors = new List<string>();
        CheckCell8888(errors, rig.ReadImage(target, Size, Size), 77, Blend(colour, Normalized(DstBytes), Constant, control), 1, "premultiplied translucent fill");
        AssertClean(errors);
    }

    // ---- 4b. every common target format: two ONE+ONE draws onto zero -------------------------------------

    private sealed record FormatCase(uint Format, uint NumberType, int Bytes, uint ChannelMask, float Tolerance, Func<byte[], float[]> Decode);

    private static float DecodeFloat11(uint v) => ((v >> 6) & 31) == 0 ? (v & 63) / 64f * MathF.Pow(2f, -14f) : (1f + ((v & 63) / 64f)) * MathF.Pow(2f, (int)((v >> 6) & 31) - 15);

    private static float DecodeFloat10(uint v) => ((v >> 5) & 31) == 0 ? (v & 31) / 32f * MathF.Pow(2f, -14f) : (1f + ((v & 31) / 32f)) * MathF.Pow(2f, (int)((v >> 5) & 31) - 15);

    private static readonly Dictionary<string, FormatCase> FormatCases = new()
    {
        ["R32_FLOAT"] = new(Fmt32, NtFloat, 4, 0x1, 0f, b => [BitConverter.ToSingle(b, 0), 0, 0, 0]),
        ["R32G32_FLOAT"] = new(Fmt32_32, NtFloat, 8, 0x3, 0f, b => [BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), 0, 0]),
        ["R32G32B32A32_FLOAT"] = new(14, NtFloat, 16, 0xF, 0f, b => [BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8), BitConverter.ToSingle(b, 12)]),
        ["R16_FLOAT"] = new(2, NtFloat, 2, 0x1, 0f, b => [(float)BitConverter.ToHalf(b, 0), 0, 0, 0]),
        ["R16G16_FLOAT"] = new(Fmt16_16, NtFloat, 4, 0x3, 0f, b => [(float)BitConverter.ToHalf(b, 0), (float)BitConverter.ToHalf(b, 2), 0, 0]),
        ["R16G16B16A16_UNORM"] = new(Fmt16x4, NtUnorm, 8, 0xF, 2f / 65535f, b => [BitConverter.ToUInt16(b, 0) / 65535f, BitConverter.ToUInt16(b, 2) / 65535f, BitConverter.ToUInt16(b, 4) / 65535f, BitConverter.ToUInt16(b, 6) / 65535f]),
        ["R16G16B16A16_SNORM"] = new(Fmt16x4, NtSnorm, 8, 0xF, 2f / 32767f, b => [BitConverter.ToInt16(b, 0) / 32767f, BitConverter.ToInt16(b, 2) / 32767f, BitConverter.ToInt16(b, 4) / 32767f, BitConverter.ToInt16(b, 6) / 32767f]),
        ["R16G16_UNORM"] = new(Fmt16_16, NtUnorm, 4, 0x3, 2f / 65535f, b => [BitConverter.ToUInt16(b, 0) / 65535f, BitConverter.ToUInt16(b, 2) / 65535f, 0, 0]),
        ["R16G16_SNORM"] = new(Fmt16_16, NtSnorm, 4, 0x3, 2f / 32767f, b => [BitConverter.ToInt16(b, 0) / 32767f, BitConverter.ToInt16(b, 2) / 32767f, 0, 0]),
        ["R16_UNORM"] = new(2, NtUnorm, 2, 0x1, 2f / 65535f, b => [BitConverter.ToUInt16(b, 0) / 65535f, 0, 0, 0]),
        ["R8G8B8A8_SNORM"] = new(Fmt8888, NtSnorm, 4, 0xF, 2f / 127f, b => [(sbyte)b[0] / 127f, (sbyte)b[1] / 127f, (sbyte)b[2] / 127f, (sbyte)b[3] / 127f]),
        ["R8G8_UNORM"] = new(3, NtUnorm, 2, 0x3, 1.5f / 255f, b => [b[0] / 255f, b[1] / 255f, 0, 0]),
        ["R8_UNORM"] = new(Fmt8, NtUnorm, 1, 0x1, 1.5f / 255f, b => [b[0] / 255f, 0, 0, 0]),
        ["R10G10B10A2_UNORM"] = new(Fmt2_10_10_10, NtUnorm, 4, 0xF, 0.34f,
            b => { var p = BitConverter.ToUInt32(b, 0); return [(p & 0x3FF) / 1023f, ((p >> 10) & 0x3FF) / 1023f, ((p >> 20) & 0x3FF) / 1023f, (p >> 30) / 3f]; }),
        ["R11G11B10_FLOAT"] = new(6, NtFloat, 4, 0x7, 0f,
            b => { var p = BitConverter.ToUInt32(b, 0); return [DecodeFloat11(p & 0x7FF), DecodeFloat11((p >> 11) & 0x7FF), DecodeFloat10(p >> 22), 0]; }),
    };

    public static IEnumerable<object[]> FormatNames() => FormatCases.Keys.Select(static name => new object[] { name });

    [Theory]
    [MemberData(nameof(FormatNames))]
    public void ATargetFormatAccumulatesTwoOneOneDrawsFromA32BitExport(string name)
    {
        var format = FormatCases[name];
        using var rig = CreateRig();
        if (rig is null) return;
        var target = FilledTarget(rig, Size, Size, new byte[format.Bytes]);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, format.Format, format.NumberType, blendClamp: format.NumberType != NtFloat);
        state.Context[Cx.SpiShaderColFormat] = Exp32Abgr;
        state.Context[Cx.CbShaderMask] = format.ChannelMask;
        state.Context[Cx.CbTargetMask] = format.ChannelMask;
        state.Context[Cx.CbBlend0Control] = Ctl(One, Add, One, One, Add, One, separateAlpha: true);
        float[] colour = [0.25f, 0.5f, 0.125f, 0.375f];
        var pm4 = new ScenePm4();
        DrawQuad(rig, pm4, state, ScenePrograms.InterpolatedColor, colour);
        DrawQuad(rig, pm4, state, ScenePrograms.InterpolatedColor, colour);
        rig.Submit(pm4);
        var texels = rig.ReadImage(target, Size, Size, (uint)format.Bytes);
        var errors = new List<string>();
        foreach (var (x, y) in new[] { (0, 0), (33, 17), (63, 63) })
        {
            var offset = ((y * (int)Size) + x) * format.Bytes;
            var got = format.Decode(texels[offset..(offset + format.Bytes)]);
            for (var k = 0; k < 4; k++)
            {
                if ((format.ChannelMask & (1u << k)) == 0)
                {
                    continue;
                }

                var want = colour[k] * 2f;
                if (MathF.Abs(got[k] - want) > format.Tolerance)
                {
                    errors.Add($"{name} pixel ({x},{y}) channel {"RGBA"[k]} got {got[k]} want {want} (raw {BitConverter.ToString(texels, offset, format.Bytes)})");
                    break;
                }
            }
        }

        AssertClean(errors);
    }

    // ---- 3b. a program that exports fewer channels than the target has -----------------------------------

    [Fact]
    public void AProgramExportingOnlyRedAndGreenLeavesBlueAndAlphaOfAnRgba8TargetAlone()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var target = FilledTarget(rig, Size, Size, DstBytes);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, Fmt8888, NtUnorm, blendClamp: true);
        state.Context[Cx.SpiShaderColFormat] = Exp32Abgr;
        state.Context[Cx.CbShaderMask] = 0x3; // exp mrt0 v2, v3, off, off: EN = 0x3
        state.Context[Cx.CbTargetMask] = 0xF;
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, Src[0], Src[1], Src[2], Src[3]), OmPrograms.Gr32));
        var dst = Normalized(DstBytes);
        var errors = new List<string>();
        CheckCell8888(errors, rig.ReadImage(target, Size, Size), 100, [Src[0], Src[1], dst[2], dst[3]], 1, "RG export on RGBA8");
        AssertClean(errors);
    }

    // ---- 5b. multisampling, resolve and alpha to coverage --------------------------------------------------

    private const uint MsaaSize = 16;
    private const uint MsaaTile = 27; // 64KB_R_X, the render-target swizzle

    private static void MsaaRegisters(SceneState state)
    {
        state.Context[Cx.PaScAaConfig] = 2u | (2u << 4); // MSAA_NUM_SAMPLES = 2 (log2: 4x), MSAA_EXPOSED_SAMPLES = 2
    }

    /// <summary>Resolves slot 0 (4x) into slot 1 with CB_COLOR_CONTROL MODE = RESOLVE (3).</summary>
    private static void ResolveDraw(SceneRig rig, ScenePm4 pm4, ulong msaa, ulong resolved)
    {
        var state = new SceneState(MsaaSize, MsaaSize)
            .ColorTarget(0, msaa, MsaaSize, MsaaSize, Fmt8888, NtUnorm, tileMode: MsaaTile, samplesLog2: 2)
            .ColorTarget(1, resolved, MsaaSize, MsaaSize, Fmt8888, NtUnorm);
        MsaaRegisters(state);
        state.Context[Cx.CbColorControl] = (3u << 4) | (0xCCu << 16);
        state.Context[Cx.CbTargetMask] = 0xFF;
        state.Context[Cx.CbShaderMask] = 0xFF;
        rig.DrawMesh(pm4, state, SceneDraws.Quad(0.5f, 0, 0, 0, 0));
    }

    [Fact]
    public void ResolvingA4xTargetKeepsInteriorPixelsExactAndGivesEdgePixelsTheirCoverage()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var msaa = rig.Memory.Allocate(MsaaSize * MsaaSize * 4 * 4 + 0x20000, 0x10000);
        var resolved = FilledTarget(rig, MsaaSize, MsaaSize, [1, 2, 3, 4]);
        var draw = new SceneState(MsaaSize, MsaaSize).ColorTarget(0, msaa, MsaaSize, MsaaSize, Fmt8888, NtUnorm, tileMode: MsaaTile, samplesLog2: 2);
        MsaaRegisters(draw);
        var pm4 = new ScenePm4();
        rig.DrawMesh(pm4, draw, SceneDraws.Quad(0.5f, 0, 0, 1, 1));
        // The lower-left half: the diagonal from the top-left to the bottom-right corner cuts the pixels with x == y in half.
        rig.DrawMesh(pm4, draw, [SceneVertex.At(-1, -1, 0.5f, 1, 0, 0), SceneVertex.At(1, -1, 0.5f, 1, 0, 0), SceneVertex.At(-1, 1, 0.5f, 1, 0, 0)]);
        ResolveDraw(rig, pm4, msaa, resolved);
        rig.Submit(pm4);
        var pixels = rig.ReadImage(resolved, MsaaSize, MsaaSize);
        var errors = new List<string>();
        var edges = new List<string>();
        for (var y = 0; y < MsaaSize; y++)
        {
            for (var x = 0; x < MsaaSize; x++)
            {
                var offset = ((y * (int)MsaaSize) + x) * 4;
                if (x == y)
                {
                    // Edge: red covers 1..3 of the 4 samples, blue the rest: r = k/4 * 255, b = (1 - k/4) * 255, alpha 255.
                    var coverage = pixels[offset] / 255f;
                    var blue = pixels[offset + 2] / 255f;
                    var ok = coverage is >= 0.24f and <= 0.76f && MathF.Abs(coverage + blue - 1f) < 0.02f &&
                        MathF.Abs((coverage * 4f) - MathF.Round(coverage * 4f)) < 0.02f && pixels[offset + 3] == 255;
                    if (!ok) edges.Add($"edge ({x},{y}): got {pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]},{pixels[offset + 3]}");
                }
                else
                {
                    var red = x < y;
                    byte[] want = red ? [255, 0, 0, 255] : [0, 0, 255, 255];
                    if (!pixels.AsSpan(offset, 4).SequenceEqual(want))
                        errors.Add($"interior ({x},{y}): got {pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]},{pixels[offset + 3]} want {want[0]},{want[1]},{want[2]},{want[3]}");
                }
            }
        }

        errors.Insert(0, $"{edges.Count} of 16 edge pixels are not a 1..3 of 4 sample mix; first: {string.Join(" | ", edges.Take(4))}");
        AssertClean(errors.Take(12).ToList());
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 2)]
    [InlineData(1f, 4)]
    public void AlphaToCoverageScalesTheCoveredSamplesWithTheOutputAlpha(float alpha, int samples)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var msaa = rig.Memory.Allocate(MsaaSize * MsaaSize * 4 * 4 + 0x20000, 0x10000);
        var resolved = FilledTarget(rig, MsaaSize, MsaaSize, [1, 2, 3, 4]);
        var draw = new SceneState(MsaaSize, MsaaSize).ColorTarget(0, msaa, MsaaSize, MsaaSize, Fmt8888, NtUnorm, tileMode: MsaaTile, samplesLog2: 2);
        MsaaRegisters(draw);
        var pm4 = new ScenePm4();
        rig.DrawMesh(pm4, draw, SceneDraws.Quad(0.5f, 0, 0, 1, 1));
        // DB_ALPHA_TO_MASK: enable, and the dither offsets and rounding a driver programs for 4x.
        draw.Context[Cx.DbAlphaToMask] = 1u | (3u << 8) | (1u << 10) | (0u << 12) | (2u << 14) | (2u << 16);
        rig.DrawMesh(pm4, draw, SceneDraws.Quad(0.5f, 1, 0, 0, alpha));
        ResolveDraw(rig, pm4, msaa, resolved);
        rig.Submit(pm4);
        var pixels = rig.ReadImage(resolved, MsaaSize, MsaaSize);
        var covered = samples / 4f;
        var errors = new List<string>();
        foreach (var (x, y) in new[] { (0, 0), (7, 9), (15, 15) })
        {
            var offset = ((y * (int)MsaaSize) + x) * 4;
            var red = pixels[offset] / 255f;
            if (MathF.Abs(red - covered) > 0.02f)
            {
                errors.Add($"alpha {alpha}: pixel ({x},{y}) red coverage {red:F3} want {covered:F2} (rgba {pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]},{pixels[offset + 3]})");
            }
        }

        AssertClean(errors);
    }
}
