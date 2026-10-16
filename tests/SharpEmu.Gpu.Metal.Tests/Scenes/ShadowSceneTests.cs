// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// Shadow-map sampling: a depth target written by the depth block, then sampled with the
/// depth-compare MIMG instructions (image_sample_c*, image_gather4_c*) into float targets.
/// The compare operand order is the one Vulkan and the S# documentation use: the result of
/// DEPTH_COMPARE_FUNC is (reference OP texel), so "Less" passes when the reference is below
/// the stored depth. Every expected value is computed here from those rules.
/// </summary>
public sealed class ShadowSceneTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint N = 16;

    // T# DST_SEL (R, 0, 0, 1): the swizzle a depth texture normally carries. The emulator
    // refuses the identity (R, G, B, A) selection on a depth view; see the last test.
    private const uint DepthSwizzle = 4 | (0 << 3) | (0 << 6) | (1 << 9);

    // Stored depths. Both sit 0.2 of a D16 step above a 16-bit code, so a D16 texel is
    // visibly different from the 32-bit float the draw wrote, and round-to-nearest and
    // truncation agree on the code.
    private static readonly float ZLow = 16384.2f / 65535f;
    private static readonly float ZHigh = 39321.3f / 65535f;

    // Shared prologue of every program (user SGPRs: T# s0-7, S# s8-11, reference s12,
    // extra dword s13, primitive mask s14): v5/v6 = interpolated PARAM0.xy, v4 = reference,
    // v3 = extra dword (the _o offset), v7 = 0 (lod).
    private static readonly uint[] Prologue =
    [
        0xBEFC030E,                // s_mov_b32 m0, s14
        0xC8140000,                // v_interp_p1_f32_e32 v5, v0, attr0.x
        0xC8150001,                // v_interp_p2_f32_e32 v5, v1, attr0.x
        0xC8180100,                // v_interp_p1_f32_e32 v6, v0, attr0.y
        0xC8190101,                // v_interp_p2_f32_e32 v6, v1, attr0.y
        0x7E08020C,                // v_mov_b32_e32 v4, s12
        0x7E06020D,                // v_mov_b32_e32 v3, s13
        0x7E0E0280,                // v_mov_b32_e32 v7, 0
    ];

    private static uint[] Program(uint image0, uint image1, bool fourComponents) =>
    [
        .. Prologue,
        image0, image1,
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        .. fourComponents
            ? new uint[] { 0xF800180F, 0x0B0A0908 }    // exp mrt0 v8, v9, v10, v11 done vm
            : new uint[] { 0xF8001801, 0x00000008 },   // exp mrt0 v8, off, off, off done vm
        0xBF810000,                // s_endpgm
    ];

    // image_sample_c v8, v[4:6], s[0:7], s[8:11] dmask:0x1 dim:SQ_RSRC_IMG_2D
    private static readonly uint[] SampleC = Program(0xF0A00108, 0x00400804, false);
    // image_sample_c_lz v8, v[4:6], ...
    private static readonly uint[] SampleCLz = Program(0xF0BC0108, 0x00400804, false);
    // image_sample_c_l v8, v[4:7], ... (lod in v7 = 0)
    private static readonly uint[] SampleCL = Program(0xF0B00108, 0x00400804, false);
    // image_sample_c_o v8, v[3:6], ... (offset dword in v3)
    private static readonly uint[] SampleCO = Program(0xF0E00108, 0x00400803, false);
    // image_gather4_c v[8:11], v[4:6], ... dmask:0x1
    private static readonly uint[] Gather4C = Program(0xF1200108, 0x00400804, true);
    // image_gather4_c_lz v[8:11], v[4:6], ... dmask:0x1
    private static readonly uint[] Gather4CLz = Program(0xF13C0108, 0x00400804, true);
    // image_sample v[8:11], v[5:6], ... dmask:0xf (no compare)
    private static readonly uint[] SamplePlain = Program(0xF0800F08, 0x00400805, true);

    // image_sample_c_lz v8, v[4:7], ... dim:SQ_RSRC_IMG_2D_ARRAY (slice, as a float, from s13)
    private static readonly uint[] SampleCLzArray =
    [
        .. Prologue[..6],
        0x7E0E020D,                // v_mov_b32_e32 v7, s13
        0xF0BC0128, 0x00400804,    // image_sample_c_lz v8, v[4:7], s[0:7], s[8:11] dmask:0x1 dim:SQ_RSRC_IMG_2D_ARRAY
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF8001801, 0x00000008,    // exp mrt0 v8, off, off, off done vm
        0xBF810000,                // s_endpgm
    ];

    private sealed class Scene(SceneRig rig, ScenePm4 pm4, ulong depth, bool d16, Func<int, int, bool> high)
    {
        public SceneRig Rig { get; } = rig;
        public ScenePm4 Pm4 { get; } = pm4;
        public ulong Depth { get; } = depth;
        public bool D16 { get; } = d16;
        public Func<int, int, bool> High { get; } = high;

        /// <summary>The texel as the sampler reads it: D16 holds the nearest 16-bit code.</summary>
        public float Texel(int x, int y) => Stored(High(x, y) ? ZHigh : ZLow, D16);
    }

    private static float Stored(float z, bool d16) => d16 ? MathF.Round(z * 65535f) / 65535f : z;

    private static bool Pattern(int x, int y) => (x * 7 + y * 13) % 5 < 2;

    private static bool RightHalf(int x, int y) => x >= 8;

    private static bool Compare(uint func, float reference, float texel) => func switch
    {
        0 => false,
        1 => reference < texel,
        2 => reference == texel,
        3 => reference <= texel,
        4 => reference > texel,
        5 => reference != texel,
        6 => reference >= texel,
        _ => true,
    };

    private static SceneVertex[] UvQuad(float u0, float v0, float u1, float v1, float z = 0.5f) =>
    [
        SceneVertex.At(-1, -1, z, u0, v1, 0, 1),
        SceneVertex.At(1, -1, z, u1, v1, 0, 1),
        SceneVertex.At(-1, 1, z, u0, v0, 0, 1),
        SceneVertex.At(-1, 1, z, u0, v0, 0, 1),
        SceneVertex.At(1, -1, z, u1, v1, 0, 1),
        SceneVertex.At(1, 1, z, u1, v0, 0, 1),
    ];

    /// <summary>
    /// Renders the depth map through the depth block: a ZLow background and a ZHigh quad
    /// per pixel for which <paramref name="high"/> is true (depth test Always, writes on).
    /// </summary>
    private static Scene BuildDepth(SceneRig rig, bool d16, Func<int, int, bool> high)
    {
        var depth = rig.Memory.Allocate(N * N * 4 + 0x10000, 0x10000);
        var state = new SceneState(N, N).ColorTarget(0, rig.ColorTarget(N, N), N, N)
            .DepthTarget(depth, N, N, zFormat: d16 ? 1u : 3u).DepthControl(test: true, write: true, CompareFunc.Always);
        var vertices = new List<SceneVertex>(SceneDraws.Quad(ZLow, 1, 1, 1));
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                if (!high(x, y))
                {
                    continue;
                }

                float Ndc(int pixel) => -1f + (2f * pixel / N);
                vertices.AddRange(SceneDraws.Quad(ZHigh, 1, 1, 1, left: Ndc(x), bottom: -Ndc(y + 1), right: Ndc(x + 1), top: -Ndc(y)));
            }
        }

        var pm4 = rig.DrawMesh(new ScenePm4(), state, vertices);
        return new Scene(rig, pm4, depth, d16, high);
    }

    /// <summary>One full-target draw sampling the depth map; returns the target address.</summary>
    private static ulong Sample(Scene scene, uint[] program, uint[] sampler, float reference, uint extra = 0,
        SceneVertex[]? quad = null, bool rgba = false, uint dstSelect = DepthSwizzle)
    {
        var target = scene.Rig.ColorTarget(N, N, rgba ? 16u : 4u);
        var state = new SceneState(N, N).ColorTarget(0, target, N, N, format: rgba ? 14u : 4u, numberType: 7);
        uint[] userData =
        [
            .. Descriptors.Texture(scene.Depth, scene.D16 ? 7u : 22u, N, N, tileMode: 24, dstSelect: dstSelect),
            .. sampler,
            SceneState.F(reference),
            extra,
        ];
        scene.Rig.DrawMesh(scene.Pm4, state, quad ?? UvQuad(0, 0, 1, 1), program, userData);
        return target;
    }

    private static void Check(List<string> failures, string label, float[] actual, int stride, int channel,
        Func<int, int, float> expected, float tolerance = 0f)
    {
        var bad = 0;
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var want = expected(x, y);
                var got = actual[((y * (int)N) + x) * stride + channel];
                if (!(MathF.Abs(got - want) <= tolerance))
                {
                    if (bad++ < 3)
                    {
                        failures.Add($"{label}: pixel ({x},{y}) channel {channel} got {got:R}, expected {want:R}");
                    }
                }
            }
        }

        if (bad > 3)
        {
            failures.Add($"{label}: {bad} pixels differ in total");
        }
    }

    private static void AssertNone(List<string> failures) =>
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

    private static float[] ReadFloats(SceneRig rig, ulong target, int perPixel) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(rig.ReadImage(target, N, N, (uint)perPixel * 4)).ToArray();

    // ---- 2. every DEPTH_COMPARE_FUNC, reference below / equal / above the stored depth --------

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
    public void SampleCAppliesTheSamplersCompareFunctionAsReferenceOpTexel(uint func, bool d16)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16, RightHalf);
        var low = Stored(ZLow, d16);
        var high = Stored(ZHigh, d16);
        // Below both, equal to the left texel, between, equal to the right texel, above both.
        float[] references = [low - 0.1f, low, (low + high) / 2, high, high + 0.1f];
        var sampler = Descriptors.Sampler(depthCompare: (CompareFunc)func);
        var targets = references.Select(reference => Sample(scene, SampleC, sampler, reference)).ToArray();
        rig.Submit(scene.Pm4);
        var failures = new List<string>();
        for (var index = 0; index < references.Length; index++)
        {
            var reference = references[index];
            Check(failures, $"func {func} d16={d16} ref {reference:R}", ReadFloats(rig, targets[index], 1), 1, 0,
                (x, y) => Compare(func, reference, scene.Texel(x, y)) ? 1f : 0f);
        }

        AssertNone(failures);
    }

    // ---- 1. the sampling opcodes ---------------------------------------------------------------

    [Theory]
    [InlineData("c")]
    [InlineData("c_lz")]
    [InlineData("c_l")]
    public void CompareSamplesOfARandomPatternPickTheTexelUnderThePixel(string opcode)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16: false, Pattern);
        var program = opcode switch { "c" => SampleC, "c_lz" => SampleCLz, _ => SampleCL };
        var failures = new List<string>();
        // Less: the reference 0.5 is below the high texels and above the low ones.
        var target = Sample(scene, program, Descriptors.Sampler(depthCompare: CompareFunc.Less), 0.5f);
        rig.Submit(scene.Pm4);
        Check(failures, opcode, ReadFloats(rig, target, 1), 1, 0, (x, y) => Pattern(x, y) ? 1f : 0f);
        AssertNone(failures);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(-2, 3)]
    [InlineData(7, -8)]
    [InlineData(-8, 7)]
    [InlineData(20, -5)]
    [InlineData(-31, 31)]
    public void SampleCOffsetShiftsTheTexelByTheSignedSixBitOffsets(int dx, int dy)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16: false, Pattern);
        // OFFSET dword: x in bits 5:0, y in bits 13:8, both signed.
        var offset = (uint)(dx & 0x3F) | ((uint)(dy & 0x3F) << 8);
        var target = Sample(scene, SampleCO, Descriptors.Sampler(depthCompare: CompareFunc.Less), 0.5f, offset);
        rig.Submit(scene.Pm4);
        var failures = new List<string>();
        Check(failures, $"offset ({dx},{dy})", ReadFloats(rig, target, 1), 1, 0,
            (x, y) => Pattern(Math.Clamp(x + dx, 0, (int)N - 1), Math.Clamp(y + dy, 0, (int)N - 1)) ? 1f : 0f);
        AssertNone(failures);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Gather4CReturnsTheFourCompareResultsInTheDocumentedOrder(bool lz, bool d16)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16, Pattern);
        // A quarter of a texel past each pixel's own texel centre: the bilinear footprint
        // is texels (x, y)..(x+1, y+1) at fraction 0.25, clamped at the target edge.
        var quarter = 0.25f / N;
        var quad = UvQuad(quarter, quarter, 1 + quarter, 1 + quarter);
        var target = Sample(scene, lz ? Gather4CLz : Gather4C, Descriptors.Sampler(depthCompare: CompareFunc.Less), 0.5f,
            quad: quad, rgba: true);
        rig.Submit(scene.Pm4);
        var texels = ReadFloats(rig, target, 4);
        float C(int x, int y) => Compare(1, 0.5f, scene.Texel(Math.Clamp(x, 0, (int)N - 1), Math.Clamp(y, 0, (int)N - 1))) ? 1f : 0f;
        var failures = new List<string>();
        // Component order: (i0, j1), (i1, j1), (i1, j0), (i0, j0) with j growing downwards.
        Check(failures, "gather x=(i0,j1)", texels, 4, 0, (x, y) => C(x, y + 1));
        Check(failures, "gather y=(i1,j1)", texels, 4, 1, (x, y) => C(x + 1, y + 1));
        Check(failures, "gather z=(i1,j0)", texels, 4, 2, (x, y) => C(x + 1, y));
        Check(failures, "gather w=(i0,j0)", texels, 4, 3, (x, y) => C(x, y));
        AssertNone(failures);
    }

    // ---- the S# compare function only matters to the _c opcodes ------------------------------

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(7u, false)]
    [InlineData(1u, true)]
    public void APlainSampleOfADepthTextureIgnoresTheSamplersCompareFunction(uint func, bool d16)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16, Pattern);
        var target = Sample(scene, SamplePlain, Descriptors.Sampler(depthCompare: (CompareFunc)func), 0.5f, rgba: true);
        rig.Submit(scene.Pm4);
        var texels = ReadFloats(rig, target, 4);
        var failures = new List<string>();
        Check(failures, $"plain depth x (S# func {func}, d16 {d16})", texels, 4, 0, (x, y) => scene.Texel(x, y), d16 ? 1e-6f : 0f);
        Check(failures, "plain depth y", texels, 4, 1, (x, y) => 0f);
        Check(failures, "plain depth w", texels, 4, 3, (x, y) => 1f);
        AssertNone(failures);
    }

    [Fact]
    public void CompareFunctionNeverFailsEveryTexelOfACompareSampleAndAlwaysPassesThem()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16: false, Pattern);
        var never = Sample(scene, SampleC, Descriptors.Sampler(depthCompare: CompareFunc.Never), 0.5f);
        var always = Sample(scene, SampleC, Descriptors.Sampler(depthCompare: CompareFunc.Always), 0.5f);
        rig.Submit(scene.Pm4);
        var failures = new List<string>();
        Check(failures, "Never", ReadFloats(rig, never, 1), 1, 0, (x, y) => 0f);
        Check(failures, "Always", ReadFloats(rig, always, 1), 1, 0, (x, y) => 1f);
        AssertNone(failures);
    }

    // ---- 3. address modes and borders -------------------------------------------------------------

    [Theory]
    [InlineData("wrap", 0u, 0u)]
    [InlineData("mirror", 1u, 0u)]
    [InlineData("edge", 2u, 0u)]
    [InlineData("border transparent black", 6u, 0u)]
    [InlineData("border opaque black", 6u, 1u)]
    [InlineData("border opaque white", 6u, 2u)]
    public void CoordinatesOutsideTheMapFollowTheAddressModeAndBorderColor(string name, uint clamp, uint borderType)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16: false, Pattern);
        // uv spans -1..2, so every pixel lands on a texel's middle (3x - 14.5 in texel space).
        var quad = UvQuad(-1, -1, 2, 2);
        var sampler = Descriptors.Sampler(clamp, clamp, clamp, CompareFunc.Less, borderColorType: borderType);
        var target = Sample(scene, SampleCLz, sampler, 0.5f, quad: quad);
        rig.Submit(scene.Pm4);
        var borderDepth = borderType == 2 ? 1f : 0f;
        float Expected(int x, int y)
        {
            int Texel(int pixel) => (int)Math.Floor(((-1.0 + (3.0 * (pixel + 0.5) / N)) * N));
            var tx = Texel(x);
            var ty = Texel(y);
            int Resolve(int t) => clamp switch
            {
                0 => ((t % (int)N) + (int)N) % (int)N,
                1 => ((t % (2 * (int)N)) + (2 * (int)N)) % (2 * (int)N) is var m && m < N ? m : (2 * (int)N) - 1 - (((t % (2 * (int)N)) + (2 * (int)N)) % (2 * (int)N)),
                2 => Math.Clamp(t, 0, (int)N - 1),
                _ => t,
            };

            var rx = Resolve(tx);
            var ry = Resolve(ty);
            var outside = rx < 0 || rx >= N || ry < 0 || ry >= N;
            var depth = outside ? borderDepth : scene.Texel(rx, ry);
            return Compare(1, 0.5f, depth) ? 1f : 0f;
        }

        var failures = new List<string>();
        Check(failures, name, ReadFloats(rig, target, 1), 1, 0, Expected);
        AssertNone(failures);
    }

    // ---- 4. PCF ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BilinearCompareSamplesFilterThePerTexelCompareResults(bool d16)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16, Pattern);
        // 0.05..0.95 over 16 pixels: texel-space coordinates 0.8 + 0.9 * (x + 0.5), so the
        // bilinear fractions vary from pixel to pixel.
        var quad = UvQuad(0.05f, 0.05f, 0.95f, 0.95f);
        var sampler = Descriptors.Sampler(depthCompare: CompareFunc.Less, magFilter: 1, minFilter: 1);
        var target = Sample(scene, SampleCLz, sampler, 0.5f, quad: quad);
        rig.Submit(scene.Pm4);
        float C(int x, int y) => Compare(1, 0.5f, scene.Texel(Math.Clamp(x, 0, (int)N - 1), Math.Clamp(y, 0, (int)N - 1))) ? 1f : 0f;
        float Expected(int x, int y)
        {
            var tx = ((0.05 + (0.9 * (x + 0.5) / N)) * N) - 0.5;
            var ty = ((0.05 + (0.9 * (y + 0.5) / N)) * N) - 0.5;
            var i0 = (int)Math.Floor(tx);
            var j0 = (int)Math.Floor(ty);
            var fx = (float)(tx - i0);
            var fy = (float)(ty - j0);
            return ((1 - fx) * (1 - fy) * C(i0, j0)) + (fx * (1 - fy) * C(i0 + 1, j0)) +
                   ((1 - fx) * fy * C(i0, j0 + 1)) + (fx * fy * C(i0 + 1, j0 + 1));
        }

        var failures = new List<string>();
        // Tolerance: the hardware quantises the bilinear fraction to 8 bits (1/256 per axis).
        Check(failures, $"pcf d16={d16}", ReadFloats(rig, target, 1), 1, 0, Expected, 0.012f);
        AssertNone(failures);
    }

    // ---- 6. D16 vs D32F: the compare happens at the texture format's precision ------------------

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(false, 2u)]
    [InlineData(false, 4u)]
    [InlineData(true, 1u)]
    [InlineData(true, 2u)]
    [InlineData(true, 4u)]
    public void CompareHappensAtTheDepthFormatsPrecision(bool d16, uint func)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16, RightHalf);
        // The reference is the exact 32-bit depth the draw wrote. A 32-bit float map stores
        // it exactly (Equal passes); the 16-bit map holds the nearest code, which is below it
        // by 0.2 of a step, so a full-precision compare says Equal fails and Greater passes.
        // (Hardware semantic doubt: whether the unit also rounds the reference to 16 bits.)
        var low = Sample(scene, SampleC, Descriptors.Sampler(depthCompare: (CompareFunc)func), ZLow);
        var high = Sample(scene, SampleC, Descriptors.Sampler(depthCompare: (CompareFunc)func), ZHigh);
        rig.Submit(scene.Pm4);
        var failures = new List<string>();
        Check(failures, $"d16={d16} func {func} ref ZLow", ReadFloats(rig, low, 1), 1, 0,
            (x, y) => Compare(func, ZLow, scene.Texel(x, y)) ? 1f : 0f);
        Check(failures, $"d16={d16} func {func} ref ZHigh", ReadFloats(rig, high, 1), 1, 0,
            (x, y) => Compare(func, ZHigh, scene.Texel(x, y)) ? 1f : 0f);
        AssertNone(failures);
    }

    [Fact]
    public void ADepthTextureWithTheIdentityDstSelMayBeSampled()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var scene = BuildDepth(rig, d16: false, RightHalf);
        // Hardware applies any DST_SEL to a depth texture's (depth, 0, 0, 1) result: the
        // identity select reads the missing channels as 0, 0, 1.
        var target = Sample(scene, SampleC, Descriptors.Sampler(depthCompare: CompareFunc.Less), 0.5f,
            dstSelect: Descriptors.IdentitySelect);
        var plain = Sample(scene, SamplePlain, Descriptors.Sampler(), 0.5f, rgba: true, dstSelect: Descriptors.IdentitySelect);
        rig.Submit(scene.Pm4);
        var failures = new List<string>();
        Check(failures, "identity dst_sel", ReadFloats(rig, target, 1), 1, 0,
            (x, y) => Compare(1, 0.5f, scene.Texel(x, y)) ? 1f : 0f);
        var texels = ReadFloats(rig, plain, 4);
        Check(failures, "identity x", texels, 4, 0, (x, y) => scene.Texel(x, y));
        Check(failures, "identity y", texels, 4, 1, (x, y) => 0f);
        Check(failures, "identity z", texels, 4, 2, (x, y) => 0f);
        Check(failures, "identity w", texels, 4, 3, (x, y) => 1f);
        AssertNone(failures);
    }

    // ---- 5. array shadow maps (a linear R32F array: the depth block cannot render layers here) -----

    [Fact]
    public void ArrayShadowMapSamplesTheSliceNamedInTheAddress()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        // Two slices of depths: slice 0 holds the pattern, slice 1 its complement.
        const int slices = 2;
        var texels = new float[slices * N * N];
        for (var slice = 0; slice < slices; slice++)
        {
            for (var y = 0; y < N; y++)
            {
                for (var x = 0; x < N; x++)
                {
                    texels[(slice * N * N) + (y * N) + x] = Pattern(x, y) == (slice == 0) ? ZHigh : ZLow;
                }
            }
        }

        var image = rig.LinearImage(N, N * slices, 4, System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(texels));
        var failures = new List<string>();
        var pm4 = new ScenePm4();
        var targets = new ulong[slices];
        for (var slice = 0; slice < slices; slice++)
        {
            targets[slice] = rig.ColorTarget(N, N);
            var state = new SceneState(N, N).ColorTarget(0, targets[slice], N, N, format: 4, numberType: 7);
            uint[] userData =
            [
                .. Descriptors.Texture(image, 22, N, N, type: 13, depthOrLastLayer: slices - 1, dstSelect: DepthSwizzle),
                .. Descriptors.Sampler(depthCompare: CompareFunc.Less),
                SceneState.F(0.5f),
                SceneState.F(slice),
            ];
            rig.DrawMesh(pm4, state, UvQuad(0, 0, 1, 1), SampleCLzArray, userData);
        }

        rig.Submit(pm4);
        for (var slice = 0; slice < slices; slice++)
        {
            var s = slice;
            Check(failures, $"slice {slice}", ReadFloats(rig, targets[slice], 1), 1, 0, (x, y) => Pattern(x, y) == (s == 0) ? 1f : 0f);
        }

        AssertNone(failures);
    }
}
