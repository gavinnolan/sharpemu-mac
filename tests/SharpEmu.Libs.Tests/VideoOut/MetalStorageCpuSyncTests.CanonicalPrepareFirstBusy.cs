// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    [Theory]
    [InlineData("current")] [InlineData("owner")] [InlineData("version")]
    [InlineData("session")] [InlineData("memory")] [InlineData("mapping")]
    [InlineData("token")] [InlineData("first-original")]
    public void ActualPrepareBusyRetainsOnlySameOriginalRefusalAfterWriterFinishes(string change)
    {
        ActualArchitecture(); using var fixture = new CanonicalPreparationMetadataFixture();
        var memory = fixture.Memory; var before = memory.Bytes(); var dirty = GuestImageWriteTracker.PeekDirty(Address);
        memory.BeginWriter();
        memory.AfterPreparation = memory.EndWriter; // after the actual FIRST Busy return, before any later Current check
        object?[] prepare = [Address, fixture.Image, fixture.Debt, fixture.Origin, 1, (nint)123, null, null, null];
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected, InvokeVolume("PrepareCanonicalDirtyValidation", prepare));
        Assert.Null(prepare[6]); Assert.NotNull(prepare[7]);
        var first = Assert.IsType<CpuMappedPublicationValidationRefusal>(prepare[8]);
        Assert.Equal(CpuMappedPublicationValidationRefusalKind.AdmissionBusy, first.Kind); Assert.Equal(1, first.RejectedAdmission);
        Assert.Equal(1, memory.Preparations); Assert.Null(memory.Writer); Assert.Same(memory.First, Property(fixture.Origin, "Mapping"));
        Assert.Same(memory.First, PrecommitGet(prepare[7]!, "Mapping"));
        switch (change)
        {
            case "owner": Images[Address] = Activator.CreateInstance(fixture.Image.GetType(), nonPublic: true); break;
            case "version": Set(fixture.Image, "ContentVersion", 2); break;
            case "session": Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1); break;
            case "memory": Field("_guestMemory").SetValue(null, new Memory([1])); break;
            case "mapping": lock (memory.Gate) Assert.True(memory.Ledger.BeforeNativeEffect(Address, CanonicalPrepareMemory.ByteCount)); break;
            case "token": fixture.Protection.Generation(1); break;
        }
        object?[] retain = [Address, fixture.Image, fixture.Debt, fixture.Origin, change == "first-original" ? 2 : 1,
            (nint)123, prepare[7], null, null];
        // The old no-marker route deterministically loses an actual Busy
        // that has already cleared. Neither route reaches byte/native/ack.
        if (change == "current")
        {
            var old = retain.ToArray(); old[6] = null;
            Assert.False((bool)InvokeVolume("TryRetainCanonicalDirtyRetryCore", old)!);
            Assert.Equal("retention-initial-owner", Property(old[8]!, "Boundary"));
            Assert.Equal(CpuMappedReadWatchStatus.Current, Property(old[8]!, "OwnerStatus"));
        }
        var retained = (bool)InvokeVolume("TryRetainCanonicalDirtyRetryCore", retain)!;
        Assert.Equal(change == "current", retained);
        if (retained)
        {
            var saved = Field("_deferredCanonicalDirty").GetValue(null)!;
            Assert.Same(fixture.Origin, Property(saved, "Origin")); Assert.Same(fixture.Image, Property(saved, "Image"));
            Assert.Same(fixture.Debt, Property(saved, "Debt")); Assert.Equal(1, Property(saved, "Version"));
        }
        else
        {
            Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
            Assert.Equal(change == "first-original" ? "retention" : change is "mapping" or "token" ? "retention-original" : "retention-initial-owner",
                Property(retain[8]!, "Boundary"));
            if (change is "owner" or "version" or "session" or "memory")
                Assert.Equal(CpuMappedReadWatchStatus.Stale, Property(retain[8]!, "OwnerStatus"));
        }
        Assert.Equal(before, memory.Bytes()); Assert.Equal(dirty, GuestImageWriteTracker.PeekDirty(Address));
        Assert.Equal(1, memory.Copies); Assert.Equal(1, memory.Preparations); Assert.Equal(0, memory.LegacyCalls);
        Assert.Equal((nint)123, PrecommitGet(fixture.Image, "Texture"));
        Assert.False((bool)PrecommitGet(fixture.Debt, "Pending")!); Assert.False((bool)PrecommitGet(fixture.Debt, "Failed")!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Fact]
    public void ActualCanonicalDirtyCoreAndDeferredRetryCarryFirstPrepareBusyWithoutEffects()
    {
        ActualArchitecture(); using var fixture = new CanonicalPreparationMetadataFixture();
        var memory = fixture.Memory; var before = memory.Bytes(); var dirty = GuestImageWriteTracker.PeekDirty(Address);
        memory.BeginWriter(); memory.AfterPreparation = memory.EndWriter;
        object?[] initial = [Address, null, null];
        var initialResult = InvokeVolume("TryAcknowledgeFiniteCanonicalDirtyCore", initial)!.ToString();
        if (initialResult != "Deferred")
            Console.WriteLine($"[canonical-prepare-fixture-first-refusal] result={initialResult} detached={initial[2]}");
        Assert.Equal("Deferred", initialResult);
        var retained = Field("_deferredCanonicalDirty").GetValue(null)!; Assert.NotNull(retained);
        Assert.Same(fixture.Image, Property(retained, "Image")); Assert.Same(fixture.Debt, Property(retained, "Debt"));
        Assert.Same(fixture.Origin, Property(retained, "Origin")); Assert.Equal(1, Property(retained, "Version"));
        Assert.Equal((nint)123, Property(retained, "Texture")); Assert.Null(memory.Writer);
        memory.BeginWriter();
        Assert.Equal("Deferred", InvokeVolume("RetryDeferredCanonicalDirty")!.ToString());
        Assert.Same(retained, Field("_deferredCanonicalDirty").GetValue(null));
        Assert.Same(memory.First, Property(fixture.Origin, "Mapping")); Assert.Same(memory, Property(fixture.Origin, "Source"));
        Assert.Null(memory.Writer); Assert.Equal(2, memory.Preparations); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.LegacyCalls);
        Assert.Equal(before, memory.Bytes()); Assert.Equal(dirty, GuestImageWriteTracker.PeekDirty(Address));
        Assert.Equal((nint)123, PrecommitGet(fixture.Image, "Texture")); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.False((bool)PrecommitGet(fixture.Debt, "Pending")!); Assert.False((bool)PrecommitGet(fixture.Debt, "Failed")!);
        // Both calls stop at real Rejected/no-ticket preparation. Omitting the
        // first marker at either production call site makes this row fail;
        // the sentinel never reaches acknowledgment, bytes or a native action.
    }

    [Fact]
    public void CanonicalPrepareFixtureIsolatesAndRestoresExactPredecessorOwnerGuardMetadata()
    {
        ActualArchitecture();
        string[] guards = ["_takenGuestWorkSequence", "_typedCpuSyncCleanupFailure", "_textureSourceCleanupFailure"];
        var previous = guards.Select(name => Field(name).GetValue(null)).ToArray();
        object?[] predecessor = [41L, new InvalidOperationException("predecessor typed cleanup metadata"),
            new InvalidOperationException("predecessor texture cleanup metadata")];
        try
        {
            for (var index = 0; index < guards.Length; index++) Field(guards[index]).SetValue(null, predecessor[index]);
            using (var fixture = new CanonicalPreparationMetadataFixture())
            {
                Assert.Equal(0L, Field(guards[0]).GetValue(null));
                Assert.Null(Field(guards[1]).GetValue(null)); Assert.Null(Field(guards[2]).GetValue(null));
                var before = fixture.Memory.Bytes();
                fixture.Memory.BeginWriter(); fixture.Memory.AfterPreparation = fixture.Memory.EndWriter;
                object?[] call = [Address, null, null];
                var result = InvokeVolume("TryAcknowledgeFiniteCanonicalDirtyCore", call)!.ToString();
                if (result != "Deferred")
                    Console.WriteLine($"[canonical-prepare-fixture-first-refusal] result={result} detached={call[2]}");
                Assert.Equal("Deferred", result);
                Assert.Same(fixture.Origin, Property(Field("_deferredCanonicalDirty").GetValue(null)!, "Origin"));
                Assert.Null(fixture.Memory.Writer); Assert.Equal(1, fixture.Memory.Preparations);
                Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.LegacyCalls);
                Assert.Equal(before, fixture.Memory.Bytes());
            }
            Assert.Equal(predecessor[0], Field(guards[0]).GetValue(null));
            Assert.Same(predecessor[1], Field(guards[1]).GetValue(null));
            Assert.Same(predecessor[2], Field(guards[2]).GetValue(null));
        }
        finally
        {
            for (var index = 0; index < guards.Length; index++) Field(guards[index]).SetValue(null, previous[index]);
        }
        // Fabricated predecessor guards are test metadata, never native owner
        // or cleanup authority. The real managed ledger alone supplies Busy.
    }

    [Fact]
    public void ActualRejectedProviderWithoutFirstAttributionCannotBorrowLaterCurrent()
    {
        ActualArchitecture(); using var fixture = new CanonicalPreparationMetadataFixture();
        var before = fixture.Memory.Bytes(); var dirty = GuestImageWriteTracker.PeekDirty(Address);
        fixture.Memory.SuppressAttribution = true;
        fixture.Memory.BeginWriter(); fixture.Memory.AfterPreparation = fixture.Memory.EndWriter;
        object?[] call = [Address, null, null];
        Assert.Equal("NotApplicable", InvokeVolume("TryAcknowledgeFiniteCanonicalDirtyCore", call)!.ToString());
        Assert.Equal("prepare", Property(call[2]!, "Boundary")); Assert.Equal("not-ordinary-first-busy", Property(call[2]!, "Reason"));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected, Property(call[2]!, "PrepareResult"));
        var first = Assert.IsType<CpuMappedPublicationValidationRefusal>(Property(call[2]!, "PrepareRefusal"));
        Assert.Equal(default(CpuMappedPublicationValidationRefusal), first); Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
        Assert.Null(fixture.Memory.Writer); Assert.Equal(1, fixture.Memory.Preparations); Assert.Equal(1, fixture.Memory.Copies);
        Assert.Equal(0, fixture.Memory.LegacyCalls); Assert.Equal(before, fixture.Memory.Bytes());
        Assert.Equal(dirty, GuestImageWriteTracker.PeekDirty(Address)); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        // Same real Rejected result as the positive row; suppress only detached
        // attribution as the interface default for an unattributed provider.
        // A missing FIRST classification intentionally cannot borrow Current.
    }

    [Fact]
    public void ExistingOwnerBusyMarkerStillForwardsExactMetadataRefusalAfterCurrent()
    {
        ActualArchitecture(); using var fixture = new CanonicalPreparationMetadataFixture();
        var before = fixture.Memory.Bytes(); var dirty = GuestImageWriteTracker.PeekDirty(Address);
        var kind = Presenter.GetNestedType("CanonicalDirtyOwnerBusy", BindingFlags.NonPublic)!;
        var marker = Activator.CreateInstance(kind, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [Address, fixture.Image, fixture.Debt, fixture.Origin, 1, (nint)123], null)!;
        // Scalar marker forwarding control only: this constructs no actual
        // first owner/provider/native Busy observation and certifies no effect.
        Assert.True((bool)InvokeVolume("TryDeferCanonicalDirtyBusy", Address, fixture.Image, fixture.Debt,
            fixture.Origin, 1, (nint)123, marker)!);
        var retained = Field("_deferredCanonicalDirty").GetValue(null)!;
        Assert.Same(fixture.Origin, Property(retained, "Origin")); Assert.Same(fixture.Image, Property(retained, "Image"));
        Assert.Same(fixture.Debt, Property(retained, "Debt")); Assert.Equal(0, fixture.Memory.Preparations);
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.LegacyCalls);
        Assert.Equal(before, fixture.Memory.Bytes()); Assert.Equal(dirty, GuestImageWriteTracker.PeekDirty(Address));
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    // Detached scalar classification controls only. These rows never mint a
    // provider ticket, native status or even a deferred owner; positive refusal
    // retention above uses the actual managed ledger's first prepare result.
    [Theory]
    [InlineData(1, false, 1, 1L, true)] [InlineData(1, false, 1, 4294967295L, true)]
    [InlineData(1, false, 1, 0L, false)] [InlineData(1, false, 1, -1L, false)]
    [InlineData(1, false, 1, 4294967296L, false)] [InlineData(1, false, 1, 4294967297L, false)]
    [InlineData(1, false, 0, 1L, false)] [InlineData(0, false, 1, 1L, false)]
    [InlineData(2, false, 1, 1L, false)] [InlineData(1, true, 1, 1L, false)]
    public void FirstPrepareMetadataOnlyClassificationNeverBorrowsLaterOrdinaryStatus(int result, bool hasTicket, int kind, long admission, bool expected)
    {
        ActualArchitecture();
        Assert.Equal(expected, InvokeVolume("IsOrdinaryCanonicalDirtyPrepareBusy", (CpuMappedPublicationPreparationResult)result,
            hasTicket, new CpuMappedPublicationValidationRefusal((CpuMappedPublicationValidationRefusalKind)kind, 17, admission)));
    }

    [Fact]
    public void RetryFailureFormatsOnlyDetachedFirstPrepareAndRetentionPredicate()
    {
        ActualArchitecture(); using var fixture = new CanonicalPreparationMetadataFixture();
        fixture.Memory.BeginWriter(); fixture.Memory.AfterPreparation = fixture.Memory.EndWriter;
        object?[] prepare = [Address, fixture.Image, fixture.Debt, fixture.Origin, 1, (nint)123, null, null, null];
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected, InvokeVolume("PrepareCanonicalDirtyValidation", prepare));
        Set(fixture.Image, "ContentVersion", 2);
        object?[] retain = [Address, fixture.Image, fixture.Debt, fixture.Origin, 1, (nint)123, prepare[7], null, null];
        Assert.False((bool)InvokeVolume("TryRetainCanonicalDirtyRetryCore", retain)!);
        var kind = Presenter.GetNestedType("CanonicalDirtyRefusalDiagnostic", BindingFlags.NonPublic)!;
        var first = Activator.CreateInstance(kind, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [Property(retain[8]!, "Boundary"), Property(retain[8]!, "Reason"), Property(retain[8]!, "OwnerStatus"),
             CpuMappedPublicationPreparationResult.Rejected, prepare[8]], null)!;
        var preparations = fixture.Memory.Preparations; var copies = fixture.Memory.Copies; var queries = fixture.Memory.MappingQueries;
        var exactOwner = PrecommitGet(prepare[7]!, "Owner")!;
        // Make any attempted later currentness query fail. Formatter consumes
        // only detached metadata, not this altered current source state.
        fixture.Memory.ThrowOnCurrentness = true;
        try
        {
            var line = Assert.IsType<string>(InvokeVolume("FormatCanonicalDirtyRetryRefusal", exactOwner, first));
            Assert.Contains("boundary=retention-initial-owner reason=presenter-owner-session-or-version", line);
            Assert.Contains("owner_status=Stale prepare_result=Rejected prepare_refusal=AdmissionBusy", line);
            Assert.Contains("prepare_rejected_admission=1 no_later_query=true", line);
            Assert.Equal(preparations, fixture.Memory.Preparations); Assert.Equal(copies, fixture.Memory.Copies); Assert.Equal(queries, fixture.Memory.MappingQueries);
        }
        finally { fixture.Memory.ThrowOnCurrentness = false; }
    }

    [Fact]
    public void RetryRefusalBudgetIsBoundedAndIndependentOfOrdinaryRuntimeTraceBudget()
    {
        ActualArchitecture(); var retry = Field("_canonicalDirtyRetryRefusalRows"); var ordinary = Field("_finiteStorageRuntimeTraceRows");
        var savedRetry = retry.GetValue(null); var savedOrdinary = ordinary.GetValue(null);
        try
        {
            ordinary.SetValue(null, 64); retry.SetValue(null, 0);
            for (var count = 0; count < 64; count++) Assert.True((bool)InvokeVolume("TryReserveCanonicalDirtyRetryRefusalTrace")!);
            Assert.False((bool)InvokeVolume("TryReserveCanonicalDirtyRetryRefusalTrace")!);
            Assert.Equal(64, retry.GetValue(null)); Assert.Equal(64, ordinary.GetValue(null));
        }
        finally { retry.SetValue(null, savedRetry); ordinary.SetValue(null, savedOrdinary); }
    }

    // Real ledger/backing lifetime over owned pinned CLR bytes. Page size and
    // release callback here are managed metadata, never Darwin mapping/permission
    // or native completion proof. No numeric image handle reaches Metal.
    private sealed unsafe class CanonicalPrepareMemory : ICpuMemory, ICpuMappedCopySource, ICpuMappedPublicationSource, IDisposable
    {
        internal const ulong ByteCount = 4194304;
        internal readonly object Gate = new();
        internal readonly HostBackingObject Backing;
        internal readonly HostMappedCopyLedger Ledger;
        internal CpuMappedCopyObservation? First;
        internal object? Writer;
        internal Action? AfterPreparation;
        internal int Copies, Preparations, LegacyCalls, MappingQueries;
        internal bool ThrowOnCurrentness, SuppressAttribution;
        private GCHandle pin;
        private bool cleanupConsumed;
        private object? unknownWriter;
        private static readonly List<object> UnknownCleanupOwners = [];
        internal CanonicalPrepareMemory()
        {
            try
            {
                var storage = new byte[checked((int)ByteCount + 32768)]; pin = GCHandle.Alloc(storage, GCHandleType.Pinned);
                var alias = ((ulong)pin.AddrOfPinnedObject() + 16383) & ~16383UL;
                Backing = new(alias, ByteCount + 16384, 16384, _ => new(HostBackingReleaseStatus.Released)); Ledger = new(Gate, 16384);
                lock (Gate)
                {
                    Ledger.RegisterBacking(Backing);
                    var view = Ledger.PrepareView(Backing, Address, ByteCount, 0, HostPageProtection.ReadWrite);
                    Assert.NotNull(view); Assert.True(Ledger.BeforeNativeEffect(Address, ByteCount)); Ledger.PublishView(view!);
                }
                Physical(1).CopyTo(new Span<byte>((void*)alias, (int)ByteCount));
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { primary.Data["CanonicalPrepareMemoryCleanup"] = cleanup; }
                ExceptionDispatchInfo.Capture(primary).Throw(); throw;
            }
        }
        internal byte[] Bytes() => new ReadOnlySpan<byte>((void*)Backing.AliasBase, (int)ByteCount).ToArray();
        internal void BeginWriter() => Writer = typeof(HostBackingObject).GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Backing, [ByteCount, 1UL])!;
        internal void EndWriter()
        {
            var exact = Writer; Writer = null; // sole consumed cleanup, never retry an unknown return
            if (exact is null) return;
            unknownWriter = exact; // retain exact consumed receipt before the sole effect
            try { typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [exact, true]); unknownWriter = null; }
            catch { RetainUnknownCleanupOwner(); throw; }
        }
        public bool TryRead(ulong address, Span<byte> destination) { LegacyCalls++; throw new InvalidOperationException("No legacy reads."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) { LegacyCalls++; throw new InvalidOperationException("No legacy writes."); }
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong source, Span<byte> destination, out CpuMappedCopyObservation? observation)
        {
            Copies++; var result = Ledger.TryReadMappedCopy(Backing, footprint, count, source, destination, out observation);
            First ??= observation; return result;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original)
        {
            MappingQueries++; if (ThrowOnCurrentness) throw new InvalidOperationException("No later diagnostic currentness query.");
            return Ledger.IsCurrent(Backing, original);
        }
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(CpuMappedCopyObservation original,
            out CpuMappedPublicationValidation? validation) => throw new InvalidOperationException("First classification must use attributed preparation.");
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(CpuMappedCopyObservation original,
            out CpuMappedPublicationValidation? validation, out CpuMappedPublicationValidationRefusal refusal)
        {
            Preparations++; Assert.Same(First, original);
            var result = Ledger.TryPrepareMappedPublicationValidation(Backing, original, out validation, out refusal);
            if (SuppressAttribution) refusal = default; // metadata-only default-interface behavior control; never supplies success
            AfterPreparation?.Invoke(); return result;
        }
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication) => throw new InvalidOperationException("No byte publication.");
        private void RetainUnknownCleanupOwner() { lock (UnknownCleanupOwners) if (!UnknownCleanupOwners.Contains(this)) UnknownCleanupOwners.Add(this); }
        public void Dispose()
        {
            if (cleanupConsumed) return;
            cleanupConsumed = true;
            try
            {
                EndWriter();
                if (unknownWriter is not null) throw new InvalidOperationException("Exact consumed writer cleanup remains unknown.");
                if (Backing is not null && (!Backing.TryDispose(TimeSpan.Zero, out var result) || result.Status != HostBackingReleaseStatus.Released))
                    throw new InvalidOperationException("Managed backing cleanup did not return Released.");
                if (pin.IsAllocated) pin.Free();
            }
            catch { RetainUnknownCleanupOwner(); throw; } // retain pin/backing; no consumed cleanup retry
        }
    }

    private sealed class CanonicalPreparationMetadataFixture : IDisposable
    {
        private VolumeState? state;
        internal VolumeProtectedScalar Protection = null!;
        internal CanonicalPrepareMemory Memory = null!;
        internal GuestDrawTexture Snapshot = null!;
        internal object Origin = null!, Image = null!, Debt = null!;
        private object? debts;
        private readonly string[] names = ["_deferredCanonicalDirty", "_cpuWrittenGuestImageSyncRequested",
            "_takenGuestWorkSequence", "_typedCpuSyncCleanupFailure", "_textureSourceCleanupFailure"];
        private object?[]? saved;
        private bool debtInstalled, disposed;
        private IReadOnlyList<GuestDrawTexture>? ownedSnapshots;
        internal CanonicalPreparationMetadataFixture()
        {
            try
            {
                state = new(); Protection = new(); Memory = new();
                debts = Field("_finiteStorageDebts").GetValue(null)!;
                saved = names.Select(name => Field(name).GetValue(null)).ToArray();
                Field(names[0]).SetValue(null, null);
                // These mutable exact-owner guards are not scoped by the
                // inherited PresenterState. Save all before changing any;
                // constructor rollback and Dispose restore the exact values.
                Field("_takenGuestWorkSequence").SetValue(null, 0L);
                Field("_typedCpuSyncCleanupFailure").SetValue(null, null);
                Field("_textureSourceCleanupFailure").SetValue(null, null);
                // Extend only the existing mutable metadata control's declared
                // range. No syscall, protection flag or native lease is supplied.
                var range = typeof(VolumeProtectedScalar).GetField("owner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Protection)!;
                range.GetType().GetField("ByteCount")!.SetValue(range, CanonicalPrepareMemory.ByteCount);
                range.GetType().GetField("End")!.SetValue(range, Address + CanonicalPrepareMemory.ByteCount);
                ownedSnapshots = MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(Memory, Generation.Gen5), [Binding(1)], out _);
                Snapshot = Assert.Single(ownedSnapshots);
                Origin = GetOrigin(Snapshot);
                // Own the exact image BEFORE registry/attachment mutation, so
                // failure after insertion cannot leave an untracked fixture row.
                var layout = (MetalStorageCpuLayout)Property(Origin, "Layout")!;
                Image = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, nonPublic: true)!;
                foreach (var (name, value) in new (string, object)[] { ("Texture", (nint)123), ("Width", 1024u), ("Height", 1024u),
                    ("TextureType", 3u), ("Depth", 1u), ("ArrayLength", 1u), ("Format", layout.NativeFormat.Format),
                    ("Initialized", true), ("GpuWritten", true), ("ContentVersion", 1), ("StorageCpuOrigin", Origin), ("StorageCpuLayout", layout) }) Set(Image, name, value);
                Images.Add(Address, Image); MetalVideoPresenter.AttachGuestMemory(Memory);
                Debt = Activator.CreateInstance(Presenter.GetNestedType("FiniteStorageDebt", BindingFlags.NonPublic)!, nonPublic: true)!;
                PrecommitSet(Debt, "CanonicalOrigin", Origin); PrecommitSet(Debt, "CanonicalVersion", 1); PrecommitSet(Debt, "CanonicalDescriptor", Snapshot);
                debtInstalled = true; debts.GetType().GetMethod("Add")!.Invoke(debts, [Image, Debt]);
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { primary.Data["CanonicalPreparationFixtureCleanup"] = cleanup; }
                ExceptionDispatchInfo.Capture(primary).Throw(); throw;
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Exception? first = null;
            void Attempt(Action cleanup) { try { cleanup(); } catch (Exception failure) { first ??= failure; } }
            // Attempt every independent metadata restoration exactly once;
            // sentinel handles have no native retains and acquire no release.
            if (debtInstalled) Attempt(() => debts!.GetType().GetMethod("Remove", [Image.GetType()])!.Invoke(debts, [Image]));
            if (Image is not null) Attempt(() => { if (ReferenceEquals(Images[Address], Image)) Images.Remove(Address); });
            if (ownedSnapshots is not null) Attempt(() => MetalVideoPresenter.ReleasePreparedTextureSources(ownedSnapshots));
            if (saved is not null)
                for (var index = 0; index < names.Length; index++)
                { var exactIndex = index; Attempt(() => Field(names[exactIndex]).SetValue(null, saved[exactIndex])); }
            if (Memory is not null) Attempt(Memory.Dispose);
            if (Protection is not null) Attempt(Protection.Dispose);
            if (state is not null) Attempt(state.Dispose);
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
    }
}
