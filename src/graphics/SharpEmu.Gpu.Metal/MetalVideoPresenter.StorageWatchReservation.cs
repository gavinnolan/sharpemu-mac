// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static int _originalReadWatchDiagnosticRows;
    private readonly record struct OriginalReadWatchDiagnostic(CpuMappedReadWatchDiagnostic Provider,
        string? LocalGuard, bool? OriginalRead, bool? Publication, bool? Validation, bool? BooleanCurrent = null, string? LocalTicket = null, CpuMappedDisjointReadWatchDiagnostic? LocalValidation = null);
    // A reporting label only. Never consumed by admission, retry or waiting.
    private static string DescribeDiagnosticLocalTicket(bool publication, bool validation, bool originalRead) =>
        originalRead ? "original-read-local-ticket-no-wait-authority" :
        publication ? "publication-local-ticket-no-wait-authority" :
        validation ? "validation-local-ticket-no-wait-authority" : "none-diagnostic-only-no-wait-authority";
    private static bool TryReserveOriginalReadWatchDiagnostic(ICpuMemory memory)
    {
        if (!_volumeOwnerDeclineDiagnostics || Monitor.IsEntered(_gate)) return false;
        // Formatting-only capability; not a status/currentness query. Unknown
        // inherited provider gates suppress output instead of being guessed.
        try { if (memory is not ICpuMappedPublicationSource source || !source.CanWaitForMappedPublicationValidation) return false; }
        catch { return false; }
        var rows = Volatile.Read(ref _originalReadWatchDiagnosticRows);
        return rows < 64 && Interlocked.CompareExchange(ref _originalReadWatchDiagnosticRows, rows + 1, rows) == rows;
    }
    // Stack-only context threaded from the exact local provider ticket. It
    // never replaces ordinary admission/currentness or acquires a provider gate.
    private readonly struct StorageWatchReservation
    {
        private readonly CpuMappedPublication? publication;
        private readonly CpuMappedPublicationValidation? validation;
        private readonly CpuMappedReadValidation? originalRead;
        internal StorageWatchReservation(CpuMappedPublication publication) => this.publication = publication;
        internal StorageWatchReservation(CpuMappedPublicationValidation validation) => this.validation = validation;
        internal StorageWatchReservation(CpuMappedReadValidation originalRead) => this.originalRead = originalRead;
        // Classification uses the status of the actual refusing check. A
        // writer can finish before a later scalar query; that must not turn a
        // healthy Busy refusal into an unrelated fatal error. This still
        // grants Current only through the same exact local reservation.
        internal CpuMappedReadWatchStatus GetStatus(SingletonVolumeOriginalWatch watch)
        {
            if (Volatile.Read(ref watch.State) == 2 || Volatile.Read(ref watch.UnknownProducer))
                return CpuMappedReadWatchStatus.Stale;
            if (originalRead is not null)
                return originalRead.IsOriginalCurrent(watch.Mapping) ? CpuMappedReadWatchStatus.Current : CpuMappedReadWatchStatus.Stale;
            var status = watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping);
            if (status == CpuMappedReadWatchStatus.Current ||
                publication?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true ||
                validation?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true)
                return CpuMappedReadWatchStatus.Current;
            return status;
        }
        // SAME predicates/calls/short-circuit order as GetStatus. Nullable
        // outcomes mean that ticket was not queried, not that it was current.
        internal CpuMappedReadWatchStatus GetStatusWithDiagnostic(SingletonVolumeOriginalWatch watch,
            out OriginalReadWatchDiagnostic diagnostic)
        {
            diagnostic = new() { LocalTicket = DescribeDiagnosticLocalTicket(publication is not null, validation is not null, originalRead is not null) };
            if (Volatile.Read(ref watch.State) == 2)
            { diagnostic = diagnostic with { LocalGuard = "watch-consumed" }; return CpuMappedReadWatchStatus.Stale; }
            if (Volatile.Read(ref watch.UnknownProducer))
            { diagnostic = diagnostic with { LocalGuard = "watch-unknown-producer" }; return CpuMappedReadWatchStatus.Stale; }
            if (originalRead is not null)
            {
                var current = originalRead.IsOriginalCurrent(watch.Mapping);
                diagnostic = diagnostic with { OriginalRead = current };
                return current ? CpuMappedReadWatchStatus.Current : CpuMappedReadWatchStatus.Stale;
            }
            var status = watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping, out var provider);
            diagnostic = diagnostic with { Provider = provider };
            if (status == CpuMappedReadWatchStatus.Current) return status;
            if (publication is not null)
            {
                var current = publication.IsOriginalDisjointReadWatchCurrent(watch.Mapping);
                diagnostic = diagnostic with { Publication = current };
                if (current) return CpuMappedReadWatchStatus.Current;
            }
            if (validation is not null)
            {
                var current = validation.IsOriginalDisjointReadWatchCurrent(watch.Mapping, out var localValidation);
                diagnostic = diagnostic with { Validation = current, LocalValidation = localValidation };
                if (current) return CpuMappedReadWatchStatus.Current;
            }
            return status;
        }
        internal bool IsCurrent(SingletonVolumeOriginalWatch watch) =>
            Volatile.Read(ref watch.State) != 2 && !Volatile.Read(ref watch.UnknownProducer) &&
            (originalRead is not null ? originalRead.IsOriginalCurrent(watch.Mapping) :
                watch.Source.IsMappedCopyWriteWatchCurrent(watch.Mapping) ||
                publication?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true ||
                validation?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true);
        // Exact Boolean order, with no Status query or Busy classification.
        internal bool IsCurrentWithDiagnostic(SingletonVolumeOriginalWatch watch,
            out OriginalReadWatchDiagnostic diagnostic)
        {
            diagnostic = new() { LocalTicket = DescribeDiagnosticLocalTicket(publication is not null, validation is not null, originalRead is not null) };
            if (Volatile.Read(ref watch.State) == 2)
            { diagnostic = diagnostic with { LocalGuard = "watch-consumed", BooleanCurrent = false }; return false; }
            if (Volatile.Read(ref watch.UnknownProducer))
            { diagnostic = diagnostic with { LocalGuard = "watch-unknown-producer", BooleanCurrent = false }; return false; }
            if (originalRead is not null)
            {
                var current = originalRead.IsOriginalCurrent(watch.Mapping);
                diagnostic = diagnostic with { OriginalRead = current, BooleanCurrent = current }; return current;
            }
            var ordinary = watch.Source.IsMappedCopyWriteWatchCurrent(watch.Mapping, out var provider);
            diagnostic = diagnostic with { Provider = provider };
            if (ordinary) { diagnostic = diagnostic with { BooleanCurrent = true }; return true; }
            if (publication is not null)
            {
                var current = publication.IsOriginalDisjointReadWatchCurrent(watch.Mapping);
                diagnostic = diagnostic with { Publication = current };
                if (current) { diagnostic = diagnostic with { BooleanCurrent = true }; return true; }
            }
            if (validation is not null)
            {
                var current = validation.IsOriginalDisjointReadWatchCurrent(watch.Mapping, out var localValidation);
                diagnostic = diagnostic with { Validation = current, LocalValidation = localValidation };
                if (current) { diagnostic = diagnostic with { BooleanCurrent = true }; return true; }
            }
            diagnostic = diagnostic with { BooleanCurrent = false }; return false;
        }
    }
}
