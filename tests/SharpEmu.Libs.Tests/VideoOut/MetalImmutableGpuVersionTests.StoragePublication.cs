// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.Tests.Fixtures;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    // A fresh worker must terminate after an unknown native completion/cleanup
    // outcome. Keep its exact native/mapping owners reachable; never retry their
    // release or let a later row reuse the borrowed presenter state.
    private static FiniteStorageFixtureOwners? finiteStorageFixtureQuarantine;
    private sealed class FiniteStorageFixtureOwners
    {
        public NativeState? State;
        public PhysicalVirtualMemory? Physical;
        public PhysicalVirtualMemory? BodyVolumePhysical;
        public ulong BodyVolumeAddress;
        public TrackedCpuMemory? Memory;
        public object? OriginalOrigin;
        public object? Image;
        public Action? AfterOwnedRetirement;
        public readonly List<GuestDrawTexture> VolumeSeeds = [];
        public readonly List<object> VolumeImages = [];
        public bool OrdinaryWriterUnconfirmed;
        public object? OrdinaryWriterOwner;
        public bool ConstructorReservationUnconfirmed;
        public CpuMappedReadValidation? ConstructorReservation;
    }

    [FiniteStoragePublicationNativeFact]
    public void ActualRuntimeTwoTypedPublicationsPreserveOriginalMappingAndOrderEop() =>
        RunActualRuntimeTwoTypedPublications(overlappingCpuWrite: false);

    [FiniteStoragePublicationNativeFact]
    public void ActualRuntimeOverlappingCpuWriteRefusesSecondTypedPublicationAndEop() =>
        RunActualRuntimeTwoTypedPublications(overlappingCpuWrite: true);

    [FiniteStoragePublicationNativeFact]
    public void ActualRuntimeRgba1024TwoPublicationsPreserveEveryPhysicalByteAndEop() =>
        RunActualRuntimeTwoTypedPublications(overlappingCpuWrite: false, rgba: true);

    [FiniteStoragePublicationNativeFact]
    public void ActualRuntimeRgba1024OverlappingWriterRejectsSecondPublicationAndEop() =>
        RunActualRuntimeTwoTypedPublications(overlappingCpuWrite: true, rgba: true);

    [FiniteStoragePublicationNativeFact]
    public void ActualRuntimeDisjointMappedGlobalAndRgbaPublicationsPreserveAllOwnedBytes() =>
        RunActualRuntimeTwoTypedPublications(overlappingCpuWrite: false, rgba: true, mixedBuffer: true);

    [FiniteStoragePublicationNativeFact]
    public void ActualRuntimeAliasWriteAfterAppliedImageCommitVetoesFinalizationAndEop() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, lateFailure: "writer");

    [FiniteStoragePublicationNativeFact]
    public void ActualRuntimeLateDirtyGenerationAfterCanonicalVersionKeepsStickyDebtAndNoEop() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, lateFailure: "generation");

    private void RunActualRuntimeTwoTypedPublications(bool overlappingCpuWrite, bool rgba = false, bool mixedBuffer = false, string? lateFailure = null, bool originalVolumes = false, string? busyOriginalRetry = null, string? constructionBusyWindow = null, string? queueRetentionWindow = null, string? bodyBusyWindow = null, string? precommitBusyWindow = null, string? phaseBusyWindow = null)
    {
        Assert.True(DeviceRequested(), "finite setup: actual native device request");
        var imageBytes = rgba ? 4194304 : 65536;
        var allocationBytes = imageBytes + (originalVolumes ? 3 : 1) * 65536;
        var storeOffset = rgba ? Bgra1024Oracle.Offset(257, 511) : 0;
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        Assert.Equal(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"));
        output.WriteLine($"finite-storage-native architecture={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; overlap={overlappingCpuWrite}");
        Assert.Null(finiteStorageFixtureQuarantine);
        Assert.Equal(0L, Field("_unorderedPresentationWriteSequence").GetValue(null));
        var owners = new FiniteStorageFixtureOwners();
        var oldFinite = Field("_finiteStorageWritebackEnabled").GetValue(null);
        var oldMemory = Field("_guestMemory").GetValue(null);
        var oldAfterApplied = Field("_finiteStorageAfterAppliedForControl").GetValue(null);
        var oldAfterVersion = Field("_finiteStorageAfterCanonicalVersionForControl").GetValue(null);
        var backendField = typeof(GuestGpu).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldBackend = backendField.GetValue(null);
        NativeState? state = null;
        PhysicalVirtualMemory? physical = null;
        object? image = null;
        ulong address = 0;
        Exception? primary = null;
        try
        {
            state = new NativeState();
            owners.State = state;
            Assert.False(string.IsNullOrWhiteSpace(state.DeviceName));
            output.WriteLine($"finite-storage-native MetalDevice={state.DeviceName}");
            // The source/version graph stays disabled. Only this separately
            // gated numerical fixture enables the finite publication cohort.
            Field("_textureSourceGraphEnabled").SetValue(null, false);
            var backend = new MetalGuestGpuBackend();
            backendField.SetValue(null, backend);
            var viewHost = HostViewMemory.Create();
            physical = new PhysicalVirtualMemory(HostMemoryAuthority.Current, viewHost,
                backingBytes: checked((ulong)allocationBytes * 2), preReserveGuestAddressSpace: false);
            owners.Physical = physical;
            Assert.True(physical.TryHoldRangeAtOrAbove(0x71F6_0000_00, (ulong)allocationBytes, 65536, out address), "finite setup: image placeholder hold-at-or-above");
            Assert.True(address != 0 && (address & 65535) == 0 && address >> 48 == 0, "finite setup: image placeholder nonzero/alignment/address bounds");
            Assert.True(physical.TryHoldRange(address, (ulong)allocationBytes), "finite setup: image placeholder exact hold");
            Assert.True(physical.TryMapBacked(address, (ulong)allocationBytes, 65536,
                GuestPageProtection.Read | GuestPageProtection.Write, out var mapFailure), $"map failure={mapFailure}");
            ICpuMemory cpuSource = physical;
            if (bodyBusyWindow is not null || precommitBusyWindow is not null || phaseBusyWindow is not null)
            {
                Assert.True(rgba && originalVolumes, "finite setup: independent volume route requires RGBA plus original volumes");
                var volumePhysical = new PhysicalVirtualMemory(HostMemoryAuthority.Current, HostViewMemory.Create(),
                    backingBytes: 6 * 65536UL, preReserveGuestAddressSpace: false);
                owners.BodyVolumePhysical = volumePhysical;
                Assert.True(volumePhysical.TryHoldRangeAtOrAbove(0x71F7_0000_00, 3 * 65536, 65536, out var volumeBase), "finite setup: independent volume placeholder hold-at-or-above");
                owners.BodyVolumeAddress = volumeBase;
                Assert.True(volumeBase > address + (ulong)allocationBytes || volumeBase + 3 * 65536 < address, $"finite setup: separate image and volume virtual extents image=[0x{address:X},0x{address + (ulong)allocationBytes:X}) volume=[0x{volumeBase:X},0x{volumeBase + 3 * 65536:X})");
                Assert.True(volumePhysical.TryHoldRange(volumeBase, 3 * 65536), "finite setup: independent volume placeholder exact hold");
                Assert.True(volumePhysical.TryMapBacked(volumeBase, 3 * 65536, 65536,
                    GuestPageProtection.Read | GuestPageProtection.Write, out var volumeFailure), $"volume map failure={volumeFailure}");
                cpuSource = new BodyBusyRoutedMemory(physical, volumePhysical, volumeBase, 3 * 65536,
                    failValidationCancellation: bodyBusyWindow == "cancel-unknown");
            }
            var memory = new TrackedCpuMemory(cpuSource);
            owners.Memory = memory;
            Assert.IsAssignableFrom<ICpuMappedCopySource>(memory);
            Assert.IsAssignableFrom<ICpuMappedPublicationSource>(memory);
            Field("_guestMemory").SetValue(null, memory);
            Field("_finiteStorageWritebackEnabled").SetValue(null, true);
            var expected = Enumerable.Range(0, allocationBytes).Select(i => (byte)(i * 19 + 7)).ToArray();
            if (rgba) Bgra1024Oracle.PhysicalSeed().CopyTo(expected, 0);
            else BinaryPrimitives.WriteUInt32LittleEndian(expected, 0x87654321);
            Assert.True(physical.TryWriteBacking(address, expected), "finite setup: full original image backing seed write");
            if (owners.BodyVolumePhysical is { } bodyPhysical)
                Assert.True(bodyPhysical.TryWriteBacking(owners.BodyVolumeAddress, expected.AsSpan(imageBytes, 3 * 65536)), "finite setup: full independent volume backing seed write");
            uint[] raw = rgba
                ? [(uint)(address >> 8), (uint)(address >> 40) | 0xC3800000u, 0x00FFC0FF, 0xD1B00F2E, 0, 0x00700000, 0, 0]
                : [(uint)(address >> 8), 0x01400000, 0, 0xD1B00204, 0, 0x00700000, 0, 0];
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor), "finite setup: original image descriptor decode");
            // No fixture pre-arm or injected receipt: the production cold copy
            // itself establishes its actual protection before its original
            // epoch/token and mapped bytes are captured.
            Assert.False(GuestImageWriteTracker.TryGetContentToken(address, out _));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], IsArrayed: true,
                ResourceDescriptor: raw, Shape: new ShaderImageShape(false, true, true, false, rgba ? TextureNumericClass.Float : TextureNumericClass.Uint),
                Access: GuestImageAccess.Write);
            Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var kernelPage), "finite setup: expected measured kernel-page environment parse");
            var host = new MetalCommandStreamHost(memory, backend, backend);
            var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
            if (originalVolumes && !MaterializeOriginalVolumes()) return;
            var first = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            Assert.Equal(rgba ? Bgra1024Oracle.LinearSeed() : expected.AsSpan(0, 4).ToArray(), first.RgbaPixels);
            var originalOrigin = Call("StorageCpuOrigin", first)!;
            owners.OriginalOrigin = originalOrigin;
            Assert.NotNull(originalOrigin);
            var originalMapping = (CpuMappedCopyObservation)StorageMember(originalOrigin, "Mapping")!;
            Assert.Equal(address, originalMapping.FootprintAddress);
            Assert.Equal((ulong)imageBytes, originalMapping.FootprintByteCount);
            Assert.All(originalMapping.Segments, segment => Assert.True(segment.BackingIdentity > 0 && segment.MappingIncarnation > 0));
            Assert.All(originalMapping.Segments, segment => Assert.Equal(
                GuestPageProtection.Read | GuestPageProtection.Write, segment.GuestAccess));
            Assert.All(originalMapping.Segments, segment => Assert.Equal(kernelPage, segment.ProviderPageSize));
            output.WriteLine($"finite-storage-native provider_kernel_page={kernelPage}; original_footprint={originalMapping.FootprintByteCount}");
            var observation = StorageMember(originalOrigin, "Observation")!;
            Assert.True((bool)StorageMember(observation, "Observed")!); // actual production arm/copy observation, never injected
            Assert.Equal(HostMemory.MappingGeneration, (long)StorageMember(observation, "MappingEpoch")!);
            using var queueScope = MetalVideoPresenter.EnterGuestQueue("finite.storage.native", 1);
            var firstShader = state.FiniteStorageShader(0x11223344, rgba, mixedBuffer);
            var firstBuffer = mixedBuffer ? CaptureBuffer() : null;
            var firstSequence = state.AdmitFiniteStorage(first, firstShader, firstBuffer);
            Assert.True(firstSequence > 0);
            host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 1, address + (ulong)imageBytes, 0x13579BDF));
            var firstEop = (long)Field("_enqueuedGuestWorkSequence").GetValue(null)!;
            Assert.Equal(expected, ReadCanonical());
            Assert.False(MetalVideoPresenter.WaitForGuestWork(firstSequence, 0));
            if (lateFailure is not null)
            {
                if (lateFailure == "writer")
                    Field("_finiteStorageAfterAppliedForControl").SetValue(null, (Action)(() =>
                    {
                        Assert.True(physical.TryWriteBacking(address, new byte[] { 0x5B }));
                    }));
                else Field("_finiteStorageAfterCanonicalVersionForControl").SetValue(null,
                    (Action)(() => GuestImageWriteTracker.NotifyCompletedMappedGpuWrite(address, 1)));
                var failure = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                Assert.IsType<GpuFatalException>(failure.InnerException);
                Assert.False(MetalVideoPresenter.WaitForGuestWork(firstSequence, 0));
                Assert.False(MetalVideoPresenter.WaitForGuestWork(firstEop, 0));
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(storeOffset), 0x11223344);
                if (lateFailure == "writer") expected[0] = 0x5B;
                Assert.Equal(expected, ReadCanonical()); // real Applied bytes, no EOP label
                state.AssertFinitePipelineCompleted(firstShader);
                image = ((IDictionary)Field("_guestImages").GetValue(null)!)[address]!;
                owners.Image = image;
                var failedDebt = FiniteDebt(image);
                Assert.True((bool)StorageMember(failedDebt, "Failed")!);
                Assert.True((bool)StorageMember(failedDebt, "Pending")!);
                if (lateFailure == "generation")
                    Assert.Equal(StorageMember(image, "ContentVersion"), StorageMember(failedDebt, "CanonicalVersion"));
                output.WriteLine($"finite-storage-native status=4; Applied full-byte image; late={lateFailure}; sticky-debt=true; EOP-unacknowledged; actual-participating-effect-only");
                return;
            }
            if (precommitBusyWindow is not null)
            {
                RunActualPrecommitOriginalBusy(state, physical, memory, owners, address, imageBytes,
                    storeOffset, expected, firstSequence, firstEop, firstShader, ReadCanonical, precommitBusyWindow);
                return;
            }
            if (phaseBusyWindow is not null)
            {
                RunActualStoragePhaseOriginalBusy(state, physical, memory, owners, address, imageBytes,
                    storeOffset, expected, firstSequence, firstEop, firstShader, ReadCanonical, phaseBusyWindow, mixedBuffer);
                return;
            }
            state.DrainWithControlledBudget();
            Assert.True(MetalVideoPresenter.WaitForGuestWork(firstEop, 0));
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(storeOffset), 0x11223344);
            if (mixedBuffer) BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes + 16384), 0xCAFEBABE);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes), 0x13579BDF);
            Assert.Equal(expected, ReadCanonical()); // every texel, padding and label byte
            state.AssertFinitePipelineCompleted(firstShader);
            output.WriteLine("finite-storage-native first status=4; all-owned canonical bytes including disjoint EOP verified");
            image = ((IDictionary)Field("_guestImages").GetValue(null)!)[address]!;
            owners.Image = image;
            Assert.NotNull(image);
            AssertFiniteDebtSettled(image);
            if (originalVolumes) ValidateOriginalVolumes();
            var ownOrigin = StorageMember(image, "StorageCpuOrigin")!;
            var ownMapping = (CpuMappedCopyObservation)StorageMember(ownOrigin, "Mapping")!;
            Assert.NotSame(originalMapping, ownMapping);
            Assert.Equal(originalMapping.Segments, ownMapping.Segments);
            Assert.Equal(observation, StorageMember(ownOrigin, "Observation"));
            Assert.Equal(rgba ? 70u : 53u, (uint)(MtlPixelFormat)StorageMember(image, "Format")!);
            Assert.Equal(3u, (uint)StorageMember(image, "TextureType")!);

            if (bodyBusyWindow is not null)
            {
                // Arm only after the initial actual status4/canonical publication proof.
                if (bodyBusyWindow == "cancel-unknown")
                    Assert.IsType<BodyBusyRoutedMemory>(cpuSource).ArmValidationCancellation();
                RunBodyBusyOriginalRetry(state, physical, memory, owners, address, imageBytes, expected, image,
                    ownOrigin, ReadCanonical, bodyBusyWindow);
                return;
            }
            if (busyOriginalRetry is not null)
            {
                Assert.True(rgba && originalVolumes && !overlappingCpuWrite && !mixedBuffer && lateFailure is null);
                RunBusyOriginalRetry(state, physical, memory, host, owners, address, imageBytes,
                    expected, image, ownOrigin, ReadCanonical, busyOriginalRetry);
                return;
            }

            if (overlappingCpuWrite)
            {
                // A real ordinary participating canonical write bypasses the
                // tracker, so the range ledger itself must reject this conflict.
                Assert.True(physical.TryWriteBacking(address, new byte[] { 0x5B }));
                expected[0] = 0x5B;
            }
            var next = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            Assert.Empty(next.RgbaPixels); // actual known native input, no new CPU seed authority
            Assert.Same(ownOrigin, StorageMember(image, "StorageCpuOrigin"));
            var secondShader = state.FiniteStorageShader(0xAABBCCDD, rgba, mixedBuffer);
            var secondBuffer = mixedBuffer ? CaptureBuffer() : null;
            var secondSequence = state.AdmitFiniteStorage(next, secondShader, secondBuffer);
            Assert.True(secondSequence > firstEop);
            host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 2, address + (ulong)imageBytes, 0x2468ACE0));
            var secondEop = (long)Field("_enqueuedGuestWorkSequence").GetValue(null)!;
            Assert.Equal(expected, ReadCanonical());
            if (overlappingCpuWrite)
            {
                var failure = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                var fatal = Assert.IsType<GpuFatalException>(failure.InnerException);
                Assert.Contains("canonical publication preparation was refused", fatal.InnerException!.Message);
                Assert.False(MetalVideoPresenter.WaitForGuestWork(secondSequence, 0));
                Assert.False(MetalVideoPresenter.WaitForGuestWork(secondEop, 0));
                Assert.Equal(expected, ReadCanonical()); // no overwritten conflict or false second label
                var debt = FiniteDebt(image);
                Assert.True((bool)StorageMember(debt, "Pending")!);
                Assert.True((bool)StorageMember(debt, "Failed")!);
                Assert.Single(reports);
                output.WriteLine("finite-storage-native overlap veto retained all-owned canonical bytes; second work/EOP unacknowledged");
            }
            else
            {
                state.DrainWithControlledBudget();
                Assert.True(MetalVideoPresenter.WaitForGuestWork(secondEop, 0));
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(storeOffset), 0xAABBCCDD);
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes), 0x2468ACE0);
                Assert.Equal(expected, ReadCanonical());
                state.AssertFinitePipelineCompleted(secondShader);
                AssertFiniteDebtSettled(image);
                if (originalVolumes) ValidateOriginalVolumes();
                var secondOrigin = StorageMember(image, "StorageCpuOrigin")!;
                Assert.NotSame(ownMapping, StorageMember(secondOrigin, "Mapping"));
                Assert.Equal(originalMapping.Segments, ((CpuMappedCopyObservation)StorageMember(secondOrigin, "Mapping")!).Segments);
                Assert.Equal(observation, StorageMember(secondOrigin, "Observation"));
                Assert.Empty(reports);
                output.WriteLine("finite-storage-native second status=4; all-owned canonical bytes including disjoint EOP verified");
            }
            output.WriteLine($"Actual Mac/PVM/Tracked original-copy→SubmitCompute→Drain→typedGPU staging→status4→checked canonical scatter→EOP; two-publication overlap={overlappingCpuWrite}; exact typed array; all-owned canonical bytes compared. Ordinary participating route only; no native/JIT writer or guest ISA qualification.");

            bool MaterializeOriginalVolumes()
            {
                // Both source scopes remain within the continuously owned PVM
                // mapping. Their native writes are deliberately NOT published;
                // the tested canonical publisher remains the RGBA1024 class.
                using var volumeQueue = MetalVideoPresenter.EnterGuestQueue("finite.volume.native", 1);
                for (var kind = 0; kind < 2; kind++)
                {
                    var volumeAddress = owners.BodyVolumePhysical is not null
                        ? owners.BodyVolumeAddress + (ulong)(kind + 1) * 65536
                        : address + (ulong)imageBytes + (ulong)(kind + 1) * 65536;
                    uint[] volumeRaw = kind == 0
                        ? [(uint)(volumeAddress >> 8), (uint)(volumeAddress >> 40) | 0x03C00000, 0, 0xA1B00FAC, 0, 0x00700000, 0, 0]
                        : [(uint)(volumeAddress >> 8), (uint)(volumeAddress >> 40) | 0xC3C00000, 0x0003C003, 0xA0000FAC, 15, 0x00700000, 0, 0];
                    Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(volumeRaw, out var volumeDescriptor), "finite setup: original volume descriptor decode");
                    var volumeBinding = new AgcExports.TranslatedImageBinding(volumeDescriptor, true, 0, [], IsArrayed: false,
                        ResourceDescriptor: volumeRaw, Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
                    var seed = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [volumeBinding], out _));
                    owners.VolumeSeeds.Add(seed);
                    Assert.Equal(65536UL, seed.SourceByteCount);
                    var initial = expected.AsSpan(imageBytes + (kind + 1) * 65536, 65536);
                    Assert.Equal(initial[..(kind == 0 ? 4 : 65536)].ToArray(), seed.RgbaPixels);
                    var original = Call("StorageCpuOrigin", seed)!;
                    var mapping = (CpuMappedCopyObservation)StorageMember(original, "Mapping")!;
                    Assert.Equal(volumeAddress, mapping.FootprintAddress); Assert.Equal(65536UL, mapping.FootprintByteCount);
                    Assert.All(mapping.Segments, part => Assert.Equal(kernelPage, part.ProviderPageSize));
                    var shader = state.VolumeOriginalShader(seed.Width, seed.Height, seed.Depth);
                    long sequence;
                    if (queueRetentionWindow is not null)
                    {
                        var watchedSource = (ICpuMappedReadWatchSource)memory;
                        var prepared = watchedSource.TryPrepareMappedReadValidation(mapping, out var held);
                        if (held is not null)
                        { owners.ConstructorReservation = held; owners.ConstructorReservationUnconfirmed = true; }
                        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, prepared); Assert.NotNull(held);
                        Exception? reservationFailure = null;
                        // This is a REAL provider constructor exclusion. Queue
                        // retention must not borrow its authority or require
                        // generic watch currentness while it remains held.
                        try
                        {
                            Assert.False(watchedSource.IsMappedCopyWriteWatchCurrent(mapping));
                            Assert.True(held!.IsOriginalCurrent(mapping));
                            sequence = state.AdmitFiniteStorage(seed, shader);
                            Assert.True(sequence > 0, "finite setup: original volume public admission sequence");
                        }
                        catch (Exception failure) { reservationFailure = failure; throw; }
                        finally
                        {
                            // Exact one attempt: a thrown release leaves the
                            // original receipt and all owners quarantined.
                            try
                            {
                                held!.Dispose(); owners.ConstructorReservationUnconfirmed = false;
                                owners.ConstructorReservation = null;
                            }
                            catch (Exception cleanup)
                            {
                                if (reservationFailure is null) throw;
                                try { reservationFailure.Data["HeldConstructorReservationCleanup"] = cleanup; } catch { }
                            }
                        }
                        Assert.True(watchedSource.IsMappedCopyWriteWatchCurrent(mapping));
                    }
                    else sequence = state.AdmitFiniteStorage(seed, shader);
                    Assert.True(sequence > 0, "finite setup: original volume public admission sequence");
                    // Actual frontend disposal after public Submit, BEFORE the
                    // real Drain transfers ownership to the native image.
                    MetalVideoPresenter.ReleasePreparedTextureSources([seed]);
                    Assert.True(((ICpuMappedReadWatchSource)memory).IsMappedCopyWriteWatchCurrent(mapping), "finite setup: original volume watch after frontend release");
                    if (queueRetentionWindow == "overlap" && kind == 1)
                    {
                        var completedBefore = (long)Field("_completedGuestWorkSequence").GetValue(null)!;
                        Assert.True(physical.TryWriteBacking(volumeAddress, new byte[] { 0xE7 }));
                        expected[imageBytes + (kind + 1) * 65536] = 0xE7;
                        var failed = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                        Assert.IsType<GpuFatalException>(failed.InnerException);
                        Assert.Equal(completedBefore, Field("_completedGuestWorkSequence").GetValue(null));
                        Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
                        Assert.False(((IDictionary)Field("_guestImages").GetValue(null)!).Contains(volumeAddress));
                        Assert.Equal(expected, ReadCanonical());
                        output.WriteLine("original-volume-queue held_constructor_submit=1 released_before_drain=1 actual_overlapping_writer_veto=1 no_native_owner=1 no_ack=1");
                        return false;
                    }
                    var controlKind = constructionBusyWindow == "overlap" ? 1 : 0;
                    var constructorControl = constructionBusyWindow is not null && kind == controlKind
                        ? new NativeConstructionBusyControl(physical, memory, host, owners, address, imageBytes,
                            expected, seed, original, sequence, constructionBusyWindow, output) : null;
                    Exception? constructionFailure = null;
                    Field("_finiteStorageWritebackEnabled").SetValue(null, false);
                    try
                    {
                        if (constructionBusyWindow == "overlap" && constructorControl is not null)
                        {
                            var failed = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                            Assert.IsType<GpuFatalException>(failed.InnerException);
                            constructorControl.AssertRefused();
                            Assert.Equal(expected, ReadCanonical());
                            return false; // first real volume status4 retained; refused second never encoded/acknowledged
                        }
                        state.DrainWithControlledBudget();
                        constructorControl?.AssertContinued();
                    }
                    catch (Exception failure) { constructionFailure = failure; throw; }
                    finally
                    {
                        try { Field("_finiteStorageWritebackEnabled").SetValue(null, true); }
                        catch (Exception cleanup)
                        {
                            if (constructionFailure is null) { constructorControl?.Finish(cleanup); throw; }
                            try { constructionFailure.Data["ConstructionFixtureFlagCleanup"] = cleanup; } catch { }
                        }
                        constructorControl?.Finish(constructionFailure);
                    }
                    Call("WaitForFlushedGuestCommands");
                    Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
                    state.AssertFinitePipelineCompleted(shader);
                    var nativeImage = ((IDictionary)Field("_guestImages").GetValue(null)!)[volumeAddress]!;
                    owners.VolumeImages.Add(nativeImage);
                    Assert.Same(original, StorageMember(nativeImage, "StorageCpuOrigin"));
                    Assert.Equal(7u, StorageMember(nativeImage,"TextureType")); Assert.Equal(73u,(uint)(MtlPixelFormat)StorageMember(nativeImage,"Format")!);
                    Assert.Equal(2, StorageMember(nativeImage,"ContentVersion"));
                    var tight = new byte[checked((int)(seed.Width*seed.Height*seed.Depth*4))];
                    for (uint z=0;z<seed.Depth;z++) for(uint y=0;y<seed.Height;y++)
                        initial.Slice(checked((int)((z*seed.Height+y)*seed.Pitch*4)), checked((int)(seed.Width*4)))
                            .CopyTo(tight.AsSpan(checked((int)((z*seed.Height+y)*seed.Width*4))));
                    new byte[]{17,34,51,68}.CopyTo(tight,tight.Length-4);
                    Assert.Equal(tight, ReadOriginalVolume((nint)StorageMember(nativeImage,"Texture")!,seed.Width,seed.Height,seed.Depth));
                    Assert.Equal(expected,ReadCanonical()); // full backing/padding and neighboring scopes untouched
                    output.WriteLine($"original-volume-native kind={kind}; shape={seed.Width}x{seed.Height}x{seed.Depth}; pitch={seed.Pitch}; original=65536; native_status=4; all_native_bytes=true; all_canonical_neighbors=true; frontend_release_before_drain=true; held_constructor_queue_retention={queueRetentionWindow is not null}");
                }
                return true;
            }

            void ValidateOriginalVolumes()
            {
                foreach(var nativeImage in owners.VolumeImages)
                {
                    var original = StorageMember(nativeImage,"StorageCpuOrigin")!;
                    var mapping = (CpuMappedCopyObservation)StorageMember(original,"Mapping")!;
                    Assert.True(((ICpuMappedReadWatchSource)memory).IsMappedCopyWriteWatchCurrent(mapping), "finite original-cohort check: retained volume watch after canonical publication");
                    lock(Field("_gate").GetValue(null)!) Assert.True((bool)Call("IsSingletonVolumeNativeOwnerCurrentLocked",nativeImage,original)!);
                }
                output.WriteLine("original-volume-native disjoint canonical Applied/publication/EOP retained BOTH exact native watch owners");
            }

            GuestMemoryBuffer CaptureBuffer()
            {
                var bufferAddress = address + (ulong)imageBytes + 16384;
                var bytes = new byte[16384];
                Assert.Equal(CpuMappedCopyResult.Copied, MetalVideoPresenter.TryReadFiniteStorageBuffer(memory,
                    bufferAddress, bytes, out var mapping, out var session));
                Assert.Equal(expected.AsSpan(imageBytes + 16384, 16384).ToArray(), bytes);
                var buffer = new GuestMemoryBuffer(bufferAddress, bytes, bytes.Length, (ulong)bytes.Length, false, true);
                MetalVideoPresenter.RegisterFiniteStorageBufferOrigin(buffer, memory, mapping!, session);
                output.WriteLine($"finite-storage-native original writable-global footprint={mapping!.FootprintByteCount}; segments={mapping.Segments.Count}; disjoint-physical=true");
                return buffer;
            }

            byte[] ReadCanonical()
            {
                var bytes = new byte[expected.Length];
                Assert.True(physical.TryRead(address, bytes));
                return bytes;
            }
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null;
            var completionConfirmed = !owners.OrdinaryWriterUnconfirmed && !owners.ConstructorReservationUnconfirmed;
            // This fresh fixture owns every newly opened batch. Discard only its
            // uncommitted encoding, then wait/status-check every committed buffer.
            // An unknown outcome keeps all exact owners quarantined, not freed.
            try
            {
                if (completionConfirmed)
                {
                    state?.AbortFiniteStorageEncoding();
                    Call("WaitForFlushedGuestCommands");
                }
                else cleanup = new InvalidOperationException("Fixture ordinary writer or constructor reservation outcome is unconfirmed; exact owners remain quarantined.");
            }
            catch (Exception failure) { cleanup = failure; completionConfirmed = false; }
            if (completionConfirmed)
            {
                Attempt(() =>
                {
                    lock (Field("_gate").GetValue(null)!)
                    {
                        var registry = (IDictionary)Field("_guestImages").GetValue(null)!;
                        var candidate = registry[address];
                        // A failure can happen after native creation but before
                        // the happy-path lookup. Identify only this original
                        // memory/session/layout owner, never an address alone.
                        if (candidate is not null && owners.OriginalOrigin is { } original &&
                            StorageMember(candidate, "StorageCpuOrigin") is { } origin &&
                            ReferenceEquals(StorageMember(origin, "Memory"), owners.Memory) &&
                            ReferenceEquals(StorageMember(origin, "Layout"), StorageMember(original, "Layout")) &&
                            Equals(StorageMember(origin, "Session"), StorageMember(original, "Session")))
                            owners.Image ??= candidate;
                        if (owners.Image is not { } owned || !ReferenceEquals(candidate, owned)) return;
                        registry.Remove(address);
                        ((IDictionary)Field("_guestImageExtents").GetValue(null)!).Remove(address);
                        var handle = (nint)StorageMember(owned, "Texture")!;
                        owned.GetType().GetField("Texture")!.SetValue(owned, (nint)0);
                        Attempt(() => owned.GetType().GetMethod("ReleaseSnapshot")!.Invoke(owned, []));
                        if (handle != 0) Attempt(() => MetalNative.SendVoid(handle, MetalNative.Selector("release")));
                    }
                });
                // Consume each exact native/watch retain only after all own
                // command buffers completed. Unknown cleanup retains all PVM
                // backing; never release mappings after an uncertain outcome.
                foreach(var seed in owners.VolumeSeeds)
                {
                    Attempt(() =>
                    {
                        lock(Field("_gate").GetValue(null)!)
                        {
                            var registry=(IDictionary)Field("_guestImages").GetValue(null)!;
                            var candidate=registry[seed.Address];
                            var original=Call("StorageCpuOrigin",seed);
                            if(candidate is null || original is null || !ReferenceEquals(StorageMember(candidate,"StorageCpuOrigin"),original)) return;
                            Call("NoteFiniteStorageOwnerRetirementLocked",candidate);
                            registry.Remove(seed.Address); ((IDictionary)Field("_guestImageExtents").GetValue(null)!).Remove(seed.Address);
                            var handle=(nint)StorageMember(candidate,"Texture")!;
                            candidate.GetType().GetField("Texture")!.SetValue(candidate,(nint)0);
                            Attempt(()=>candidate.GetType().GetMethod("ReleaseSnapshot")!.Invoke(candidate,[]));
                            if(handle!=0) Attempt(()=>MetalNative.SendVoid(handle,MetalNative.Selector("release")));
                        }
                    });
                    Attempt(()=>MetalVideoPresenter.ReleasePreparedTextureSources([seed]));
                    Attempt(()=>GuestImageWriteTracker.Untrack(seed.Address));
                }
                if (address != 0) Attempt(() => GuestImageWriteTracker.Untrack(address));
                if (state is not null) Attempt(state.Dispose);
                // Do not free its mappings after uncertain native teardown.
                if (cleanup is null && owners.BodyVolumePhysical is { } bodyPhysical) Attempt(bodyPhysical.Dispose);
                if (cleanup is null && physical is not null) Attempt(physical.Dispose);
                // Negative Busy rows keep their original receipt/session until
                // every real owned native resource and mapping has retired.
                if (cleanup is null) Attempt(() => owners.AfterOwnedRetirement?.Invoke());
            }
            Attempt(() => Field("_finiteStorageAfterAppliedForControl").SetValue(null, oldAfterApplied));
            Attempt(() => Field("_finiteStorageAfterCanonicalVersionForControl").SetValue(null, oldAfterVersion));
            Attempt(() => Field("_finiteStorageWritebackEnabled").SetValue(null, oldFinite));
            Attempt(() => Field("_guestMemory").SetValue(null, oldMemory));
            Attempt(() => backendField.SetValue(null, oldBackend));
            if (!completionConfirmed || cleanup is not null) finiteStorageFixtureQuarantine = owners;
            if (cleanup is not null)
            {
                if (primary is not null) { try { primary.Data["FiniteStorageFixtureCleanupFailure"] = cleanup; } catch { } }
                else ExceptionDispatchInfo.Capture(cleanup).Throw();
            }
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { cleanup ??= error; }
            }
        }
    }

    private static object? StorageMember(object source, string name)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return source.GetType().GetField(name, instance)?.GetValue(source) ?? source.GetType().GetProperty(name, instance)?.GetValue(source);
    }
    private static object FiniteDebt(object image)
    {
        var table = Field("_finiteStorageDebts").GetValue(null)!;
        object?[] args = [image, null];
        Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
        return args[1]!;
    }
    private static void AssertFiniteDebtSettled(object image)
    {
        var debt = FiniteDebt(image);
        Assert.False((bool)StorageMember(debt, "Pending")!);
        Assert.False((bool)StorageMember(debt, "Failed")!);
        Assert.Equal(StorageMember(image, "ContentVersion"), StorageMember(debt, "CanonicalVersion"));
        Assert.Same(StorageMember(image, "StorageCpuOrigin"), StorageMember(debt, "CanonicalOrigin"));
    }

    private sealed partial class NativeState
    {
        public void AbortFiniteStorageEncoding() => AbortBorrowedConsumerBatch();

        public MetalCompiledGuestShader FiniteStorageShader(uint value, bool rgba = false, bool mixedBuffer = false)
        {
            var resource = new ImageResource { ResourceClass = ImageResourceClass.Storage,
                NumericClass = rgba ? ImageNumericClass.Float : ImageNumericClass.Uint, Dimension = ImageDimension.Dim2DArray, Written = true };
            var kind = ImageDescriptorBinding.ForImage(resource)!.Value;
            var layout = Gen5MslArgumentLayout.Build(new BindingLayout { Descriptors = mixedBuffer
                ? [new(DescriptorBindingKind.Buffers, [0]), new(kind, [0])] : [new(kind, [0])] });
            var pointer = mixedBuffer ? layout.Fields.Single(field => field.FieldKind == MslArgumentFieldKind.BufferPointers) : null;
            var count = mixedBuffer ? layout.Fields.Single(field => field.FieldKind == MslArgumentFieldKind.BufferByteCounts) : null;
            var bufferFields = mixedBuffer ? $"array<device uint*, 1> buffers [[id({pointer!.FirstId})]]; array<uint, 1> buffer_bytes [[id({count!.FirstId})]];" : "";
            var bufferStore = mixedBuffer ? "if (resources.buffer_bytes[0] >= 4u) resources.buffers[0][0] = 0xCAFEBABEu;" : "";
            var texture = Assert.Single(layout.Fields.Where(field => field.FieldKind == MslArgumentFieldKind.Textures));
            var componentType = rgba ? "float" : "uint";
            var valueExpression = rgba
                ? $"float4({value & 255}u, {(value >> 8) & 255}u, {(value >> 16) & 255}u, {value >> 24}u) / 255.0f"
                : $"uint4({value}u, 0u, 0u, 1u)";
            var coordinates = rgba ? "uint2(257u, 511u)" : "uint2(0u)";
            var source = $$"""
                #include <metal_stdlib>
                using namespace metal;
                struct Gen5StageResources {
                    {{bufferFields}}
                    array<texture2d_array<{{componentType}}, access::read_write>, 1> {{texture.Name}} [[id({{texture.FirstId}})]];
                };
                kernel void finite_storage_write(constant Gen5StageResources& resources [[buffer(0)]]) {
                    resources.{{texture.Name}}[0].write({{valueExpression}}, {{coordinates}}, 0u);
                    {{bufferStore}}
                }
                """;
            var shader = new MetalCompiledGuestShader(new Gen5MslShader(source, "finite_storage_write", Gen5MslStage.Compute, 0, ArgumentLayout: layout));
            consumerShaders.Add(shader); // existing exact shader/cache cleanup owner
            return shader;
        }
        public long AdmitFiniteStorage(GuestDrawTexture texture, MetalCompiledGuestShader shader, GuestMemoryBuffer? buffer = null) =>
            MetalVideoPresenter.SubmitComputeDispatch(0, shader, [texture], buffer is null ? [] : [buffer], 1, 1, 1, 0, 0, 0, false,
                new GuestStageBindings(GuestStageKind.Compute, buffer is null ? [] : [0], [0], [], [], null, [], [], false, false, 0));
        public void AssertFinitePipelineCompleted(MetalCompiledGuestShader shader)
        {
            Assert.NotEqual((nint)0, shader.CachedLibrary);
            var pipelines = (IDictionary)Field("_computePipelineCache").GetValue(null)!;
            Assert.True(pipelines.Contains(shader)); Assert.NotEqual((nint)0, pipelines[shader]);
            var commandBuffer = (nint)Field("_lastFlushedCommandBuffer").GetValue(null)!;
            Assert.NotEqual((nint)0, commandBuffer);
            Assert.Equal((nint)4, MetalNative.Send(commandBuffer, MetalNative.Selector("status")));
        }
    }
}

internal sealed class FiniteStoragePublicationNativeFactAttribute : FactAttribute
{
    public FiniteStoragePublicationNativeFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
            Skip = "Finite typed publication requires an exclusive fresh Mac provider/Metal device worker.";
    }
}
