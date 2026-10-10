// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static object RegularOriginal() => ((IDictionary)Field("_regularColorOriginals").GetValue(null)!)[Address]!;
    private static object RegularField(object owner, string name) => PrecommitGet(owner, name)!;

    [Fact]
    public void RegularColorAbandonedPreparationReusesExactFirstLedgerMappingWithoutRecapture()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var first = fixture.Capture(); var original = RegularOriginal();
        var observation = RegularField(original, "Observation");
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(first);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(first);
        Assert.Equal(0, RegularField(original, "Preparations"));
        var second = fixture.Capture(); Assert.NotSame(first, second);
        Assert.Same(original, RegularOriginal()); Assert.Same(observation, RegularField(original, "Observation"));
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads);
        Assert.Equal(1, RegularField(original, "Preparations")); Assert.Equal(0, RegularField(original, "Queued"));
        Assert.Null(PrecommitGet(original, "Native")); Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegularColorActualQueueOwnsMappingAfterPreparationUntilPendingOrTakenCancellation(bool taken)
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var target = fixture.Capture(); var original = RegularOriginal(); var observation = RegularField(original, "Observation");
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireRegularColorTargetLocked", target));
        SubmitBorrowColor(target);
        Assert.True((long)Field("_enqueuedGuestWorkSequence").GetValue(null)! > 0);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        Assert.Equal(0, RegularField(original, "Preparations")); Assert.Equal(1, RegularField(original, "Queued"));
        Assert.Same(original, InvokeVolume("RequireRegularColorTargetLocked", target));
        if (taken) _ = TakeVolumeWork();
        lock (Field("_gate").GetValue(null)!) InvokeVolume("CancelQueuedNativeColorLocked", (object?)null);
        Assert.Equal(0, RegularField(original, "Queued")); Assert.Same(observation, RegularField(original, "Observation"));
        Assert.Empty((IDictionary)Field("_regularColorQueued").GetValue(null)!);
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireRegularColorTargetLocked", target));
        Assert.Null(PrecommitGet(original, "Native")); Assert.Equal(1, fixture.Memory.Copies);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null)); Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Fact]
    public void RegularColorActualAdmissionConsumesOnePreparationAndRejectsDuplicateWithoutNewQueueOwnership()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var target = fixture.Capture(); SubmitBorrowColor(target); var original = RegularOriginal();
        var sequence = Field("_enqueuedGuestWorkSequence").GetValue(null);
        Assert.Throws<NotSupportedException>(() => SubmitBorrowColor(target));
        Assert.Equal(sequence, Field("_enqueuedGuestWorkSequence").GetValue(null));
        Assert.Equal(1, RegularField(original, "Queued")); Assert.Equal(1, fixture.Memory.Copies);
        Assert.Single(((IDictionary)Field("_regularColorQueued").GetValue(null)!).Values.Cast<object>());
        Assert.Null(PrecommitGet(original, "Native")); Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Theory]
    [InlineData("mapping")] [InlineData("session")] [InlineData("reader")]
    [InlineData("raw-layout")] [InlineData("unknown-owner")]
    public void RegularColorLaterPreparationCannotReplaceFirstMappingDomainLayoutOrUnknownNative(string variant)
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var first = fixture.Capture(); var original = RegularOriginal();
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(first);
        if (variant == "mapping") fixture.Memory.Map();
        if (variant == "session") Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentNativeDepthFaceSession + 1);
        if (variant == "reader") Field("_guestMemory").SetValue(null, new Memory([1]));
        if (variant == "unknown-owner")
        {
            // Failure metadata only; no selector, native retain or handle.
            PrecommitSet(original, "Unknown", true);
        }
        var words = RegularColorWords(Address);
        if (variant == "raw-layout") words = words with { Attrib2 = (words.Attrib2 & ~0x3FFFu) | 15u };
        Assert.Throws<NotSupportedException>(() => fixture.Capture(words));
        Assert.Same(original, RegularOriginal()); Assert.Equal(1, fixture.Memory.Copies);
        Assert.Null(PrecommitGet(original, "Native")); Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Fact]
    public void RegularColorExistingUntrackedImageRefusesBeforeFirstProviderProbe()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        Images[Address] = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, true);
        Assert.Throws<NotSupportedException>(() => fixture.Capture());
        Assert.Equal(0, fixture.Memory.Copies); Assert.Empty((IDictionary)Field("_regularColorOriginals").GetValue(null)!);
        Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Fact]
    public void RegularColorConsumedPreparationCleanupFailureRetainsExactOwnerAndNeverRetries()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var target = fixture.Capture(); var original = RegularOriginal();
        var preparations = Field("_regularColorPreparations").GetValue(null)!;
        var exact = Assert.Single(((IEnumerable)preparations).Cast<object>());
        // Deliberately corrupt only managed bookkeeping to reach refusal. The
        // actual ledger copy grants mapping only and holds no native/read lease.
        PrecommitSet(original, "Preparations", 0);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.ReleasePreparedNativeColorTarget(target));
        Assert.Same(exact, RegularField(original, "UnknownPreparation")); Assert.True((bool)RegularField(exact, "Released"));
        Assert.True((bool)RegularField(original, "Unknown")); Assert.True((bool)RegularField(exact, "CleanupUnknown"));
        var failure = Field("_regularColorOriginalFailure").GetValue(null); Assert.NotNull(failure);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); // consumed: no second cleanup
        Assert.Same(failure, Field("_regularColorOriginalFailure").GetValue(null));
        Assert.Same(exact, RegularField(original, "UnknownPreparation")); Assert.Equal(1, fixture.Memory.Copies);
        Assert.Equal(0, fixture.Memory.Backing.ActiveAccesses); Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Fact]
    public void RegularColorOldAcceptedTargetCannotBorrowAnotherTargetQueueCharge()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var oldTarget = fixture.Capture(); SubmitBorrowColor(oldTarget);
        lock (Field("_gate").GetValue(null)!) InvokeVolume("CancelQueuedNativeColorLocked", (object?)null);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(oldTarget);
        var newTarget = fixture.Capture(); SubmitBorrowColor(newTarget);
        Assert.Same(RegularOriginal(), InvokeVolume("RequireRegularColorTargetLocked", newTarget));
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireRegularColorTargetLocked", oldTarget));
        Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Equal(1, fixture.Memory.Copies);
        Assert.Equal(1, RegularField(RegularOriginal(), "Queued")); Assert.Null(PrecommitGet(RegularOriginal(), "Native"));
    }

    [Theory]
    [InlineData("active")] [InlineData("released")] [InlineData("retired")]
    public void RegularColorFeatureLossRefusesKnownFirstOwnerAtCaptureAdmissionAndPreNativeGuard(string state)
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var target = fixture.Capture(); var original = RegularOriginal();
        if (state == "active") SubmitBorrowColor(target);
        else MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        if (state == "retired") PrecommitSet(original, "Retired", true); // metadata only, no native owner
        var sequence = Field("_enqueuedGuestWorkSequence").GetValue(null);
        MetalVideoPresenter.NativeColorBorrowEnabled = false;
        Assert.Throws<NotSupportedException>(() => fixture.Capture());
        Assert.Throws<NotSupportedException>(() => SubmitBorrowColor(target));
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireRegularColorTargetLocked", target));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Equal(sequence, Field("_enqueuedGuestWorkSequence").GetValue(null));
        Assert.Same(original, RegularOriginal()); Assert.Null(PrecommitGet(original, "Native"));
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads); Assert.Equal(fixture.Before, fixture.Memory.Expected);
        // The real pre-native helper fails before the generic construction
        // route. No native entry is invoked with a sentinel/zero device.
    }

    [Fact]
    public void RegularColorAcceptedMappingCannotCertifyAnUnassociatedNativeImage()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var target = fixture.Capture(); SubmitBorrowColor(target); var original = RegularOriginal();
        var image = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, true)!;
        var layout = ((MetalRegularColorPhysicalPlacement.Observation)RegularField(original, "Observation")).Placement;
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireRegularColorNativeImageLocked", original, image, layout.NativeFormat));
        Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Null(PrecommitGet(original, "Native"));
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads); Assert.Equal(fixture.Before, fixture.Memory.Expected);
        // No caller-generated field values create a native association.
    }

    [Theory]
    [InlineData("mips")] [InlineData("missing-registers")] [InlineData("projected-base")]
    public void RegularColorKnownAddressClassificationLossRefusesGenericPreNativeRoute(string variant)
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var originalTarget = fixture.Capture(); var original = RegularOriginal(); SubmitBorrowColor(originalTarget);
        var words = RegularColorWords(Address) with { Attrib2 = RegularColorWords(Address).Attrib2 | (1u << 28) };
        if (variant is "mips" or "projected-base") Assert.Throws<NotSupportedException>(() => fixture.Capture(words));
        var changed = originalTarget with { Registers = variant == "missing-registers" ? null : words };
        if (variant == "projected-base") changed = changed with { Address = Address + 0xB0000 };
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireRegularColorTargetLocked", changed));
        Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Same(original, RegularOriginal());
        Assert.Equal(1, fixture.Memory.Copies); Assert.Null(PrecommitGet(original, "Native")); Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Fact]
    public void RegularColorRejectedSelectedFixturePreflightPreservesExactForeignQueueMetadata()
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var queued = (IDictionary)Field("_nativeColorQueued").GetValue(null)!;
        var kind = Presenter.GetNestedType("NativeColorQueuedOwner", BindingFlags.NonPublic)!;
        var originals = Array.CreateInstance(Presenter.GetNestedType("NativeColorOriginal", BindingFlags.NonPublic)!, 0);
        var dependencies = Array.CreateInstance(Presenter.GetNestedType("NativeColorProducer", BindingFlags.NonPublic)!, 0);
        var foreign = Activator.CreateInstance(kind, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            null, [originals, dependencies], null)!;
        var key = new object(); var memory = Field("_guestMemory").GetValue(null);
        var failure = Field("_regularColorOriginalFailure").GetValue(null); var borrow = MetalVideoPresenter.NativeColorBorrowEnabled;
        queued.Add(key, foreign);
        try
        {
            // An actual typed but empty metadata cohort supplies no native
            // retain/watch/dependency. It belongs to this outer fixture only.
            Assert.ThrowsAny<Exception>(() => new RegularColorOwnershipFixture());
            Assert.Same(foreign, queued[key]); Assert.Single(queued.Values.Cast<object>());
            Assert.Same(memory, Field("_guestMemory").GetValue(null));
            Assert.Same(failure, Field("_regularColorOriginalFailure").GetValue(null));
            Assert.Equal(borrow, MetalVideoPresenter.NativeColorBorrowEnabled);
            Assert.Empty((IDictionary)Field("_regularColorOriginals").GetValue(null)!);
        }
        finally
        {
            if (ReferenceEquals(queued[key], foreign)) queued.Remove(key); // exact owned metadata only
        }
    }

    [Fact]
    public void RegularColorRejectedFixturePreflightCannotCancelOrClearPreexistingOriginals()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        var target = fixture.Capture(); SubmitBorrowColor(target); var original = RegularOriginal();
        var failure = new InvalidOperationException("retained preexisting failure metadata");
        Field("_regularColorOriginalFailure").SetValue(null, failure);
        var sequence = Field("_enqueuedGuestWorkSequence").GetValue(null);
        Assert.ThrowsAny<Exception>(() => new RegularColorOwnershipFixture());
        Assert.Same(original, RegularOriginal()); Assert.Same(failure, Field("_regularColorOriginalFailure").GetValue(null));
        Assert.Equal(1, RegularField(original, "Preparations")); Assert.Equal(1, RegularField(original, "Queued"));
        Assert.Single(((IDictionary)Field("_regularColorQueued").GetValue(null)!).Values.Cast<object>());
        Assert.Equal(sequence, Field("_enqueuedGuestWorkSequence").GetValue(null)); Assert.Equal(1, fixture.Memory.Copies);
        Assert.Equal(fixture.Before, fixture.Memory.Expected);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegularColorAssociatedCpuImageEffectRefusesUnqualifiedAssociationWithoutNativeOwner(bool replace)
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        _ = fixture.Capture(); var original = RegularOriginal();
        var image = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, true)!;
        var kind = Presenter.GetNestedType("RegularColorNativeOwner", BindingFlags.NonPublic)!;
        var association = Activator.CreateInstance(kind, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            null, [original, image], null)!;
        var table = Field("_regularColorImages").GetValue(null)!;
        table.GetType().GetMethod("Add")!.Invoke(table, [image, association]);
        try
        {
            // An unentered metadata-only association pins refusal before either
            // clear or replacement. It grants no handle/retain/positive authority.
            var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireRegularColorCpuImageEffectLocked", image, replace));
            Assert.IsType<NotSupportedException>(failure.InnerException);
            Assert.False((bool)RegularField(association, "CreationEntered")); Assert.False((bool)RegularField(association, "Installed"));
            Assert.Equal((nint)0, PrecommitGet(image, "Texture")); Assert.Null(PrecommitGet(original, "Native"));
            Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(fixture.Before, fixture.Memory.Expected);
        }
        finally { table.GetType().GetMethod("Remove", [image.GetType()])!.Invoke(table, [image]); }
        InvokeVolume("RequireRegularColorCpuImageEffectLocked", image, replace); // generic nonassociated metadata unchanged
    }

    [Fact]
    public void RegularColorUnsupportedPendingSeedRefusesBeforeNativeOwnerAndRetainsExactSeedArray()
    {
        ActualArchitecture(); using var fixture = new RegularColorOwnershipFixture();
        _ = fixture.Capture(); var original = RegularOriginal();
        var seeds = (IDictionary)Field("_pendingGuestImageInitialData").GetValue(null)!;
        var previous = seeds[Address]; var bytes = new byte[] { 1, 2, 3, 4 }; seeds[Address] = bytes;
        try
        {
            var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireNoRegularColorPendingSeedLocked", original));
            Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Same(bytes, seeds[Address]);
            Assert.Null(PrecommitGet(original, "Native")); Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(fixture.Before, fixture.Memory.Expected);
        }
        finally { if (previous is null) seeds.Remove(Address); else seeds[Address] = previous; }
    }

    private sealed class RegularColorOwnershipFixture : IDisposable
    {
        private BorrowColorState? state;
        internal DisplayLedgerMemory Memory = null!;
        internal byte[] Before = [];
        private readonly List<GuestRenderTarget> targets = [];
        private object? previousFailure;
        private bool consumed;
        private bool fixtureOwned;
        private static readonly List<RegularColorOwnershipFixture> UnknownOwners = [];
        internal RegularColorOwnershipFixture()
        {
            try
            {
                Assert.Empty((IDictionary)Field("_regularColorOriginals").GetValue(null)!);
                Assert.Empty((IDictionary)Field("_regularColorQueued").GetValue(null)!);
                Assert.Empty((IEnumerable)Field("_regularColorPreparations").GetValue(null)!);
                foreach (var name in new[] { "_nativeColorLatest", "_nativeColorQueued", "_nativeColorBatches" })
                    Assert.Empty((IDictionary)Field(name).GetValue(null)!);
                Assert.Empty((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!);
                previousFailure = Field("_regularColorOriginalFailure").GetValue(null);
                fixtureOwned = true; // after all foreign/preexisting guards and saved metadata
                state = new(); Memory = new(); Before = Memory.Expected;
                MetalVideoPresenter.AttachGuestMemory(Memory);
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { primary.Data["RegularColorFixtureCleanup"] = cleanup; }
                ExceptionDispatchInfo.Capture(primary).Throw(); throw;
            }
        }
        internal GuestRenderTarget Capture(ColorTargetWords? supplied = null)
        {
            var words = supplied ?? RegularColorWords(Address);
            var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
            var target = RegularColorTarget(words, resolution);
            targets.Add(target); // own BEFORE an actual capture can register it
            return MetalVideoPresenter.CaptureRegularColorTarget(Memory, resolution, words, target);
        }
        public void Dispose()
        {
            if (consumed) return; consumed = true;
            if (!fixtureOwned) return; // preflight failure has no global cleanup ownership
            Exception? first = null;
            void Attempt(Action action) { try { action(); } catch (Exception failure) { first ??= failure; } }
            Attempt(() => { lock (Field("_gate").GetValue(null)!) InvokeVolume("CancelQueuedRegularColorLocked", (object?)null); });
            foreach (var target in targets) Attempt(() => MetalVideoPresenter.ReleasePreparedNativeColorTarget(target));
            // These controls have no native construction: reject instead of
            // clearing a real entered owner. Cleanup-unknown here is explicitly
            // synthetic bookkeeping over mapping-only, unleased CLR memory.
            var originals = (IDictionary)Field("_regularColorOriginals").GetValue(null)!;
            var preparations = Field("_regularColorPreparations").GetValue(null)!;
            // An unexpected native owner is never treated as fixture metadata.
            // Preserve this exact fixture/pin/state and do not force teardown.
            if (originals.Values.Cast<object>().Any(original => PrecommitGet(original, "Native") is not null))
            {
                lock (UnknownOwners) UnknownOwners.Add(this);
                throw new InvalidOperationException("Regular color metadata fixture unexpectedly acquired a native owner; quarantine retained.", first);
            }
            Attempt(() =>
            {
                Assert.Equal(0, Memory?.Backing.ActiveAccesses ?? 0);
                originals.Clear(); preparations.GetType().GetMethod("Clear")!.Invoke(preparations, []);
                Images.Remove(Address); Field("_regularColorOriginalFailure").SetValue(null, previousFailure);
            });
            if (Memory is not null) Attempt(Memory.Dispose);
            if (state is not null) Attempt(state.Dispose);
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
    }
}
