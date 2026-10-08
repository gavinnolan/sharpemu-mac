// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

// Actual neutral alias-copy transaction over owned pinned managed arrays.
// The fixture provider never fabricates kernel outcomes or native coverage.
public sealed class MappedPublicationTests
{
    public MappedPublicationTests(ITestOutputHelper output)
    {
        output.WriteLine($"mapped-publication architecture={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; managed-only");
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
    }
    private sealed unsafe class State : IDisposable
    {
        internal const ulong Page = 16384, Address = 0x100000000;
        internal readonly FakeProvider Host = new();
        internal readonly SharedBackingViews Views;
        internal HostBackingObject Backing => Host.Backing!;
        internal State(HostPageProtection protection = HostPageProtection.ReadWrite)
        {
            Views = new(Host, Page * 2);
            Assert.True(Views.TryMapReservedRange(Address, Page, 0, protection, out _));
            Assert.True(Views.TryMapReservedRange(Address + Page, Page, Page, HostPageProtection.ReadWrite, out _));
        }
        internal object Enroll()
        {
            Assert.Equal(CpuMappedCopyResult.Copied, Views.TryReadMappedCopy(Address, Page, Address, new byte[1], out _));
            return typeof(HostBackingObject).GetField("_managedWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Backing)!;
        }
        internal CpuMappedCopyObservation Capture(ulong address = Address, int bytes = 8)
        {
            Assert.Equal(CpuMappedCopyResult.Copied, Views.TryReadMappedCopy(Address, Page, address, new byte[bytes], out var observation));
            return Assert.IsType<CpuMappedCopyObservation>(observation);
        }
        internal CpuMappedPublication Prepare(CpuMappedCopyObservation original, ulong address, byte[] bytes)
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                Views.TryPrepareMappedPublication(original, [new(address, bytes)], out var receipt));
            return Assert.IsType<CpuMappedPublication>(receipt);
        }
        internal byte[] Bytes() => new ReadOnlySpan<byte>((void*)Backing.AliasBase, (int)(Page * 2)).ToArray();
        public void Dispose() { Host.Cleanup = true; Views.Dispose(); Host.Dispose(); }
    }

    private sealed class FakeProvider : IHostViewMemory, IHostMappedCopyMemory, IHostMappedPublicationMemory, IDisposable
    {
        private readonly byte[] storage = new byte[checked((int)(State.Page * 3))];
        private GCHandle pin;
        private readonly object gate = new();
        internal readonly HostMappedCopyLedger Ledger;
        internal HostBackingObject? Backing;
        internal bool Cleanup;
        internal FakeProvider() { pin = GCHandle.Alloc(storage, GCHandleType.Pinned); Ledger = new(gate, State.Page); }
        public ulong PageSize => State.Page;
        public ulong Granularity => PageSize;
        public bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure)
        {
            Assert.Equal(State.Page * 2, size);
            var alias = ((ulong)pin.AddrOfPinnedObject() + PageSize - 1) & ~(PageSize - 1);
            Backing = backing = new(alias, size, PageSize, _ => new(HostBackingReleaseStatus.Released));
            lock (gate) Ledger.RegisterBacking(backing);
            failure = HostViewFailure.None; return true;
        }
        public bool TryMapView(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure)
        {
            lock (gate)
            {
                var prepared = Ledger.PrepareView(backing, address, size, offset, protection);
                Assert.NotNull(prepared); Assert.True(Ledger.BeforeNativeEffect(address, size)); Ledger.PublishView(prepared);
            }
            failure = HostViewFailure.None; return true;
        }
        public bool UnmapView(ulong address, ulong size) { lock (gate) return Cleanup || Ledger.BeforeNativeEffect(address, size); }
        public CpuMappedCopyResult TryReadMappedCopy(HostBackingObject backing, ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation) =>
            Ledger.TryReadMappedCopy(backing, footprintAddress, footprintByteCount, sourceAddress, destination, out observation);
        public bool IsMappedCopyObservationCurrent(HostBackingObject backing, CpuMappedCopyObservation observation) => Ledger.IsCurrent(backing, observation);
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(HostBackingObject backing,
            CpuMappedCopyObservation original, IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication) =>
            Ledger.TryPrepareMappedPublication(backing, original, runs, out publication);
        internal bool Effect(ulong address, ulong size) { lock (gate) return Ledger.BeforeNativeEffect(address, size); }
        internal void Unknown() { lock (gate) Ledger.QuarantineUnknownAlias(); }
        public ulong ReserveHole(ulong address, ulong size) => throw new NotSupportedException();
        public ulong FindUnmappedRange(ulong address, ulong size, ulong searchEnd) => throw new NotSupportedException();
        public bool SplitHole(ulong address, ulong size) => throw new NotSupportedException();
        public bool JoinHoles(ulong address, ulong size) => throw new NotSupportedException();
        public bool FreeHole(ulong address, ulong size) => throw new NotSupportedException();
        public bool CommitPrivate(ulong address, ulong size, HostPageProtection protection) => throw new NotSupportedException();
        public bool ReleasePrivate(ulong address, ulong size) => throw new NotSupportedException();
        public bool ChangeAccess(ulong address, ulong size, HostPageProtection protection) => throw new NotSupportedException();
        public bool FreeOwnedRange(ulong address, ulong size) => throw new NotSupportedException();
        public void Dispose() { Backing?.Dispose(); if (pin.IsAllocated) pin.Free(); }
    }


    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(8)]
    public void ActualDetachedScatterPreservesEveryNeighbor(int count)
    {
        using var state = new State();
        Assert.True(state.Views.Clear(0, State.Page * 2));
        var original = state.Capture(State.Address + 31, count);
        byte[] data = Enumerable.Range(1, count).Select(i => (byte)(i * 17)).ToArray();
        var expected = (byte[])data.Clone();
        using var receipt = state.Prepare(original, State.Address + 31, data);
        Array.Fill(data, (byte)255); // no caller-memory retention
        Assert.Equal(CpuMappedPublicationResult.Applied, receipt.TryCommit());
        var all = state.Bytes();
        for (var i = 0; i < all.Length; i++) Assert.Equal(i >= 31 && i < 31 + count ? expected[i - 31] : (byte)0, all[i]);
        Assert.Equal(CpuMappedPublicationResult.Rejected, receipt.TryCommit());
    }

    [Fact]
    public void HeldReservationRejectsMutationAndAllParticipatingDestinationsBeforeBytes()
    {
        using var state = new State(); var original = state.Capture(); var before = state.Bytes();
        using var receipt = state.Prepare(original, State.Address, [41]);
        Assert.False(state.Host.Effect(State.Address, 1));
        Assert.True(state.Views.IsMappedCopyObservationCurrent(original));
        Assert.False(state.Views.Clear(0, 1));
        Assert.False(state.Views.TryWriteBacking(State.Address, new byte[] { 99 }));
        Assert.False(state.Views.TryCopyBacking(State.Address, State.Address + 1, 1));
        Assert.Equal(before, state.Bytes());
        Assert.Equal(CpuMappedPublicationResult.Applied, receipt.TryCommit());
    }

    [Fact]
    public void CancellationConsumesOnceAndRestoresWriterAndMappingAdmission()
    {
        using var state = new State(); var original = state.Capture(); var before = state.Bytes();
        var receipt = state.Prepare(original, State.Address, [41]);
        receipt.Dispose(); receipt.Dispose();
        Assert.Equal(CpuMappedPublicationResult.Rejected, receipt.TryCommit()); Assert.Equal(before, state.Bytes());
        Assert.True(state.Views.TryWriteBacking(State.Address, new byte[] { 99 }));
        Assert.True(state.Host.Effect(State.Address, 1));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void CompletedOrdinaryWriterSinceOriginalCopyVetoesPublication(int kind)
    {
        using var state = new State(); var original = state.Capture();
        Assert.True(kind switch
        {
            0 => state.Views.Clear(0, 1),
            1 => state.Views.TryWriteBacking(State.Address, new byte[] { 13 }),
            _ => state.Views.TryCopyBacking(State.Address, State.Address + 1, 1),
        });
        Assert.True(state.Views.IsMappedCopyObservationCurrent(original)); // mapping alone is not writer history
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 7 })], out var receipt));
        Assert.Null(receipt);
    }

    [Fact]
    public void RetiredSameAddressAndUnknownOutcomesCannotPrepare()
    {
        using var state = new State(); var original = state.Capture();
        Assert.True(state.Views.Unmap(State.Address, State.Page, out var preserved)); Assert.True(preserved);
        Assert.True(state.Views.TryMapReservedRange(State.Address, State.Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 7 })], out _));
        var replaced = state.Capture(); state.Host.Unknown();
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(replaced, [new(State.Address, new byte[] { 7 })], out _));
    }

    [Fact]
    public void ForeignBackingAndInvalidRunBoundsRejectWithoutBytes()
    {
        using var first = new State(); using var other = new State(); var original = first.Capture();
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            other.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 1 })], out _));
        foreach (var run in new CpuMappedWriteRun[]
        {
            new(State.Address, Array.Empty<byte>()), new(State.Address, new byte[9]),
            new(State.Address + 8, new byte[1]), new(ulong.MaxValue, new byte[2]),
        }) Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            first.Views.TryPrepareMappedPublication(original, [run], out _));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            first.Views.TryPrepareMappedPublication(original,
                [new(State.Address, new byte[] { 1, 2 }), new(State.Address + 1, new byte[] { 3 })], out _));
    }

    [Fact]
    public async Task ConcurrentCommitCopiesExactlyOnce()
    {
        using var state = new State(); using var receipt = state.Prepare(state.Capture(), State.Address, [57]);
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(receipt.TryCommit)).ToArray();
        await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempts.Count(t => t.Result == CpuMappedPublicationResult.Applied));
        Assert.Equal(15, attempts.Count(t => t.Result == CpuMappedPublicationResult.Rejected));
        Assert.Equal(57, state.Bytes()[0]);
    }

    [Fact]
    public void BackingCloseBeforeCommitRejectsAndReleasesPin()
    {
        using var state = new State(); var before = state.Bytes(); using var receipt = state.Prepare(state.Capture(), State.Address, [57]);
        state.Backing.CloseAccess();
        Assert.Equal(CpuMappedPublicationResult.Rejected, receipt.TryCommit()); Assert.Equal(before, state.Bytes());
        Assert.Equal(0, state.Backing.ActiveAccesses);
    }
    [Fact]
    public void ReadableButNotWritableOriginalViewCannotPublish()
    {
        using var state = new State(HostPageProtection.ReadOnly);
        var original = state.Capture();
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 13 })], out _));
    }

    [Fact]
    public void ExactOwnContinuityAllowsRepeatButOldOriginalAndExternalWriteReject()
    {
        using var state = new State(); var original = state.Capture();
        using var first = state.Prepare(original, State.Address, [13]);
        Assert.Null(first.SuccessfulObservation);
        Assert.Equal(CpuMappedPublicationResult.Applied, first.TryCommit());
        var own = Assert.IsType<CpuMappedCopyObservation>(first.SuccessfulObservation);
        Assert.Equal(original.FootprintAddress, own.FootprintAddress);
        Assert.Equal(original.SourceAddress, own.SourceAddress);
        Assert.Equal(original.Segments, own.Segments);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 14 })], out _));
        using var second = state.Prepare(own, State.Address, [27]);
        Assert.Equal(CpuMappedPublicationResult.Applied, second.TryCommit());
        var next = Assert.IsType<CpuMappedCopyObservation>(second.SuccessfulObservation);
        Assert.Equal(27, state.Bytes()[0]);
        Assert.True(state.Views.TryWriteBacking(State.Address, new byte[] { 91 }));
        Assert.True(state.Views.IsMappedCopyObservationCurrent(next));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(next, [new(State.Address, new byte[] { 14 })], out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void CompletedDisjointPhysicalWriterPermitsExactOwnContinuationWithoutRecapture(int kind)
    {
        using var state = new State(); var original = state.Capture();
        using var first = state.Prepare(original, State.Address, [13]);
        Assert.Equal(CpuMappedPublicationResult.Applied, first.TryCommit());
        var own = Assert.IsType<CpuMappedCopyObservation>(first.SuccessfulObservation);
        // A participating ordinary EOP-like label destination outside the texel.
        Assert.True(kind switch
        {
            0 => state.Views.Clear(32, 4),
            1 => state.Views.TryWriteBacking(State.Address + 32, new byte[] { 21, 22, 23, 24 }),
            _ => state.Views.TryCopyBacking(State.Address + 32, State.Address, 1),
        });
        Assert.Same(own, first.SuccessfulObservation); // no replacement/recapture of the original baseline
        using var second = state.Prepare(own, State.Address, [27]);
        Assert.Equal(CpuMappedPublicationResult.Applied, second.TryCommit());
        Assert.Equal(27, state.Bytes()[0]);
        Assert.Equal(original.Segments, second.SuccessfulObservation!.Segments);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 31 })], out _));
    }

    [Theory]
    [InlineData(7, false)] [InlineData(8, true)]
    public void ExactPhysicalConflictUsesHalfOpenTexelBounds(int offset, bool expected)
    {
        using var state = new State(); var original = state.Capture();
        Assert.True(state.Views.TryWriteBacking(State.Address + (ulong)offset, new byte[] { 91 }));
        var result = state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[8])], out var receipt);
        using (receipt)
        {
            Assert.Equal(expected ? CpuMappedPublicationPreparationResult.Prepared : CpuMappedPublicationPreparationResult.Rejected, result);
            if (expected) Assert.Equal(CpuMappedPublicationResult.Applied, receipt!.TryCommit());
        }
    }

    [Fact]
    public void BoundedHistoryEvictionRejectsOldBaselineButExactOwnContinuityCrossesRingWrap()
    {
        using var state = new State(); var old = state.Capture(); var current = old;
        for (var iteration = 0; iteration < 600; iteration++)
        {
            using var receipt = state.Prepare(current, State.Address, [(byte)iteration]);
            Assert.Equal(CpuMappedPublicationResult.Applied, receipt.TryCommit());
            current = Assert.IsType<CpuMappedCopyObservation>(receipt.SuccessfulObservation);
            Assert.True(state.Views.TryWriteBacking(State.Address + 32, new byte[] { (byte)iteration }));
        }
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(old, [new(State.Address, new byte[] { 14 })], out _));
        using var last = state.Prepare(current, State.Address, [57]);
        Assert.Equal(CpuMappedPublicationResult.Applied, last.TryCommit());
        Assert.Equal(57, state.Bytes()[0]);
    }

    [Fact]
    public void UnknownCompletedWriterCannotBeRelabeledDisjoint()
    {
        using var state = new State(); var original = state.Capture(); var writers = state.Enroll();
        var begin = writers.GetType().GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = writers.GetType().GetMethod("End", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var token = begin.Invoke(writers, [32UL, 4UL])!;
        Assert.True((bool)end.Invoke(writers, [token, false])!);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 13 })], out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(32)]
    public void OtherVirtualAliasConflictsByCanonicalPhysicalOffset(int offset)
    {
        using var state = new State();
        Assert.True(state.Views.Unmap(State.Address + State.Page, State.Page, out var preserved)); Assert.True(preserved);
        Assert.True(state.Views.TryMapReservedRange(State.Address + State.Page, State.Page, 0, HostPageProtection.ReadWrite, out _));
        var original = state.Capture();
        Assert.True(state.Views.TryWriteBacking(State.Address + State.Page + (ulong)offset, new byte[] { 99 }));
        var result = state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 13 })], out var receipt);
        using (receipt)
        {
            Assert.Equal(offset == 0 ? CpuMappedPublicationPreparationResult.Rejected : CpuMappedPublicationPreparationResult.Prepared, result);
            if (offset != 0) Assert.Equal(CpuMappedPublicationResult.Applied, receipt!.TryCommit());
        }
    }

    [Fact]
    public void CrossViewStagedCopyRecordsActualDestinationsAndPreservesMemmoveBytes()
    {
        using var state = new State();
        var seed = Enumerable.Range(0, (int)(State.Page * 2)).Select(i => (byte)(i * 31)).ToArray();
        Assert.True(state.Views.TryWriteBacking(State.Address, seed));
        var original = state.Capture();
        var expected = (byte[])seed.Clone();
        Array.Copy(expected, (int)State.Page - 4, expected, (int)State.Page - 2, 8);
        Assert.True(state.Views.TryCopyBacking(State.Address + State.Page - 2, State.Address + State.Page - 4, 8));
        Assert.Equal(expected, state.Bytes());
        using var receipt = state.Prepare(original, State.Address, [57]);
        Assert.Equal(CpuMappedPublicationResult.Applied, receipt.TryCommit());
        expected[0] = 57; Assert.Equal(expected, state.Bytes());
    }

    [Fact]
    public void FailedUnresolvedOperationEnvelopeNeverBecomesDisjointHistory()
    {
        using var state = new State(); var original = state.Capture();
        Assert.False(state.Views.TryWriteBacking(State.Address + State.Page * 3, new byte[] { 99 }));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 13 })], out _));
    }

    [Fact]
    public void DisjointHistoryOlderThanBoundedWindowDeclinesWithoutRecapture()
    {
        using var state = new State(); var original = state.Capture();
        for (var iteration = 0; iteration < 257; iteration++)
            Assert.True(state.Views.TryWriteBacking(State.Address + 32, new byte[] { (byte)iteration }));
        Assert.True(state.Views.IsMappedCopyObservationCurrent(original));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 13 })], out _));
    }

    [Fact]
    public void DelayedCompletionCannotOverwriteNewerWrappedHistory()
    {
        using var state = new State(); var original = state.Capture(); var writers = state.Enroll();
        var begin = writers.GetType().GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = writers.GetType().GetMethod("End", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var delayed = begin.Invoke(writers, [32UL, 1UL])!;
        try
        {
            for (var iteration = 0; iteration < 600; iteration++)
            {
                var token = begin.Invoke(writers, [64UL, 1UL])!;
                Assert.True((bool)end.Invoke(writers, [token, true])!);
            }
        }
        finally { Assert.True((bool)end.Invoke(writers, [delayed, true])!); }
        Assert.Equal(1, writers.GetType().GetField("coverageIncomplete", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writers));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 13 })], out _));
        // Scalar delayed completion only, no forged bytes/native result.
    }

    [Fact]
    public async Task ConcurrentCompletedDisjointWritesPreserveOriginalRangeHistory()
    {
        using var state = new State(); var original = state.Capture();
        var writes = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            state.Views.TryWriteBacking(State.Address + 32 + (ulong)index, new byte[] { (byte)index }))).ToArray();
        Assert.All(await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(5)), succeeded => Assert.True(succeeded));
        using var receipt = state.Prepare(original, State.Address, [57]);
        Assert.Equal(CpuMappedPublicationResult.Applied, receipt.TryCommit());
        Assert.Equal(57, state.Bytes()[0]);
        for (var index = 0; index < 32; index++) Assert.Equal((byte)index, state.Bytes()[32 + index]);
    }

    [Fact]
    public void CancelledOrRejectedCommitCannotMintOwnContinuity()
    {
        using var state = new State(); var original = state.Capture();
        var cancelled = state.Prepare(original, State.Address, [13]);
        cancelled.Dispose(); Assert.Null(cancelled.SuccessfulObservation);
        Assert.Equal(CpuMappedPublicationResult.Rejected, cancelled.TryCommit()); Assert.Null(cancelled.SuccessfulObservation);
        using var rejected = state.Prepare(original, State.Address, [27]);
        state.Backing.CloseAccess();
        Assert.Equal(CpuMappedPublicationResult.Rejected, rejected.TryCommit()); Assert.Null(rejected.SuccessfulObservation);
    }

    [Fact]
    public void ParticipatingWriterDuringOriginalCopyPermanentlyDeclinesPublicationHistory()
    {
        using var state = new State();
        var hook = typeof(HostMappedCopyLedger).GetField("afterCaptureForControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            hook.SetValue(state.Host.Ledger, (Action)(() => Assert.True(state.Views.TryWriteBacking(State.Address, new byte[] { 79 }))));
            var original = state.Capture();
            Assert.True(state.Views.IsMappedCopyObservationCurrent(original));
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 14 })], out _));
        }
        finally { hook.SetValue(state.Host.Ledger, null); }
    }

    [Fact]
    public void HeldWriterAndSaturatedBookkeepingCannotAcquirePublication()
    {
        using var state = new State(); var original = state.Capture(); var writers = state.Enroll();
        var begin = writers.GetType().GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = writers.GetType().GetMethod("End", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tokens = new List<object>();
        try
        {
            tokens.Add(begin.Invoke(writers, [0ul, 1ul])!);
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 14 })], out _));
            for (var i = 1; i < 128; i++) tokens.Add(begin.Invoke(writers, [(ulong)i, 1ul])!);
            Assert.True(state.Views.TryWriteBacking(State.Address, new byte[] { 81 })); // saturation preserves legacy write
        }
        finally { foreach (var token in tokens) Assert.True((bool)end.Invoke(writers, [token, true])!); }
        var after = state.Capture();
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(after, [new(State.Address, new byte[] { 14 })], out _));
    }

    [Fact]
    public void EmptyOperationsRetainLegacyValidationDuringPublication()
    {
        using var state = new State(); using var receipt = state.Prepare(state.Capture(), State.Address, [13]);
        Assert.False(state.Views.Clear(0, 0));
        Assert.False(state.Views.TryWriteBacking(State.Address, ReadOnlySpan<byte>.Empty));
        Assert.False(state.Views.TryCopyBacking(State.Address, State.Address, 0));
        Assert.Equal(CpuMappedPublicationResult.Applied, receipt.TryCommit());
    }

}
