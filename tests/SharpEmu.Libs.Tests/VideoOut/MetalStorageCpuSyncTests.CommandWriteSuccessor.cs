// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Real ledger commit and bytes; only the optional successor accessor is
    // injected. These rows confer no native/GPU completion or queue authority.
    [Theory]
    [InlineData("missing")]
    [InlineData("throwing")]
    public void WatchedCommandAppliedWithoutItsExactSuccessorCannotAcknowledgeOrRetry(string fault)
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        var source = new CommandSuccessorSource(memory, fault);
        memory.Backing.EnrollManagedWrites();
        Assert.Equal(CpuMappedCopyResult.Copied, source.TryReadMappedCopyWithWriteWatch(Address, 8, Address, new byte[8], out var original));
        Assert.NotNull(original);
        byte[] payload = [4, 0, 0, 0, 0, 0, 0, 0];
        var expected = memory.Expected; payload.CopyTo(expected, 0);
        var operation = CommandMemoryOperation.Write(Address, payload); var ownerChecks = 0;
        try
        {
            var failure = Record.Exception(() => operation.ExecuteOriginalWrite(source, original!,
                () => ownerChecks++, originalHasWriteWatch: true));
            Assert.NotNull(failure);
            if (fault == "throwing") Assert.Same(source.AccessorFailure, failure);
            else Assert.Contains("SAME Applied successor", failure.Message);
            Assert.True(operation.RetainsUncertainOriginalWrite);
            Assert.Equal(1, source.Transaction!.Commits); Assert.Equal(1, source.Transaction.Cancellations);
            Assert.Equal(1, source.Transaction.AccessorQueries); Assert.Equal(0, source.Releases);
            Assert.Equal(2, ownerChecks); // no final owner check/dirty-completed acknowledgement
            Assert.Equal(expected, memory.Expected); // actual known provider effect already happened once
            Assert.NotNull(source.Transaction.Actual.SuccessfulObservation);
            Assert.Throws<InvalidOperationException>(() => operation.ExecuteOriginalWrite(source, original!, () => ownerChecks++, true));
            Assert.Equal(1, source.Transaction.Commits); Assert.Equal(expected, memory.Expected);
        }
        finally
        {
            // The fixture knows its injected accessor alone failed AFTER the
            // real completed commit/release. Production retains that uncertainty.
            source.ReleaseMappedCopyWriteWatch(original!);
            if (source.Transaction?.Actual.SuccessfulObservation is { } successor)
                source.ReleaseMappedCopyWriteWatch(successor);
        }
        Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    [Fact]
    public void PlainCommandWriteDoesNotQueryOptionalAppliedSuccessor()
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        memory.Backing.EnrollManagedWrites(); var source = new CommandSuccessorSource(memory, "throwing");
        Assert.Equal(CpuMappedCopyResult.Copied, memory.TryReadMappedCopy(Address, 8, Address, new byte[8], out var original));
        byte[] payload = [4, 0, 0, 0, 0, 0, 0, 0]; var expected = memory.Expected; payload.CopyTo(expected, 0);
        var operation = CommandMemoryOperation.Write(Address, payload); var ownerChecks = 0;
        operation.ExecuteOriginalWrite(source, original!, () => ownerChecks++);
        Assert.False(operation.RetainsUncertainOriginalWrite);
        Assert.Equal(1, source.Transaction!.Commits); Assert.Equal(1, source.Transaction.Cancellations);
        Assert.Equal(0, source.Transaction.AccessorQueries); Assert.Equal(0, source.Releases);
        Assert.Equal(3, ownerChecks); Assert.Equal(expected, memory.Expected); Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    private sealed class CommandSuccessorSource(DisplayLedgerMemory memory, string fault)
        : ICpuMappedPublicationSource, ICpuMappedReadWatchSource
    {
        internal readonly IOException AccessorFailure = new("controlled successor accessor failure AFTER actual Applied");
        internal CommandSuccessorTransaction? Transaction;
        internal int Releases;
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong footprint, ulong source,
            Span<byte> bytes, out CpuMappedCopyObservation? original) =>
            memory.Ledger.TryReadMappedCopy(memory.Backing, address, footprint, source, bytes, out original);
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => memory.Ledger.IsCurrent(memory.Backing, original);
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong footprint, ulong source,
            Span<byte> bytes, out CpuMappedCopyObservation? original) =>
            memory.Ledger.TryReadMappedCopyWithWriteWatch(memory.Backing, address, footprint, source, bytes, out original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) =>
            memory.Ledger.IsMappedCopyWriteWatchCurrent(memory.Backing, original);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original)
        { Releases++; memory.Ledger.ReleaseMappedCopyWriteWatch(original); }
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
        {
            var result = memory.Ledger.TryPrepareMappedPublication(memory.Backing, original, runs, out var actual);
            publication = null;
            if (result == CpuMappedPublicationPreparationResult.Prepared)
                publication = new CpuMappedPublication(Transaction = new(actual!, fault, AccessorFailure));
            return result;
        }
    }
    private sealed class CommandSuccessorTransaction(CpuMappedPublication actual, string fault, IOException failure)
        : CpuMappedPublicationTransaction
    {
        internal readonly CpuMappedPublication Actual = actual;
        internal int Commits, Cancellations, AccessorQueries;
        internal override CpuMappedPublicationResult TryCommit() { Commits++; return Actual.TryCommit(); }
        internal override CpuMappedCopyObservation? SuccessfulObservation
        { get { AccessorQueries++; if (fault == "throwing") throw failure; return null; } }
        internal override void Cancel() { Cancellations++; Actual.Dispose(); }
    }
}
