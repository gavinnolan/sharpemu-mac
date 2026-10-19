// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Remains false until production native copy/completion proof is complete.
    // This is a private integration boundary, not a user-selectable fallback.
    private static bool _textureSourceGraphEnabled;
    private const int MaximumTextureSourceVersions = 4096;
    private static readonly HashSet<ulong> _gpuTextureSourceAddresses = [];
    private static readonly ConditionalWeakTable<object, TextureSourceWorkOwner> _textureSourceWorkOwners = new();

    private sealed class TextureSourceWorkOwner(TextureSourceVersion[] outputs, TextureSourceVersion[] predecessors)
    {
        public TextureSourceVersion[] Outputs { get; } = outputs;
        public TextureSourceVersion[] Predecessors { get; } = predecessors;
        public bool Released;
        public bool PredecessorsRetained;
        // Diagnostic receipt only: exact work/pin batch, never resource policy.
        public long NativeUseBatchIdentity;
        public bool ReleasedWithUncommittedNativeUse;
    }

    private static GuestDrawTexture DescribeColorTextureSource(GuestRenderTarget target)
    {
        if (target.Registers is not { } words)
            throw new NotSupportedException($"Metal ordered color producer 0x{target.Address:X} requires its real register view.");
        if (words.BaseAddress != target.Address || words.Width + 1 != target.Width || words.Height + 1 != target.Height ||
            (uint)words.Layout != target.Format || (uint)words.NumberType != target.NumberType ||
            (uint)words.TileMode != target.TileMode || (uint)words.Order != target.ComponentSwap ||
            words.Dimension != 1 || words.Depth != 0 || words.SliceStart != 0 || words.SliceMax != 0 ||
            words.MipLevel != 0 || words.MaxMip + 1 != target.MipLevels || words.SamplesLog2 != 0 ||
            words.FragmentsLog2 != 0 || words.Order != ChannelOrder.Standard || words.FastClear || words.DccEnabled ||
            words.CmaskAddress != 0 || words.FmaskAddress != 0 || words.DccAddress != 0)
            throw new NotSupportedException($"Metal ordered color producer has unsupported real view at 0x{target.Address:X}: {words}.");
        var resolved = ImageRequestBuilders.ColorTarget(words, target.WriteMask, 0, false) ??
            throw new NotSupportedException("Metal ordered producer has no active color view.");
        var format = MetalGuestFormats.DecodeTextureFormat(target.Format, target.NumberType);
        if (!MetalGuestFormats.TryDecodeRenderTargetFormat(target.Format, target.NumberType, target.ComponentSwap, out var color) ||
            color.Format != format.Format)
            throw new NotSupportedException("Metal color producer and texture descriptor formats require an unsupported reinterpretation.");
        return new GuestDrawTexture(target.Address, resolved.Extent.Width, resolved.Extent.Height,
            target.Format, target.NumberType, [], false, false, Type: 9,
            Pitch: resolved.Request.Description.Pitch, TileMode: target.TileMode,
            MipLevels: 1, ResourceMipLevels: target.MipLevels,
            SourceByteCount: resolved.BackingSize, DstSelect: 0xFAC,
            Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
    }

    private static TextureSourceWorkOwner PrepareTextureSourceWorkLocked(object work, long sequence, GuestQueueIdentity queue)
    {
        var outputs = new Dictionary<ulong, TextureSourceVersion>();
        var predecessors = new HashSet<TextureSourceVersion>(ReferenceEqualityComparer.Instance);
        void AddOutput(GuestDrawTexture descriptor, bool clearsWholeImage, bool verifiedColorProducer = false)
        {
            if (descriptor.Address == 0) return;
            var source = PrepareTextureSourceVersionLocked(descriptor, sequence, queue, clearsWholeImage, verifiedColorProducer);
            if (outputs.TryGetValue(descriptor.Address, out var duplicate))
            {
                var first = duplicate.Descriptor!;
                if (duplicate.View != source.View || first.Shape != descriptor.Shape || first.Access != descriptor.Access ||
                    duplicate.Initializer != source.Initializer || duplicate.ClearsOutput != source.ClearsOutput ||
                    !ReferenceEquals(first.RgbaPixels, descriptor.RgbaPixels) ||
                    !ReferenceEquals(first.TiledSource, descriptor.TiledSource) ||
                    !ReferenceEquals(first.MipUploads, descriptor.MipUploads))
                    throw new NotSupportedException("Metal one-work writable aliases require the same exact view, intent and initialization source.");
                return;
            }
            if (_latestTextureSources.TryGetValue(descriptor.Address, out var previous))
                predecessors.Add(previous); // WAW order is separate from selected initialization.
            if (source.Initializer is { } initializer) predecessors.Add(initializer);
            outputs.Add(descriptor.Address, source);
        }
        var textures = work switch
        {
            ComputeGuestDispatch dispatch => dispatch.Textures,
            OffscreenGuestDraw draw => draw.Draw.Textures,
            _ => [],
        };
        foreach (var texture in textures)
            if (StorageImageWrites(texture)) AddOutput(texture, false);
        if (work is OffscreenGuestDraw colorDraw)
        {
            foreach (var target in colorDraw.Targets)
            {
                if (target.Address == 0 || target.WriteMask == 0) continue;
                var descriptor = DescribeColorTextureSource(target);
                // First use clears a fresh attachment. An initialized old image
                // without an ordered source cannot be relabeled as a clear.
                if (_pendingGuestImageInitialData.ContainsKey(target.Address))
                    throw new NotSupportedException("Metal ordered color producer cannot discard pending CPU initialization.");
                var canClear = !_latestTextureSources.ContainsKey(target.Address) &&
                    (!_guestImages.TryGetValue(target.Address, out var live) || !live.Initialized);
                if (!canClear && !_latestTextureSources.ContainsKey(target.Address))
                    throw new NotSupportedException("Metal ordered color producer cannot discard initialized contents without a retained GPU predecessor.");
                AddOutput(descriptor, canClear, verifiedColorProducer: true);
            }
        }
        var preparedOutputs = outputs.Values.ToArray();
        var preparedPredecessors = predecessors.ToArray();
        if (preparedOutputs.Count(output => !_gpuTextureSourceAddresses.Contains(output.View.Address)) >
            MaximumTextureSourceVersions - _gpuTextureSourceAddresses.Count)
            throw new InvalidOperationException("Metal GPU source address provenance budget exceeded.");
        if (preparedOutputs.Length > MaximumTextureSourceVersions - _textureSourceVersions.Count)
            throw new InvalidOperationException("Metal ordered texture source metadata budget exceeded.");
        ulong bytes = 0;
        foreach (var output in preparedOutputs) bytes = checked(bytes + output.ByteCount * 2);
        // Both each writer's new native target and immutable copy count. This
        // includes latest producers with no read leases, not only pinned reads.
        if (bytes > MaximumReservedTextureSourceBytes - Math.Min(_immutableTextureSourceBytes, MaximumReservedTextureSourceBytes))
            throw new InvalidOperationException("Metal ordered native texture version budget exceeded.");
        return new(preparedOutputs, preparedPredecessors);
    }

    private static void RetainTextureSourcePredecessorsLocked(TextureSourceWorkOwner owner)
    {
        ulong bytes = 0;
        foreach (var source in owner.Predecessors)
        {
            if (source.Failed || source.References == int.MaxValue)
                throw new InvalidOperationException("Metal initializer/WAW owner cannot be retained.");
            if (source.References == 0) bytes = checked(bytes + source.ByteCount);
        }
        if (bytes > MaximumReservedTextureSourceBytes - Math.Min(_reservedTextureSourceBytes, MaximumReservedTextureSourceBytes))
            throw new InvalidOperationException("Metal retained initializer/WAW version budget exceeded.");
        foreach (var source in owner.Predecessors)
        {
            if (source.References++ == 0) _reservedTextureSourceBytes += source.ByteCount;
        }
        owner.PredecessorsRetained = true;
    }

    private static void ReleaseTextureSourcePredecessorsLocked(TextureSourceWorkOwner owner)
    {
        if (!owner.PredecessorsRetained) return;
        owner.PredecessorsRetained = false;
        foreach (var source in owner.Predecessors)
            if (--source.References == 0) _reservedTextureSourceBytes -= source.ByteCount;
    }

    private static void ReleaseTextureSourceWorkOwnerLocked(object work)
    {
        if (!_textureSourceWorkOwners.TryGetValue(work, out var owner) || owner.Released) return;
        foreach (var batch in _textureSourceNativeBatches.Values)
            if (batch.Identity == owner.NativeUseBatchIdentity && !batch.Committed)
            {
                owner.ReleasedWithUncommittedNativeUse = owner.NativeUseBatchIdentity != 0;
                break;
            }
        owner.Released = true;
        ReleaseTextureSourcePredecessorsLocked(owner);
        ReleaseQueuedTextureSourcesLocked(work);
        foreach (var source in owner.Outputs) source.ProducerReleased = true;
    }

    private static long EnqueueTextureSourceWorkLocked(object work, ulong payloadBytes, GuestQueueIdentity queue)
    {
        var sequence = checked(_enqueuedGuestWorkSequence + 1);
        var prefixProducer = GuestPrefixProducerForAdmission(sequence, queue);
        // Every throwing descriptor/provenance/layout/budget/allocation step is
        // before linked-list admission and producer publication.
        var owner = PrepareTextureSourceWorkLocked(work, sequence, queue);
        var payloadOwner = PrepareGuestPayloadOwnerLocked(work);
        if (_textureSourceWorkOwners.TryGetValue(work, out _))
            throw new InvalidOperationException("Metal work already has an ordered source owner.");
        var pending = new PendingGuestWork(work, payloadBytes, sequence, queue)
        {
            RequiresSuccessfulExecution = _guestPrefixCapture is not null || _guestPrefixExecution,
            PrefixProducerSequence = prefixProducer,
        };
        var node = new LinkedListNode<PendingGuestWork>(pending);
        var newQueue = !_pendingGuestWorkByQueue.TryGetValue(queue.Name, out var pendingQueue);
        pendingQueue ??= new();
        _pendingGuestWorkByQueue.EnsureCapacity(_pendingGuestWorkByQueue.Count + 1);
        _lastEnqueuedGuestWorkByQueue.EnsureCapacity(_lastEnqueuedGuestWorkByQueue.Count + 1);
        _pendingGuestQueueSchedule.EnsureCapacity(_pendingGuestQueueSchedule.Count + 1);
        _latestTextureSources.EnsureCapacity(_latestTextureSources.Count + owner.Outputs.Length);
        _textureSourceVersions.EnsureCapacity(_textureSourceVersions.Count + owner.Outputs.Length);
        var previousVersions = owner.Outputs.Select(output =>
            _latestTextureSources.TryGetValue(output.View.Address, out var previous) ? previous : null).ToArray();
        _gpuTextureSourceAddresses.EnsureCapacity(_gpuTextureSourceAddresses.Count + owner.Outputs.Length);
        var hadAddressProvenance = owner.Outputs.Select(output => _gpuTextureSourceAddresses.Contains(output.View.Address)).ToArray();
        var oldRetired = previousVersions.Select(static previous => previous?.Retired ?? false).ToArray();
        var oldSequence = _enqueuedGuestWorkSequence;
        var oldBytes = _pendingGuestWorkBytes;
        var oldCount = _pendingGuestWorkCount;
        var oldVersionsCount = _textureSourceVersions.Count;
        var oldNativeBytes = _immutableTextureSourceBytes;
        var oldTail = _immediateFollowupTail;
        var hadLast = _lastEnqueuedGuestWorkByQueue.TryGetValue(queue.Name, out var oldLast);
        _textureSourceWorkOwners.Add(work, owner);
        try
        {
            RegisterGuestPayloadOwnerLocked(payloadOwner);
            RetainQueuedTextureSourcesLocked(work);
            RetainTextureSourcePredecessorsLocked(owner);
            if (newQueue)
            {
                _pendingGuestWorkByQueue.Add(queue.Name, pendingQueue);
                _pendingGuestQueueSchedule.Add(queue.Name);
            }
            if (_enqueueAsImmediateQueueFollowup && oldTail is { List: not null } tail && ReferenceEquals(tail.List, pendingQueue))
            {
                pendingQueue.AddAfter(tail, node);
                _immediateFollowupTail = node;
            }
            else if (_enqueueAsImmediateQueueFollowup)
            {
                pendingQueue.AddFirst(node);
                _immediateFollowupTail = node;
            }
            else pendingQueue.AddLast(node);
            _enqueuedGuestWorkSequence = sequence;
            _lastEnqueuedGuestWorkByQueue[queue.Name] = sequence;
            _pendingGuestWorkCount++;
            _pendingGuestWorkBytes = checked(_pendingGuestWorkBytes + payloadBytes);
            for (var index = 0; index < owner.Outputs.Length; index++)
            {
                var source = owner.Outputs[index];
                if (previousVersions[index] is { } previous) previous.Retired = true;
                source.Budgeted = true;
                _immutableTextureSourceBytes = checked(_immutableTextureSourceBytes + source.ByteCount * 2);
                _latestTextureSources[source.View.Address] = source;
                _gpuTextureSourceAddresses.Add(source.View.Address);
                _textureSourceVersions.Add(source);
            }
            CommitGuestPayloadOwnerLocked(payloadOwner);
        }
        catch
        {
            RollbackGuestPayloadOwnerLocked(payloadOwner);
            if (node.List is not null) pendingQueue.Remove(node);
            if (newQueue)
            {
                _pendingGuestWorkByQueue.Remove(queue.Name);
                _pendingGuestQueueSchedule.Remove(queue.Name);
            }
            _immediateFollowupTail = oldTail;
            _enqueuedGuestWorkSequence = oldSequence;
            _pendingGuestWorkBytes = oldBytes;
            _pendingGuestWorkCount = oldCount;
            if (hadLast) _lastEnqueuedGuestWorkByQueue[queue.Name] = oldLast;
            else _lastEnqueuedGuestWorkByQueue.Remove(queue.Name);
            _immutableTextureSourceBytes = oldNativeBytes;
            for (var index = 0; index < owner.Outputs.Length; index++)
            {
                var source = owner.Outputs[index];
                source.Budgeted = false;
                if (!hadAddressProvenance[index]) _gpuTextureSourceAddresses.Remove(source.View.Address);
                if (previousVersions[index] is { } previous)
                {
                    previous.Retired = oldRetired[index];
                    _latestTextureSources[source.View.Address] = previous;
                }
                else _latestTextureSources.Remove(source.View.Address);
            }
            if (_textureSourceVersions.Count > oldVersionsCount)
                _textureSourceVersions.RemoveRange(oldVersionsCount, _textureSourceVersions.Count - oldVersionsCount);
            ReleaseTextureSourcePredecessorsLocked(owner);
            ReleaseQueuedTextureSourcesLocked(work);
            _queuedTextureSourceOwners.Remove(work);
            _textureSourceWorkOwners.Remove(work);
            throw;
        }
        if (!_enqueueAsImmediateQueueFollowup) ScheduleGuestWorkDrain();
        TraceGuestWork("admitted", new PendingGuestWork(work, payloadBytes, sequence, queue));
        return sequence;
    }
}
