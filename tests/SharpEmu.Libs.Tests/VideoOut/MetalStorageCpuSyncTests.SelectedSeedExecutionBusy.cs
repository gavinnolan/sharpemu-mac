// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Actual managed ledger, pinned owned bytes and frontend capture/admission.
    // Executing-sequence/tracker scalars model metadata only: no native image,
    // Metal encoding, GPU commit, permission or backing lease is manufactured.
    [SelectedSeedExecutionBusyTheory]
    [InlineData("current")] [InlineData("disjoint-busy-clears")] [InlineData("shared-original-alias")]
    [InlineData("overlap")] [InlineData("publication")]
    [InlineData("session")] [InlineData("association")]
    [InlineData("sequence")] [InlineData("capability-lost")]
    [InlineData("final-gap-busy")] [InlineData("provider-gate")]
    [InlineData("unknown-provider")] [InlineData("intersect-after-busy")]
    [InlineData("token-change")] [InlineData("mapping-change")]
    [InlineData("association-removed")] [InlineData("association-added")]
    [InlineData("persistent-busy")]
    public void ActualFrontendOriginalSelectedSeedFirstExecutionPreservesExactReceipt(string variant)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE"));
        ActualArchitecture(); using var fixture = new SelectedSeedExecutionMetadataFixture(extraTexture: variant is "association-added" or "shared-original-alias", aliasInitially: variant == "shared-original-alias");
        var source = fixture.Source; var mapping = source.First!; var pixels = fixture.Texture.RgbaPixels.ToArray();
        var before = source.Memory.Bytes(); object? publication = null; Exception? primary = null;
        try
        {
            if (variant is not "current" and not "final-gap-busy" and not "shared-original-alias") source.BeginWriter(variant == "overlap");
            if (variant == "publication")
            {
                source.EndWriter();
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    source.Memory.Ledger.TryPrepareMappedReadValidation(source.Memory.Backing, mapping, out var validation));
                publication = Assert.IsType<CpuMappedReadValidation>(validation);
            }
            if (variant == "sequence") Field("_executingGuestWorkSequence").SetValue(null, fixture.Sequence + 1);
            source.AfterStatus = (query, status) =>
            {
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.False(Monitor.IsEntered(source.Memory.Gate));
                Assert.Same(mapping, source.First);
                if (query != 1) return;
                if (variant is "disjoint-busy-clears" or "session" or "association" or "capability-lost" or "intersect-after-busy" or "token-change" or "mapping-change" or "association-removed" or "association-added" or "persistent-busy")
                    Assert.Equal(CpuMappedReadWatchStatus.Busy, status);
                if (variant == "session") Field("_selectedStorageMipSession").SetValue(null, fixture.Session + 1);
                if (variant == "association") fixture.ReplaceAssociation();
                if (variant == "association-removed") fixture.RemoveAssociation();
                if (variant == "association-added") fixture.AddExtraAssociation();
                if (variant == "capability-lost") source.Allowed = false;
                if (variant is "disjoint-busy-clears" or "session" or "association" or "capability-lost" or "intersect-after-busy" or "token-change" or "mapping-change" or "association-removed" or "association-added") source.EndWriter();
                if (variant == "intersect-after-busy") { source.BeginWriter(true); source.EndWriter(); }
                if (variant == "token-change") fixture.ChangeReadGeneration();
                if (variant == "mapping-change") lock (source.Memory.Gate)
                    Assert.True(source.Memory.Ledger.BeforeNativeEffect(Address, CanonicalPrepareMemory.ByteCount));
            };
            if (variant == "final-gap-busy") source.BeforeBoolean = source.BeginDisjointWriter;
            if (variant == "unknown-provider") source.Allowed = false;
            IDisposable Begin()
            {
                if (variant == "provider-gate") lock (source.Memory.Gate) return fixture.Begin();
                return fixture.Begin();
            }
            if (variant is "current" or "disjoint-busy-clears" or "shared-original-alias")
            {
                using (Begin())
                {
                    Assert.Equal(2, PrecommitGet(fixture.Seed, "Phase"));
                    Assert.Same(fixture.Work, PrecommitGet(fixture.Seed, "Work"));
                    Assert.Equal(fixture.Sequence, PrecommitGet(fixture.Seed, "Sequence"));
                    Assert.Same(Thread.CurrentThread, PrecommitGet(fixture.Seed, "Thread"));
                    Assert.Throws<InvalidOperationException>(() => fixture.Begin());
                }
                Assert.Equal(variant == "disjoint-busy-clears" ? 2 : 1, source.StatusQueries);
                Assert.Equal(1, source.BooleanQueries); // unchanged final strict watch predicate
                Assert.Equal(3, PrecommitGet(fixture.Seed, "Phase"));
            }
            else
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var refused = variant is "overlap" or "publication" or "capability-lost" or "intersect-after-busy" or "persistent-busy"
                    ? Assert.Throws<NotSupportedException>(() => Begin())
                    : (Exception)Assert.Throws<InvalidOperationException>(() => Begin());
                if (variant is "final-gap-busy" or "provider-gate" or "unknown-provider")
                { Assert.Contains("original-physical-read-watch-veto", refused.Message); Assert.Equal(variant == "final-gap-busy" ? 1 : 0, source.StatusQueries); Assert.Equal(1, source.BooleanQueries); }
                else if (variant is "overlap" or "publication" or "intersect-after-busy")
                { Assert.Contains("Stale/unknown", refused.Message); Assert.Equal(variant == "intersect-after-busy" ? 2 : 1, source.StatusQueries); Assert.Equal(0, source.BooleanQueries); }
                else if (variant == "persistent-busy")
                { Assert.Contains("expired", refused.Message); Assert.InRange(source.StatusQueries, 1, 501); Assert.Equal(0, source.BooleanQueries); Assert.InRange(elapsed.ElapsedMilliseconds, 450L, 3000L); }
                else
                {
                    // An added alias leaves the saved original valid through
                    // Busy -> Current; only the frozen Core rejects the new link.
                    if (variant == "association-added") Assert.Contains("cannot adopt an added original", refused.Message);
                    var expectedStatusQueries = variant == "sequence" ? 0 : variant == "association-added" ? 2 : 1;
                    Assert.True(source.StatusQueries == expectedStatusQueries,
                        $"variant={variant}; expected statusQueries={expectedStatusQueries}; actual={source.StatusQueries}; refusal={refused.Message}");
                    Assert.True(source.BooleanQueries == 0,
                        $"variant={variant}; expected final BooleanQueries=0; actual={source.BooleanQueries}; refusal={refused.Message}");
                }
                Assert.Equal(3, PrecommitGet(fixture.Seed, "Phase"));
            }
            Assert.True(source.Copies == 1, $"variant={variant}; expected copies=1; actual={source.Copies}");
            Assert.True(source.LegacyReads == 0, $"variant={variant}; expected legacyReads=0; actual={source.LegacyReads}");
            Assert.Equal(pixels, fixture.Texture.RgbaPixels); Assert.Equal(before, source.Memory.Bytes());
            Assert.Same(mapping, PrecommitGet(fixture.Seed, "Mapping")); Assert.Same(source, PrecommitGet(fixture.Seed, "Source"));
            Assert.True(source.Releases == 1, $"variant={variant}; expected watchReleases=1; actual={source.Releases}");
            Assert.Equal(1, PrecommitGet(fixture.Seed, "WriteWatchConsumed"));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { cleanup ??= error; } }
            // Exact cancel receipt rooted before its sole attempt. Unknown
            // outcome quarantines the fixture/backing and is never retried.
            if (publication is not null) { var exact = publication; publication = null; Attempt(() => fixture.CancelValidation((CpuMappedReadValidation)exact)); }
            Attempt(source.EndWriter);
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                else primary.Data["SelectedSeedOwnedCleanup"] = cleanup;
            }
        }
    }

    [SelectedSeedExecutionBusyTheory]
    [InlineData("watch-busy", "original-physical-read-watch-veto", 1)]
    [InlineData("mapping", "original-mapping-currentness-veto", 0)]
    [InlineData("read", "original-read-generation", 0)]
    public void ActualSelectedSeedAdmissionRefusalNamesItsFirstPredicateWithoutStatusWaitOrRecapture(string variant, string reason, int booleans)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE"));
        ActualArchitecture(); using var fixture = new SelectedSeedExecutionMetadataFixture(admit: false);
        var source = fixture.Source; var before = source.Memory.Bytes(); var exact = source.First;
        if (variant == "watch-busy") { source.BeginDisjointWriter(); source.AfterBoolean = source.EndWriter; }
        if (variant == "read") fixture.ChangeReadGeneration();
        if (variant == "mapping") lock (source.Memory.Gate)
            Assert.True(source.Memory.Ledger.BeforeNativeEffect(Address, CanonicalPrepareMemory.ByteCount));
        try
        {
            var refused = Assert.Throws<InvalidOperationException>(() => fixture.Admit());
            Assert.Contains($"stage=admission reason={reason}", refused.Message);
            Assert.Contains("no_later_query=true", refused.Message);
            if (variant == "watch-busy")
            {
                if (Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_COPY_DIAGNOSTICS") == "1")
                { Assert.Contains("first_boolean=False", refused.Message); Assert.Contains("first_admission=1", refused.Message); }
                Assert.Equal(CpuMappedReadWatchStatus.Current,
                    source.Memory.Ledger.GetMappedCopyWriteWatchStatus(source.Memory.Backing, source.First!));
            }
            Assert.Equal(booleans, source.BooleanQueries); Assert.Equal(0, source.StatusQueries);
            Assert.Equal(1, source.Copies); Assert.Equal(0, source.LegacyReads); Assert.Same(exact, source.First);
            Assert.Equal(before, source.Memory.Bytes()); Assert.Equal(0, PrecommitGet(fixture.Seed, "Phase"));
            Assert.Equal(0L, Field("_enqueuedGuestWorkSequence").GetValue(null));
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
        }
        finally { source.EndWriter(); }
    }

    [SelectedSeedExecutionBusyTheory]
    [InlineData("unknown-release")]
    public void SelectedSeedPreflightFailureRetainsItsExactConsumedUnknownCleanupRoots(string _)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE"));
        ActualArchitecture(); var fixture = new SelectedSeedExecutionMetadataFixture();
        var source = fixture.Source; var injected = new IOException("decline-only managed provider release boundary");
        try
        {
            source.BeginDisjointWriter(); source.ReleaseFailure = injected;
            source.AfterStatus = (_, status) =>
            {
                Assert.Equal(CpuMappedReadWatchStatus.Busy, status);
                source.EndWriter(); fixture.RemoveAssociation();
            };
            var primary = Assert.Throws<InvalidOperationException>(() => fixture.Begin());
            Assert.Same(injected, primary.Data["MetalSelectedSeedWatchCleanup"]);
            Assert.Same(injected, Field("_selectedNativeOriginalCleanupFailure").GetValue(null));
            var roots = Field("_selectedSeedExecutionCleanupRoots").GetValue(null)!;
            var saved = Assert.Single(((IEnumerable)PrecommitGet(roots, "Originals")!).Cast<object>());
            Assert.Same(fixture.Seed, Property(saved!, "Seed")); Assert.Same(fixture.Owner, Property(saved!, "Owner"));
            Assert.Equal(1, source.Releases); Assert.Equal(1, PrecommitGet(fixture.Seed, "WriteWatchConsumed"));
            Assert.Equal(3, PrecommitGet(fixture.Seed, "Phase")); Assert.Equal(1, source.Copies);
            Assert.Throws<InvalidOperationException>(() => fixture.Dispose());
            Assert.Equal(1, source.Releases); // consumed unknown release is never retried
        }
        finally
        {
            source.EndWriter(); fixture.Dispose(); // once-only disposal now returns; exact pinned roots remain quarantined
        }
        // Synthetic provider exception is a refusal/retention control only.
        // This one fresh worker intentionally keeps unknown managed roots until
        // process exit; no native effect or successful cleanup is claimed.
    }

    private sealed class SelectedSeedExecutionSource : ICpuMemory, ICpuMappedReadWatchSource, IDisposable
    {
        internal readonly CanonicalPrepareMemory Memory = new();
        internal CpuMappedCopyObservation? First;
        internal int Copies, LegacyReads, BooleanQueries, StatusQueries, Releases;
        internal bool Allowed = true;
        internal Action<int, CpuMappedReadWatchStatus>? AfterStatus;
        internal Action? BeforeBoolean, AfterBoolean;
        internal Exception? ReleaseFailure;
        private object? writer, consumedUnknownWriter;
        private bool disposed, watchConsumed, unknownWatch;
        internal bool HasUnconsumedWatch => First is not null && !watchConsumed;
        private static readonly List<object> UnknownRoots = [];
        public bool CanWaitForMappedReadWatchStatus => Allowed && Memory.Ledger.CanWaitForMappedReadWatchStatus;
        public bool TryRead(ulong address, Span<byte> bytes) { LegacyReads++; throw new InvalidOperationException("No legacy capture."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("No guest write.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong a, ulong b, ulong c, Span<byte> bytes, out CpuMappedCopyObservation? mapping)
            => throw new InvalidOperationException("Actual watched frontend must not use generic recapture.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong a, ulong b, ulong c, Span<byte> bytes, out CpuMappedCopyObservation? mapping)
        { Copies++; var result = Memory.Ledger.TryReadMappedCopyWithWriteWatch(Memory.Backing, a, b, c, bytes, out mapping); First ??= mapping; return result; }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation mapping)
        { Assert.Same(First, mapping); return Memory.Ledger.IsCurrent(Memory.Backing, mapping); }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation mapping)
        { BooleanQueries++; BeforeBoolean?.Invoke(); var current = Memory.Ledger.IsMappedCopyWriteWatchCurrent(Memory.Backing, mapping); AfterBoolean?.Invoke(); return current; }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation mapping, out CpuMappedReadWatchDiagnostic diagnostic)
        { BooleanQueries++; BeforeBoolean?.Invoke(); var current = Memory.Ledger.IsMappedCopyWriteWatchCurrent(Memory.Backing, mapping, out diagnostic); AfterBoolean?.Invoke(); return current; }
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation mapping)
        { StatusQueries++; var status = Memory.Ledger.GetMappedCopyWriteWatchStatus(Memory.Backing, mapping); AfterStatus?.Invoke(StatusQueries, status); return status; }
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation mapping)
        {
            Assert.Same(First, mapping); Assert.False(watchConsumed); watchConsumed = true; Releases++;
            try { if (ReleaseFailure is not null) throw ReleaseFailure; Memory.Ledger.ReleaseMappedCopyWriteWatch(mapping); }
            catch { unknownWatch = true; RetainUnknown(); throw; }
        }
        internal void BeginDisjointWriter() => BeginWriter(false);
        internal void BeginWriter(bool overlap)
        {
            Assert.Null(writer);
            writer = typeof(HostBackingObject).GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Memory.Backing, [overlap ? 131072UL : 32768UL, 1UL]); Assert.NotNull(writer);
        }
        internal void EndWriter()
        {
            var exact = writer; writer = null; if (exact is null) return;
            consumedUnknownWriter = exact;
            try { typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [exact, true]); consumedUnknownWriter = null; }
            catch { RetainUnknown(); throw; }
        }
        internal void RetainUnknown() { lock (UnknownRoots) if (!UnknownRoots.Contains(this)) UnknownRoots.Add(this); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try
            {
                EndWriter();
                if (consumedUnknownWriter is not null || unknownWatch || HasUnconsumedWatch)
                    throw new InvalidOperationException("Selected seed exact cleanup remains unconfirmed; pinned owner retained.");
                Memory.Dispose();
            }
            catch { RetainUnknown(); throw; }
        }
    }

    private sealed class SelectedSeedExecutionMetadataFixture : IDisposable
    {
        private State? state;
        private OriginalWatchBusyContentMetadata? tracker;
        internal SelectedSeedExecutionSource Source = null!;
        internal GuestDrawTexture Texture = null!;
        internal object Owner = null!, Seed = null!, Work = null!;
        internal long Sequence, Session;
        private bool disposed;
        private CpuMappedReadValidation? unknownValidation;
        private object? replacement;
        private bool associationRemoved, extraAssociationInstalled;
        private GuestDrawTexture? extra;
        private static readonly List<object> UnknownRoots = [];
        internal SelectedSeedExecutionMetadataFixture(bool admit = true, bool extraTexture = false, bool aliasInitially = false)
        {
            try
            {
                // Existing global queue helper can only clear our empty lane;
                // foreign selected/native owners are refused before mutation.
                foreach (var name in new[] { "_pendingGuestWorkByQueue", "_selectedStorageMipOwners", "_selectedStorageMipPending",
                    "_selectedStorageMipImages", "_selectedStorageMipNativeQueues", "_selectedNativeOriginalProofs" })
                    Assert.Empty((IDictionary)Field(name).GetValue(null)!);
                Assert.Null(Field("_selectedNativeOriginalCleanupFailure").GetValue(null));
                Assert.False((bool)Field("_batchOpen").GetValue(null)!);
                foreach (var name in new[] { "_retiredUploadPages", "_retiredSnapshotResources", "_pendingFaultScans" })
                    Assert.Empty((IEnumerable)Field(name).GetValue(null)!);
                foreach (var name in new[] { "_rangesByAddress", "_watchRangesByAddress" })
                    Assert.False(((IDictionary)typeof(GuestImageWriteTracker).GetField(name, Private)!.GetValue(null)!).Contains(Address));
                state = new(); tracker = new(Address, 262144); Source = new();
                MetalVideoPresenter.AttachGuestMemory(Source); Session = MetalVideoPresenter.CurrentSelectedStorageMipSeedSession;
                uint[] raw = [checked((uint)(Address >> 8)), 0xC1600000, 0x003FC07F, 0x90911FAC, 511, 0x80, 0, 0];
                Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
                var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 1, [], ResourceDescriptor: raw,
                    Shape: new(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Read | GuestImageAccess.Write);
                Texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(Source, Generation.Gen5), [binding], out _));
                Owner = TableValue("_selectedStorageMipAssociations", Texture);
                Seed = TableValue("_selectedStorageMipSeeds", Owner);
                Assert.Same(Source.First, PrecommitGet(Seed, "Mapping"));
                Assert.Equal(1, Source.Copies); Source.BooleanQueries = 0; Source.StatusQueries = 0;
                var type = Presenter.GetNestedType("ComputeGuestDispatch", BindingFlags.NonPublic)!;
                if (extraTexture) extra = Texture with { RgbaPixels = Texture.RgbaPixels.ToArray() };
                if (aliasInitially) AddExtraAssociation();
                Work = Activator.CreateInstance(type, [0UL, null, extra is null ? new[] { Texture } : new[] { Texture, extra }, Array.Empty<GuestMemoryBuffer>(), 1u, 1u, 1u, 0u, 0u, 0u, null])!;
                if (admit) Admit();
                Source.BooleanQueries = 0; // admission is part of setup, execution counts start here
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { primary.Data["SelectedSeedFixtureCleanup"] = cleanup; }
                ExceptionDispatchInfo.Capture(primary).Throw(); throw;
            }
        }
        private static object TableValue(string name, object key)
        {
            var table = Field(name).GetValue(null)!; object?[] args = [key, null];
            Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!); return args[1]!;
        }
        internal void Admit()
        {
            try { lock (Field("_gate").GetValue(null)!) Sequence = (long)InvokeVolume("EnqueueGuestWorkLocked", Work)!; }
            catch (TargetInvocationException failure) when (failure.InnerException is not null)
            { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw; }
            Assert.True(Sequence > 0); Assert.Equal(1, PrecommitGet(Seed, "Phase"));
            Field("_executingGuestWorkSequence").SetValue(null, Sequence);
        }
        internal void ChangeReadGeneration() => tracker!.Generation(1);
        internal IDisposable Begin()
        {
            try { return (IDisposable)InvokeVolume("BeginSelectedStorageMipSeedExecution", Work)!; }
            catch (TargetInvocationException failure) when (failure.InnerException is not null)
            { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw; }
        }
        internal void RemoveAssociation()
        {
            var table = Field("_selectedStorageMipAssociations").GetValue(null)!;
            Assert.True((bool)table.GetType().GetMethod("Remove", [typeof(GuestDrawTexture)])!.Invoke(table, [Texture])!);
            associationRemoved = true;
        }
        internal void AddExtraAssociation()
        {
            Assert.NotNull(extra); var table = Field("_selectedStorageMipAssociations").GetValue(null)!;
            extraAssociationInstalled = true; table.GetType().GetMethod("Add")!.Invoke(table, [extra, Owner]);
        }
        internal void ReplaceAssociation()
        {
            var table = Field("_selectedStorageMipAssociations").GetValue(null)!;
            Assert.True((bool)table.GetType().GetMethod("Remove", [typeof(GuestDrawTexture)])!.Invoke(table, [Texture])!);
            replacement = Activator.CreateInstance(Owner.GetType(), [Property(Owner, "Key"), Property(Owner, "MappingEpoch"),
                Property(Owner, "Token"), Property(Owner, "HasToken"), Property(Owner, "Session"), Property(Owner, "Observation")])!;
            table.GetType().GetMethod("Add")!.Invoke(table, [Texture, replacement]);
        }
        internal void CancelValidation(CpuMappedReadValidation exact)
        {
            unknownValidation = exact;
            try { exact.Dispose(); unknownValidation = null; }
            catch { lock (UnknownRoots) UnknownRoots.Add(this); Source.RetainUnknown(); throw; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (unknownValidation is not null) { lock (UnknownRoots) UnknownRoots.Add(this); Source.RetainUnknown(); throw new InvalidOperationException("Consumed validation outcome unknown; fixture retained."); }
            Exception? first = null;
            void Attempt(Action action) { try { action(); } catch (Exception failure) { first ??= failure; } }
            if (Seed is not null)
                Attempt(() =>
                {
                    object?[] args = [Seed, null]; InvokeVolume("TryReleaseSelectedSeedWatch", args);
                    if (args[1] is Exception error) ExceptionDispatchInfo.Capture(error).Throw();
                });
            else if (Source is not null && Source.HasUnconsumedWatch)
                Attempt(() => Source.ReleaseMappedCopyWriteWatch(Source.First!));
            if (Owner is not null)
                foreach (var name in new[] { "_selectedStorageMipOwners", "_selectedStorageMipPending" })
                    Attempt(() => { var table = (IDictionary)Field(name).GetValue(null)!; var key = Property(Owner, "Key");
                        if (!table.Contains(key)) return;
                        if (name == "_selectedStorageMipOwners") Assert.Same(Owner, table[key]);
                        else Assert.Equal(Sequence, Property(table[key]!, "Sequence"));
                        table.Remove(key); });
            if (extraAssociationInstalled)
                Attempt(() =>
                {
                    Assert.Same(Owner, TableValue("_selectedStorageMipAssociations", extra!));
                    var table = Field("_selectedStorageMipAssociations").GetValue(null)!;
                    Assert.True((bool)table.GetType().GetMethod("Remove", [typeof(GuestDrawTexture)])!.Invoke(table, [extra])!);
                });
            if (Texture is not null && Owner is not null)
                Attempt(() =>
                {
                    var table = Field("_selectedStorageMipAssociations").GetValue(null)!;
                    if (associationRemoved)
                    {
                        object?[] args = [Texture, null]; Assert.False((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!); return;
                    }
                    Assert.Same(replacement ?? Owner, TableValue("_selectedStorageMipAssociations", Texture));
                    Assert.True((bool)table.GetType().GetMethod("Remove", [typeof(GuestDrawTexture)])!.Invoke(table, [Texture])!);
                });
            Attempt(() => Source?.Dispose());
            var trackerRestored = tracker is null;
            Attempt(() => { tracker?.Dispose(); trackerRestored = true; });
            if (trackerRestored) Attempt(() => state?.Dispose()); // never untrack a foreign replacement after failed exact restore
            if (first is not null) { lock (UnknownRoots) UnknownRoots.Add(this); ExceptionDispatchInfo.Capture(first).Throw(); }
        }
    }
}

internal sealed class SelectedSeedExecutionBusyTheoryAttribute : TheoryAttribute
{
    public SelectedSeedExecutionBusyTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE") != "1")
            Skip = "Separately filtered fresh-worker watched-seed metadata controls require SELECTED_NATIVE_REUSE=1 before Metal initialization; no native effects.";
    }
}
