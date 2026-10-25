// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// A pass whose colour targets have fewer samples than its depth target. Prospero rasterises
/// such a pass (EQAA): Ghost of Tsushima's checkerboard G-buffer writes one colour sample per
/// pixel against two depth samples. Metal wants every attachment at one sample count, so the
/// pass is drawn against a one-sample proxy of the depth target instead: before it, sample 0
/// of each pixel is copied into the proxy (unless the pass clears everything it attaches), and
/// after it, the proxy is written back to every sample. The depth test then sees the prepass,
/// the pass's own depth lands where lighting reads it, and the colour targets are untouched.
/// Dropping the depth attachment instead (SHARPEMU_MIXED_SAMPLES=drop-depth) drew the G-buffer
/// with no depth test and left the prepass depth behind it, so lighting took the katana, the
/// grass field and nearly all of the island for sky.
/// </summary>
internal sealed partial class MetalRenderHost
{
    private const string MixedDepthVertex = "sharpemu_mixed_depth_vs";

    private const string MixedDepthSource = """
        #include <metal_stdlib>
        using namespace metal;

        struct sharpemu_mixed_depth_position
        {
            float4 position [[position]];
        };

        vertex sharpemu_mixed_depth_position sharpemu_mixed_depth_vs(uint vertex_id [[vertex_id]])
        {
            const float2 corner = float2(float((vertex_id << 1) & 2u), float(vertex_id & 2u));
            sharpemu_mixed_depth_position out;
            out.position = float4(corner * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            return out;
        }

        struct sharpemu_mixed_depth_out
        {
            float depth [[depth(any)]];
        };

        struct sharpemu_mixed_stencil_out
        {
            uint stencil [[stencil]];
        };

        struct sharpemu_mixed_depth_stencil_out
        {
            float depth [[depth(any)]];
            uint stencil [[stencil]];
        };

        // Into the proxy: sample 0 of each pixel.
        fragment sharpemu_mixed_depth_out sharpemu_mixed_depth_in(
            sharpemu_mixed_depth_position in [[stage_in]], depth2d_ms<float> source [[texture(0)]])
        {
            return { source.read(uint2(in.position.xy), 0) };
        }

        fragment sharpemu_mixed_stencil_out sharpemu_mixed_stencil_in(
            sharpemu_mixed_depth_position in [[stage_in]], texture2d_ms<uint> stencil [[texture(1)]])
        {
            return { stencil.read(uint2(in.position.xy), 0).r };
        }

        fragment sharpemu_mixed_depth_stencil_out sharpemu_mixed_depth_stencil_in(
            sharpemu_mixed_depth_position in [[stage_in]], depth2d_ms<float> source [[texture(0)]],
            texture2d_ms<uint> stencil [[texture(1)]])
        {
            const uint2 pixel = uint2(in.position.xy);
            return { source.read(pixel, 0), stencil.read(pixel, 0).r };
        }

        // Back out of it: one value per pixel, which the multisampled target takes for each sample.
        fragment sharpemu_mixed_depth_out sharpemu_mixed_depth_out_fn(
            sharpemu_mixed_depth_position in [[stage_in]], depth2d<float> source [[texture(0)]])
        {
            return { source.read(uint2(in.position.xy)) };
        }

        fragment sharpemu_mixed_stencil_out sharpemu_mixed_stencil_out_fn(
            sharpemu_mixed_depth_position in [[stage_in]], texture2d<uint> stencil [[texture(1)]])
        {
            return { stencil.read(uint2(in.position.xy)).r };
        }

        fragment sharpemu_mixed_depth_stencil_out sharpemu_mixed_depth_stencil_out_fn(
            sharpemu_mixed_depth_position in [[stage_in]], depth2d<float> source [[texture(0)]],
            texture2d<uint> stencil [[texture(1)]])
        {
            const uint2 pixel = uint2(in.position.xy);
            return { source.read(pixel), stencil.read(pixel).r };
        }
        """;

    private const ulong StencilViewFormatX32 = 261;
    private const nuint CompareFunctionAlways = 7;
    private const nuint StencilOperationReplace = 2;
    private const ulong TextureType2DMultisample = 4;

    private static readonly nint SelSampleCount = MetalNative.Selector("sampleCount");
    private static readonly nint SelTextureWidth = MetalNative.Selector("width");
    private static readonly nint SelTextureHeight = MetalNative.Selector("height");
    private static readonly nint SelNewTextureView = MetalNative.Selector("newTextureViewWithPixelFormat:");

    private nint _mixedDepthLibrary;
    private readonly Dictionary<(ulong Format, uint Samples, bool Into), nint> _mixedDepthPipelines = [];
    private nint _mixedDepthStencilState;
    private readonly Dictionary<(ulong Format, uint Width, uint Height), nint> _mixedDepthProxies = [];
    private readonly Dictionary<nint, nint> _mixedStencilViews = [];
    private long _mixedDepthPasses;

    // The open pass's proxy: the multisampled target it stands for, at which level and slice.
    private nint _mixedDepthTarget;
    private nint _mixedDepthProxy;
    private ulong _mixedDepthFormat;
    private uint _mixedDepthSamples;
    private uint _mixedDepthLevel;
    private uint _mixedDepthSlice;

    public bool SupportsMixedDepthSamples => true;

    /// <summary>Passes drawn against a one-sample proxy of their depth target since the last call.</summary>
    internal long TakeMixedDepthPasses() => Interlocked.Exchange(ref _mixedDepthPasses, 0);

    private static bool FormatHasDepth(ulong format) =>
        format is MtlDepthFormats.Depth16Unorm or MtlDepthFormats.Depth32Float or MtlDepthFormats.Depth32FloatStencil8;

    private static bool FormatHasStencil(ulong format) =>
        format is MtlDepthFormats.Stencil8 or MtlDepthFormats.Depth32FloatStencil8;

    /// <summary>
    /// The depth texture this pass attaches: the target itself, or - when it has more samples
    /// than the pass rasterises with - a one-sample proxy, filled from the target here unless
    /// the pass clears every plane it attaches. Called with no encoder open.
    /// </summary>
    private nint AttachDepthTexture(nint commandBuffer, nint target, uint passSamples, bool clearsDepth, bool clearsStencil,
        ref uint level, ref uint slice)
    {
        _mixedDepthTarget = 0;
        _mixedDepthProxy = 0;
        if (target == 0)
        {
            return target;
        }

        var targetSamples = (uint)MetalV2Native.SendValue(target, SelSampleCount);
        if (targetSamples <= Math.Max(passSamples, 1u) ||
            (ulong)MetalV2Native.SendValue(target, SelTextureType) != TextureType2DMultisample)
        {
            return target;
        }

        var format = (ulong)MetalV2Native.SendValue(target, SelPixelFormat);
        if (!FormatHasDepth(format) && !FormatHasStencil(format))
        {
            throw Fatal($"A pass rasterising with {passSamples} sample(s) attaches a {targetSamples}-sample depth target of format {format}, which has no depth or stencil plane to stand in for.");
        }

        var width = (uint)MetalV2Native.SendValue(target, SelTextureWidth);
        var height = (uint)MetalV2Native.SendValue(target, SelTextureHeight);
        var proxy = MixedDepthProxy(format, width, height);
        var hasDepth = FormatHasDepth(format);
        var hasStencil = FormatHasStencil(format);
        if ((hasDepth && !clearsDepth) || (hasStencil && !clearsStencil))
        {
            EncodeMixedDepthCopy(commandBuffer, source: target, destination: proxy, format, targetSamples, into: true, 0, 0);
        }

        _mixedDepthTarget = target;
        _mixedDepthProxy = proxy;
        _mixedDepthFormat = format;
        _mixedDepthSamples = targetSamples;
        _mixedDepthLevel = level;
        _mixedDepthSlice = slice;
        level = 0;
        slice = 0;
        Interlocked.Increment(ref _mixedDepthPasses);
        return proxy;
    }

    /// <summary>After a pass drawn against a proxy: write it back to every sample of the target.</summary>
    private void FinishMixedDepth()
    {
        if (_mixedDepthProxy == 0)
        {
            return;
        }

        var commandBuffer = EnsureCommandBuffer();
        EncodeMixedDepthCopy(commandBuffer, source: _mixedDepthProxy, destination: _mixedDepthTarget, _mixedDepthFormat,
            _mixedDepthSamples, into: false, _mixedDepthLevel, _mixedDepthSlice);
        _mixedDepthTarget = 0;
        _mixedDepthProxy = 0;
    }

    private nint MixedDepthProxy(ulong format, uint width, uint height)
    {
        if (_mixedDepthProxies.TryGetValue((format, width, height), out var existing))
        {
            return existing;
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLTextureDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setTextureType:"), (nuint)Mtl.TextureType2D);
        MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setPixelFormat:"), (nuint)format);
        MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setWidth:"), width);
        MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setHeight:"), height);
        MetalV2Native.SendVoidValue(
            descriptor, MetalNative.Selector("setUsage:"),
            (nuint)(Mtl.TextureUsageRenderTarget | Mtl.TextureUsageShaderRead | TextureUsagePixelFormatView));
        MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setStorageMode:"), Mtl.StorageModePrivate);
        var texture = MetalNative.Send(_device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
        if (texture == 0)
        {
            throw Fatal($"A {width}x{height} one-sample depth proxy (format {format}) could not be created.");
        }

        _mixedDepthProxies[(format, width, height)] = texture;
        return texture;
    }

    private const ulong TextureUsagePixelFormatView = 0x10;

    // The stencil plane of a depth-stencil texture as a texture a shader can read.
    private nint MixedStencilView(nint texture, ulong format)
    {
        if (format == MtlDepthFormats.Stencil8)
        {
            return texture;
        }

        if (_mixedStencilViews.TryGetValue(texture, out var view))
        {
            return view;
        }

        view = MetalNative.Send(texture, SelNewTextureView, (nint)StencilViewFormatX32);
        if (view == 0)
        {
            throw Fatal("The stencil plane of a mixed-sample depth target could not be viewed.");
        }

        _mixedStencilViews[texture] = view;
        return view;
    }

    private void EncodeMixedDepthCopy(nint commandBuffer, nint source, nint destination, ulong format, uint targetSamples,
        bool into, nuint level, nuint slice)
    {
        var hasDepth = FormatHasDepth(format);
        var hasStencil = FormatHasStencil(format);
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        try
        {
            if (hasDepth)
            {
                var depth = MetalNative.Send(descriptor, SelDepthAttachment);
                MetalNative.SendVoid(depth, SelSetTexture, destination);
                MetalV2Native.SendVoidValue(depth, SelSetLevel, level);
                MetalV2Native.SendVoidValue(depth, SelSetSlice, slice);
                MetalV2Native.SendVoidValue(depth, SelSetLoadAction, Mtl.LoadActionDontCare);
                MetalV2Native.SendVoidValue(depth, SelSetStoreAction, Mtl.StoreActionStore);
            }

            if (hasStencil)
            {
                var stencil = MetalNative.Send(descriptor, SelStencilAttachment);
                MetalNative.SendVoid(stencil, SelSetTexture, destination);
                MetalV2Native.SendVoidValue(stencil, SelSetLevel, level);
                MetalV2Native.SendVoidValue(stencil, SelSetSlice, slice);
                MetalV2Native.SendVoidValue(stencil, SelSetLoadAction, Mtl.LoadActionDontCare);
                MetalV2Native.SendVoidValue(stencil, SelSetStoreAction, Mtl.StoreActionStore);
            }

            var encoder = RetainEncoder(MetalNative.Send(commandBuffer, SelRenderEncoder, descriptor));
            if (encoder == 0)
            {
                throw Fatal("A mixed-sample depth copy encoder could not be created.");
            }

            WaitForPreviousEncoder(encoder, render: true);
            LabelEncoder(encoder, into ? "mixed-sample depth in" : "mixed-sample depth out");
            MetalNative.SendVoid(encoder, SelSetRenderPipelineState, MixedDepthPipeline(format, into ? 1u : targetSamples, into));
            MetalNative.SendVoid(encoder, SelSetDepthStencilState, MixedDepthStencilState());
            if (hasStencil)
            {
                MetalV2Native.SendVoidValue(encoder, MetalNative.Selector("setStencilReferenceValue:"), 0);
            }

            var setFragmentTexture = MetalNative.Selector("setFragmentTexture:atIndex:");
            if (hasDepth)
            {
                MetalNative.SendSetAtIndex(encoder, setFragmentTexture, source, 0);
            }

            if (hasStencil)
            {
                MetalNative.SendSetAtIndex(encoder, setFragmentTexture, MixedStencilView(source, format), 1);
            }

            MetalNative.SendDrawPrimitives(
                encoder, MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"), Mtl.PrimitiveTriangle, 0, 3);
            EndAndReleaseEncoder(encoder, render: true);
        }
        finally
        {
            MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
        }
    }

    private nint MixedDepthPipeline(ulong format, uint samples, bool into)
    {
        if (_mixedDepthPipelines.TryGetValue((format, samples, into), out var existing))
        {
            return existing;
        }

        if (_mixedDepthLibrary == 0)
        {
            var options = MetalNative.Send(
                MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
                MetalNative.Selector("init"));
            MetalNative.SetMslLanguageVersion(options);
            nint error = 0;
            _mixedDepthLibrary = MetalNative.Send(
                _device, MetalNative.Selector("newLibraryWithSource:options:error:"), MetalNative.NsString(MixedDepthSource),
                options, ref error);
            MetalNative.SendVoid(options, MetalNative.Selector("release"));
            if (_mixedDepthLibrary == 0)
            {
                throw Fatal($"The mixed-sample depth copy shaders did not compile: {MetalNative.DescribeError(error)}.");
            }
        }

        var hasDepth = FormatHasDepth(format);
        var hasStencil = FormatHasStencil(format);
        var fragmentName = (hasDepth, hasStencil) switch
        {
            (true, true) => "sharpemu_mixed_depth_stencil",
            (true, false) => "sharpemu_mixed_depth",
            _ => "sharpemu_mixed_stencil",
        } + (into ? "_in" : "_out_fn");
        var newFunction = MetalNative.Selector("newFunctionWithName:");
        var vertex = MetalNative.Send(_mixedDepthLibrary, newFunction, MetalNative.NsString(MixedDepthVertex));
        var fragment = MetalNative.Send(_mixedDepthLibrary, newFunction, MetalNative.NsString(fragmentName));
        if (vertex == 0 || fragment == 0)
        {
            throw Fatal($"The mixed-sample depth copy function {fragmentName} is missing.");
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        try
        {
            MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertex);
            MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragment);
            if (hasDepth)
            {
                MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setDepthAttachmentPixelFormat:"), (nuint)format);
            }

            if (hasStencil)
            {
                MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setStencilAttachmentPixelFormat:"), (nuint)format);
            }

            MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setRasterSampleCount:"), samples);
            nint error = 0;
            var pipeline = MetalNative.Send(
                _device, MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref error);
            if (pipeline == 0)
            {
                throw Fatal($"The mixed-sample depth copy pipeline {fragmentName} did not build: {MetalNative.DescribeError(error)}.");
            }

            _mixedDepthPipelines[(format, samples, into)] = pipeline;
            return pipeline;
        }
        finally
        {
            MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
            MetalNative.SendVoid(vertex, MetalNative.Selector("release"));
            MetalNative.SendVoid(fragment, MetalNative.Selector("release"));
        }
    }

    // Every pixel's depth and stencil written as the copy gives them.
    private nint MixedDepthStencilState()
    {
        if (_mixedDepthStencilState != 0)
        {
            return _mixedDepthStencilState;
        }

        var stencil = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLStencilDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalV2Native.SendVoidValue(stencil, MetalNative.Selector("setStencilCompareFunction:"), CompareFunctionAlways);
        MetalV2Native.SendVoidValue(stencil, MetalNative.Selector("setDepthStencilPassOperation:"), StencilOperationReplace);
        MetalV2Native.SendVoidValue(stencil, MetalNative.Selector("setWriteMask:"), 0xFF);
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLDepthStencilDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setDepthCompareFunction:"), CompareFunctionAlways);
        MetalNative.SendVoidBool(descriptor, MetalNative.Selector("setDepthWriteEnabled:"), true);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setFrontFaceStencil:"), stencil);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setBackFaceStencil:"), stencil);
        _mixedDepthStencilState = MetalNative.Send(_device, MetalNative.Selector("newDepthStencilStateWithDescriptor:"), descriptor);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
        MetalNative.SendVoid(stencil, MetalNative.Selector("release"));
        if (_mixedDepthStencilState == 0)
        {
            throw Fatal("The mixed-sample depth copy's depth-stencil state could not be created.");
        }

        return _mixedDepthStencilState;
    }
}
