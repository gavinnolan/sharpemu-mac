// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;


namespace SharpEmu.Gpu.Metal;

// Guest compute dispatches: ordered guest work like draws, with two contracts to
// honor. Storage images are shared live through the guest-image registry so a
// dispatch's writes are visible to later draws, blits, and flips of the same
// address; and CPU-visible buffer writes land back in guest memory before the
// work item completes, which is the ordering point WaitForGuestWork promises.
internal static partial class MetalVideoPresenter
{
    private static readonly bool _skipAllCompute =
        Environment.GetEnvironmentVariable("SHARPEMU_SKIP_ALL_COMPUTE") == "1";
    private static bool _tracedDispatchBase;

    private sealed record ComputeGuestDispatch(
        ulong ShaderAddress,
        MetalCompiledGuestShader Shader,
        GuestDrawTexture[] Textures,
        GuestMemoryBuffer[] GlobalMemoryBuffers,
        uint GroupCountX,
        uint GroupCountY,
        uint GroupCountZ,
        uint BaseGroupX,
        uint BaseGroupY,
        uint BaseGroupZ,
        GuestStageBindings? StageBindings);

    private static readonly Dictionary<MetalCompiledGuestShader, nint> _computePipelineCache = new();

    // The kernel bakes its thread bounds; a dispatch needs storage images, written buffers or the data share.
    public static long SubmitComputeDispatch(
        ulong shaderAddress,
        MetalCompiledGuestShader computeShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint baseGroupX,
        uint baseGroupY,
        uint baseGroupZ,
        bool writesGlobalMemory,
        GuestStageBindings? stageBindings)
    {
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0) return 0;
        var hasStorageWrites = false;
        foreach (var texture in textures)
            hasStorageWrites |= StorageImageWrites(texture);
        if (!hasStorageWrites && !writesGlobalMemory && !(stageBindings?.UsesGlobalDataShare ?? false))
            return 0;

        lock (_gate)
        {
            if (_closed || _thread is null)
            {
                return 0;
            }

            var sequence = EnqueueGuestWorkLocked(
                new ComputeGuestDispatch(
                    shaderAddress,
                    computeShader,
                    ToArray(textures),
                    ToArray(globalMemoryBuffers),
                    groupCountX,
                    groupCountY,
                    groupCountZ,
                    baseGroupX,
                    baseGroupY,
                    baseGroupZ,
                    stageBindings));
            if (sequence <= 0) return 0;
            // Admission may wait and release the gate. Publish only once a
            // real queued writer exists; storage class alone is not intent.
            foreach (var texture in textures)
            {
                if (!StorageImageWrites(texture) || texture.Address == 0) continue;
                var guestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType);
                if (guestFormat != 0) _availableGuestImages[texture.Address] = guestFormat;
                _guestImageWorkSequences[texture.Address] = sequence;
            }

            return sequence;
        }
    }

    private static void ExecuteComputeDispatch(nint device, nint queue, ComputeGuestDispatch dispatch)
    {
        if (_skipAllCompute)
        {
            ReturnPooledComputeData(dispatch);
            return;
        }

        PerfOverlay.RecordDraw();

        if ((dispatch.BaseGroupX | dispatch.BaseGroupY | dispatch.BaseGroupZ) != 0 &&
            !_tracedDispatchBase)
        {
            // Metal has no dispatch-base; the translated kernel derives its ids
            // from the raw grid position, so a nonzero base computes offset-zero
            // work until base support lands in the emitted kernel.
            _tracedDispatchBase = true;
            Console.Error.WriteLine(
                "[LOADER][WARN] Metal compute dispatch with nonzero base group " +
                $"({dispatch.BaseGroupX},{dispatch.BaseGroupY},{dispatch.BaseGroupZ}); " +
                "executing without the base offset.");
        }

        if (!TryGetComputePipeline(device, dispatch.Shader, out var pipeline))
        {
            ReturnPooledComputeData(dispatch);
            return;
        }

        var commandBuffer = BeginBatchedGuestCommands(queue);

        // Pre-resolve textures before the compute encoder opens: snapshot
        // blits for feedback reads encode into the batch and encoder order
        // must place them ahead of this dispatch.
        Span<nint> textureHandles = stackalloc nint[dispatch.Textures.Length];
        Span<bool> textureOwned = stackalloc bool[dispatch.Textures.Length];
        for (var index = 0; index < dispatch.Textures.Length; index++)
        {
            var descriptor = dispatch.Textures[index];
            if (descriptor.IsStorage && descriptor.Address != 0)
            {
                textureHandles[index] = EnsureStorageImage(device, descriptor)?.Texture ?? 0;
                textureOwned[index] = false;
            }
            else
            {
                textureHandles[index] = CreateDrawTexture(
                    device, commandBuffer, descriptor, out var ownedTexture);
                textureOwned[index] = ownedTexture;
            }
        }

        var encoder = MetalNative.Send(commandBuffer, MetalNative.Selector("computeCommandEncoder"));
        MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), pipeline);

        var writeBackBuffers = new List<(nint Pointer, GuestMemoryBuffer Guest)>();
        var uploads = UploadDrawBuffers(device, dispatch.GlobalMemoryBuffers, writeBackBuffers);
        var stages = dispatch.StageBindings is { } bindings ? new[] { bindings } : [];
        if (FindStageBindings(stages, GuestStageKind.Compute, dispatch.Shader) is { } computeBindings)
        {
            EncodeStageResources(
                device, new StageEncoder(encoder, MetalNative.Selector("setBuffer:offset:atIndex:"), 0), dispatch.Shader.Shader.ArgumentLayout,
                computeBindings, uploads, textureHandles, MergePushData(stages));
        }

        for (var index = 0; index < textureHandles.Length; index++)
        {
            if (textureHandles[index] != 0 && textureOwned[index])
            {
                MetalNative.SendVoid(textureHandles[index], MetalNative.Selector("release"));
            }
        }

        var shader = dispatch.Shader.Shader;
        MetalNative.SendDispatch(
            encoder,
            MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:"),
            new MtlSize
            {
                Width = dispatch.GroupCountX,
                Height = dispatch.GroupCountY,
                Depth = dispatch.GroupCountZ,
            },
            new MtlSize
            {
                Width = Math.Max(shader.ThreadgroupSizeX, 1),
                Height = Math.Max(shader.ThreadgroupSizeY, 1),
                Depth = Math.Max(shader.ThreadgroupSizeZ, 1),
            });
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));

        // CPU-visible writes are ordering points (see the draw path): flush
        // the batch and wait so the write-back lands before this work item
        // completes. Pure-GPU dispatches stay in the open batch.
        if (writeBackBuffers.Count > 0)
        {
            var committed = FlushBatchedGuestCommands();
            WaitForCommittedCommandBuffer(committed);
            WriteBuffersBackToGuest(writeBackBuffers);
        }

        RecordStorageImageWrites(dispatch.Textures);

        ReturnPooledComputeData(dispatch);
    }

    internal static bool StorageImageWrites(GuestDrawTexture texture)
    {
        if (!texture.IsStorage) return false;
        if (texture.Access == GuestImageAccess.Unknown ||
            (texture.Access & ~(GuestImageAccess.Read | GuestImageAccess.Write | GuestImageAccess.Atomic)) != 0)
            throw new NotSupportedException($"Metal storage image 0x{texture.Address:X} has unknown or undefined instruction access intent.");
        if ((texture.Access & GuestImageAccess.Atomic) != 0 &&
            (texture.Access & (GuestImageAccess.Read | GuestImageAccess.Write)) != (GuestImageAccess.Read | GuestImageAccess.Write))
            throw new NotSupportedException("Metal atomic image intent must include read and write.");
        return (texture.Access & GuestImageAccess.Write) != 0;
    }

    internal static void RecordStorageImageWrites(IReadOnlyList<GuestDrawTexture> textures)
    {
        lock (_gate)
        {
            var changed = new HashSet<ulong>();
            foreach (var descriptor in textures)
                if (descriptor.Address != 0 && StorageImageWrites(descriptor) && changed.Add(descriptor.Address) &&
                    _guestImages.TryGetValue(descriptor.Address, out var image)) image.MarkContentChanged();
        }
    }

    /// <summary>The live, shared storage image for a guest address: dispatches,
    /// draws, blits, and flips of the same address all see one texture.</summary>
    private static GuestImage? EnsureStorageImage(nint device, GuestDrawTexture descriptor)
    {
        lock (_gate)
        {
            if (_guestImages.TryGetValue(descriptor.Address, out var existing))
            {
                return existing;
            }
        }

        if (descriptor.Width == 0 || descriptor.Height == 0 ||
            descriptor.Width > 16384 || descriptor.Height > 16384)
        {
            return null;
        }

        var format = MetalGuestFormats.TryDecodeRenderTargetFormat(
            descriptor.Format, descriptor.NumberType, out var decoded)
            ? decoded.Format
            : MtlPixelFormat.Rgba8Unorm;
        var textureDescriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format,
            descriptor.Width,
            descriptor.Height,
            mipmapped: false);
        MetalNative.Send(
            textureDescriptor,
            MetalNative.Selector("setUsage:"),
            (nint)(UsageShaderRead | UsageShaderWrite | UsageRenderTarget));
        var image = new GuestImage
        {
            Texture = MetalNative.Send(
                device, MetalNative.Selector("newTextureWithDescriptor:"), textureDescriptor),
            Width = descriptor.Width,
            Height = descriptor.Height,
            Format = format,
        };
        if (image.Texture == 0)
        {
            return null;
        }

        var bytesPerPixel = MetalRenderTargetFormat.GetBytesPerPixel(format);
        // Snapshot copies arrive in the image's native texel layout; only
        // 4-byte texels can be RGBA8 verbatim, wider ones carry native bytes.
        if ((ulong)descriptor.RgbaPixels.Length >= (ulong)descriptor.Width * bytesPerPixel)
        {
            var pitch = descriptor.Pitch != 0
                ? Math.Max(descriptor.Pitch, descriptor.Width)
                : descriptor.Width;
            ReplaceTextureContents(
                image.Texture, descriptor.Width, descriptor.Height, descriptor.RgbaPixels, pitch, bytesPerPixel);
            image.Initialized = true;
            image.ContentVersion++;
        }

        lock (_gate)
        {
            if (_guestImages.TryGetValue(descriptor.Address, out var raced))
            {
                MetalNative.SendVoid(image.Texture, MetalNative.Selector("release"));
                return raced;
            }

            _guestImages[descriptor.Address] = image;
            _guestImageExtents[descriptor.Address] =
                (descriptor.Width, descriptor.Height, (ulong)descriptor.Width * descriptor.Height * bytesPerPixel);
        }

        return image;
    }

    private static bool TryGetComputePipeline(nint device, MetalCompiledGuestShader shader, out nint pipeline)
    {
        lock (_computePipelineCache)
        {
            if (_computePipelineCache.TryGetValue(shader, out pipeline))
            {
                return pipeline != 0;
            }
        }

        var function = GetShaderFunction(device, shader);
        if (function != 0)
        {
            nint error = 0;
            pipeline = MetalNative.Send(
                device,
                MetalNative.Selector("newComputePipelineStateWithFunction:error:"),
                function,
                ref error);
            if (pipeline == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Metal compute pipeline creation failed: {MetalNative.DescribeError(error)}");
            }
            else
            {
                Interlocked.Increment(ref _perfPipelineCreations);
            }
        }
        else
        {
            pipeline = 0;
        }

        lock (_computePipelineCache)
        {
            _computePipelineCache[shader] = pipeline;
        }

        return pipeline != 0;
    }

    private static void ReturnPooledComputeData(ComputeGuestDispatch dispatch)
    {
        foreach (var buffer in dispatch.GlobalMemoryBuffers)
        {
            if (buffer.Pooled)
            {
                GuestDataPool.Shared.Return(buffer.Data);
            }
        }
    }
}
