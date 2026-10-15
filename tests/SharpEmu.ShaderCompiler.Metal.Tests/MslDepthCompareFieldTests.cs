// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class MslDepthCompareFieldTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryImageKindHasUniqueFieldNameWithoutChangingItsIdOffsetOrType()
    {
        var kinds = Enumerable.Range((int)BindingLayout.FirstImageBinding, (int)BindingLayout.ImageBindingCount)
            .Select(static value => (DescriptorBindingKind)value).ToArray();
        var layout = Gen5MslArgumentLayout.Build(new BindingLayout
        {
            Descriptors = kinds.Select(static kind => new DescriptorBinding(kind, [0])).ToArray(),
        });
        // Every layout also carries the trailing step-report and pair-lock pointers, which
        // are not guest descriptors and must never move an image field's id or byte offset.
        Assert.Equal(kinds.Length + 2, layout.Fields.Count);
        Assert.Equal(kinds.Length + 2, layout.Fields.Select(static field => field.Name).Distinct().Count());
        Assert.Equal((uint)(kinds.Length + 2) * 8, layout.ByteSize);
        var report = layout.Fields[^2];
        Assert.Equal(MslArgumentFieldKind.StepReport, report.FieldKind);
        Assert.Equal(Gen5MslArgumentLayout.StepReportName, report.Name);
        Assert.Equal((uint)kinds.Length, report.FirstId);
        Assert.Equal((uint)kinds.Length * 8, report.ByteOffset);
        var pairLocks = layout.Fields[^1];
        Assert.Equal(MslArgumentFieldKind.PairLocks, pairLocks.FieldKind);
        Assert.Equal(Gen5MslArgumentLayout.PairLocksName, pairLocks.Name);
        Assert.Equal((uint)kinds.Length + 1, pairLocks.FirstId);
        Assert.Equal((uint)(kinds.Length + 1) * 8, pairLocks.ByteOffset);
        for (var index = 0; index < kinds.Length; index++)
        {
            var field = layout.Fields[index];
            Assert.Equal(kinds[index], field.Kind); Assert.Equal((uint)index, field.FirstId);
            Assert.Equal((uint)index * 8, field.ByteOffset); Assert.Equal(8u, field.ByteSize); Assert.Equal(1u, field.Count);
            Assert.Equal(MslArgumentFieldKind.Textures, field.FieldKind);
            Assert.Equal(Gen5MslArgumentLayout.ImageClassName(kinds[index]), field.Name);
            var (resource, numeric, dimension, _) = ImageDescriptorBinding.Describe(kinds[index]);
            Assert.NotEqual(ImageResourceClass.None, resource); Assert.NotEqual(ImageNumericClass.Unsupported, numeric);
            Assert.NotEqual(ImageDimension.Unknown, dimension);
            Assert.Equal(ImageDescriptorBinding.IsDepthCompare(kinds[index]), field.Name.StartsWith("sampled_compare_", StringComparison.Ordinal));
        }
    }

    private static Gen5MslShader MixedColorCompareShader()
    {
        var control = new Gen5ImageControl(0xF, 0, Enumerable.Range(0, 12).Select(static value => (uint)value).ToArray(),
            16, 8, 16, Gen5ImageFixtures.Dim2D, false, false, false, false, false);
        var program = new Gen5ShaderProgram(0x10000C000UL, new[]
        {
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mimg, "ImageSampleLz", [], [], [], control),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Mimg, "ImageSampleCLz", [], [], [], control with { ScalarResource = 24 }),
            new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        });
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, 0x534844524546UL, 0, 40);
        var userData = new uint[40];
        foreach (var scalar in new[] { 8, 24 })
        {
            userData[scalar] = scalar == 8 ? 0x20u : 0x40u;
            userData[scalar + 1] = 22u << 20; // Canonical Float R32 descriptor.
            userData[scalar + 3] = 9u << 28 | Gen5ShaderTranslator.IdentityImageDstSelect;
        }
        var snapshot = new ResourceSnapshot(); var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), $"Descriptor materialization failed: {failure}");
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(2, resources.Info.Images.Count);
        Assert.Single(resources.Info.Images, static image => image.DepthCompare);
        Assert.Single(resources.Info.Images, static image => !image.DepthCompare);
        var binding = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 40),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, binding)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader!;
    }

    [Fact]
    public void ActualNormalizedMixedColorAndDrefEmitDistinctFieldsAndConsistentBodyLookups()
    {
        var shader = MixedColorCompareShader();
        var fields = shader.ArgumentLayout!.Fields.Where(static field => field.FieldKind == MslArgumentFieldKind.Textures).ToArray();
        Assert.Equal(2, fields.Length); Assert.Equal(2, fields.Select(static field => field.Name).Distinct().Count());
        Assert.Contains(fields, static field => field.Name == "sampled_float_2d");
        Assert.Contains(fields, static field => field.Name == "sampled_compare_float_2d");
        foreach (var field in fields)
        {
            var type = ImageDescriptorBinding.IsDepthCompare(field.Kind) ? "depth2d<float>" : "texture2d<float>";
            Assert.Contains($"array<{type}, 1> {field.Name} [[id({field.FirstId})]]", shader.Source);
            Assert.Contains("sharpemu_resources." + field.Name, shader.Source);
        }
        Assert.Contains(".sample_compare(", shader.Source);
    }

    [Theory]
    [InlineData("ImageSampleCL", 9u, ".sample_compare(", "metal::level(")]
    [InlineData("ImageSampleCD", 9u, ".sample_compare(", "metal::gradient2d(")]
    [InlineData("ImageSampleCB", 9u, ".sample_compare(", "metal::bias(")]
    [InlineData("ImageSampleCLzO", 13u, ".sample_compare(", "metal::level(")]
    [InlineData("ImageGather4C", 13u, ".gather_compare(", "int2(0)")]
    public void NativeDepthComparisonUsesCorrectOperandFamilyAndCompiles(string opcode, uint type, string operation, string operand)
    {
        Assert.True(Gen5ImageFixtures.TryCompile(opcode, type, out var shader, out var error,
            unifiedFormat: 22, stage: ShaderStage.Pixel), error);
        Assert.Contains(type == 9 ? "depth2d<float>" : "depth2d_array<float>", shader!.Source);
        Assert.Contains(operation, shader.Source); Assert.Contains(operand, shader.Source);
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1") return;
        nint library = 0;
        try
        {
            Assert.True(MetalNative.IsAvailable, "Required depth compiler needs a device.");
            Assert.True(MetalNative.TryCompileLibrary(shader.Source, out library, out error), error + "\n" + shader.Source);
        }
        finally { if (library != 0) MetalNative.ReleaseCollisionLibrary(library); }
    }

    [Fact]
    public void ActualNormalizedMixedColorAndDrefFragmentCompilesWithRuntimeMetalCompiler()
    {
        var shader = MixedColorCompareShader();
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Source-only mixed color/Dref fixture; no runtime compiler/device execution.");
            return;
        }
        Assert.True(MetalNative.IsAvailable, "Required native compiler needs a Metal device.");
        nint library = 0;
        try
        {
            Assert.True(MetalNative.TryCompileLibrary(shader.Source, out library, out var error), error + "\n" + shader.Source);
            Assert.NotEqual((nint)0, library);
            output.WriteLine("Actual runtime Metal fragment compiler accepted normalized ordinary Float2D and separate Dref2D argument arrays with distinct names, native depth type and unchanged IDs/offsets.");
        }
        finally { if (library != 0) MetalNative.ReleaseCollisionLibrary(library); }
    }
}
