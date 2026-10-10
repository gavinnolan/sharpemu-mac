// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Mapping bookkeeping supplies no native/content/publication completion
    // authority. Exact first originals remain rooted through process lifetime.
    private const int MaximumRegularColorOriginals = 128;
    private sealed class RegularColorOriginal(MetalRegularColorPhysicalPlacement.Observation observation)
    {
        internal readonly MetalRegularColorPhysicalPlacement.Observation Observation = observation;
        internal int Preparations, Queued;
        internal bool Accepted, Retired, Unknown;
        internal RegularColorNativeOwner? Native;
        internal RegularColorPreparation? UnknownPreparation;
    }
    private sealed class RegularColorPreparation(RegularColorOriginal original)
    {
        internal readonly RegularColorOriginal Original = original;
        internal bool Accepted, Released;
        internal object? QueuedWork;
        internal RegularColorPreparation? UnknownNext;
        internal bool CleanupUnknown;
    }
    private sealed class RegularColorNativeOwner(RegularColorOriginal original, GuestImage image)
    {
        internal readonly RegularColorOriginal Original = original;
        internal readonly GuestImage Image = image;
        internal bool CreationEntered, CreationReturned, Installed, Unknown;
        internal nint Texture;
    }
    private static readonly Dictionary<ulong, RegularColorOriginal> _regularColorOriginals = [];
    private static readonly ConditionalWeakTable<GuestRenderTarget, RegularColorPreparation> _regularColorTargetPreparations = new();
    private static readonly HashSet<RegularColorPreparation> _regularColorPreparations = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<object, RegularColorPreparation[]> _regularColorQueued = new(ReferenceEqualityComparer.Instance);
    private static readonly ConditionalWeakTable<GuestImage, RegularColorNativeOwner> _regularColorImages = new();
    private static Exception? _regularColorOriginalFailure;

    private static void RequireRegularColorOriginalCurrentLocked(RegularColorOriginal original)
    {
        var observation = original.Observation;
        if (_closed || _guestWorkFailure is not null || Volatile.Read(ref _regularColorOriginalFailure) is not null ||
            !Volatile.Read(ref NativeColorBorrowEnabled) || Volatile.Read(ref original.Unknown) || original.Retired || !ReferenceEquals(_guestMemory, observation.Memory) ||
            observation.Session != CurrentNativeDepthFaceSession ||
            !_regularColorOriginals.TryGetValue(observation.Placement.Address, out var exact) || !ReferenceEquals(exact, original) ||
            !observation.Source.IsMappedCopyObservationCurrent(observation.Mapping))
            throw new NotSupportedException("Regular color original mapping/domain/session/first-owner changed.");
    }

    internal static GuestRenderTarget CaptureRegularColorTarget(ICpuMemory memory, ColorTargetResolution resolution,
        ColorTargetWords words, GuestRenderTarget target)
    {
        if (!NativeColorBorrowEnabled)
        {
            lock (_gate) RefuseRegularColorOwnerFallbackLocked(target, words.BaseAddress);
            return target;
        }
        if (target.Address == 0 || words.MaxMip != 0)
        {
            lock (_gate) RefuseRegularColorOwnerFallbackLocked(target, words.BaseAddress);
            return target;
        }
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Regular color original capture requires a released presenter gate.");
        memory = CpuGpuMemoryView.Resolve(memory);
        var layout = MetalRegularColorPhysicalPlacement.Layout.Describe(resolution, words, target);
        var session = CurrentNativeDepthFaceSession;
        RegularColorOriginal? original;
        lock (_gate)
        {
            if (_closed || _guestWorkFailure is not null || Volatile.Read(ref _regularColorOriginalFailure) is not null ||
                _textureSourceGraphEnabled ||
                !ReferenceEquals(_guestMemory, memory) || session != CurrentNativeDepthFaceSession)
                throw new NotSupportedException("Regular color first capture owner is closed or changed.");
            _regularColorOriginals.TryGetValue(layout.Address, out original);
            if (original is not null)
            {
                RequireRegularColorOriginalCurrentLocked(original);
                if (!ReferenceEquals(original.Observation.Memory, memory) || !layout.Matches(original.Observation.Placement))
                    throw new NotSupportedException("Regular color cannot replace its first layout/domain with a younger capture.");
            }
            else if (_regularColorOriginals.Count >= MaximumRegularColorOriginals || _guestImages.ContainsKey(layout.Address))
                throw new NotSupportedException("Regular color original inventory is full or its native image already lacks first provenance.");
        }
        if (original is null)
        {
            bool OwnerStillCurrent()
            {
                lock (_gate) return !_closed && _guestWorkFailure is null && Volatile.Read(ref _regularColorOriginalFailure) is null &&
                    ReferenceEquals(_guestMemory, memory) && session == CurrentNativeDepthFaceSession &&
                    !_textureSourceGraphEnabled &&
                    !_regularColorOriginals.ContainsKey(layout.Address) && !_guestImages.ContainsKey(layout.Address);
            }
            var observation = MetalRegularColorPhysicalPlacement.Observation.Capture(memory, layout, session, OwnerStillCurrent);
            var first = new RegularColorOriginal(observation);
            lock (_gate)
            {
                if (!OwnerStillCurrent() || _regularColorOriginals.Count >= MaximumRegularColorOriginals ||
                    !observation.Source.IsMappedCopyObservationCurrent(observation.Mapping))
                    throw new NotSupportedException("Regular color first capture changed before its sole registration.");
                _regularColorOriginals.Add(layout.Address, first);
                original = first;
            }
        }
        lock (_gate)
        {
            RequireRegularColorOriginalCurrentLocked(original);
            if (_regularColorPreparations.Count >= MaximumRegularColorOriginals || original.Preparations >= MaximumRegularColorOriginals)
                throw new NotSupportedException("Regular color preparation ownership is full.");
            var preparation = new RegularColorPreparation(original);
            _regularColorPreparations.EnsureCapacity(_regularColorPreparations.Count + 1);
            _regularColorTargetPreparations.Add(target, preparation);
            try { _regularColorPreparations.Add(preparation); }
            catch { _regularColorTargetPreparations.Remove(target); throw; }
            original.Preparations++;
        }
        return target;
    }

    private static RegularColorPreparation[] PrepareRegularColorAdmissionLocked(object work)
    {
        if (work is not OffscreenGuestDraw draw) return [];
        if (!NativeColorBorrowEnabled)
        {
            foreach (var target in draw.Targets) RefuseRegularColorOwnerFallbackLocked(target);
            return [];
        }
        if (_textureSourceGraphEnabled || draw.Targets.Length > 8)
            throw new NotSupportedException("Regular color target inventory or alternate graph ownership is unsupported.");
        var result = new List<RegularColorPreparation>();
        foreach (var target in draw.Targets)
        {
            if (!_regularColorTargetPreparations.TryGetValue(target, out var preparation)) continue;
            if (preparation.Accepted || preparation.Released || preparation.QueuedWork is not null || !_regularColorPreparations.Contains(preparation) ||
                preparation.Original.Preparations <= 0 || !preparation.Original.Observation.Placement.MatchesTarget(target) ||
                result.Any(previous => ReferenceEquals(previous.Original, preparation.Original)))
                throw new NotSupportedException("Regular color admission lost or duplicated its exact unconsumed preparation.");
            RequireRegularColorOriginalCurrentLocked(preparation.Original);
            result.Add(preparation);
        }
        return result.ToArray();
    }

    private static bool RetainQueuedRegularColorLocked(object work, RegularColorPreparation[] preparations)
    {
        if (preparations.Length == 0) return false;
        if (_regularColorQueued.Count >= MaxPendingGuestWork + 1 || _regularColorQueued.ContainsKey(work))
            throw new NotSupportedException("Regular color queued original ownership is full or duplicated.");
        foreach (var preparation in preparations)
        {
            RequireRegularColorOriginalCurrentLocked(preparation.Original);
            if (preparation.Accepted || preparation.Released || preparation.QueuedWork is not null || !_regularColorPreparations.Contains(preparation) ||
                preparation.Original.Queued >= MaxPendingGuestWork + 1)
                throw new NotSupportedException("Regular color queue cannot acquire a consumed or overflowing original.");
        }
        var owned = preparations.ToArray();
        _regularColorQueued.Add(work, owned); // allocation precedes scalar ownership
        foreach (var preparation in preparations)
        { preparation.QueuedWork = work; preparation.Original.Queued++; }
        return true;
    }

    private static void PublishRegularColorAdmissionLocked(RegularColorPreparation[] preparations)
    {
        foreach (var preparation in preparations)
        {
            RequireRegularColorOriginalCurrentLocked(preparation.Original);
            if (preparation.Accepted || preparation.Released || preparation.QueuedWork is null || preparation.Original.Queued <= 0)
                throw new NotSupportedException("Regular color admission cannot consume an unretained preparation.");
        }
        foreach (var preparation in preparations)
        { preparation.Accepted = true; preparation.Original.Accepted = true; }
    }

    internal static void ReleasePreparedRegularColorTarget(GuestRenderTarget target, Exception? primary = null)
    {
        lock (_gate)
        {
            if (!_regularColorTargetPreparations.TryGetValue(target, out var preparation) || preparation.Released) return;
            preparation.Released = true; // exact consumption BEFORE cleanup, no retry
            if (preparation.Original.Preparations <= 0 || !_regularColorPreparations.Remove(preparation))
            {
                var failure = new InvalidOperationException("Regular color preparation ownership cleanup is unknown.");
                RetainUnknownRegularColorPreparation(preparation);
                preparation.Original.Unknown = true; Interlocked.CompareExchange(ref _regularColorOriginalFailure, failure, null);
                if (primary is not null) { try { primary.Data["RegularColorPreparationCleanup"] = failure; } catch { } }
                else throw failure;
                return;
            }
            preparation.Original.Preparations--;
            // Keep the FIRST mapping in the bounded ledger even for abandoned
            // preparation; a younger capture cannot erase its prior history.
        }
    }

    private static void ReleaseQueuedRegularColorLocked(object work, Exception? primary = null)
    {
        if (!_regularColorQueued.Remove(work, out var preparations)) return;
        Exception? first = primary;
        foreach (var preparation in preparations)
        {
            if (!ReferenceEquals(preparation.QueuedWork, work) || preparation.Original.Queued <= 0)
            {
                var failure = new InvalidOperationException("Regular color queued original cleanup is unknown.");
                RetainUnknownRegularColorPreparation(preparation);
                preparation.Original.Unknown = true; Interlocked.CompareExchange(ref _regularColorOriginalFailure, failure, null); first ??= failure;
                continue;
            }
            preparation.QueuedWork = null;
            preparation.Original.Queued--;
        }
        if (primary is null && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }

    private static void RetainUnknownRegularColorPreparation(RegularColorPreparation preparation)
    {
        if (preparation.CleanupUnknown) return;
        preparation.CleanupUnknown = true;
        preparation.UnknownNext = preparation.Original.UnknownPreparation;
        preparation.Original.UnknownPreparation = preparation; // no allocation or replacement of an older unknown
    }

    private static void CancelQueuedRegularColorLocked(Exception? primary = null)
    {
        Exception? first = primary;
        while (_regularColorQueued.Count != 0)
        {
            using var iterator = _regularColorQueued.Keys.GetEnumerator();
            if (!iterator.MoveNext()) break;
            try { ReleaseQueuedRegularColorLocked(iterator.Current, first); }
            catch (Exception cleanup) { first ??= cleanup; }
        }
        if (primary is null && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }

    private static RegularColorOriginal? RequireRegularColorTargetLocked(GuestRenderTarget target)
    {
        if (!NativeColorBorrowEnabled)
        {
            RefuseRegularColorOwnerFallbackLocked(target);
            return null;
        }
        if (target.Address == 0 || target.Registers is not { MaxMip: 0 })
        {
            RefuseRegularColorOwnerFallbackLocked(target);
            return null;
        }
        if (!_regularColorTargetPreparations.TryGetValue(target, out var preparation) || !preparation.Accepted ||
            !preparation.Original.Accepted || preparation.Original.Queued <= 0 || preparation.QueuedWork is not { } work ||
            !_regularColorQueued.TryGetValue(work, out var queued) || !queued.Contains(preparation) ||
            !preparation.Original.Observation.Placement.MatchesTarget(target))
            throw new NotSupportedException("Regular color target lacks its exact accepted and queued first preparation.");
        RequireRegularColorOriginalCurrentLocked(preparation.Original);
        return preparation.Original;
    }

    private static void RefuseRegularColorOwnerFallbackLocked(GuestRenderTarget target, ulong suppliedAllocationBase = 0)
    {
        if (_regularColorOriginals.ContainsKey(target.Address) || _regularColorOriginals.ContainsKey(suppliedAllocationBase) ||
            target.Registers is { } words && _regularColorOriginals.ContainsKey(words.BaseAddress) ||
            _regularColorTargetPreparations.TryGetValue(target, out _))
            throw new NotSupportedException("Regular color feature/classification loss cannot discard a retained first owner or use the generic native path.");
    }

    private static void RequireRegularColorNativeImageLocked(RegularColorOriginal original, GuestImage image, MtlPixelFormat format)
    {
        RequireRegularColorOriginalCurrentLocked(original);
        var layout = original.Observation.Placement;
        if (!_regularColorImages.TryGetValue(image, out var native) || !ReferenceEquals(native, original.Native) ||
            !ReferenceEquals(native.Original, original) || !ReferenceEquals(native.Image, image) || Volatile.Read(ref native.Unknown) ||
            !native.CreationEntered || !native.CreationReturned || !native.Installed || native.Texture == 0 ||
            image.Texture != native.Texture || image.TextureType != MtlTextureType2D || image.Depth != 1 || image.ArrayLength != 1 ||
            image.Width != layout.Width || image.Height != layout.Height || image.Format != format || format != layout.NativeFormat)
            throw new NotSupportedException("Regular color native image lost its exact first mapping/handle/layout association.");
    }

    private static GuestImage? CreateAndInstallRegularColorImage(nint device, GuestRenderTarget target,
        MtlPixelFormat format, RegularColorOriginal original)
    {
        var layout = original.Observation.Placement;
        var image = new GuestImage { Width = layout.Width, Height = layout.Height, Format = format,
            DiagnosticOrigin = CaptureColorImageOrigin(target) };
        var native = new RegularColorNativeOwner(original, image);
        lock (_gate)
        {
            if (!ReferenceEquals(RequireRegularColorTargetLocked(target), original) || original.Native is not null ||
                _guestImages.ContainsKey(layout.Address) || format != layout.NativeFormat)
                throw new NotSupportedException("Regular color cannot adopt or replace a native image after its first capture.");
            RequireNoRegularColorPendingSeedLocked(original);
            // All bookkeeping allocations precede the sole native call. The
            // exact image/owner is rooted even if that call never returns.
            _guestImages.EnsureCapacity(_guestImages.Count + 1);
            _guestImageExtents.EnsureCapacity(_guestImageExtents.Count + 1);
            _regularColorImages.Add(image, native);
            original.Native = native;
            native.CreationEntered = true;
        }
        try
        {
            var texture = CreateGuestTexture(device, format, layout.Width, layout.Height);
            RecordRegularColorNativeCreationReturn(native, texture);
            lock (_gate)
            {
                if (texture == 0) throw new NotSupportedException("Regular color sole native construction returned no texture.");
                if (!ReferenceEquals(RequireRegularColorTargetLocked(target), original) ||
                    !ReferenceEquals(original.Native, native) || _guestImages.ContainsKey(layout.Address))
                    throw new NotSupportedException("Regular color first native construction owner changed before installation.");
                // Pending CPU seeds have no content authority in this mapping
                // class. Do not infer a seed from captured eight probe bytes.
                RequireNoRegularColorPendingSeedLocked(original);
                _guestImages.Add(layout.Address, image);
                _guestImageExtents.Add(layout.Address, (layout.Width, layout.Height, layout.ByteCount));
                native.Installed = true;
            }
            return image;
        }
        catch (Exception failure)
        {
            // Exact return fields are already rooted before any possibly
            // throwing gate acquisition. Failure latching cannot require a
            // second acquisition of that interrupted gate.
            Volatile.Write(ref native.Unknown, true); Volatile.Write(ref original.Unknown, true);
            Interlocked.CompareExchange(ref _regularColorOriginalFailure, failure, null);
            // No automatic release, retry, recapture or removal after an
            // unknown native creation/publication. Exact owner stays rooted.
            throw;
        }
    }

    private static void RecordRegularColorNativeCreationReturn(RegularColorNativeOwner native, nint texture)
    {
        // No query, allocation, lock or callback between an actual returned
        // native handle and its already-rooted exact owner fields.
        native.Texture = texture; native.Image.Texture = texture; native.CreationReturned = true;
    }

    private static void RequireNoRegularColorPendingSeedLocked(RegularColorOriginal original)
    {
        if (_pendingGuestImageInitialData.ContainsKey(original.Observation.Placement.Address))
            throw new NotSupportedException("Regular color pending content seed has no qualified original content receipt.");
    }

    private static void RequireRegularColorCpuImageEffectLocked(GuestImage image, bool replace)
    {
        if (!_regularColorImages.TryGetValue(image, out var native)) return;
        RequireRegularColorNativeImageLocked(native.Original, image, native.Original.Observation.Placement.NativeFormat);
        if (replace)
            throw new NotSupportedException("Regular color CPU pixel replacement has no qualified native-owner transfer.");
    }

    private static void RetireRegularColorImageLocked(GuestImage image)
    {
        if (!_regularColorImages.TryGetValue(image, out var native)) return;
        native.Original.Retired = true; // preserve FIRST original/native owner
    }
}
