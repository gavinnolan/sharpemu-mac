// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.VideoOut;
using static SharpEmu.Libs.Agc.AgcBackendServices;


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
            if (descriptor.IsStorage && descriptor.Address != 0 &&
                (!_textureSourceGraphEnabled || StorageImageWrites(descriptor)))
            {
                textureHandles[index] = EnsureStorageImage(device, descriptor).Texture;
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
    private static GuestImage EnsureStorageImage(nint device, GuestDrawTexture descriptor)
    {
        // Decode texture descriptor semantics before looking up a live image.
        // Render-target codes are a different namespace; substituting RGBA8
        // here changes both native binding type and the seed's byte layout.
        var nativeFormat = StorageTextureFormat(descriptor);
        descriptor = PrepareDrawTextureUpload(descriptor);
        var seedLayout = StorageTextureSeedLayout(descriptor, nativeFormat);
        lock (_gate)
        {
            if (_guestImages.TryGetValue(descriptor.Address, out var existing))
            {
                ValidateStorageImageView(existing, descriptor, nativeFormat);
                return existing;
            }
        }

        if (descriptor.RgbaPixels.Length == 0)
            throw new InvalidOperationException($"Metal storage image 0x{descriptor.Address:X} has no complete initialization source.");
        var format = nativeFormat.Format;
        var shape = GuestTextureShape(descriptor);
        var textureDescriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format,
            descriptor.Width,
            descriptor.Height,
            mipmapped: false);
        MetalNative.Send(textureDescriptor, MetalNative.Selector("setStorageMode:"), (nint)0);
        MetalNative.Send(textureDescriptor, MetalNative.Selector("setTextureType:"), (nint)shape.TextureType);
        MetalNative.Send(textureDescriptor, MetalNative.Selector("setDepth:"), (nint)shape.Depth);
        MetalNative.Send(
            textureDescriptor,
            MetalNative.Selector("setUsage:"),
            (nint)(UsageShaderRead | UsageShaderWrite |
                (shape.TextureType == MtlTextureType2D ? UsageRenderTarget : 0)));
        var image = new GuestImage
        {
            Texture = MetalNative.Send(
                device, MetalNative.Selector("newTextureWithDescriptor:"), textureDescriptor),
            Width = descriptor.Width,
            Height = descriptor.Height,
            TextureType = shape.TextureType,
            Depth = shape.Depth,
            Format = format,
        };
        if (image.Texture == 0)
            throw new InvalidOperationException($"Metal cannot allocate storage image 0x{descriptor.Address:X} with format {format}.");

        var bytesPerPixel = nativeFormat.BytesPerPixel;
        // Snapshot copies arrive in the image's native texel layout; only
        // 4-byte texels can be RGBA8 verbatim, wider ones carry native bytes.
        if (descriptor.RgbaPixels.Length != 0)
        {
            try
            {
                ReplaceDrawTextureContents(image.Texture, descriptor, seedLayout.Pitch, nativeFormat);
                image.Initialized = true;
                image.ContentVersion++;
            }
            catch
            {
                MetalNative.SendVoid(image.Texture, MetalNative.Selector("release"));
                throw;
            }
        }

        lock (_gate)
        {
            if (_guestImages.TryGetValue(descriptor.Address, out var raced))
            {
                MetalNative.SendVoid(image.Texture, MetalNative.Selector("release"));
                ValidateStorageImageView(raced, descriptor, nativeFormat);
                return raced;
            }

            _guestImages[descriptor.Address] = image;
            _guestImageExtents[descriptor.Address] =
                (descriptor.Width, descriptor.Height,
                    checked((ulong)descriptor.Width * descriptor.Height * shape.Depth * bytesPerPixel));
        }

        return image;
    }

    private static void ValidateStorageImageView(GuestImage image, GuestDrawTexture descriptor, MetalTextureFormat format)
    {
        var shape = GuestTextureShape(descriptor);
        if (image.Width != descriptor.Width || image.Height != descriptor.Height || image.Format != format.Format ||
            image.TextureType != shape.TextureType || image.Depth != shape.Depth)
            throw new NotSupportedException($"Metal storage image view does not match live image at 0x{descriptor.Address:X}.");
        if (!image.Initialized)
            throw new InvalidOperationException($"Metal storage image 0x{descriptor.Address:X} has no initialized contents.");
    }

    internal static MetalTextureFormat StorageTextureFormat(GuestDrawTexture descriptor)
    {
        if (descriptor.Width == 0 || descriptor.Height == 0 || descriptor.Width > 16384 || descriptor.Height > 16384)
            throw new NotSupportedException($"Metal storage image 0x{descriptor.Address:X} has unsupported extent {descriptor.Width}x{descriptor.Height}.");
        var format = MetalGuestFormats.DecodeTextureFormat(descriptor.Format, descriptor.NumberType);
        if (format.IsBlockCompressed)
            throw new NotSupportedException($"Metal block-compressed storage image format {descriptor.Format}/{descriptor.NumberType} is unsupported.");
        // The descriptor and compiled shader must agree on native dimension,
        // including depth-one texture3d. No array/cube/mip reinterpretation.
        var volume = descriptor.Type == Gen5TextureType3D;
        if (volume && (descriptor.TiledSource is not null || descriptor.Detile is not null))
            throw new NotSupportedException("Metal storage volumes require owned linear seed bytes; deferred XY-only detiling is not a volume source proof.");
        if (volume && (descriptor.Descriptor is not { Length: >= 8 } raw ||
            !TryDecodeTextureDescriptor(raw, out var sourceDescriptor) ||
            sourceDescriptor.MetadataAddress != 0 || sourceDescriptor.DescriptorFlags != 0 || sourceDescriptor.BcSwizzle != 0 ||
            sourceDescriptor.Type != descriptor.Type || sourceDescriptor.Address != descriptor.Address ||
            sourceDescriptor.Width != descriptor.Width || sourceDescriptor.Height != descriptor.Height ||
            sourceDescriptor.Depth != descriptor.Depth || sourceDescriptor.TileMode != descriptor.TileMode ||
            sourceDescriptor.Format != descriptor.Format || sourceDescriptor.NumberType != descriptor.NumberType ||
            sourceDescriptor.BaseLevel != 0 || sourceDescriptor.LastLevel != 0 || sourceDescriptor.MaxMip != 0))
            throw new NotSupportedException(
                $"Metal volume storage requires a matching complete single-mip uncompressed descriptor with zero metadata/flags/BC swizzle: " +
                $"addr=0x{descriptor.Address:X} raw=[{string.Join(",", descriptor.Descriptor?.Select(static word => $"{word:X8}") ?? [])}].");
        if ((descriptor.Type != 9 && !volume) || descriptor.Shape.Volume != volume ||
            descriptor.ArrayedView || descriptor.ArrayLayers != 1 ||
            (!volume && descriptor.Depth != 1) || descriptor.Depth == 0 ||
            (volume && (descriptor.Width > 2048 || descriptor.Height > 2048 || descriptor.Depth > 2048)) ||
            descriptor.MipLevels != 1 || descriptor.MipUploads is { Length: > 0 } ||
            descriptor.BaseMipLevel != 0 || descriptor.MipLevel != 0 ||
            !descriptor.Shape.Storage || descriptor.Shape.NumericClass == TextureNumericClass.Unsupported ||
            descriptor.Shape.DynamicMip || descriptor.Shape.Arrayed || descriptor.Shape.Cube ||
            descriptor.Shape.OneDimensional || descriptor.Shape.Multisampled || descriptor.Shape.DepthCompare || descriptor.Shape.R128 || descriptor.Shape.Atomic)
            throw new NotSupportedException(
                "Metal storage image materialization supports only a single selected base mip, non-array 2D or 3D view (3D extent <=2048): " +
                $"addr=0x{descriptor.Address:X} type={descriptor.Type} size={descriptor.Width}x{descriptor.Height} " +
                $"arrayed={descriptor.ArrayedView} layers={descriptor.ArrayLayers} depth={descriptor.Depth} " +
                $"view_mips={descriptor.MipLevels} base_mip={descriptor.BaseMipLevel} selected_mip={descriptor.MipLevel} " +
                $"resource_mips={descriptor.ResourceMipLevels} fmt={descriptor.Format}/{descriptor.NumberType} " +
                $"pitch={descriptor.Pitch} tile={descriptor.TileMode} shape={descriptor.Shape} access={descriptor.Access}.");
        if (volume && descriptor.TileMode != 0 &&
            (descriptor.Width != 1 || descriptor.Height != 1 || descriptor.Depth != 1 ||
                descriptor.TileMode != 27 || descriptor.ResourceMipLevels != 1 ||
                (descriptor.Address & 0xFFFFUL) != 0 || !GnmTiling.NeedsDetile(descriptor.TileMode)))
            throw new NotSupportedException(
                $"Metal tiled volume seed supports only an aligned mode27 single-mip 1x1x1 origin texel; " +
                $"addr=0x{descriptor.Address:X} depth={descriptor.Depth} tile={descriptor.TileMode}.");
        var numeric = format.Format switch
        {
            MtlPixelFormat.R8Uint or MtlPixelFormat.R16Uint or MtlPixelFormat.R32Uint or
                MtlPixelFormat.Rg8Uint or MtlPixelFormat.Rg16Uint or MtlPixelFormat.Rg32Uint or
                MtlPixelFormat.Rgba8Uint or MtlPixelFormat.Rgba16Uint or MtlPixelFormat.Rgba32Uint => TextureNumericClass.Uint,
            MtlPixelFormat.R8Sint or MtlPixelFormat.R16Sint or MtlPixelFormat.R32Sint or
                MtlPixelFormat.Rg8Sint or MtlPixelFormat.Rg16Sint or MtlPixelFormat.Rg32Sint or
                MtlPixelFormat.Rgba8Sint or MtlPixelFormat.Rgba16Sint or MtlPixelFormat.Rgba32Sint => TextureNumericClass.Sint,
            _ => TextureNumericClass.Float,
        };
        if (descriptor.Shape.NumericClass != numeric)
            throw new NotSupportedException($"Metal storage image numeric binding mismatch at 0x{descriptor.Address:X}: shader={descriptor.Shape.NumericClass} native={numeric} fmt={descriptor.Format}/{descriptor.NumberType}.");
        return format;
    }

    internal static (uint Pitch, ulong RequiredBytes) StorageTextureSeedLayout(GuestDrawTexture descriptor, MetalTextureFormat format)
    {
        var pitch = Math.Max(descriptor.Width, descriptor.Pitch);
        // 3D upload walks full padded image strides, including depth one.
        // A short final row is sufficient only for the 2D region API.
        var required = descriptor.Type == Gen5TextureType3D
            ? checked((ulong)pitch * descriptor.Height * descriptor.Depth * format.BytesPerPixel)
            : descriptor.Height == 0 ? 0 : checked(
                ((ulong)(descriptor.Height - 1) * pitch + descriptor.Width) * format.BytesPerPixel);
        if (required > int.MaxValue)
            throw new NotSupportedException("Metal storage image seed exceeds the bounded managed snapshot size.");
        if (descriptor.RgbaPixels.Length != 0 &&
            (!descriptor.CpuSnapshotStable || (ulong)descriptor.RgbaPixels.LongLength < required))
            throw new InvalidOperationException("Metal storage image seed must contain every complete stable native row and padded volume slice.");
        return (pitch, required);
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
