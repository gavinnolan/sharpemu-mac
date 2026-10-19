// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class FaultThreadImportTraceTests
{
    // Metadata controls only: no native entry, TLS allocation, fault bridge,
    // guest worker, cached flag mutation or actual import ABI success claim.
    private static long Begin(FaultThreadImportTrace trace, ulong guest = 7, string nid = "control") =>
        trace.Begin(trace, 3, nid, guest);

    [Fact]
    public void ExactZeroReturnIsObservedWithoutInventingSemanticSuccess()
    {
        var trace = new FaultThreadImportTrace(); var original = Begin(trace); trace.Complete(original, 0);
        var entry = Assert.Single(trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7));
        Assert.Equal(FaultThreadImportStatus.Returned, entry.Status); Assert.Equal(0UL, entry.Result);
        Assert.Equal(original, entry.Sequence); Assert.Same(trace, entry.BackendOwner);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EnteredAndThrowingCallsHaveNoReturnedValue(bool threw)
    {
        var trace = new FaultThreadImportTrace(); var original = Begin(trace);
        if (threw) trace.Threw(original);
        var entry = Assert.Single(trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7));
        Assert.Equal(threw ? FaultThreadImportStatus.Threw : FaultThreadImportStatus.Entered, entry.Status);
        Assert.Null(entry.Result);
    }

    [Fact]
    public void NestedEvictionCannotCompleteTheYoungerSlotAsItsOriginalCall()
    {
        var trace = new FaultThreadImportTrace(); var evicted = Begin(trace);
        for (var index = 0; index < FaultThreadImportTrace.Capacity; index++) Begin(trace, nid: $"younger-{index}");
        trace.Complete(evicted, 0xBAD); trace.Threw(evicted);
        var entries = trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7);
        Assert.Equal(64, entries.Length); Assert.Equal(2, entries[0].Sequence); Assert.Equal(65, entries[^1].Sequence);
        Assert.All(entries, entry => { Assert.Equal(FaultThreadImportStatus.Entered, entry.Status); Assert.Null(entry.Result); });
    }

    [Fact]
    public void FirstCompletionCannotBeReplacedByALaterReturnOrThrow()
    {
        var trace = new FaultThreadImportTrace(); var first = Begin(trace);
        trace.Complete(first, ulong.MaxValue); trace.Complete(first, 0); trace.Threw(first);
        var entry = Assert.Single(trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7));
        Assert.Equal(FaultThreadImportStatus.Returned, entry.Status); Assert.Equal(ulong.MaxValue, entry.Result);
    }

    [Theory]
    [InlineData(1)] [InlineData(64)]
    public void MaximumSequenceSnapshotAndRejectedNextBeginKeepExactRetainedHistory(int retainedCount)
    {
        var trace = new FaultThreadImportTrace();
        var sequence = typeof(FaultThreadImportTrace).GetField("sequence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var count = typeof(FaultThreadImportTrace).GetField("count", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Mutable diagnostic-only metadata seam; no native/fault/frame access.
        sequence.SetValue(trace, long.MaxValue - retainedCount);
        for (var index = 0; index < retainedCount; index++)
        {
            var original = Begin(trace, nid: $"boundary-{index}"); trace.Complete(original, (ulong)index);
        }
        var entries = trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7);
        Assert.Equal(retainedCount, entries.Length); Assert.Equal(long.MaxValue, entries[^1].Sequence);
        for (var index = 0; index < retainedCount; index++)
        {
            var entry = entries[index];
            Assert.Equal(long.MaxValue - (retainedCount - 1L) + index, entry.Sequence);
            Assert.Equal($"boundary-{index}", entry.Nid); Assert.Equal((ulong)index, entry.Result);
            Assert.Equal(FaultThreadImportStatus.Returned, entry.Status); Assert.Same(trace, entry.BackendOwner);
            Assert.Equal(Environment.CurrentManagedThreadId, entry.ManagedThreadId); Assert.Equal(7UL, entry.GuestThreadHandle);
        }
        Assert.Throws<OverflowException>(() => Begin(trace, nid: "must-not-replace-original"));
        Assert.Equal(long.MaxValue, (long)sequence.GetValue(trace)!); Assert.Equal(retainedCount, (int)count.GetValue(trace)!);
        Assert.Equal(entries, trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7));
    }

    [Fact]
    public void SnapshotFiltersBothOriginalManagedAndGuestThreadIdentity()
    {
        var trace = new FaultThreadImportTrace(); Begin(trace, 7, "guest-seven"); Begin(trace, 8, "guest-eight");
        Assert.Equal("guest-seven", Assert.Single(trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7)).Nid);
        Assert.Empty(trace.Snapshot(trace, Environment.CurrentManagedThreadId, 9));
        Assert.Empty(trace.Snapshot(trace, Environment.CurrentManagedThreadId + 1, 7));
    }

    [Fact]
    public void ForeignManagedThreadCannotReadOrMutateTheExactOwnerRing()
    {
        var trace = new FaultThreadImportTrace(); var first = Begin(trace); Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Throws<InvalidOperationException>(() => Begin(trace));
                Assert.Throws<InvalidOperationException>(() => trace.Complete(first, 9));
                Assert.Throws<InvalidOperationException>(() => trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7));
            }
            catch (Exception error) { failure = error; }
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10))); Assert.Null(failure);
        Assert.Null(Assert.Single(trace.Snapshot(trace, Environment.CurrentManagedThreadId, 7)).Result);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public unsafe void ActualEnabledHelperCapturesTrivialFastReturnAndPreservesMemoryFailure(bool writtenRax)
    {
        using var fixture = new DispatchMetadataFixture(); var calls = 0;
        fixture.Export(ctx => { calls++; if (writtenRax) ctx[CpuRegister.Rax] = 0xFEDCBA9876543210; return 123; });
        Assert.False(fixture.Memory.TryRead(0x1234, new byte[1])); var originalFailure = fixture.Memory.LastFailure;
        var result = fixture.Invoke();
        Assert.Equal(writtenRax ? 0xFEDCBA9876543210UL : 123UL, result); Assert.Equal(1, calls);
        Assert.Equal(originalFailure, fixture.Memory.LastFailure);
        var entry = Assert.Single(fixture.Snapshot()); Assert.Equal(result, entry.Result);
        Assert.Equal(FaultThreadImportStatus.Returned, entry.Status);
        Assert.Equal("V++UgBtQhn0", entry.Nid); Assert.Same(fixture.Backend, entry.BackendOwner);
        Assert.Null(fixture.Backend.GetType().GetField("_recentImportTrace", DispatchMetadataFixture.Instance)!.GetValue(fixture.Backend));
    }

    [Fact]
    public void ActualEnabledHelperRetainsOriginalFastExportThrowWithoutInventedReturn()
    {
        using var fixture = new DispatchMetadataFixture(); var original = new IOException("controlled fast export throw");
        fixture.Export(_ => throw original);
        Assert.Same(original, Assert.Throws<TargetInvocationException>(() => fixture.Invoke()).InnerException);
        var entry = Assert.Single(fixture.Snapshot()); Assert.Equal(FaultThreadImportStatus.Threw, entry.Status); Assert.Null(entry.Result);
    }

    [Fact]
    public void ActualEnabledHelperWithNoContextKeepsExistingErrorWithoutDiagnosticArgumentReads()
    {
        using var fixture = new DispatchMetadataFixture(); fixture.Context(null);
        Assert.Equal(18446744071562199298UL, fixture.InvokeWithoutArgumentStorage());
        Assert.Empty(fixture.Snapshot());
    }

    [Fact]
    public void ActualEnabledHelperUsesSeparateRingsOnTwoManagedThreads()
    {
        FaultThreadImportEntry[]?[] snapshots = new FaultThreadImportEntry[2][]; Exception?[] failures = new Exception?[2];
        var threads = Enumerable.Range(0, 2).Select(index => new Thread(() =>
        {
            try
            {
                using var fixture = new DispatchMetadataFixture(); fixture.Export(ctx => { ctx[CpuRegister.Rax] = (ulong)(index + 7); return 0; });
                fixture.Invoke(); snapshots[index] = fixture.Snapshot();
            }
            catch (Exception failure) { failures[index] = failure; }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.All(failures, failure => Assert.Null(failure));
        var first = Assert.Single(snapshots[0]!); var second = Assert.Single(snapshots[1]!);
        Assert.NotEqual(first.ManagedThreadId, second.ManagedThreadId);
        Assert.Equal(7UL, first.Result); Assert.Equal(8UL, second.Result); Assert.Equal(1, first.Sequence); Assert.Equal(1, second.Sequence);
    }

    [Fact]
    public void SnapshotFiltersExactBackendEvenWithSameManagedThreadAndGuestHandleZero()
    {
        var trace = new FaultThreadImportTrace(); var firstBackend = new object(); var secondBackend = new object();
        var first = trace.Begin(firstBackend, 1, "first-backend", 0); trace.Complete(first, 11);
        var second = trace.Begin(secondBackend, 1, "second-backend", 0); trace.Complete(second, 22);
        var firstEntry = Assert.Single(trace.Snapshot(firstBackend, Environment.CurrentManagedThreadId, 0));
        var secondEntry = Assert.Single(trace.Snapshot(secondBackend, Environment.CurrentManagedThreadId, 0));
        Assert.Equal("first-backend", firstEntry.Nid); Assert.Equal(11UL, firstEntry.Result);
        Assert.Same(firstBackend, firstEntry.BackendOwner); Assert.Same(secondBackend, secondEntry.BackendOwner);
        Assert.Equal("second-backend", secondEntry.Nid); Assert.Equal(22UL, secondEntry.Result);
        Assert.Empty(trace.Snapshot(new object(), Environment.CurrentManagedThreadId, 0));
    }

    [Fact]
    public unsafe void ActualEnabledHotMemcpyHelperPreservesThreeQwordFrameAndReadsNoExtraSlots()
    {
        using var fixture = new DispatchMetadataFixture(); var exportCalls = 0;
        fixture.Export(_ => { exportCalls++; throw new InvalidOperationException("hot memcpy must bypass export"); }, "Q3VBxCXhUHs");
        Assert.False(fixture.Memory.TryRead(0x1234, new byte[1])); var originalFailure = fixture.Memory.LastFailure;
        // Managed source control, not a native guard-page or readable-VMA claim.
        // The actual hot path needs these three slots only; diagnostics have no
        // argument-pack dereferences and retain no fabricated frame fields.
        var pack = new ulong[] { 0x12345678, 0, 0 };
        fixed (ulong* pointer = pack) Assert.Equal(pack[0], fixture.InvokeCore((nint)pointer));
        Assert.Equal(0, exportCalls); Assert.Equal(originalFailure, fixture.Memory.LastFailure);
        Assert.Equal(new ulong[] { 0x12345678, 0, 0 }, pack);
        var entry = Assert.Single(fixture.Snapshot()); Assert.Equal(0x12345678UL, entry.Result);
        Assert.Equal("Q3VBxCXhUHs", entry.Nid); Assert.Equal(FaultThreadImportStatus.Returned, entry.Status);
        Assert.Same(fixture.Backend, entry.BackendOwner);
    }

    [Fact]
    public void ActualEnabledHelperWithInvalidIndexKeepsExistingErrorWithoutArgumentStorage()
    {
        using var fixture = new DispatchMetadataFixture(); var calls = 0;
        fixture.Export(_ => { calls++; return 0; });
        Assert.Equal(18446744071562199042UL, fixture.InvokeCore(0, -1));
        Assert.Equal(0, calls); Assert.Empty(fixture.Snapshot());
    }

    private sealed unsafe class DispatchMetadataFixture : IDisposable
    {
        internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly Type BackendType = typeof(DirectExecutionBackend);
        private readonly Dictionary<FieldInfo, object?> saved = new();
        internal readonly DirectExecutionBackend Backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(BackendType);
        internal readonly TrackedCpuMemory Memory = new(new RefusingMemory());
        internal DispatchMetadataFixture()
        {
            foreach (var name in new[] { "_activeExecutionBackend", "_activeCpuContext", "_activeGuestThreadState", "_importCounterOwner", "_nextImportDispatchIndex", "_importDispatchBlockEnd", "_faultThreadImports" })
            {
                var field = BackendType.GetField(name, Static)!; saved.Add(field, field.GetValue(null));
                field.SetValue(null, field.FieldType == typeof(long) ? (object)0L : null);
            }
            Context(new CpuContext(Memory, Generation.Gen5)); Export(_ => 0);
        }
        internal void Context(CpuContext? context) => BackendType.GetField("_cpuContext", Instance)!.SetValue(Backend, context);
        internal void Export(SysAbiFunction function, string nid = "V++UgBtQhn0")
        {
            var entry = BackendType.GetNestedType("ImportStubEntry", BindingFlags.NonPublic)!;
            var export = new ExportedFunction("control", nid, "metadata-only fast export", Generation.Gen5, function);
            var value = Activator.CreateInstance(entry, [0x800010000UL, nid, export, true, true, false, false, 0UL])!;
            var entries = Array.CreateInstance(entry, 1); entries.SetValue(value, 0);
            BackendType.GetField("_importEntries", Instance)!.SetValue(Backend, entries);
        }
        internal ulong Invoke()
        {
            var pack = new ulong[13]; for (var index = 0; index < 6; index++) pack[index] = (ulong)(index + 1);
            pack[12] = 0x8049A7A6D;
            fixed (ulong* pointer = pack) return InvokeCore((nint)pointer);
        }
        internal ulong InvokeWithoutArgumentStorage() => InvokeCore(0);
        internal ulong InvokeCore(nint pointer, int importIndex = 0) => (ulong)BackendType.GetMethod("DispatchImportWithFaultThreadDiagnostics", Instance)!.Invoke(Backend, [importIndex, pointer])!;
        internal FaultThreadImportEntry[] Snapshot() => BackendType.GetField("_faultThreadImports", Static)!.GetValue(null) is FaultThreadImportTrace trace
            ? trace.Snapshot(Backend, Environment.CurrentManagedThreadId, GuestThreadExecution.CurrentGuestThreadHandle) : [];
        public void Dispose() { foreach (var field in saved) field.Key.SetValue(null, field.Value); }
    }
    private sealed class RefusingMemory : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
