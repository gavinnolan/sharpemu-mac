// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

// Pinned managed-array ledger controls. No provider/composition/syscalls,
// permission receipt, content certification or native page measurement.
public sealed unsafe class MappedCopyLedgerTests
{
    public MappedCopyLedgerTests(ITestOutputHelper output)
    {
        output.WriteLine($"mapped-copy-ledger architecture={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; managed-only");
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
    }

    private sealed class State : IDisposable
    {
        public const ulong Page = 16384, Address = 0x100000000;
        public readonly object Gate = new();
        public readonly HostMappedCopyLedger Ledger;
        public readonly HostBackingObject Backing;
        private GCHandle handle;
        public readonly byte[] Bytes = new byte[checked((int)(Page * 3))];
        public State()
        {
            handle = GCHandle.Alloc(Bytes, GCHandleType.Pinned);
            var raw = (ulong)handle.AddrOfPinnedObject();
            var aligned = (raw + Page - 1) & ~(Page - 1);
            Backing = new(aligned, Page * 2, Page, _ => new(HostBackingReleaseStatus.Released));
            Ledger = new(Gate, Page);
            lock (Gate) Ledger.RegisterBacking(Backing);
            for (var i = 0; i < (int)(Page * 2); i++) ((byte*)aligned)[i] = (byte)(i * 17 + 3);
        }
        public void Map(ulong address = Address, ulong size = Page, ulong offset = 0,
            HostPageProtection protection = HostPageProtection.ReadWrite)
        {
            lock (Gate)
            {
                var prepared = Ledger.PrepareView(Backing, address, size, offset, protection);
                Assert.NotNull(prepared); Assert.True(Ledger.BeforeNativeEffect(address, size));
                Ledger.PublishView(prepared); // synthetic publication; no kernel receipt
            }
        }
        public CpuMappedCopyObservation Copy(ulong address = Address, ulong size = 16)
        {
            var bytes = new byte[checked((int)size)];
            Assert.Equal(CpuMappedCopyResult.Copied, Ledger.TryReadMappedCopy(Backing, address, size, address, bytes, out var observed));
            Assert.NotNull(observed); return observed!;
        }
        public void Hook(Action action) => typeof(HostMappedCopyLedger)
            .GetField("afterCaptureForControl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Ledger, action);
        public void Dispose()
        {
            Backing.Dispose(); if (handle.IsAllocated) handle.Free();
        }
    }

    [Fact]
    public void CopyUsesDetachedDestinationAndCanonicalPhysicalOffset()
    {
        using var state = new State(); state.Map(offset: State.Page);
        var destination = new byte[128];
        Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page, State.Address + 32, destination, out var observation));
        var original = destination.ToArray();
        new Span<byte>((void*)(state.Backing.AliasBase + State.Page), 256).Fill(0xAA);
        Assert.Equal(original, destination); // owned copy survives later alias writes
        Assert.True(state.Ledger.IsCurrent(state.Backing, observation!)); // mapping-only, NOT content freshness
        Assert.Equal(State.Page, observation!.Segments[0].MappingBackingOffset);
        Assert.Equal(state.Backing.Identity, observation.Segments[0].BackingIdentity);
    }

    [Fact]
    public void SameTupleReplacementNeverRestoresOldIncarnation()
    {
        using var state = new State(); state.Map(); var old = state.Copy();
        state.Map(); var current = state.Copy();
        Assert.False(state.Ledger.IsCurrent(state.Backing, old));
        Assert.True(state.Ledger.IsCurrent(state.Backing, current));
        Assert.NotEqual(old.Segments[0].MappingIncarnation, current.Segments[0].MappingIncarnation);
    }

    [Fact]
    public void PreEffectRevocationRemainsAfterFailedOrUnpublishedMutation()
    {
        using var state = new State(); state.Map(); var old = state.Copy();
        lock (state.Gate) Assert.True(state.Ledger.BeforeNativeEffect(State.Address + 1, 1));
        Assert.False(state.Ledger.IsCurrent(state.Backing, old));
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, 1, State.Address, new byte[1], out _));
    }

    [Fact]
    public void UnrelatedEffectDoesNotRevokeCopy()
    {
        using var state = new State(); state.Map(); var old = state.Copy();
        lock (state.Gate) Assert.True(state.Ledger.BeforeNativeEffect(State.Address + State.Page, 1));
        Assert.True(state.Ledger.IsCurrent(state.Backing, old));
    }

    [Fact]
    public void CanonicalAliasCannotBeOverwrittenOrProtectedByOrdinaryEffects()
    {
        using var state = new State(); state.Map(); var old = state.Copy();
        lock (state.Gate)
        {
            Assert.False(state.Ledger.BeforeNativeEffect(state.Backing.AliasBase + 1, 1));
            Assert.False(state.Ledger.BeforeNativeEffect(state.Backing.AliasBase - 1, 2));
            Assert.True(state.Ledger.IsInternalAliasAddress(state.Backing.AliasBase));
            Assert.True(state.Ledger.IsCurrent(state.Backing, old));
        }
    }

    [Fact]
    public void ExactBackingAndLedgerIdentityAreRequired()
    {
        using var state = new State(); using var other = new State(); state.Map(); var old = state.Copy();
        Assert.False(state.Ledger.IsCurrent(other.Backing, old));
        Assert.False(other.Ledger.IsCurrent(state.Backing, old));
        Assert.Equal(CpuMappedCopyResult.Unavailable, state.Ledger.TryReadMappedCopy(other.Backing,
            State.Address, 1, State.Address, new byte[1], out _));
    }

    [Fact]
    public void CrossSegmentCopyUsesEachOriginalBackingOffset()
    {
        using var state = new State(); state.Map(); state.Map(State.Address + State.Page, offset: State.Page);
        var bytes = new byte[32];
        Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page * 2, State.Address + State.Page - 16, bytes, out var observation));
        Assert.Equal(2, observation!.Segments.Count);
        for (var i = 0; i < bytes.Length; i++) Assert.Equal((byte)(((int)State.Page - 16 + i) * 17 + 3), bytes[i]);
    }

    [Fact]
    public void PartialFootprintAndUnobservedGapFailClosed()
    {
        using var state = new State(); state.Map();
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page * 2, State.Address, new byte[1], out _));
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, 1, State.Address + 1, new byte[1], out _));
    }

    [Fact]
    public void NonKernelAlignedAndUnreadableViewsRemainUnavailable()
    {
        using var state = new State();
        lock (state.Gate)
        {
            Assert.Null(state.Ledger.PrepareView(state.Backing, State.Address, 4096, 0, HostPageProtection.ReadWrite));
            Assert.Null(state.Ledger.PrepareView(state.Backing, State.Address, State.Page, 4096, HostPageProtection.ReadWrite));
        }
        state.Map(protection: HostPageProtection.Execute);
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, 1, State.Address, new byte[1], out _));
    }

    [Fact]
    public void RacedMappingRejectsDestinationAndHookRunsOutsideGate()
    {
        using var state = new State(); state.Map();
        state.Hook(() =>
        {
            Assert.False(Monitor.IsEntered(state.Gate)); Assert.Equal(1, state.Backing.ActiveAccesses);
            lock (state.Gate) Assert.True(state.Ledger.BeforeNativeEffect(State.Address, State.Page));
        });
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, 1, State.Address, new byte[1], out var observation));
        Assert.Null(observation); Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Fact]
    public void ClosingBackingCannotUnmapAliasWhileCopyOwnsPin()
    {
        using var state = new State(); state.Map();
        state.Hook(() =>
        {
            Assert.False(state.Backing.TryDispose(TimeSpan.Zero, out var result));
            Assert.Equal(HostBackingReleaseStatus.TimedOut, result.Status);
            Assert.False(state.Backing.IsDisposed);
        });
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, 1, State.Address, new byte[1], out _));
        Assert.Equal(0, state.Backing.ActiveAccesses); Assert.True(state.Backing.TryDispose(TimeSpan.Zero, out _));
    }

    [Fact]
    public void CopyExceptionReleasesBackingPinWithoutFabricatedObservation()
    {
        using var state = new State(); state.Map(); var failure = new IOException("pause failed");
        state.Hook(() => throw failure);
        Assert.Same(failure, Assert.Throws<IOException>(() => state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, 1, State.Address, new byte[1], out _)));
        Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Fact]
    public void InFlightAliasReleaseBlocksAllAnonymousPlacementUntilExactOutcome()
    {
        using var state = new State(); state.Backing.CloseAccess();
        lock (state.Gate)
        {
            Assert.True(state.Ledger.CanMakeAnonymousPlacement());
            Assert.True(state.Ledger.BeginBackingRelease(state.Backing));
            Assert.False(state.Ledger.BeginBackingRelease(state.Backing));
            Assert.False(state.Ledger.CanMakeAnonymousPlacement());
            state.Ledger.CompleteBackingAliasRelease(state.Backing, true);
            Assert.True(state.Ledger.CanMakeAnonymousPlacement());
            Assert.True(state.Ledger.BeforeNativeEffect(state.Backing.AliasBase, 1));
        }
    }

    [Fact]
    public void UnknownAliasReleaseGloballyVetoesPreviouslyCurrentOtherBacking()
    {
        using var state = new State(); using var other = new State();
        var address = State.Address + State.Page * 4;
        lock (state.Gate)
        {
            state.Ledger.RegisterBacking(other.Backing);
            var prepared = state.Ledger.PrepareView(other.Backing, address, State.Page, 0, HostPageProtection.ReadWrite);
            Assert.NotNull(prepared); state.Ledger.PublishView(prepared);
        }
        Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopy(other.Backing,
            address, 1, address, new byte[1], out var original));
        state.Backing.CloseAccess();
        lock (state.Gate)
        {
            Assert.True(state.Ledger.BeginBackingRelease(state.Backing));
            state.Ledger.CompleteBackingAliasRelease(state.Backing, false);
            Assert.False(state.Ledger.CanMakeAnonymousPlacement());
            Assert.Null(state.Ledger.PrepareView(other.Backing, address, State.Page, 0, HostPageProtection.ReadWrite));
        }
        Assert.False(other.Backing.IsClosing);
        Assert.False(state.Ledger.IsCurrent(other.Backing, original!));
    }

    [Fact]
    public void UnknownCompletionReplayCannotConsumeAnotherPendingAlias()
    {
        using var state = new State(); using var other = new State();
        lock (state.Gate) state.Ledger.RegisterBacking(other.Backing);
        state.Backing.CloseAccess(); other.Backing.CloseAccess();
        lock (state.Gate)
        {
            Assert.True(state.Ledger.BeginBackingRelease(state.Backing));
            Assert.True(state.Ledger.BeginBackingRelease(other.Backing));
            state.Ledger.CompleteBackingAliasRelease(state.Backing, false);
            Assert.Throws<InvalidOperationException>(() => state.Ledger.CompleteBackingAliasRelease(state.Backing, false));
            Assert.False(state.Ledger.BeginBackingRelease(state.Backing));
            state.Ledger.CompleteBackingAliasRelease(other.Backing, true);
            var count = typeof(HostMappedCopyLedger).GetField("aliasEffectsInFlight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state.Ledger);
            Assert.Equal(0, (int)count!);
            Assert.False(state.Ledger.CanMakeAnonymousPlacement());
        }
    }

    [Fact]
    public void SixtyFourSegmentsAreBoundedAndSixtyFifthRejects()
    {
        using var state = new State();
        for (var i = 0; i < 65; i++) state.Map(State.Address + (ulong)i * State.Page);
        Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page * 64, State.Address, new byte[1], out var captured));
        Assert.Equal(64, captured!.Segments.Count);
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page * 65, State.Address, new byte[1], out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void AliasUnknownOutcomeRetainsExclusionAndRevokesEveryObservation()
    {
        using var state = new State(); state.Map(); var old = state.Copy(); state.Backing.CloseAccess();
        lock (state.Gate)
        {
            Assert.True(state.Ledger.BeginBackingRelease(state.Backing));
            state.Ledger.CompleteBackingAliasRelease(state.Backing, false);
            Assert.False(state.Ledger.BeforeNativeEffect(state.Backing.AliasBase, 1));
        }
        Assert.False(state.Ledger.IsCurrent(state.Backing, old));
    }
}
