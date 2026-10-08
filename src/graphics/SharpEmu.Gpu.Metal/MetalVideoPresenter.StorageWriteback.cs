// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Deliberately dormant: provider publication/writer participation is a separate
    // prerequisite. Neither an environment flag nor address lookup enables this.
    private static bool _finiteStorageWritebackEnabled = false;
    private const int MaximumFiniteStorageWritebacks = 32;
    private const int FiniteStorageStagingRowAlignment = 256;

    private sealed class FiniteStorageDebt
    {
        internal bool Pending;
        internal bool Failed;
        internal long Work;
        // This is the exact native version whose bytes were actually published.
        // GpuWritten remains historical metadata and is never cleared to pretend
        // that another unrecorded writer has settled its canonical debt.
        internal int CanonicalVersion = -1;
        internal StorageCpuSeedOrigin? CanonicalOrigin;
    }
    private static readonly ConditionalWeakTable<GuestImage, FiniteStorageDebt> _finiteStorageDebts = new();

    private sealed class FiniteStorageWriteback : IDisposable
    {
        internal readonly GuestDrawTexture Descriptor;
        internal readonly GuestImage Image;
        internal readonly StorageCpuSeedOrigin Origin;
        internal readonly FiniteStorageDebt Debt;
        internal readonly int Version, Bytes, RowBytes, StagingBytes;
        internal readonly long Work;
        internal StorageCpuSeedOrigin? PublishedOrigin;
        internal nint Texture, Staging, CommandBuffer;
        internal bool Encoded, SuccessfullyCompleted, Published, Consumed;

        internal FiniteStorageWriteback(GuestDrawTexture descriptor, GuestImage image,
            StorageCpuSeedOrigin origin, FiniteStorageDebt debt, int bytes)
        {
            Descriptor = descriptor; Image = image; Origin = origin; Debt = debt;
            Version = image.ContentVersion; Bytes = bytes;
            RowBytes = checked(((int)descriptor.Width * (int)origin.Layout.NativeFormat.BytesPerPixel +
                FiniteStorageStagingRowAlignment - 1) & -FiniteStorageStagingRowAlignment);
            StagingBytes = checked(RowBytes * (int)descriptor.Height);
            Work = Volatile.Read(ref _executingGuestWorkSequence);
        }

        public void Dispose()
        {
            if (Consumed) return;
            Consumed = true; // consume exact retains before a throwing release
            if (!Published) { Debt.Failed = true; Debt.Pending = true; }
            var commandBuffer = CommandBuffer; var staging = Staging; var texture = Texture;
            CommandBuffer = Staging = Texture = 0;
            Exception? first = null;
            ReleaseFiniteStorageHandle(commandBuffer, ref first);
            ReleaseFiniteStorageHandle(staging, ref first);
            ReleaseFiniteStorageHandle(texture, ref first);
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    private static void ReleaseFiniteStorageHandle(nint handle, ref Exception? first)
    {
        if (handle == 0) return;
        try { MetalNative.SendVoid(handle, MetalNative.Selector("release")); }
        catch (Exception failure) { first ??= failure; }
    }

    private static int FiniteStorageTexelBytes(GuestDrawTexture descriptor)
    {
        if ((!MetalSingletonArrayStorageAdmission.IsQualified(descriptor) &&
            !MetalBgra1024ArrayStorageAdmission.IsQualified(descriptor)) ||
            !StorageImageWrites(descriptor))
            throw new NotSupportedException("Metal finite storage publication requires an exact qualified writable singleton or RGBA8 1024-square array and its full physical footprint.");
        var format = StorageTextureFormat(descriptor);
        if (format.BlockBytes != 0 || format.BytesPerPixel is not (1 or 4 or 8))
            throw new NotSupportedException("Metal finite storage publication has an unqualified native byte layout.");
        return checked((int)((ulong)descriptor.Width * descriptor.Height * format.BytesPerPixel));
    }

    private static void ValidateFiniteStorageDispatch(GuestDrawTexture[] textures)
    {
        if (textures.Length > MaximumFiniteStorageWritebacks)
            throw new NotSupportedException("Metal finite storage dispatch exceeds its bounded texture inventory.");
        if (_guestMemory is not ICpuMappedPublicationSource)
            throw new NotSupportedException("Metal finite storage canonical provider transaction is not connected.");
        var addresses = new HashSet<ulong>();
        foreach (var texture in textures)
        {
            if (!texture.IsStorage || !StorageImageWrites(texture)) continue;
            _ = FiniteStorageTexelBytes(texture);
            if (!addresses.Add(texture.Address))
                throw new NotSupportedException("Metal finite storage publication rejects duplicate writable image bindings.");
            if (addresses.Count > 1)
                throw new NotSupportedException("Metal finite storage publication currently supports one writable image per work; a multi-image canonical transaction is not implemented.");
        }
    }

    private static void ValidateFiniteStorageBufferCohort(GuestDrawTexture[] textures, GuestMemoryBuffer[] buffers)
    {
        if (!textures.Any(texture => texture.IsStorage && StorageImageWrites(texture))) return;
        if (buffers.Any(buffer => buffer.Writable && buffer.WriteBackToGuest && buffer.Length != 0))
            throw new NotSupportedException("Metal finite storage publication cannot mix image and nonempty writable guest-buffer publication in one work.");
    }

    // Exact GPU bytes only. Singleton padding is never copied; the qualified
    // RGBA8 1024-square bijection covers its entire 4MiB physical footprint.
    private static byte[] PrepareFiniteStorageOriginRun(GuestDrawTexture descriptor, byte[] nativeTexel)
    {
        var bytes = FiniteStorageTexelBytes(descriptor);
        if (nativeTexel.Length != bytes || descriptor.Address > ulong.MaxValue - (ulong)bytes)
            throw new InvalidOperationException("Metal finite storage staging does not contain the exact native texel.");
        if (MetalBgra1024ArrayStorageAdmission.IsQualified(descriptor))
            return MetalStorageCpuLayout.TryCreate(descriptor)?.Encode(nativeTexel) ??
                throw new InvalidOperationException("RGBA8 publication lost its original qualified layout.");
        return (byte[])nativeTexel.Clone();
    }

    private static FiniteStorageWriteback CaptureFiniteStorageWriteback(GuestDrawTexture descriptor, GuestImage image)
    {
        var bytes = FiniteStorageTexelBytes(descriptor);
        var origin = image.StorageCpuOrigin ??
            throw new NotSupportedException("Metal finite storage writeback has no original mapped seed and checked canonical publisher.");
        if (origin.Memory is not ICpuMappedPublicationSource)
            throw new NotSupportedException("Metal finite storage canonical provider transaction is not connected.");
        if (!ReferenceEquals(image.StorageCpuLayout, origin.Layout) || !origin.Layout.Matches(descriptor) ||
            origin.Layout.LinearBytes != bytes)
            throw new InvalidOperationException("Metal finite storage publication does not match its immutable original layout.");
        ValidateFiniteStorageOrigin(origin, descriptor);
        // Metadata is private/owned. Preserve raw descriptor identity against a
        // later caller mutation; payload is unnecessary once native input exists.
        var owned = descriptor with { Descriptor = descriptor.Descriptor!.ToArray(), RgbaPixels = [] };
        lock (_gate)
        {
            if (_closed || _guestWorkFailure is not null)
                throw new InvalidOperationException("Metal finite storage publication cannot acquire a closed or failed presenter owner.");
            ValidateStorageImageView(image, descriptor, StorageTextureFormat(descriptor));
            if (image.TextureType != MtlTextureType2DArray || image.ArrayLength != 1 || image.Depth != 1 || image.Texture == 0 ||
                !_guestImages.TryGetValue(descriptor.Address, out var current) || !ReferenceEquals(current, image))
                throw new InvalidOperationException("Metal finite storage publication lost its exact native image owner.");
            var debt = _finiteStorageDebts.GetValue(image, static _ => new FiniteStorageDebt());
            if (debt.Pending || debt.Failed)
                throw new InvalidOperationException("Metal finite storage image has unresolved GPU publication debt.");
            ValidateFiniteStoragePreviousPublication(image, debt, origin);
            var receipt = new FiniteStorageWriteback(owned, image, origin, debt, bytes);
            debt.Pending = true; debt.Work = receipt.Work;
            try
            {
                receipt.Texture = MetalNative.Send(image.Texture, MetalNative.Selector("retain"));
                if (receipt.Texture == 0) throw new InvalidOperationException("Metal finite storage cannot retain its original producer texture.");
                return receipt;
            }
            catch { debt.Failed = true; throw; }
        }
    }

    private static void ValidateFiniteStoragePreviousPublication(GuestImage image, FiniteStorageDebt debt,
        StorageCpuSeedOrigin origin)
    {
        if (image.GpuWritten && (debt.CanonicalVersion != image.ContentVersion ||
            !ReferenceEquals(debt.CanonicalOrigin, origin)))
            throw new InvalidOperationException("Metal finite storage image has no exact successful canonical publication for its current GPU version.");
    }

    private static void ValidateFiniteStorageOrigin(StorageCpuSeedOrigin origin, GuestDrawTexture descriptor)
    {
        if (_closed || _guestWorkFailure is not null || !ReferenceEquals(_guestMemory, origin.Memory) ||
            origin.Session != Volatile.Read(ref _selectedStorageMipSession) ||
            origin.Mapping.FootprintAddress != descriptor.Address || origin.Mapping.FootprintByteCount != origin.Layout.PhysicalBytes ||
            origin.Mapping.SourceAddress != descriptor.Address || origin.Mapping.SourceByteCount != origin.Layout.PhysicalBytes ||
            !origin.Source.IsMappedCopyObservationCurrent(origin.Mapping) || !origin.Observation.Observed ||
            !IsCpuTextureSnapshotObservationStable(descriptor.Address, origin.Observation))
            throw new InvalidOperationException("Metal finite storage publication original mapping/content/session observation changed.");
    }

    private static void ValidateFiniteStorageReceipt(FiniteStorageWriteback receipt)
    {
        lock (_gate)
        {
            if (_closed || _guestWorkFailure is not null || receipt.Consumed || receipt.Published || receipt.Image.Texture == 0 ||
                receipt.Image.Texture != receipt.Texture || receipt.Image.ContentVersion != receipt.Version ||
                !ReferenceEquals(receipt.Image.StorageCpuOrigin, receipt.Origin) ||
                !_guestImages.TryGetValue(receipt.Descriptor.Address, out var current) || !ReferenceEquals(current, receipt.Image) ||
                !receipt.Debt.Pending || receipt.Debt.Failed || receipt.Debt.Work != receipt.Work)
                throw new InvalidOperationException("Metal finite storage publication exact owner/version receipt changed.");
        }
        ValidateFiniteStorageOrigin(receipt.Origin, receipt.Descriptor);
    }

    private static void EncodeFiniteStorageReadback(nint device, nint commandBuffer, FiniteStorageWriteback receipt)
    {
        if (receipt.Encoded || receipt.Consumed || commandBuffer == 0 || device == 0)
            throw new InvalidOperationException("Metal finite storage readback requires one real producer command buffer.");
        if (MetalNative.Send(commandBuffer, MetalNative.Selector("retainedReferences")) == 0)
            throw new NotSupportedException("Metal finite storage readback requires command-buffer resource retention.");
        // Every owned handle is installed before subsequent allocation/encoding
        // can throw, so receipt disposal always sees the exact retain.
        receipt.CommandBuffer = MetalNative.Send(commandBuffer, MetalNative.Selector("retain"));
        receipt.Staging = MetalNative.SendNewBuffer(device, MetalNative.Selector("newBufferWithLength:options:"),
            (nuint)receipt.StagingBytes, 0);
        if (receipt.Texture == 0 || receipt.CommandBuffer == 0 || receipt.Staging == 0)
            throw new InvalidOperationException("Metal finite storage readback cannot allocate/retain its exact resources.");
        ValidateFiniteStorageReceipt(receipt);
        var blit = MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));
        if (blit == 0) throw new InvalidOperationException("Metal finite storage readback cannot create a blit encoder.");
        Exception? failure = null;
        try
        {
            MetalNative.SendCopyTextureToBuffer(blit,
                MetalNative.Selector("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                receipt.Texture, 0, 0, default, new MtlSize { Width = receipt.Descriptor.Width, Height = receipt.Descriptor.Height, Depth = 1 },
                receipt.Staging, 0, (nuint)receipt.RowBytes, (nuint)receipt.StagingBytes);
        }
        catch (Exception error) { failure = error; }
        try { MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding")); }
        catch (Exception cleanup) when (failure is not null)
        {
            try { failure.Data["FiniteStorageBlitEndFailure"] = cleanup; } catch { }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        receipt.Encoded = true;
    }

    private static unsafe void CompleteFiniteStorageWriteback(nint committed, FiniteStorageWriteback receipt)
    {
        if (!receipt.Encoded || committed == 0 || committed != receipt.CommandBuffer || receipt.SuccessfullyCompleted)
            throw new InvalidOperationException("Metal finite storage publication has no exact committed producer receipt.");
        WaitForCommittedGuestBufferWriteback(committed); // actual status4; Error is never a receipt
        receipt.SuccessfullyCompleted = true;
        ValidateFiniteStorageReceipt(receipt);
        var pointer = MetalNative.Send(receipt.Staging, MetalNative.Selector("contents"));
        if (pointer == 0) throw new InvalidOperationException("Metal finite storage readback has no completed staging bytes.");
        var nativeLinear = ReadFiniteStorageLinearBytes(receipt, pointer);
        var run = MetalBgra1024ArrayStorageAdmission.IsQualified(receipt.Descriptor)
            ? receipt.Origin.Layout.Encode(nativeLinear)
            : PrepareFiniteStorageOriginRun(receipt.Descriptor, nativeLinear);
        ValidateFiniteStorageReceipt(receipt);
        // Outside _gate. Provider exclusion comes BEFORE the final scalar veto
        // and write, not a post-copy check which cannot undo stale bytes.
        var publisher = receipt.Origin.Memory as ICpuMappedPublicationSource ??
            throw new NotSupportedException("Metal finite storage canonical provider transaction is not connected.");
        CpuMappedPublication? transaction = null;
        CpuMappedPublicationPreparationResult prepared;
        try
        {
            prepared = publisher.TryPrepareMappedPublication(receipt.Origin.Mapping,
                PrepareFiniteStorageMappingRuns(receipt.Origin.Mapping, receipt.Descriptor.Address, run), out transaction);
        }
        catch (Exception failure)
        {
            CancelFiniteStoragePublication(transaction, failure);
            throw;
        }
        if (prepared != CpuMappedPublicationPreparationResult.Prepared || transaction is null)
        {
            var refusal = new InvalidOperationException($"Metal finite storage canonical publication preparation was refused: result={prepared}; GPU debt remains pending.");
            CancelFiniteStoragePublication(transaction, refusal);
            throw refusal;
        }
        Exception? primary = null;
        try
        {
            ValidateFiniteStorageReceipt(receipt);
            var result = transaction.TryCommit();
            if (result != CpuMappedPublicationResult.Applied)
                throw new InvalidOperationException($"Metal finite storage canonical publication was not fully applied: result={result}; GPU debt remains pending.");
            // Applied effects cannot be retried, even if subsequent metadata
            // allocation or continuity validation fails. The provider creates
            // this continuation from the original identities and its own write;
            // no fresh mapping/content observation is acquired here.
            receipt.Published = true;
            var mapping = transaction.SuccessfulObservation ??
                throw new InvalidOperationException("Metal finite storage publication applied without its exact provider continuity; GPU debt remains pending.");
            ValidateFiniteStorageMappingContinuation(receipt.Origin.Mapping, mapping);
            var nextOrigin = receipt.Origin with { Mapping = mapping };
            lock (_gate)
            {
                ValidateFiniteStorageFinalizationContext(receipt.Origin);
                if (receipt.Image.Texture != receipt.Texture || receipt.Image.ContentVersion != receipt.Version ||
                    !ReferenceEquals(receipt.Image.StorageCpuOrigin, receipt.Origin) ||
                    !_guestImages.TryGetValue(receipt.Descriptor.Address, out var current) || !ReferenceEquals(current, receipt.Image) ||
                    receipt.Debt.Failed || !receipt.Debt.Pending || receipt.Debt.Work != receipt.Work)
                {
                    receipt.Debt.Failed = true;
                    throw new InvalidOperationException("Metal finite storage owner changed during canonical publication.");
                }
                receipt.PublishedOrigin = nextOrigin;
                // Remain pending until the actual native mutation metadata is
                // recorded. CompleteGuestWork/EOP cannot precede this transition.
            }
        }
        catch (Exception failure) { receipt.Debt.Failed = true; primary = failure; throw; }
        finally
        {
            CancelFiniteStoragePublication(transaction, primary);
        }
    }

    private static unsafe byte[] ReadFiniteStorageLinearBytes(FiniteStorageWriteback receipt, nint pointer)
    {
        var rowBytes = checked((int)receipt.Descriptor.Width * (int)receipt.Origin.Layout.NativeFormat.BytesPerPixel);
        var linear = new byte[receipt.Bytes];
        var staging = new ReadOnlySpan<byte>((void*)pointer, receipt.StagingBytes);
        for (var y = 0; y < receipt.Descriptor.Height; y++)
            staging.Slice(checked((int)y * receipt.RowBytes), rowBytes)
                .CopyTo(linear.AsSpan(checked((int)y * rowBytes), rowBytes));
        return linear;
    }

    // Use only ORIGINAL segment boundaries. A single provider transaction covers
    // all runs; this is not atomic visibility of the large alias memory copy.
    private static CpuMappedWriteRun[] PrepareFiniteStorageMappingRuns(
        CpuMappedCopyObservation original, ulong address, byte[] ownedPhysical)
    {
        if (ownedPhysical.Length == 0 || ownedPhysical.Length > SharpEmu.HLE.Host.HostMappedCopyLedger.MaximumPublicationBytes ||
            address > ulong.MaxValue - (ulong)ownedPhysical.Length ||
            original.SourceAddress != address || original.SourceByteCount < (ulong)ownedPhysical.Length)
            throw new InvalidOperationException("Finite storage publication has an invalid original physical extent.");
        var end = address + (ulong)ownedPhysical.Length;
        var cursor = address;
        var runs = new List<CpuMappedWriteRun>(original.Segments.Count);
        foreach (var segment in original.Segments)
        {
            if (cursor == end) break;
            var start = Math.Max(cursor, segment.CoveredAddress);
            var stop = Math.Min(end, checked(segment.CoveredAddress + segment.CoveredByteCount));
            if (stop <= start) continue;
            if (start != cursor || runs.Count == SharpEmu.HLE.Host.HostMappedCopyLedger.MaximumPublicationRuns)
                throw new InvalidOperationException("Finite storage original mapping segments do not cover every byte exactly once.");
            runs.Add(new(start, ownedPhysical.AsMemory(checked((int)(start - address)), checked((int)(stop - start)))));
            cursor = stop;
        }
        if (cursor != end) throw new InvalidOperationException("Finite storage original mapping has a publication hole.");
        return runs.ToArray();
    }

    private static void ValidateFiniteStorageMappingContinuation(CpuMappedCopyObservation original,
        CpuMappedCopyObservation successor)
    {
        if (ReferenceEquals(original, successor) || original.FootprintAddress != successor.FootprintAddress ||
            original.FootprintByteCount != successor.FootprintByteCount || original.SourceAddress != successor.SourceAddress ||
            original.SourceByteCount != successor.SourceByteCount || original.Segments.Count != successor.Segments.Count)
            throw new InvalidOperationException("Metal finite storage publication provider continuity changed its original physical mapping footprint.");
        for (var index = 0; index < original.Segments.Count; index++)
            if (original.Segments[index] != successor.Segments[index])
                throw new InvalidOperationException("Metal finite storage publication provider continuity changed an original physical mapping identity.");
    }

    private static void FinalizeFiniteStorageWrites(List<FiniteStorageWriteback>? receipts)
    {
        if (receipts is null) return;
        lock (_gate)
        {
            // Validate the complete work inventory before changing any owner.
            foreach (var receipt in receipts)
            {
                ValidateFiniteStorageFinalizationContext(receipt.Origin);
                if (!receipt.Published || receipt.Consumed || receipt.PublishedOrigin is null ||
                    !receipt.SuccessfullyCompleted || receipt.Debt.Failed || !receipt.Debt.Pending ||
                    receipt.Debt.Work != receipt.Work || receipt.Image.Texture != receipt.Texture ||
                    receipt.Image.ContentVersion != checked(receipt.Version + 1) ||
                    !ReferenceEquals(receipt.Image.StorageCpuOrigin, receipt.Origin) ||
                    !_guestImages.TryGetValue(receipt.Descriptor.Address, out var current) || !ReferenceEquals(current, receipt.Image))
                    throw new InvalidOperationException("Metal finite storage canonical publication cannot finalize its exact native mutation version.");
            }
            foreach (var receipt in receipts)
            {
                receipt.Image.StorageCpuOrigin = receipt.PublishedOrigin;
                receipt.Debt.CanonicalOrigin = receipt.PublishedOrigin;
                receipt.Debt.CanonicalVersion = receipt.Image.ContentVersion;
                receipt.Debt.Pending = false;
            }
        }
    }

    // Called under _gate at both post-effect metadata boundaries. Applied bytes
    // cannot be undone/retried; a closed/failed/replaced session must instead
    // retain debt and stop acknowledgement even before owner teardown finishes.
    private static void ValidateFiniteStorageFinalizationContext(StorageCpuSeedOrigin origin)
    {
        if (_closed || _guestWorkFailure is not null || !ReferenceEquals(_guestMemory, origin.Memory) ||
            origin.Session != Volatile.Read(ref _selectedStorageMipSession))
            throw new InvalidOperationException("Metal finite storage publication cannot finalize a closed, failed or replaced original memory/session.", _guestWorkFailure);
    }

    private static void CancelFiniteStoragePublication(CpuMappedPublication? transaction, Exception? primary)
    {
        try { transaction?.Dispose(); }
        catch (Exception cleanup) when (primary is not null)
        {
            lock (_gate) _textureSourceCleanupFailure ??= cleanup;
            try { primary.Data["FiniteStoragePublicationCleanup"] = cleanup; } catch { }
        }
    }

    private static void ReleaseFiniteStorageWritebacks(List<FiniteStorageWriteback>? receipts, Exception? primary)
    {
        if (receipts is null) return;
        Exception? cleanup = null;
        foreach (var receipt in receipts)
            try { receipt.Dispose(); } catch (Exception failure) { cleanup ??= failure; }
        if (cleanup is null) return;
        if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
        lock (_gate) _textureSourceCleanupFailure ??= cleanup;
    }
}
