// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Cached exception-path scalar snapshot only. This never retains a native
    // handle, creates mapping proof, or queries an owner after gate release.
    private readonly record struct FiniteStorageCohortOffender(ulong RegistryAddress, string Branch,
        uint Width, uint Height, uint Depth, uint ArrayLength, uint NativeType, uint NativeFormat,
        int Version, bool GpuWritten, bool Initialized, bool OriginPresent,
        long? OriginalSession, ulong? LayoutAddress, ulong? LayoutBytes,
        ulong? SourceAddress, ulong? SourceBytes, ulong? FootprintAddress, ulong? FootprintBytes,
        ImageDiagnosticOrigin Creation, ImageDiagnosticWork Mutation,
        int? VolumeState, bool? VolumeUnknownProducer, string? VolumeUnknownReason, int? VolumeVersion);

    private sealed class FiniteStorageCohortRefusal(string message, FiniteStorageCohortOffender offender)
        : NotSupportedException(message)
    {
        internal FiniteStorageCohortOffender Offender { get; } = offender;
    }

    private static FiniteStorageCohortOffender CaptureFiniteStorageCohortOffender(
        ulong registryAddress, GuestImage image, string branch)
    {
        var original = image.StorageCpuOrigin;
        return new(registryAddress, branch, image.Width, image.Height, image.Depth, image.ArrayLength,
            image.TextureType, (uint)image.Format, image.ContentVersion, image.GpuWritten, image.Initialized,
            original is not null, original?.Session, original?.Layout.Address, original?.Layout.PhysicalBytes,
            original?.Mapping.SourceAddress, original?.Mapping.SourceByteCount,
            original?.Mapping.FootprintAddress, original?.Mapping.FootprintByteCount,
            _imageProvenanceEnabled ? image.DiagnosticOrigin : default,
            _imageProvenanceEnabled ? image.DiagnosticMutation : default,
            _volumeOwnerDeclineDiagnostics && original?.VolumeWatch is { } watch ? Volatile.Read(ref watch.State) : null,
            _volumeOwnerDeclineDiagnostics ? original?.VolumeWatch?.UnknownProducer : null,
            _volumeOwnerDeclineDiagnostics ? original?.VolumeWatch?.DiagnosticUnknownProducerReason : null,
            _volumeOwnerDeclineDiagnostics ? original?.VolumeWatch?.Version : null);
    }

    private static int _finiteStorageRuntimeTraceRows;
    private static int _finiteStoragePostReleaseRefusalTraceRows;
    private static int _finiteStorageRetainedOwnerRefusalTraceRows;
    private static long _finiteStorageRuntimeProducers, _finiteStorageRuntimeApplied, _finiteStorageRuntimeCanonical;

    private static void TraceFiniteStorageDispatchRefusal(ComputeGuestDispatch dispatch, Exception failure)
    {
        if (!_rgbaStorageRuntimePublicationRequested || Monitor.IsEntered(_gate)) return;
        try
        {
            var texture = dispatch.Textures.FirstOrDefault(MetalBgra1024ArrayStorageAdmission.IsQualified);
            if (texture is not null) TraceFiniteStorageRuntime("dispatch-refused", texture.Address, failure.Message,
                StorageCpuOrigin(texture), failure is FiniteStorageCohortRefusal cohort ? cohort.Offender : null);
        }
        catch { /* Even producer lookup must preserve the original exception. */ }
    }

    // Runtime opt-in only. Scalar counters are diagnostic, never receipts. One
    // bounded reservation attempt may drop a contended row; no signal calls.
    // All owner snapshots are already captured. Tracker sampling/formatting is
    // outside the presenter/provider gates and cannot replace a primary error.
    private static void TraceFiniteStorageRuntime(string stage, ulong address, string reason,
        StorageCpuSeedOrigin? origin = null, FiniteStorageCohortOffender? offender = null)
    {
        if (!_rgbaStorageRuntimePublicationRequested || Monitor.IsEntered(_gate)) return;
        try
        {
            if (stage == "producer-retained") Interlocked.Increment(ref _finiteStorageRuntimeProducers);
            else if (stage == "provider-applied") Interlocked.Increment(ref _finiteStorageRuntimeApplied);
            else if (stage == "canonical-completed") Interlocked.Increment(ref _finiteStorageRuntimeCanonical);
            ref var budget = ref _finiteStorageRuntimeTraceRows;
            if (stage == "dirty-post-release-refused") budget = ref _finiteStoragePostReleaseRefusalTraceRows;
            else if (stage == "dirty-retained-owner-refused") budget = ref _finiteStorageRetainedOwnerRefusalTraceRows;
            var rows = Volatile.Read(ref budget);
            if (rows >= 64 || Interlocked.CompareExchange(ref budget, rows + 1, rows) != rows) return;
            var original = origin?.Observation.Token;
            var current = default(GuestImageWriteTracker.ContentToken);
            var observed = origin is not null && GuestImageWriteTracker.TryGetProtectedObservedContentToken(
                address, origin.Layout.PhysicalBytes, out current);
            // Formatting follows the existing row reservation and is outside
            // presenter/provider gates. Optional metadata failure keeps primary.
            var offenderFields = offender is { } other ?
                $"; offender_registry_address=0x{other.RegistryAddress:X}; offender_branch={other.Branch}" +
                $"; offender_shape={other.Width}x{other.Height}x{other.Depth}; offender_array_length={other.ArrayLength}" +
                $"; offender_native_type={other.NativeType}; offender_native_format={other.NativeFormat}" +
                $"; offender_version={other.Version}; offender_gpu_written={other.GpuWritten}; offender_initialized={other.Initialized}" +
                $"; offender_origin_present={other.OriginPresent}; offender_original_session={other.OriginalSession}" +
                $"; offender_layout_address={other.LayoutAddress}; offender_layout_bytes={other.LayoutBytes}" +
                $"; offender_source_address={other.SourceAddress}; offender_source_bytes={other.SourceBytes}" +
                $"; offender_footprint_address={other.FootprintAddress}; offender_footprint_bytes={other.FootprintBytes}" +
                $"; offender_creation_kind={other.Creation.Work.Kind ?? "unrecorded"}; offender_creation_sequence={other.Creation.Work.Sequence}" +
                $"; offender_creation_queue='{other.Creation.Work.Queue.Name}'; offender_creation_submission={other.Creation.Work.Queue.SubmissionId}" +
                $"; offender_creation_address=0x{other.Creation.Address:X}; offender_creation_pitch={other.Creation.Pitch}" +
                $"; offender_creation_tile={other.Creation.Tile}; offender_creation_format={other.Creation.Format}/{other.Creation.Number}" +
                $"; offender_creation_type={other.Creation.Type}; offender_creation_raw={other.Creation.Raw?.ToString() ?? "unavailable"}" +
                $"; offender_creation_color=[{other.Creation.Color?.ToString() ?? "unavailable"}]" +
                $"; offender_mutation_kind={other.Mutation.Kind ?? "unrecorded"}; offender_mutation_sequence={other.Mutation.Sequence}" +
                $"; offender_volume_state={other.VolumeState}; offender_volume_unknown={other.VolumeUnknownProducer}" +
                $"; offender_volume_unknown_reason={other.VolumeUnknownReason}; offender_volume_version={other.VolumeVersion}" : "";
            Console.Error.WriteLine($"finite-storage-runtime stage={stage}; address=0x{address:X}; reason={reason[..Math.Min(reason.Length, 192)]}; " +
                $"producer_count={Volatile.Read(ref _finiteStorageRuntimeProducers)}; applied_count={Volatile.Read(ref _finiteStorageRuntimeApplied)}; canonical_count={Volatile.Read(ref _finiteStorageRuntimeCanonical)}; " +
                $"original_session={origin?.Session}; original_protected_id={original?.ProtectedIdentity}; original_protected_generation={original?.ProtectedGeneration}; " +
                $"original_watch_id={original?.WatchIdentity}; original_watch_generation={original?.WatchGeneration}; current_observed={observed}; " +
                $"current_protected_id={current.ProtectedIdentity}; current_protected_generation={current.ProtectedGeneration}; current_watch_id={current.WatchIdentity}; current_watch_generation={current.WatchGeneration}; " +
                $"original_token_equal={observed && original == current}; ordinary_observations_not_writer_authority=true{offenderFields}");
        }
        catch { /* A diagnostic cannot change ordering, refusal or the primary. */ }
    }
}
