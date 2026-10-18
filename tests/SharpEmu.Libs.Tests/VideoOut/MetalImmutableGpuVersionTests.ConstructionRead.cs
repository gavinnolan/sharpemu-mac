// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [FiniteStoragePublicationNativeFact]
    public void ActualVolumeConstructionContinuesOriginalAfterWriterStartsBeforeFinalContent() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, constructionBusyWindow: "content");

    [FiniteStoragePublicationNativeFact]
    public void ActualVolumeConstructionContinuesOriginalAfterWriterStartsBeforeFinalOwner() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, constructionBusyWindow: "owner");

    [FiniteStoragePublicationNativeFact]
    public void ActualVolumeConstructionRetainsFirstBusyAfterDisjointWriterCompletesBeforeContentCatchContinues() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, constructionBusyWindow: "content-completed");

    [FiniteStoragePublicationNativeFact]
    public void ActualVolumeConstructionRetainsFirstBusyAfterDisjointWriterCompletesBeforeOwnerCatchContinues() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, constructionBusyWindow: "owner-completed");

    [FiniteStoragePublicationNativeFact]
    public void ActualVolumeConstructionRefusesOriginalOverlappingWriterBeforeSecondDispatch() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, constructionBusyWindow: "overlap");

    private sealed class NativeConstructionBusyControl : IDisposable
    {
        private readonly SharedBackingViews views;
        private readonly PhysicalVirtualMemory physical;
        private readonly TrackedCpuMemory memory;
        private readonly FiniteStorageFixtureOwners owners;
        private readonly GuestDrawTexture seed;
        private readonly object origin;
        private readonly CpuMappedCopyObservation mapping;
        private readonly GuestImageWriteTracker.ContentToken token;
        private readonly string window;
        private readonly byte[] expected;
        private readonly ulong baseAddress, writeAddress, labelAddress;
        private readonly long sequence, eop;
        private readonly ITestOutputHelper output;
        private readonly FieldInfo writeHook, entryHook, busyHook, preparedHook, encoderHook, firstBusyHook;
        private readonly object? oldWrite, oldEntry, oldBusy, oldPrepared, oldEncoder, oldFirstBusy;
        private readonly ManualResetEventSlim admitted = new(), resume = new();
        private Thread? writer;
        private Exception? writerFailure;
        private bool written, entered;
        private int busy, prepared, encoders, caughtFirstBusy;

        internal NativeConstructionBusyControl(PhysicalVirtualMemory physical,
            TrackedCpuMemory memory, MetalCommandStreamHost host, FiniteStorageFixtureOwners owners,
            ulong baseAddress, int imageBytes, byte[] expected, GuestDrawTexture seed, object origin,
            long sequence, string window, ITestOutputHelper output)
        {
            this.physical = physical; this.memory = memory; this.owners = owners; this.seed = seed;
            this.origin = origin; this.baseAddress = baseAddress; this.expected = expected;
            this.sequence = sequence; this.window = window; this.output = output;
            mapping = (CpuMappedCopyObservation)StorageMember(origin, "Mapping")!;
            token = (GuestImageWriteTracker.ContentToken)StorageMember(StorageMember(origin, "Observation")!, "Token")!;
            var space = StorageMember(physical, "_backedSpace")!;
            views = Assert.IsType<SharedBackingViews>(StorageMember(space, "_views"));
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            writeHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", fields)!;
            entryHook = Field(window.StartsWith("owner") ? "_singletonVolumeConstructionBeforeFinalOwnerForControl" :
                "_singletonVolumeConstructionBeforeFinalContentForControl");
            busyHook = Field("_singletonVolumeConstructionBusyForControl");
            firstBusyHook = Field("_singletonVolumeConstructionFirstBusyForControl");
            preparedHook = Field("_singletonVolumeConstructionReadPreparedForControl");
            encoderHook = Field("_computeEncoderCreationOverride");
            oldWrite = writeHook.GetValue(views); oldEntry = entryHook.GetValue(null);
            oldBusy = busyHook.GetValue(null); oldFirstBusy = firstBusyHook.GetValue(null); oldPrepared = preparedHook.GetValue(null); oldEncoder = encoderHook.GetValue(null);
            owners.OrdinaryWriterOwner = this;
            writeAddress = window == "overlap" ? seed.Address : checked(baseAddress + (ulong)imageBytes + 32768);
            labelAddress = checked(baseAddress + (ulong)imageBytes + 8);
            try
            {
                host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 1, labelAddress, 0x2468ACE0));
                eop = (long)Field("_enqueuedGuestWorkSequence").GetValue(null)!;
                Assert.True(eop > sequence);
                // Only actual production selectors are used. Count the first real
                // compute encoder; no status/handle/counter result is overridden.
                encoderHook.SetValue(null, (Func<nint, nint>)(command =>
                {
                    Interlocked.Increment(ref encoders);
                    return oldEncoder is Func<nint, nint> prior ? prior(command) :
                        MetalNative.Send(command, MetalNative.Selector("computeCommandEncoder"));
                }));
                preparedHook.SetValue(null, (Action<CpuMappedReadValidation, CpuMappedCopyObservation>)((read, original) =>
                {
                    if (!ReferenceEquals(original, mapping)) return;
                    RequireReleasedGates(); Assert.Equal(1, ++prepared);
                    Assert.True(read.IsOriginalCurrent(mapping));
                    Assert.False(((ICpuMappedReadWatchSource)memory).IsMappedCopyWriteWatchCurrent(mapping));
                    Assert.Equal(0, StorageMember(StorageMember(origin, "VolumeWatch")!, "State"));
                    CheckOriginalToken();
                }));
                entryHook.SetValue(null, (Action)StartWriter);
                if (window.EndsWith("completed"))
                    firstBusyHook.SetValue(null, (Action)(() =>
                    {
                        RequireReleasedGates(); Assert.True(entered); Assert.Equal(0, encoders);
                        Assert.Equal(0, caughtFirstBusy++);
                        Assert.Equal(CpuMappedReadWatchStatus.Busy, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
                        firstBusyHook.SetValue(null, oldFirstBusy);
                        resume.Set(); Join(); // real writer completes before the catch continues
                        Assert.Equal(CpuMappedReadWatchStatus.Current, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
                        CheckOriginalToken(); CheckExactNativeOwner();
                    }));
                busyHook.SetValue(null, (Action)(() =>
                {
                    RequireReleasedGates(); Assert.True(entered); Assert.True(window != "overlap");
                    Assert.Equal(!window.EndsWith("completed"), writer!.IsAlive); Assert.Equal(0, encoders);
                    Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
                    Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                    Assert.Equal(window.EndsWith("completed") ? CpuMappedReadWatchStatus.Current : CpuMappedReadWatchStatus.Busy,
                        ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
                    CheckOriginalToken(); CheckExactNativeOwner(); busy++;
                    busyHook.SetValue(null, oldBusy); resume.Set();
                }));
            }
            catch (Exception primary) { Finish(primary); throw; }
        }

        private void StartWriter()
        {
            if (entered) return;
            entered = true; entryHook.SetValue(null, oldEntry);
            RequireReleasedGates(); Assert.Equal(1, prepared); Assert.Equal(0, encoders);
            CheckExactNativeOwner(); CheckOriginalToken();
            Assert.True(views.TryResolveAlias(writeAddress, 1, out var alias));
            var writers = StorageMember(StorageMember(views, "_backing")!, "_managedWrites")!;
            writeHook.SetValue(views, (Action<ulong, int>)((destination, count) =>
            {
                RequireReleasedGates(); Assert.Equal(alias, destination); Assert.Equal(1, count);
                Assert.Equal(2L, StorageMember(writers, "admission")); Assert.Equal(0, StorageMember(writers, "coverageIncomplete"));
                writeHook.SetValue(views, oldWrite); // later actual EOP uses original ordinary route
                admitted.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Constructor writer pause exceeded owned fixture bound.");
            }));
            writer = new Thread(() =>
            {
                try { written = views.TryWriteBacking(writeAddress, new byte[] { 0xA9 }); }
                catch (Exception failure) { writerFailure = failure; }
            }) { IsBackground = true, Name = "actual original constructor ordinary writer" };
            writer.Start(); Assert.True(admitted.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(2L, StorageMember(writers, "admission"));
            Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
            CheckOriginalToken();
            if (window == "overlap")
            {
                Assert.Equal(CpuMappedReadWatchStatus.Stale, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
                resume.Set(); Join();
            }
            else Assert.Equal(CpuMappedReadWatchStatus.Busy, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
        }

        internal void AssertContinued()
        {
            Join(); Assert.True(entered); Assert.Equal(1, prepared); Assert.Equal(1, busy); Assert.Equal(1, encoders);
            Assert.Equal(window.EndsWith("completed") ? 1 : 0, caughtFirstBusy);
            Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.True(MetalVideoPresenter.WaitForGuestWork(eop, 0));
            Assert.Equal(CpuMappedReadWatchStatus.Current, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
            CheckOriginalToken();
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(checked((int)(labelAddress - baseAddress))), 0x2468ACE0);
            output.WriteLine($"constructor-original-native window={window}; same-read-ticket-through-transfer; actual-admission=2; no-encoder-or-EOP-while-Busy; one-construction-and-one-dispatch; exact-original-continued");
        }

        internal void AssertRefused()
        {
            Join(); Assert.True(entered); Assert.Equal(1, prepared); Assert.Equal(0, busy); Assert.Equal(0, encoders);
            Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
            Assert.Equal(CpuMappedReadWatchStatus.Stale, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
            CheckOriginalToken();
            output.WriteLine("constructor-original-native window=overlap; first-volume-native-status4; second-private-native-original-refused; zero-second-encoder; EOP-unacknowledged");
        }

        private void CheckExactNativeOwner()
        {
            var watch = StorageMember(origin, "VolumeWatch")!;
            lock (Field("_gate").GetValue(null)!)
            {
                var image = ((IDictionary)Field("_guestImages").GetValue(null)!)[seed.Address]!;
                Assert.NotNull(image); Assert.Same(origin, StorageMember(image, "StorageCpuOrigin"));
                Assert.Same(image, StorageMember(watch, "Image"));
                Assert.Equal(1, StorageMember(watch, "State")); Assert.Equal(1, StorageMember(image, "ContentVersion"));
                Assert.Equal(StorageMember(image, "Texture"), StorageMember(watch, "Texture"));
                Assert.Equal(false, StorageMember(watch, "UnknownProducer"));
            }
        }
        private void CheckOriginalToken()
        {
            Assert.True(((ICpuMappedCopySource)memory).IsMappedCopyObservationCurrent(mapping));
            Assert.Same(mapping, StorageMember(StorageMember(origin, "VolumeWatch")!, "Mapping"));
            Assert.True(GuestImageWriteTracker.TryGetProtectedObservedContentToken(seed.Address, 65536, out var current));
            Assert.Equal(token, current);
            Assert.True(GuestImageWriteTracker.IsReadSnapshotStable((GuestImageWriteTracker.ReadSnapshot)StorageMember(StorageMember(origin, "VolumeWatch")!, "Read")!));
        }
        private void RequireReleasedGates()
        {
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
            Assert.False(Monitor.IsEntered(StorageMember(views, "_lock")!));
            var gate = (ReaderWriterLockSlim)StorageMember(physical, "_gate")!;
            Assert.False(gate.IsReadLockHeld || gate.IsWriteLockHeld || gate.IsUpgradeableReadLockHeld);
        }
        private void Join()
        {
            if (writer is null || !writer.Join(TimeSpan.FromSeconds(10)))
            {
                owners.OrdinaryWriterUnconfirmed = true;
                throw new TimeoutException("Constructor ordinary writer is unknown; retain native/backing/event owners.");
            }
            if (writerFailure is not null) ExceptionDispatchInfo.Capture(writerFailure).Throw();
            Assert.True(written); expected[checked((int)(writeAddress - baseAddress))] = 0xA9;
        }
        internal void Finish(Exception? primary)
        {
            try { Dispose(); }
            catch (Exception cleanup)
            {
                if (primary is null) throw;
                try { primary.Data["ConstructionNativeControlCleanup"] = cleanup; } catch { }
            }
        }
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            Exception? cleanup = null;
            resume.Set();
            try { if (writer is not null) Join(); } catch (Exception failure) { cleanup = failure; }
            Attempt(() => writeHook.SetValue(views, oldWrite)); Attempt(() => entryHook.SetValue(null, oldEntry));
            Attempt(() => busyHook.SetValue(null, oldBusy)); Attempt(() => firstBusyHook.SetValue(null, oldFirstBusy)); Attempt(() => preparedHook.SetValue(null, oldPrepared));
            Attempt(() => encoderHook.SetValue(null, oldEncoder));
            if (!owners.OrdinaryWriterUnconfirmed) { Attempt(admitted.Dispose); Attempt(resume.Dispose); }
            if (cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
        }
    }
}
