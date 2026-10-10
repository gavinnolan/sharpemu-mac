// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    [Theory]
    [InlineData("current", false)] [InlineData("current", true)]
    [InlineData("ordinary-one", false)] [InlineData("ordinary-one", true)]
    [InlineData("ordinary-two", false)] [InlineData("ordinary-two", true)]
    [InlineData("revoked", false)] [InlineData("revoked", true)]
    [InlineData("coverage", false)] [InlineData("coverage", true)]
    [InlineData("publication", false)] [InlineData("publication", true)]
    [InlineData("validation", false)] [InlineData("validation", true)]
    [InlineData("original-read", false)] [InlineData("original-read", true)]
    [InlineData("unknown-owner", false)] [InlineData("unknown-owner", true)]
    [InlineData("ends-after-refusal", false)] [InlineData("ends-after-refusal", true)]
    public void PostCopyFirstWatchDiagnosticPreservesBytesResultPrivateWatchAndExactOwner(string kind, bool diagnose)
    {
        var owners = new PostCopyDiagnosticOwners(); Exception? primary = null;
        try
        {
            var state = owners.Memory; EnableOwnerDiagnostics(state, kind != "unknown-owner");
            var expected = Enumerable.Range(0, 16).Select(i => (byte)(31 + i * 7)).ToArray();
            expected.CopyTo(new Span<byte>((void*)(state.Backing.AliasBase + 32), 16));
            var other = PublicationOriginal(state, State.Address + State.Page, 16);
            if (kind == "original-read")
            {
                Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                    State.Address + State.Page, State.Page, State.Address + State.Page, new byte[16], out owners.ReadOriginal));
                Assert.NotNull(owners.ReadOriginal);
            }
            var baselineWatches = CommandWriteWatchCharges(state); var copies = 0;
            var lines = new List<string>();
            owners.Trace = new PostCopyTraceControl(diagnose, line =>
            {
                Assert.False(Monitor.IsEntered(state.Gate));
                Assert.Equal(owners.Receipt is null ? 0 : 1, state.Backing.ActiveAccesses);
                Assert.Equal(baselineWatches, CommandWriteWatchCharges(state));
                lines.Add(line);
                if (kind == "ends-after-refusal") owners.EndWriters(); // AFTER the actual query and private cleanup
            });
            state.Hook(() =>
            {
                copies++; Assert.Equal(1, copies); Assert.Equal(baselineWatches + 1, CommandWriteWatchCharges(state));
                switch (kind)
                {
                    case "ordinary-one": case "ends-after-refusal": owners.Writers.Add(state.Begin(State.Page, 1)); break;
                    case "ordinary-two": owners.Writers.Add(state.Begin(State.Page, 1)); owners.Writers.Add(state.Begin(State.Page + 8, 1)); break;
                    case "revoked": state.End(state.Begin(32, 1)); break;
                    case "coverage": state.End(state.Begin(state.Backing.Size + 1, 1)); break; // real invalid-bound sticky coverage, no bytes
                    case "publication": case "unknown-owner":
                        var publicationResult = state.Ledger.TryPrepareMappedPublication(state.Backing, other,
                            [new(other.SourceAddress, new byte[] { 0x71 })], out var publication);
                        owners.Receipt = publication; // own actual return before any assertion
                        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, publicationResult);
                        Assert.IsType<CpuMappedPublication>(owners.Receipt); break;
                    case "validation":
                        var validationResult = state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, other, out var validation);
                        owners.Receipt = validation;
                        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, validationResult);
                        Assert.IsType<CpuMappedPublicationValidation>(owners.Receipt); break;
                    case "original-read":
                        var readResult = state.Ledger.TryPrepareMappedReadValidation(state.Backing, owners.ReadOriginal!, out var read);
                        owners.Receipt = read;
                        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, readResult);
                        Assert.IsType<CpuMappedReadValidation>(owners.Receipt); break;
                }
            });
            var destination = Enumerable.Repeat((byte)0xad, 16).ToArray();
            var result = state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                State.Address, State.Page, State.Address + 32, destination, out owners.Published);
            Assert.Equal(expected, destination); // the same full alias bytes were copied once, no fresh copy
            Assert.Equal(1, copies);
            if (kind == "current")
            {
                Assert.Equal(CpuMappedCopyResult.Copied, result); Assert.NotNull(owners.Published);
                Assert.Empty(lines); Assert.Equal(baselineWatches + 1, CommandWriteWatchCharges(state));
            }
            else
            {
                Assert.Equal(CpuMappedCopyResult.Rejected, result); Assert.Null(owners.Published);
                Assert.Equal(baselineWatches, CommandWriteWatchCharges(state));
                if (!diagnose) Assert.Empty(lines);
                else
                {
                    var line = Assert.Single(lines); Assert.Contains("reason=post-copy-original-watch result=Rejected", line);
                    var guard = kind == "revoked" ? "revoked" : kind == "coverage" ? "coverage-incomplete" : "admission-nonzero";
                    Assert.Contains($"first_post_copy_watch_guard={guard}", line);
                    var admission = kind is "revoked" or "coverage" ? "unobserved" : kind == "ordinary-two" ? "2" :
                        kind is "ordinary-one" or "ends-after-refusal" ? "1" : "4294967296";
                    Assert.Contains($"first_post_copy_admission={admission} ", line);
                    Assert.Contains("first_post_copy_attribution_is_authority=False", line);
                    if (kind is "publication" or "validation" or "original-read")
                    {
                        var actual = ReceiptOwner(owners.Receipt!);
                        var expectedKind = kind == "publication" ? "Publication" : kind == "validation" ? "PublicationValidation" : "OriginalReadValidation";
                        Assert.Contains($"first_post_copy_owner_identity={OwnerIdentity(actual)} ", line);
                        Assert.Contains($"first_post_copy_owner_kind={expectedKind} ", line);
                        Assert.Contains($"first_post_copy_owner_thread={Environment.CurrentManagedThreadId} ", line);
                        Assert.Contains("first_post_copy_owner_phase=Prepared ", line);
                        Assert.Contains("first_post_copy_owner_outcome=None ", line);
                        Assert.Contains("first_post_copy_owner_refusal=diagnostic-only-no-wait-authority ", line);
                    }
                    else if (kind == "unknown-owner")
                    { Assert.Contains("first_post_copy_owner_identity=unobserved ", line); Assert.Contains("first_post_copy_owner_refusal=owner-gap ", line); }
                    else Assert.Contains("first_post_copy_owner_identity=unobserved ", line);
                    if (kind == "ends-after-refusal") Assert.Equal(0L, ColdAdmission(state)); // detached first admission remains 1
                }
            }
            Assert.Equal(owners.Receipt is null ? 0 : 1, state.Backing.ActiveAccesses);
        }
        catch (Exception error) { primary = error; throw; }
        finally { FinishPostCopyOwners(owners, primary); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PostCopyMappingFirstRefusalCarriesNoWatchAttribution(bool diagnose)
    {
        var owners = new PostCopyDiagnosticOwners(); Exception? primary = null;
        try
        {
            var state = owners.Memory; var lines = new List<string>(); var copies = 0;
            owners.Trace = new PostCopyTraceControl(diagnose, lines.Add);
            state.Hook(() => { copies++; state.Map(State.Address, 0); });
            var destination = Enumerable.Repeat((byte)0xad, 16).ToArray();
            Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                State.Address, State.Page, State.Address + 32, destination, out owners.Published));
            Assert.Null(owners.Published); Assert.Equal(new byte[16], destination); Assert.Equal(1, copies);
            Assert.Equal(0, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
            if (diagnose) { var line = Assert.Single(lines); Assert.Contains("reason=post-copy-original-mapping", line); Assert.DoesNotContain("first_post_copy_watch_guard", line); }
            else Assert.Empty(lines);
        }
        catch (Exception error) { primary = error; throw; }
        finally { FinishPostCopyOwners(owners, primary); }
    }

    [Fact]
    public void ThrowingPostCopyDiagnosticSinkCannotChangeRejectionOrPrivateCleanup()
    {
        var owners = new PostCopyDiagnosticOwners(); Exception? primary = null;
        try
        {
            var state = owners.Memory; var calls = 0;
            owners.Trace = new PostCopyTraceControl(true, _ => { calls++; throw new IOException("diagnostic sink only"); });
            state.Hook(() => owners.Writers.Add(state.Begin(State.Page, 1)));
            Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                State.Address, State.Page, State.Address + 32, new byte[16], out owners.Published));
            Assert.Null(owners.Published); Assert.Equal(1, calls); Assert.Equal(0, CommandWriteWatchCharges(state));
            Assert.Equal(0, state.Backing.ActiveAccesses); Assert.Equal(1L, ColdAdmission(state));
        }
        catch (Exception error) { primary = error; throw; }
        finally { FinishPostCopyOwners(owners, primary); }
    }

    private static void FinishPostCopyOwners(PostCopyDiagnosticOwners owners, Exception? primary)
    {
        try { owners.Dispose(); }
        catch (Exception cleanup)
        { if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw(); else try { primary.Data["PostCopyDiagnosticCleanup"] = cleanup; } catch { } }
    }

    private sealed class PostCopyDiagnosticOwners : IDisposable
    {
        internal readonly State Memory;
        internal readonly List<object> Writers = new(2);
        internal CpuMappedCopyObservation? Published, ReadOriginal;
        internal IDisposable? Receipt;
        internal PostCopyTraceControl? Trace;
        private readonly object?[] unknown = new object?[5];
        private static readonly List<object> UnknownOwners = [];
        private bool disposed;
        internal PostCopyDiagnosticOwners() { Memory = new State(); } // all containers allocated before the pin
        internal void EndWriters()
        {
            Exception? first = null;
            for (var i = 0; i < Writers.Count; i++)
            {
                var exact = Writers[i]; if (exact is null) continue;
                Writers[i] = null!; unknown[i] = exact;
                try { Memory.End(exact); unknown[i] = null; } catch (Exception error) { first ??= error; }
            }
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; Exception? first = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { first ??= error; } }
            Attempt(() => Memory.Hook(null)); Attempt(EndWriters);
            if (Receipt is { } exact) { Receipt = null; unknown[2] = exact; Attempt(() => { exact.Dispose(); unknown[2] = null; }); }
            if (Published is { } published) { Published = null; unknown[3] = published; Attempt(() => { Memory.Ledger.ReleaseMappedCopyWriteWatch(published); unknown[3] = null; }); }
            if (ReadOriginal is { } read) { ReadOriginal = null; unknown[4] = read; Attempt(() => { Memory.Ledger.ReleaseMappedCopyWriteWatch(read); unknown[4] = null; }); }
            Attempt(() => Trace?.Dispose());
            if (unknown.Any(value => value is not null)) first ??= new IOException("Exact consumed managed cleanup remains unknown.");
            if (first is null) Attempt(Memory.Dispose);
            if (first is not null) { lock (UnknownOwners) UnknownOwners.Add(this); ExceptionDispatchInfo.Capture(first).Throw(); }
        }
    }

    private sealed class PostCopyTraceControl : IDisposable
    {
        private static readonly Type Type = typeof(HostMappedCopyLedger).Assembly.GetType("SharpEmu.HLE.CpuMappedCopyDiagnostics", true)!;
        private static readonly FieldInfo Enabled = Type.GetField("enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo Rows = Type.GetField("rows", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo Sink = Type.GetField("sinkForControl", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? enabled = Enabled.GetValue(null), rows = Rows.GetValue(null), sink = Sink.GetValue(null);
        private bool disposed;
        private static readonly List<object> UnknownTraceOwners = [];
        internal PostCopyTraceControl(bool enabled, Action<string> sink)
        {
            try { Enabled.SetValue(null, enabled); Rows.SetValue(null, 0); Sink.SetValue(null, sink); }
            catch (Exception primary) { try { Dispose(); } catch (Exception cleanup) { primary.Data["PostCopyTraceRestore"] = cleanup; } throw; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; Exception? first = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { first ??= error; } }
            Attempt(() => Sink.SetValue(null, sink)); Attempt(() => Rows.SetValue(null, rows)); Attempt(() => Enabled.SetValue(null, enabled));
            if (first is not null) { lock (UnknownTraceOwners) UnknownTraceOwners.Add(this); ExceptionDispatchInfo.Capture(first).Throw(); }
        }
    }
}
