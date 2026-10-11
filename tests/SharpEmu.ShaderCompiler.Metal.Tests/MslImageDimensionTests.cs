// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Every guest image type crossed with every image opcode family the translator handles.
/// The structural assertions pin the Metal texture type and the coordinate shape; the
/// runtime theory then hands the same shaders to the OS Metal compiler, which is the only
/// authority on whether the generated accessor calls actually exist.
/// </summary>
public sealed class MslImageDimensionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ImageBvhIntersectRay")]
    [InlineData("ImageBvh64IntersectRay")]
    public void UnimplementedBvhIntersectionIsReportedRatherThanReturningAFalseMiss(string opcode)
    {
        Assert.False(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out _, out var error));
        Assert.Contains("BVH", error);
    }

    // The guest types that reach a sampled binding class.
    public static TheoryData<uint> SampledTypes() =>
    [
        Gen5ImageFixtures.ImageType1D,
        Gen5ImageFixtures.ImageType1DArray,
        Gen5ImageFixtures.ImageType2D,
        Gen5ImageFixtures.ImageType2DArray,
        Gen5ImageFixtures.ImageTypeCube,
        Gen5ImageFixtures.ImageType3D,
    ];

    // Storage classes exist for everything but multisampled images.
    public static TheoryData<uint> StorageTypes() =>
    [
        Gen5ImageFixtures.ImageType1D,
        Gen5ImageFixtures.ImageType1DArray,
        Gen5ImageFixtures.ImageType2D,
        Gen5ImageFixtures.ImageType2DArray,
        Gen5ImageFixtures.ImageTypeCube,
        Gen5ImageFixtures.ImageType3D,
    ];

    // The Metal texture type each guest type must be declared as. Cube maps are
    // addressed as 2D arrays because Gen5 shaders resolve the face themselves.
    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType1D, "texture2d<float>")]
    [InlineData(Gen5ImageFixtures.ImageType1DArray, "texture2d_array<float>")]
    [InlineData(Gen5ImageFixtures.ImageType2D, "texture2d<float>")]
    [InlineData(Gen5ImageFixtures.ImageType2DArray, "texture2d_array<float>")]
    [InlineData(Gen5ImageFixtures.ImageTypeCube, "texture2d_array<float>")]
    [InlineData(Gen5ImageFixtures.ImageType3D, "texture3d<float>")]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaa, "texture2d_ms<float>")]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaaArray, "texture2d_ms_array<float>")]
    public void SampledClass_DeclaresTheMetalTextureType(uint descriptorType, string expected)
    {
        var opcode = descriptorType is Gen5ImageFixtures.ImageType2DMsaa or Gen5ImageFixtures.ImageType2DMsaaArray
            ? "ImageLoad"
            : "ImageSampleLz";
        var shader = Gen5ImageFixtures.CompileOrThrow(opcode, descriptorType);
        Assert.Contains($"array<{expected}, 1>", shader.Source);
        Assert.DoesNotContain("texturecube", shader.Source);
    }

    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType1D, "texture2d<float, access::write>")]
    [InlineData(Gen5ImageFixtures.ImageType1DArray, "texture2d_array<float, access::write>")]
    [InlineData(Gen5ImageFixtures.ImageType2D, "texture2d<float, access::write>")]
    [InlineData(Gen5ImageFixtures.ImageType2DArray, "texture2d_array<float, access::write>")]
    [InlineData(Gen5ImageFixtures.ImageTypeCube, "texture2d_array<float, access::write>")]
    [InlineData(Gen5ImageFixtures.ImageType3D, "texture3d<float, access::write>")]
    public void StorageClass_DeclaresTheMetalTextureType(uint descriptorType, string expected)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageStore", descriptorType);
        Assert.Contains($"array<{expected}, 1>", shader.Source);
    }

    // The spatial coordinate rank follows the dimension, and an arrayed image passes the
    // layer as a separate integer argument rather than an extra coordinate component.
    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType1D, "sample(t", 1, false)]
    [InlineData(Gen5ImageFixtures.ImageType1DArray, "sample(t", 1, true)]
    [InlineData(Gen5ImageFixtures.ImageType2D, "sample(t", 2, false)]
    [InlineData(Gen5ImageFixtures.ImageType2DArray, "sample(t", 2, true)]
    [InlineData(Gen5ImageFixtures.ImageTypeCube, "sample(t", 2, true)]
    [InlineData(Gen5ImageFixtures.ImageType3D, "sample(t", 3, false)]
    public void SampleCoordinates_FollowTheDimension(
        uint descriptorType, string marker, int spatialComponents, bool arrayed)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageSampleLz", descriptorType);
        var line = Assert.Single(Lines(shader.Source, marker));
        var coordinateType = spatialComponents switch { 1 => "float2 ", 3 => "float3 ", _ => "float2 " };
        Assert.Contains(coordinateType, DeclarationOf(shader.Source, Argument(line, 1)));
        if (arrayed)
        {
            // get_array_size clamps the layer, which only the arrayed path emits.
            Assert.Contains("get_array_size()", shader.Source);
        }
        else
        {
            Assert.DoesNotContain("get_array_size()", shader.Source);
        }
    }

    [Theory]
    [MemberData(nameof(StorageTypes))]
    public void Store_WritesTheDimensionCoordinateAndLayer(uint descriptorType)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageStore", descriptorType);
        var write = Assert.Single(Lines(shader.Source, ".write("));
        var expectedCoordinate = descriptorType switch
        {
            Gen5ImageFixtures.ImageType1D or Gen5ImageFixtures.ImageType1DArray => "uint2((uint)",
            Gen5ImageFixtures.ImageType3D => "uint3(",
            _ => "uint2(",
        };
        Assert.Contains(expectedCoordinate, write);

        // Arrayed stores bound the layer against the array size before writing.
        var arrayed = descriptorType is Gen5ImageFixtures.ImageType1DArray
            or Gen5ImageFixtures.ImageType2DArray or Gen5ImageFixtures.ImageTypeCube;
        Assert.Equal(arrayed, shader.Source.Contains("get_array_size()", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SampledTypes))]
    public void Load_ReadsTheDimensionCoordinate(uint descriptorType)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageLoad", descriptorType);
        Assert.Single(Lines(shader.Source, ".read("));
        if (descriptorType == Gen5ImageFixtures.ImageType3D)
        {
            Assert.Contains("uint3(", shader.Source);
            Assert.DoesNotContain("clamp(as_type<int>", shader.Source);
        }
    }

    // A sampled mip load takes its level from the address component right after the
    // coordinates, so the operand index moves with the dimension.
    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType1D, 1)]
    [InlineData(Gen5ImageFixtures.ImageType1DArray, 2)]
    [InlineData(Gen5ImageFixtures.ImageType2D, 2)]
    [InlineData(Gen5ImageFixtures.ImageType2DArray, 3)]
    [InlineData(Gen5ImageFixtures.ImageType3D, 3)]
    public void LoadMip_TakesTheLevelAfterTheCoordinates(uint descriptorType, int expectedRegister)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageLoadMip", descriptorType);
        Assert.Contains($"= v{expectedRegister};", shader.Source);
    }

    // Multisampled textures carry no mip chain: the trailing operand is a sample index.
    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaa, 2)]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaaArray, 3)]
    public void MultisampledLoad_TakesASampleIndex(uint descriptorType, int expectedRegister)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageLoad", descriptorType);
        Assert.Contains($"= v{expectedRegister};", shader.Source);

        // get_width() on a multisampled texture takes no level argument.
        Assert.DoesNotContain("get_width(t", shader.Source);
    }

    // Metal multisampled textures expose read() and nothing else, so a sample is refused
    // by the translator. A multisampled store never gets that far: the binding layout has
    // no storage MSAA class to put it in.
    [Fact]
    public void MultisampledSample_IsRefusedByTheTranslator()
    {
        Assert.False(
            Gen5ImageFixtures.TryCompile("ImageSample", Gen5ImageFixtures.ImageType2DMsaa, out _, out var error),
            "Metal has no texture2d_ms::sample");
        Assert.Contains("multisampled", error);
        Assert.Contains("ImageSample", error);
    }

    [Fact]
    public void MultisampledStore_HasNoStorageBindingClass()
    {
        var failure = Assert.Throws<Resources.ResourcePlanException>(
            static () => Gen5ImageFixtures.TryCompile("ImageStore", Gen5ImageFixtures.ImageType2DMsaa, out _, out _));
        Assert.Contains("invalid binding class", failure.Message);
    }

    // Cube bodies are biased by -1 and their layer loses the two padding slices per cube,
    // matching the SPIR-V backend so both backends address a cube face identically.
    [Fact]
    public void CubeSample_BiasesTheBodyAndPacksTheLayer()
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageSampleLz", Gen5ImageFixtures.ImageTypeCube);
        Assert.Contains(") - 1.0f", shader.Source);
        Assert.Contains(">> 3u) << 1u)", shader.Source);
    }

    [Fact]
    public void NonCubeArraySample_RoundsTheLayerToTheNearestSlice()
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageSampleLz", Gen5ImageFixtures.ImageType2DArray);
        Assert.Contains("rint(", shader.Source);
        Assert.DoesNotContain(">> 3u) << 1u)", shader.Source);
    }

    // The integer path takes the raw layer: no cube remap, no rounding.
    [Fact]
    public void CubeLoad_DoesNotRemapTheLayer()
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageLoad", Gen5ImageFixtures.ImageTypeCube);
        Assert.DoesNotContain(">> 3u) << 1u)", shader.Source);
        Assert.DoesNotContain("rint(", shader.Source);
    }

    // A height-one 2D backing retains the guest LOD and derivatives.
    [Theory]
    [InlineData("ImageSampleL", "level(")]
    [InlineData("ImageSampleB", "bias(")]
    [InlineData("ImageSampleD", "gradient2d(")]
    public void OneDimensionalSample_RetainsTheLevelOperand(string opcode, string modifier)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(opcode, Gen5ImageFixtures.ImageType1D,
            stage: SharpEmu.ShaderCompiler.Resources.ShaderStage.Pixel);
        var line = Assert.Single(Lines(shader.Source, ".sample("));
        Assert.Contains(modifier, line);
        Assert.Contains("float2(", shader.Source);
    }

    // A volume sample needs gradient3d, not gradient2d.
    [Fact]
    public void VolumeGradientSample_UsesGradient3d()
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageSampleD", Gen5ImageFixtures.ImageType3D);
        Assert.Contains("gradient3d(", shader.Source);
    }

    [Fact]
    public void VolumeSampleOffset_FoldsThreeAxesIntoTheCoordinate()
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageSampleLzO", Gen5ImageFixtures.ImageType3D);
        Assert.Contains("extract_bits(", shader.Source);
        Assert.Contains("get_depth(", shader.Source);
    }

    // Metal exposes gather on 2D and 2D-array textures only.
    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType2D)]
    [InlineData(Gen5ImageFixtures.ImageType2DArray)]
    [InlineData(Gen5ImageFixtures.ImageTypeCube)]
    public void Gather_UsesTheHardwareInstructionWhereMetalHasIt(uint descriptorType)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageGather4", descriptorType);
        Assert.Contains(".gather(", shader.Source);
    }

    [Fact]
    public void OneDimensionalGatherLz_IsEmulatedFromTwoSamples()
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageGather4Lz", Gen5ImageFixtures.ImageType1D);
        Assert.DoesNotContain(".gather(", shader.Source);
        Assert.Equal(2, Lines(shader.Source, ".sample(").Count);
        Assert.Contains("floor(", shader.Source);
    }

    [Fact]
    public void HorizontalGather_TakesFourHorizontallyAdjacentTexels()
    {
        // GATHER4_H has no Metal instruction: the four texels the SPIR-V
        // translator reaches with constant offsets are point-sampled instead.
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageGather4H", Gen5ImageFixtures.ImageType2D);
        Assert.DoesNotContain(".gather(", shader.Source);
        Assert.Equal(4, Lines(shader.Source, ".sample(").Count);
        Assert.Contains("float2(-1.0f, 0.0f)", shader.Source);
        Assert.Contains("float2(2.0f, 0.0f)", shader.Source);
    }

    [Fact]
    public void HorizontalGather_OnAOneDimensionalImage_IsRefused()
    {
        Assert.False(
            Gen5ImageFixtures.TryCompile("ImageGather4H", Gen5ImageFixtures.ImageType1D, out _, out var error));
        Assert.Contains("1D horizontal image gather", error);
    }

    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType2D)]
    [InlineData(Gen5ImageFixtures.ImageType2DArray)]
    [InlineData(Gen5ImageFixtures.ImageType3D)]
    public void GetLod_ReportsTheClampedAndUnclampedLevels(uint descriptorType)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageGetLod", descriptorType);
        Assert.Contains("calculate_clamped_lod(", shader.Source);
        Assert.Contains("calculate_unclamped_lod(", shader.Source);
    }

    [Theory]
    // SAMPLE_CD is coarse derivatives, so it reads gradients and no reference.
    [InlineData("ImageSampleCd", true, false)]
    [InlineData("ImageSampleCdCl", true, false)]
    [InlineData("ImageSampleCdO", true, false)]
    // SAMPLE_C_CD is a compare that also reads gradients.
    [InlineData("ImageSampleCCd", true, true)]
    [InlineData("ImageSampleCCdCl", true, true)]
    // SAMPLE_CL is a LOD clamp, so it reads neither.
    [InlineData("ImageSampleCl", false, false)]
    [InlineData("ImageSampleClO", false, false)]
    // SAMPLE_C_L, named with a capital L, really is a compare.
    [InlineData("ImageSampleCL", false, true)]
    [InlineData("ImageSampleCLz", false, true)]
    // SAMPLE_C without a derivative family reads only the reference.
    [InlineData("ImageSampleC", false, true)]
    [InlineData("ImageSampleD", true, false)]
    public void CoarseDerivativeSamplesReadGradientsWithoutMistakingCdForACompare(
        string opcode, bool gradients, bool compare)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(opcode, Gen5ImageFixtures.ImageType2D);
        Assert.Equal(gradients, shader.Source.Contains("gradient2d(", StringComparison.Ordinal));
        // A colour-format image compares the filtered value, so the reference
        // shows up as the comparison rather than as a sample_compare argument.
        Assert.Equal(compare, shader.Source.Contains("<= (float)", StringComparison.Ordinal));
    }

    // A 32-bit single-channel uint image: the only class BindingLayout gives an
    // atomic binding, and the only one Metal's texture atomics accept.
    private const uint AtomicUintFormat = 20u;

    [Theory]
    // The ops with a Metal texture atomic of their own.
    [InlineData("ImageAtomicSwap", "atomic_exchange(")]
    [InlineData("ImageAtomicAdd", "atomic_fetch_add(")]
    [InlineData("ImageAtomicSub", "atomic_fetch_sub(")]
    [InlineData("ImageAtomicUmin", "atomic_fetch_min(")]
    [InlineData("ImageAtomicUmax", "atomic_fetch_max(")]
    [InlineData("ImageAtomicAnd", "atomic_fetch_and(")]
    [InlineData("ImageAtomicOr", "atomic_fetch_or(")]
    [InlineData("ImageAtomicXor", "atomic_fetch_xor(")]
    // The ops Metal has no member for take a compare-exchange loop.
    [InlineData("ImageAtomicSmin", "atomic_compare_exchange_weak(")]
    [InlineData("ImageAtomicSmax", "atomic_compare_exchange_weak(")]
    [InlineData("ImageAtomicInc", "atomic_compare_exchange_weak(")]
    [InlineData("ImageAtomicDec", "atomic_compare_exchange_weak(")]
    [InlineData("ImageAtomicCmpswap", "atomic_compare_exchange_weak(")]
    [InlineData("ImageAtomicFmin", "atomic_compare_exchange_weak(")]
    [InlineData("ImageAtomicFmax", "atomic_compare_exchange_weak(")]
    [InlineData("ImageAtomicFcmpswap", "atomic_compare_exchange_weak(")]
    public void StorageImageAtomicsUseTheMetalTextureAtomic(string opcode, string expected)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(
            opcode, Gen5ImageFixtures.ImageType2D, dmask: 1, unifiedFormat: AtomicUintFormat);
        // An atomic binding is read_write, which is what the members need.
        Assert.Contains("texture2d<uint, access::read_write>", shader.Source, StringComparison.Ordinal);
        Assert.Contains(expected, shader.Source, StringComparison.Ordinal);
        if (!MetalNative.IsAvailable)
        {
            Assert.False(
                OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
                "The required gate cannot compile a texture atomic without a Metal device.");
            output.WriteLine("No Metal device on this host; compile validation skipped.");
            return;
        }

        // The OS compiler is the authority on whether the member exists at the
        // language version the backend pins.
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var compileError),
            $"[{opcode}] Metal rejected the emitted texture atomic: {compileError}\n{shader.Source}");
    }

    [Theory]
    // Every dimension an atomic storage binding exists for.
    [InlineData(Gen5ImageFixtures.ImageType1D)]
    [InlineData(Gen5ImageFixtures.ImageType1DArray)]
    [InlineData(Gen5ImageFixtures.ImageType2D)]
    [InlineData(Gen5ImageFixtures.ImageType2DArray)]
    [InlineData(Gen5ImageFixtures.ImageTypeCube)]
    [InlineData(Gen5ImageFixtures.ImageType3D)]
    public void StorageImageAtomicsCompileForEveryAtomicCapableDimension(uint descriptorType)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(
            "ImageAtomicAdd", descriptorType, dmask: 1, unifiedFormat: AtomicUintFormat);
        if (!MetalNative.IsAvailable)
        {
            Assert.False(
                OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
                "The required gate cannot compile a texture atomic without a Metal device.");
            return;
        }

        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"[{Gen5ImageFixtures.TypeName(descriptorType)}] {error}\n{shader.Source}");
    }

    [Fact]
    public void StorageImageAtomicsNeedASingleSampledUintBinding()
    {
        // A multisampled image has no storage binding class at all, so an
        // atomic on one is refused while the layout is allocated -- before the
        // translator could emit a texture atomic Metal would reject.
        var failure = Assert.Throws<ResourcePlanException>(static () => Gen5ImageFixtures.TryCompile(
            "ImageAtomicAdd", Gen5ImageFixtures.ImageType2DMsaa, out _, out _,
            dmask: 1, unifiedFormat: AtomicUintFormat));
        Assert.Contains("invalid binding class", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeGather_IsRefusedWithTheDimensionInTheMessage()
    {
        Assert.False(
            Gen5ImageFixtures.TryCompile("ImageGather4", Gen5ImageFixtures.ImageType3D, out _, out var error),
            "Metal has no texture3d::gather");
        Assert.Contains("Dim3D", error);
        Assert.Contains("ImageGather4", error);
    }

    // The size query reports the spatial extent, then the layer count, then the mip count.
    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType1D, false, false)]
    [InlineData(Gen5ImageFixtures.ImageType1DArray, true, false)]
    [InlineData(Gen5ImageFixtures.ImageType2D, false, false)]
    [InlineData(Gen5ImageFixtures.ImageType2DArray, true, false)]
    [InlineData(Gen5ImageFixtures.ImageType3D, false, true)]
    public void Resinfo_ReportsTheShapeOfTheImage(uint descriptorType, bool arrayed, bool volume)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageGetResinfo", descriptorType);
        Assert.Contains("get_width(", shader.Source);
        Assert.Equal(arrayed, shader.Source.Contains("get_array_size()", StringComparison.Ordinal));
        Assert.Equal(volume, shader.Source.Contains("get_depth(", StringComparison.Ordinal));

        // The w channel of a full dmask is the mip count.
        Assert.Contains("get_num_mip_levels()", shader.Source);
    }

    [Fact]
    public void Resinfo_OnAStorageImage_ReportsASingleLevel()
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageGetResinfo", Gen5ImageFixtures.ImageType2DArray, unifiedFormat: 71u);
        Assert.Contains("get_width(", shader.Source);
    }

    // ---- the OS Metal compiler is the authority on the generated accessor calls ----

    public static TheoryData<string, uint> EveryDimensionAndOpcode()
    {
        var data = new TheoryData<string, uint>();
        string[] sampled =
        [
            "ImageSample", "ImageSampleL", "ImageSampleLz", "ImageSampleB", "ImageSampleD",
            "ImageSampleO", "ImageSampleLzO", "ImageSampleC", "ImageSampleCLz",
        ];
        foreach (var descriptorType in new[]
                 {
                     Gen5ImageFixtures.ImageType1D, Gen5ImageFixtures.ImageType1DArray,
                     Gen5ImageFixtures.ImageType2D, Gen5ImageFixtures.ImageType2DArray,
                     Gen5ImageFixtures.ImageTypeCube, Gen5ImageFixtures.ImageType3D,
                 })
        {
            foreach (var opcode in sampled)
            {
                data.Add(opcode, descriptorType);
            }

            data.Add("ImageLoad", descriptorType);
            data.Add("ImageLoadMip", descriptorType);
            data.Add("ImageStore", descriptorType);
            data.Add("ImageStoreMip", descriptorType);
            data.Add("ImageGetResinfo", descriptorType);
            data.Add("ImageGetLod", descriptorType);
            data.Add("ImageSampleCd", descriptorType);
            data.Add("ImageSampleCCd", descriptorType);
        }

        // Gather exists on 2D shapes only; the 1D form is emulated.
        data.Add("ImageGather4", Gen5ImageFixtures.ImageType2D);
        data.Add("ImageGather4", Gen5ImageFixtures.ImageType2DArray);
        data.Add("ImageGather4", Gen5ImageFixtures.ImageTypeCube);
        data.Add("ImageGather4Lz", Gen5ImageFixtures.ImageType1D);
        data.Add("ImageGather4C", Gen5ImageFixtures.ImageType2DArray);
        data.Add("ImageGather4H", Gen5ImageFixtures.ImageType2D);
        data.Add("ImageGather4H", Gen5ImageFixtures.ImageType2DArray);

        // Multisampled images only ever reach the fetch paths.
        data.Add("ImageLoad", Gen5ImageFixtures.ImageType2DMsaa);
        data.Add("ImageLoad", Gen5ImageFixtures.ImageType2DMsaaArray);
        data.Add("ImageGetResinfo", Gen5ImageFixtures.ImageType2DMsaa);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryDimensionAndOpcode))]
    public void EmittedImageAccessCompilesWithTheRuntimeMetalCompiler(string opcode, uint descriptorType)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(opcode, descriptorType);
        if (!MetalNative.IsAvailable)
        {
            Assert.False(
                OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
                "The required gate cannot compile MSL without a Metal device.");
            output.WriteLine("No Metal device on this host; compile validation skipped.");
            return;
        }

        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"[{opcode} on {Gen5ImageFixtures.TypeName(descriptorType)}] Metal rejected the emitted MSL: {error}\n{shader.Source}");
    }

    private static List<string> Lines(string source, string marker) =>
        [.. source.Split('\n').Where(line => line.Contains(marker, StringComparison.Ordinal))];

    // The nth comma-separated argument of a call, ignoring nesting (the emitter only ever
    // passes locals and simple casts, so a flat split is enough).
    private static string Argument(string line, int index)
    {
        var open = line.IndexOf('(', line.IndexOf(".sample(", StringComparison.Ordinal) + 1);
        var arguments = line[(open + 1)..].Split(',');
        return index < arguments.Length ? arguments[index].Trim().TrimEnd(')', ';') : string.Empty;
    }

    // The declaration line of a local, so a coordinate's static type can be asserted.
    private static string DeclarationOf(string source, string local) =>
        Lines(source, $" {local} = ").FirstOrDefault() ?? string.Empty;
}
