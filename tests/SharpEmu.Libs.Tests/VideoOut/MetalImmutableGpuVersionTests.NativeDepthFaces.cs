// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Kernel;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    private static object? nativeDepthFixtureQuarantine;

    [NativeDepthFacesFact]
    public void ActualColdSixDepthProducersAssembleOnSameCommandBuffer() => RunNativeDepthFaces(false);

    [NativeDepthFacesFact]
    public void ActualColdSixDepthProducersAssembleAfterOriginalSuccessfulCompletion() => RunNativeDepthFaces(true);

    private void RunNativeDepthFaces(bool completedFirst)
    {
        NativeDepthAssert(DeviceRequested());
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"), RuntimeInformation.ProcessArchitecture.ToString());
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"), RuntimeInformation.FrameworkDescription);
        Assert.Null(nativeDepthFixtureQuarantine);
        output.WriteLine($"native-depth-faces architecture={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription} completed_first={completedFirst}");
        var oldFlag = MetalDepthFacePlacement.Enabled;
        var oldMemory = Field("_guestMemory").GetValue(null);
        var oldOwner = Field("_renderOwner").GetValue(null);
        var oldBatchQueue = Field("_batchQueue").GetValue(null);
        var backendField = typeof(GuestGpu).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldBackend = backendField.GetValue(null);
        NativeState? state = null;
        PhysicalVirtualMemory? physical = null;
        NativeDepthCheckedMemory? memory = null;
        IDisposable? queueScope = null;
        nint readback = 0;
        ulong address = 0;
        Exception? primary = null;
        var safeToRelease = false;
        try
        {
            state = new NativeState();
            Field("_textureSourceGraphEnabled").SetValue(null, false);
            Field("_renderOwner").SetValue(null, Thread.CurrentThread); // this fixture's actual rendering owner
            Assert.Null(Field("_guestCommandBufferStatusOverride").GetValue(null));
            Assert.Null(Field("_guestCommandBufferWaitOverride").GetValue(null));
            Assert.Empty((IDictionary)Field("_transientTargets").GetValue(null)!);
            output.WriteLine($"native-depth-faces device={state.DeviceName}");
            Assert.Empty((IDictionary)Field("_nativeDepthFaces").GetValue(null)!);
            Assert.Empty((IDictionary)Field("_nativeDepthBatches").GetValue(null)!);
            foreach (var name in new[] { "_pendingSnapshotResources", "_retiredSnapshotResources", "_freeSnapshotResources" })
                Assert.Empty((IEnumerable)Field(name).GetValue(null)!); // fresh worker owns every new pooled retain
            physical = new PhysicalVirtualMemory(HostMemoryAuthority.Current, HostViewMemory.Create(),
                backingBytes: 16 * 1024 * 1024UL, preReserveGuestAddressSpace: false);
            const ulong totalBytes = 6 * MetalDepthFacePlacement.FaceBytes;
            NativeDepthAssert(physical.TryHoldRangeAtOrAbove(0x71F8_0000_00, totalBytes, 65536, out address));
            NativeDepthAssert(address != 0 && (address & 65535) == 0 && address >> 48 == 0);
            NativeDepthAssert(physical.TryHoldRange(address, totalBytes));
            NativeDepthAssert(physical.TryMapBacked(address, totalBytes, 0,
                GuestPageProtection.Read | GuestPageProtection.Write, out var mapFailure), $"map_failure={mapFailure}");
            NativeDepthAssert(physical.TryWriteBacking(address, Enumerable.Repeat((byte)0xED, (int)totalBytes).ToArray()));
            memory = new NativeDepthCheckedMemory(new TrackedCpuMemory(physical));
            var backend = new MetalGuestGpuBackend(); backendField.SetValue(null, backend);
            MetalVideoPresenter.AttachGuestMemory(memory);
            MetalDepthFacePlacement.Enabled = true;
            queueScope = MetalVideoPresenter.EnterGuestQueue("native.depth.faces", 1);
            var host = new MetalCommandStreamHost(memory, backend, backend);
            var originals = new List<MetalDepthFacePlacement.Observation>();
            var shaders = state.CreateNativeFaceShaders();
            for (uint face = 0; face < 6; face++)
            {
                var selected = address + face * MetalDepthFacePlacement.FaceBytes;
                Assert.False(GuestImageWriteTracker.TryGetContentToken(selected, out _));
                var words = new DepthTargetWords((uint)GuestDepthFormat.Z16 | 24u << 4, 0,
                    face | face << 13, 1023 | 1023u << 16, true, 0, 1, 2 | 4 | 7u << 4,
                    address, address, 0, 0, 0);
                var normalized = ImageRequestBuilders.DepthTarget(words, ((IRenderHost)host).FormatSupport)!.Value;
                var target = new GuestDepthTarget(address, address, 1024, 1024, (uint)GuestDepthFormat.Z16, 24,
                    (face + 1) / 8f, false, Registers: words);
                // Inject a real unrelated mapping after the ORIGINAL token
                // and mapping have been captured, during first currentness
                // validation. Initial Project must retain both observations.
                var mappingEpochBeforeProject = HostMemory.MappingGeneration;
                var unrelatedOffset = totalBytes + face * 65536UL;
                memory.OnNextCurrentQuery = () =>
                {
                    NativeDepthAssert(physical.TryHoldRangeAtOrAbove(address + unrelatedOffset, 65536, 65536, out var extraAddress));
                    NativeDepthAssert(extraAddress >= address + totalBytes);
                    NativeDepthAssert(physical.TryHoldRange(extraAddress, 65536));
                    NativeDepthAssert(physical.TryMapBacked(extraAddress, 65536, unrelatedOffset,
                        GuestPageProtection.Read | GuestPageProtection.Write, out var extraMapFailure),
                        $"initial_unrelated_map_failure={extraMapFailure}");
                };
                // Actual normalizer and original provider capture, not CWT insertion/prearm.
                target = MetalDepthFacePlacement.Project(memory, normalized, words, target);
                NativeDepthAssert(MetalDepthFacePlacement.TryGet(target, out var original));
                Assert.Equal(selected, target.Address);
                Assert.Equal(address, original.Mapping.FootprintAddress);
                Assert.Equal((face + 1UL) * MetalDepthFacePlacement.FaceBytes, original.Mapping.FootprintByteCount);
                Assert.Equal(selected, original.Mapping.SourceAddress); Assert.Equal(8UL, original.Mapping.SourceByteCount);
                Assert.Equal(MetalDepthFacePlacement.FaceBytes, original.Content.ByteCount);
                NativeDepthAssert(original.Content.Observed);
                Assert.NotEqual(mappingEpochBeforeProject, HostMemory.MappingGeneration);
                Assert.NotEqual(original.Content.MappingEpoch, HostMemory.MappingGeneration);
                NativeDepthAssert(memory.IsMappedCopyObservationCurrent(original.Mapping));
                NativeDepthAssert(GuestImageWriteTracker.TryGetObservedContentToken(selected,
                    MetalDepthFacePlacement.FaceBytes, out var originalToken));
                Assert.Equal(original.Content.Token, originalToken);
                Assert.All(original.Mapping.Segments, segment =>
                {
                    NativeDepthAssert(segment.BackingIdentity > 0 && segment.MappingIncarnation > 0);
                    Assert.Equal(GuestPageProtection.Read | GuestPageProtection.Write, segment.GuestAccess);
                    Assert.Equal(ulong.Parse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE")!), segment.ProviderPageSize);
                });
                originals.Add(original);
                // Later cold faces must never resize/replace the earlier original watch.
                foreach (var prior in originals) MetalDepthFacePlacement.RequireOriginalCurrent(prior);
                state.AdmitNativeFace(shaders.Vertex, shaders.Pixel, target);
            }
            output.WriteLine("native-depth-faces initial_project_unrelated_mapping_inside_validation=6 original_mapping_and_token_unchanged=1");
            Assert.Equal(6, memory.MappedCopies); Assert.Equal(0, memory.DirectReads);
            // Real unrelated canonical mapping effects must not invalidate the
            // retained face mappings or replace any original content token.
            var capturedEpoch = HostMemory.MappingGeneration;
            const ulong unrelatedBytes = 65536;
            NativeDepthAssert(physical.TryHoldRangeAtOrAbove(address + totalBytes, unrelatedBytes, 65536, out var unrelatedAddress));
            NativeDepthAssert(unrelatedAddress >= address + totalBytes);
            NativeDepthAssert(physical.TryHoldRange(unrelatedAddress, unrelatedBytes));
            NativeDepthAssert(physical.TryMapBacked(unrelatedAddress, unrelatedBytes, totalBytes,
                GuestPageProtection.Read | GuestPageProtection.Write, out var unrelatedMapFailure),
                $"unrelated_map_failure={unrelatedMapFailure}");
            var unrelatedEpoch = HostMemory.MappingGeneration;
            Assert.NotEqual(capturedEpoch, unrelatedEpoch);
            foreach (var original in originals)
            {
                Assert.NotEqual(original.Content.MappingEpoch, unrelatedEpoch);
                NativeDepthAssert(memory.IsMappedCopyObservationCurrent(original.Mapping));
                NativeDepthAssert(GuestImageWriteTracker.TryGetObservedContentToken(original.Address,
                    original.Content.ByteCount, out var unchangedToken));
                Assert.Equal(original.Content.Token, unchangedToken);
                MetalDepthFacePlacement.RequireOriginalCurrent(original);
            }
            output.WriteLine($"native-depth-faces unrelated_mapping_epoch_before={capturedEpoch} after={unrelatedEpoch} original_mapping_and_token_unchanged=1");
            memory.RejectDirectReads = true;
            uint[] raw = [(uint)(address >> 8), (uint)(address >> 40) | 0xC0700000,
                0x00FFC0FF, 0xB1800924, 5, 0x00700000, 0, 0];
            NativeDepthAssert(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, false, 0, [], true, ResourceDescriptor: raw,
                Shape: new ShaderImageShape(false, true, true, false, false, TextureNumericClass.Float, DepthCompare: true),
                Access: GuestImageAccess.Read);
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
            Assert.Equal(0, fallbacks); Assert.Empty(snapshot.RgbaPixels); Assert.Null(snapshot.TiledSource);
            Assert.Equal(6, memory.MappedCopies); Assert.Equal(0, memory.DirectReads);
            // Drain commits in its finally. Enroll the consumer BEFORE drain
            // so production texture resolution is a later encoder in that CB.
            if (!completedFirst) state.AdmitNativeFaceConsumer(shaders.Vertex, shaders.Pixel, snapshot);
            state.DrainWithControlledBudget();
            var producerCommand = (nint)Field("_lastFlushedCommandBuffer").GetValue(null)!;
            Assert.NotEqual((nint)0, producerCommand);
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            foreach (var original in originals) MetalDepthFacePlacement.RequireOriginalCurrent(original);
            nint depth;
            if (completedFirst)
            {
                Call("WaitForFlushedGuestCommands");
                var consumerCommand = (nint)Call("BeginBatchedGuestCommands", state.NativeFaceQueue)!;
                object?[] create = [state.NativeFaceDevice, consumerCommand, snapshot, false];
                depth = (nint)Call("CreateDrawTexture", create)!;
                Assert.False((bool)create[3]!);
            }
            else
            {
                // The actual resolved assembly was tagged by the real drain's
                // commit, on the SAME retaining CB as its six producers.
                var resources = ((IEnumerable)Field("_retiredSnapshotResources").GetValue(null)!).Cast<object>();
                var assembly = Assert.Single(resources, entry => (uint)Get(entry, "Format")! == (uint)MtlPixelFormat.Depth32Float);
                Assert.Equal(producerCommand, (nint)Get(assembly, "LastCommandBuffer")!);
                depth = (nint)Get(assembly, "Handle")!;
            }
            var command = (nint)Call("BeginBatchedGuestCommands", state.NativeFaceQueue)!;
            Assert.NotEqual((nint)0, command);
            Assert.NotEqual((nint)0, depth); // pool owns, no unrelated construction retain
            Assert.Equal((nint)MtlPixelFormat.Depth32Float, MetalNative.Send(depth, MetalNative.Selector("pixelFormat")));
            Assert.Equal((nint)3, MetalNative.Send(depth, MetalNative.Selector("textureType")));
            Assert.Equal((nint)6, MetalNative.Send(depth, MetalNative.Selector("arrayLength")));
            readback = MetalNative.SendNewBuffer(state.NativeFaceDevice, MetalNative.Selector("newBufferWithLength:options:"), 6 * 1024 * 1024 * 4, 0);
            Assert.NotEqual(0, readback);
            var blit = MetalNative.Send(command, MetalNative.Selector("blitCommandEncoder")); Assert.NotEqual(0, blit);
            Exception? blitFailure = null;
            try
            {
                for (uint face = 0; face < 6; face++)
                    MetalNative.SendCopyTextureToBuffer(blit, MetalNative.Selector(
                        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                        depth, face, 0, default, new MtlSize { Width = 1024, Height = 1024, Depth = 1 },
                        readback, face * 1024 * 1024 * 4, 1024 * 4, 1024 * 1024 * 4);
            }
            catch (Exception error) { blitFailure = error; }
            try { MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding")); }
            catch (Exception error) { NativeDepthPreserve(ref blitFailure, error); }
            if (blitFailure is not null) ExceptionDispatchInfo.Capture(blitFailure).Throw();
            Call("WaitForFlushedGuestCommands");
            Assert.Equal((nint)4, MetalNative.Send(command, MetalNative.Selector("status")));
            safeToRelease = true;
            var actual = new byte[6 * 1024 * 1024 * 4];
            var contents = MetalNative.Send(readback, MetalNative.Selector("contents"));
            Assert.NotEqual((nint)0, contents);
            Marshal.Copy(contents, actual, 0, actual.Length);
            for (var face = 0; face < 6; face++)
                for (var pixel = 0; pixel < 1024 * 1024; pixel++)
                    Assert.Equal((face + 1) / 8f, BitConverter.ToSingle(actual, (face * 1024 * 1024 + pixel) * 4));
            Assert.Equal(6, memory.MappedCopies); Assert.Equal(0, memory.DirectReads);
            // A later readback receipt does not consume the earlier producer's
            // retained receipt. Observe that exact original CB through the real
            // status4 path before asserting its owned batch was retired.
            if (!completedFirst)
            {
                Assert.Equal((nint)4, MetalNative.Send(producerCommand, MetalNative.Selector("status")));
                Call("WaitForCommittedCommandBuffer", producerCommand); // consumes its owned retain; do not use this pointer again
            }
            Assert.Empty((IDictionary)Field("_nativeDepthBatches").GetValue(null)!);
            // Refused logical queue cannot replace the original captured cohort.
            using (MetalVideoPresenter.EnterGuestQueue("other.depth.queue", 2))
                Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                    new CpuContext(memory, Generation.Gen5), [binding], out _));
            // A real renderer lifetime retirement invalidates the original captured view.
            Call("ReleaseSelectedStorageMipImages");
            var rejectCommand = state.Buffer();
            var stale = Assert.Throws<TargetInvocationException>(() => Call("CreateDrawTexture", state.NativeFaceDevice, rejectCommand, snapshot, false));
            Assert.IsType<NotSupportedException>(stale.InnerException);
            output.WriteLine($"native-depth-faces status=4 faces=6 full_float_texels=6291456 bytes=25165824 original_cold_watches=6 original_mapped_probes=6 zero_CPU_shadow_reads=1 same_CB={!completedFirst} stale_session_veto=1 wrong_queue_veto=1");
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            Exception? cleanup = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { NativeDepthPreserve(ref cleanup, error); } }
            // Never release actual GPU/mapping owners after unknown completion.
            if (state is not null)
            {
                try { Call("WaitForFlushedGuestCommands"); safeToRelease = true; }
                catch (Exception error) { NativeDepthPreserve(ref cleanup, error); safeToRelease = false; }
            }
            if (safeToRelease)
            {
                if (readback != 0) { var owned = readback; readback = 0; Attempt(() => MetalNative.SendVoid(owned, MetalNative.Selector("release"))); }
                Attempt(() => Field("_closed").SetValue(null, true));
                Attempt(() => Call("RetireNativeDepthCubeFromRenderOwner"));
                if (state is not null) Attempt(state.DisposeNativeDepthFixture);
                if (address != 0)
                    for (uint face = 0; face < 6; face++)
                    { var selected = address + face * MetalDepthFacePlacement.FaceBytes; Attempt(() => GuestImageWriteTracker.Untrack(selected)); }
                if (cleanup is null && physical is not null) Attempt(physical.Dispose);
            }
            if (!safeToRelease || cleanup is not null)
                nativeDepthFixtureQuarantine = new object?[] { state, physical, memory, readback };
            Attempt(() => queueScope?.Dispose());
            Attempt(() => Field("_guestMemory").SetValue(null, oldMemory));
            Attempt(() => Field("_renderOwner").SetValue(null, oldOwner));
            Attempt(() => Field("_batchQueue").SetValue(null, oldBatchQueue));
            Attempt(() => backendField.SetValue(null, oldBackend));
            MetalDepthFacePlacement.Enabled = oldFlag;
            if (cleanup is not null) NativeDepthPreserve(ref primary, cleanup);
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static void NativeDepthAssert(bool condition, string? message = null,
        [CallerArgumentExpression("condition")] string expression = "") => Assert.True(condition, message ?? expression);

    private static void NativeDepthPreserve(ref Exception? primary, Exception cleanup)
    {
        if (primary is null) primary = cleanup;
        else { try { primary.Data["NativeDepthFixtureCleanup"] = primary.Data["NativeDepthFixtureCleanup"] is Exception prior ? new AggregateException(prior, cleanup) : cleanup; } catch (Exception) { } }
    }

    private sealed class NativeDepthFacesFact : FactAttribute
    {
        public NativeDepthFacesFact()
        { if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1") Skip = "Exclusive actual Mac/PVM/Metal worker required."; }
    }

    private sealed class NativeDepthCheckedMemory(TrackedCpuMemory actual) : ICpuMemory, ICpuMappedCopySource
    {
        public int DirectReads, MappedCopies;
        public bool RejectDirectReads;
        public Action? OnNextCurrentQuery;
        public bool TryRead(ulong address, Span<byte> bytes)
        { DirectReads++; if (RejectDirectReads) throw new InvalidOperationException("Native cube must not recover CPU shadow bytes."); return actual.TryRead(address, bytes); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => actual.TryWrite(address, bytes);
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong extent, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { MappedCopies++; return actual.TryReadMappedCopy(footprint, extent, source, bytes, out observation); }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation)
        {
            var current = actual.IsMappedCopyObservationCurrent(observation);
            var callback = OnNextCurrentQuery;
            OnNextCurrentQuery = null;
            callback?.Invoke();
            return current;
        }
    }

    private sealed partial class NativeState
    {
        private bool nativeDepthDisposed;
        public nint NativeFaceDevice => device;
        public nint NativeFaceQueue => queue;
        public (MetalCompiledGuestShader Vertex, MetalCompiledGuestShader Pixel) CreateNativeFaceShaders()
        {
            var vertex = new MetalCompiledGuestShader(new Gen5MslShader("""
                #include <metal_stdlib>
                using namespace metal;
                struct FaceVertex { float4 position [[position]]; };
                vertex FaceVertex depth_face_vertex(uint id [[vertex_id]]) {
                    const float2 p[3] = { float2(-1,-1), float2(3,-1), float2(-1,3) };
                    return FaceVertex{float4(p[id], 0.5f, 1.0f)};
                }
                """, "depth_face_vertex", Gen5MslStage.Vertex, 0));
            var pixel = new MetalCompiledGuestShader(new Gen5MslShader("""
                #include <metal_stdlib>
                using namespace metal;
                fragment float4 depth_face_pixel() { return float4(0,0,0,1); }
                """, "depth_face_pixel", Gen5MslStage.Pixel, 0));
            colorShaders.Add(vertex); colorShaders.Add(pixel);
            return (vertex, pixel); // controlled VS/PS; not guest ISA translation proof
        }
        public void AdmitNativeFace(MetalCompiledGuestShader vertex, MetalCompiledGuestShader pixel, GuestDepthTarget target)
        {
            var render = GuestRenderState.Default with { Depth = new(true, true, 7, ClearEnable: true) };
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(pixel, [], [], 0,
                [new GuestRenderTarget(0, 1024, 1024, 10, 0)], vertex, 3, 1, 4, null, [], render, target, 0);
        }
        public void AdmitNativeFaceConsumer(MetalCompiledGuestShader vertex, MetalCompiledGuestShader pixel, GuestDrawTexture snapshot) =>
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(pixel, [snapshot], [], 0,
                [new GuestRenderTarget(0, 1024, 1024, 10, 0)], vertex, 3, 1, 4, null, [], GuestRenderState.Default, null, 0);
        public void DisposeNativeDepthFixture()
        {
            if (nativeDepthDisposed) return; nativeDepthDisposed = true;
            Exception? first = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { NativeDepthPreserve(ref first, error); } }
            Attempt(AbortBorrowedConsumerBatch);
            Attempt(() => MetalVideoPresenter.ReleasePreparedTextureSources(captures));
            Attempt(() => Call("ShutdownTextureSourceNativeOwners"));
            Attempt(uploadArena.Dispose);
            var last = (nint)Field("_lastFlushedCommandBuffer").GetValue(null)!;
            Field("_lastFlushedCommandBuffer").SetValue(null, (nint)0);
            if (last != 0) Attempt(() => MetalNative.SendVoid(last, MetalNative.Selector("release")));
            // Fresh fixture owns all pool entries, all real committed uses were status4.
            foreach (var name in new[] { "_pendingSnapshotResources", "_retiredSnapshotResources", "_freeSnapshotResources" })
            {
                var list = (IList)Field(name).GetValue(null)!;
                var records = list.Cast<object>().ToArray(); list.Clear();
                foreach (var record in records)
                {
                    var handle = (nint)Get(record, "Handle")!; var cb = (nint)Get(record, "LastCommandBuffer")!;
                    record.GetType().GetField("Handle")!.SetValue(record, (nint)0);
                    record.GetType().GetField("LastCommandBuffer")!.SetValue(record, (nint)0);
                    if (cb != 0) Attempt(() => MetalNative.SendVoid(cb, MetalNative.Selector("release")));
                    if (handle != 0) Attempt(() => MetalNative.SendVoid(handle, MetalNative.Selector("release")));
                }
            }
            // Initially empty and used only by the controlled six face draws.
            var targets = (IDictionary)Field("_transientTargets").GetValue(null)!;
            var targetHandles = targets.Values.Cast<nint>().ToArray(); targets.Clear();
            foreach (var owned in targetHandles)
                if (owned != 0) Attempt(() => MetalNative.SendVoid(owned, MetalNative.Selector("release")));
            var cache = (IDictionary)Field("_pipelineCache").GetValue(null)!;
            foreach (var key in cache.Keys.Cast<object>().Where(key => colorShaders.Contains((MetalCompiledGuestShader)key.GetType().GetProperty("PixelShader")!.GetValue(key)!)).ToArray())
            { var owned = (nint)cache[key]!; cache.Remove(key); if (owned != 0) Attempt(() => MetalNative.SendVoid(owned, MetalNative.Selector("release"))); }
            foreach (var shader in colorShaders)
            { var owned = shader.CachedLibrary; shader.CachedLibrary = 0; if (owned != 0) Attempt(() => MetalNative.SendVoid(owned, MetalNative.Selector("release"))); }
            Field("_executingTextureSourceWork").SetValue(null, null);
            Field("_textureSourceGraphEnabled").SetValue(null, oldGraph);
            foreach (var owned in buffers.Concat(new[] { queue, device, pool }))
                if (owned != 0) Attempt(() => MetalNative.SendVoid(owned, MetalNative.Selector("release")));
            Attempt(trackerState.Dispose);
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
    }
}
