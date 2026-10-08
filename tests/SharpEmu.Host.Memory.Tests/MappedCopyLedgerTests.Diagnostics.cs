// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyLedgerTests
{
    [Theory]
    [InlineData("geometry", "request-geometry")]
    [InlineData("missing", "no-view-at-cursor")]
    [InlineData("gap", "view-gap")]
    [InlineData("other", "view-other-backing")]
    [InlineData("unreadable", "view-not-readable")]
    [InlineData("pending", "view-copy-unavailable")]
    [InlineData("segments", "segment-bound")]
    [InlineData("raced", "post-copy-original-mapping")]
    public void FirstRefusalDiagnosticPreservesActualLedgerResultAndRunsAfterGateAndPin(string variant, string reason)
    {
        using var state = new State(); using var other = new State();
        var messages = new List<string>(); var heldGate = false; var livePin = -1;
        using var trace = new MappedCopyTraceControl(true, line =>
        { messages.Add(line); heldGate |= Monitor.IsEntered(state.Gate); livePin = state.Backing.ActiveAccesses; });
        var footprint = State.Page;
        if (variant is not ("geometry" or "missing" or "other"))
            state.Map(protection: variant == "unreadable" ? HostPageProtection.Execute : HostPageProtection.ReadWrite);
        switch (variant)
        {
            case "geometry": footprint = 0; break;
            case "gap": footprint *= 2; break;
            case "other":
                lock (state.Gate)
                {
                    state.Ledger.RegisterBacking(other.Backing);
                    var view = state.Ledger.PrepareView(other.Backing, State.Address, State.Page, 0, HostPageProtection.ReadWrite);
                    Assert.NotNull(view); Assert.True(state.Ledger.BeforeNativeEffect(State.Address, State.Page));
                    state.Ledger.PublishView(view); // synthetic metadata; no syscall/permission claim
                }
                break;
            case "pending":
                lock (state.Gate)
                {
                    var operation = state.Ledger.PrepareProtectionChange(State.Address, State.Page, HostPageProtection.ReadOnly);
                    Assert.NotNull(operation); Assert.True(state.Ledger.BeginProtectionChange(operation, State.Address, State.Page));
                    // Deliberately no successful completion receipt.
                }
                break;
            case "segments":
                for (var i = 1; i < 65; i++) state.Map(State.Address + (ulong)i * State.Page);
                footprint *= 65; break;
            case "raced": state.Hook(() =>
                { lock (state.Gate) Assert.True(state.Ledger.BeforeNativeEffect(State.Address, State.Page)); }); break;
        }
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, footprint, State.Address, new byte[1], out var observation));
        Assert.Null(observation); Assert.False(heldGate); Assert.Equal(0, livePin);
        var message = Assert.Single(messages); Assert.Contains("stage=ledger reason=" + reason, message);
        Assert.Contains("copy_bytes=1 watched=False", message);
        if (variant == "segments") Assert.Contains("segments=64", message);
    }

    [Fact]
    public void DisabledCopyDiagnosticsNeverTouchSinkOrCounter()
    {
        using var state = new State(); var called = false;
        using var trace = new MappedCopyTraceControl(false, _ => called = true);
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page, State.Address, new byte[1], out _));
        Assert.False(called); Assert.Equal(0, trace.RowCount);
    }

    [Fact]
    public void DiagnosticSinkFailureCannotReplaceOriginalRejectionOrCopyException()
    {
        using var state = new State();
        using var trace = new MappedCopyTraceControl(true, _ => throw new IOException("diagnostic sink"));
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page, State.Address, new byte[1], out _));
        state.Map(); var original = new IOException("original copy hook"); state.Hook(() => throw original);
        Assert.Same(original, Assert.Throws<IOException>(() => state.Ledger.TryReadMappedCopy(state.Backing,
            State.Address, State.Page, State.Address, new byte[1], out _)));
        Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Fact]
    public void DiagnosticRowBoundDoesNotChangeLaterRefusedCopyResults()
    {
        using var state = new State(); var lines = 0;
        using var trace = new MappedCopyTraceControl(true, _ => lines++);
        for (var i = 0; i < 70; i++)
            Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopy(state.Backing,
                State.Address, State.Page, State.Address, new byte[1], out _));
        Assert.Equal(64, lines); Assert.Equal(70, trace.RowCount);
    }

    // Assembly is serialized. Mutate only private diagnostic fields, restore
    // their exact values; no public acceptance/native authority seam is added.
    private sealed class MappedCopyTraceControl : IDisposable
    {
        private static readonly Type Type = typeof(HostMappedCopyLedger).Assembly.GetType("SharpEmu.HLE.CpuMappedCopyDiagnostics", true)!;
        private static readonly FieldInfo Enabled = Type.GetField("enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo Rows = Type.GetField("rows", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo Sink = Type.GetField("sinkForControl", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? previousEnabled = Enabled.GetValue(null), previousRows = Rows.GetValue(null), previousSink = Sink.GetValue(null);
        internal MappedCopyTraceControl(bool enabled, Action<string> sink)
        { Enabled.SetValue(null, enabled); Rows.SetValue(null, 0); Sink.SetValue(null, sink); }
        internal int RowCount => (int)Rows.GetValue(null)!;
        public void Dispose()
        { Sink.SetValue(null, previousSink); Rows.SetValue(null, previousRows); Enabled.SetValue(null, previousEnabled); }
    }
}
