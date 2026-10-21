// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// Real ledger admissions and pinned managed bytes. These controls do not
// manufacture kernel mappings, native receipts, render ownership or GPU work.
public sealed unsafe partial class MappedCopyReadWatchTests
{
    private static CpuMappedCopyResult ColdCopy(State state, byte[] bytes, Func<bool> current,
        out CpuMappedCopyObservation? observation) =>
        state.Ledger.TryReadMappedCopyWithWriteWatchAdmissionWait(state.Backing,
            State.Address, State.Page, State.Address + 32, bytes, current, out observation);

    private static long ColdAdmission(State state) => (long)HistoryWriters(state).GetType()
        .GetField("admission", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(HistoryWriters(state))!;

    private static void ColdUnchanged(byte[] bytes, CpuMappedCopyObservation? observation)
    {
        Assert.Null(observation);
        Assert.All(bytes, value => Assert.Equal((byte)0xad, value));
    }

    private sealed class ColdTrace : IDisposable
    {
        private static readonly Type Type = typeof(HostMappedCopyLedger).Assembly
            .GetType("SharpEmu.HLE.CpuMappedCopyDiagnostics", true)!;
        private static readonly FieldInfo Enabled = Type.GetField("enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo Rows = Type.GetField("rows", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo Sink = Type.GetField("sinkForControl", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? enabled = Enabled.GetValue(null), rows = Rows.GetValue(null), sink = Sink.GetValue(null);
        internal ColdTrace(Action<string> action)
        { Enabled.SetValue(null, true); Rows.SetValue(null, 0); Sink.SetValue(null, action); }
        public void Dispose()
        { Sink.SetValue(null, sink); Rows.SetValue(null, rows); Enabled.SetValue(null, enabled); }
    }

    [Theory]
    [InlineData("ordinary-disjoint", 1L)]
    [InlineData("ordinary-overlap", 1L)]
    [InlineData("read-validation", 4294967296L)]
    [InlineData("disjoint-publication", 4294967296L)]
    public void ColdEnrollmentWaitRetainsFirstMappingAndPinUntilKnownAdmissionRelease(string kind, long firstAdmission)
    {
        using var state = new State(); Enroll(state);
        new Span<byte>((void*)(state.Backing.AliasBase + 32), 16).Fill(0x3a);
        var priorMapping = PublicationOriginal(state, State.Address + 32, 16);
        var mapping = priorMapping.Segments.ToArray();
        var firstStates = (Array)CaptureOwner(priorMapping).GetType()
            .GetField("States", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(CaptureOwner(priorMapping))!;
        object? writer = null;
        CpuMappedReadValidation? read = null;
        CpuMappedPublication? publication = null;
        CpuMappedCopyObservation? readOriginal = null;
        CpuMappedCopyObservation? result = null;
        if (kind.StartsWith("ordinary", StringComparison.Ordinal))
            writer = state.Begin(kind == "ordinary-overlap" ? 32 : State.Page, 16);
        else if (kind == "read-validation")
        {
            readOriginal = state.Capture();
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedReadValidation(state.Backing, readOriginal, out read));
            Assert.NotNull(read);
        }
        else
        {
            var original = PublicationOriginal(state, State.Address + State.Page, 16);
            publication = Prepare(state, original, original.SourceAddress);
        }
        var baselineAccesses = state.Backing.ActiveAccesses;
        var baselineWatches = CommandWriteWatchCharges(state);
        var calls = 0; var copies = 0; var releases = 0; var initialSamples = 0;
        RevisionHook(state, (hasRevision, _) => { initialSamples++; Assert.False(hasRevision); });
        var lines = new List<string>();
        state.Hook(() => { copies++; Assert.Equal(1, state.Backing.ActiveAccesses); });
        using var trace = new ColdTrace(line =>
        {
            Assert.False(Monitor.IsEntered(state.Gate));
            Assert.Equal(0, state.Backing.ActiveAccesses);
            lines.Add(line);
        });
        try
        {
            var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray();
            Assert.Equal(CpuMappedCopyResult.Copied, ColdCopy(state, bytes, () =>
            {
                Assert.False(Monitor.IsEntered(state.Gate));
                if (++calls == 2)
                {
                    Assert.Equal(firstAdmission, ColdAdmission(state));
                    Assert.Equal(baselineAccesses + 1, state.Backing.ActiveAccesses);
                    Assert.Equal(baselineWatches + 1, CommandWriteWatchCharges(state));
                    Assert.All(bytes, value => Assert.Equal((byte)0xad, value));
                    Assert.Equal(0, copies); releases++;
                    if (writer is not null) { state.End(writer); writer = null; }
                    else if (read is not null) read.Dispose();
                    else Assert.Equal(CpuMappedPublicationResult.Applied, publication!.TryCommit());
                }
                return true;
            }, out result));
            var exact = Assert.IsType<CpuMappedCopyObservation>(result);
            Assert.Equal(1, copies); Assert.Equal(1, releases); Assert.True(calls >= 5);
            Assert.Equal(1, initialSamples); // No new generic/cold copy capture while waiting.
            Assert.All(bytes, value => Assert.Equal((byte)0x3a, value));
            Assert.Equal(mapping, exact.Segments.ToArray());
            var returnedStates = (Array)CaptureOwner(exact).GetType()
                .GetField("States", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(CaptureOwner(exact))!;
            Assert.Equal(firstStates.Length, returnedStates.Length);
            for (var index = 0; index < firstStates.Length; index++) Assert.Same(firstStates.GetValue(index), returnedStates.GetValue(index));
            Assert.Equal(EnrollmentRevision(exact), OriginalRevision(exact));
            Assert.True(state.Current(exact)); Assert.Equal(0L, ColdAdmission(state));
            Assert.Equal(baselineWatches + 1, CommandWriteWatchCharges(state));
            var first = Assert.Single(lines);
            Assert.Contains("stage=ledger-prebyte-enrollment-first", first);
            Assert.Contains("reason=watch-enrollment-admission-busy", first);
            Assert.Contains("enrollment_admission=" + firstAdmission, first);
        }
        finally
        {
            if (writer is not null) state.End(writer);
            read?.Dispose(); publication?.Dispose();
            if (publication?.SuccessfulObservation is { } successor) state.Ledger.ReleaseMappedCopyWriteWatch(successor);
            if (readOriginal is not null) state.Ledger.ReleaseMappedCopyWriteWatch(readOriginal);
            if (result is not null) state.Ledger.ReleaseMappedCopyWriteWatch(result);
        }
        Assert.Equal(0, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("read-token")] [InlineData("protected-token")]
    [InlineData("session")] [InlineData("cold-owner")]
    public void ColdOriginalCallbackVetoLeavesFirstAdmissionHeldAndCopiesNoBytes(string owner)
    {
        using var state = new State(); Enroll(state); var held = state.Begin(State.Page, 1);
        var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray(); var calls = 0; var copies = 0;
        var originalReadToken = 41; var protectedToken = 17; var session = new object(); var coldOwner = new object();
        var readNow = originalReadToken; var protectedNow = protectedToken; var sessionNow = session; var coldNow = coldOwner;
        state.Hook(() => copies++);
        try
        {
            Assert.Equal(CpuMappedCopyResult.Rejected, ColdCopy(state, bytes, () =>
            {
                if (++calls == 2)
                {
                    Assert.Equal(1, state.Backing.ActiveAccesses);
                    if (owner == "read-token") readNow++;
                    if (owner == "protected-token") protectedNow++;
                    if (owner == "session") sessionNow = new object();
                    if (owner == "cold-owner") coldNow = new object();
                }
                return readNow == originalReadToken && protectedNow == protectedToken &&
                    ReferenceEquals(sessionNow, session) && ReferenceEquals(coldNow, coldOwner);
            }, out var rejected));
            ColdUnchanged(bytes, rejected); Assert.Equal(0, copies);
            Assert.Equal(1L, ColdAdmission(state)); Assert.Equal(0, CommandWriteWatchCharges(state));
            Assert.Equal(0, state.Backing.ActiveAccesses);
        }
        finally { state.End(held); }
        Assert.Equal(0L, ColdAdmission(state));
    }

    [Theory]
    [InlineData("ABA")] [InlineData("physical-remap")] [InlineData("rights")]
    [InlineData("pending-protection")] [InlineData("unknown-alias")] [InlineData("closure")]
    public void ColdEnrollmentNeverRecapturesAfterFirstMappingOrBackingIsLost(string mutation)
    {
        using var state = new State(); Enroll(state); var held = state.Begin(State.Page, 1);
        var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray(); var calls = 0; var copies = 0;
        state.Hook(() => copies++);
        try
        {
            Assert.Equal(CpuMappedCopyResult.Rejected, ColdCopy(state, bytes, () =>
            {
                if (++calls == 2)
                {
                    Assert.Equal(1, state.Backing.ActiveAccesses);
                    if (mutation == "ABA") state.Map(State.Address, 0);
                    if (mutation == "physical-remap") state.Map(State.Address, State.Page);
                    if (mutation is "rights" or "pending-protection")
                        lock (state.Gate)
                        {
                            var change = state.Ledger.PrepareProtectionChange(State.Address, State.Page, HostPageProtection.ReadOnly);
                            Assert.NotNull(change); Assert.True(state.Ledger.BeginProtectionChange(change, State.Address, State.Page));
                            if (mutation == "rights") state.Ledger.CompleteProtectionChange(change, nativeSuccess: true);
                        }
                    if (mutation == "unknown-alias") lock (state.Gate) state.Ledger.QuarantineUnknownAlias();
                    if (mutation == "closure") state.Backing.CloseAccess();
                    state.End(held); held = null!;
                }
                return true;
            }, out var rejected));
            ColdUnchanged(bytes, rejected); Assert.Equal(0, copies);
            Assert.Equal(0, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
        }
        finally { if (held is not null) state.End(held); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ColdWaitTimesOutWithoutForceReleasingAnOrdinaryOrSelfHeldReadTicket(bool readTicket)
    {
        using var state = new State(); Enroll(state);
        object? writer = null; CpuMappedReadValidation? read = null; CpuMappedCopyObservation? old = null;
        if (readTicket)
        {
            old = state.Capture();
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedReadValidation(state.Backing, old, out read));
            Assert.NotNull(read);
        }
        else writer = state.Begin(State.Page, 1);
        var baseline = state.Backing.ActiveAccesses; var watches = CommandWriteWatchCharges(state);
        var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray(); var copies = 0;
        var lines = new List<string>(); state.Hook(() => copies++);
        using var trace = new ColdTrace(line =>
        { Assert.False(Monitor.IsEntered(state.Gate)); Assert.Equal(baseline, state.Backing.ActiveAccesses); lines.Add(line); });
        try
        {
            Assert.Equal(CpuMappedCopyResult.Rejected, ColdCopy(state, bytes, () => true, out var rejected));
            ColdUnchanged(bytes, rejected); Assert.Equal(0, copies);
            Assert.Equal(readTicket ? 4294967296L : 1L, ColdAdmission(state));
            Assert.Equal(watches, CommandWriteWatchCharges(state)); Assert.Equal(baseline, state.Backing.ActiveAccesses);
            Assert.Equal(2, lines.Count);
            Assert.Contains("stage=ledger-prebyte-enrollment-first", lines[0]);
            Assert.Contains("reason=prebyte-admission-timeout", lines[1]);
            if (readTicket) { Assert.True(read!.IsOriginalCurrent(old!)); Assert.False(state.Current(old!)); }
        }
        finally
        {
            if (writer is not null) state.End(writer);
            read?.Dispose(); if (old is not null) state.Ledger.ReleaseMappedCopyWriteWatch(old);
        }
        Assert.Equal(0L, ColdAdmission(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Fact]
    public void SyntheticLostReadValidationLinkTimesOutWithoutBorrowingOrReleasingItsExactReservation()
    {
        using var state = new State(); var original = state.Capture();
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedReadValidation(state.Backing, original, out var validation));
        using var exact = Assert.IsType<CpuMappedReadValidation>(validation);
        var link = typeof(HostMappedCopyLedger).GetField("readValidations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object firstLink;
        lock (state.Gate) firstLink = link.GetValue(state.Ledger)!;
        Assert.NotNull(firstLink); Assert.Equal(link.FieldType, firstLink.GetType());
        var consumed = firstLink.GetType().GetField("consumed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var copies = 0; var lines = new List<string>(); var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray();
        state.Hook(() => copies++);
        using var trace = new ColdTrace(line =>
        { Assert.False(Monitor.IsEntered(state.Gate)); Assert.Equal(1, state.Backing.ActiveAccesses); lines.Add(line); });
        try
        {
            // Labelled synthetic bookkeeping loss ONLY. The actual ordinary
            // exclusion, access pin and unchanged original receipt stay live.
            // No native unknown-effects outcome is manufactured or claimed.
            lock (state.Gate) link.SetValue(state.Ledger, null);
            Assert.Equal(CpuMappedCopyResult.Rejected, ColdCopy(state, bytes, () => true, out var rejected));
            ColdUnchanged(bytes, rejected); Assert.Equal(0, copies);
            Assert.Equal(4294967296L, ColdAdmission(state));
            Assert.Equal(1, state.Backing.ActiveAccesses); Assert.Equal(1, CommandWriteWatchCharges(state));
            Assert.Equal(0, (int)consumed.GetValue(firstLink)!);
            lock (state.Gate) Assert.Null(link.GetValue(state.Ledger));
            Assert.False(state.Current(original));
            Assert.Equal(2, lines.Count);
            Assert.Contains("stage=ledger-prebyte-enrollment-first", lines[0]);
            Assert.Contains("enrollment_admission=4294967296", lines[0]);
            Assert.Contains("reason=prebyte-admission-timeout", lines[1]);
        }
        finally
        {
            // Fixture teardown restores only its exact unchanged link. The
            // ordinary original receipt then performs its normal one-use
            // cancellation; do not zero admissions/pins or reset consumed.
            lock (state.Gate) link.SetValue(state.Ledger, firstLink);
            try { exact.Dispose(); }
            finally { state.Ledger.ReleaseMappedCopyWriteWatch(original); }
        }
        Assert.Equal(0L, ColdAdmission(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
        Assert.Equal(0, CommandWriteWatchCharges(state));
    }

    [Theory]
    [InlineData("coverage")] [InlineData("capacity")] [InlineData("gate")] [InlineData("first-veto")]
    public void ColdWaitNeverTurnsUnavailableProofOrSelfHeldProviderGateIntoCurrent(string refusal)
    {
        using var state = new State(); Enroll(state); var originals = new List<CpuMappedCopyObservation>();
        if (refusal == "coverage") state.End(state.Begin(state.Backing.Size + 1, 1));
        if (refusal == "capacity") for (var index = 0; index < 128; index++) originals.Add(state.Capture());
        var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray(); var copies = 0; var calls = 0;
        state.Hook(() => copies++);
        try
        {
            CpuMappedCopyResult result; CpuMappedCopyObservation? rejected;
            if (refusal == "gate")
                lock (state.Gate)
                {
                    Assert.False(state.Ledger.CanWaitForMappedReadWatchEnrollment);
                    result = ColdCopy(state, bytes, () => { calls++; return true; }, out rejected);
                }
            else result = ColdCopy(state, bytes, () => { calls++; return refusal != "first-veto"; }, out rejected);
            Assert.Equal(CpuMappedCopyResult.Rejected, result); ColdUnchanged(bytes, rejected);
            Assert.Equal(0, copies); Assert.Equal(refusal == "gate" ? 0 : 1, calls);
            Assert.Equal(originals.Count, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
        }
        finally { foreach (var original in originals) state.Ledger.ReleaseMappedCopyWriteWatch(original); }
    }

    [Theory]
    [InlineData("watch-overlap")] [InlineData("mapping")] [InlineData("callback")]
    public void ColdCopyPostByteFailureDoesNotRepeatBytesOrEnrollAYoungerOriginal(string failure)
    {
        using var state = new State(); Enroll(state);
        new Span<byte>((void*)(state.Backing.AliasBase + 32), 16).Fill(0x31);
        var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray(); var copies = 0; var afterHookQueries = 0;
        state.Hook(() => copies++);
        Assert.Equal(CpuMappedCopyResult.Rejected, ColdCopy(state, bytes, () =>
        {
            if (copies == 1 && ++afterHookQueries == 2)
            {
                Assert.All(bytes, value => Assert.Equal((byte)0x31, value));
                // Change actual bytes only AFTER the first copy. A forbidden
                // refresh/retry would now overwrite the destination with 0x72.
                var offset = failure == "mapping" ? State.Page + 32 : 32;
                if (failure == "mapping") state.Map(State.Address, State.Page);
                var write = state.Begin(offset, 16);
                new Span<byte>((void*)(state.Backing.AliasBase + offset), 16).Fill(0x72); state.End(write);
                return failure != "callback";
            }
            return true;
        }, out var rejected));
        Assert.Null(rejected); Assert.Equal(1, copies); Assert.Equal(2, afterHookQueries);
        Assert.All(bytes, value => Assert.Equal((byte)0x31, value));
        Assert.Equal(0, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ColdCopyRejectsProviderGateAcquiredInsideOriginalCallbackWithoutRetrying(bool beforeBytes)
    {
        using var state = new State(); Enroll(state);
        new Span<byte>((void*)(state.Backing.AliasBase + 32), 16).Fill(0x31);
        var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray(); var copies = 0; var afterHookQueries = 0;
        var entered = false; state.Hook(() => copies++);
        try
        {
            Assert.Equal(CpuMappedCopyResult.Rejected, ColdCopy(state, bytes, () =>
            {
                if (copies == 1 && ++afterHookQueries == (beforeBytes ? 1 : 2))
                {
                    Assert.All(bytes, value => Assert.Equal(beforeBytes ? (byte)0xad : (byte)0x31, value));
                    // A caller callback changes the calling-thread capability.
                    // Teardown alone owns release of this deliberately entered
                    // gate; production must not enter it or force-release it.
                    Monitor.Enter(state.Gate); entered = true;
                }
                return true;
            }, out var rejected));
            Assert.Null(rejected); Assert.True(entered); Assert.False(state.Ledger.CanWaitForMappedReadWatchEnrollment);
            Assert.Equal(1, copies); Assert.Equal(beforeBytes ? 1 : 2, afterHookQueries);
            Assert.All(bytes, value => Assert.Equal(beforeBytes ? (byte)0xad : (byte)0x31, value));
            Assert.Equal(0, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
        }
        finally { if (entered) Monitor.Exit(state.Gate); }
        Assert.True(state.Ledger.CanWaitForMappedReadWatchEnrollment);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ColdCallbackExceptionUnwindsItsPrivateWatchChargeAndPinWithoutReleasingWriter(bool duringWait)
    {
        using var state = new State(); Enroll(state); object? held = duringWait ? state.Begin(State.Page, 1) : null;
        var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray(); var calls = 0; var cause = new IOException("cold owner query");
        try
        {
            Assert.Same(cause, Assert.Throws<IOException>(() => ColdCopy(state, bytes, () =>
            { if (++calls == (duringWait ? 2 : 1)) throw cause; return true; }, out _)));
            Assert.All(bytes, value => Assert.Equal((byte)0xad, value));
            Assert.Equal(0, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
            Assert.Equal(duringWait ? 1L : 0L, ColdAdmission(state));
        }
        finally { if (held is not null) state.End(held); }
    }

    private sealed class LegacyColdCpu : ICpuMappedReadWatchSource
    {
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation) =>
            throw new InvalidOperationException("Generic copy cannot substitute for explicit cold capability.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation) =>
            throw new InvalidOperationException("Legacy watched copy cannot substitute for explicit cold capability.");
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => false;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation) => false;
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation) => throw new InvalidOperationException();
    }

    private sealed class LegacyColdHost : IHostMappedReadWatchMemory
    {
        public CpuMappedCopyResult TryReadMappedCopy(HostBackingObject backing, ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation) =>
            throw new InvalidOperationException("Generic copy cannot substitute for explicit cold capability.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(HostBackingObject backing, ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation) =>
            throw new InvalidOperationException("Legacy watched copy cannot substitute for explicit cold capability.");
        public bool IsMappedCopyObservationCurrent(HostBackingObject backing, CpuMappedCopyObservation observation) => false;
        public bool IsMappedCopyWriteWatchCurrent(HostBackingObject backing, CpuMappedCopyObservation observation) => false;
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownCpuAndHostWrappersDeclineColdWaitWithoutCallbackOrLegacyCopy(bool hostWrapper)
    {
        using var state = new State(); var bytes = Enumerable.Repeat((byte)0xad, 16).ToArray();
        CpuMappedCopyResult result; CpuMappedCopyObservation? rejected;
        bool Veto() => throw new InvalidOperationException("Unknown capability cannot invoke an original-owner callback.");
        if (hostWrapper)
        {
            IHostMappedReadWatchMemory source = new LegacyColdHost();
            Assert.False(source.CanWaitForMappedReadWatchEnrollment);
            result = source.TryReadMappedCopyWithWriteWatchAdmissionWait(state.Backing,
                State.Address, State.Page, State.Address + 32, bytes, Veto, out rejected);
        }
        else
        {
            ICpuMappedReadWatchSource source = new LegacyColdCpu();
            Assert.False(source.CanWaitForMappedReadWatchEnrollment);
            result = source.TryReadMappedCopyWithWriteWatchAdmissionWait(
                State.Address, State.Page, State.Address + 32, bytes, Veto, out rejected);
        }
        Assert.Equal(CpuMappedCopyResult.Unavailable, result); ColdUnchanged(bytes, rejected);
        Assert.Equal(0, state.Backing.ActiveAccesses); Assert.Equal(0, CommandWriteWatchCharges(state));
    }
}
