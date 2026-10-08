// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// Serialized by the assembly's existing DisableTestParallelization setting.
// Phantom tracker metadata ONLY: never Track/Arm, initialize a provider,
// manipulate page permissions, install handlers or execute guest/native code.
public sealed class NativeRangeDecisionTests
{
    [Theory]
    [InlineData(false, 0x8120UL, 1)]
    [InlineData(true, 0UL, 2)]
    public void EntryGuardRecordsReasonWithoutBorrowingOwner(bool enabled, ulong address, int reason)
    {
        using var fixture = new Fixture(enabled);
        Assert.False(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(address, 17));
        var record = Assert.Single(fixture.Decisions);
        Check(record, 0, reason, -1, false);
        Assert.Equal(0L, Value<long>(record, "OwnerIdentity"));
        Assert.Equal(0, Value<int>(record, "OwnerObservation"));
    }

    [Fact]
    public void DisarmedExactRangeDeclinesAndReportsActualCasState()
    {
        using var fixture = new Fixture(true, new Range(0x8120, 16, 0));
        Assert.False(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17));
        var decisions = fixture.Decisions;
        Assert.Equal(2, decisions.Length);
        Check(decisions[0], 1, 5, 0, false);
        Assert.Equal(2, Value<int>(decisions[0], "OwnerObservation"));
        Assert.Equal(0x8120UL, Value<ulong>(decisions[0], "OwnerAddress"));
        Assert.Equal(16UL, Value<ulong>(decisions[0], "RawAddress")); // Kind8 requested bytes
        Assert.Equal(0x8000UL, Value<ulong>(decisions[0], "ErrorCode")); // Kind8 tracking start
        Assert.Equal(0x9000UL, Value<ulong>(decisions[0], "OwnerEnd"));
        Assert.Equal(7L, Value<long>(decisions[0], "ArmEpoch"));
        Check(decisions[1], 3, 8, -1, false);
        Assert.Equal(0L, Value<long>(decisions[1], "OwnerIdentity"));
    }

    [Fact]
    public void NoMatchingRangeProducesOnlyNoHandledCompletion()
    {
        using var fixture = new Fixture(true, new Range(0xA120, 16, 0));
        Assert.False(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17));
        Check(Assert.Single(fixture.Decisions), 3, 8, -1, false);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void BusyExactAndSharedPageChoicesPreserveExistingAcceptance(int state, int reason)
    {
        using var fixture = new Fixture(true, new Range(0x8120, 16, state));
        Assert.True(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17));
        var decisions = fixture.Decisions;
        Assert.Equal(2, decisions.Length);
        Check(decisions[0], 1, reason, state, true);
        Check(decisions[1], 2, reason, state, true);
        Assert.Equal(Value<long>(decisions[0], "OwnerIdentity"), Value<long>(decisions[1], "OwnerIdentity"));
        Assert.DoesNotContain(fixture.Records, record => Value<int>(record, "Kind") is 3 or 4);
    }

    [Fact]
    public void SharedPageOutsideExactBytesIdentifiesInitiatingRange()
    {
        using var fixture = new Fixture(true, new Range(0x8120, 16, 2));
        Assert.True(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8F00, 17));
        var record = Assert.Single(fixture.Decisions);
        Check(record, 2, 3, 2, true);
        Assert.Equal(0x8F00UL, Value<ulong>(record, "Address"));
        Assert.Equal(0x8120UL, Value<ulong>(record, "OwnerAddress"));
    }

    [Fact]
    public void OverlapReportsEachOwnerRatherThanFirstSnapshotMatch()
    {
        using var fixture = new Fixture(true, new Range(0x8120, 32, 0), new Range(0x8110, 64, 2));
        Assert.True(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17));
        var exact = fixture.Decisions.Where(record => Value<int>(record, "Route") == 1).ToArray();
        Assert.Equal(2, exact.Length);
        Check(exact[0], 1, 5, 0, false);
        Check(exact[1], 1, 3, 2, true);
        Assert.NotEqual(Value<long>(exact[0], "OwnerIdentity"), Value<long>(exact[1], "OwnerIdentity"));
        Assert.Equal(0x8110UL, Value<ulong>(exact[1], "OwnerAddress"));
        // Busy co-owner keeps boundary pass active, exposing the disarmed co-owner too.
        Assert.Contains(fixture.Decisions, record => Value<int>(record, "Route") == 2 && Value<int>(record, "Reason") == 5);
    }

    [Fact]
    public void ZeroWatcherRemovalSuccessDoesNotInventNativePermissionReceipt()
    {
        using var fixture = new Fixture(true, new Range(0x8120, 16, 1));
        Assert.True(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17));
        var record = Assert.Single(fixture.Decisions);
        Check(record, 1, 7, 1, true);
        Assert.Equal(0, Value<int>(record, "OwnerState"));
        Assert.DoesNotContain(fixture.Records, row => Value<int>(row, "Kind") is 3 or 4);
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void RecorderKeepsCasInputSeparateFromLaterObservation(int reason, bool handled)
    {
        using var fixture = new Fixture(true, new Range(0x8120, 16, 0));
        fixture.RecordDecision(reason, 2, handled);
        var record = Assert.Single(fixture.Decisions);
        Check(record, 1, reason, 2, handled);
        Assert.Equal(0, Value<int>(record, "OwnerState"));
        // Pure encoding control, NOT execution of a failed mprotect branch.
    }

    [Fact]
    public void DisabledDiagnosticsAndFullBufferDoNotChangeDecisions()
    {
        using var fixture = new Fixture(true, new Range(0x8120, 16, 0));
        fixture.SetBuffer(null);
        Assert.False(GuestImageWriteTracker.NativeFaultDiagnosticsEnabled);
        for (var index = 0; index < 100; index++)
            Assert.False(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            _ = GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        fixture.SetBuffer(fixture.Buffer);
        for (var index = 0; index < 100; index++)
            Assert.False(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17));
        Assert.True(Value<long>(fixture.Buffer, "Dropped") > 0);
    }

    [Fact]
    public void WarmEnabledRecorderAllocatesNoManagedBytes()
    {
        using var fixture = new Fixture(true, new Range(0x8120, 16, 0));
        for (var index = 0; index < 10; index++)
            _ = GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 30; index++)
            _ = GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(0x8120, 17);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(80, fixture.Decisions.Length);
        Assert.Equal(0L, Value<long>(fixture.Buffer, "Dropped"));
    }

    private static void Check(object record, int pass, int reason, int previous, bool handled)
    {
        Assert.Equal(8, Value<int>(record, "Kind"));
        Assert.Equal(17L, Value<long>(record, "Invocation"));
        Assert.Equal(4096UL, Value<ulong>(record, "Size")); // guest tracking geometry, not native16K
        Assert.Equal(pass, Value<int>(record, "Route"));
        Assert.Equal(reason, Value<int>(record, "Reason"));
        Assert.Equal(previous, Value<int>(record, "Reserved"));
        Assert.Equal(handled ? 1 : 0, Value<int>(record, "Result"));
    }

    private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly Type Tracker = typeof(GuestImageWriteTracker);
    private static T Value<T>(object value, string name) => (T)(value.GetType().GetField(name, Instance)?.GetValue(value)
        ?? value.GetType().GetProperty(name, Instance)!.GetValue(value))!;
    private static void Set(object value, string name, object field) => value.GetType().GetField(name, Instance)!.SetValue(value, field);
    private sealed record Range(ulong Address, ulong Size, int State);

    private sealed class Fixture : IDisposable
    {
        private readonly object? _oldBuffer;
        private readonly object? _oldSnapshot;
        private readonly object? _oldEnabled;
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"sharpemu-range-decision-{Guid.NewGuid():N}.bin");
        private readonly object[] _ranges;
        internal object Buffer { get; }
        internal object[] Records => ((Array)Tracker.GetMethod("SnapshotNativeFaultDiagnosticsForTests", HiddenStatic)!.Invoke(null, null)!).Cast<object>().ToArray();
        internal object[] Decisions => Records.Where(record => Value<int>(record, "Kind") == 8).ToArray();

        internal Fixture(bool enabled, params Range[] ranges)
        {
            _oldBuffer = Tracker.GetField("_nativeDiagnosticBuffer", HiddenStatic)!.GetValue(null);
            _oldSnapshot = Tracker.GetField("_rangeSnapshot", HiddenStatic)!.GetValue(null);
            _oldEnabled = Tracker.GetField("_enabled", HiddenStatic)!.GetValue(null);
            var bufferType = Tracker.Assembly.GetType("SharpEmu.HLE.Host.NativeFaultTraceBuffer")!;
            Buffer = Activator.CreateInstance(bufferType, Instance, null, [_path, 128], null)!;
            var rangeType = Tracker.GetNestedType("TrackedRange", BindingFlags.NonPublic)!;
            var array = Array.CreateInstance(rangeType, ranges.Length);
            _ranges = ranges.Select(range =>
            {
                var value = Activator.CreateInstance(rangeType, nonPublic: true)!;
                Set(value, "Address", range.Address); Set(value, "ByteCount", range.Size);
                Set(value, "Start", range.Address & ~4095UL); Set(value, "End", (range.Address + range.Size + 4095) & ~4095UL);
                Set(value, "Armed", range.State); Set(value, "DiagnosticArmEpoch", 7L);
                return value;
            }).ToArray();
            for (var index = 0; index < _ranges.Length; index++) array.SetValue(_ranges[index], index);
            var snapshot = Activator.CreateInstance(Tracker.GetNestedType("RangeSnapshot", BindingFlags.NonPublic)!, Instance, null, [array], null);
            Tracker.GetField("_rangeSnapshot", HiddenStatic)!.SetValue(null, snapshot);
            Tracker.GetField("_enabled", HiddenStatic)!.SetValue(null, enabled);
            SetBuffer(Buffer);
        }
        internal void SetBuffer(object? buffer) => Tracker.GetMethod("ExchangeNativeDiagnosticBufferForTests", HiddenStatic)!.Invoke(null, [buffer]);
        internal void RecordDecision(int reason, int previous, bool handled)
        {
            var passType = Tracker.GetNestedType("NativeRangeDecisionPass", BindingFlags.NonPublic)!;
            var reasonType = Tracker.GetNestedType("NativeRangeDecisionReason", BindingFlags.NonPublic)!;
            Tracker.GetMethod("RecordNativeRangeDecision", HiddenStatic)!.Invoke(null,
                [_ranges[0], 0x8120UL, 17L, Enum.ToObject(passType, 1), Enum.ToObject(reasonType, reason), previous, handled]);
        }
        public void Dispose()
        {
            SetBuffer(_oldBuffer);
            Tracker.GetField("_rangeSnapshot", HiddenStatic)!.SetValue(null, _oldSnapshot);
            Tracker.GetField("_enabled", HiddenStatic)!.SetValue(null, _oldEnabled);
            ((IDisposable)Buffer).Dispose(); File.Delete(_path);
        }
    }
}
