// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Collections;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    [Theory]
    [InlineData(4, 0u)]
    [InlineData(4096, 0x12345678u)]
    [InlineData(65536, 0xA1B2C3D4u)]
    public void OriginalClosedFillAppliesItsIndependentFullLittleEndianPatternOnce(int count, uint value)
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        memory.Backing.EnrollManagedWrites(); var source = new CommandSuccessorSource(memory, "success");
        Assert.Equal(CpuMappedCopyResult.Copied, source.TryReadMappedCopyWithWriteWatch(Address, (ulong)count, Address, new byte[count], out var original));
        var expected = memory.Expected;
        for (var i = 0; i < count; i++) expected[i] = (byte)(value >> ((i & 3) * 8));
        var fill = CommandMemoryOperation.Fill(Address, (ulong)count, value, false); var ownerChecks = 0;
        Assert.True(fill.HasOriginalWritePayload); Assert.Equal((ulong)count, fill.OwnedByteCount);
        try
        {
            fill.ExecuteOriginalWrite(source, original!, () => ownerChecks++, originalHasWriteWatch: true);
            Assert.Equal(3, ownerChecks); Assert.Equal(1, source.Transaction!.Commits);
            Assert.Equal(1, source.Transaction.Cancellations); Assert.Equal(1, source.Releases);
            Assert.False(fill.RetainsUncertainOriginalWrite); Assert.Equal(expected, memory.Expected);
            Assert.Throws<InvalidOperationException>(() => fill.ExecuteOriginalWrite(source, original!, () => ownerChecks++, true));
            Assert.Equal(1, source.Transaction.Commits); Assert.Equal(expected, memory.Expected);
        }
        finally { source.ReleaseMappedCopyWriteWatch(original!); }
        Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("overlap")] [InlineData("unknown-write")]
    [InlineData("replacement")] [InlineData("rights")]
    [InlineData("released")]
    public void OriginalClosedFillRefusesLostOriginalBeforeAnyByteAndNeverRetries(string change)
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        memory.Backing.EnrollManagedWrites(); var source = new CommandSuccessorSource(memory, "success");
        Assert.Equal(CpuMappedCopyResult.Copied, source.TryReadMappedCopyWithWriteWatch(Address, 4096, Address, new byte[4096], out var original));
        if (change is "overlap" or "unknown-write")
        {
            var begin = typeof(HostBackingObject).GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var end = typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!;
            var token = begin.Invoke(memory.Backing, [change == "overlap" ? 4095ul : 32768ul, 1ul])!;
            end.Invoke(null, [token, change != "unknown-write"]);
        }
        else if (change == "replacement") memory.Map();
        else if (change == "released") source.ReleaseMappedCopyWriteWatch(original!);
        else lock (memory.Gate)
        {
            var rights = memory.Ledger.PrepareProtectionChange(Address, DisplayLedgerMemory.ByteCount, HostPageProtection.ReadOnly);
            Assert.NotNull(rights); Assert.True(memory.Ledger.BeginProtectionChange(rights, Address, DisplayLedgerMemory.ByteCount));
            memory.Ledger.CompleteProtectionChange(rights!, nativeSuccess: true); // controlled ledger protection receipt only
        }
        var expected = memory.Expected; var fill = CommandMemoryOperation.Fill(Address, 4096, 0x12345678, false);
        try
        {
            Assert.Throws<NotSupportedException>(() => fill.ExecuteOriginalWrite(source, original!, () => Assert.Fail("No original owner callback after refused preparation."), true));
            Assert.Null(source.Transaction); Assert.Equal(expected, memory.Expected);
            Assert.Throws<InvalidOperationException>(() => fill.ExecuteOriginalWrite(source, original!, () => { }, true));
            Assert.Equal(expected, memory.Expected); Assert.Equal(0, memory.Backing.ActiveAccesses);
        }
        finally { source.ReleaseMappedCopyWriteWatch(original!); }
    }

    [Theory]
    [InlineData("missing")] [InlineData("throwing")] [InlineData("commit-throw")]
    public void OriginalClosedFillRetainsUncertainAppliedScopeWithoutAcknowledgementOrReplay(string failure)
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        memory.Backing.EnrollManagedWrites(); var source = new CommandSuccessorSource(memory, failure);
        Assert.Equal(CpuMappedCopyResult.Copied, source.TryReadMappedCopyWithWriteWatch(Address, 4, Address, new byte[4], out var original));
        var expected = memory.Expected; expected.AsSpan(0, 4).Clear();
        var fill = CommandMemoryOperation.Fill(Address, 4, 0, false); var checks = 0;
        try
        {
            Assert.NotNull(Record.Exception(() => fill.ExecuteOriginalWrite(source, original!, () => checks++, true)));
            Assert.True(fill.RetainsUncertainOriginalWrite); Assert.Equal(2, checks);
            Assert.Equal(1, source.Transaction!.Commits); Assert.Equal(1, source.Transaction.Cancellations);
            Assert.Equal(expected, memory.Expected); // the real ledger Applied once before the injected loss
            Assert.Throws<InvalidOperationException>(() => fill.ExecuteOriginalWrite(source, original!, () => { }, true));
            Assert.Equal(1, source.Transaction.Commits); Assert.Equal(expected, memory.Expected);
        }
        finally
        {
            // Only this controlled accessor/return loss is known to the fixture.
            // The renderer roots the same ticket/original owner in production.
            source.ReleaseMappedCopyWriteWatch(original!);
            if (source.Transaction?.Actual.SuccessfulObservation is { } successor) source.ReleaseMappedCopyWriteWatch(successor);
        }
        Assert.Equal(0, memory.Backing.ActiveAccesses);
    }
    [Theory]
    [InlineData("capture")]
    [InlineData("execution")]
    public void OriginalFillNeverBypassesAnExistingOrLaterNativeMirrorExtent(string moment)
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var memory = new TypedColorMemory("fill"); MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
        var operation = CommandMemoryOperation.Fill(TypedColorMemory.Destination, 4, 0, false);
        var extents = (IDictionary)Field("_guestImageExtents").GetValue(null)!;
        object? original = null;
        try
        {
            if (moment == "capture") lock (Field("_gate").GetValue(null)!)
                extents.Add(TypedColorMemory.Destination, (1u, 1u, 4ul)); // no native handle/authority
            original = InvokeVolume("PrepareCommandMemoryOperation", host, operation, (Action)(() => Assert.Fail("No callback invocation.")))!;
            var mapping = original.GetType().GetProperty("Mapping")!.GetValue(original);
            if (moment == "capture")
            {
                Assert.Null(mapping); Assert.Equal(0, memory.CommandCopies); Assert.Equal(0, memory.WatchedCommandCopies);
            }
            else
            {
                Assert.NotNull(mapping); var queries = memory.CommandQueries;
                lock (Field("_gate").GetValue(null)!) extents.Add(TypedColorMemory.Destination, (1u, 1u, 4ul));
                var refused = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireCommandMemoryOriginal", original, mapping, false));
                Assert.IsType<NotSupportedException>(refused.InnerException);
                Assert.Contains("mirror destination", refused.InnerException!.Message);
                Assert.Equal(queries, memory.CommandQueries); // scalar veto before any later provider call
                Assert.Equal(1, memory.CommandCopies);
            }
            Assert.Equal(0, memory.Writes); Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        }
        finally
        {
            lock (Field("_gate").GetValue(null)!) extents.Remove(TypedColorMemory.Destination);
            if (original is not null) InvokeVolume("ReleaseOriginalCommandMemoryWatch", original, 0L, null);
        }
    }

}
