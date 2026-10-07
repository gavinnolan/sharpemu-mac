// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
    [InlineData(Gen5ImageFixtures.ImageType1D, "texture1d<float>")]
    [InlineData(Gen5ImageFixtures.ImageType1DArray, "texture1d_array<float>")]
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
    [InlineData(Gen5ImageFixtures.ImageType1D, "texture1d<float, access::write>")]
    [InlineData(Gen5ImageFixtures.ImageType1DArray, "texture1d_array<float, access::write>")]
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
        var coordinateType = spatialComponents switch { 1 => "float ", 3 => "float3 ", _ => "float2 " };
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
            Gen5ImageFixtures.ImageType1D or Gen5ImageFixtures.ImageType1DArray => "(uint)",
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
            // A volume load clamps against the depth as well as width and height.
            Assert.Contains("get_depth(", shader.Source);
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
        Assert.Contains($"= v[{expectedRegister}];", shader.Source);
    }

    // Multisampled textures carry no mip chain: the trailing operand is a sample index.
    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaa, 2)]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaaArray, 3)]
    public void MultisampledLoad_TakesASampleIndex(uint descriptorType, int expectedRegister)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow("ImageLoad", descriptorType);
        Assert.Contains($"= v[{expectedRegister}];", shader.Source);

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

    // Metal has no LOD, bias or gradient form of texture1d::sample, and a 1D image has a
    // single level, so the modifier is dropped rather than refused.
    [Theory]
    [InlineData("ImageSampleL")]
    [InlineData("ImageSampleB")]
    [InlineData("ImageSampleD")]
    public void OneDimensionalSample_DropsTheLevelOperand(string opcode)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(opcode, Gen5ImageFixtures.ImageType1D);
        var line = Assert.Single(Lines(shader.Source, ".sample("));
        Assert.DoesNotContain("level(", line);
        Assert.DoesNotContain("bias(", line);
        Assert.DoesNotContain("gradient", line);
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
        }

        // Gather exists on 2D shapes only; the 1D form is emulated.
        data.Add("ImageGather4", Gen5ImageFixtures.ImageType2D);
        data.Add("ImageGather4", Gen5ImageFixtures.ImageType2DArray);
        data.Add("ImageGather4", Gen5ImageFixtures.ImageTypeCube);
        data.Add("ImageGather4Lz", Gen5ImageFixtures.ImageType1D);
        data.Add("ImageGather4C", Gen5ImageFixtures.ImageType2DArray);

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
