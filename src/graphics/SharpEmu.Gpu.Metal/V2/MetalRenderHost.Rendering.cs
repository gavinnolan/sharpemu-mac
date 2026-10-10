// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Metal;

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
    private static readonly nint SelDrawMeshThreadgroups = MetalNative.Selector(
        "drawMeshThreadgroups:threadsPerObjectThreadgroup:threadsPerMeshThreadgroup:");
    private static readonly nint SelDispatchThreadgroups =
        MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:");
    private static readonly nint SelDispatchIndirect =
        MetalNative.Selector("dispatchThreadgroupsWithIndirectBuffer:indirectBufferOffset:threadsPerThreadgroup:");
    private static readonly nint SelRenderMemoryBarrier =
        MetalNative.Selector("memoryBarrierWithScope:afterStages:beforeStages:");
    private static readonly nint SelComputeMemoryBarrier = MetalNative.Selector("memoryBarrierWithScope:");

    private nint _renderEncoder;
    private nint _computeEncoder;
    private long _renderPasses;
    private long _passEndClear;
    private long _passEndTarget;
    private long _passEndTransfer;
    private long _passEndCompute;
    private long _targetlessPasses;

    internal (long Clear, long Target, long Transfer, long Compute, long Targetless) TakePassBreakdown()
    {
        var clear = Interlocked.Exchange(ref _passEndClear, 0);
        var target = Interlocked.Exchange(ref _passEndTarget, 0);
        var transfer = Interlocked.Exchange(ref _passEndTransfer, 0);
        var compute = Interlocked.Exchange(ref _passEndCompute, 0);
        var targetless = Interlocked.Exchange(ref _targetlessPasses, 0);
        return (clear, target, transfer, compute, targetless);
    }
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

    // The subresources the open pass was created with, so the next draw can tell
    // whether it renders into the very same attachments.
    private readonly uint[] _passLevels = new uint[MaxColorAttachments];
    private readonly uint[] _passSlices = new uint[MaxColorAttachments];
    private readonly uint[] _passDepthPlanes = new uint[MaxColorAttachments];
    private int _passAttachmentCount;
    private uint _passDepthLevel;
    private uint _passDepthSlice;

    /// <summary>
    /// Whether the next draw can keep the open render pass. Only what Metal bakes
    /// into the pass matters: the attachments, their subresources, the render area
    /// and whether a load clear has to happen. The shared RenderingState also
    /// carries the clear values of attachments that are not being cleared, and
    /// comparing those would end the pass between ordinary draws — which on a
    /// tile-based GPU means storing and reloading the whole framebuffer each time.
    /// </summary>
    private bool CanContinuePass(in RenderingState state)
    {
        if (!_renderingActive || _renderEncoder == 0 ||
            state.ColorAttachmentCount != _renderingState.ColorAttachmentCount ||
            state.Width != _renderingState.Width || state.Height != _renderingState.Height ||
            state.Layers != _renderingState.Layers || state.Samples != _renderingState.Samples ||
            _attachmentCount != _passAttachmentCount)
        {
            return false;
        }

        for (var index = 0; index < state.ColorAttachmentCount; index++)
        {
            ref readonly var next = ref state.ColorAttachments[index];
            ref readonly var open = ref _renderingState.ColorAttachments[index];
            if (next.IsClear || next.View.Handle != open.View.Handle || next.Format != open.Format)
            {
                return false;
            }

            if (index < _attachmentCount &&
                (_attachmentLevels[index] != _passLevels[index] ||
                 _attachmentSlices[index] != _passSlices[index] ||
                 _attachmentDepthPlanes[index] != _passDepthPlanes[index]))
            {
                return false;
            }
        }

        ref readonly var nextDepth = ref state.DepthStencilAttachment;
        ref readonly var openDepth = ref _renderingState.DepthStencilAttachment;
        if (nextDepth.DepthClear || nextDepth.StencilClear ||
            nextDepth.View.Handle != openDepth.View.Handle ||
            nextDepth.HasDepth != openDepth.HasDepth || nextDepth.HasStencil != openDepth.HasStencil)
        {
            return false;
        }

        return nextDepth.View.Handle == 0 ||
            (_depthAttachmentLevel == _passDepthLevel && _depthAttachmentSlice == _passDepthSlice);
    }

    public void BeginRendering(in RenderingState state)
    {
        if (CanContinuePass(in state))
        {
            return;
        }

        if (_renderingActive)
        {
            var clears = state.DepthStencilAttachment.DepthClear || state.DepthStencilAttachment.StencilClear;
            for (var index = 0; index < state.ColorAttachmentCount && !clears; index++)
            {
                clears |= state.ColorAttachments[index].IsClear;
            }

            if (clears)
            {
                _passEndClear++;
            }
            else
            {
                _passEndTarget++;
            }
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

            // A draw without attachments reaches the executor only when it has no
            // pixel stage: its output is what the vertex stage stores, and no
            // fragment ever runs. The shared state still asks for the maximum
            // framebuffer, and a tile-based GPU would walk every one of a
            // 16384x16384 area's tiles for it, so the pass takes the smallest area
            // Metal accepts.
            if (state.ColorAttachmentCount == 0 && !depthStencil.HasDepth && !depthStencil.HasStencil)
            {
                MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetWidth, 1);
                MetalV2Native.SendVoidValue(descriptor, SelSetRenderTargetHeight, 1);
                _targetlessPasses++;
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
        _renderPasses++;
        if (MetalV2Diagnostics.Verbose || _renderPasses < 4096)
        {
            var first = state.ColorAttachmentCount != 0 ? state.ColorAttachments[0] : default;
            ref readonly var passDepth = ref state.DepthStencilAttachment;
            MetalV2Diagnostics.InfoOnce(
                $"pass:{state.Width}x{state.Height}:{state.Samples}:{state.ColorAttachmentCount}:" +
                $"{(int)first.Format}:{first.IsClear}:{passDepth.HasDepth}:{passDepth.DepthClear}",
                $"render pass {state.Width}x{state.Height} samples={state.Samples} layers={state.Layers} " +
                $"colors={state.ColorAttachmentCount} format={(int)first.Format} clear={first.IsClear} " +
                $"depth={passDepth.HasDepth} depth_clear={passDepth.DepthClear} stencil={passDepth.HasStencil}.");
        }
        _passAttachmentCount = _attachmentCount;
        Array.Copy(_attachmentLevels, _passLevels, MaxColorAttachments);
        Array.Copy(_attachmentSlices, _passSlices, MaxColorAttachments);
        Array.Copy(_attachmentDepthPlanes, _passDepthPlanes, MaxColorAttachments);
        _passDepthLevel = _depthAttachmentLevel;
        _passDepthSlice = _depthAttachmentSlice;
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

        if (_renderingActive)
        {
            _passEndCompute++;
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
        if (entry.Mesh is { } mesh)
        {
            DrawMesh(mesh, vertexCount, instanceCount, indexed: false, firstIndexOrVertex: firstVertex,
                baseVertex: 0, firstInstance: firstInstance);
            CountDraw(started);
            ProfileWork(entry, "draw-mesh", $"vertices={vertexCount} instances={instanceCount}");
            return;
        }

        MetalV2Native.SendDrawPrimitivesBaseInstance(
            _renderEncoder,
            SelDrawPrimitives,
            entry.PrimitiveType,
            firstVertex,
            vertexCount,
            Math.Max(instanceCount, 1),
            firstInstance);
        CountDraw(started);
        ProfileWork(entry, "draw", $"vertices={vertexCount} instances={instanceCount}");
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
        if (entry.Mesh is { } mesh)
        {
            DrawMesh(mesh, indexCount, instanceCount, indexed: true, firstIndexOrVertex: firstIndex,
                baseVertex: vertexOffset, firstInstance: firstInstance);
            CountDraw(started);
            ProfileWork(entry, "draw-indexed-mesh", $"indices={indexCount} instances={instanceCount}");
            return;
        }

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
        ProfileWork(entry, "draw-indexed", $"indices={indexCount} instances={instanceCount}");
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
        if (entry.Mesh is not null)
        {
            // A mesh grid is sized from the counts, and Metal has no indirect mesh
            // draw that reads them; the executor resolves such a draw on the CPU when
            // the host says it cannot take one (ResolvesIndirectDrawOnGpu).
            MetalV2Diagnostics.WarnOnce(
                "mesh-indirect",
                "An indexed indirect draw reached a mesh pipeline and was skipped; its counts are not on the host.");
            return;
        }

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
        ProfileWork(entry, "draw-indexed-indirect", "indirect");
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
        ProfileWork(
            entry,
            "dispatch",
            $"groups={groupsX}x{groupsY}x{groupsZ} " +
            $"threadgroup={entry.ThreadsPerThreadgroup.Width}x{entry.ThreadsPerThreadgroup.Height}x" +
            $"{entry.ThreadsPerThreadgroup.Depth}");
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
        ProfileWork(
            entry,
            "dispatch-indirect",
            $"threadgroup={entry.ThreadsPerThreadgroup.Width}x{entry.ThreadsPerThreadgroup.Height}x" +
            $"{entry.ThreadsPerThreadgroup.Depth}");
        return true;
    }

    // One NGG subgroup per threadgroup, one instance per grid row. The counts the
    // geometry engine would have put in registers travel in the draw-info block the
    // mesh prologue reads (docs/mac/ngg-mesh-shaders.md).
    private void DrawMesh(
        Gen5MslMeshInfo mesh,
        uint count,
        uint instanceCount,
        bool indexed,
        uint firstIndexOrVertex,
        int baseVertex,
        uint firstInstance)
    {
        var verticesPerPrimitive = Math.Max(mesh.VerticesPerPrimitive, 1);
        var primitives = count / verticesPerPrimitive;
        var maxPrimitives = Math.Max(mesh.MaxPrimitives, 1);
        var maxVertices = Math.Max(mesh.MaxVertices, 1);
        var subgroups = Math.Max(
            (primitives + maxPrimitives - 1) / maxPrimitives,
            (count + maxVertices - 1) / maxVertices);
        if (subgroups == 0 || instanceCount == 0)
        {
            return;
        }

        var drawInfo = _guestMemory.Allocate(Gen5MslMeshInfo.DrawInfoByteSize, 256, out var span);
        span.Clear();
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(span);
        words[0] = maxVertices;
        words[1] = maxPrimitives;
        words[2] = indexed ? count : 0;
        words[3] = indexed ? (_indexType == Mtl.IndexTypeUInt32 ? 2u : 1u) : 0u;
        words[4] = indexed ? firstIndexOrVertex : 0;
        words[5] = unchecked((uint)baseVertex);
        words[6] = instanceCount;
        words[7] = firstInstance;
        MetalNative.SendSetBuffer(
            _renderEncoder, SelSetMeshBuffer, drawInfo.Buffer, (nuint)drawInfo.Offset, (nuint)mesh.DrawInfoBufferIndex);
        // The index slot is read as a dword stream and must be bound even for an
        // automatic draw; the null buffer serves when the draw has no index buffer.
        var indexBuffer = indexed && _indexBuffer != 0 ? _indexBuffer : _guestMemory.NullBuffer;
        var indexOffset = indexed && _indexBuffer != 0 ? _indexOffset : 0;
        MetalNative.SendSetBuffer(
            _renderEncoder, SelSetMeshBuffer, indexBuffer, (nuint)indexOffset, (nuint)mesh.IndexBufferIndex);
        MetalNative.SendDrawMesh(
            _renderEncoder,
            SelDrawMeshThreadgroups,
            new MtlSize { Width = subgroups, Height = instanceCount, Depth = 1 },
            new MtlSize { Width = 1, Height = 1, Depth = 1 },
            new MtlSize { Width = Math.Max(mesh.ThreadsPerThreadgroup, 1), Height = 1, Depth = 1 });
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
        NoteGuestMemoryWrite();
        RecordMemoryBarrier();
    }

    public void ShaderWriteHazardBarrier() => RecordMemoryBarrier();

    public void ShaderAccessBarrier()
    {
        NoteGuestMemoryWrite();
        RecordMemoryBarrier();
    }

    private void RecordMemoryBarrier()
    {
        const nuint scope = Mtl.BarrierScopeBuffers | Mtl.BarrierScopeTextures | Mtl.BarrierScopeRenderTargets;
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

    /// <summary>
    /// A guest cache flush. Ending the render pass here would store and reload the
    /// whole attachment on a tile-based GPU, which is what the guest's per-draw
    /// barriers would cost; a scoped memory barrier on the open encoder expresses
    /// the same ordering without leaving the pass. Only a barrier with no encoder
    /// open has nothing to attach to, and then there is nothing to order either.
    /// </summary>
    public void EmitGlobalBarrier()
    {
        if (_renderEncoder != 0 || _computeEncoder != 0)
        {
            RecordMemoryBarrier();
            return;
        }

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
        var context = ((IMetalImageCacheOwner)this).BeginTransfer();
        var commandBuffer = context.CommandBuffer;
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

        context.ProfileEncoded($"clear {width}x{height} layers={range.LayerCount}");
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

        var context = ((IMetalImageCacheOwner)this).BeginTransfer();
        var commandBuffer = context.CommandBuffer;
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

        context.ProfileEncoded($"clear {width}x{height} layers={range.LayerCount}");
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
