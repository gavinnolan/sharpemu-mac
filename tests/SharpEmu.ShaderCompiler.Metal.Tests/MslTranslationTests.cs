// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Structural checks over the emitted MSL — these run on every platform because
/// translation is pure text generation; only the runtime tests need a Metal device.
/// </summary>
public sealed class MslTranslationTests
{
    [Fact]
    public void ComputeFixturesResolveDescriptorFormatsBeforeCompilation()
    {
        foreach (var fixture in new[] { Gen5ComputeFixtures.TypedLoad, Gen5ComputeFixtures.TypedStore })
        {
            var request = Gen5ComputeFixtures.CreateComputeRequest(fixture);
            Assert.NotEmpty(request.Resources.Info.Buffers);
            Assert.All(request.Resources.Info.Buffers, buffer =>
            {
                Assert.Equal(56u, buffer.DescriptorFormat);
                Assert.Equal(DescriptorConstants.IdentityDestinationSelect, buffer.DescriptorSwizzle);
            });
            _ = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
        }
    }

    [Fact]
    public void SadU32UsesUnsignedAbsoluteDifferenceAndAccumulator()
    {
        var sad = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vop3,
            "VSadU32",
            [0, 0],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
            [Gen5Operand.Vector(3)],
            null);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0, [sad, end]), ShaderStage.Compute, localSizeX: 1);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("((max(v0, v1) - min(v0, v1)) + (v2))", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void F16CompareUsesHalfOperands()
    {
        var compare = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vopc,
            "VCmpLtF16",
            [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            [],
            null);
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0, [compare, end]), ShaderStage.Compute, localSizeX: 1);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("as_type<half>", shader.Source, StringComparison.Ordinal);
        Assert.Contains(" < ", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFixtureTranslates()
    {
        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
            Assert.Equal(Gen5MslStage.Compute, shader.Stage);
            Assert.Equal("gen5_cs", shader.EntryPoint);
            Assert.Contains("kernel void gen5_cs(", shader.Source, StringComparison.Ordinal);

            // Structured control flow, so no PC dispatcher survives in the output.
            Assert.DoesNotContain("switch (pc)", shader.Source, StringComparison.Ordinal);
        }
    }

    /// <summary>The per-lane EXEC guard is dropped only where the program proves every
    /// lane is set. A program that narrows EXEC keeps its guards from that point on, and
    /// turning the elision off brings them all back — which is what the switch is for.</summary>
    [Fact]
    public void ExecGuardsSurviveWhereExecIsNarrowed()
    {
        // The fixture moves 0 into EXEC_LO, stores, then restores it: everything before
        // the first EXEC write is unguarded, everything after it is guarded.
        var request = Gen5ComputeFixtures.CreateComputeRequest(Gen5ComputeFixtures.ExecStore);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var elided, out var error), error);
        var body = elided.Source;
        var firstExecWrite = body.IndexOf("exec = ((", StringComparison.Ordinal);
        Assert.True(firstExecWrite > 0, "the fixture is supposed to write EXEC");
        Assert.DoesNotContain("if (exec)", body[..firstExecWrite], StringComparison.Ordinal);
        Assert.Contains("if (exec)", body[firstExecWrite..], StringComparison.Ordinal);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(
                request,
                out var guarded,
                out error,
                new Gen5MslCodegenOptions(ExecGuardElision: false)),
            error);
        Assert.Contains("if (exec) { v0 = ", guarded.Source, StringComparison.Ordinal);
        Assert.True(
            CountOf(guarded.Source, "if (exec)") > CountOf(body, "if (exec)"),
            "the switch has to put the guards back");
    }

    private static int CountOf(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
            index = source.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The register file is uint, so an instruction that reads a float and
    /// writes back raw bits used to emit as_type&lt;uint&gt;(as_type&lt;float&gt;(v0)) — a
    /// no-op, but one the Metal compiler had to be handed first. V_CVT_PKRTZ_F16_F32
    /// truncates its operands' mantissas before converting, which is where it showed up.
    /// The packed halves still have to come out right: 1.5 is 0x3E00 and 2.0 is 0x4000.</summary>
    [Fact]
    public void PackedHalfConversionDoesNotRoundTripItsBitcasts()
    {
        var fixture = new Gen5ComputeFixture(
            "pkrtz",
            [
                0x7E0002FF, 0x3FC00000, // v_mov_b32 v0, 1.5f
                0x7E0202FF, 0x40000000, // v_mov_b32 v1, 2.0f
                0x5E0A0300,             // v_cvt_pkrtz_f16_f32 v5, v0, v1
                0xE0700000, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 8,
            StoreBackingBytes: 16);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
        Assert.Contains("& 0xFFFFE000u", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("as_type<uint>(as_type<float>(", shader.Source, StringComparison.Ordinal);

        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            return;
        }

        Assert.True(MetalNative.IsAvailable, "The required execution gate needs a Metal device.");
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out var library, out var metalError),
            metalError);
        Assert.True(
            MetalNative.TryExecuteWithArgumentBuffer(
                library,
                shader.EntryPoint,
                new byte[fixture.StoreBackingBytes],
                new byte[Resources.PushData.ByteSize],
                shader.ArgumentLayout!,
                threadsPerThreadgroup: 1,
                out var result,
                out var runError),
            runError);
        Assert.Equal(
            0x40003E00u,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result));
    }

    [Fact]
    public void ExecMaskedStoresAreGuarded()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.ExecStore);

        // Every buffer store past the first EXEC write must sit behind the per-lane
        // guard, and a whole-dword store goes out as one dword, not four bytes.
        Assert.Contains("if (exec)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_store_dword(b0,", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_store_bytes(b0,", shader.Source, StringComparison.Ordinal);

        // s_mov_b32 exec_lo, 0 / -1 must drive the per-lane bool.
        Assert.Contains("exec = ((", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoopFixtureBecomesARealLoop()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.Loop);

        // The backward branch is a while (true) whose back edge is a continue and
        // whose exit is a break, with the scalar condition driving both.
        Assert.Contains("while (true)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("if (scc)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("continue;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("break;", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("switch (pc)", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoopsAreBoundedByDefault()
    {
        // The step cap rides on the back edge now that there is no dispatcher to
        // charge per block, so a loop carries it and straight-line code does not.
        var looping = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.Loop);
        Assert.Contains("if (++steps >=", looping.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_note_step_cap(", looping.Source, StringComparison.Ordinal);

        var straightLine = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.Fmac);
        Assert.DoesNotContain("++steps", straightLine.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentBufferCarriesBufferLengthsAndUsesSeparatePushData()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.ExecStore);
        Assert.NotNull(shader.ArgumentLayout);
        Assert.Contains("buffer_bytes [[id(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_resources [[buffer(0)]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_push_data [[buffer(1)]]", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpEmuUniforms", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PixelStageEmitsFragmentInterface()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();

        Assert.Equal(Gen5MslStage.Pixel, shader.Stage);
        Assert.Equal("gen5_ps", shader.EntryPoint);
        Assert.Equal(1u, shader.AttributeCount);
        Assert.Contains("fragment Gen5PsOut gen5_ps(", shader.Source, StringComparison.Ordinal);
        // The fixture reads attr0 through V_INTERP_MOV's P0 selector, which is
        // the provoking vertex's parameter -- exactly a flat input.
        Assert.Contains("float4 attr0 [[user(locn0), flat]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[color(0)]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[position]]", shader.Source, StringComparison.Ordinal);

        // Interpolation reads land in VGPRs; the export writes MRT0 and inactive
        // lanes discard at the end. This program never narrows EXEC, so the export
        // assigns outright — ExecGuardsSurviveWhereExecIsNarrowed covers the masked form.
        Assert.Contains("as_type<uint>(sharpemu_in.attr0[0])", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_out.mrt0 = vec<float, 4>(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("discard_fragment();", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void NullValidMaskExportControlsFragmentDiscard()
    {
        var export = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            ],
            [],
            new Gen5ExportControl(9, 0, false, true, true));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0, [export, end]), ShaderStage.Pixel, pixelOutputs: []);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains(
            "bool pixel_valid_mask_active = true;",
            shader.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "pixel_valid_mask_active = exec;",
            shader.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!pixel_valid_mask_active)",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PixelOutputKindsSelectTheAttachmentType()
    {
        var uintShader = Gen5ComputeFixtures.CompilePixelOrThrow(Gen5PixelOutputKind.Uint);
        Assert.Contains("uint4 mrt0 [[color(0)]];", uintShader.Source, StringComparison.Ordinal);

        var sintShader = Gen5ComputeFixtures.CompilePixelOrThrow(Gen5PixelOutputKind.Sint);
        Assert.Contains("int4 mrt0 [[color(0)]];", sintShader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityPixelOutputKeepsGuestComponentOrder()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();

        Assert.Contains(
            "vec<float, 4>(as_type<float>(v0), as_type<float>(v1), " +
            "as_type<float>(v2), as_type<float>(v3))",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BgraPixelOutputMapsGuestComponentsToPhysicalOrder()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow(
            componentMapping: new Gen5ColorComponentMapping(0xC6));

        Assert.Contains(
            "vec<float, 4>(as_type<float>(v2), as_type<float>(v1), " +
            "as_type<float>(v0), as_type<float>(v3))",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VertexStageEmitsVertexInterface()
    {
        var shader = Gen5ComputeFixtures.CompileVertexOrThrow();

        Assert.Equal(Gen5MslStage.Vertex, shader.Stage);
        Assert.Equal("gen5_vs", shader.EntryPoint);
        Assert.Equal(1u, shader.AttributeCount);
        Assert.Contains("vertex Gen5VsOut gen5_vs(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 sharpemu_position [[position]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 param0 [[user(locn0)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("uint sharpemu_vertex_id [[vertex_id]],", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v5 = sharpemu_vertex_id;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v8 = sharpemu_instance_id;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_out.sharpemu_position = float4(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_out.param0 = float4(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("return sharpemu_out;", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredVertexOutputsAreZeroFilledDeclarations()
    {
        // The paired fragment shader reads locations 0..2; the program only
        // exports param0, so 1 and 2 must still be declared (zero-filled).
        var shader = Gen5ComputeFixtures.CompileVertexOrThrow(requiredVertexOutputCount: 3);
        Assert.Equal(3u, shader.AttributeCount);
        Assert.Contains("float4 param1 [[user(locn1)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 param2 [[user(locn2)]];", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedShadersCoverThePresenterSurface()
    {
        var fullscreen = MslFixedShaders.CreateFullscreenVertex(2);
        Assert.Contains("vertex FullscreenOut fullscreen_vs(", fullscreen, StringComparison.Ordinal);
        Assert.Contains("float4 attr1 [[user(locn1)]];", fullscreen, StringComparison.Ordinal);

        Assert.Contains("tex0.sample(smp0, in.attr0.xy)", MslFixedShaders.CreateCopyFragment(), StringComparison.Ordinal);
        Assert.Contains("float4(1.0f, 0.0f, 1.0f, 1.0f)", MslFixedShaders.CreateSolidFragment(1f, 0f, 1f, 1f), StringComparison.Ordinal);
        Assert.Contains("return in.attr3;", MslFixedShaders.CreateAttributeFragment(3), StringComparison.Ordinal);
        Assert.Contains("fragment void depth_only_fs()", MslFixedShaders.CreateDepthOnlyFragment(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xFACu, "as_type<float>(v4), as_type<float>(v5), as_type<float>(v6), as_type<float>(v7)")]
    [InlineData(0x9F5u, "as_type<float>(v7), as_type<float>(v4), as_type<float>(v5), as_type<float>(v6)")]
    [InlineData(0xF2Eu, "as_type<float>(v6), as_type<float>(v5), as_type<float>(v4), as_type<float>(v7)")]
    [InlineData(0x3ACu, "as_type<float>(v4), as_type<float>(v5), as_type<float>(v6), 0.0f")]
    [InlineData(0xFA4u, "as_type<float>(v4), 0.0f, as_type<float>(v6), as_type<float>(v7)")]
    public void ImageStoreAppliesInverseDescriptorSwizzle(
        uint dstSelect,
        string expectedComponents)
    {
        var shader = CompileImageStore(dstSelect, dmask: 0xF);

        Assert.Contains(
            $".write(vec<float, 4>({expectedComponents}),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageStoreTreatsZeroDmaskAsX()
    {
        var shader = CompileImageStore(
            Gen5ShaderTranslator.IdentityImageDstSelect,
            dmask: 0);

        Assert.Contains(
            ".write(vec<float, 4>(as_type<float>(v4), 0.0f, 0.0f, 0.0f),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageStoreMipAppliesInverseDescriptorSwizzle()
    {
        var shader = CompileImageStore(
            0x9F5u,
            dmask: 0xF,
            opcode: "ImageStoreMip");

        Assert.Contains(
            ".write(vec<float, 4>(as_type<float>(v7), as_type<float>(v4), as_type<float>(v5), as_type<float>(v6)),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UintImageStoreUsesUintTextureAndInverseDescriptorSwizzle()
    {
        var shader = CompileImageStore(
            0xF2Eu,
            dmask: 0xF,
            unifiedFormat: 69u); // FORMAT_16_16_16_16_UINT

        Assert.Contains(
            "texture2d<uint, access::write>",
            shader.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".write(vec<uint, 4>(v6, v5, v4, v7),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedOpcodeFailsLoudlyWithPc()
    {
        // v_cubeid_f32 is real but outside the phase-1 ALU set: the translator
        // must name the opcode and pc instead of emitting wrong code.
        var fixture = new Gen5ComputeFixture(
            "unsupported",
            [
                0xD5C40000, 0x04060501, // v_cubeid_f32 v0, v1, v2, v3
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Gen5ComputeFixtures.CompileRequestOrThrow(fixture));
        Assert.Contains("pc=0x", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeVectorSourceUsesM0ForDynamicRead()
    {
        var fixture = new Gen5ComputeFixture(
            "relative-vector-source",
            [
                0x7E1802FF, 0x11111111, // v_mov_b32 v12, 0x11111111
                0x7E1A02FF, 0x22222222, // v_mov_b32 v13, 0x22222222
                0x7E6E870C,             // v_movrels_b32 v55, v12
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        // The relative number is still the encoded one plus M0, wrapped into the file,
        // but nothing indexes the register file with it: the read is a choice among the
        // registers this program touches, so they all stay in hardware registers. A
        // register the program never writes holds undefined data in hardware, which is
        // why only its own registers have to be reachable.
        Assert.Contains("(12u + ", shader.Source, StringComparison.Ordinal);
        Assert.Contains("s124", shader.Source, StringComparison.Ordinal);
        Assert.Contains(") & 255u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("== 12u ? v12", shader.Source, StringComparison.Ordinal);
        Assert.Contains("== 13u ? v13", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("uint v[256]", shader.Source, StringComparison.Ordinal);
    }

    /// <summary>When straight-line scalar code produces M0, the move reaches a known set
    /// of registers and resolves to them directly — no run-time number at all.</summary>
    [Fact]
    public void RelativeVectorSourceWithAKnownM0ResolvesToOneRegister()
    {
        var fixture = new Gen5ComputeFixture(
            "relative-vector-source-constant",
            [
                0xBEFC0382,             // s_mov_b32 m0, 2
                0x7E1C02FF, 0x33333333, // v_mov_b32 v14, 0x33333333
                0x7E6E870C,             // v_movrels_b32 v55, v12
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        // 12 + 2: the source is v14 outright, with no register number to resolve.
        Assert.Contains("= v14;", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("& 255u", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("== 14u ?", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("uint v[256]", shader.Source, StringComparison.Ordinal);

        // With the switch off the same program indexes the register file at run time,
        // and the file has to stay an array for it.
        var request = Gen5ComputeFixtures.CreateComputeRequest(fixture);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(
                request,
                out var unresolved,
                out var error,
                new Gen5MslCodegenOptions(BoundedRelativeMoves: false)),
            error);
        Assert.Contains("(12u + ", unresolved.Source, StringComparison.Ordinal);
        Assert.Matches(@"v\[t\d+\]", unresolved.Source);
        Assert.Contains("uint v[256] = {};", unresolved.Source, StringComparison.Ordinal);
    }

    [Theory]
    // V_MOVRELD writes the relative destination from an ordinary src0, V_MOVRELSD
    // makes both ends relative, and V_MOVRELSD2 splits M0 into two offsets.
    [InlineData(0x7E6E850Cu, "VMovreldB32")]
    [InlineData(0x7E6E890Cu, "VMovrelsdB32")]
    [InlineData(0x7E6E910Cu, "VMovrelsd2B32")]
    public void RelativeMoveFormsAddressTheRegisterFileThroughM0(uint word, string opcode)
    {
        var fixture = new Gen5ComputeFixture(
            $"relative-{opcode}",
            [word, 0xBF810000],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);
        var program = Gen5ComputeFixtures.DecodeOrThrow(fixture);
        Assert.Equal(opcode, program.Instructions[0].Opcode);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
        Assert.Contains("s124", shader.Source, StringComparison.Ordinal);
        // The destination is the relative one for every form but V_MOVRELS, and
        // the relative number wraps into the register file.
        Assert.Contains("(55u + ", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 255u", shader.Source, StringComparison.Ordinal);
        // V_MOVRELSD2 reads M0's two halves as independent offsets.
        Assert.Equal(
            opcode == "VMovrelsd2B32",
            shader.Source.Contains(">> 16u) & 0x3FFu", StringComparison.Ordinal));
    }

    [Fact]
    public void ScalarBlockerOpcodesCompileWithRdna2Semantics()
    {
        var fixture = new Gen5ComputeFixture(
            "scalar-blockers",
            [
                0xBF130200, // s_cmp_lg_u64 s[0:1], s[2:3]
                0xBE861404, // s_ff1_i32_b64 s6, s[4:5]
                0xBEEB106A, // s_bcnt1_i32_b64 s107, s[106:107]
                0xBE890908, // s_wqm_b32 s9, s8
                0xBF810000, // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains(" != ", shader.Source, StringComparison.Ordinal);
        Assert.Contains("(uint)ctz(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("(uint)popcount(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 0x11111111u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("* 0xFu", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void DataShareWaveCountersUseOneAtomicPerWave()
    {
        var fixture = new Gen5ComputeFixture(
            "data-share-wave-counters",
            [
                0xD8F80014, 0x07000000, // ds_append v7 offset:20
                0xD8F40014, 0x08000000, // ds_consume v8 offset:20
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("popcount(", shader.Source, StringComparison.Ordinal);
        Assert.Contains(">> 16u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFFu", shader.Source, StringComparison.Ordinal);
        Assert.Contains("atomic_fetch_add_explicit", shader.Source, StringComparison.Ordinal);
        Assert.Contains("atomic_fetch_sub_explicit", shader.Source, StringComparison.Ordinal);
        Assert.Contains("simd_broadcast", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xBF970001u)]
    [InlineData(0xBF980001u)]
    [InlineData(0xBF990001u)]
    [InlineData(0xBF9A0001u)]
    public void DebugConditionBranchesFallThroughWithoutShaderDebugger(uint branch)
    {
        var fixture = new Gen5ComputeFixture(
            "debug-condition-branch",
            [
                branch,
                0xBF800000, // s_nop 0
                0xBF810000, // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("if (!(false))", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 22u)]
    [InlineData(false, 0u)]
    public void TypedBufferLoadUsesTheInstructionFormatAndUntypedKeepsTheDescriptorTable(bool typed, uint typedFormat)
    {
        var load = new Gen5ShaderInstruction(
            0,
            typed ? Gen5ShaderEncoding.Mtbuf : Gen5ShaderEncoding.Mubuf,
            typed ? "TBufferLoadFormatXyzw" : "BufferLoadFormatXyzw",
            [0, 0],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(8), Gen5Operand.Source(128, null)],
            [Gen5Operand.Vector(4)],
            new Gen5BufferMemoryControl(
                4,
                0,
                4,
                8,
                0,
                IndexEnabled: false,
                OffsetEnabled: false,
                Glc: false,
                Slc: false,
                Typed: typed,
                TypedFormat: typedFormat));
        var end = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);
        var scalars = new uint[256];
        scalars[8] = 0x2000;
        scalars[10] = 64;
        scalars[11] = 77u << 12;
        var shader = CompileMaterialized(new Gen5ShaderProgram(0, [load, end]), scalars[..12]);

        if (typed)
        {
            Assert.DoesNotContain("sharpemu_gfx10_formats[(", shader.Source, StringComparison.Ordinal);
            Assert.Contains("sharpemu_format_layout(4u, 0u,", shader.Source, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("sharpemu_gfx10_formats[(", shader.Source, StringComparison.Ordinal);
            Assert.Contains("== 7u ?", shader.Source, StringComparison.Ordinal);
            Assert.Contains(": true);", shader.Source, StringComparison.Ordinal);
            Assert.DoesNotContain("sharpemu_format_bytes", shader.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TypedBufferStoreEncodesAndPlacesEachComponentInOneElement()
    {
        // Format 14 is 8_8 unorm: both registers are encoded, packed into one element
        // with a two-byte mask, and stored under the descriptor and element bounds.
        var shader = CompileTypedBufferAccess("TBufferStoreFormatXy", dwordCount: 2, typedFormat: 14);

        Assert.Contains("sharpemu_format_encode(v4, 8u, 0u, 3u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_format_encode(v5, 8u, 0u, 3u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("<< 8u), 0u, 0u, 0u), uint4(0xFFFFu, 0x0u, 0x0u, 0x0u));", shader.Source, StringComparison.Ordinal);
        Assert.Contains("2u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_store_bytes(b", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedBufferStoreOfWholeDwordsKeepsTheRegisterBits()
    {
        // Format 77 is 32x4 float: four aligned dword stores, no encoding, one bounds test.
        var shader = CompileTypedBufferAccess("TBufferStoreFormatXyzw", dwordCount: 4, typedFormat: 77);

        Assert.Contains(", v4);", shader.Source, StringComparison.Ordinal);
        Assert.Contains("+ 12u), v7);", shader.Source, StringComparison.Ordinal);
        Assert.Contains("16u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_format_encode(v[", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_store_element(b", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TBufferLoadFormatX", 1u, 4u)]
    [InlineData("TBufferLoadFormatXy", 2u, 8u)]
    [InlineData("TBufferStoreFormatX", 1u, 4u)]
    [InlineData("TBufferStoreFormatXy", 2u, 8u)]
    public void TypedBufferBoundsUseOnlyTransferredComponents(string opcode, uint componentCount, uint accessBytes)
    {
        var shader = CompileTypedBufferAccess(opcode, componentCount, typedFormat: 77);
        Assert.Contains($"{accessBytes}u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("16u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattedBufferStoreUsesDescriptorFormatForPackedNormals()
    {
        var shader = CompileTypedBufferAccess("BufferStoreFormatXyzw", 4, 0, typed: false, descriptorFormat: 50);

        // 2_10_10_10 is R10G10B10A2: ten-bit X, Y and Z, then the two-bit W.
        Assert.Contains("sharpemu_format_encode(v4, 10u, 0u, 9u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_format_encode(v5, 10u, 0u, 9u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_format_encode(v6, 10u, 0u, 9u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_format_encode(v7, 2u, 0u, 9u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("uint4(0xFFFFFFFFu, 0x0u, 0x0u, 0x0u)", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_store_bytes(b", shader.Source, StringComparison.Ordinal);
    }

    private static Gen5MslShader CompileTypedBufferAccess(string opcode, uint dwordCount, uint typedFormat,
        bool typed = true, uint descriptorFormat = 77)
    {
        var data = new Gen5Operand[dwordCount];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = Gen5Operand.Vector(4 + (uint)index);
        }

        var access = new Gen5ShaderInstruction(
            0,
            typed ? Gen5ShaderEncoding.Mtbuf : Gen5ShaderEncoding.Mubuf,
            opcode,
            [0, 0],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(8), Gen5Operand.Source(128, null)],
            data,
            new Gen5BufferMemoryControl(
                dwordCount,
                0,
                4,
                8,
                0,
                IndexEnabled: false,
                OffsetEnabled: false,
                Glc: false,
                Slc: false,
                Typed: typed,
                TypedFormat: typedFormat));
        var end = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);
        var scalars = new uint[256];
        scalars[8] = 0x2000;
        scalars[10] = 64;
        scalars[11] = descriptorFormat << 12;
        var shader = CompileMaterialized(new Gen5ShaderProgram(0, [access, end]), scalars[..12]);
        return shader;
    }

    [Fact]
    public void FormattedVertexFetchZeroFillsMissingComponents()
    {
        var fetch = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadFormatXyz",
            [],
            [],
            [],
            new Gen5BufferMemoryControl(
                3,
                5,
                0,
                0,
                0,
                IndexEnabled: true,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(0, [fetch, end]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, 1, 0, 0, new HashSet<uint> { 0 });
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            VertexInputs = [new ShaderVertexInput(0, 0, 2, 7, false, [])],
        };
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("v2 = 0u;", shader.Source, StringComparison.Ordinal);
    }

    private static Gen5MslShader CompileImageStore(
        uint dstSelect,
        uint dmask,
        string opcode = "ImageStore",
        uint unifiedFormat = 71u)
    {
        var control = new Gen5ImageControl(
            Dmask: dmask,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var store = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var userData = new uint[16];
        userData[8] = 0x20;
        userData[9] = unifiedFormat << 20;
        userData[11] = (9u << 28) | dstSelect;
        return CompileMaterialized(new Gen5ShaderProgram(0x1_0000_C000, [store, end]), userData);
    }
    private static Gen5MslShader CompileMaterialized(Gen5ShaderProgram program, uint[] userData)
    {
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1, 0, (uint)userData.Length);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, (uint)userData.Length), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }

}
