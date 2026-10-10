// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Gpu.Metal.V2;

// Render passes, the pending draw state Metal needs at encoder level, draws,
// dispatches, barriers and the clears that replace a draw.
internal sealed partial class MetalRenderHost
{
    // MSL reserves buffer 0 for the stage argument buffer and buffer 1 for push data,
    // so vertex streams start at 2. Metal allows 31 buffer arguments per stage.
    private const int VertexBufferSlotBase = 2;
    private const int MaxVertexBufferSlots = 29;
    private const int MaxColorAttachments = RenderingState.ColorAttachmentCapacity;

    private static readonly nint SelRenderEncoder = MetalNative.Selector("renderCommandEncoderWithDescriptor:");
    private static readonly nint SelColorAttachments = MetalNative.Selector("colorAttachments");
    private static readonly nint SelDepthAttachment = MetalNative.Selector("depthAttachment");
    private static readonly nint SelStencilAttachment = MetalNative.Selector("stencilAttachment");
    private static readonly nint SelObjectAtIndex = MetalNative.Selector("objectAtIndexedSubscript:");
    private static readonly nint SelSetTexture = MetalNative.Selector("setTexture:");
    private static readonly nint SelSetLevel = MetalNative.Selector("setLevel:");
    private static readonly nint SelSetSlice = MetalNative.Selector("setSlice:");
    private static readonly nint SelSetDepthPlane = MetalNative.Selector("setDepthPlane:");
    private static readonly nint SelSetLoadAction = MetalNative.Selector("setLoadAction:");
    private static readonly nint SelSetStoreAction = MetalNative.Selector("setStoreAction:");
    private static readonly nint SelSetClearColor = MetalNative.Selector("setClearColor:");
    private static readonly nint SelSetClearDepth = MetalNative.Selector("setClearDepth:");
    private static readonly nint SelSetClearStencil = MetalNative.Selector("setClearStencil:");
    private static readonly nint SelSetRenderTargetWidth = MetalNative.Selector("setRenderTargetWidth:");
    private static readonly nint SelSetRenderTargetHeight = MetalNative.Selector("setRenderTargetHeight:");
    private static readonly nint SelSetRenderTargetArrayLength = MetalNative.Selector("setRenderTargetArrayLength:");
    private static readonly nint SelSetRenderPipelineState = MetalNative.Selector("setRenderPipelineState:");
    private static readonly nint SelSetDepthStencilState = MetalNative.Selector("setDepthStencilState:");
    private static readonly nint SelSetViewport = MetalNative.Selector("setViewport:");
    private static readonly nint SelSetScissorRect = MetalNative.Selector("setScissorRect:");
    private static readonly nint SelSetCullMode = MetalNative.Selector("setCullMode:");
    private static readonly nint SelSetFrontFacingWinding = MetalNative.Selector("setFrontFacingWinding:");
    private static readonly nint SelSetDepthClipMode = MetalNative.Selector("setDepthClipMode:");
    private static readonly nint SelSetTriangleFillMode = MetalNative.Selector("setTriangleFillMode:");
    private static readonly nint SelSetBlendColor = MetalNative.Selector("setBlendColorRed:green:blue:alpha:");
    private static readonly nint SelSetDepthBias = MetalNative.Selector("setDepthBias:slopeScale:clamp:");
    private static readonly nint SelSetStencilReferences =
        MetalNative.Selector("setStencilFrontReferenceValue:backReferenceValue:");
    private static readonly nint SelSetVertexBuffer = MetalNative.Selector("setVertexBuffer:offset:atIndex:");
    private static readonly nint SelSetFragmentBuffer = MetalNative.Selector("setFragmentBuffer:offset:atIndex:");
    private static readonly nint SelSetComputeBuffer = MetalNative.Selector("setBuffer:offset:atIndex:");
    private static readonly nint SelSetComputePipelineState = MetalNative.Selector("setComputePipelineState:");
    private static readonly nint SelUseResourceStages = MetalNative.Selector("useResource:usage:stages:");
    private static readonly nint SelUseResourceUsage = MetalNative.Selector("useResource:usage:");
    private static readonly nint SelDrawPrimitives =
        MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:");
    private static readonly nint SelDrawIndexedPrimitives = MetalNative.Selector(
        "drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:");
    private static readonly nint SelDrawIndexedIndirect = MetalNative.Selector(
        "drawIndexedPrimitives:indexType:indexBuffer:indexBufferOffset:indirectBuffer:indirectBufferOffset:");
    private static readonly nint SelDispatchThreadgroups =
        MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:");
    private static readonly nint SelDispatchIndirect =
        MetalNative.Selector("dispatchThreadgroupsWithIndirectBuffer:indirectBufferOffset:threadsPerThreadgroup:");
    private static readonly nint SelRenderMemoryBarrier =
        MetalNative.Selector("memoryBarrierWithScope:afterStages:beforeStages:");
    private static readonly nint SelComputeMemoryBarrier = MetalNative.Selector("memoryBarrierWithScope:");

    private nint _renderEncoder;
    private nint _computeEncoder;
    private bool _renderingActive;
    private RenderingState _renderingState;
    private nint _boundComputePipeline;
    private MetalPipelineEntry? _boundGraphicsPipeline;
    private MetalPipelineEntry? _pendingGraphicsPipeline;

    private readonly nint[] _vertexBuffers = new nint[MaxVertexBufferSlots];
    private readonly ulong[] _vertexOffsets = new ulong[MaxVertexBufferSlots];
    private int _vertexBufferCount;
    private nint _indexBuffer;
    private ulong _indexOffset;
    private nuint _indexType;
    private DynamicDrawState _dynamicState;

    // The attachment subresource each acquired view points at; a created view starts
    // at its own level zero, and the image texture keeps the real level and slice.
    private readonly uint[] _attachmentLevels = new uint[MaxColorAttachments];
    private readonly uint[] _attachmentSlices = new uint[MaxColorAttachments];
    private readonly uint[] _attachmentDepthPlanes = new uint[MaxColorAttachments];
    private int _attachmentCount;
    private uint _depthAttachmentLevel;
    private uint _depthAttachmentSlice;

    public void BeginRendering(in RenderingState state)
    {
        if (_renderingActive && _renderingState == state)
        {
            return;
        }

        EndRendering();
        EndComputeEncoder();
        var commandBuffer = EnsureCommandBuffer();
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (descriptor == 0)
        {
            throw Fatal("A Metal render pass descriptor could not be created.");
        }

        try
        {
            var colors = MetalNative.Send(descriptor, SelColorAttachments);
            for (var index = 0; index < state.ColorAttachmentCount; index++)
            {
                ref readonly var attachment = ref state.ColorAttachments[index];
                var slot = MetalNative.SendAtIndex(colors, SelObjectAtIndex, (nuint)index);
                MetalNative.SendVoid(slot, SelSetTexture, (nint)attachment.View.Handle);
                MetalV2Native.SendVoidValue(slot, SelSetLevel, index < _attachmentCount ? _attachmentLevels[index] : 0);
                MetalV2Native.SendVoidValue(slot, SelSetSlice, index < _attachmentCount ? _attachmentSlices[index] : 0);
                MetalV2Native.SendVoidValue(
                    slot, SelSetDepthPlane, index < _attachmentCount ? _attachmentDepthPlanes[index] : 0);
                MetalV2Native.SendVoidValue(
                    slot, SelSetLoadAction, attachment.IsClear ? Mtl.LoadActionClear : Mtl.LoadActionLoad);
                MetalV2Native.SendVoidValue(slot, SelSetStoreAction, Mtl.StoreActionStore);
                if (attachment.IsClear)
                {
                    MetalNative.SendVoidClearColor(slot, SelSetClearColor, ClearColorOf(in attachment));
                }
            }

            ref readonly var depthStencil = ref state.DepthStencilAttachment;
            if (depthStencil.HasDepth && depthStencil.View.Handle != 0)
            {
                var depth = MetalNative.Send(descriptor, SelDepthAttachment);
                MetalNative.SendVoid(depth, SelSetTexture, (nint)depthStencil.View.Handle);
                MetalV2Native.SendVoidValue(depth, SelSetLevel, _depthAttachmentLevel);
                MetalV2Native.SendVoidValue(depth, SelSetSlice, _depthAttachmentSlice);
                MetalV2Native.SendVoidValue(
                    depth, SelSetLoadAction, depthStencil.DepthClear ? Mtl.LoadActionClear : Mtl.LoadActionLoad);
                MetalV2Native.SendVoidValue(depth, SelSetStoreAction, Mtl.StoreActionStore);
                if (depthStencil.DepthClear)
                {
                    MetalNative.SendVoidDouble(
                        depth, SelSetClearDepth, BitConverter.UInt32BitsToSingle(depthStencil.ClearWord0));
                }
            }

            if (depthStencil.HasStencil && depthStencil.View.Handle != 0)
            {
                var stencil = MetalNative.Send(descriptor, SelStencilAttachment);
                MetalNative.SendVoid(stencil, SelSetTexture, (nint)depthStencil.View.Handle);
                MetalV2Native.SendVoidValue(stencil, SelSetLevel, _depthAttachmentLevel);
                MetalV2Native.SendVoidValue(stencil, SelSetSlice, _depthAttachmentSlice);
                MetalV2Native.SendVoidValue(
                    stencil, SelSetLoadAction, depthStencil.StencilClear ? Mtl.LoadActionClear : Mtl.LoadActionLoad);
                MetalV2Native.SendVoidValue(stencil, SelSetStoreAction, Mtl.StoreActionStore);
                if (depthStencil.StencilClear)
                {
                    MetalV2Native.SendVoidValue(stencil, SelSetClearStencil, (nuint)depthStencil.ClearWord1);
                }
            }

            // A draw without attachments still rasterizes (its stores are the output),
            // and Metal needs the render area stated explicitly for that case.
            if (state.ColorAttachmentCount == 0 && !depthStencil.HasDepth && !depthStencil.HasStencil)
            {
                MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetWidth, Math.Min(state.Width, 16384));
                MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetHeight, Math.Min(state.Height, 16384));
            }

            if (state.Layers > 1)
            {
                MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetArrayLength, state.Layers);
            }

            _renderEncoder = MetalNative.Send(commandBuffer, SelRenderEncoder, descriptor);
            if (_renderEncoder == 0)
            {
                throw Fatal(
                    $"A Metal render command encoder could not be created: colors={state.ColorAttachmentCount} " +
                    $"depth={depthStencil.HasDepth} stencil={depthStencil.HasStencil} area={state.Width}x{state.Height}.");
            }
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }

        _renderingActive = true;
        _renderingState = state;
        _boundGraphicsPipeline = null;
    }

    private static MtlClearColor ClearColorOf(in RenderingAttachment attachment) => new()
    {
        Red = BitConverter.UInt32BitsToSingle(attachment.ClearWord0),
        Green = BitConverter.UInt32BitsToSingle(attachment.ClearWord1),
        Blue = BitConverter.UInt32BitsToSingle(attachment.ClearWord2),
        Alpha = BitConverter.UInt32BitsToSingle(attachment.ClearWord3),
    };

    public void EndRendering()
    {
        if (!_renderingActive)
        {
            return;
        }

        _renderingActive = false;
        _renderingState = default;
        if (_renderEncoder != 0)
        {
            MetalNative.SendVoid(_renderEncoder, SelEndEncoding);
            _renderEncoder = 0;
        }

        _boundGraphicsPipeline = null;
    }

    private void EndComputeEncoder()
    {
        if (_computeEncoder == 0)
        {
            return;
        }

        MetalNative.SendVoid(_computeEncoder, SelEndEncoding);
        _computeEncoder = 0;
        _boundComputePipeline = 0;
    }

    private nint EnsureComputeEncoder()
    {
        if (_computeEncoder != 0)
        {
            return _computeEncoder;
        }

        EndRendering();
        _computeEncoder = MetalNative.Send(EnsureCommandBuffer(), SelComputeEncoder);
        if (_computeEncoder == 0)
        {
            throw Fatal("A Metal compute command encoder could not be created.");
        }

        return _computeEncoder;
    }

    public void BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings, VertexInputInfo input)
    {
        _ = input;
        var count = Math.Min(bindings.Length, MaxVertexBufferSlots);
        if (bindings.Length > MaxVertexBufferSlots)
        {
            MetalV2Diagnostics.WarnOnce(
                "vertex-slots",
                $"A draw binds more vertex streams than Metal has buffer slots; the extra streams are dropped: " +
                $"streams={bindings.Length} slots={MaxVertexBufferSlots}.");
        }

        for (var index = 0; index < count; index++)
        {
            _vertexBuffers[index] = (nint)bindings[index].Handle;
            _vertexOffsets[index] = bindings[index].Offset;
        }

        _vertexBufferCount = count;
    }

    public void BindIndexBuffer(BufferBinding binding, GpuIndexType type)
    {
        _indexBuffer = (nint)binding.Handle;
        _indexOffset = binding.Offset;
        _indexType = MetalV2Formats.IndexType(type);
    }

    public void SetDynamicState(in DynamicDrawState state) => _dynamicState = state;

    public void BindPipeline(GpuBindPoint bindPoint, in PipelineHandle pipeline)
    {
        var entry = RequirePipelineEntry(in pipeline);
        if (bindPoint == GpuBindPoint.Graphics)
        {
            _pendingGraphicsPipeline = entry;
            return;
        }

        var encoder = EnsureComputeEncoder();
        if (_boundComputePipeline != entry.State)
        {
            MetalNative.SendVoid(encoder, SelSetComputePipelineState, entry.State);
            _boundComputePipeline = entry.State;
        }

        _pendingComputePipeline = entry;
    }

    private MetalPipelineEntry? _pendingComputePipeline;

    // Everything the executor recorded before the pass opened lands on the encoder here.
    private void ApplyGraphicsState()
    {
        if (_renderEncoder == 0)
        {
            throw Fatal("A draw has no open Metal render encoder.");
        }

        var entry = _pendingGraphicsPipeline ??
            throw Fatal("A draw has no bound Metal render pipeline state.");
        if (!ReferenceEquals(_boundGraphicsPipeline, entry))
        {
            MetalNative.SendVoid(_renderEncoder, SelSetRenderPipelineState, entry.State);
            MetalV2Native.SendVoidValue(_renderEncoder, SelSetCullMode, (nuint)entry.CullMode);
            MetalV2Native.SendVoidValue(_renderEncoder, SelSetFrontFacingWinding, (nuint)entry.Winding);
            MetalV2Native.SendVoidValue(_renderEncoder, SelSetDepthClipMode, (nuint)entry.DepthClipMode);
            MetalV2Native.SendVoidValue(_renderEncoder, SelSetTriangleFillMode, (nuint)entry.FillMode);
            _boundGraphicsPipeline = entry;
        }

        var depthStencilState = GetDepthStencilState(entry, in _dynamicState);
        MetalNative.SendVoid(_renderEncoder, SelSetDepthStencilState, depthStencilState);
        ApplyViewportAndScissor();
        MetalNative.SendVoidBlendColor(
            _renderEncoder,
            SelSetBlendColor,
            _dynamicState.BlendRed,
            _dynamicState.BlendGreen,
            _dynamicState.BlendBlue,
            _dynamicState.BlendAlpha);
        MetalV2Native.SendDepthBias(
            _renderEncoder,
            SelSetDepthBias,
            _dynamicState.DepthBiasEnabled ? _dynamicState.DepthBiasConstantFactor : 0f,
            _dynamicState.DepthBiasEnabled ? _dynamicState.DepthBiasSlopeFactor : 0f,
            _dynamicState.DepthBiasEnabled ? _dynamicState.DepthBiasClamp : 0f);
        if (_dynamicState.StencilTestEnabled)
        {
            MetalV2Native.SendStencilReferences(
                _renderEncoder,
                SelSetStencilReferences,
                _dynamicState.FrontStencil.Reference,
                _dynamicState.BackStencil.Reference);
        }

        for (var index = 0; index < _vertexBufferCount; index++)
        {
            MetalNative.SendSetBuffer(
                _renderEncoder,
                SelSetVertexBuffer,
                _vertexBuffers[index],
                (nuint)_vertexOffsets[index],
                (nuint)(VertexBufferSlotBase + index));
        }

        ApplyStageBindings(graphics: true);
    }

    private void ApplyViewportAndScissor()
    {
        var width = Math.Max(_renderingState.Width, 1u);
        var height = Math.Max(_renderingState.Height, 1u);
        // Guests program a negative viewport height to get y-up rendering out of
        // Vulkan's y-down NDC. Metal's NDC is already y-up and rejects a negative
        // height, so the same on-screen mapping is the flipped positive rect.
        var originY = (double)_dynamicState.ViewportY;
        var viewportHeight = (double)_dynamicState.ViewportHeight;
        if (viewportHeight < 0)
        {
            originY += viewportHeight;
            viewportHeight = -viewportHeight;
        }

        MetalNative.SendVoidViewport(_renderEncoder, SelSetViewport, new MtlViewport
        {
            OriginX = _dynamicState.ViewportX,
            OriginY = originY,
            Width = Math.Max(_dynamicState.ViewportWidth, 0),
            Height = viewportHeight,
            ZNear = Math.Clamp(_dynamicState.ViewportMinDepth, 0f, 1f),
            ZFar = Math.Clamp(_dynamicState.ViewportMaxDepth, 0f, 1f),
        });
        var scissor = _dynamicState.Scissor;
        var left = (uint)Math.Clamp(scissor.Left, 0, (int)width);
        var top = (uint)Math.Clamp(scissor.Top, 0, (int)height);
        var right = (uint)Math.Clamp(scissor.Right, 0, (int)width);
        var bottom = (uint)Math.Clamp(scissor.Bottom, 0, (int)height);
        var scissorWidth = right > left ? right - left : 0;
        var scissorHeight = bottom > top ? bottom - top : 0;
        if (_boundGraphicsPipeline is { CullsEverything: true })
        {
            // Metal has no "cull both faces"; a one-pixel scissor is the cheapest
            // equivalent, and the draw then covers nothing it would have kept.
            left = 0;
            top = 0;
            scissorWidth = 1;
            scissorHeight = 1;
        }

        // Metal refuses an empty scissor rectangle; an empty guest rectangle becomes
        // one pixel at its corner, which the clamped viewport keeps out of the image.
        MetalNative.SendVoidScissor(_renderEncoder, SelSetScissorRect, new MtlScissorRect
        {
            X = left,
            Y = top,
            Width = Math.Max(scissorWidth, 1),
            Height = Math.Max(scissorHeight, 1),
        });
    }

    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyGraphicsState();
        var entry = _boundGraphicsPipeline!;
        MetalV2Native.SendDrawPrimitivesBaseInstance(
            _renderEncoder,
            SelDrawPrimitives,
            entry.PrimitiveType,
            firstVertex,
            vertexCount,
            Math.Max(instanceCount, 1),
            firstInstance);
        CountDraw(started);
    }

    void IRenderHost.DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_indexBuffer == 0)
        {
            throw Fatal("An indexed draw has no bound index buffer.");
        }

        ApplyGraphicsState();
        var entry = _boundGraphicsPipeline!;
        var indexBytes = _indexType == Mtl.IndexTypeUInt32 ? 4UL : 2UL;
        MetalV2Native.SendDrawIndexedBaseVertex(
            _renderEncoder,
            SelDrawIndexedPrimitives,
            entry.PrimitiveType,
            indexCount,
            _indexType,
            _indexBuffer,
            (nuint)(_indexOffset + (firstIndex * indexBytes)),
            Math.Max(instanceCount, 1),
            vertexOffset,
            firstInstance);
        CountDraw(started);
    }

    void IRenderHost.DrawIndexedIndirect(BufferBinding arguments)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_indexBuffer == 0)
        {
            throw Fatal("An indexed indirect draw has no bound index buffer.");
        }

        // The guest's argument layout is (indexCount, instanceCount, firstIndex,
        // vertexOffset, firstInstance), which is exactly MTLDrawIndexedPrimitivesIndirectArguments.
        ApplyGraphicsState();
        var entry = _boundGraphicsPipeline!;
        MetalV2Native.SendDrawIndexedIndirect(
            _renderEncoder,
            SelDrawIndexedIndirect,
            entry.PrimitiveType,
            _indexType,
            _indexBuffer,
            (nuint)_indexOffset,
            (nint)arguments.Handle,
            (nuint)arguments.Offset);
        CountDraw(started);
    }

    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var encoder = EnsureComputeEncoder();
        var entry = _pendingComputePipeline ?? throw Fatal("A dispatch has no bound Metal compute pipeline state.");
        ApplyStageBindings(graphics: false);
        MetalNative.SendDispatch(
            encoder,
            SelDispatchThreadgroups,
            new MtlSize { Width = Math.Max(groupsX, 1), Height = Math.Max(groupsY, 1), Depth = Math.Max(groupsZ, 1) },
            entry.ThreadsPerThreadgroup);
        CountDraw(started);
    }

    public bool TryDispatchIndirect(ulong argumentsAddress)
    {
        var range = _guestMemory.Resolve(argumentsAddress, 3 * sizeof(uint));
        if (!range.IsValid)
        {
            return false;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var encoder = EnsureComputeEncoder();
        var entry = _pendingComputePipeline ??
            throw Fatal("An indirect dispatch has no bound Metal compute pipeline state.");
        ApplyStageBindings(graphics: false);
        MetalV2Native.SendDispatchIndirect(
            encoder,
            SelDispatchIndirect,
            range.Buffer,
            (nuint)range.Offset,
            entry.ThreadsPerThreadgroup);
        CountDraw(started);
        return true;
    }

    private void CountDraw(long startedTicks)
    {
        Interlocked.Increment(ref _drawCount);
        Interlocked.Add(ref _drawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks);
        _recordedCommands++;
    }

    // Shader stores become visible to later work. Inside a render pass a scoped
    // barrier is enough; across encoders Metal's own tracking orders the windows.
    public void ShaderWriteBarrier(GpuPipelineStage sourceStages)
    {
        _ = sourceStages;
        RecordMemoryBarrier();
    }

    public void ShaderWriteHazardBarrier() => RecordMemoryBarrier();

    public void ShaderAccessBarrier() => RecordMemoryBarrier();

    private void RecordMemoryBarrier()
    {
        const nuint scope = Mtl.BarrierScopeBuffers | Mtl.BarrierScopeTextures;
        if (_renderEncoder != 0)
        {
            MetalV2Native.SendMemoryBarrierStages(
                _renderEncoder,
                SelRenderMemoryBarrier,
                scope,
                Mtl.RenderStageVertex | Mtl.RenderStageFragment,
                Mtl.RenderStageVertex | Mtl.RenderStageFragment);
            return;
        }

        if (_computeEncoder != 0)
        {
            MetalV2Native.SendMemoryBarrierScope(_computeEncoder, SelComputeMemoryBarrier, scope);
        }
    }

    /// <summary>A guest barrier that spans encoders: the open encoder closes.</summary>
    public void EmitGlobalBarrier()
    {
        EndRendering();
        EndComputeEncoder();
    }

    // Replaces a draw whose only effect is one colour over the whole target.
    public void ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear)
    {
        Span<float> logical = stackalloc float[4] { clear.Red, clear.Green, clear.Blue, clear.Alpha };
        foreach (ref readonly var target in targets)
        {
            var acquisition = ((IRenderHost)this).AcquireColorAttachment(in target);
            if (acquisition.View.Handle == 0)
            {
                continue;
            }

            var mapping = target.Resolution.ExportMapping;
            var color = default(GpuClearColor);
            color.Float32_0 = logical[(int)mapping.Map(0)];
            color.Float32_1 = logical[(int)mapping.Map(1)];
            color.Float32_2 = logical[(int)mapping.Map(2)];
            color.Float32_3 = logical[(int)mapping.Map(3)];
            var view = target.Resolution.Request.View;
            var range = new SubresourceRange(0, 1, 0, Math.Max(view.LayerCount, 1));
            ClearAttachmentTexture(
                (nint)acquisition.View.Handle,
                in range,
                target.Resolution.Extent.Width,
                target.Resolution.Extent.Height,
                in color);
        }

        _recordedCommands++;
    }

    void IMetalImageCacheOwner.ClearTextureColor(
        nint texture,
        ulong pixelFormat,
        in SubresourceRange range,
        uint width,
        uint height,
        in GpuClearColor color)
    {
        _ = pixelFormat;
        ClearAttachmentTexture(texture, in range, width, height, in color);
    }

    void IMetalImageCacheOwner.ClearTextureDepthStencil(
        nint texture,
        ulong pixelFormat,
        in SubresourceRange range,
        uint width,
        uint height,
        float depth,
        byte stencil,
        bool clearDepth,
        bool clearStencil)
    {
        var commandBuffer = ((IMetalImageCacheOwner)this).BeginTransfer().CommandBuffer;
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        try
        {
            if (clearDepth && MetalV2Formats.IsDepthFormat(pixelFormat))
            {
                var attachment = MetalNative.Send(descriptor, SelDepthAttachment);
                ConfigureClearAttachment(attachment, texture, in range);
                MetalNative.SendVoidDouble(attachment, SelSetClearDepth, depth);
            }

            if (clearStencil && MetalV2Formats.HasStencil(pixelFormat))
            {
                var attachment = MetalNative.Send(descriptor, SelStencilAttachment);
                ConfigureClearAttachment(attachment, texture, in range);
                MetalV2Native.SendVoidValue(attachment, SelSetClearStencil, stencil);
            }

            MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetWidth, Math.Max(width, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetHeight, Math.Max(height, 1));
            if (range.LayerCount > 1)
            {
                MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetArrayLength, range.LayerCount);
            }

            var encoder = MetalNative.Send(commandBuffer, SelRenderEncoder, descriptor);
            if (encoder != 0)
            {
                MetalNative.SendVoid(encoder, SelEndEncoding);
            }
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }
    }

    // One load-clear render pass with no draws: the cheapest full clear in Metal.
    private void ClearAttachmentTexture(
        nint texture,
        in SubresourceRange range,
        uint width,
        uint height,
        in GpuClearColor color)
    {
        if (texture == 0)
        {
            return;
        }

        var commandBuffer = ((IMetalImageCacheOwner)this).BeginTransfer().CommandBuffer;
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        try
        {
            var colors = MetalNative.Send(descriptor, SelColorAttachments);
            var attachment = MetalNative.SendAtIndex(colors, SelObjectAtIndex, 0);
            ConfigureClearAttachment(attachment, texture, in range);
            MetalNative.SendVoidClearColor(attachment, SelSetClearColor, new MtlClearColor
            {
                Red = color.Float32_0,
                Green = color.Float32_1,
                Blue = color.Float32_2,
                Alpha = color.Float32_3,
            });
            MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetWidth, Math.Max(width, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetHeight, Math.Max(height, 1));
            if (range.LayerCount > 1)
            {
                MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetArrayLength, range.LayerCount);
            }

            var encoder = MetalNative.Send(commandBuffer, SelRenderEncoder, descriptor);
            if (encoder == 0)
            {
                MetalV2Diagnostics.WarnOnce("clear-encoder", "A Metal clear pass could not be encoded.");
                return;
            }

            MetalNative.SendVoid(encoder, SelEndEncoding);
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }
    }

    private static void ConfigureClearAttachment(nint attachment, nint texture, in SubresourceRange range)
    {
        MetalNative.SendVoid(attachment, SelSetTexture, texture);
        MetalV2Native.SendVoidValue(attachment, SelSetLevel, range.BaseLevel);
        MetalV2Native.SendVoidValue(attachment, SelSetSlice, range.BaseLayer);
        MetalV2Native.SendVoidValue(attachment, SelSetLoadAction, Mtl.LoadActionClear);
        MetalV2Native.SendVoidValue(attachment, SelSetStoreAction, Mtl.StoreActionStore);
    }

    public bool TryRetainTargetlessDraw(RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments)
    {
        _ = banks;
        _ = programs;
        _ = arguments;
        // v2 renders a targetless draw as it is; nothing is retained for a later flip.
        return false;
    }
}
