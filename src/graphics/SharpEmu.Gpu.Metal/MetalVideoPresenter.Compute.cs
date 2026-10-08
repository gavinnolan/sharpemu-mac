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
                if (_selectedStorageMipAssociations.TryGetValue(texture, out _)) continue;
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
            if (dispatch.Textures.Any(texture => _selectedStorageMipAssociations.TryGetValue(texture, out _)))
                throw new NotSupportedException("Metal selected storage mip writes cannot be skipped.");
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
            if (dispatch.Textures.Any(texture => _selectedStorageMipAssociations.TryGetValue(texture, out _)))
                throw new InvalidOperationException("Metal selected storage mip dispatch pipeline could not be created.");
            ReturnPooledComputeData(dispatch);
            return;
        }

        var commandBuffer = _computeBeginCommandsOverride is { } beginCommands
            ? beginCommands(queue) : BeginBatchedGuestCommands(queue);
        if (commandBuffer == 0)
            throw new InvalidOperationException("Metal compute dispatch cannot create its command buffer.");

        // Pre-resolve textures before the compute encoder opens: snapshot
        // blits for feedback reads encode into the batch and encoder order
        // must place them ahead of this dispatch.
        Span<nint> textureHandles = stackalloc nint[dispatch.Textures.Length];
        Span<bool> textureOwned = stackalloc bool[dispatch.Textures.Length];
        // stackalloc storage is not an ownership receipt until explicitly set.
        textureHandles.Clear();
        textureOwned.Clear();
        Exception? dispatchFailure = null;
        try
        {
            for (var index = 0; index < dispatch.Textures.Length; index++)
            {
                var descriptor = dispatch.Textures[index];
                if (descriptor.IsStorage && descriptor.Address != 0 &&
                    (!_textureSourceGraphEnabled || StorageImageWrites(descriptor)))
                {
                    ValidateSelectedStorageMipExecution(descriptor, queue);
                    if (_selectedStorageMipAssociations.TryGetValue(descriptor, out _) &&
                        MetalNative.Send(commandBuffer, MetalNative.Selector("retainedReferences")) == 0)
                        throw new NotSupportedException("Metal selected storage resources require native command-buffer retention through completion.");
                    textureHandles[index] = EnsureStorageImage(device, descriptor).Texture;
                    textureOwned[index] = false;
                }
                else
                {
                    if (_computeTextureCreationOverride is { } createTexture)
                    {
                        var created = createTexture(device, commandBuffer, descriptor);
                        textureHandles[index] = created.Handle;
                        textureOwned[index] = created.Owned;
                    }
                    else
                    {
                        textureHandles[index] = CreateDrawTexture(
                            device, commandBuffer, descriptor, out var ownedTexture);
                        textureOwned[index] = ownedTexture;
                    }
                }
            }

            var encoder = _computeEncoderCreationOverride is { } createEncoder
                ? createEncoder(commandBuffer)
                : MetalNative.Send(commandBuffer, MetalNative.Selector("computeCommandEncoder"));
            if (encoder == 0)
                throw new InvalidOperationException("Metal compute dispatch cannot create its compute encoder.");
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
        }
        catch (Exception exception)
        {
            dispatchFailure = exception;
            throw;
        }
        finally
        {
            ReleaseComputeTemporaryTextures(textureHandles, textureOwned, dispatchFailure);
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
            HashSet<SelectedStorageMipKey>? selectedChanged = null;
            foreach (var descriptor in textures)
                if (descriptor.Address != 0 && StorageImageWrites(descriptor))
                {
                    if (_selectedStorageMipAssociations.TryGetValue(descriptor, out var selected))
                    {
                        if ((selectedChanged ??= new()).Add(selected.Key) &&
                            _selectedStorageMipImages.TryGetValue(selected.Key, out var selectedImage)) selectedImage.MarkContentChangedWithDiagnostic("storage-dispatch");
                        if (_selectedStorageMipPending.TryGetValue(selected.Key, out var pending) && pending.Sequence == _executingGuestWorkSequence)
                            _selectedStorageMipPending.Remove(selected.Key);
                    }
                    else if (changed.Add(descriptor.Address) && _guestImages.TryGetValue(descriptor.Address, out var image)) image.MarkContentChangedWithDiagnostic("storage-dispatch");
                }
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
        if (!_selectedStorageMipAssociations.TryGetValue(descriptor, out _))
            RejectAddressOnlySelectedStorageAlias(descriptor.Address);
        descriptor = PrepareDrawTextureUpload(descriptor);
        var seedLayout = StorageTextureSeedLayout(descriptor, nativeFormat);
        _selectedStorageMipAssociations.TryGetValue(descriptor, out var selected);
        lock (_gate)
        {
            if (selected is not null) RejectConflictingSelectedStorageConstructionLocked(selected);
            if (selected is not null ? _selectedStorageMipImages.TryGetValue(selected.Key, out var existing) :
                _guestImages.TryGetValue(descriptor.Address, out existing))
            {
                if (selected is not null)
                {
                    ValidateSelectedStorageMipOwner(_selectedStorageMipOwners[selected.Key], requireObserved: true);
                    ValidateSelectedStorageMipOwner(selected);
                }
                ValidateStorageImageView(existing, descriptor, nativeFormat);
                return existing;
            }
        }

        if (selected is not null)
        {
            ValidateSelectedStorageMipOwner(selected);
            lock (_gate)
            {
                if (_selectedStorageMipImages.Count >= 128)
                    throw new NotSupportedException("Metal selected storage native owner limit exceeded.");
                _selectedStorageMipImages.EnsureCapacity(_selectedStorageMipImages.Count + 1);
                _selectedStorageMipOwners.EnsureCapacity(_selectedStorageMipOwners.Count + 1);
            }
        }
        if (descriptor.RgbaPixels.Length == 0)
            throw new InvalidOperationException($"Metal storage image 0x{descriptor.Address:X} has no complete initialization source.");
        var format = nativeFormat.Format;
        var shape = GuestTextureShape(descriptor);
        var extent = (descriptor.Width, descriptor.Height,
            checked((ulong)descriptor.Width * descriptor.Height * shape.Depth * shape.ArrayLength * nativeFormat.BytesPerPixel));
        if (selected is null)
            lock (_gate)
            {
                _guestImages.EnsureCapacity(_guestImages.Count + 1);
                _guestImageExtents.EnsureCapacity(_guestImageExtents.Count + 1);
            }
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
        MetalNative.Send(textureDescriptor, MetalNative.Selector("setArrayLength:"), (nint)shape.ArrayLength);
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
            ArrayLength = shape.ArrayLength,
            Format = format,
            DiagnosticOrigin = CaptureStorageImageOrigin(descriptor),
        };
        if (image.Texture == 0)
            throw new InvalidOperationException($"Metal cannot allocate storage image 0x{descriptor.Address:X} with format {format}.");

        var installed = false;
        Exception? r16ConstructionFailure = null;
        try
        {
            // CPU replace owns its source bytes synchronously; native binding
            // commands independently retain the installed texture.
            ReplaceDrawTextureContents(image.Texture, descriptor, seedLayout.Pitch, nativeFormat);
            image.Initialized = true;
            image.ContentVersion++;
            lock (_gate)
            {
                if (selected is not null) RejectConflictingSelectedStorageConstructionLocked(selected);
                if (selected is not null ? _selectedStorageMipImages.TryGetValue(selected.Key, out var raced) :
                    _guestImages.TryGetValue(descriptor.Address, out raced))
                {
                    if (selected is not null)
                    {
                        ValidateSelectedStorageMipOwner(selected);
                        ValidateSelectedStorageMipOwner(_selectedStorageMipOwners[selected.Key], requireObserved: true);
                    }
                    ValidateStorageImageView(raced, descriptor, nativeFormat);
                    return raced;
                }
                PublishStorageImageLocked(descriptor, selected, image, extent);
                installed = true;
            }
            return image;
        }
        catch (Exception failure) { r16ConstructionFailure = failure; throw; }
        finally
        {
            // All construction, upload, raced lookup, and publication failures
            // flow through the same one-retain cleanup. A raced image is owned
            // by its registry; only this call's new texture is released here.
            if (!installed)
            {
                if (selected is not null && MetalR16FloatSelectedMip.TryPlacement(selected.Key.Resource, selected.Key.Mip, out _))
                {
                    var owned = image.Texture; image.Texture = 0;
                    ReleaseR16SelectedConstructionRetain(owned, r16ConstructionFailure);
                }
                else MetalNative.SendVoid(image.Texture, MetalNative.Selector("release"));
            }
        }
    }

    private static void ValidateStorageImageView(GuestImage image, GuestDrawTexture descriptor, MetalTextureFormat format)
    {
        if (_selectedStorageMipAssociations.TryGetValue(descriptor, out var selected) &&
            MetalR16FloatSelectedMip.TryPlacement(selected.Key.Resource, selected.Key.Mip, out _) &&
            (!image.R16SelectedOwnsRetain || image.Texture == 0))
            throw new InvalidOperationException("Metal R16Float selected live image lacks its accepted native retain.");
        var shape = GuestTextureShape(descriptor);
        var observed = (image.Width, image.Height, image.Format, image.TextureType, image.Depth, image.ArrayLength);
        var requested = (descriptor.Width, descriptor.Height, format.Format, shape.TextureType, shape.Depth, shape.ArrayLength);
        if (observed != requested)
            throw StorageImageMismatch($"Metal storage image view does not match live image at 0x{descriptor.Address:X}: " +
                $"observed_width={observed.Width} observed_height={observed.Height} observed_format={(uint)observed.Format} " +
                $"observed_type={observed.TextureType} observed_depth={observed.Depth} observed_layers={observed.ArrayLength} " +
                $"requested_width={requested.Width} requested_height={requested.Height} requested_format={(uint)requested.Format} " +
                $"requested_type={requested.TextureType} requested_depth={requested.Depth} requested_layers={requested.ArrayLength} " +
                $"guest_format={descriptor.Format}/{descriptor.NumberType} guest_type={descriptor.Type} " +
                $"base_mip={descriptor.BaseMipLevel} selected_mip={descriptor.MipLevel} numeric_class={descriptor.Shape.NumericClass}.", image, descriptor);
        if (!image.Initialized)
            throw new InvalidOperationException($"Metal storage image 0x{descriptor.Address:X} has no initialized contents.");
    }

    internal static MetalTextureFormat StorageTextureFormat(GuestDrawTexture descriptor)
    {
        if (descriptor.Width == 0 || descriptor.Height == 0 || descriptor.Width > 16384 || descriptor.Height > 16384)
            throw new NotSupportedException($"Metal storage image 0x{descriptor.Address:X} has unsupported extent {descriptor.Width}x{descriptor.Height}.");
        var format = MetalGuestFormats.DecodeTextureFormat(descriptor.Format, descriptor.NumberType);
        if (MetalR16FloatSelectedMip.RequiresPhysicalSelectedView(descriptor.Width, descriptor.Height,
                descriptor.Format, descriptor.NumberType, descriptor.TileMode, descriptor.ResourceMipLevels) &&
            !IsQualifiedSelectedStorageMip(descriptor))
            throw new NotSupportedException("Metal R16Float mip-chain storage requires its exact qualified selected physical view, including mip0.");
        if (format.IsBlockCompressed)
            throw new NotSupportedException($"Metal block-compressed storage image format {descriptor.Format}/{descriptor.NumberType} is unsupported.");
        // The descriptor and compiled shader must agree on native dimension,
        // including depth-one texture3d. No array/cube/mip reinterpretation.
        var volume = descriptor.Type == Gen5TextureType3D;
        var qualifiedArray = MetalSingletonArrayStorageAdmission.IsQualified(descriptor) ||
            MetalBgra1024ArrayStorageAdmission.IsQualified(descriptor) ||
            MetalR8FourKArrayStorageAdmission.IsQualified(descriptor);
        if (volume && (descriptor.TiledSource is not null || descriptor.Detile is not null))
            throw new NotSupportedException("Metal storage volumes require owned linear seed bytes; deferred XY-only detiling is not a volume source proof.");
        if (volume && (descriptor.Descriptor is not { Length: >= 8 } raw ||
            !TryDecodeTextureDescriptor(raw, out var sourceDescriptor) ||
            sourceDescriptor.MetadataAddress != 0 || sourceDescriptor.DescriptorFlags != 0 || sourceDescriptor.BcSwizzle != 0 ||
            sourceDescriptor.Type != descriptor.Type || sourceDescriptor.Address != descriptor.Address ||
            sourceDescriptor.Width != descriptor.Width || sourceDescriptor.Height != descriptor.Height ||
            sourceDescriptor.Depth != descriptor.Depth || sourceDescriptor.TileMode != descriptor.TileMode ||
            sourceDescriptor.Format != descriptor.Format || sourceDescriptor.NumberType != descriptor.NumberType ||
            sourceDescriptor.DstSelect != descriptor.DstSelect ||
            sourceDescriptor.BaseLevel != 0 || sourceDescriptor.LastLevel != 0 || sourceDescriptor.MaxMip != 0))
            throw new NotSupportedException(
                $"Metal volume storage requires a matching complete single-mip uncompressed descriptor with zero metadata/flags/BC swizzle: " +
                $"addr=0x{descriptor.Address:X} raw=[{string.Join(",", descriptor.Descriptor?.Select(static word => $"{word:X8}") ?? [])}].");
        if ((descriptor.Type != 9 && !volume && !qualifiedArray) || descriptor.Shape.Volume != volume ||
            descriptor.ArrayedView != qualifiedArray || descriptor.ArrayLayers != 1 ||
            (!volume && descriptor.Depth != 1) || descriptor.Depth == 0 ||
            (volume && (descriptor.Width > 2048 || descriptor.Height > 2048 || descriptor.Depth > 2048)) ||
            descriptor.MipLevels != 1 || descriptor.MipUploads is { Length: > 0 } ||
            (descriptor.BaseMipLevel != 0 || descriptor.MipLevel != 0) && !IsQualifiedSelectedStorageMip(descriptor) ||
            !descriptor.Shape.Storage || descriptor.Shape.NumericClass == TextureNumericClass.Unsupported ||
            descriptor.Shape.DynamicMip || descriptor.Shape.Arrayed != qualifiedArray || descriptor.Shape.Cube ||
            descriptor.Shape.OneDimensional || descriptor.Shape.Multisampled || descriptor.Shape.DepthCompare || descriptor.Shape.R128 || descriptor.Shape.Atomic)
            throw new NotSupportedException(
                "Metal storage image materialization supports a single base mip, the qualified 2D/mip1 view, non-array 3D or qualified singleton or exact1024 BGRA 2D-array (3D extent <=2048): " +
                $"addr=0x{descriptor.Address:X} type={descriptor.Type} size={descriptor.Width}x{descriptor.Height} " +
                $"arrayed={descriptor.ArrayedView} layers={descriptor.ArrayLayers} depth={descriptor.Depth} " +
                $"view_mips={descriptor.MipLevels} base_mip={descriptor.BaseMipLevel} selected_mip={descriptor.MipLevel} " +
                $"resource_mips={descriptor.ResourceMipLevels} fmt={descriptor.Format}/{descriptor.NumberType} " +
                $"pitch={descriptor.Pitch} tile={descriptor.TileMode} shape={descriptor.Shape} access={descriptor.Access}.");
        var qualifiedThickVolume = MetalVolumeStorageAdmission.IsQualified(
            descriptor.Address, descriptor.Width, descriptor.Height, descriptor.Depth,
            descriptor.Format, descriptor.NumberType, descriptor.Type, descriptor.TileMode,
            descriptor.ResourceMipLevels, descriptor.DstSelect);
        if (volume && descriptor.TileMode != 0 && !qualifiedThickVolume &&
            (descriptor.Width != 1 || descriptor.Height != 1 || descriptor.Depth != 1 ||
                descriptor.TileMode != 27 || descriptor.ResourceMipLevels != 1 ||
                (descriptor.Address & 0xFFFFUL) != 0 || !GnmTiling.NeedsDetile(descriptor.TileMode)))
            throw new NotSupportedException(
                $"Metal tiled volume seed supports only a qualified mode5 R16Uint 16x16x16 or aligned mode27 single-mip 1x1x1 origin texel; " +
                $"addr=0x{descriptor.Address:X} depth={descriptor.Depth} tile={descriptor.TileMode}.");
        if (qualifiedThickVolume && (descriptor.Pitch != 16 ||
            !MetalVolumeStorageAdmission.HasQualifiedRawFields(descriptor.Descriptor!)))
            throw new NotSupportedException("The qualified thick volume requires its exact raw fields and 16-texel linear seed pitch.");
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
        // Array/3D slice uploads walk full padded image strides, even at one slice.
        // A short final row is sufficient only for the 2D region API.
        var required = descriptor.Type == Gen5TextureType3D || descriptor.ArrayedView
            ? checked((ulong)pitch * descriptor.Height * descriptor.Depth * descriptor.ArrayLayers * format.BytesPerPixel)
            : descriptor.Height == 0 ? 0 : checked(
                ((ulong)(descriptor.Height - 1) * pitch + descriptor.Width) * format.BytesPerPixel);
        if (required > int.MaxValue)
            throw new NotSupportedException("Metal storage image seed exceeds the bounded managed snapshot size.");
        if (descriptor.RgbaPixels.Length != 0 &&
            (!descriptor.CpuSnapshotStable || (ulong)descriptor.RgbaPixels.LongLength < required))
            throw new InvalidOperationException("Metal storage image seed must contain every complete stable native row and padded array/volume slice.");
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
        if (_textureSourceGraphEnabled) { ReleaseGuestPayloadWork(dispatch); return; }
        foreach (var buffer in dispatch.GlobalMemoryBuffers)
        {
            if (buffer.Pooled)
            {
                GuestDataPool.Shared.Return(buffer.Data);
            }
        }
    }
}
