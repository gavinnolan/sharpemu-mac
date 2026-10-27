// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyProtectionTests
{
    private const GuestPageProtection RW = GuestPageProtection.Read | GuestPageProtection.Write;
    private static HostMappedCopyLedger.DerivedProtectionReceipt BeginDerived(State state,
        GuestPageProtection guest = RW, HostPageProtection native = HostPageProtection.ReadOnly,
        ulong size = State.Size)
    {
        Assert.True(state.Ledger.BeginDerivedProtection(State.Address, size, guest, native, out var receipt)); return receipt;
    }

    [Fact]
    public void TemporaryReadonlyKeepsGuestWriteEntitlementButOldCaptureNeverBecomesCurrent()
    {
        Marker(); using var state = new State(); var old = state.Copy();
        HostMappedCopyLedger.DerivedProtectionReceipt receipt;
        lock (state.Gate) receipt = BeginDerived(state);
        state.Rejected(); // synthetic pending receipt, no copy under provider gate
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(receipt, true); // synthetic, no native receipt claim
        var current = state.Copy(); Assert.Equal(RW, Assert.Single(current.Segments).GuestAccess);
        Assert.NotEqual(old.Segments[0].MappingIncarnation, current.Segments[0].MappingIncarnation);
        Assert.False(state.Ledger.IsCurrent(state.Backing, old));
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, state.Ledger.TryPrepareMappedPublication(state.Backing,
            current, [new(State.Address + State.Page * 2, new byte[] { 71 })], out var publication));
        using (publication) Assert.Equal(CpuMappedPublicationResult.Applied, publication!.TryCommit());
    }

    [Fact]
    public void GenuineGuestReadonlyNeverGainsAliasWriteFromWatcherProtection()
    {
        Marker(); using var state = new State();
        lock (state.Gate)
        {
            var prepared = state.Ledger.PrepareProtectionChange(State.Address, State.Size, HostPageProtection.ReadOnly, GuestPageProtection.Read);
            Assert.True(state.Ledger.BeginProtectionChange(prepared, State.Address, State.Size)); state.Ledger.CompleteProtectionChange(prepared, true);
            state.Ledger.CompleteDerivedProtection(BeginDerived(state, GuestPageProtection.Read), true);
        }
        var current = state.Copy(); Assert.Equal(GuestPageProtection.Read, Assert.Single(current.Segments).GuestAccess);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected, state.Ledger.TryPrepareMappedPublication(state.Backing,
            current, [new(State.Address + State.Page * 2, new byte[] { 71 })], out _));
    }

    [Fact]
    public void FailedTransientReceiptRetainsPhysicalFactsForSeparateSuccessfulRestore()
    {
        Marker(); using var state = new State(); var original = state.Copy();
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(BeginDerived(state), false);
        state.Rejected();
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(BeginDerived(state, native: HostPageProtection.ReadWrite), true);
        Assert.NotNull(state.Copy()); Assert.False(state.Ledger.IsCurrent(state.Backing, original));
    }

    [Fact]
    public void SubKernelTransientReceiptDoesNotInventGuestRightsOrLosePhysicalIdentity()
    {
        Marker(); using var state = new State(); var old = state.Copy();
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(BeginDerived(state, size: 4096), true);
        var current = state.Copy(); Assert.Equal(State.Size, Assert.Single(current.Segments).MappingByteCount);
        Assert.Equal(RW, current.Segments[0].GuestAccess); Assert.False(state.Ledger.IsCurrent(state.Backing, old));
        // Alias-backed CPU copy, not a 4K or 16K virtual permission receipt.
    }

    [Fact]
    public void GuestPermissionChangeCannotBeMisclassifiedAsTemporaryReadonly()
    {
        Marker(); using var state = new State();
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(BeginDerived(state, GuestPageProtection.Read), true);
        state.Rejected();
        lock (state.Gate)
        {
            var prepared = state.Ledger.PrepareProtectionChange(State.Address, State.Size, HostPageProtection.ReadOnly, GuestPageProtection.Read);
            Assert.NotNull(prepared); Assert.True(state.Ledger.BeginProtectionChange(prepared, State.Address, State.Size));
            state.Ledger.CompleteProtectionChange(prepared, true);
        }
        Assert.Equal(GuestPageProtection.Read, Assert.Single(state.Copy().Segments).GuestAccess);
    }

    [Fact]
    public void DerivedReceiptCannotReopenRetiredMappingOrRepeatCompletion()
    {
        Marker(); using var state = new State();
        lock (state.Gate)
        {
            var receipt = BeginDerived(state); Assert.True(state.Ledger.BeforeNativeEffect(State.Address, State.Size));
            state.Ledger.CompleteDerivedProtection(receipt, true);
            Assert.Throws<InvalidOperationException>(() => state.Ledger.CompleteDerivedProtection(receipt, true));
        }
        state.Rejected();
    }

    [Fact]
    public void PublicationReservationRejectsDerivedKernelEffectBeforeAnyRevocation()
    {
        Marker(); using var state = new State(); var original = state.Copy();
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, state.Ledger.TryPrepareMappedPublication(state.Backing,
            original, [new(State.Address + State.Page * 2, new byte[] { 71 })], out var publication));
        using (publication)
        {
            lock (state.Gate) Assert.False(state.Ledger.BeginDerivedProtection(State.Address, State.Size, RW, HostPageProtection.ReadOnly, out _));
            Assert.True(state.Ledger.IsCurrent(state.Backing, original));
        }
    }

    [Fact]
    public void DerivedReceiptHelpersAllocateNoHeapAfterStartupWarmup()
    {
        Marker(); using var state = new State();
        lock (state.Gate)
        {
            Assert.True(state.Ledger.BeginDerivedProtection(State.Address, State.Size, RW, HostPageProtection.ReadOnly, out var warm));
            state.Ledger.CompleteDerivedProtection(warm, true);
            // Least of several rounds: a one-off runtime allocation (tiered recompilation
            // on a busy machine) lands in one round only, while storage the helpers
            // allocate shows in every round.
            var least = long.MaxValue;
            for (var round = 0; round < 5 && least != 0; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 1000; index++)
                {
                    if (!state.Ledger.BeginDerivedProtection(State.Address, State.Size, RW, HostPageProtection.ReadOnly, out var receipt))
                        throw new InvalidOperationException("Unexpected refusal during scalar allocation control.");
                    state.Ledger.CompleteDerivedProtection(receipt, true);
                }
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Assert.Equal(0, least);
        }
        // Ordinary helper invocation only, not actual signal/provider/JIT closure proof.
    }
    [Fact]
    public void ExhaustedReceiptSlotsNeverReopenCopyWithoutANewerSuccessfulReceipt()
    {
        Marker(); using var state = new State(); var old = state.Copy();
        var receipts = new HostMappedCopyLedger.DerivedProtectionReceipt[8];
        lock (state.Gate)
        {
            for (var index = 0; index < receipts.Length; index++) receipts[index] = BeginDerived(state);
            var overflow = BeginDerived(state);
            state.Ledger.CompleteDerivedProtection(overflow, true);
            for (var index = receipts.Length - 1; index >= 0; index--)
                state.Ledger.CompleteDerivedProtection(receipts[index], true);
        }
        state.Rejected();
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(BeginDerived(state), true);
        Assert.NotNull(state.Copy()); Assert.False(state.Ledger.IsCurrent(state.Backing, old));
    }

    [Fact]
    public void ReplayedAndForeignReceiptsCannotConsumeANewerOperationInTheSameSlot()
    {
        Marker(); using var state = new State(); using var foreign = new State();
        HostMappedCopyLedger.DerivedProtectionReceipt current;
        lock (state.Gate)
        {
            var old = BeginDerived(state); state.Ledger.CompleteDerivedProtection(old, false);
            current = BeginDerived(state);
            Assert.Throws<InvalidOperationException>(() => state.Ledger.CompleteDerivedProtection(old, true));
            lock (foreign.Gate)
                Assert.Throws<InvalidOperationException>(() => foreign.Ledger.CompleteDerivedProtection(current, true));
        }
        state.Rejected();
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(current, true);
        Assert.NotNull(state.Copy());
    }

    [Fact]
    public void EvenAnUnrelatedInterposedNativeEffectDeclinesPendingAvailabilityReceipt()
    {
        Marker(); using var state = new State();
        lock (state.Gate)
        {
            var receipt = BeginDerived(state);
            Assert.True(state.Ledger.BeforeNativeEffect(State.Address + State.Size * 2, State.Page));
            state.Ledger.CompleteDerivedProtection(receipt, true);
        }
        state.Rejected();
        lock (state.Gate) state.Ledger.CompleteDerivedProtection(BeginDerived(state), true);
        Assert.NotNull(state.Copy());
    }

    [Fact]
    public void IncarnationExhaustionNeverWrapsOrRevivesAnOldObservation()
    {
        Marker(); using var state = new State(); var old = state.Copy();
        var field = typeof(HostMappedCopyLedger).GetField("nextIncarnation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        lock (state.Gate)
        {
            field.SetValue(state.Ledger, long.MaxValue);
            state.Ledger.CompleteDerivedProtection(BeginDerived(state), true);
        }
        state.Rejected(); Assert.False(state.Ledger.IsCurrent(state.Backing, old));
        Assert.Equal(long.MaxValue, field.GetValue(state.Ledger));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void AffectedViewBoundIsExactAndNeverPartiallyReopensOverflow(int count, bool expectedAvailable)
    {
        Marker(); var gate = new object(); var page = State.Page; var size = page * (ulong)count;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(new byte[checked((int)(size + page))], System.Runtime.InteropServices.GCHandleType.Pinned);
        HostBackingObject? backing = null;
        try
        {
            var alias = ((ulong)handle.AddrOfPinnedObject() + page - 1) & ~(page - 1);
            // These are ledger-only guest addresses, not native mappings. A fixed guest
            // range can collide with the pinned array under ASLR, in which case the
            // ledger correctly rejects an effect on its canonical backing alias.
            var guestBase = checked(alias + size + page);
            output.WriteLine($"boundary fixture: alias=0x{alias:X}, guest=0x{guestBase:X}, bytes=0x{size:X}");
            backing = new(alias, size, page, _ => new(HostBackingReleaseStatus.Released));
            var ledger = new HostMappedCopyLedger(gate, page);
            lock (gate)
            {
                ledger.RegisterBacking(backing);
                for (var index = 0; index < count; index++)
                {
                    var address = guestBase + (ulong)index * page;
                    var prepared = ledger.PrepareView(backing, address, page, (ulong)index * page, HostPageProtection.ReadWrite);
                    Assert.NotNull(prepared); Assert.True(ledger.BeforeNativeEffect(address, page)); ledger.PublishView(prepared);
                }
                Assert.True(ledger.BeginDerivedProtection(guestBase, size, RW, HostPageProtection.ReadOnly, out var receipt));
                ledger.CompleteDerivedProtection(receipt, true);
            }
            // Inspect both ends independently; overflow must not reopen its first64 rows.
            foreach (var address in new[] { guestBase, guestBase + size - page })
                Assert.Equal(expectedAvailable ? CpuMappedCopyResult.Copied : CpuMappedCopyResult.Rejected,
                    ledger.TryReadMappedCopy(backing, address, page, address, new byte[1], out _));
        }
        finally { backing?.Dispose(); if (handle.IsAllocated) handle.Free(); }
    }

}
