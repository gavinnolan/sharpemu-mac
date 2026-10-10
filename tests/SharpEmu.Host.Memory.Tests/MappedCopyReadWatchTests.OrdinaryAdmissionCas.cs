// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// Actual ledger/watch/reservation protocols over pinned managed bytes. These
// controls supply no native permissions, syscall outcomes or fault authority.
public sealed unsafe partial class MappedCopyReadWatchTests
{
    private const long AdmissionCasPublicationBit = 1L << 32;
    private const BindingFlags AdmissionCasMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object AdmissionCasWriters(State state) => typeof(HostBackingObject)
        .GetField("_managedWrites", AdmissionCasMembers)!.GetValue(state.Backing)!;
    private static long AdmissionCasCount(object writers, string field) =>
        (long)writers.GetType().GetField(field, AdmissionCasMembers)!.GetValue(writers)!;
    private static long AdmissionCasRejected(object token) =>
        (long)token.GetType().GetProperty("RejectedAdmission", AdmissionCasMembers)!.GetValue(token)!;
    private static bool AdmissionCasBusy(object token) =>
        (bool)token.GetType().GetProperty("IsPublicationBusy", AdmissionCasMembers)!.GetValue(token)!;
    private static bool AdmissionCasAllowsBytes(object token) =>
        (bool)token.GetType().DeclaringType!.GetMethod("AllowsBytes", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [token])!;
    private static bool AdmissionCasEnd(object writers, object token, bool succeeded) =>
        (bool)writers.GetType().GetMethod("End", AdmissionCasMembers)!.Invoke(writers, [token, succeeded])!;
    private static long[] AdmissionCasCounters(object writers) =>
        new[] { "nextIdentity", "active", "begun", "completed", "aborted" }
            .Select(field => AdmissionCasCount(writers, field)).ToArray();

    [Theory]
    [InlineData("read", false)] [InlineData("read", true)]
    [InlineData("validation", false)] [InlineData("validation", true)]
    [InlineData("publication", false)] [InlineData("publication", true)]
    public void RejectedWriterCannotPerturbHeldOriginalWatchReservation(string kind, bool operation)
    {
        using var state = new State();
        var watch = state.Capture();
        var segments = watch.Segments.ToArray();
        var writers = AdmissionCasWriters(state);
        var before = new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, (int)state.Backing.Size).ToArray();
        CpuMappedReadValidation? read = null;
        CpuMappedPublicationValidation? validation = null;
        CpuMappedPublication? publication = null;
        try
        {
            Func<bool> exactCurrent;
            if (kind == "read")
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    state.Ledger.TryPrepareMappedReadValidation(state.Backing, watch, out read));
                Assert.NotNull(read); exactCurrent = () => read.IsOriginalCurrent(watch);
            }
            else
            {
                var original = PublicationOriginal(state, State.Address + State.Page, 32);
                if (kind == "validation")
                {
                    Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                        state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out validation));
                    Assert.NotNull(validation); exactCurrent = () => validation.IsOriginalDisjointReadWatchCurrent(watch);
                }
                else
                {
                    publication = Prepare(state, original, original.SourceAddress);
                    exactCurrent = () => publication.IsOriginalDisjointReadWatchCurrent(watch);
                }
            }
            Assert.True(exactCurrent());
            Assert.Equal(AdmissionCasPublicationBit, AdmissionCasCount(writers, "admission"));
            Assert.False(state.Current(watch)); // ordinary guard remains strict during exclusion
            var counters = AdmissionCasCounters(writers);
            for (var attempt = 0; attempt < 16; attempt++)
            {
                object rejected;
                lock (state.Gate) // actual outer provider gate: no wait or callback may be added to admission
                    rejected = operation ? state.Envelope() : state.Begin(32, 1);
                Assert.False(AdmissionCasAllowsBytes(rejected)); Assert.True(AdmissionCasBusy(rejected));
                // Deterministic parent red control: Increment/Decrement returns
                // Bit+1 here even though no bytes/identity/history were admitted.
                Assert.Equal(AdmissionCasPublicationBit, AdmissionCasRejected(rejected));
                Assert.False(AdmissionCasEnd(writers, rejected, false)); // no owned admission to decrement
                Assert.Equal(AdmissionCasPublicationBit, AdmissionCasCount(writers, "admission"));
                Assert.Equal(counters, AdmissionCasCounters(writers));
                Assert.True(exactCurrent());
                Assert.Equal(segments, watch.Segments.ToArray());
                Assert.Equal(before, new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, (int)state.Backing.Size).ToArray());
            }
            // Cancel the ORIGINAL reservation, without recapture or byte replay.
            read?.Dispose(); validation?.Dispose(); publication?.Dispose();
            Assert.False(exactCurrent()); Assert.Equal(0, AdmissionCasCount(writers, "admission"));
            Assert.True(state.Current(watch));
            var admitted = state.Begin(32, 1);
            var wrote = false;
            try
            {
                Assert.True(AdmissionCasAllowsBytes(admitted)); Assert.False(AdmissionCasBusy(admitted));
                Assert.Equal(1, AdmissionCasCount(writers, "admission"));
                Assert.False(state.Current(watch)); // actual admission revokes BEFORE the destination byte
                Assert.Equal(before[32], *((byte*)state.Backing.AliasBase + 32));
                *((byte*)state.Backing.AliasBase + 32) = 0xA5; wrote = true;
            }
            finally { Assert.True(AdmissionCasEnd(writers, admitted, wrote)); }
            var expected = before.ToArray(); expected[32] = 0xA5;
            Assert.Equal(expected, new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, (int)state.Backing.Size).ToArray());
            Assert.Equal(0, AdmissionCasCount(writers, "admission"));
            Assert.False(state.Current(watch)); // released publisher cannot repair the original revoked watch
            Assert.Equal(segments, watch.Segments.ToArray());
        }
        finally
        {
            try { read?.Dispose(); }
            finally
            {
                try { validation?.Dispose(); }
                finally
                {
                    try { publication?.Dispose(); }
                    finally { state.Ledger.ReleaseMappedCopyWriteWatch(watch); }
                }
            }
        }
        Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData(-1L, false)] [InlineData(-1L, true)]
    [InlineData(long.MinValue, false)] [InlineData(long.MinValue, true)]
    [InlineData(4294967295L, false)] [InlineData(4294967295L, true)]
    public void InvalidOrCeilingOrdinaryCountRefusesBytesWithoutInventingPublication(long count, bool operation)
    {
        using var state = new State(); var watch = state.Capture();
        var writers = AdmissionCasWriters(state);
        var field = writers.GetType().GetField("admission", AdmissionCasMembers)!;
        var saved = AdmissionCasCount(writers, "admission");
        var before = new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, (int)state.Backing.Size).ToArray();
        var counters = AdmissionCasCounters(writers);
        object? token = null;
        try
        {
            // Mutable scalar metadata controls ONLY. No real publication,
            // native mapping, permission, original receipt or authority is forged.
            field.SetValue(writers, count);
            token = operation ? state.Envelope() : state.Begin(32, 1);
            Assert.False(AdmissionCasAllowsBytes(token)); Assert.False(AdmissionCasBusy(token));
            Assert.Equal(count, AdmissionCasRejected(token));
            Assert.False(AdmissionCasEnd(writers, token, false));
            Assert.Equal(count, AdmissionCasCount(writers, "admission"));
            Assert.Equal(counters, AdmissionCasCounters(writers));
            Assert.Equal(before, new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, (int)state.Backing.Size).ToArray());
        }
        finally
        {
            try
            {
                // Preserve sole cleanup if a parent red-control unexpectedly
                // admitted an ordinary token; never repeat destination bytes.
                if (token is not null && AdmissionCasAllowsBytes(token))
                    _ = AdmissionCasEnd(writers, token, false);
            }
            finally
            {
                field.SetValue(writers, saved); // restore only injected managed metadata
                state.Ledger.ReleaseMappedCopyWriteWatch(watch);
            }
        }
        Assert.Equal(0, state.Backing.ActiveAccesses);
    }
}
