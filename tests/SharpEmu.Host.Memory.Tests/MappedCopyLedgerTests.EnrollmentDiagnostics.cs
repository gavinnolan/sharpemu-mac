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
    [InlineData("writer", "watch-enrollment-admission-busy", 1L)]
    [InlineData("publication", "watch-enrollment-admission-busy", 4294967296L)]
    [InlineData("coverage", "watch-enrollment-coverage-incomplete", -1L)]
    [InlineData("slots", "watch-enrollment-slot-capacity", 0L)]
    public void EnrollmentDiagnosticRetainsFirstActualGuardAndUnwindsBeforeEmission(
        string variant, string reason, long admission)
    {
        using var state = new State(); state.Map();
        var ledger = state.Ledger; var gate = state.Gate;
        object? interval = null;
        CpuMappedPublicationValidation? publication = null;
        var watches = new List<CpuMappedCopyObservation>();
        var begin = typeof(HostBackingObject).GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            if (variant == "writer") interval = begin.Invoke(state.Backing, [State.Page, 1UL]);
            if (variant == "coverage") _ = begin.Invoke(state.Backing, [state.Backing.Size + 1, 1UL]);
            if (variant == "publication")
            {
                var original = state.Copy();
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out publication));
                Assert.NotNull(publication);
            }
            if (variant == "slots")
            {
                for (var i = 0; i < 128; i++)
                {
                    Assert.Equal(CpuMappedCopyResult.Copied, ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                        State.Address, State.Page, State.Address, new byte[1], out var watch));
                    watches.Add(Assert.IsType<CpuMappedCopyObservation>(watch));
                }
                // A second real ledger over this pinned backing reaches the
                // shared interval watch bound before its own copy-owner bound.
                // Synthetic metadata is not a native mapping receipt.
                gate = new object(); ledger = new HostMappedCopyLedger(gate, State.Page);
                lock (gate)
                {
                    ledger.RegisterBacking(state.Backing);
                    var view = ledger.PrepareView(state.Backing, State.Address, State.Page, 0, HostPageProtection.ReadWrite);
                    Assert.NotNull(view); Assert.True(ledger.BeforeNativeEffect(State.Address, State.Page));
                    ledger.PublishView(view);
                }
            }
            var messages = new List<string>();
            using var trace = new MappedCopyTraceControl(true, line =>
            {
                Assert.False(Monitor.IsEntered(gate));
                Assert.Equal(0, state.Backing.ActiveAccesses);
                messages.Add(line);
            });
            var bytes = new byte[] { 0xED };
            Assert.Equal(CpuMappedCopyResult.Rejected, ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                State.Address, State.Page, State.Address, bytes, out var rejected));
            Assert.Null(rejected); Assert.Equal(0xED, bytes[0]);
            var message = Assert.Single(messages);
            Assert.Contains("reason=" + reason + " result=Rejected", message);
            Assert.Contains("enrollment_admission=" + admission, message);
            Assert.Contains("copy_bytes=1 watched=True", message);
        }
        finally
        {
            publication?.Dispose();
            if (interval is not null) end.Invoke(null, [interval, true]);
            foreach (var watch in watches) state.Ledger.ReleaseMappedCopyWriteWatch(watch);
        }
    }
}
