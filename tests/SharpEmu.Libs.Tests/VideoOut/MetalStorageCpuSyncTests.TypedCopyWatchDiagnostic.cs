// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Actual provider watches/reservations over owned pinned CLR bytes only.
    // No native selector, publication commit, byte retry or supplied authority.
    private sealed class TypedCopyWatchDiagnosticControl : IDisposable
    {
        private static readonly List<object[]> UnknownOwners = [];
        internal readonly DisplayLedgerMemory Memory = new();
        internal readonly TypedCopyWatchDiagnosticSource Source;
        internal CpuMappedCopyObservation? Original;
        internal IDisposable? Receipt;
        internal object? Writer;
        private bool watchReleased, disposed, cleanupUnknown;
        internal TypedCopyWatchDiagnosticControl(bool ownerMetadata = true)
        {
            Source = new(Memory);
            try
            {
                Memory.Backing.EnrollManagedWrites();
                var writers = typeof(HostBackingObject).GetProperty("ManagedWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Memory.Backing)!;
                writers.GetType().GetField("admissionDiagnosticsForControl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(writers, ownerMetadata ? 1 : 0);
                Assert.Equal(CpuMappedCopyResult.Copied, Source.TryReadMappedCopyWithWriteWatch(Address,
                    DisplayLedgerMemory.ByteCount, Address + 32, new byte[16], out Original));
                Assert.NotNull(Original);
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { primary.Data["TypedCopyDiagnosticControlCleanup"] = cleanup; }
                throw;
            }
        }
        internal void BeginWriter(bool overlap = false) => Writer = typeof(HostBackingObject)
            .GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Memory.Backing, [overlap ? 32UL : 16384UL, 1UL]);
        internal void EndWriter()
        {
            if (Writer is not { } exact) return;
            Writer = null; // once only; unknown exact consumed receipt stays rooted below
            var owners = new object[] { this, exact }; UnknownOwners.Add(owners);
            try { typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [exact, true]); }
            catch { cleanupUnknown = true; throw; }
            UnknownOwners.Remove(owners);
        }
        internal void ReleaseWatch()
        {
            if (watchReleased || Original is null) return;
            var owners = new object[] { this, Original }; UnknownOwners.Add(owners);
            watchReleased = true;
            try { Memory.Ledger.ReleaseMappedCopyWriteWatch(Original); }
            catch { cleanupUnknown = true; throw; }
            UnknownOwners.Remove(owners);
        }
        internal void CancelReceipt()
        {
            if (Receipt is not { } exact) return;
            var owners = new object[] { this, exact }; UnknownOwners.Add(owners);
            Receipt = null;
            try { exact.Dispose(); }
            catch { cleanupUnknown = true; throw; }
            UnknownOwners.Remove(owners);
        }
        internal object Prepare(string kind)
        {
            var source = Original!;
            if (kind != "original-read")
            {
                Assert.Equal(CpuMappedCopyResult.Copied, Memory.Ledger.TryReadMappedCopy(Memory.Backing,
                    Address + 16384, 16, Address + 16384, new byte[16], out var disjoint));
                source = Assert.IsType<CpuMappedCopyObservation>(disjoint);
            }
            if (kind == "publication")
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, Memory.Ledger.TryPrepareMappedPublication(
                    Memory.Backing, source, [new CpuMappedWriteRun(source.SourceAddress, new byte[16])], out var receipt));
                Receipt = Assert.IsType<CpuMappedPublication>(receipt);
            }
            else if (kind == "validation")
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    Memory.Ledger.TryPrepareMappedPublicationValidation(Memory.Backing, source, out var receipt));
                Receipt = Assert.IsType<CpuMappedPublicationValidation>(receipt);
            }
            else
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    Memory.Ledger.TryPrepareMappedReadValidation(Memory.Backing, source, out var receipt));
                Receipt = Assert.IsType<CpuMappedReadValidation>(receipt);
            }
            var transaction = Receipt.GetType().GetField("transaction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Receipt)!;
            return transaction.GetType().GetField("Admission", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transaction)!;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            // Provisional strong root precedes consuming any exact cleanup.
            // On unknown return retain everything; never retry, force-clear or
            // dispose potentially held backing ownership after that failure.
            var owners = new object[] { this, Memory, Original!, Receipt!, Writer! }; UnknownOwners.Add(owners);
            if (cleanupUnknown) throw new InvalidOperationException("Unknown exact diagnostic fixture cleanup remains quarantined.");
            EndWriter(); CancelReceipt();
            ReleaseWatch(); Assert.Equal(0, Memory.Backing.ActiveAccesses);
            Memory.Dispose(); UnknownOwners.Remove(owners);
        }
    }
    private sealed class TypedCopyWatchDiagnosticSource(DisplayLedgerMemory memory) : ICpuMappedReadWatchSource
    {
        internal int Copies, BooleanQueries, AttributedQueries, MappingQueries;
        internal Action? AfterGuard;
        internal CpuMappedCopyObservation? LastOriginal;
        public bool CanWaitForMappedReadWatchStatus => throw new InvalidOperationException("Diagnostics cannot request waiting.");
        public bool CanWaitForMappedReadWatchEnrollment => throw new InvalidOperationException("Diagnostics cannot enroll another watch.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? observation) => throw new InvalidOperationException("No replacement copy.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source,
            Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { Copies++; return memory.Ledger.TryReadMappedCopyWithWriteWatch(memory.Backing, address, count, source, bytes, out observation); }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original)
        { MappingQueries++; return memory.Ledger.IsCurrent(memory.Backing, original); }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original)
        { BooleanQueries++; LastOriginal = original; var result = memory.Ledger.IsMappedCopyWriteWatchCurrent(memory.Backing, original); AfterGuard?.Invoke(); return result; }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original, out CpuMappedReadWatchDiagnostic diagnostic)
        { AttributedQueries++; LastOriginal = original; var result = memory.Ledger.IsMappedCopyWriteWatchCurrent(memory.Backing, original, out diagnostic); AfterGuard?.Invoke(); return result; }
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original) => throw new InvalidOperationException("No status reclassification.");
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => throw new InvalidOperationException("Guard cannot release its original.");
    }
    private sealed class TypedCopyBooleanOnlySource(TypedCopyWatchDiagnosticSource source) : ICpuMappedReadWatchSource
    {
        public CpuMappedCopyResult TryReadMappedCopy(ulong a, ulong b, ulong c, Span<byte> d, out CpuMappedCopyObservation? e) => throw new InvalidOperationException("No copy.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong a, ulong b, ulong c, Span<byte> d, out CpuMappedCopyObservation? e) => throw new InvalidOperationException("No capture.");
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => throw new InvalidOperationException("No extra mapping guard.");
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => source.IsMappedCopyWriteWatchCurrent(original);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => throw new InvalidOperationException("No release.");
    }
    private static bool InspectTypedCopyWatch(ICpuMappedReadWatchSource source, CpuMappedCopyObservation original,
        bool diagnose, out CpuMappedReadWatchDiagnostic? first)
    {
        object?[] args = [source, original, diagnose, null];
        var result = (bool)typeof(MetalTextureSnapshots).GetMethod("InspectTypedCopyOriginalWriteWatch", Private)!.Invoke(null, args)!;
        first = (CpuMappedReadWatchDiagnostic?)args[3]; return result;
    }

    [Theory]
    [InlineData(false, "current")] [InlineData(true, "current")]
    [InlineData(false, "busy")] [InlineData(true, "busy")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "released")] [InlineData(true, "released")]
    [InlineData(false, "mapping-stale")] [InlineData(true, "mapping-stale")]
    public void TypedCopyFirstBooleanDiagnosticPreservesActualProviderResultAndSingleGuard(bool diagnose, string state)
    {
        ActualArchitecture(); using var control = new TypedCopyWatchDiagnosticControl();
        var bytes = control.Memory.Expected;
        switch (state)
        {
            case "busy": control.BeginWriter(); break;
            case "revoked": control.BeginWriter(true); control.EndWriter(); break;
            case "released": control.ReleaseWatch(); break;
            case "mapping-stale": lock (control.Memory.Gate) Assert.True(control.Memory.Ledger.BeforeNativeEffect(Address, DisplayLedgerMemory.ByteCount)); break;
        }
        Assert.Equal(state == "current", InspectTypedCopyWatch(control.Source, control.Original!, diagnose, out var first));
        Assert.Equal(diagnose ? 0 : 1, control.Source.BooleanQueries);
        Assert.Equal(diagnose ? 1 : 0, control.Source.AttributedQueries);
        Assert.Equal(1, control.Source.Copies); Assert.Equal(0, control.Source.MappingQueries);
        Assert.Same(control.Original, control.Source.LastOriginal);
        Assert.Equal(bytes, control.Memory.Expected);
        if (!diagnose || state == "current") Assert.Null(first);
        else
        {
            Assert.False(first!.Value.BooleanCurrent); Assert.Null(first.Value.ProviderStatus); Assert.Null(first.Value.WatchStatus);
            if (state == "busy") Assert.Equal(1L, first.Value.PrimaryAdmission);
        }
    }

    [Theory]
    [InlineData(false, "publication")] [InlineData(true, "publication")]
    [InlineData(false, "validation")] [InlineData(true, "validation")]
    [InlineData(false, "original-read")] [InlineData(true, "original-read")]
    public void TypedCopyFirstPublicationOwnerAttributionDoesNotAuthorizeOrRefreshOriginal(bool diagnose, string kind)
    {
        ActualArchitecture(); using var control = new TypedCopyWatchDiagnosticControl();
        var bytes = control.Memory.Expected; var owner = control.Prepare(kind);
        var ownerId = (long)owner.GetType().GetField("Identity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        Assert.False(InspectTypedCopyWatch(control.Source, control.Original!, diagnose, out var first));
        Assert.Equal(diagnose ? 0 : 1, control.Source.BooleanQueries); Assert.Equal(diagnose ? 1 : 0, control.Source.AttributedQueries);
        Assert.Equal(1, control.Source.Copies); Assert.Equal(0, control.Source.MappingQueries);
        Assert.Same(control.Original, control.Source.LastOriginal);
        Assert.Equal(bytes, control.Memory.Expected);
        if (!diagnose) { Assert.Null(first); return; }
        var detached = first!.Value;
        Assert.Equal(1L << 32, detached.PrimaryAdmission); Assert.False(detached.BooleanCurrent);
        Assert.Null(detached.ProviderStatus); Assert.Null(detached.WatchStatus);
        var expected = kind == "publication" ? CpuMappedAdmissionOwnerKind.Publication : kind == "validation" ?
            CpuMappedAdmissionOwnerKind.PublicationValidation : CpuMappedAdmissionOwnerKind.OriginalReadValidation;
        Assert.Equal(ownerId, detached.AdmissionOwner!.Value.Identity); Assert.True(ownerId > 0);
        Assert.Equal(expected, detached.AdmissionOwner.Value.Kind);
        Assert.Equal(Environment.CurrentManagedThreadId, detached.AdmissionOwner.Value.AcquiringThread);
        Assert.Equal(CpuMappedAdmissionOwnerPhase.Prepared, detached.AdmissionOwner.Value.Phase);
        Assert.Equal(CpuMappedAdmissionOwnerOutcome.None, detached.AdmissionOwner.Value.Outcome);
        control.CancelReceipt(); // exact known cancellation, no commit
        Assert.Equal(CpuMappedAdmissionOwnerPhase.Prepared, detached.AdmissionOwner.Value.Phase); // no refreshed owner scalar
        var message = (string)typeof(MetalTextureSnapshots).GetMethod("FormatTypedCopyOriginalWatchRefusal", Private)!.Invoke(null, [first])!;
        Assert.Contains($"same_query_admission_owner_id={ownerId};", message);
        Assert.Contains($"same_query_admission_owner_kind={expected};", message);
        Assert.Contains($"same_query_admission_owner_thread={Environment.CurrentManagedThreadId};", message);
        Assert.Contains("same_query_admission_owner_phase=Prepared;", message);
        Assert.Contains("attribution_not_authority=true", message);
        Assert.Contains("caller_local_ticket_context=not-queried-no-wait-authority", message);
        Assert.Equal(bytes, control.Memory.Expected); Assert.Equal(0, control.Memory.Backing.ActiveAccesses);
    }

    [Fact]
    public void TypedCopyFirstBooleanRefusalSurvivesActualWriterReleaseBeforeReturn()
    {
        ActualArchitecture(); using var control = new TypedCopyWatchDiagnosticControl();
        var bytes = control.Memory.Expected; control.BeginWriter();
        control.Source.AfterGuard = control.EndWriter;
        Assert.False(InspectTypedCopyWatch(control.Source, control.Original!, true, out var first));
        Assert.Null(control.Writer); Assert.Equal(1L, first!.Value.PrimaryAdmission);
        Assert.False(first.Value.BooleanCurrent); Assert.Equal("admission-nonzero", first.Value.WatchGuard);
        Assert.Null(first.Value.ProviderStatus); Assert.Null(first.Value.WatchStatus);
        Assert.Equal(0, control.Source.BooleanQueries); Assert.Equal(1, control.Source.AttributedQueries);
        Assert.Equal(1, control.Source.Copies); Assert.Equal(0, control.Source.MappingQueries);
        Assert.Same(control.Original, control.Source.LastOriginal); Assert.Equal(bytes, control.Memory.Expected);
        // No later currentness/status query may rewrite this actual first
        // refusal after the real provider-owned writer has completed.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TypedCopyDiagnosticDefaultOverloadDelegatesExactlyOneOriginalBoolean(bool current)
    {
        ActualArchitecture(); using var control = new TypedCopyWatchDiagnosticControl();
        if (!current) control.BeginWriter();
        Assert.Equal(current, InspectTypedCopyWatch(new TypedCopyBooleanOnlySource(control.Source), control.Original!, true, out var first));
        Assert.Equal(1, control.Source.BooleanQueries); Assert.Equal(0, control.Source.AttributedQueries);
        Assert.Equal(1, control.Source.Copies); Assert.Equal(0, control.Source.MappingQueries);
        Assert.Same(control.Original, control.Source.LastOriginal);
        if (current) Assert.Null(first);
        else { Assert.Equal("source-boolean-unattributed", first!.Value.WatchGuard); Assert.False(first.Value.BooleanCurrent); Assert.Null(first.Value.AdmissionOwner); }
    }
}
