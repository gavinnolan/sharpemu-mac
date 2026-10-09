// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Core.Memory;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [ColdEnrollmentNativeFact]
    public void ActualColdOriginalWaitsForForeignReadValidationThenPreservesAllNativeBytesAndEop() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, coldEnrollmentWindow: "read-validation");

    [ColdEnrollmentNativeFact]
    public void ActualColdOriginalWaitsForForeignDisjointPublicationThenPreservesAllNativeBytesAndEop() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, coldEnrollmentWindow: "disjoint-publication");

    [ColdEnrollmentNativeFact]
    public void ActualColdOriginalWaitsForForeignOrdinaryWriterThenPreservesAllNativeBytesAndEop() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, coldEnrollmentWindow: "ordinary-writer");

    // Test instrumentation only, explicitly opting into the optional wrapper
    // contract. Every mapping, watch, validation, publication and status comes
    // unchanged from the real PVM. No fabricated Current/Applied/receipt exists.
    // Only the FIRST target cold copy is controlled; the two real volume
    // originals and all later native publications follow the normal provider.
    private sealed class ColdNativeMappedSource : ICpuMemory, ICpuMappedReadWatchSource, ICpuMappedPublicationSource
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly PhysicalVirtualMemory physical;
        private readonly FiniteStorageFixtureOwners owners;
        private readonly ulong target, targetBytes, disjoint;
        private readonly string scenario;
        private readonly ITestOutputHelper output;
        private readonly SharedBackingViews views;
        private readonly HostBackingObject backing;
        private readonly HostMappedCopyLedger ledger;
        private readonly FieldInfo revisionHook, copyHook, writerHook;
        private readonly object? oldRevisionHook, oldCopyHook, oldWriterHook;
        private readonly FieldInfo traceEnabled, traceRows, traceSink;
        private readonly object? oldTraceEnabled, oldTraceRows, oldTraceSink;
        private readonly List<string> diagnostics = [];
        private readonly ManualResetEventSlim ready = new(), resume = new();
        private Thread? actor;
        private Exception? actorFailure;
        private Exception? hookCleanupFailure;
        private CpuMappedCopyObservation? fixtureOriginal, fixtureSuccessor;
        private CpuMappedReadValidation? heldRead;
        private CpuMappedPublication? heldPublication;
        private object? unknownActorReceipt;
        private CpuMappedCopyObservation? unknownCleanupWatch;
        private object? firstView;
        private long firstIncarnation;
        private int originalAttempts, originalRevisionSamples, originalCopyHooks, releases;
        private int legacyTargetCopies, plainTargetCopies;
        private int baselinePins, admittedPins, admittedWatchCharges;
        private long originalSession, originalEpoch, originalReadGeneration;
        private GuestImageWriteTracker.ContentToken originalToken;
        private object? originalBackend;
        private bool started, releaseRequested, joined, hooksInstalled, writerHookInstalled, finished;
        private long InitialAdmission => (long)StorageMember(backing.ManagedWrites!, "admission")!;

        internal ColdNativeMappedSource(PhysicalVirtualMemory physical, IHostViewMemory host,
            FiniteStorageFixtureOwners owners, ulong target, int bytes, string scenario, ITestOutputHelper output)
        {
            this.physical = physical; this.owners = owners; this.target = target;
            targetBytes = (ulong)bytes; disjoint = target + targetBytes + 32768;
            this.scenario = scenario; this.output = output;
            views = Assert.IsType<SharedBackingViews>(StorageMember(StorageMember(physical, "_backedSpace")!, "_views"));
            backing = Assert.IsType<HostBackingObject>(StorageMember(views, "_backing"));
            var state = host.GetType().Assembly.GetType("SharpEmu.Host.MacOS.MacMappedCopyState", true)!;
            ledger = Assert.IsType<HostMappedCopyLedger>(state.GetProperty("Ledger", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
            // The backing writer ledger is enrolled by the real seed write
            // before the first optional copy, so resolve it lazily in Start.
            revisionHook = typeof(HostMappedCopyLedger).GetField("afterWriterRevisionForControl", Instance)!;
            copyHook = typeof(HostMappedCopyLedger).GetField("afterCaptureForControl", Instance)!;
            writerHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", Instance)!;
            oldRevisionHook = revisionHook.GetValue(ledger); oldCopyHook = copyHook.GetValue(ledger);
            oldWriterHook = writerHook.GetValue(views);
            var trace = typeof(HostMappedCopyLedger).Assembly.GetType("SharpEmu.HLE.CpuMappedCopyDiagnostics", true)!;
            traceEnabled = trace.GetField("enabled", BindingFlags.Static | BindingFlags.NonPublic)!;
            traceRows = trace.GetField("rows", BindingFlags.Static | BindingFlags.NonPublic)!;
            traceSink = trace.GetField("sinkForControl", BindingFlags.Static | BindingFlags.NonPublic)!;
            oldTraceEnabled = traceEnabled.GetValue(null); oldTraceRows = traceRows.GetValue(null); oldTraceSink = traceSink.GetValue(null);
        }

        private void RequireGateFree()
        {
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
            Assert.False(Monitor.IsEntered(StorageMember(ledger, "gate")!));
            Assert.False(Monitor.IsEntered(StorageMember(views, "_lock")!));
            Assert.False(Monitor.IsEntered(StorageMember(views, "_disposeGate")!));
            Assert.False(Monitor.IsEntered(StorageMember(backing, "Gate")!));
            var gate = Assert.IsType<ReaderWriterLockSlim>(StorageMember(physical, "_gate"));
            Assert.False(gate.IsReadLockHeld || gate.IsWriteLockHeld || gate.IsUpgradeableReadLockHeld);
            Assert.True(physical.CanWaitForMappedReadWatchEnrollment);
        }

        private void Start()
        {
            Assert.False(started); started = true; RequireGateFree();
            Assert.NotNull(backing.ManagedWrites);
            originalSession = MetalVideoPresenter.CurrentStorageCpuSeedSession;
            originalEpoch = HostMemory.MappingGeneration;
            originalBackend = GuestGpu.Current;
            // Fixture reference observations only. Production already captured
            // its original read/token/epoch before calling this adapter; these
            // comparisons never feed replacement observations to that caller.
            Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(target, targetBytes, out originalToken));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(target, out originalReadGeneration));
            baselinePins = backing.ActiveAccesses;
            Assert.Equal(0L, InitialAdmission);
            var registered = (IDictionary)StorageMember(ledger, "views")!;
            lock (StorageMember(ledger, "gate")!)
                firstView = Assert.Single(registered.Values.Cast<object>().Where(view =>
                {
                    var address = (ulong)StorageMember(view, "Address")!;
                    var size = (ulong)StorageMember(view, "Size")!;
                    return address <= target && target - address < size && targetBytes <= size - (target - address);
                }));
            firstIncarnation = (long)StorageMember(firstView!, "Incarnation")!;
            var disjointByte = new byte[1]; Assert.True(physical.TryRead(disjoint, disjointByte));
            actor = new Thread(() =>
            {
                try
                {
                    if (scenario == "ordinary-writer")
                    {
                        Assert.True(views.TryResolveAlias(disjoint, 1, out var alias));
                        // Own restoration before the first reflection mutation,
                        // including a partially failed installation.
                        writerHookInstalled = true;
                        writerHook.SetValue(views, (Action<ulong, int>)((destination, length) =>
                        {
                            Assert.Equal(alias, destination); Assert.Equal(1, length); RequireGateFree();
                            Assert.Equal(2L, InitialAdmission); ready.Set();
                            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cold ordinary actor resume bound.");
                        }));
                        Assert.True(views.TryWriteBacking(disjoint, disjointByte));
                        Interlocked.Increment(ref releases);
                    }
                    else
                    {
                        Assert.Equal(CpuMappedCopyResult.Copied, physical.TryReadMappedCopyWithWriteWatch(
                            disjoint, 1, disjoint, new byte[1], out fixtureOriginal));
                        Assert.NotNull(fixtureOriginal);
                        if (scenario == "read-validation")
                        {
                            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                                physical.TryPrepareMappedReadValidation(fixtureOriginal!, out heldRead));
                            Assert.NotNull(heldRead);
                        }
                        else
                        {
                            Assert.Equal("disjoint-publication", scenario);
                            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                                physical.TryPrepareMappedPublication(fixtureOriginal!, [new(disjoint, disjointByte)], out heldPublication));
                            Assert.NotNull(heldPublication);
                        }
                        RequireGateFree(); Assert.Equal(4294967296L, InitialAdmission); ready.Set();
                        if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cold reservation actor resume bound.");
                        if (heldRead is { } read)
                        {
                            // Clear BEFORE the sole release. An exception keeps
                            // all owners quarantined rather than retrying Dispose.
                            unknownActorReceipt = read; heldRead = null;
                            read.Dispose(); Interlocked.Increment(ref releases);
                            unknownActorReceipt = null;
                        }
                        else
                        {
                            var publication = heldPublication!;
                            unknownActorReceipt = publication; heldPublication = null;
                            Assert.Equal(CpuMappedPublicationResult.Applied, publication.TryCommit());
                            // A known Applied provider commit consumed its own
                            // predecessor watch; cleanup owns only the successor.
                            fixtureOriginal = null;
                            fixtureSuccessor = publication.SuccessfulObservation;
                            Assert.NotNull(fixtureSuccessor); Interlocked.Increment(ref releases);
                            unknownActorReceipt = null;
                        }
                    }
                }
                catch (Exception failure) { actorFailure = failure; ready.Set(); }
            }) { IsBackground = true, Name = "real cold enrollment foreign admission" };
            owners.OrdinaryWriterUnconfirmed = true;
            actor.Start(); Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            if (actorFailure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            admittedPins = backing.ActiveAccesses;
            Assert.True(admittedPins > baselinePins);
            admittedWatchCharges = (int)StorageMember(ledger, "readWatchCount")!;
            // Own the entire restore set BEFORE its first mutation, including
            // a partially successful reflection installation.
            hooksInstalled = true;
            revisionHook.SetValue(ledger, (Action<bool, long>)((hasRevision, _) =>
            {
                RequireGateFree(); Assert.False(hasRevision); Assert.Equal(1, ++originalRevisionSamples);
                Assert.Equal(scenario == "ordinary-writer" ? 2L : 4294967296L, InitialAdmission);
                Assert.True(backing.ActiveAccesses > admittedPins);
            }));
            copyHook.SetValue(ledger, (Action)(() =>
            {
                RequireGateFree(); Assert.Equal(1, ++originalCopyHooks); Assert.True(joined);
                Assert.Equal(1, releases); Assert.Equal(0L, InitialAdmission);
                Assert.True(backing.ActiveAccesses > baselinePins);
            }));
            traceEnabled.SetValue(null, true); traceRows.SetValue(null, 0);
            traceSink.SetValue(null, (Action<string>)(line =>
            {
                // Ledger diagnostics unwind its pin before emitting, while
                // SharedBackingViews still owns the SAME outer alias pin.
                RequireGateFree(); Assert.Equal(baselinePins + 1, backing.ActiveAccesses); diagnostics.Add(line);
            }));
        }

        public bool CanWaitForMappedReadWatchEnrollment => physical.CanWaitForMappedReadWatchEnrollment;
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatchAdmissionWait(ulong address, ulong count,
            ulong source, Span<byte> bytes, Func<bool> current, out CpuMappedCopyObservation? original)
        {
            if (address != target) return physical.TryReadMappedCopyWithWriteWatchAdmissionWait(address, count, source, bytes, current, out original);
            Assert.Equal(1, ++originalAttempts); Assert.Equal(targetBytes, count); Assert.Equal(target, source);
            Start();
            bool SameOriginal()
            {
                RequireGateFree();
                if (originalRevisionSamples != 0 && !releaseRequested)
                {
                    // This callback is reached only after the original provider
                    // capture and the FIRST failed watch-enrollment CAS. No
                    // revision recapture or replacement watch is requested.
                    Assert.Equal(0, originalCopyHooks); Assert.Equal(1, originalRevisionSamples);
                    Assert.True(backing.ActiveAccesses > admittedPins);
                    Assert.Equal(admittedWatchCharges + 1, StorageMember(ledger, "readWatchCount"));
                    Assert.Equal(scenario == "ordinary-writer" ? 2L : 4294967296L, InitialAdmission);
                    releaseRequested = true; resume.Set();
                    joined = actor!.Join(TimeSpan.FromMilliseconds(200));
                    Assert.True(joined, "Exact foreign admission must close inside the cold deadline.");
                    if (actorFailure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                    owners.OrdinaryWriterUnconfirmed = false;
                    Assert.Equal(1, releases); Assert.Equal(0L, InitialAdmission);
                }
                return current(); // caller veto never provides copy authority
            }
            Exception? primary = null;
            try
            {
                var result = physical.TryReadMappedCopyWithWriteWatchAdmissionWait(address, count, source, bytes, SameOriginal, out original);
                Assert.Equal(CpuMappedCopyResult.Copied, result); Assert.NotNull(original);
                Assert.Equal(1, originalCopyHooks); Assert.Equal(1, originalRevisionSamples);
                Assert.Equal(baselinePins, backing.ActiveAccesses);
                var states = (Array)StorageMember(original!.OwnerIdentity, "States")!;
                Assert.Single(states.Cast<object>()); Assert.Same(firstView, states.GetValue(0));
                Assert.All(original.Segments, part => Assert.Equal(firstIncarnation, part.MappingIncarnation));
                var first = Assert.Single(diagnostics);
                Assert.Contains("stage=ledger-prebyte-enrollment-first", first);
                Assert.Contains("reason=watch-enrollment-admission-busy", first);
                Assert.Contains("enrollment_admission=" + (scenario == "ordinary-writer" ? 2L : 4294967296L), first);
                return result;
            }
            catch (Exception failure) { primary = failure; throw; }
            finally
            {
                var cleanup = RestoreHooks();
                if (cleanup is not null)
                {
                    if (primary is not null)
                    { try { primary.Data["ColdEnrollmentHookCleanup"] = cleanup; } catch { } }
                    else ExceptionDispatchInfo.Capture(cleanup).Throw();
                }
            }
        }

        internal void AssertFirstOriginalCopied(GuestDrawTexture snapshot)
        {
            Assert.Equal(target, snapshot.Address); Assert.Equal(targetBytes, snapshot.SourceByteCount);
            Assert.Equal(1, originalAttempts); Assert.Equal(1, originalRevisionSamples);
            Assert.Equal(1, originalCopyHooks); Assert.Equal(1, releases);
            Assert.Equal(0, legacyTargetCopies); Assert.Equal(0, plainTargetCopies);
            var origin = Call("StorageCpuOrigin", snapshot)!;
            var observation = Assert.IsType<MetalVideoPresenter.CpuTextureSnapshotObservation>(StorageMember(origin, "Observation"));
            Assert.Equal(originalSession, StorageMember(origin, "Session"));
            Assert.Equal(originalEpoch, observation.MappingEpoch); Assert.Equal(originalToken, observation.Token);
            Assert.Equal(originalReadGeneration, snapshot.WriteGeneration); Assert.Same(originalBackend, GuestGpu.Current);
            output.WriteLine($"cold-enrollment-native scenario={scenario}; first_capture=1; first_CAS_refusal=1; same_view=true; original_pin_held=true; foreign_release=1; actual_alias_copy=1; legacy_or_plain_copy=0");
        }

        private Exception? RestoreHooks()
        {
            if (!hooksInstalled && !writerHookInstalled) return hookCleanupFailure;
            Exception? cleanup = null;
            if (writerHookInstalled)
            {
                // The controlled foreign writer has joined before a successful
                // cold copy returns. Later publication/EOP writes must see the
                // original hook; consume this restore attempt even if it fails.
                Attempt(() => writerHook.SetValue(views, oldWriterHook));
                writerHookInstalled = false;
            }
            if (hooksInstalled)
            {
                Attempt(() => revisionHook.SetValue(ledger, oldRevisionHook));
                Attempt(() => copyHook.SetValue(ledger, oldCopyHook));
                Attempt(() => traceSink.SetValue(null, oldTraceSink));
                Attempt(() => traceRows.SetValue(null, oldTraceRows));
                Attempt(() => traceEnabled.SetValue(null, oldTraceEnabled));
            }
            // Every restore was attempted exactly once. Failed restoration
            // retains all fixture owners and cannot be retried by Finish.
            hooksInstalled = false;
            if (cleanup is not null)
            {
                hookCleanupFailure ??= cleanup;
                owners.OrdinaryWriterUnconfirmed = true;
            }
            return hookCleanupFailure;
            void Attempt(Action restore)
            { try { restore(); } catch (Exception failure) { cleanup ??= failure; } }
        }

        internal void Finish()
        {
            if (finished) return; finished = true;
            // Known admission release is not known fixture/watch cleanup.
            // Keep the strong owner root until every foreign cleanup returns.
            owners.OrdinaryWriterUnconfirmed = true;
            resume.Set();
            if (actor is not null && !joined)
            {
                joined = actor.Join(TimeSpan.FromSeconds(5));
                if (!joined) throw new TimeoutException("Cold actor outcome unknown; fixture owners retained.");
            }
            var cleanup = RestoreHooks();
            if (actorFailure is { } failure)
            {
                // Release/commit may have thrown. Never retry it or retire its
                // native backing merely because the thread subsequently exited.
                owners.OrdinaryWriterUnconfirmed = true;
                if (cleanup is not null)
                { try { failure.Data["ColdEnrollmentHookCleanup"] = cleanup; } catch { } }
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            if (cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
            if (unknownActorReceipt is not null)
                throw new InvalidOperationException("Cold actor receipt outcome remains unknown; exact ticket and fixture owners retained without retry.");
            ReleaseForeignWatch(ref fixtureSuccessor);
            ReleaseForeignWatch(ref fixtureOriginal);
            ready.Dispose(); resume.Dispose();
            owners.OrdinaryWriterUnconfirmed = false;
        }

        private void ReleaseForeignWatch(ref CpuMappedCopyObservation? saved)
        {
            if (saved is not { } exact) return;
            saved = null; unknownCleanupWatch = exact; // consume BEFORE callback, no retry
            physical.ReleaseMappedCopyWriteWatch(exact);
            unknownCleanupWatch = null;
        }

        public bool TryRead(ulong address, Span<byte> bytes) => physical.TryRead(address, bytes);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => physical.TryWrite(address, bytes);
        public bool CanRead(ulong address, ulong bytes) => physical.CanRead(address, bytes);
        public string DescribeReadRange(ulong address, ulong bytes) => physical.DescribeReadRange(address, bytes);
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? original)
        { if (address == target) plainTargetCopies++; return physical.TryReadMappedCopy(address, count, source, bytes, out original); }
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? original)
        { if (address == target) legacyTargetCopies++; return physical.TryReadMappedCopyWithWriteWatch(address, count, source, bytes, out original); }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => physical.IsMappedCopyObservationCurrent(original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => physical.IsMappedCopyWriteWatchCurrent(original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original, out CpuMappedReadWatchDiagnostic diagnostic) => physical.IsMappedCopyWriteWatchCurrent(original, out diagnostic);
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original) => physical.GetMappedCopyWriteWatchStatus(original);
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original, out CpuMappedReadWatchDiagnostic diagnostic) => physical.GetMappedCopyWriteWatchStatus(original, out diagnostic);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => physical.ReleaseMappedCopyWriteWatch(original);
        public CpuMappedPublicationPreparationResult TryPrepareMappedReadValidation(CpuMappedCopyObservation original, out CpuMappedReadValidation? ticket) => physical.TryPrepareMappedReadValidation(original, out ticket);
        public bool CanWaitForMappedPublicationValidation => physical.CanWaitForMappedPublicationValidation;
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(CpuMappedCopyObservation original, out CpuMappedPublicationValidation? ticket) => physical.TryPrepareMappedPublicationValidation(original, out ticket);
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(CpuMappedCopyObservation original, out CpuMappedPublicationValidation? ticket, out CpuMappedPublicationValidationRefusal refusal) => physical.TryPrepareMappedPublicationValidation(original, out ticket, out refusal);
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original, IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? ticket) => physical.TryPrepareMappedPublication(original, runs, out ticket);
    }
}

internal sealed class ColdEnrollmentNativeFactAttribute : FactAttribute
{
    public ColdEnrollmentNativeFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1" ||
            Environment.GetEnvironmentVariable("SHARPEMU_METAL_STORAGE_ORIGINAL_ENROLLMENT_WAIT") != "1")
            Skip = "Requires an exclusive fresh Mac provider/Metal worker with cold enrollment enabled before load.";
    }
}
