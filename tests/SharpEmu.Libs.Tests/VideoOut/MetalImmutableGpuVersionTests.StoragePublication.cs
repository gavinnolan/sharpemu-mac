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
        public TrackedCpuMemory? Memory;
        public object? OriginalOrigin;
        public object? Image;
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

    private void RunActualRuntimeTwoTypedPublications(bool overlappingCpuWrite, bool rgba = false, bool mixedBuffer = false, string? lateFailure = null)
    {
        Assert.True(DeviceRequested());
        var imageBytes = rgba ? 4194304 : 65536;
        var allocationBytes = imageBytes + 65536;
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
            Assert.True(physical.TryHoldRangeAtOrAbove(0x71F6_0000_00, (ulong)allocationBytes, 65536, out address));
            Assert.True(address != 0 && (address & 65535) == 0 && address >> 48 == 0);
            Assert.True(physical.TryHoldRange(address, (ulong)allocationBytes));
            Assert.True(physical.TryMapBacked(address, (ulong)allocationBytes, 65536,
                GuestPageProtection.Read | GuestPageProtection.Write, out var mapFailure), $"map failure={mapFailure}");
            var memory = new TrackedCpuMemory(physical);
            owners.Memory = memory;
            Assert.IsAssignableFrom<ICpuMappedCopySource>(memory);
            Assert.IsAssignableFrom<ICpuMappedPublicationSource>(memory);
            Field("_guestMemory").SetValue(null, memory);
            Field("_finiteStorageWritebackEnabled").SetValue(null, true);
            var expected = Enumerable.Range(0, allocationBytes).Select(i => (byte)(i * 19 + 7)).ToArray();
            if (rgba) Bgra1024Oracle.PhysicalSeed().CopyTo(expected, 0);
            else BinaryPrimitives.WriteUInt32LittleEndian(expected, 0x87654321);
            Assert.True(physical.TryWriteBacking(address, expected));
            uint[] raw = rgba
                ? [(uint)(address >> 8), (uint)(address >> 40) | 0xC3800000u, 0x00FFC0FF, 0xD1B00F2E, 0, 0x00700000, 0, 0]
                : [(uint)(address >> 8), 0x01400000, 0, 0xD1B00204, 0, 0x00700000, 0, 0];
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
            // No fixture pre-arm or injected receipt: the production cold copy
            // itself establishes its actual protection before its original
            // epoch/token and mapped bytes are captured.
            Assert.False(GuestImageWriteTracker.TryGetContentToken(address, out _));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], IsArrayed: true,
                ResourceDescriptor: raw, Shape: new ShaderImageShape(false, true, true, false, rgba ? TextureNumericClass.Float : TextureNumericClass.Uint),
                Access: GuestImageAccess.Write);
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
            Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var kernelPage));
            Assert.All(originalMapping.Segments, segment => Assert.Equal(kernelPage, segment.ProviderPageSize));
            output.WriteLine($"finite-storage-native provider_kernel_page={kernelPage}; original_footprint={originalMapping.FootprintByteCount}");
            var observation = StorageMember(originalOrigin, "Observation")!;
            Assert.True((bool)StorageMember(observation, "Observed")!); // actual production arm/copy observation, never injected
            Assert.Equal(HostMemory.MappingGeneration, (long)StorageMember(observation, "MappingEpoch")!);
            var host = new MetalCommandStreamHost(memory, backend, backend);
            var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
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
            var ownOrigin = StorageMember(image, "StorageCpuOrigin")!;
            var ownMapping = (CpuMappedCopyObservation)StorageMember(ownOrigin, "Mapping")!;
            Assert.NotSame(originalMapping, ownMapping);
            Assert.Equal(originalMapping.Segments, ownMapping.Segments);
            Assert.Equal(observation, StorageMember(ownOrigin, "Observation"));
            Assert.Equal(rgba ? 70u : 53u, (uint)(MtlPixelFormat)StorageMember(image, "Format")!);
            Assert.Equal(3u, (uint)StorageMember(image, "TextureType")!);

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
                var secondOrigin = StorageMember(image, "StorageCpuOrigin")!;
                Assert.NotSame(ownMapping, StorageMember(secondOrigin, "Mapping"));
                Assert.Equal(originalMapping.Segments, ((CpuMappedCopyObservation)StorageMember(secondOrigin, "Mapping")!).Segments);
                Assert.Equal(observation, StorageMember(secondOrigin, "Observation"));
                Assert.Empty(reports);
                output.WriteLine("finite-storage-native second status=4; all-owned canonical bytes including disjoint EOP verified");
            }
            output.WriteLine($"Actual Mac/PVM/Tracked original-copy→SubmitCompute→Drain→typedGPU staging→status4→checked canonical scatter→EOP; two-publication overlap={overlappingCpuWrite}; exact typed array; all-owned canonical bytes compared. Ordinary participating route only; no native/JIT writer or guest ISA qualification.");

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
            var completionConfirmed = true;
            // This fresh fixture owns every newly opened batch. Discard only its
            // uncommitted encoding, then wait/status-check every committed buffer.
            // An unknown outcome keeps all exact owners quarantined, not freed.
            try
            {
                state?.AbortFiniteStorageEncoding();
                Call("WaitForFlushedGuestCommands");
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
                if (address != 0) Attempt(() => GuestImageWriteTracker.Untrack(address));
                if (state is not null) Attempt(state.Dispose);
                // Do not free its mappings after uncertain native teardown.
                if (cleanup is null && physical is not null) Attempt(physical.Dispose);
            }
            Attempt(() => Field("_finiteStorageAfterAppliedForControl").SetValue(null, oldAfterApplied));
            Attempt(() => Field("_finiteStorageAfterCanonicalVersionForControl").SetValue(null, oldAfterVersion));
            Attempt(() => Field("_finiteStorageWritebackEnabled").SetValue(null, oldFinite));
            Attempt(() => Field("_guestMemory").SetValue(null, oldMemory));
            Attempt(() => backendField.SetValue(null, oldBackend));
            if (cleanup is not null) finiteStorageFixtureQuarantine = owners;
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
