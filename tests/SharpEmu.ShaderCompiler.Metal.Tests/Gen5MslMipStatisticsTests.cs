// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A pixel sample of an eligible image carries the recording into the GET_LOD_STATS counter
/// bank (Templates/mip_stats_prelude.msl), guarded at run time by the image's shader-data
/// field, so the bound T#'s MIP_STATS_ENABLE does not change the program; with lod
/// statistics off a program carries neither the binding nor the atomics.
/// </summary>
// These tests set the process-wide ResourceMaterializer.RecordMipStatistics; nothing else
// may apply a specialization while they do.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MipStatisticsSwitchCollection
{
    public const string Name = "MipStatisticsSwitch";
}

[Collection(MipStatisticsSwitchCollection.Name)]
public sealed class Gen5MslMipStatisticsTests(ITestOutputHelper output)
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint UserDataCount = 20;

    private static Gen5ShaderProgram SampleProgram(string opcode)
    {
        var control = new Gen5ImageControl(0xF, 0, Enumerable.Range(0, 12).Select(static value => (uint)value).ToArray(),
            16, 8, 16, Gen5ImageFixtures.Dim2D, false, false, false, false, false);
        return new Gen5ShaderProgram(ShaderAddress,
        [
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mimg, opcode, [], [], [], control),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        ]);
    }

    private static uint[] SampleUserData(bool mipStatsEnabled)
    {
        var userData = new uint[UserDataCount];
        userData[8] = 0x20;
        userData[9] = 71u << 20;
        userData[11] = (Gen5ImageFixtures.ImageType2D << 28) | Gen5ShaderTranslator.IdentityImageDstSelect;
        userData[13] = mipStatsEnabled ? 1u << 25 : 0u;
        userData[14] = 7;
        return userData;
    }

    // recordStatistics is the host's switch (ResourceMaterializer.RecordMipStatistics);
    // mipStatsEnabled is the bound T#'s MIP_STATS_ENABLE.
    private static Gen5MslShader PixelSample(string opcode, bool recordStatistics, bool mipStatsEnabled = true)
    {
        var program = SampleProgram(opcode);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, 0x4D49505354415453UL, 0, UserDataCount);
        var userData = SampleUserData(mipStatsEnabled);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), $"{failure}");
        var previous = ResourceMaterializer.RecordMipStatistics;
        ResourceMaterializer.RecordMipStatistics = recordStatistics;
        SpecializedResourceInfo resources;
        try
        {
            resources = ResourceMaterializer.ApplyTo(plan, specialization);
        }
        finally
        {
            ResourceMaterializer.RecordMipStatistics = previous;
        }

        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, UserDataCount),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.Equal(recordStatistics ? 1u : 0u, layout.MipStatisticsCount);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader!;
    }

    [Theory]
    [InlineData("ImageSample", "calculate_unclamped_lod(")]
    [InlineData("ImageSampleB", "calculate_unclamped_lod(")]
    [InlineData("ImageSampleL", null)]
    [InlineData("ImageSampleLz", null)]
    [InlineData("ImageSampleD", "log2(max(dot(")]
    public void ASampleOfACountedImageRecordsItsRequestedLod(string opcode, string? lodSource)
    {
        var shader = PixelSample(opcode, recordStatistics: true);

        var field = shader.ArgumentLayout!.Find(DescriptorBindingKind.MipStatistics, MslArgumentFieldKind.Pointer);
        Assert.NotNull(field);
        Assert.Equal(Gen5MslArgumentLayout.MipStatisticsName, field.Name);
        Assert.Contains($"device atomic_uint* {Gen5MslArgumentLayout.MipStatisticsName} [[id({field.FirstId})]]", shader.Source);
        Assert.Contains("static inline void sharpemu_mip_stats(", shader.Source);
        Assert.Contains($"sharpemu_mip_stats(sharpemu_resources.{Gen5MslArgumentLayout.MipStatisticsName}, ", shader.Source);
        Assert.Contains("!simd_is_helper_thread()", shader.Source);
        Assert.Contains("atomic_fetch_min_explicit(", shader.Source);
        // The slot's field is tested first; the LOD work and the call sit behind it.
        var guard = shader.Source.IndexOf(" & 0x8000u) == 0u)", StringComparison.Ordinal);
        Assert.True(guard >= 0, shader.Source);
        Assert.True(shader.Source.IndexOf($"sharpemu_mip_stats(sharpemu_resources.", StringComparison.Ordinal) > guard);
        if (lodSource is null)
        {
            Assert.DoesNotContain("calculate_unclamped_lod(", shader.Source);
        }
        else
        {
            Assert.True(shader.Source.IndexOf(lodSource, StringComparison.Ordinal) > guard, shader.Source);
        }

        CompileWhenRequired(shader.Source);
    }

    [Fact]
    public void MipStatsEnableDoesNotSplitTheProgram()
    {
        var plan = ShaderResourcePlan.Extract(SampleProgram("ImageSample"), ShaderStage.Pixel, 0x4D49505354415453UL, 0, UserDataCount);
        ResourceSpecialization Materialize(bool enabled)
        {
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = SampleUserData(enabled) },
                ref snapshot, ref specialization, out var failure), $"{failure}");
            return specialization;
        }

        var counted = Materialize(enabled: true);
        var uncounted = Materialize(enabled: false);
        Assert.Equal(counted, uncounted);
        Assert.Equal(counted.GetHashCode(), uncounted.GetHashCode());

        var countedSource = PixelSample("ImageSample", recordStatistics: true, mipStatsEnabled: true).Source;
        Assert.Equal(countedSource, PixelSample("ImageSample", recordStatistics: true, mipStatsEnabled: false).Source);
        Assert.Contains("sharpemu_mip_stats(sharpemu_resources.", countedSource);
    }

    [Fact]
    public void ASampleOfAnUncountedImageCarriesNoCounters()
    {
        var shader = PixelSample("ImageSample", recordStatistics: false);

        Assert.Null(shader.ArgumentLayout!.Find(DescriptorBindingKind.MipStatistics, MslArgumentFieldKind.Pointer));
        Assert.DoesNotContain(Gen5MslArgumentLayout.MipStatisticsName, shader.Source);
        Assert.DoesNotContain("sharpemu_mip_stats", shader.Source);
        Assert.DoesNotContain("atomic_fetch_min_explicit(", shader.Source);
    }

    private void CompileWhenRequired(string source)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Source-only mip statistics fixture; no runtime compiler.");
            return;
        }

        Assert.True(MetalNative.IsAvailable, "Required native compilation needs a Metal device.");
        nint library = 0;
        try
        {
            Assert.True(MetalNative.TryCompileLibrary(source, out library, out var error), error + "\n" + source);
        }
        finally
        {
            if (library != 0)
            {
                MetalNative.ReleaseCollisionLibrary(library);
            }
        }
    }
}
