// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void NativeColorUnknownActualOrderedActionRemainsRejectedWithoutCallbackOrAcknowledgment(bool taken, bool diagnostics)
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("host.default", 83);
        var target = ProjectBorrowColor(memory); SubmitBorrowColor(target);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        var producer = ((IDictionary)Field("_nativeColorLatest").GetValue(null)!)[target.Address]!;
        var calls = 0;
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => calls++, "original-eop-callback");
        Assert.True(sequence > 0);
        var pendingQueues = (IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!;
        var accepted = pendingQueues.Values.Cast<IEnumerable>().SelectMany(items => items.Cast<object>())
            .Single(item => (long)item.GetType().GetProperty("Sequence")!.GetValue(item)! == sequence);
        var work = accepted.GetType().GetProperty("Work")!.GetValue(accepted)!;
        var previousExecuting = Field("_executingGuestWorkSequence").GetValue(null);
        try
        {
            if (taken) { _ = TakeVolumeWork(); accepted = TakeVolumeWork(); }
            Field("_executingGuestWorkSequence").SetValue(null, sequence);
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Exception captured;
            lock (Field("_gate").GetValue(null)!)
            {
                var copies = memory.Copies; var queries = memory.MappingQueries;
                captured = (Exception)Presenter.GetMethod("NativeColorUnknownWorkFailure", Private)!
                    .Invoke(null, [work, taken ? null : accepted, producer, diagnostics])!;
                Assert.Equal(copies, memory.Copies); Assert.Equal(queries, memory.MappingQueries);
                Assert.Equal("Native color has an unknown accepted sibling writer.", captured.Message);
                if (diagnostics)
                {
                    var snapshot = captured.GetType().GetField("Snapshot", instance)!.GetValue(captured)!;
                    object? Value(string name) => snapshot.GetType().GetProperty(name)!.GetValue(snapshot);
                    Assert.Equal(taken ? "taken" : "pending", Value("Location"));
                    Assert.Equal("ordered-action", Value("Kind")); Assert.Equal("original-eop-callback", Value("DebugName"));
                    Assert.Equal(sequence, Value("AcceptedSequence")); Assert.Equal(sequence, Value("ExecutingSequence"));
                    Assert.Equal("host.default", Value("ExecutingQueue")); Assert.Equal(83UL, Value("ExecutingSubmission"));
                    Assert.Equal(target.Address, Value("ProducerAddress")); Assert.Equal(0x200000UL, Value("ProducerBytes"));
                    if (!taken) { Assert.Equal("host.default", Value("AcceptedQueue")); Assert.Equal(83UL, Value("AcceptedSubmission")); }
                }
                else Assert.IsType<NotSupportedException>(captured);
                var refusal = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireNativeColorCohortLocked", producer));
                Assert.IsAssignableFrom<NotSupportedException>(refusal.InnerException);
                Assert.Equal(captured.Message, refusal.InnerException!.Message);
            }
            Assert.Equal(0, calls); Assert.Equal(0, memory.PlainReads); Assert.Equal(0, memory.Releases);
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
            if (diagnostics)
            {
                var row = (string)Presenter.GetMethod("FormatNativeColorUnknownWork", Private)!.Invoke(null, [captured])!;
                Assert.Contains("reason=unknown-accepted-sibling-writer", row);
                Assert.Contains("debug_name=original-eop-callback", row);
                Assert.Contains($"accepted_sequence={sequence}", row);
                Assert.Equal(0, calls);
            }
        }
        finally
        {
            Field("_executingGuestWorkSequence").SetValue(null, previousExecuting);
            InvokeVolume("CompletePresenterShutdown");
        }
        Assert.Equal(0, calls); Assert.Equal(1, memory.Releases);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Empty((IDictionary)Field("_nativeColorQueued").GetValue(null)!);
    }
}
