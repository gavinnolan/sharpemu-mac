// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    internal readonly record struct SelectedSeedCopyDiagnostic(bool ProviderPresent, bool WatchedRequested,
        CpuMappedCopyResult ProviderResult, bool MappingReturned, bool? MappingCurrent, bool LegacyRead,
        ulong SourceAddress, ulong SourceBytes);
    private static readonly bool _selectedCopyDiagnostics =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_COPY_DIAGNOSTICS") == "1";
    private static int _selectedCopyDiagnosticRows;

    // Ordinary producer only. No provider/tracker query while presenter gate is
    // held; default-off returns before extra observation, counter or formatting.
    internal static void TraceSelectedSeedCopyAttempt(ulong address, int attempt, uint mip, long originalSession,
        string reason, SelectedSeedCopyDiagnostic copy, GuestImageWriteTracker.ReadSnapshot read,
        CpuTextureSnapshotObservation observation, bool hasToken, GuestImageWriteTracker.ContentToken token,
        ICpuMappedCopySource? source, CpuMappedCopyObservation? mapping,
        bool? readStable = null, bool seedChecked = false, bool seedStable = false,
        bool tokenChecked = false, bool tokenPresent = false, bool tokenStable = false,
        GuestImageWriteTracker.ContentToken decisionAfter = default)
    {
        if (!_selectedCopyDiagnostics || Monitor.IsEntered(_gate)) return;
        try
        {
            var count = Volatile.Read(ref _selectedCopyDiagnosticRows);
            if (count >= 64 || Interlocked.CompareExchange(ref _selectedCopyDiagnosticRows, count + 1, count) != count) return;
            var row = count + 1;
            var currentObserved = GuestImageWriteTracker.TryGetObservedContentToken(address, observation.ByteCount, out var current);
            var generationPresent = GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation);
            var ownerPresent = GuestImageWriteTracker.TryGetProtectionState(address, out var protect, out var armed);
            bool? mappingNow = source is not null && mapping is not null ? source.IsMappedCopyObservationCurrent(mapping) : null;
            bool? watchNow = source is ICpuMappedReadWatchSource watched && mapping is not null && copy.WatchedRequested
                ? watched.IsMappedCopyWriteWatchCurrent(mapping) : null;
            Console.Error.WriteLine($"selected-copy-attempt row={row} address=0x{address:X} attempt={attempt} mip={mip} reason={reason}" +
                $" provider_present={copy.ProviderPresent} watched_requested={copy.WatchedRequested} provider_result={copy.ProviderResult}" +
                $" source_address=0x{copy.SourceAddress:X} source_bytes={copy.SourceBytes}" +
                $" provider_mapping_returned={copy.MappingReturned} provider_mapping_current={copy.MappingCurrent} legacy_read={copy.LegacyRead}" +
                $" retained_mapping_present={mapping is not null} retained_mapping_current={mappingNow} retained_watch_current={watchNow}" +
                $" original_session={originalSession} current_session={CurrentSelectedStorageMipSeedSession}" +
                $" snapshot_active={read.Active} snapshot_generation={read.Generation} current_generation_present={generationPresent} current_generation={generation}" +
                $" decision_read_stable={readStable} decision_seed_checked={seedChecked} decision_seed_stable={seedStable}" +
                $" decision_token_checked={tokenChecked} decision_token_present={tokenPresent} decision_token_stable={tokenStable}" +
                $" has_original_token={hasToken} original_token=[{token}] decision_after_token=[{decisionAfter}]" +
                $" original_observed={observation.Observed} original_observed_token=[{observation.Token}] current_observed={currentObserved} current_observed_token=[{current}]" +
                $" original_epoch={observation.MappingEpoch} current_epoch={HostMemory.MappingGeneration} footprint_bytes={observation.ByteCount}" +
                $" owner_present={ownerPresent} protected={protect} armed={armed} samples_not_atomic_or_writer_authority=true");
            GuestImageWriteTracker.TraceSelectedCopyWriterState(address, row, attempt, reason);
        }
        catch { } // Optional argument sampling/sink failure never replaces the producer cause.
    }
}
