// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalTextureSourceRuntimeDrainTests
{
    private static GuestRenderTarget ControlColorTarget(ulong address = Address, uint mask = 0xF) =>
        new(address, 4, 4, 10, 0,
            Registers: new ColorTargetWords(address, 0, 10u << 2, 0, 3u | 3u << 14, 1u << 24, 0, 0, 0, 0, 0), WriteMask: mask);
    private static object LastQueuedWork()
    {
        var pending = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<IEnumerable>()
            .SelectMany(static queue => queue.Cast<object>())
            .OrderBy(item => (long)item.GetType().GetProperty("Sequence")!.GetValue(item)!).Last();
        return pending.GetType().GetProperty("Work")!.GetValue(pending)!;
    }
    private static void SubmitControlColor(GuestRenderTarget[] targets, uint vertices = 6, uint instances = 1, uint primitive = 4,
        GuestRenderState? render = null, GuestDepthTarget? depth = null, GuestDrawTexture[]? textures = null, GuestVertexBuffer[]? streams = null, GuestIndexBuffer? indices = null, bool noVertexShader = false) =>
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(new(new Gen5MslShader("", "managed color pipeline never compiled", Gen5MslStage.Pixel, 0)),
            textures ?? [], [], 0, targets, noVertexShader ? null : new(new Gen5MslShader("", "managed vertex never compiled", Gen5MslStage.Vertex, 0)), vertices, instances, primitive, indices, streams, render, depth, 0);

    [Theory]
    [InlineData("depth")]
    [InlineData("zero_target")]
    [InlineData("multiple_targets")]
    [InlineData("empty_mask")]
    [InlineData("mismatched_mask")]
    [InlineData("empty_vertices")]
    [InlineData("empty_instances")]
    [InlineData("unsupported_primitive")]
    [InlineData("storage_writer")]
    [InlineData("indexed")]
    [InlineData("missing_vertex_shader")]
    public void ActualColorDrainRejectsUnqualifiedWriterBeforeNativeMutation(string invalid)
    {
        using var state = new State();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        var target = ControlColorTarget(mask: invalid == "empty_mask" ? 0u : invalid == "mismatched_mask" ? 2u : 0xFu);
        if (invalid == "zero_target") target = target with { Address = 0 };
        var targets = invalid == "multiple_targets" ? new[] { target, target } : new[] { target };
        var storage = new GuestDrawTexture(0, 1, 1, 10, 0, new byte[4], true, true,
            Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
        SubmitControlColor(targets, vertices: invalid == "empty_vertices" ? 0u : 6u,
            instances: invalid == "empty_instances" ? 0u : 1u, primitive: invalid == "unsupported_primitive" ? 1u : 4u,
            depth: invalid == "depth" ? new GuestDepthTarget(Address + 0x1000, Address + 0x1000, 4, 4, 3, 0, 1, false) : null,
            textures: invalid == "storage_writer" ? [storage] : [],
            indices: invalid == "indexed" ? new GuestIndexBuffer(new byte[12], 6, false, false) : null,
            noVertexShader: invalid == "missing_vertex_shader");
        var work = LastQueuedWork();
        var owner = Owner(work); var sources = (Array)owner.GetType().GetProperty("Outputs")!.GetValue(owner)!;
        var failure = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Single(reports);
        foreach (var source in sources)
        {
            Assert.False((bool)Get(source!, "Completed")!); Assert.True((bool)Get(source!, "Failed")!);
            Assert.Equal((nint)0, Get(source!, "Texture")); Assert.Equal((nint)0, Get(source!, "ImmutableTexture"));
        }
        Assert.Equal(0UL, Field("_immutableTextureSourceBytes").GetValue(null));
        Assert.False((bool)Field("_batchOpen").GetValue(null)!);
        Assert.Empty((IDictionary)Field("_textureSourceNativeBatches").GetValue(null)!);
    }

    [Fact]
    public void PendingCpuSeedCannotBeDiscardedByFirstUseColorClearAdmission()
    {
        using var state = new State();
        var seeds = (IDictionary)Field("_pendingGuestImageInitialData").GetValue(null)!;
        var bytes = Enumerable.Repeat((byte)0x5A, 64).ToArray(); seeds.Add(Address, bytes);
        try
        {
            var error = Assert.Throws<NotSupportedException>(() => SubmitControlColor([ControlColorTarget()]));
            Assert.Contains("pending CPU initialization", error.Message); Assert.Same(bytes, seeds[Address]);
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0UL, Field("_immutableTextureSourceBytes").GetValue(null));
        }
        finally { seeds.Remove(Address); }
    }

    [Fact]
    public void ActualColorPipelineFailureCannotMaterializeOrCertifyOutput()
    {
        using var state = new State();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        // Actual TryGetDrawPipeline fails its real unique vertex-slot bound
        // before any native call. No synthetic successful pipeline is injected.
        var streams = Enumerable.Range(0, 6).Select(index =>
            new GuestVertexBuffer((uint)index, 4, 4, 7, (ulong)(index + 1) * 0x1000, 16, 0, [], 0, false)).ToArray();
        SubmitControlColor([ControlColorTarget()], streams: streams);
        var source = ((IDictionary)Field("_latestTextureSources").GetValue(null)!)[Address]!;
        var failure = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Contains("pipeline creation failed", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        Assert.Single(reports); Assert.False((bool)Get(source, "Completed")!); Assert.True((bool)Get(source, "Failed")!);
        Assert.Equal((nint)0, Get(source, "Texture")); Assert.Equal((nint)0, Get(source, "ImmutableTexture"));
        Assert.False(((IDictionary)Field("_guestImages").GetValue(null)!).Contains(Address));
        Assert.Equal(0UL, Field("_immutableTextureSourceBytes").GetValue(null));
    }

    [Fact]
    public void InitializedLiveContentWithoutGpuPredecessorCannotBeRelabeledAsClear()
    {
        using var state = new State();
        var registry = (IDictionary)Field("_guestImages").GetValue(null)!;
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, true)!;
        imageType.GetField("Width")!.SetValue(image, 4u); imageType.GetField("Height")!.SetValue(image, 4u);
        imageType.GetField("Initialized")!.SetValue(image, true);
        imageType.GetField("Format")!.SetValue(image, MtlPixelFormat.Rgba8Unorm);
        registry.Add(Address, image);
        try
        {
            var error = Assert.Throws<NotSupportedException>(() => SubmitControlColor([ControlColorTarget()]));
            Assert.Contains("initialized contents", error.Message); Assert.Same(image, registry[Address]);
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0UL, Field("_immutableTextureSourceBytes").GetValue(null));
        }
        finally { registry.Remove(Address); }
    }

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("type")]
    [InlineData("depth")]
    [InlineData("format")]
    [InlineData("missing_handle")]
    public void ReservedColorAttachmentRejectsMismatchedActualMetadataBeforeNativeLookup(string invalid)
    {
        using var state = new State(); SubmitControlColor([ControlColorTarget()]);
        var work = LastQueuedWork();
        var sources = (Array)Owner(work).GetType().GetProperty("Outputs")!.GetValue(Owner(work))!;
        var source = sources.GetValue(0)!;
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, true)!;
        imageType.GetField("Width")!.SetValue(image, invalid == "width" ? 8u : 4u);
        imageType.GetField("Height")!.SetValue(image, invalid == "height" ? 8u : 4u);
        imageType.GetField("Depth")!.SetValue(image, invalid == "depth" ? 2u : 1u);
        imageType.GetField("TextureType")!.SetValue(image, invalid == "type" ? 7u : 2u);
        imageType.GetField("Format")!.SetValue(image, invalid == "format" ? MtlPixelFormat.R16Float : MtlPixelFormat.Rgba8Unorm);
        source.GetType().GetField("Image")!.SetValue(source, image);
        Field("_executingTextureSourceWork").SetValue(null, work);
        try
        {
            // Handles remain0. Mismatch/zero-handle checks reject before any
            // objc/native texture query; this is metadata rejection only.
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => Call("ValidateReservedColorTextureSourceTarget", work, image));
            Assert.IsType<NotSupportedException>(failure.InnerException);
            Assert.False((bool)Get(source, "Completed")!); Assert.Equal((nint)0, Get(source, "Texture"));
        }
        finally
        {
            Field("_executingTextureSourceWork").SetValue(null, null);
            source.GetType().GetField("Image")!.SetValue(source, null);
        }
    }

    [Fact]
    public void ActualPresentationWriterRejectsBeforeTransientNativeAllocation()
    {
        using var state = new State();
        MetalVideoPresenter.SubmitTranslatedDraw(new(new Gen5MslShader("", "managed presentation never compiled", Gen5MslStage.Pixel, 0)),
            [], [], 4, 4, 0, null, 6, 1, 4, null, null, null);
        var presentation = Field("_latestPresentation").GetValue(null); Assert.NotNull(presentation);
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => Call("ExecutePresentationDraw", (nint)0, (nint)0, presentation));
        Assert.Contains("presentation/transient", Assert.IsType<NotSupportedException>(error.InnerException).Message);
        Assert.False((bool)Field("_batchOpen").GetValue(null)!);
    }
}
