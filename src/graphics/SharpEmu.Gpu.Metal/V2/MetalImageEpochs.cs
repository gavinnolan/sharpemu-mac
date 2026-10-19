// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Counts of the image-cache mutations that a memo keyed on the cache's state has to see:
/// a memo stores the counts it was built at and is stale once one has moved. Nothing reads
/// them yet; the packet block (ownership), the draw-target memo (set and shape) and the
/// stage-binding memo (binding) will.
///
/// Every count is bumped with Interlocked after the mutation it counts, so a reader that
/// reads the count (Volatile) before it reads the state built from it never pairs an old
/// count with state newer than it, and a mutation racing the read moves the count after.
/// Only real changes count: a call that leaves the state as it was bumps nothing, so a
/// steady-state draw that touches the same images the same way leaves every count still.
///
/// <list type="bullet">
/// <item><b>Shape</b>: an image's texture, its views or its DCC metadata changed. The
/// texture is replaced with its views (<see cref="MetalCachedImage.PromoteToOwned"/> from
/// PromoteLinearView, <see cref="MetalCachedImage.ReplaceLevels"/> from
/// ReshapeLevelsLocked and FitResidentLevelsLocked,
/// <see cref="MetalCachedImage.ReplaceWithCopy"/> from a usage promotion or a sparse grow,
/// <see cref="MetalCachedImage.ReplaceTexture"/>), or a lookup changed the image's
/// Description.Metadata (AdoptDccMetadataLocked, AcquireColorTargetView,
/// AcquireDepthTargetView).</item>
/// <item><b>GpuModified</b>: an image went from clean to GPU-modified
/// (<see cref="MetalCachedImage.MarkGpuModified"/>, whoever calls it): from then on its
/// bytes in guest memory are owed by the texture. With
/// <see cref="MetalImageCache.ImageSetVersion"/> it is the image ownership of guest
/// memory.</item>
/// <item><b>Dcc</b>: the DCC clear bookkeeping changed. A claim was taken or removed
/// (TryClaimMaterializedDccLocked; ReArmMaterializedDccLocked and
/// ForgetImageBookkeepingLocked), a deferral was taken or removed, or a recorded fill was
/// recorded, given its own store or forgotten. A deferral that settles because recorded
/// work completed changes none of these: a lookup applies it then.</item>
/// <item><b>Binding</b>: anything that can change which texture or view a binding of a
/// cached image resolves to, or what acquiring it would do first. Every Shape,
/// GpuModified and Dcc bump, and: an image inserted (InsertImage) or released, dropped or
/// evicted (ReleaseImage); an image going CPU-dirty or its dirty range widening
/// (<see cref="MetalCachedImage.MarkCpuDirty(MetalImageDirtyCause, ulong, ulong)"/>); an
/// image going clean from CPU-dirty (<see cref="MetalCachedImage.RefreshComplete()"/>) or
/// from GPU-modified (<see cref="MetalCachedImage.ClearGpuModified"/>: a writeback, an
/// unmap, a CPU store over it); the range an alias publication put in guest memory
/// changing (<see cref="MetalCachedImage.MarkPublished"/>); and a drain of deferred
/// refreshes that had any (DrainDeferredRefreshesLocked).</item>
/// </list>
/// </summary>
internal sealed class MetalImageEpochs
{
    private long _shape;
    private long _gpuModified;
    private long _dcc;
    private long _binding;

    public long Shape => Volatile.Read(ref _shape);

    public long GpuModified => Volatile.Read(ref _gpuModified);

    public long Dcc => Volatile.Read(ref _dcc);

    public long Binding => Volatile.Read(ref _binding);

    public void NoteShapeChanged()
    {
        Interlocked.Increment(ref _shape);
        Interlocked.Increment(ref _binding);
    }

    public void NoteGpuModified()
    {
        Interlocked.Increment(ref _gpuModified);
        Interlocked.Increment(ref _binding);
    }

    public void NoteDccChanged()
    {
        Interlocked.Increment(ref _dcc);
        Interlocked.Increment(ref _binding);
    }

    public void NoteBindingChanged() => Interlocked.Increment(ref _binding);

    /// <summary>Whether two metadata descriptions are the same field for field.</summary>
    public static bool SameMetadata(in MetadataDescription left, in MetadataDescription right) =>
        left.Range == right.Range &&
        left.Kind == right.Kind &&
        left.Control == right.Control &&
        left.DccClearWord == right.DccClearWord &&
        left.Compression == right.Compression &&
        left.StencilCompressed == right.StencilCompressed &&
        left.DccClearRegisterValid == right.DccClearRegisterValid &&
        left.DccAlphaMsb == right.DccAlphaMsb &&
        left.NativeColorClear == right.NativeColorClear &&
        left.ColorAlphaOnLeastSignificantBits == right.ColorAlphaOnLeastSignificantBits &&
        left.ColorMetadataBaseLayer == right.ColorMetadataBaseLayer &&
        left.PackedColorClearSupported == right.PackedColorClearSupported &&
        left.PackedColorClear.Uint32_0 == right.PackedColorClear.Uint32_0 &&
        left.PackedColorClear.Uint32_1 == right.PackedColorClear.Uint32_1 &&
        left.PackedColorClear.Uint32_2 == right.PackedColorClear.Uint32_2 &&
        left.PackedColorClear.Uint32_3 == right.PackedColorClear.Uint32_3;
}
