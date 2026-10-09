// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static object?[]? typedMemoryResetQuarantine;

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
                lock (Field("_gate").GetValue(null)!)
                {
                    if (variant is "disjoint" or "saved-without-take") InvokeVolume("RequireNativeColorCohortLocked", producer);
                    else
                    {
                        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireNativeColorCohortLocked", producer));
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

    private sealed class TypedColorMemory(string variant) : ICpuMemory, ICpuMappedReadWatchSource, ICpuMappedPublicationSource
    {
        internal const ulong Destination = Address + 0x500000;
        internal readonly BorrowColorMemory Original = new();
        internal int CommandCopies, Writes;
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
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? mapping) =>
            Original.TryReadMappedCopyWithWriteWatch(address, count, source, bytes, out mapping);
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation mapping) =>
            ReferenceEquals(mapping, command) ? CommandCurrent : Original.IsMappedCopyObservationCurrent(mapping);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation mapping) => Original.IsMappedCopyWriteWatchCurrent(mapping);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation mapping) => Original.ReleaseMappedCopyWriteWatch(mapping);
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication) =>
            throw new InvalidOperationException("Metadata inventory controls must not prepare or execute bytes.");
    }
}
