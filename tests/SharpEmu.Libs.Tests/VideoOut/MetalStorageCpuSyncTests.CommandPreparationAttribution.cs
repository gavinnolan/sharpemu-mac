// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void FirstCommandPreparationAttributionKeepsOriginalOnceAndNeverReplays(bool fill, bool watched)
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        memory.Backing.EnrollManagedWrites();
        CpuMappedCopyObservation? original;
        var result = watched
            ? memory.Ledger.TryReadMappedCopyWithWriteWatch(memory.Backing, Address, 4096, Address, new byte[4096], out original)
            : memory.Ledger.TryReadMappedCopy(memory.Backing, Address, 4096, Address, new byte[4096], out original);
        Assert.Equal(CpuMappedCopyResult.Copied, result); Assert.NotNull(original);
        var begin = typeof(HostBackingObject).GetMethod("BeginManagedWriteOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!;
        var source = new AttributedCommandPreparationSource(memory);
        var operation = fill ? CommandMemoryOperation.Fill(Address, 4096, 0xBADC0FFE, false)
            : CommandMemoryOperation.Write(Address, Enumerable.Repeat((byte)0xE5, 4096).ToArray(), "ATTRIBUTED_WRITE");
        var bytes = memory.Expected; object? held = null;
        try
        {
            held = begin.Invoke(memory.Backing, null)!;
            var error = Assert.Throws<NotSupportedException>(() => operation.ExecuteOriginalWrite(source, original!,
                () => Assert.Fail("Rejected preparation cannot reach owner/effect callback."), watched));
            Assert.Contains("first_preparation_refusal=AdmissionNonzero", error.Message);
            Assert.Contains("first_rejected_admission=1; attribution_is_authority=False", error.Message);
            Assert.Equal(1, source.Calls); Assert.Same(original, source.Original);
            Assert.Equal(bytes, memory.Expected); Assert.Equal(0, memory.Backing.ActiveAccesses);
            Assert.False(operation.RetainsUncertainOriginalWrite);
        }
        finally
        {
            if (held is not null) { var exact = held; held = null; end.Invoke(null, [exact, true]); }
            if (watched) memory.Ledger.ReleaseMappedCopyWriteWatch(original!);
        }
        // Even after the actual foreign admission ends, the command's first
        // execution is consumed. There is no second prepare or fresh original.
        Assert.Throws<InvalidOperationException>(() => operation.ExecuteOriginalWrite(source, original!, () => { }, watched));
        Assert.Equal(1, source.Calls); Assert.Equal(bytes, memory.Expected); Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    private sealed class AttributedCommandPreparationSource(DisplayLedgerMemory memory) : ICpuMappedPublicationSource, ICpuMappedReadWatchSource
    {
        internal int Calls;
        internal CpuMappedCopyObservation? Original;
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
            => throw new InvalidOperationException("Attributed caller must use its first actual returned classification.");
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication,
            out CpuMappedPublicationPreparationRefusal refusal)
        {
            Calls++; Original = original;
            return memory.Ledger.TryPrepareMappedPublication(memory.Backing, original, runs, out publication, out refusal);
        }
        private static InvalidOperationException Unexpected() => new("No later mapping/watch/copy query may classify preparation.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong footprint, ulong source,
            Span<byte> bytes, out CpuMappedCopyObservation? original) { original = null; throw Unexpected(); }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => throw Unexpected();
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong footprint, ulong source,
            Span<byte> bytes, out CpuMappedCopyObservation? original) { original = null; throw Unexpected(); }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => throw Unexpected();
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original) => throw Unexpected();
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => throw Unexpected();
    }
}
