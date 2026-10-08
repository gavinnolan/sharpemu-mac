// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

// Pinned managed bytes and synthetic ledger metadata, no kernel/native receipt.
public sealed unsafe class MappedCopyReadWatchTests
{
    public MappedCopyReadWatchTests(ITestOutputHelper output)
    {
        var actual = RuntimeInformation.ProcessArchitecture.ToString();
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, actual);
        output.WriteLine($"mapped-read-watch actualCLR={actual}; framework={RuntimeInformation.FrameworkDescription}; managed-only");
    }
    private sealed class State : IDisposable
    {
        internal const ulong Page = 16384, Address = 0x100000000;
        internal readonly object Gate = new();
        internal readonly HostMappedCopyLedger Ledger;
        internal readonly HostBackingObject Backing;
        private readonly byte[] bytes = new byte[checked((int)(Page * 3))];
        private GCHandle pin;
        internal State()
        {
            pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var alias = ((ulong)pin.AddrOfPinnedObject() + Page - 1) & ~(Page - 1);
            Backing = new(alias, Page * 2, Page, _ => new(HostBackingReleaseStatus.Released));
            Ledger = new(Gate, Page);
            lock (Gate) Ledger.RegisterBacking(Backing);
            Map(Address, 0); Map(Address + Page, Page);
        }
        internal void Map(ulong address, ulong offset)
        {
            lock (Gate)
            {
                var prepared = Ledger.PrepareView(Backing, address, Page, offset, HostPageProtection.ReadWrite);
                Assert.NotNull(prepared); Assert.True(Ledger.BeforeNativeEffect(address, Page)); Ledger.PublishView(prepared);
            }
        }
        internal CpuMappedCopyObservation Capture(ulong source = Address + 32, int count = 16)
        {
            Assert.Equal(CpuMappedCopyResult.Copied, Ledger.TryReadMappedCopyWithWriteWatch(Backing,
                Address, Page, source, new byte[count], out var original));
            return Assert.IsType<CpuMappedCopyObservation>(original);
        }
        internal bool Current(CpuMappedCopyObservation original) => Ledger.IsMappedCopyWriteWatchCurrent(Backing, original);
        internal object Begin(ulong offset, ulong count) => typeof(HostBackingObject)
            .GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Backing, [offset, count])!;
        internal object Envelope() => typeof(HostBackingObject)
            .GetMethod("BeginManagedWriteOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Backing, null)!;
        internal void End(object exact, bool succeeded = true) => typeof(HostBackingObject)
            .GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [exact, succeeded]);
        internal void Hook(Action? action) => typeof(HostMappedCopyLedger)
            .GetField("afterCaptureForControl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Ledger, action);
        public void Dispose() { Backing.Dispose(); if (pin.IsAllocated) pin.Free(); }
    }

    [Fact]
    public void OverlapInvalidatesBeforeAnyDestinationByteAndNeverReopens()
    {
        using var state = new State(); var original = state.Capture();
        var before = new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 16).ToArray();
        var token = state.Begin(32, 1);
        Assert.False(state.Current(original)); Assert.Equal(before, new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 16).ToArray());
        state.End(token); Assert.False(state.Current(original));
    }

    [Fact]
    public void DisjointCompletedWritesSurviveHistoryEvictionWithoutBaselineRefresh()
    {
        using var state = new State(); var original = state.Capture();
        for (var i = 0; i < 1024; i++)
        {
            var token = state.Begin(State.Page, 4); Assert.False(state.Current(original));
            state.End(token); Assert.True(state.Current(original));
        }
        Assert.True(state.Current(original));
    }

    [Theory]
    [InlineData(31ul, 1ul, true)] [InlineData(48ul, 1ul, true)]
    [InlineData(32ul, 1ul, false)] [InlineData(47ul, 1ul, false)]
    [InlineData(32ul, 0ul, true)]
    public void ExactHalfOpenPhysicalBoundsAndEmptyWrites(ulong offset, ulong count, bool current)
    {
        using var state = new State(); var original = state.Capture();
        state.End(state.Begin(offset, count)); Assert.Equal(current, state.Current(original));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void WholeOperationEnvelopeRecordsUnknownFailureWithoutInventingTouchedRange(bool succeeded)
    {
        using var state = new State(); var original = state.Capture();
        state.End(state.Envelope(), succeeded); Assert.Equal(succeeded, state.Current(original));
    }

    [Fact]
    public void AliasWritesUseCanonicalPhysicalOffsetsRatherThanVirtualAddress()
    {
        using var state = new State(); var original = state.Capture();
        state.Map(State.Address + State.Page, 0); // second VA aliases ORIGINAL physical page
        state.End(state.Begin(33, 1)); Assert.False(state.Current(original));
    }

    [Fact]
    public void SameAddressABAAndUnknownMappingCannotReuseOriginalWatch()
    {
        using var state = new State(); var original = state.Capture();
        state.Map(State.Address, 0); Assert.False(state.Current(original));
        var next = state.Capture();
        lock (state.Gate) state.Ledger.QuarantineUnknownAlias();
        Assert.False(state.Current(next));
    }

    [Fact]
    public void BoundedCapacityAndExactOldReleaseDoNotStealReplacement()
    {
        using var state = new State();
        var originals = Enumerable.Range(0, 128).Select(_ => state.Capture()).ToArray();
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, State.Address + 32, new byte[16], out var missing)); Assert.Null(missing);
        state.Ledger.ReleaseMappedCopyWriteWatch(originals[0]);
        var replacement = state.Capture(); state.Ledger.ReleaseMappedCopyWriteWatch(originals[0]);
        Assert.False(state.Current(originals[0])); Assert.True(state.Current(replacement));
        state.End(state.Begin(32, 1)); Assert.False(state.Current(replacement));
        foreach (var original in originals) state.Ledger.ReleaseMappedCopyWriteWatch(original);
        state.Ledger.ReleaseMappedCopyWriteWatch(replacement);
    }

    [Fact]
    public void ParticipatingWriteDuringCopyRejectsOriginalInsteadOfRestamping()
    {
        using var state = new State();
        state.Hook(() => state.End(state.Begin(32, 1)));
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, State.Address + 32, new byte[16], out var rejected)); Assert.Null(rejected);
        state.Hook(null); var next = state.Capture(); Assert.True(state.Current(next));
    }

    [Fact]
    public void ThrowingCopyReleasesExactlyItsWatchChargeAndBackingPin()
    {
        using var state = new State(); var cause = new IOException("copy pause");
        state.Hook(() => throw cause);
        Assert.Same(cause, Assert.Throws<IOException>(() => state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, State.Address + 32, new byte[16], out _)));
        Assert.Equal(0, state.Backing.ActiveAccesses); state.Hook(null);
        var originals = Enumerable.Range(0, 128).Select(_ => state.Capture()).ToArray();
        foreach (var original in originals) state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Fact]
    public void ClosingBackingAndForeignLedgerCannotCertifyOriginalWatch()
    {
        using var state = new State(); using var foreign = new State(); var original = state.Capture();
        Assert.False(foreign.Ledger.IsMappedCopyWriteWatchCurrent(state.Backing, original));
        state.Backing.Dispose(); Assert.False(state.Current(original));
    }

    [Fact]
    public void WriterSlotSaturationPermanentlyInvalidatesExistingWatch()
    {
        using var state = new State(); var original = state.Capture();
        var held = Enumerable.Range(0, 128).Select(_ => state.Begin(State.Page, 1)).ToArray();
        var declined = state.Begin(State.Page, 1);
        foreach (var exact in held) state.End(exact);
        state.End(declined); Assert.False(state.Current(original));
    }

    [Fact]
    public void UnknownDisjointPhysicalWriteCannotBeRelabeledAsKnownDisjointSuccess()
    {
        using var state = new State(); var original = state.Capture();
        state.End(state.Begin(State.Page, 1), false); Assert.False(state.Current(original));
    }
}
