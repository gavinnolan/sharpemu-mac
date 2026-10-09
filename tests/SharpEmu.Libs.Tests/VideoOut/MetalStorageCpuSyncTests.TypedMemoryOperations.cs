// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static object?[]? typedMemoryResetQuarantine;

    [Fact]
    public void FailedFirstCommandWatchCannotFallbackToPlainMappingCapture()
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var memory = new TypedColorMemory("watch-unavailable"); MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
        var operation = SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation.Write(TypedColorMemory.Destination, new byte[8]);
        var original = InvokeVolume("PrepareCommandMemoryOperation", host, operation, (Action)(() => throw new Exception("not invoked")))!;
        Assert.Null(original.GetType().GetProperty("Mapping")!.GetValue(original));
        Assert.Null(original.GetType().GetProperty("OriginalWatch")!.GetValue(original));
        Assert.Equal(1, memory.WatchedCommandCopies); Assert.Equal(0, memory.CommandCopies);
        Assert.Equal(0, memory.ReleasedCommandWatches); Assert.Equal(0, memory.Writes);
    }

    // Metadata lifetime only. Active uses actual Take, but never invokes the
    // saved callback; cleanup-unknown uses a synthetic release exception.
    [Theory]
    [InlineData("known")]
    [InlineData("out-of-order-known")]
    [InlineData("active")]
    [InlineData("pending-sequence-unknown")]
    [InlineData("failed-pending")]
    [InlineData("cleanup-unknown")]
    public void OriginalCommandWatchCleanupCannotReleaseActiveOrUncertainOwner(string variant)
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var memory = new TypedColorMemory("disjoint"); MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
        var operation = SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation.Write(TypedColorMemory.Destination, new byte[8]);
        var original = InvokeVolume("PrepareCommandMemoryOperation", host, operation, (Action)(() => throw new Exception("not invoked")))!;
        var root = Field("_uncertainCommandMemoryWrite"); var previousRoot = root.GetValue(null); Assert.Null(previousRoot);
        var primary = new IOException("original caller failure"); var cleanup = new IOException("synthetic unknown watch release");
        long sequence = 0;
        try
        {
            if (variant is "active" or "out-of-order-known" or "pending-sequence-unknown" or "failed-pending")
            {
                if (variant == "out-of-order-known")
                {
                    using var foreign = MetalVideoPresenter.EnterGuestQueue("unrun.foreign", 1);
                    MetalVideoPresenter.SubmitOrderedGuestAction(() => throw new Exception("not invoked"), "unrun prior queue");
                }
                lock (Field("_gate").GetValue(null)!)
                {
                    sequence = (long)InvokeVolume("EnqueueGuestWorkLocked", original)!;
                    if (variant == "out-of-order-known") Field("_pendingGuestQueueCursor").SetValue(null, 1);
                }
                if (variant == "pending-sequence-unknown") sequence = 0; // submitting caller never received the accepted sequence
                else if (variant == "failed-pending")
                {
                    MetalVideoPresenter.LatchGuestCompletionFailure("known untaken fixture cancellation");
                    object?[] refused = [null]; Assert.Equal(false, InvokeVolume("TryTakeGuestWork", refused));
                    Assert.Equal(0L, Field("_takenGuestWorkSequence").GetValue(null));
                }
                else
                {
                    object?[] taken = [null]; Assert.Equal(true, InvokeVolume("TryTakeGuestWork", taken));
                    if (variant == "out-of-order-known")
                    {
                        Assert.Equal(2L, sequence); InvokeVolume("CompleteGuestWork", taken[0]);
                        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
                        Assert.Contains(sequence, (HashSet<long>)Field("_completedGuestWorkOutOfOrder").GetValue(null)!);
                    }
                }
            }
            if (variant == "cleanup-unknown") memory.BeforeCommandRelease = () => throw cleanup;
            Assert.Same(primary, InvokeVolume("ReleaseOriginalCommandMemoryWatch", original, sequence, primary));
            if (variant is "active" or "pending-sequence-unknown") { Assert.Equal(0, memory.ReleasedCommandWatches); Assert.Same(original, root.GetValue(null)); }
            else
            {
                Assert.Equal(1, memory.ReleasedCommandWatches);
                Assert.Same(primary, InvokeVolume("ReleaseOriginalCommandMemoryWatch", original, sequence, primary));
                Assert.Equal(1, memory.ReleasedCommandWatches); // consumed-before-release, no retry
                if (variant == "cleanup-unknown")
                { Assert.Same(original, root.GetValue(null)); Assert.Same(cleanup, primary.Data["CommandMemoryOriginalWatchCleanup"]); }
                else Assert.Null(root.GetValue(null));
            }
            Assert.Equal(1, memory.WatchedCommandCopies); Assert.Equal(1, memory.CommandCopies);
            Assert.Equal(0, memory.Writes); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        }
        finally
        {
            // No callback, thread or native effect exists in these metadata
            // rows. Clear the actual taken record before disposing fake roots.
            InvokeVolume("CompletePresenterShutdown"); memory.BeforeCommandRelease = null;
            InvokeVolume("ReleaseOriginalCommandMemoryWatch", original, 0L, primary);
            root.SetValue(null, previousRoot);
        }
    }

    // Real admission/Take/Complete, metadata only: this callback validates the
    // exact original receipt without preparing a transaction or writing bytes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedOriginalReceiptRequiresActualRenderDrainScope(bool productionWrapper)
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        GpuFatal.OnFatal = _ => { };
        var memory = new TypedColorMemory("disjoint"); MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
        var operation = SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation.Write(TypedColorMemory.Destination, new byte[8]);
        object? original = null; var validated = false;
        Action inspect = () =>
        {
            try
            {
                lock (Field("_gate").GetValue(null)!) InvokeVolume("RequireCommandMemoryExecutionLocked", original);
                var mapping = original!.GetType().GetProperty("Mapping")!.GetValue(original)!;
                InvokeVolume("RequireCommandMemoryOriginal", original, mapping, true);
                validated = true;
            }
            catch (TargetInvocationException failure) when (failure.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw; }
        };
        original = InvokeVolume("PrepareCommandMemoryOperation", host, operation, inspect)!;
        Assert.NotNull(original.GetType().GetProperty("Mapping")!.GetValue(original));
        Assert.Equal(0, Field("_guestWorkDrainDepth").GetValue(null));
        try
        {
            lock (Field("_gate").GetValue(null)!) Assert.Equal(1L, InvokeVolume("EnqueueGuestWorkLocked", original));
            if (productionWrapper)
            {
                Assert.Equal(true, InvokeVolume("TryDrainGuestWork", (nint)0, (nint)0));
                Assert.True(validated); Assert.Equal(1L, Field("_completedGuestWorkSequence").GetValue(null));
            }
            else
            {
                var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("DrainGuestWorkCore",
                    (nint)0, (nint)0, 5L * System.Diagnostics.Stopwatch.Frequency));
                var fatal = Assert.IsType<GpuFatalException>(failure.InnerException);
                Assert.Contains("exact taken/executing original receipt", fatal.InnerException!.Message);
                Assert.False(validated); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
            }
            Assert.Equal(0, Field("_guestWorkDrainDepth").GetValue(null));
            Assert.Equal(0L, Field("_executingGuestWorkSequence").GetValue(null));
            Assert.Equal(0L, Field("_takenGuestWorkSequence").GetValue(null));
            Assert.Equal(1, memory.CommandCopies); Assert.Equal(0, memory.Writes);
        }
        finally { InvokeVolume("CompletePresenterShutdown"); }
    }

    [Fact]
    public void RetainedUncertainOriginalWriteRefusesPublicReadmissionAfterPresenterReset()
    {
        ActualArchitecture(); Assert.Null(typedMemoryResetQuarantine);
        var root = Field("_uncertainCommandMemoryWrite"); var previous = root.GetValue(null);
        var previousFatal = GpuFatal.OnFatal; Assert.Null(previous);
        GpuFatal.OnFatal = _ => { }; // real exception, no process FailFast in refusing controls
        object? retained = null;
        BorrowColorState? active = null; IDisposable? queue = null;
        TypedColorMemory? memory = null; Task<Exception?>? caller = null;
        var callerJoined = true; var closeAttempted = false; Exception? primary = null; Exception? cleanup = null;
        // Bookkeeping only: a real publicly admitted DTO supplies a fake
        // uncertain scope. No publication/native effect or unknown receipt is
        // fabricated. Preallocated roots precede caller effects and survive
        // uncertain join/cleanup without restoring presenter/root/observer.
        try
        {
            active = new BorrowColorState(); memory = new TypedColorMemory("disjoint");
            typedMemoryResetQuarantine = new object?[] { active, memory, null, null, null };
            MetalVideoPresenter.AttachGuestMemory(memory);
            queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7); typedMemoryResetQuarantine[2] = queue;
            var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
            callerJoined = false;
            caller = Task.Run(() =>
            {
                using var submitting = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
                return Record.Exception(() => host.TryWriteGuest(TypedColorMemory.Destination, new byte[8]));
            });
            typedMemoryResetQuarantine[3] = caller;
            var deadline = Environment.TickCount64 + 5000;
            while (retained is null)
            {
                lock (Field("_gate").GetValue(null)!)
                {
                    var queues = (IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!;
                    retained = queues.Values.Cast<IEnumerable>().SelectMany(items => items.Cast<object>())
                        .Select(item => item.GetType().GetProperty("Work")!.GetValue(item)!)
                        .SingleOrDefault(item => item.GetType().Name == "OrderedCommandMemoryOperation");
                    if (retained is not null) { root.SetValue(null, retained); typedMemoryResetQuarantine[4] = retained; }
                }
                Assert.False(caller.IsCompleted); Assert.True(Environment.TickCount64 < deadline); Thread.Yield();
            }
            Assert.Equal(1, memory.CommandCopies); Assert.Equal(0, memory.Writes);
            CloseActive(); // exact caller joins BEFORE any presenter reset
            if (cleanup is not null) throw cleanup;
            Assert.Same(retained, root.GetValue(null));

            closeAttempted = false;
            active = new BorrowColorState(); memory = new TypedColorMemory("disjoint"); caller = null;
            typedMemoryResetQuarantine = new object?[] { active, memory, null, null, retained };
            MetalVideoPresenter.AttachGuestMemory(memory);
            Field("_selectedStorageMipSession").SetValue(null, (long)Field("_selectedStorageMipSession").GetValue(null)! + 1);
            queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 8); typedMemoryResetQuarantine[2] = queue;
            backend = new MetalGuestGpuBackend(); host = new MetalCommandStreamHost(memory, backend, backend);
            var failure = Assert.IsType<GpuFatalException>(Record.Exception(
                () => host.TryWriteGuest(TypedColorMemory.Destination, new byte[8])));
            Assert.Contains("remains process-owned", failure.InnerException!.Message);
            Assert.Same(retained, root.GetValue(null)); Assert.Equal(1, memory.CommandCopies); Assert.Equal(0, memory.Writes);
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0L, Field("_enqueuedGuestWorkSequence").GetValue(null));
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            CloseActive();
            if (typedMemoryResetQuarantine is null)
            {
                Attempt(() => root.SetValue(null, previous)); // only the fake fixture scope
                Attempt(() => GpuFatal.OnFatal = previousFatal);
            }
            if (cleanup is not null)
            {
                if (primary is null) primary = cleanup;
                else try { primary.Data["TypedMemoryResetCleanup"] = cleanup; } catch { }
            }
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();

        void CloseActive()
        {
            if (active is null || closeAttempted) return;
            closeAttempted = true; // never repeat an uncertain join/shutdown/disposal attempt
            if (!callerJoined)
            {
                Attempt(() => MetalVideoPresenter.LatchGuestCompletionFailure("fake uncertain scope reset control"));
                try { callerJoined = caller is null || caller.Wait(2000); }
                catch (Exception failure) { callerJoined = caller?.IsCompleted == true; cleanup ??= failure; }
                if (!callerJoined) { cleanup ??= new TimeoutException("Original caller is still live; all fixture owners remain quarantined."); return; }
            }
            Attempt(() => InvokeVolume("CompletePresenterShutdown"));
            if (cleanup is not null) return; // incomplete shutdown cannot restore presenter or release its root
            Attempt(() => { Assert.Same(retained, root.GetValue(null)); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null)); });
            if (cleanup is not null) return;
            var ownedQueue = queue; queue = null; Attempt(() => ownedQueue?.Dispose());
            var ownedState = active; active = null; Attempt(ownedState.Dispose);
            if (cleanup is null) typedMemoryResetQuarantine = null;
        }
        void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
    }

    [Fact]
    public void ArbitraryCallbackCannotClaimClosedMemoryEffectsFromItsDebugName()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var target = ProjectBorrowColor(memory); SubmitBorrowColor(target);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        var producer = ((IDictionary)Field("_nativeColorLatest").GetValue(null)!)[target.Address]!;
        var invoked = false;
        MetalVideoPresenter.SubmitOrderedGuestAction(() => invoked = true, "command_stream memory operation");
        try
        {
            lock (Field("_gate").GetValue(null)!)
                Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(
                    () => InvokeVolume("RequireNativeColorCohortLocked", producer)).InnerException);
            Assert.False(invoked); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        }
        finally { InvokeVolume("CompletePresenterShutdown"); }
    }

    // Synthetic metadata only: the independent native row must prove actual
    // producer completion, retained texture contents and the one real write.
    [Theory]
    [InlineData("disjoint")]
    [InlineData("physical-alias")]
    [InlineData("tail-physical-alias")]
    [InlineData("missing-mapping")]
    [InlineData("wrong-queue")]
    [InlineData("stale-original")]
    [InlineData("rights-after-admission")]
    [InlineData("changed-session")]
    [InlineData("read-only")]
    [InlineData("saved-without-take")]
    public void NativeColorTypedMemoryInventoryUsesOnlyItsFirstCompleteOriginal(string variant)
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        GpuFatal.OnFatal = _ => { }; // State restores the original fatal observer after the caller joins
        var memory = new TypedColorMemory(variant);
        MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var words = BorrowColorWords;
        var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var target = MetalColorMipPlacement.Project(memory, normalized, words,
            new GuestRenderTarget(Address, 512, 512, (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27));
        SubmitBorrowColor(target); MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        var producer = ((IDictionary)Field("_nativeColorLatest").GetValue(null)!)[target.Address]!;
        var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
        var caller = Task.Run(() =>
        {
            using var submitting = MetalVideoPresenter.EnterGuestQueue(variant == "wrong-queue" ? "foreign.typed" : "typed.color", 7);
            return Record.Exception(() => host.TryWriteGuest(TypedColorMemory.Destination,
                variant == "tail-physical-alias" ? new byte[32768] : new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        });
        var oldSession = Field("_selectedStorageMipSession").GetValue(null);
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (!caller.IsCompleted)
            {
                int pending;
                lock (Field("_gate").GetValue(null)!) pending = (int)Field("_pendingGuestWorkCount").GetValue(null)!;
                if (pending == 2) break;
                Assert.True(Environment.TickCount64 < deadline, "The exact typed operation was not admitted.");
                Thread.Yield(); // GuestQueueScope is thread-local, not an async-flow scope.
            }
            if (variant == "read-only")
            {
                Assert.NotNull(caller.GetAwaiter().GetResult()); Assert.Equal(1, Field("_pendingGuestWorkCount").GetValue(null));
                Assert.Equal(1L, Field("_enqueuedGuestWorkSequence").GetValue(null));
            }
            else
            {
                Assert.False(caller.IsCompleted); Assert.Equal(2, Field("_pendingGuestWorkCount").GetValue(null));
                if (variant is "stale-original" or "rights-after-admission") memory.CommandCurrent = false;
                if (variant == "changed-session") Field("_selectedStorageMipSession").SetValue(null, (long)oldSession! + 1);
                if (variant == "saved-without-take")
                {
                    Action saved;
                    lock (Field("_gate").GetValue(null)!)
                    {
                        var queues = (IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!;
                        var record = queues.Values.Cast<IEnumerable>().SelectMany(items => items.Cast<object>())
                            .Select(item => item.GetType().GetProperty("Work")!.GetValue(item)!)
                            .Single(item => item.GetType().Name == "OrderedCommandMemoryOperation");
                        saved = (Action)record.GetType().GetProperty("Action")!.GetValue(record)!;
                    }
                    var refusal = Assert.Throws<NotSupportedException>(saved);
                    Assert.Contains("taken/executing original receipt", refusal.Message);
                    Assert.False(caller.IsCompleted); // invoking a borrowed callback did not acknowledge it
                }
                object? inventory = null;
                var inventoryFailure = Record.Exception(() => inventory = InvokeVolume("CaptureNativeColorCommandMemoryInventory"));
                if (inventoryFailure is not null)
                {
                    Assert.Contains(variant, new[] { "stale-original", "rights-after-admission", "changed-session", "wrong-queue", "missing-mapping" });
                    Assert.IsType<NotSupportedException>(Assert.IsType<TargetInvocationException>(inventoryFailure).InnerException);
                }
                else lock (Field("_gate").GetValue(null)!)
                {
                    if (variant is "disjoint" or "saved-without-take")
                    {
                        // The old locked-only seam cannot manufacture a reached
                        // provider check, even with the same original metadata.
                        Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(
                            () => InvokeVolume("RequireNativeColorCohortLocked", producer)).InnerException);
                        InvokeVolume("RequireNativeColorCohortWithCommandMemoryLocked", producer, inventory);
                    }
                    else
                    {
                        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireNativeColorCohortWithCommandMemoryLocked", producer, inventory));
                        Assert.IsType<NotSupportedException>(failure.InnerException);
                    }
                }
            }
            Assert.Equal(1, memory.CommandCopies); Assert.Equal(0, memory.Writes);
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
            Assert.Equal((nint)0, producer.GetType().GetField("Texture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer));
        }
        finally
        {
            MetalVideoPresenter.LatchGuestCompletionFailure("metadata-only typed memory control finished");
            Assert.True(caller.Wait(2000), "Never restore a presenter while the typed caller still owns its receipt.");
            Field("_selectedStorageMipSession").SetValue(null, oldSession);
            memory.CommandCurrent = true;
            InvokeVolume("CompletePresenterShutdown");
        }
        Assert.True(caller.IsCompleted); Assert.Equal(1, memory.Original.Releases);
    }

    [Fact]
    public void OriginalCommandCurrentnessCannotRunUnderPresenterGate()
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var memory = new TypedColorMemory("disjoint"); MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
        var operation = SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation.Write(TypedColorMemory.Destination, new byte[8]);
        var original = InvokeVolume("PrepareCommandMemoryOperation", host, operation, (Action)(() => throw new Exception("not invoked")))!;
        var mapping = original.GetType().GetProperty("Mapping")!.GetValue(original)!;
        var before = memory.CommandQueries;
        lock (Field("_gate").GetValue(null)!)
        {
            var refusal = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireCommandMemoryOriginal", original, mapping, false));
            Assert.IsType<InvalidOperationException>(refusal.InnerException);
            Assert.Equal(before, memory.CommandQueries);
            InvokeVolume("RequireCommandMemoryOriginalLocked", original, mapping); // scalars ONLY
            Assert.Equal(before, memory.CommandQueries);
        }
        InvokeVolume("RequireCommandMemoryOriginal", original, mapping, false);
        Assert.Equal(before + 1, memory.CommandQueries); Assert.Equal(0, memory.Writes);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("memory")]
    [InlineData("uncertain-root")]
    [InlineData("stale")]
    public void OriginalCommandProviderCheckCannotAdoptChangedScalarOwner(string change)
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var memory = new TypedColorMemory("disjoint"); MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var backend = new MetalGuestGpuBackend(); var host = new MetalCommandStreamHost(memory, backend, backend);
        var operation = SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation.Write(TypedColorMemory.Destination, new byte[8]);
        var original = InvokeVolume("PrepareCommandMemoryOperation", host, operation, (Action)(() => throw new Exception("not invoked")))!;
        var mapping = original.GetType().GetProperty("Mapping")!.GetValue(original)!;
        var oldRoot = Field("_uncertainCommandMemoryWrite").GetValue(null);
        Assert.Null(oldRoot);
        memory.BeforeCommandCurrent = () =>
        {
            memory.BeforeCommandCurrent = null;
            switch (change)
            {
                case "session": Field("_selectedStorageMipSession").SetValue(null, (long)Field("_selectedStorageMipSession").GetValue(null)! + 1); break;
                case "memory": Field("_guestMemory").SetValue(null, new TypedColorMemory("disjoint")); break;
                case "uncertain-root": Field("_uncertainCommandMemoryWrite").SetValue(null, original); break; // fake bookkeeping, no effect
                case "stale": memory.CommandCurrent = false; break;
            }
        };
        try
        {
            var before = memory.CommandQueries;
            var refusal = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireCommandMemoryOriginal", original, mapping, false));
            Assert.IsType<NotSupportedException>(refusal.InnerException);
            Assert.Equal(before + 1, memory.CommandQueries); Assert.Equal(1, memory.CommandCopies);
            Assert.Equal(0, memory.Writes); Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        }
        finally
        {
            memory.BeforeCommandCurrent = null;
            Field("_uncertainCommandMemoryWrite").SetValue(null, oldRoot); // only this known fake scope
        }
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("take")]
    public void NativeColorCommandInventoryCannotPassAChangedAcceptedOrTakenReceipt(string change)
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var memory = new TypedColorMemory("disjoint"); MetalVideoPresenter.AttachGuestMemory(memory);
        using var queue = MetalVideoPresenter.EnterGuestQueue("typed.color", 7);
        var invoked = false;
        object inventory;
        if (change == "admission")
        {
            inventory = InvokeVolume("CaptureNativeColorCommandMemoryInventory")!;
            MetalVideoPresenter.SubmitOrderedGuestAction(() => invoked = true, "unknown controlled callback");
        }
        else
        {
            MetalVideoPresenter.SubmitOrderedGuestAction(() => invoked = true, "unknown controlled callback");
            inventory = InvokeVolume("CaptureNativeColorCommandMemoryInventory")!;
            object?[] args = [null];
            Assert.True((bool)Presenter.GetMethod("TryTakeGuestWork", Private)!.Invoke(null, args)!);
        }
        try
        {
            lock (Field("_gate").GetValue(null)!)
            {
                var refusal = Assert.Throws<TargetInvocationException>(
                    () => InvokeVolume("RequireNativeColorCommandMemoryInventoryLocked", inventory));
                Assert.IsType<NotSupportedException>(refusal.InnerException);
            }
            Assert.False(invoked); Assert.Equal(0, memory.CommandQueries); Assert.Equal(0, memory.Writes);
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        }
        finally { InvokeVolume("CompletePresenterShutdown"); }
    }

    private sealed class TypedColorMemory(string variant) : ICpuMemory, ICpuMappedReadWatchSource, ICpuMappedPublicationSource
    {
        internal const ulong Destination = Address + 0x500000;
        internal readonly BorrowColorMemory Original = new();
        internal int CommandCopies, Writes, WatchedCommandCopies, ReleasedCommandWatches;
        internal Action? BeforeCommandRelease;
        internal int CommandQueries;
        internal Action? BeforeCommandCurrent;
        internal bool CommandCurrent = true;
        private CpuMappedCopyObservation? command;
        public bool TryRead(ulong address, Span<byte> bytes) => Original.TryRead(address, bytes);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) { Writes++; throw new InvalidOperationException("No scalar test may execute its writer."); }
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? mapping)
        {
            Assert.Equal(Destination, address); Assert.Equal(variant == "tail-physical-alias" ? 32768UL : 8UL, count);
            Assert.Equal(address, source); Assert.Equal(count, (ulong)bytes.Length);
            CommandCopies++; mapping = null;
            if (variant == "missing-mapping") return CpuMappedCopyResult.Unavailable;
            CpuMappingFootprintSegment[] segments = variant == "tail-physical-alias"
                ? [new(address, 16384, address, 16384, 1, 1, 0x400000, 0x300000, 16384, GuestPageProtection.Read | GuestPageProtection.Write),
                   new(address + 16384, 16384, address + 16384, 16384, 2, 1, 0x400000, 0xB0000, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]
                : [new(address, 16384, address, count, 1, 1, 0x400000,
                    variant == "physical-alias" ? 0xB0000UL : 0x300000UL, 16384,
                    variant == "read-only" ? GuestPageProtection.Read : GuestPageProtection.Read | GuestPageProtection.Write)];
            mapping = command = new(new object(), address, count, source, count, segments);
            bytes[0] = 0xAE; return CpuMappedCopyResult.Copied;
        }
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? mapping)
        {
            if (address != Destination) return Original.TryReadMappedCopyWithWriteWatch(address, count, source, bytes, out mapping);
            WatchedCommandCopies++;
            if (variant == "watch-unavailable") { mapping = null; return CpuMappedCopyResult.Unavailable; }
            return TryReadMappedCopy(address, count, source, bytes, out mapping);
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation mapping)
        {
            if (!ReferenceEquals(mapping, command)) return Original.IsMappedCopyObservationCurrent(mapping);
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!), "Original command provider query ran under the presenter gate.");
            CommandQueries++; BeforeCommandCurrent?.Invoke(); return CommandCurrent;
        }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation mapping) => Original.IsMappedCopyWriteWatchCurrent(mapping);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation mapping)
        {
            if (!ReferenceEquals(mapping, command)) Original.ReleaseMappedCopyWriteWatch(mapping);
            else { ReleasedCommandWatches++; BeforeCommandRelease?.Invoke(); }
        }
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication) =>
            throw new InvalidOperationException("Metadata inventory controls must not prepare or execute bytes.");
    }
}
