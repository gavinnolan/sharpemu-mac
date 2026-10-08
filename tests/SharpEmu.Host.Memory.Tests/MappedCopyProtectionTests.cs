// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

// Synthetic ledger completion only. These controls do not forge a production
// provider receipt or qualify actual mprotect, permissions or native geometry.
public sealed unsafe partial class MappedCopyProtectionTests(ITestOutputHelper output)
{
    private sealed class State : IDisposable
    {
        internal const ulong Page = 16384, Address = 0x140000000, Size = Page * 4;
        internal readonly object Gate = new();
        internal readonly HostMappedCopyLedger Ledger;
        internal readonly HostBackingObject Backing;
        private GCHandle handle;
        internal State()
        {
            handle = GCHandle.Alloc(new byte[checked((int)(Size + Page))], GCHandleType.Pinned);
            var raw = (ulong)handle.AddrOfPinnedObject(); var alias = (raw + Page - 1) & ~(Page - 1);
            Backing = new(alias, Size, Page, _ => new(HostBackingReleaseStatus.Released));
            Ledger = new(Gate, Page);
            lock (Gate)
            {
                Ledger.RegisterBacking(Backing);
                var view = Ledger.PrepareView(Backing, Address, Size, 0, HostPageProtection.ReadWrite);
                Assert.NotNull(view); Assert.True(Ledger.BeforeNativeEffect(Address, Size)); Ledger.PublishView(view);
            }
            for (var i = 0; i < (int)Size; i++) ((byte*)alias)[i] = (byte)(i * 17 + i / (int)Page);
        }
        internal HostMappedCopyLedger.ProtectionChange? Begin(ulong address, ulong size, HostPageProtection access)
        {
            Assert.True(Monitor.IsEntered(Gate));
            var prepared = Ledger.PrepareProtectionChange(address, size, access);
            Assert.True(Ledger.BeginProtectionChange(prepared, address, size)); return prepared;
        }
        internal CpuMappedCopyObservation Copy()
        {
            var bytes = new byte[16];
            Assert.Equal(CpuMappedCopyResult.Copied, Ledger.TryReadMappedCopy(Backing, Address, Size,
                Address + Page * 2, bytes, out var observation));
            for (var i = 0; i < bytes.Length; i++) Assert.Equal((byte)(i * 17 + 2), bytes[i]);
            return Assert.IsType<CpuMappedCopyObservation>(observation);
        }
        internal void Rejected() => Assert.Equal(CpuMappedCopyResult.Rejected,
            Ledger.TryReadMappedCopy(Backing, Address, Size, Address, new byte[1], out _));
        public void Dispose() { Backing.Dispose(); if (handle.IsAllocated) handle.Free(); }
    }
    private void Marker()
    {
        output.WriteLine($"mapped-protection managed-only architecture={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}");
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
    }

    [Theory]
    [InlineData(HostPageProtection.ReadOnly, GuestPageProtection.Read)]
    [InlineData(HostPageProtection.ReadWrite, GuestPageProtection.Read | GuestPageProtection.Write)]
    public void SuccessfulWholeProtectionMintsFreshMappingWithExactAccess(HostPageProtection access, GuestPageProtection expected)
    {
        Marker(); using var state = new State(); var original = state.Copy();
        HostMappedCopyLedger.ProtectionChange? operation;
        lock (state.Gate)
        {
            operation = state.Begin(State.Address, State.Size, access);
            Assert.NotNull(operation); Assert.False(state.Ledger.IsCurrent(state.Backing, original));
        }
        state.Rejected(); // actual optional copy outside provider gate; synthetic receipt remains pending
        lock (state.Gate) state.Ledger.CompleteProtectionChange(operation, true); // synthetic success, no syscall
        var current = state.Copy(); Assert.Equal(expected, Assert.Single(current.Segments).GuestAccess);
        Assert.NotEqual(original.Segments[0].MappingIncarnation, current.Segments[0].MappingIncarnation);
        Assert.False(state.Ledger.IsCurrent(state.Backing, original));
    }

    [Fact]
    public void PartialSixteenKChangeSplitsOriginalSixtyFourKBackingOffsets()
    {
        Marker(); using var state = new State(); var original = state.Copy();
        lock (state.Gate) state.Ledger.CompleteProtectionChange(
            state.Begin(State.Address + State.Page, State.Page, HostPageProtection.ReadOnly), true);
        var current = state.Copy(); Assert.Equal(3, current.Segments.Count);
        Assert.Equal(new[] { 0UL, State.Page, State.Page * 2 }, current.Segments.Select(s => s.MappingBackingOffset));
        Assert.Equal(new[] { State.Page, State.Page, State.Page * 2 }, current.Segments.Select(s => s.MappingByteCount));
        Assert.Equal(new[] { GuestPageProtection.Read | GuestPageProtection.Write, GuestPageProtection.Read,
            GuestPageProtection.Read | GuestPageProtection.Write }, current.Segments.Select(s => s.GuestAccess));
        Assert.All(current.Segments, s => Assert.Equal(state.Backing.Identity, s.BackingIdentity));
        Assert.False(state.Ledger.IsCurrent(state.Backing, original));
    }

    [Fact]
    public void FailedProtectionNeedsASeparateSuccessfulReceiptToRestoreRetainedPhysicalFacts()
    {
        Marker(); using var state = new State(); var original = state.Copy();
        lock (state.Gate)
            state.Ledger.CompleteProtectionChange(state.Begin(State.Address, State.Size, HostPageProtection.ReadOnly), false);
        state.Rejected();
        lock (state.Gate)
        {
            var later = state.Begin(State.Address, State.Size, HostPageProtection.ReadWrite);
            Assert.NotNull(later); state.Ledger.CompleteProtectionChange(later, true);
        }
        Assert.NotNull(state.Copy()); Assert.False(state.Ledger.IsCurrent(state.Backing, original));
    }

    [Fact]
    public void SubKernelFourKRequestRevokesWithoutClaimingSixteenKRights()
    {
        Marker(); using var state = new State(); var original = state.Copy();
        lock (state.Gate)
        {
            var unqualified = state.Begin(State.Address + 4096, 4096, HostPageProtection.ReadOnly);
            Assert.Null(unqualified); state.Ledger.CompleteProtectionChange(unqualified, true);
        }
        state.Rejected(); Assert.False(state.Ledger.IsCurrent(state.Backing, original));
    }

    [Fact]
    public void NoAccessRetainsOnlyOriginalIdentityForLaterSuccessfulReadableProtection()
    {
        Marker(); using var state = new State(); var original = state.Copy();
        lock (state.Gate) state.Ledger.CompleteProtectionChange(state.Begin(State.Address, State.Size, HostPageProtection.NoAccess), true);
        state.Rejected();
        lock (state.Gate) state.Ledger.CompleteProtectionChange(state.Begin(State.Address, State.Size, HostPageProtection.ReadOnly), true);
        Assert.Equal(GuestPageProtection.Read, Assert.Single(state.Copy().Segments).GuestAccess);
        Assert.False(state.Ledger.IsCurrent(state.Backing, original));
    }

    [Fact]
    public void OneUseAndForeignPreparedOperationsCannotRepeatPublication()
    {
        Marker(); using var state = new State(); using var other = new State();
        lock (state.Gate)
        {
            var operation = state.Begin(State.Address, State.Size, HostPageProtection.ReadOnly);
            lock (other.Gate) Assert.Throws<InvalidOperationException>(() => other.Ledger.CompleteProtectionChange(operation, true));
            state.Ledger.CompleteProtectionChange(operation, true);
            Assert.Throws<InvalidOperationException>(() => state.Ledger.CompleteProtectionChange(operation, true));
        }
        Assert.Equal(GuestPageProtection.Read, Assert.Single(state.Copy().Segments).GuestAccess);
    }

    [Fact]
    public void InterposedEffectNeverPublishesAStaleProtectionCompletion()
    {
        Marker(); using var state = new State();
        lock (state.Gate)
        {
            var operation = state.Begin(State.Address, State.Size, HostPageProtection.ReadOnly);
            Assert.True(state.Ledger.BeforeNativeEffect(State.Address, State.Size)); // synthetic later unmap
            state.Ledger.CompleteProtectionChange(operation, true);
        }
        state.Rejected();
    }

    [Fact]
    public void ClosingBackingDeclinesSuccessorWithoutInventingNativeFailure()
    {
        Marker(); using var state = new State();
        lock (state.Gate)
        {
            var operation = state.Begin(State.Address, State.Size, HostPageProtection.ReadOnly);
            state.Backing.Dispose();
            state.Ledger.CompleteProtectionChange(operation, true); // consumes, conservatively publishes nothing
        }
        state.Rejected();
    }
}
