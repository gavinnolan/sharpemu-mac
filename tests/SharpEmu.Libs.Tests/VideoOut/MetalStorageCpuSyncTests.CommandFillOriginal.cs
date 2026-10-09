// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Collections;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using System.Runtime.ExceptionServices;
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

    private static object?[]? fillMirrorControlQuarantine;

    [Fact]
    public void ActualAppliedFillMirrorBoundaryFailureRetainsExactOwnerAndCannotAcknowledgeOrReplay()
    {
        ActualArchitecture(); BorrowColorState? state = new(); DisplayLedgerMemory? backing = new();
        backing.Backing.EnrollManagedWrites(); var memory = new OriginalFillLedgerMemory(backing);
        var quarantine = new object?[4]; // roots exist before any effect/cleanup uncertainty
        var hookField = Field("_commandMemoryBeforeOriginalFillMirrorForControl"); var rootField = Field("_uncertainCommandMemoryWrite");
        var oldHook = hookField.GetValue(null); var oldRoot = rootField.GetValue(null); Assert.Null(oldRoot);
        var saved = new Dictionary<FieldInfo, object?>();
        foreach (var name in new[] { "_thread", "_renderOwner", "_device", "_commandQueue", "_lastFlushedCommandBuffer" })
        { var field = Field(name); saved.Add(field, field.GetValue(null)); field.SetValue(null, field.FieldType == typeof(nint) ? (object)(nint)0 : null); }
        var priorFatal = GpuFatal.OnFatal; var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        IDisposable? queue = null; Exception? primary = null, cleanup = null; var boundaryCalls = 0;
        var boundaryFailure = new IOException("controlled mirror boundary loss after actual original Fill Applied");
        try
        {
            MetalVideoPresenter.AttachGuestMemory(memory); queue = MetalVideoPresenter.EnterGuestQueue("typed.fill.initial", 1);
            var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
            var expected = backing.Expected; expected.AsSpan(0, 4).Clear();
            hookField.SetValue(null, (Action)(() =>
            {
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Equal(expected, backing.Expected); Assert.Equal(1, memory.Source.Transaction!.Commits);
                Assert.Equal(1, memory.Source.Transaction.Cancellations); Assert.Equal(0, backing.Backing.ActiveAccesses);
                boundaryCalls++; throw boundaryFailure;
            }));
            var fatal = Assert.Throws<GpuFatalException>(() => host.FillBuffer(Address, 4, 0, false));
            Assert.Same(boundaryFailure, fatal.InnerException); Assert.Equal(1, boundaryCalls);
            var retained = rootField.GetValue(null); Assert.NotNull(retained);
            var actualFill = Assert.IsType<CommandMemoryOperation>(retained!.GetType().GetProperty("Operation")!.GetValue(retained));
            Assert.Equal(CommandMemoryOperation.OperationKind.Fill, actualFill.Kind);
            Assert.Same(memory, retained!.GetType().GetProperty("Memory")!.GetValue(retained));
            Assert.Same(host, retained!.GetType().GetProperty("OriginalHost")!.GetValue(retained));
            Assert.Same(memory.First, retained!.GetType().GetProperty("Mapping")!.GetValue(retained));
            Assert.NotNull(retained!.GetType().GetProperty("OriginalWatch")!.GetValue(retained));
            Assert.Equal(1, memory.Source.Releases); // exact Applied successor, not caller's retained original owner
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null)); Assert.Equal(1, reports.Count);
            Assert.Throws<InvalidOperationException>(() => actualFill.ExecuteOriginalWrite(memory, memory.First!, () => Assert.Fail("No replay."), true));
            Assert.Equal(1, memory.Source.Transaction!.Commits); Assert.Equal(1, boundaryCalls); Assert.Equal(expected, backing.Expected);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            // No async caller exists: this is the actual synchronous initial
            // completion route. Restore fake state only after known real provider
            // completion/release and zero native/pending callback ownership.
            var known = memory.Source.Transaction is { Commits: 1, Cancellations: 1 } transaction &&
                transaction.Actual.SuccessfulObservation is not null && backing.Backing.ActiveAccesses == 0 &&
                Field("_initialGuestCompletion").GetValue(null) is null;
            if (known)
            {
                Attempt(() => hookField.SetValue(null, oldHook));
                Attempt(() => { if (memory.First is not null) memory.ReleaseMappedCopyWriteWatch(memory.First); });
                if (cleanup is null)
                {
                    Attempt(() => queue?.Dispose()); queue = null;
                    Attempt(() => backing.Dispose()); backing = null;
                    if (cleanup is null)
                    {
                        Attempt(() => rootField.SetValue(null, oldRoot));
                        foreach (var entry in saved) Attempt(() => entry.Key.SetValue(null, entry.Value));
                        Attempt(() => { GpuFatal.OnFatal = priorFatal; });
                        Attempt(() => state.Dispose()); state = null;
                    }
                }
            }
            if (!known || cleanup is not null)
            {
                quarantine[0] = state; quarantine[1] = backing; quarantine[2] = memory; quarantine[3] = rootField.GetValue(null);
                fillMirrorControlQuarantine = quarantine; // preserve root/hook/observer; no uncertain second close
                cleanup ??= new InvalidOperationException("Mirror control did not prove exact synchronous owner cleanup.");
            }
            if (cleanup is not null)
            { if (primary is null) primary = cleanup; else try { primary.Data["FillMirrorControlCleanup"] = cleanup; } catch { } }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        void Attempt(Action action) { try { action(); } catch (Exception error) { cleanup ??= error; } }
    }

    private sealed class OriginalFillLedgerMemory(DisplayLedgerMemory memory)
        : ICpuMemory, ICpuMappedReadWatchSource, ICpuMappedPublicationSource
    {
        internal readonly CommandSuccessorSource Source = new(memory, "success");
        internal CpuMappedCopyObservation? First;
        public bool TryRead(ulong address, Span<byte> bytes) => memory.TryRead(address, bytes);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("Original Fill cannot use ordinary TryWrite fallback.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong footprint, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? original) => throw new InvalidOperationException("Capable Fill cannot use plain recapture.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong footprint, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? original)
        { var result = Source.TryReadMappedCopyWithWriteWatch(address, footprint, source, bytes, out original); First ??= original; return result; }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => Source.IsMappedCopyObservationCurrent(original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => Source.IsMappedCopyWriteWatchCurrent(original);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => Source.ReleaseMappedCopyWriteWatch(original);
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication) => Source.TryPrepareMappedPublication(original, runs, out publication);
    }

}
