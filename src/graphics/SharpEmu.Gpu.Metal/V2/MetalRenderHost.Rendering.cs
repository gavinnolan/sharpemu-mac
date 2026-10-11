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
    private static readonly nint SelSetColorStoreAction = MetalNative.Selector("setColorStoreAction:atIndex:");
    private static readonly nint SelSetDepthStoreAction = MetalNative.Selector("setDepthStoreAction:");
    private static readonly nint SelSetStencilStoreAction = MetalNative.Selector("setStencilStoreAction:");
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

    // Sized from the enum: a hard-coded length indexed by a cause is one new cause away
    // from throwing on the first transfer of that kind.
    private readonly long[] _transferCauses =
        new long[Enum.GetValues<MetalTransferCause>().Length];

    /// <summary>Transfers by cause since the last call; every one ended the open pass.</summary>
    internal string TakeTransferCauses()
    {
        var builder = new System.Text.StringBuilder();
        for (var cause = 0; cause < _transferCauses.Length; cause++)
        {
            var count = _transferCauses[cause];
            _transferCauses[cause] = 0;
            if (count != 0)
            {
                builder.Append(builder.Length == 0 ? string.Empty : " ")
                    .Append((MetalTransferCause)cause).Append('=').Append(count);
            }
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }

    // What the passes cost in memory traffic, which on a tile-based GPU is the whole
    // story: a pass loads every attachment it does not clear and stores every one it
    // keeps, so the bill is attachment bytes times passes, and each mid-frame split
    // pays it again. Counted as the descriptor is built, from the real pixel size.
    private long _loadBytes;
    private long _storeBytes;
    private long _clearedBytes;
    private long _passesEmpty;
    private long _discardedBytes;

    // The standalone clear passes: a load-clear encoder with no draws, which ends the
    // open pass and then stores a whole attachment of its own. They are render passes
    // too, and the ones the pass counters never saw.
    private long _clearPasses;
    private long _clearDepthPasses;
    private long _clearPixels;

    internal (long Passes, long DepthPasses, long Pixels) TakeClearPasses()
    {
        var passes = Interlocked.Exchange(ref _clearPasses, 0);
        var depth = Interlocked.Exchange(ref _clearDepthPasses, 0);
        var pixels = Interlocked.Exchange(ref _clearPixels, 0);
        return (passes, depth, pixels);
    }

    // A pass that renders into the same attachments as the pass before last: the
    // target set left them and came straight back, so the pair stored and reloaded
    // the whole framebuffer for whatever happened in between. This is the merge
    // opportunity, counted rather than assumed.
    private long _passesReturned;
    private ulong _previousPassKey;
    private ulong _passBeforeKey;

    internal (long Load, long Store, long Cleared, long Discarded, long Empty, long Returned)
        TakeAttachmentBandwidth()
    {
        var load = Interlocked.Exchange(ref _loadBytes, 0);
        var store = Interlocked.Exchange(ref _storeBytes, 0);
        var cleared = Interlocked.Exchange(ref _clearedBytes, 0);
        var discarded = Interlocked.Exchange(ref _discardedBytes, 0);
        var empty = Interlocked.Exchange(ref _passesEmpty, 0);
        var returned = Interlocked.Exchange(ref _passesReturned, 0);
        return (load, store, cleared, discarded, empty, returned);
    }

    private void NotePassIdentity(in RenderingState state)
    {
        var first = state.ColorAttachmentCount != 0 ? state.ColorAttachments[0].View.Handle : 0;
        var key = first ^ (state.DepthStencilAttachment.View.Handle * 0x9E3779B97F4A7C15UL);
        if (key != 0 && key == _passBeforeKey && key != _previousPassKey)
        {
            _passesReturned++;
        }

        _passBeforeKey = _previousPassKey;
        _previousPassKey = key;
    }

    // One attachment's cost for one pass: the render area, every sample, and the
    // bytes one pixel of that attachment really occupies.
    private static long AttachmentBytes(in RenderingState state, uint texelBytes) =>
        (long)state.Width * state.Height * Math.Max(1u, state.Samples) * texelBytes;

    private void NoteAttachmentTraffic(in RenderingState state)
    {
        for (var index = 0; index < state.ColorAttachmentCount; index++)
        {
            var bytes = AttachmentBytes(
                in state, index < MaxColorAttachments ? _attachmentTexelBytes[index] : 0);
            if (state.ColorAttachments[index].IsClear)
            {
                _clearedBytes += bytes;
            }
            else
            {
                _loadBytes += bytes;
            }

            _storeBytes += bytes;
        }

        ref readonly var depthStencil = ref state.DepthStencilAttachment;
        if (depthStencil.HasDepth && depthStencil.View.Handle != 0)
        {
            var bytes = AttachmentBytes(in state, _depthTexelBytes);
            if (depthStencil.DepthClear)
            {
                _clearedBytes += bytes;
            }
            else
            {
                _loadBytes += bytes;
            }

            _storeBytes += bytes;
        }

        if (depthStencil.HasStencil && depthStencil.View.Handle != 0)
        {
            // Metal keeps stencil in its own plane, one byte a pixel, whatever the
            // combined format's element size says.
            var bytes = AttachmentBytes(in state, 1);
            if (depthStencil.StencilClear)
            {
                _clearedBytes += bytes;
            }
            else
            {
                _loadBytes += bytes;
            }

            _storeBytes += bytes;
        }
    }

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

    // What the open encoder already has. Metal keeps encoder state between draws, so
    // these let a draw send only what changed; every one of them is reset when an
    // encoder is created, because a new encoder starts with none of it.
    private nint _boundDepthStencilState;
    private float _boundBlendRed = float.NaN;
    private float _boundBlendGreen = float.NaN;
    private float _boundBlendBlue = float.NaN;
    private float _boundBlendAlpha = float.NaN;
    private float _boundBiasConstant = float.NaN;
    private float _boundBiasSlope = float.NaN;
    private float _boundBiasClamp = float.NaN;
    private uint _boundStencilFront = uint.MaxValue;
    private uint _boundStencilBack = uint.MaxValue;
    private readonly nint[] _boundVertexBuffers = new nint[MaxVertexBufferSlots];
    private readonly ulong[] _boundVertexOffsets = new ulong[MaxVertexBufferSlots];

    private void ForgetEncoderState()
    {
        _boundGraphicsPipeline = null;
        _boundDepthStencilState = 0;
        _boundBlendRed = float.NaN;
        _boundBlendGreen = float.NaN;
        _boundBlendBlue = float.NaN;
        _boundBlendAlpha = float.NaN;
        _boundBiasConstant = float.NaN;
        _boundBiasSlope = float.NaN;
        _boundBiasClamp = float.NaN;
        _boundStencilFront = uint.MaxValue;
        _boundStencilBack = uint.MaxValue;
        Array.Clear(_boundVertexBuffers);
        Array.Clear(_boundVertexOffsets);
        ForgetResidentResources();
    }
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

    // How wide one pixel of each attachment is, taken from the image the view was
    // made from. A tile-based GPU's cost is the attachment's bytes, not its pixels,
    // so this is what makes a pass split measurable rather than guessed at.
    private readonly uint[] _attachmentTexelBytes = new uint[MaxColorAttachments];
    private uint _depthTexelBytes;

    // The subresource each attachment view addresses. A store can only be discarded
    // when the pass covered all of it, because discarding throws the whole
    // subresource away and not just the part the render area touched.
    private readonly uint[] _attachmentWidths = new uint[MaxColorAttachments];
    private readonly uint[] _attachmentHeights = new uint[MaxColorAttachments];
    private uint _depthAttachmentWidth;
    private uint _depthAttachmentHeight;

    // The image texture behind each attachment view, and the subresource of it the
    // view really addresses. A clear names the texture and its own subresource, so a
    // clear that follows a pass can only be matched against the pass through these.
    private readonly nint[] _attachmentTextures = new nint[MaxColorAttachments];
    private readonly uint[] _attachmentRealLevels = new uint[MaxColorAttachments];
    private readonly uint[] _attachmentRealLayers = new uint[MaxColorAttachments];
    private nint _depthAttachmentTexture;
    private uint _depthAttachmentRealLevel;
    private uint _depthAttachmentRealLayer;

    // The pixel sizes the open pass was created with. The live arrays above belong to
    // whatever attachments were acquired most recently, which by the time a pass ends
    // is the pass about to begin.
    private readonly uint[] _passTexelBytes = new uint[MaxColorAttachments];
    private uint _passDepthTexelBytes;
    private readonly nint[] _passTextures = new nint[MaxColorAttachments];
    private readonly uint[] _passRealLevels = new uint[MaxColorAttachments];
    private readonly uint[] _passRealLayers = new uint[MaxColorAttachments];
    private readonly uint[] _passWidths = new uint[MaxColorAttachments];
    private readonly uint[] _passHeights = new uint[MaxColorAttachments];
    private nint _passDepthTexture;
    private uint _passDepthRealLevel;
    private uint _passDepthRealLayer;
    private uint _passDepthWidth;
    private uint _passDepthHeight;

    // A clear that is about to be encoded, which is how the pass it ends learns that
    // its stores are dead. Set just before the clear takes the pass's place and read
    // once, as the pass resolves its store actions.
    private nint _clearedTexture;
    private uint _clearedBaseLevel;
    private uint _clearedLevelCount;
    private uint _clearedBaseLayer;
    private uint _clearedLayerCount;
    private uint _clearedWidth;
    private uint _clearedHeight;
    private bool _clearedDepth;
    private bool _clearedStencil;

    /// <summary>
    /// Records the clear that is about to be encoded in place of the open pass, so the
    /// pass can discard the stores it overwrites. The clear is the very next thing the
    /// GPU does to that texture — nothing else is encoded between ending the pass and
    /// encoding the clear — so a store it covers cannot be observed.
    /// </summary>
    private void NoteClearAboutToBeEncoded(
        nint texture,
        in SubresourceRange range,
        uint width,
        uint height,
        bool depth,
        bool stencil)
    {
        if (!_renderingActive || _renderEncoder == 0)
        {
            return;
        }

        _clearedTexture = texture;
        _clearedBaseLevel = range.BaseLevel;
        _clearedLevelCount = Math.Max(range.LevelCount, 1);
        _clearedBaseLayer = range.BaseLayer;
        _clearedLayerCount = Math.Max(range.LayerCount, 1);
        _clearedWidth = width;
        _clearedHeight = height;
        _clearedDepth = depth;
        _clearedStencil = stencil;
    }

    private void ForgetNotedClear() => _clearedTexture = 0;

    private bool NotedClearCoversAttachment(
        nint texture,
        uint realLevel,
        uint realLayer,
        uint subresourceWidth,
        uint subresourceHeight)
    {
        return _clearedTexture != 0 && texture == _clearedTexture &&
            realLevel >= _clearedBaseLevel && realLevel < _clearedBaseLevel + _clearedLevelCount &&
            realLayer >= _clearedBaseLayer &&
            realLayer + _renderingState.Layers <= _clearedBaseLayer + _clearedLayerCount &&
            MetalPassStores.ClearCoversAttachment(
                _clearedWidth,
                _clearedHeight,
                _renderingState.Layers,
                _renderingState.Samples,
                _renderingState.Layers,
                _renderingState.Samples,
                subresourceWidth,
                subresourceHeight);
    }

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

    // What a pass asks for while it is open. Unknown is a promise to name the real
    // action before encoding ends, which is the only way to let the work that follows
    // the pass decide: a store whose pixels the very next pass clears is dead, and a
    // tile-based GPU charges the full attachment for it.
    private static nuint PendingStoreAction =>
        MetalV2Diagnostics.DeferStoreActions ? Mtl.StoreActionUnknown : Mtl.StoreActionStore;

    /// <summary>
    /// Names every store action the open pass left as Unknown. A colour, depth or
    /// stencil attachment whose whole subresource the next pass clears is discarded;
    /// everything else is stored, including every attachment of a pass that ends with
    /// nothing known to follow it.
    /// </summary>
    private void ResolveStoreActions(in RenderingState next, bool hasNext)
    {
        ref readonly var open = ref _renderingState;
        for (var index = 0; index < open.ColorAttachmentCount; index++)
        {
            var discard = hasNext
                ? NextPassClearsColorAttachment(in next, index)
                : index < MaxColorAttachments && NotedClearCoversAttachment(
                    _passTextures[index],
                    _passRealLevels[index],
                    _passRealLayers[index],
                    _passWidths[index],
                    _passHeights[index]);
            MetalV2Native.SendSetStoreActionAtIndex(
                _renderEncoder,
                SelSetColorStoreAction,
                discard ? Mtl.StoreActionDontCare : Mtl.StoreActionStore,
                (nuint)index);
            if (discard)
            {
                _discardedBytes += AttachmentBytes(
                    in open, index < MaxColorAttachments ? _passTexelBytes[index] : 0);
            }
        }

        ref readonly var openDepth = ref open.DepthStencilAttachment;
        if (openDepth.HasDepth && openDepth.View.Handle != 0)
        {
            var discard = hasNext
                ? NextPassClearsDepthStencil(in next, stencil: false)
                : _clearedDepth && NotedClearCoversAttachment(
                    _passDepthTexture, _passDepthRealLevel, _passDepthRealLayer,
                    _passDepthWidth, _passDepthHeight);
            MetalV2Native.SendVoidValue(
                _renderEncoder,
                SelSetDepthStoreAction,
                discard ? Mtl.StoreActionDontCare : Mtl.StoreActionStore);
            if (discard)
            {
                _discardedBytes += AttachmentBytes(in open, _passDepthTexelBytes);
            }
        }

        if (openDepth.HasStencil && openDepth.View.Handle != 0)
        {
            var discard = hasNext
                ? NextPassClearsDepthStencil(in next, stencil: true)
                : _clearedStencil && NotedClearCoversAttachment(
                    _passDepthTexture, _passDepthRealLevel, _passDepthRealLayer,
                    _passDepthWidth, _passDepthHeight);
            MetalV2Native.SendVoidValue(
                _renderEncoder,
                SelSetStencilStoreAction,
                discard ? Mtl.StoreActionDontCare : Mtl.StoreActionStore);
            if (discard)
            {
                _discardedBytes += AttachmentBytes(in open, 1);
            }
        }

        ForgetNotedClear();
    }

    // Discarding throws away the whole subresource, so the clear that follows has to
    // cover the whole subresource: its render area at least as large as the mip, and
    // at least as many layers as the pass being discarded wrote.
    private bool NextPassClearsColorAttachment(in RenderingState next, int openIndex)
    {
        var handle = _renderingState.ColorAttachments[openIndex].View.Handle;
        if (handle == 0)
        {
            return false;
        }

        for (var index = 0; index < next.ColorAttachmentCount && index < MaxColorAttachments; index++)
        {
            ref readonly var candidate = ref next.ColorAttachments[index];
            if (!candidate.IsClear || candidate.View.Handle != handle ||
                _attachmentLevels[index] != _passLevels[openIndex] ||
                _attachmentSlices[index] != _passSlices[openIndex] ||
                _attachmentDepthPlanes[index] != _passDepthPlanes[openIndex])
            {
                continue;
            }

            return MetalPassStores.ClearCoversAttachment(
                next.Width,
                next.Height,
                next.Layers,
                next.Samples,
                _renderingState.Layers,
                _renderingState.Samples,
                _attachmentWidths[index],
                _attachmentHeights[index]);
        }

        return false;
    }

    private bool NextPassClearsDepthStencil(in RenderingState next, bool stencil)
    {
        ref readonly var nextDepth = ref next.DepthStencilAttachment;
        var handle = _renderingState.DepthStencilAttachment.View.Handle;
        return handle != 0 &&
            nextDepth.View.Handle == handle &&
            (stencil ? nextDepth.StencilClear : nextDepth.DepthClear) &&
            _depthAttachmentLevel == _passDepthLevel &&
            _depthAttachmentSlice == _passDepthSlice &&
            MetalPassStores.ClearCoversAttachment(
                next.Width,
                next.Height,
                next.Layers,
                next.Samples,
                _renderingState.Layers,
                _renderingState.Samples,
                _depthAttachmentWidth,
                _depthAttachmentHeight);
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

        EndRenderingBefore(in state);
        EndComputeEncoder();
        EncodeDeferredLabelCopies();
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
                MetalV2Native.SendVoidValue(slot, SelSetStoreAction, PendingStoreAction);
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
                MetalV2Native.SendVoidValue(depth, SelSetStoreAction, PendingStoreAction);
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
                MetalV2Native.SendVoidValue(stencil, SelSetStoreAction, PendingStoreAction);
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

        NoteRecordedWork();
        _renderingActive = true;
        _renderingState = state;
        ForgetEncoderState();
        _renderPasses++;
        NoteAttachmentTraffic(in state);
        NotePassIdentity(in state);
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
        Array.Copy(_attachmentTexelBytes, _passTexelBytes, MaxColorAttachments);
        Array.Copy(_attachmentTextures, _passTextures, MaxColorAttachments);
        Array.Copy(_attachmentRealLevels, _passRealLevels, MaxColorAttachments);
        Array.Copy(_attachmentRealLayers, _passRealLayers, MaxColorAttachments);
        Array.Copy(_attachmentWidths, _passWidths, MaxColorAttachments);
        Array.Copy(_attachmentHeights, _passHeights, MaxColorAttachments);
        _passDepthTexture = _depthAttachmentTexture;
        _passDepthRealLevel = _depthAttachmentRealLevel;
        _passDepthRealLayer = _depthAttachmentRealLayer;
        _passDepthWidth = _depthAttachmentWidth;
        _passDepthHeight = _depthAttachmentHeight;
        _passDepthLevel = _depthAttachmentLevel;
        _passDepthSlice = _depthAttachmentSlice;
        _passDepthTexelBytes = _depthTexelBytes;
    }

    private static MtlClearColor ClearColorOf(in RenderingAttachment attachment) => new()
    {
        Red = BitConverter.UInt32BitsToSingle(attachment.ClearWord0),
        Green = BitConverter.UInt32BitsToSingle(attachment.ClearWord1),
        Blue = BitConverter.UInt32BitsToSingle(attachment.ClearWord2),
        Alpha = BitConverter.UInt32BitsToSingle(attachment.ClearWord3),
    };

    public void EndRendering() => FinishRendering(default, hasNext: false);

    // The pass is ending because another one is about to begin, and that one is what
    // decides whether this pass's stores are worth making.
    private void EndRenderingBefore(in RenderingState next) => FinishRendering(in next, hasNext: true);

    private void FinishRendering(in RenderingState next, bool hasNext)
    {
        if (!_renderingActive)
        {
            return;
        }

        var ending = _renderingState;
        if (_renderEncoder != 0)
        {
            if (MetalV2Diagnostics.DeferStoreActions)
            {
                ResolveStoreActions(in next, hasNext);
            }

            MetalNative.SendVoid(_renderEncoder, SelEndEncoding);
            _renderEncoder = 0;
        }

        _renderingActive = false;
        _renderingState = default;
        ForgetNotedClear();

        ForgetEncoderState();
        DumpPassTargets(in ending);
        if (_passDrawCount == 0)
        {
            _passesEmpty++;
        }

        _passDrawCount = 0;
        _passLastProfile = null;
    }

    // Under SHARPEMU_METAL_V2_DUMP_TARGETS, remember what the pass just finished writing.
    // Only remember: a dump reads back through its own command buffer and waits, and
    // EndRendering runs inside EndEncoders, which runs inside the flush. Dumping here
    // would nest a flush inside a flush. They are drained at the next flip instead.
    private void DumpPassTargets(in RenderingState ending)
    {
        if (!DumpTargetsEnabled)
        {
            return;
        }

        for (var index = 0; index < ending.ColorAttachmentCount; index++)
        {
            NotePendingDump((nint)ending.ColorAttachments[index].View.Handle, $"pass{index}");
        }

        // The depth the pass leaves behind says whether its geometry rasterised at all,
        // which a colour target that never changes cannot.
        if (ending.DepthStencilAttachment.HasDepth)
        {
            NotePendingDump((nint)ending.DepthStencilAttachment.View.Handle, "passdepth");
        }
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
        NoteRecordedWork();
        if (_computeEncoder != 0)
        {
            return _computeEncoder;
        }

        if (_renderingActive)
        {
            _passEndCompute++;
        }

        EndRendering();
        EncodeDeferredLabelCopies();
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
            MetalV2Native.SendVoidValue(
                _renderEncoder, SelSetCullMode, (nuint)(MetalV2Diagnostics.IgnoreFragmentTests ? Mtl.CullModeNone : entry.CullMode));
            MetalV2Native.SendVoidValue(_renderEncoder, SelSetFrontFacingWinding, (nuint)entry.Winding);
            MetalV2Native.SendVoidValue(_renderEncoder, SelSetDepthClipMode, (nuint)entry.DepthClipMode);
            MetalV2Native.SendVoidValue(_renderEncoder, SelSetTriangleFillMode, (nuint)entry.FillMode);
            _boundGraphicsPipeline = entry;
        }

        // Encoder state survives between draws, so only what changed is sent. A draw
        // costs twenty-odd message sends otherwise, and consecutive draws in one pass
        // share almost all of their state.
        var depthStencilState = GetDepthStencilState(entry, in _dynamicState);
        if (_boundDepthStencilState != depthStencilState)
        {
            MetalNative.SendVoid(_renderEncoder, SelSetDepthStencilState, depthStencilState);
            _boundDepthStencilState = depthStencilState;
        }

        ApplyViewportAndScissor();
        if (_boundBlendRed != _dynamicState.BlendRed || _boundBlendGreen != _dynamicState.BlendGreen ||
            _boundBlendBlue != _dynamicState.BlendBlue || _boundBlendAlpha != _dynamicState.BlendAlpha)
        {
            MetalNative.SendVoidBlendColor(
                _renderEncoder,
                SelSetBlendColor,
                _dynamicState.BlendRed,
                _dynamicState.BlendGreen,
                _dynamicState.BlendBlue,
                _dynamicState.BlendAlpha);
            _boundBlendRed = _dynamicState.BlendRed;
            _boundBlendGreen = _dynamicState.BlendGreen;
            _boundBlendBlue = _dynamicState.BlendBlue;
            _boundBlendAlpha = _dynamicState.BlendAlpha;
        }

        var biasConstant = _dynamicState.DepthBiasEnabled ? _dynamicState.DepthBiasConstantFactor : 0f;
        var biasSlope = _dynamicState.DepthBiasEnabled ? _dynamicState.DepthBiasSlopeFactor : 0f;
        var biasClamp = _dynamicState.DepthBiasEnabled ? _dynamicState.DepthBiasClamp : 0f;
        if (_boundBiasConstant != biasConstant || _boundBiasSlope != biasSlope || _boundBiasClamp != biasClamp)
        {
            MetalV2Native.SendDepthBias(_renderEncoder, SelSetDepthBias, biasConstant, biasSlope, biasClamp);
            _boundBiasConstant = biasConstant;
            _boundBiasSlope = biasSlope;
            _boundBiasClamp = biasClamp;
        }

        if (_dynamicState.StencilTestEnabled &&
            (_boundStencilFront != _dynamicState.FrontStencil.Reference ||
                _boundStencilBack != _dynamicState.BackStencil.Reference))
        {
            MetalV2Native.SendStencilReferences(
                _renderEncoder,
                SelSetStencilReferences,
                _dynamicState.FrontStencil.Reference,
                _dynamicState.BackStencil.Reference);
            _boundStencilFront = _dynamicState.FrontStencil.Reference;
            _boundStencilBack = _dynamicState.BackStencil.Reference;
        }

        for (var index = 0; index < _vertexBufferCount; index++)
        {
            if (_boundVertexBuffers[index] == _vertexBuffers[index] &&
                _boundVertexOffsets[index] == _vertexOffsets[index])
            {
                continue;
            }

            MetalNative.SendSetBuffer(
                _renderEncoder,
                SelSetVertexBuffer,
                _vertexBuffers[index],
                (nuint)_vertexOffsets[index],
                (nuint)(VertexBufferSlotBase + index));
            _boundVertexBuffers[index] = _vertexBuffers[index];
            _boundVertexOffsets[index] = _vertexOffsets[index];
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
        if (_boundGraphicsPipeline is { CullsEverything: true } && !MetalV2Diagnostics.IgnoreFragmentTests)
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
        NoteRecordedWork();
        Interlocked.Increment(ref _drawCount);
        NotePassDraw(_boundGraphicsPipeline?.Profile);
        Interlocked.Add(ref _drawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks);
        _recordedCommands++;
    }

    // Shader stores become visible to later work. Inside a render pass a scoped
    // barrier is enough; across encoders Metal's own tracking orders the windows.
    // The barrier itself records no guest write range: what the stores reached was
    // recorded where the storage buffers and images were bound.
    public void ShaderWriteBarrier(GpuPipelineStage sourceStages)
    {
        _ = sourceStages;
        RecordMemoryBarrier();
    }

    public void ShaderWriteHazardBarrier() => RecordMemoryBarrier();

    public void ShaderAccessBarrier() => RecordMemoryBarrier();

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

    // How a guest clear arrives: with no pass open, with one open that has drawn
    // nothing yet, or with one open that has. Only the first two can become the next
    // pass's load action; the third has to be a real clear.
    private long _clearNoPass;
    private long _clearEmptyPass;
    private long _clearDrawnPass;

    internal string TakeClearArrivals()
    {
        var none = Interlocked.Exchange(ref _clearNoPass, 0);
        var empty = Interlocked.Exchange(ref _clearEmptyPass, 0);
        var drawn = Interlocked.Exchange(ref _clearDrawnPass, 0);
        return $"no_pass={none} empty_pass={empty} drawn_pass={drawn}";
    }

    // Replaces a draw whose only effect is one colour over the whole target.
    public void ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear)
    {
        if (!_renderingActive)
        {
            _clearNoPass++;
        }
        else if (_passDrawCount == 0)
        {
            _clearEmptyPass++;
        }
        else
        {
            _clearDrawnPass++;
        }

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
        _clearPasses++;
        _clearDepthPasses++;
        _clearPixels += (long)Math.Max(width, 1) * Math.Max(height, 1) * Math.Max(range.LayerCount, 1);
        NoteClearAboutToBeEncoded(texture, in range, width, height, clearDepth, clearStencil);
        var context = ((IMetalImageCacheOwner)this).BeginTransfer(MetalTransferCause.DepthClear);
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

        _clearPasses++;
        _clearPixels += (long)Math.Max(width, 1) * Math.Max(height, 1) * Math.Max(range.LayerCount, 1);
        NoteClearAboutToBeEncoded(texture, in range, width, height, depth: false, stencil: false);
        var context = ((IMetalImageCacheOwner)this).BeginTransfer(MetalTransferCause.ColorClear);
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
