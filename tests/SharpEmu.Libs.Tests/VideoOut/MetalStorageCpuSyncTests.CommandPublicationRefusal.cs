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
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public void FirstOriginalPublicationRefusalReportsExactAttemptWithoutLaterQueriesOrBytes(
        bool fill, bool watched, bool replaced)
    {
        // Actual managed ledger and pinned bytes; the foreign admission is a
        // pre-byte managed token control, not a native/GPU execution receipt.
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        memory.Backing.EnrollManagedWrites();
        CpuMappedCopyObservation? original;
        var result = watched
            ? memory.Ledger.TryReadMappedCopyWithWriteWatch(memory.Backing, Address, 4096, Address, new byte[4096], out original)
            : memory.Ledger.TryReadMappedCopy(memory.Backing, Address, 4096, Address, new byte[4096], out original);
        Assert.Equal(CpuMappedCopyResult.Copied, result); Assert.NotNull(original);
        var begin = typeof(HostBackingObject).GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!;
        var tokens = new List<object>();
        var expected = memory.Expected;
        var source = new FirstPublicationRefusalSource(memory);
        var operation = fill ? CommandMemoryOperation.Fill(Address, 4096, 0xBADC0FFE, false)
            : CommandMemoryOperation.Write(Address, Enumerable.Repeat((byte)0xE5, 4096).ToArray(), "CONTROL_ORIGINAL_WRITE");
        try
        {
            if (replaced) memory.Map();
            else
            {
                // Two actual disjoint managed admission tokens, matching the
                // observed admission=2 family without claiming its game cause.
                tokens.Add(begin.Invoke(memory.Backing, [32768ul, 1ul])!);
                tokens.Add(begin.Invoke(memory.Backing, [49152ul, 1ul])!);
            }
            var error = Assert.Throws<NotSupportedException>(() => operation.ExecuteOriginalWrite(source, original!,
                () => Assert.Fail("Refused preparation cannot reach owner callback."), watched));
            Assert.Contains("preparation=Rejected; ticket_present=False", error.Message);
            Assert.Contains("first_preparation_refusal=UnknownRejected; first_original_revision=0; first_rejected_admission=0", error.Message);
            Assert.Contains($"kind={operation.Kind}; debug_name='{operation.DebugName}'", error.Message);
            Assert.Contains($"address=0x{Address:X16}; bytes=4096; original_has_write_watch={watched}.", error.Message);
            Assert.Same(original, source.PreparedOriginal); Assert.Equal(1, source.Preparations);
            Assert.Equal(0, source.LaterQueries); Assert.False(operation.RetainsUncertainOriginalWrite);
            Assert.Equal(expected, memory.Expected);
            Assert.Throws<InvalidOperationException>(() => operation.ExecuteOriginalWrite(source, original!, () => { }, watched));
            Assert.Equal(1, source.Preparations); Assert.Equal(0, source.LaterQueries);
            Assert.Equal(expected, memory.Expected); Assert.Equal(0, memory.Backing.ActiveAccesses);
        }
        finally
        {
            foreach (var token in tokens) end.Invoke(null, [token, true]);
            if (watched) memory.Ledger.ReleaseMappedCopyWriteWatch(original!);
        }
        Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    private sealed class FirstPublicationRefusalSource(DisplayLedgerMemory memory)
        : ICpuMappedPublicationSource, ICpuMappedReadWatchSource
    {
        internal int Preparations, LaterQueries;
        internal CpuMappedCopyObservation? PreparedOriginal;
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
        {
            Preparations++; PreparedOriginal = original;
            return memory.Ledger.TryPrepareMappedPublication(memory.Backing, original, runs, out publication);
        }
        private InvalidOperationException Unexpected() { LaterQueries++; return new("No later query may explain the first refusal."); }
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
