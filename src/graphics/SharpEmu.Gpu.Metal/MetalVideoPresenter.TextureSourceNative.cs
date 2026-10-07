// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Render-thread ownership. Every batch retains its command buffer and pins
    // the private versions used indirectly through argument buffers. Encoding
    // completion never releases these pins; native completion/error does.
    private static long _nextTextureSourceNativeBatchIdentity;
    private sealed class TextureSourceNativeBatch
    {
        public readonly long Identity = Interlocked.Increment(ref _nextTextureSourceNativeBatchIdentity);
        public readonly nint CommandBuffer;
        public readonly HashSet<TextureSourceVersion> Versions = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<TextureSourceVersion> ProducedVersions = new(ReferenceEqualityComparer.Instance);
        public bool Committed;
        public TextureSourceNativeBatch(nint commandBuffer) =>
            CommandBuffer = MetalNative.Send(commandBuffer, MetalNative.Selector("retain"));
    }
    private static readonly Dictionary<nint, TextureSourceNativeBatch> _textureSourceNativeBatches = new();
    private static object? _executingTextureSourceWork;
    private static nint _textureSourceSerialQueue;

    private static TextureSourceWorkOwner ExecutingTextureSourceOwner()
    {
        lock (_gate)
        {
            if (_executingTextureSourceWork is null ||
                !_textureSourceWorkOwners.TryGetValue(_executingTextureSourceWork, out var owner) || owner.Released)
                throw new InvalidOperationException("Metal native texture source work has no admitted owner.");
            return owner;
        }
    }

    private static void PinTextureSourceNativeUse(nint commandBuffer, TextureSourceVersion version)
    {
        lock (_gate)
        {
            if (commandBuffer == 0 || MetalNative.Send(commandBuffer, MetalNative.Selector("retainedReferences")) == 0)
                throw new NotSupportedException("Metal ordered sources require retained command-buffer resources.");
            var queue = MetalNative.Send(commandBuffer, MetalNative.Selector("commandQueue"));
            if (queue == 0 || _textureSourceSerialQueue != 0 && _textureSourceSerialQueue != queue)
                throw new NotSupportedException("Metal ordered source encoding requires one serial native command queue.");
            _textureSourceSerialQueue = queue;
            if (version.Failed || version.NativePins == int.MaxValue)
                throw new InvalidOperationException("Metal ordered native texture source cannot be pinned.");
            if (!_textureSourceNativeBatches.TryGetValue(commandBuffer, out var batch))
            {
                batch = new(commandBuffer);
                try { _textureSourceNativeBatches.Add(commandBuffer, batch); }
                catch { MetalNative.SendVoid(batch.CommandBuffer, MetalNative.Selector("release")); throw; }
            }
            if (batch.Committed) throw new InvalidOperationException("Metal source use cannot enter a committed command buffer.");
            if (batch.Versions.Add(version)) version.NativePins++;
            RecordTextureSourceNativeWorkUseLocked(batch);
        }
    }

    private static void RecordTextureSourceNativeWorkUseLocked(TextureSourceNativeBatch batch)
    {
        if (_executingTextureSourceWork is { } work &&
            _textureSourceWorkOwners.TryGetValue(work, out var owner) && !owner.Released)
            owner.NativeUseBatchIdentity = batch.Identity;
    }

    private static void ValidateTextureSourceNativeView(TextureSourceVersion source, nint texture)
    {
        if (texture == 0 || MetalNative.Send(texture, MetalNative.Selector("textureType")) != (nint)MtlTextureType2D ||
            MetalNative.Send(texture, MetalNative.Selector("width")) != (nint)source.View.Width ||
            MetalNative.Send(texture, MetalNative.Selector("height")) != (nint)source.View.Height ||
            MetalNative.Send(texture, MetalNative.Selector("depth")) != 1 ||
            MetalNative.Send(texture, MetalNative.Selector("arrayLength")) != 1 ||
            MetalNative.Send(texture, MetalNative.Selector("mipmapLevelCount")) != 1 ||
            MetalNative.Send(texture, MetalNative.Selector("sampleCount")) != 1 ||
            MetalNative.Send(texture, MetalNative.Selector("pixelFormat")) !=
                (nint)MetalGuestFormats.DecodeTextureFormat(source.View.Format, source.View.NumberType).Format)
            throw new NotSupportedException($"Metal ordered source version {source.Identity} has incompatible actual native texture view.");
    }

    private static nint AllocateTextureSourceNativeImage(nint device, TextureSourceVersion source)
    {
        var format = MetalGuestFormats.DecodeTextureFormat(source.View.Format, source.View.NumberType).Format;
        var descriptor = MetalNative.SendTextureDescriptor(MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format, source.View.Width, source.View.Height, false);
        MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), 0);
        MetalNative.Send(descriptor, MetalNative.Selector("setUsage:"), (nint)(UsageShaderRead | UsageShaderWrite | UsageRenderTarget));
        var texture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        if (texture == 0) throw new InvalidOperationException($"Metal cannot allocate ordered texture version {source.Identity}.");
        try { ValidateTextureSourceNativeView(source, texture); return texture; }
        catch { MetalNative.SendVoid(texture, MetalNative.Selector("release")); throw; }
    }

    private static void MaterializeTextureSourceOutputs(nint device, nint commandBuffer)
    {
        if (!_textureSourceGraphEnabled) return;
        var owner = ExecutingTextureSourceOwner();
        foreach (var source in owner.Outputs)
        {
            if (source.Failed || !source.Budgeted || source.Texture != 0 || source.Completed)
                throw new InvalidOperationException("Metal ordered output is failed, unbudgeted or already materialized.");
            _ = source.Descriptor ?? throw new InvalidOperationException("Metal ordered output lost its descriptor.");
            if (source.Initializer is null && !source.ClearsOutput)
                throw new NotSupportedException("Metal ordered native CPU initialization requires a backing/copy lease; only retained immutable GPU initialization is qualified.");
            var texture = AllocateTextureSourceNativeImage(device, source);
            source.Texture = texture; // version owns the allocation until pruning
            PinTextureSourceNativeUse(commandBuffer, source);
            var image = new GuestImage { Texture = texture, Width = source.View.Width, Height = source.View.Height,
                Format = MetalGuestFormats.DecodeTextureFormat(source.View.Format, source.View.NumberType).Format };
            source.Image = image;
            if (source.Initializer is { } initializer)
            {
                RequireTextureSourceCopyReady(initializer, commandBuffer);
                ValidateTextureSourceNativeView(source, initializer.ImmutableTexture);
                PinTextureSourceNativeUse(commandBuffer, initializer);
                EncodeCopyTexture(commandBuffer, initializer.ImmutableTexture, texture);
                image.Initialized = true;
            }
            else if (!source.ClearsOutput)
                throw new NotSupportedException("Metal ordered native CPU initialization requires a backing/copy lease; only retained immutable GPU initialization is qualified.");
            // The live registry owns a separate retain. Capacity and rollback
            // metadata are established before that extra native owner exists.
            lock (_gate)
            {
                _guestImages.EnsureCapacity(_guestImages.Count + 1);
                _guestImageExtents.EnsureCapacity(_guestImageExtents.Count + 1);
                var hadPrevious = _guestImages.TryGetValue(source.View.Address, out var previous);
                var hadExtent = _guestImageExtents.TryGetValue(source.View.Address, out var previousExtent);
                var extent = (source.View.Width, source.View.Height, source.ObservedGuestByteCount);
                MetalNative.Send(texture, MetalNative.Selector("retain"));
                try
                {
                    _guestImages[source.View.Address] = image;
                    _guestImageExtents[source.View.Address] = extent;
                }
                catch
                {
                    if (hadPrevious) _guestImages[source.View.Address] = previous!;
                    else _guestImages.Remove(source.View.Address);
                    if (hadExtent) _guestImageExtents[source.View.Address] = previousExtent;
                    else _guestImageExtents.Remove(source.View.Address);
                    MetalNative.SendVoid(texture, MetalNative.Selector("release"));
                    throw;
                }
                if (previous is not null)
                {
                    previous.ReleaseSnapshot();
                    MetalNative.SendVoid(previous.Texture, MetalNative.Selector("release"));
                }
            }
        }
    }

    private static bool TryResolveCapturedTextureSource(nint commandBuffer, GuestDrawTexture texture, out nint handle)
    {
        handle = 0;
        lock (_gate)
        {
            if (!_textureSourceLeases.TryGetValue(texture, out var lease)) return false;
            var source = lease.Version;
            if (lease.References <= 0 || source.View != TextureSourceView.Of(texture))
                throw new InvalidOperationException("Metal captured source has no retained safely ordered immutable native version.");
            // A detached immutable version deliberately ignores subsequent guest
            // address retirement/mapping epochs: no late guest read is needed.
            RequireTextureSourceCopyReady(source, commandBuffer);
            ValidateTextureSourceNativeView(source, source.ImmutableTexture);
            PinTextureSourceNativeUse(commandBuffer, source);
            handle = source.ImmutableTexture;
            return true;
        }
    }

    private static void EncodeTextureSourceOutputCopies(nint device, nint commandBuffer)
    {
        if (!_textureSourceGraphEnabled) return;
        foreach (var source in ExecutingTextureSourceOwner().Outputs)
        {
            if (source.Failed || source.Completed || source.ImmutableTexture != 0 || source.Image is not { } image ||
                image.Texture != source.Texture || !image.Initialized)
                throw new InvalidOperationException("Metal ordered writer did not produce its exact admitted output.");
            Require2DGuestImage(image, "ordered immutable source copy");
            ValidateTextureSourceNativeView(source, source.Texture);
            source.ImmutableTexture = AllocateTextureSourceNativeImage(device, source);
            PinTextureSourceNativeUse(commandBuffer, source);
            EncodeCopyTexture(commandBuffer, source.Texture, source.ImmutableTexture);
            // Certifies ordered copy encoding, never successful GPU completion.
            var copyBatch = _textureSourceNativeBatches[commandBuffer];
            copyBatch.ProducedVersions.Add(source);
            source.CopyBatch = copyBatch;
            source.Completed = true;
            source.MutationStarted = true;
        }
    }

    private static void RequireTextureSourceCopyReady(TextureSourceVersion source, nint consumerCommandBuffer)
    {
        if (source.Failed || !source.Completed || source.ImmutableTexture == 0 || source.CopyBatch is not { } producer ||
            !source.NativeCompleted && producer.CommandBuffer != consumerCommandBuffer && !producer.Committed)
            throw new InvalidOperationException($"Metal immutable source version {source.Identity} producer={source.ProducerSequence} has no ordered producing command buffer for this consumer.");
    }

    // Own the real native commit operation; no metadata-only publication can
    // authorize a second buffer to read an uncommitted producer's copy.
    // Private failure-injection boundary. Never assigned by production code;
    // tests fail before the real commit without inventing native completion.
    private static Action? _textureSourceBeforeNativeCommit;

    private static void CommitTextureSourceNativeCommands(nint commandBuffer)
    {
        if (_textureSourceNativeBatches.TryGetValue(commandBuffer, out var batch) && batch.Committed)
            throw new InvalidOperationException("Metal source command buffer has already been committed.");
        _textureSourceBeforeNativeCommit?.Invoke();
        MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
        if (batch is not null) batch.Committed = true;
    }

    private static void AbortTextureSourceNativeBatch(nint commandBuffer)
    {
        lock (_gate)
        {
            if (!_textureSourceNativeBatches.TryGetValue(commandBuffer, out var batch)) return;
            if (batch.Committed) throw new InvalidOperationException("Metal committed source batch cannot be discarded.");
            foreach (var source in batch.Versions)
            {
                source.Failed = true;
                source.Completed = false;
                source.NativePins--;
            }
            _textureSourceNativeBatches.Remove(commandBuffer);
            MetalNative.SendVoid(batch.CommandBuffer, MetalNative.Selector("release"));
            PruneTextureSourceNativeVersionsLocked();
        }
    }

    private static void RecycleTextureSourceNativeBatches()
    {
        lock (_gate)
        {
            foreach (var pair in _textureSourceNativeBatches.ToArray())
            {
                var batch = pair.Value;
                if (!batch.Committed) continue;
                var status = (long)MetalNative.Send(batch.CommandBuffer, MetalNative.Selector("status"));
                if (status < 4) continue;
                // Error decoding/context allocation can fail. Build it before
                // changing ownership; shutdown must see either all pins or none.
                var failureContext = status == 4 ? null :
                    $"Metal ordered source command buffer failed: status={status} error='{MetalNative.DescribeError(MetalNative.Send(batch.CommandBuffer, MetalNative.Selector("error")))}' " +
                    string.Join("; ", batch.ProducedVersions.Select(static source =>
                        $"producer={source.ProducerSequence} queue='{source.ProducerQueue.Name}' submission={source.ProducerQueue.SubmissionId} version={source.Identity} image=0x{source.View.Address:X}"));
                foreach (var source in batch.Versions)
                {
                    source.NativePins--;
                    if (status == 4)
                    {
                        if (batch.ProducedVersions.Contains(source) && source.Completed) source.NativeCompleted = true;
                    }
                    else { source.Failed = true; source.Completed = false; }
                }
                _textureSourceNativeBatches.Remove(pair.Key);
                MetalNative.SendVoid(batch.CommandBuffer, MetalNative.Selector("release"));
                if (status != 4)
                {
                    InvalidateTextureSourceVersionsLocked();
                    throw new InvalidOperationException(failureContext);
                }
            }
            RetireUnreferencedLatestTextureSourcesLocked();
            PruneTextureSourceNativeVersionsLocked();
        }
    }

    private static void RetireUnreferencedLatestTextureSourcesLocked()
    {
        // Leave headroom without blocking CPU admission while a render-owned
        // gate is held. Eviction never steals a captured version or reads guest
        // bytes later to reconstruct a missing source.
        const ulong softBudget = 192UL * 1024 * 1024;
        foreach (var source in _textureSourceVersions)
        {
            if (_immutableTextureSourceBytes <= softBudget) break;
            if (!source.NativeCompleted || !source.ProducerReleased || source.NativePins != 0 || source.References != 0 ||
                !_latestTextureSources.TryGetValue(source.View.Address, out var latest) || !ReferenceEquals(latest, source)) continue;
            _latestTextureSources.Remove(source.View.Address);
            source.Retired = true;
            PruneTextureSourceNativeVersionsLocked();
            // Pruning mutates the list; another render iteration handles the
            // next eviction rather than enumerating a modified collection.
            break;
        }
    }

    private static void ShutdownTextureSourceNativeOwners()
    {
        if (Monitor.IsEntered(_gate))
            throw new InvalidOperationException("Metal source shutdown must not wait while holding the presenter gate.");
        nint[] committed;
        lock (_gate)
        {
            InvalidateTextureSourceVersionsLocked();
            foreach (var work in _textureSourceWorkOwners.Select(static pair => pair.Key).ToArray())
                ReleaseTextureSourceWorkOwnerLocked(work);
            foreach (var batch in _textureSourceNativeBatches.Values.Where(static batch => !batch.Committed).ToArray())
                AbortTextureSourceNativeBatch(batch.CommandBuffer);
            committed = _textureSourceNativeBatches.Values.Select(static batch => batch.CommandBuffer).ToArray();
        }
        // Native work is independent of the presenter gate. Waiting under it
        // could strand guest admission or an ordered work needed for progress.
        foreach (var commandBuffer in committed)
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("waitUntilCompleted"));
        lock (_gate)
        {
            foreach (var batch in _textureSourceNativeBatches.Values)
            {
                foreach (var source in batch.Versions) source.NativePins--;
                MetalNative.SendVoid(batch.CommandBuffer, MetalNative.Selector("release"));
            }
            _textureSourceNativeBatches.Clear();
            _latestTextureSources.Clear();
            _gpuTextureSourceAddresses.Clear();
            foreach (var source in _textureSourceVersions)
            {
                RemoveTextureSourceRegistryOwnerLocked(source);
                source.Retired = true;
                if (source.Texture != 0) MetalNative.SendVoid(source.Texture, MetalNative.Selector("release"));
                if (source.ImmutableTexture != 0) MetalNative.SendVoid(source.ImmutableTexture, MetalNative.Selector("release"));
                source.Texture = source.ImmutableTexture = 0;
                source.Budgeted = false;
                source.Descriptor = null;
                source.Initializer = null;
            }
            _textureSourceVersions.Clear();
            _immutableTextureSourceBytes = 0;
            _textureSourceSerialQueue = 0;
        }
    }

    private static void RemoveTextureSourceRegistryOwnerLocked(TextureSourceVersion source)
    {
        if (source.Image is { } owned && _guestImages.TryGetValue(source.View.Address, out var current) && ReferenceEquals(owned, current))
        {
            _guestImages.Remove(source.View.Address);
            _guestImageExtents.Remove(source.View.Address);
            owned.ReleaseSnapshot();
            MetalNative.SendVoid(owned.Texture, MetalNative.Selector("release"));
        }
    }

    private static void PruneTextureSourceNativeVersionsLocked()
    {
        for (var index = _textureSourceVersions.Count - 1; index >= 0; index--)
        {
            var source = _textureSourceVersions[index];
            if (source.References != 0 || source.NativePins != 0 || !source.ProducerReleased ||
                _latestTextureSources.TryGetValue(source.View.Address, out var latest) && ReferenceEquals(latest, source)) continue;
            RemoveTextureSourceRegistryOwnerLocked(source);
            if (source.Texture != 0) MetalNative.SendVoid(source.Texture, MetalNative.Selector("release"));
            if (source.ImmutableTexture != 0) MetalNative.SendVoid(source.ImmutableTexture, MetalNative.Selector("release"));
            source.Texture = source.ImmutableTexture = 0;
            if (source.Budgeted) { _immutableTextureSourceBytes -= source.ByteCount * 2; source.Budgeted = false; }
            source.Descriptor = null;
            source.Initializer = null;
            source.CopyBatch = null;
            _textureSourceVersions.RemoveAt(index);
        }
    }
}
