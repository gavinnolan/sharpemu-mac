// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
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
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    private static object? nativeColorFixtureQuarantine;
    [NativeColorBorrowFact]
    public void ActualSelectedColorOriginalBorrowUsesLaterEncoderInSameCommandBuffer() => RunNativeColorBorrow(false);
    [NativeColorBorrowFact]
    public void ActualSelectedColorOriginalBorrowUsesOriginalSuccessfulStatus4Batch() => RunNativeColorBorrow(true);

    [NativeColorBorrowFact]
    public void ActualNativeColorPredecessorFailureConsumesEveryTemporaryRetainAndPreservesCause() =>
        RunNativeColorBorrow(true, failPredecessorCleanup: true);

    private void RunNativeColorBorrow(bool completedFirst, bool failPredecessorCleanup = false)
    {
        NativeDepthAssert(DeviceRequested());
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"), RuntimeInformation.ProcessArchitecture.ToString());
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"), RuntimeInformation.FrameworkDescription);
        Assert.Null(nativeColorFixtureQuarantine);
        output.WriteLine($"native-color-borrow architecture={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription} completed_first={completedFirst}");
        var oldPlacement = MetalColorMipPlacement.Enabled;
        var oldBorrow = MetalVideoPresenter.NativeColorBorrowEnabled;
        var oldMemory = Field("_guestMemory").GetValue(null);
        var oldOwner = Field("_renderOwner").GetValue(null);
        var oldQueue = Field("_batchQueue").GetValue(null);
        var oldPredecessorControl = Field("_nativeColorPredecessorValidationForControl").GetValue(null);
        var oldTemporaryRelease = Field("_computeTextureReleaseOverride").GetValue(null);
        var oldTemporaryFailure = Field("_textureSourceCleanupFailure").GetValue(null);
        NativeState? state = null; PhysicalVirtualMemory? physical = null;
        IDisposable? queueScope = null;
        TrackedCpuMemory? memory = null;
        GuestRenderTarget? target = null; GuestDrawTexture? snapshot = null;
        ulong address = 0;
        nint borrowed = 0, readback = 0;
        var safe = false;
        Exception? primary = null;
        try
        {
            state = new NativeState();
            Field("_textureSourceGraphEnabled").SetValue(null, false);
            Field("_renderOwner").SetValue(null, Thread.CurrentThread);
            Assert.Null(Field("_guestCommandBufferStatusOverride").GetValue(null));
            Assert.Null(Field("_guestCommandBufferWaitOverride").GetValue(null));
            Assert.Empty((IDictionary)Field("_guestImages").GetValue(null)!);
            Assert.Empty((IDictionary)Field("_guestDepthImages").GetValue(null)!);
            Assert.Empty((IDictionary)Field("_nativeColorBatches").GetValue(null)!);
            physical = new PhysicalVirtualMemory(HostMemoryAuthority.Current, HostViewMemory.Create(),
                backingBytes: 8 * 1024 * 1024UL, preReserveGuestAddressSpace: false);
            const ulong chainBytes = 0x2B0000;
            NativeDepthAssert(physical.TryHoldRangeAtOrAbove(0x71FC_0000_00, chainBytes, 65536, out address));
            NativeDepthAssert(address != 0 && (address & 65535) == 0 && address >> 48 == 0);
            NativeDepthAssert(physical.TryHoldRange(address, chainBytes));
            NativeDepthAssert(physical.TryMapBacked(address, chainBytes, 0, GuestPageProtection.Read | GuestPageProtection.Write, out var mapFailure), $"map_failure={mapFailure}");
            var originalBytes = Enumerable.Range(0, (int)chainBytes).Select(i => (byte)(i * 37 + i / 251)).ToArray();
            NativeDepthAssert(physical.TryWriteBacking(address, originalBytes));
            memory = new TrackedCpuMemory(physical);
            MetalVideoPresenter.AttachGuestMemory(memory);
            MetalColorMipPlacement.Enabled = true; MetalVideoPresenter.NativeColorBorrowEnabled = true;
            queueScope = MetalVideoPresenter.EnterGuestQueue("native.selected.color", 1);
            var words = new ColorTargetWords(address, 0, 0x40730, 0, 0x907FC1FF, 0x4DC6C000, 72, 0, 0, 0, 0);
            var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
            target = MetalColorMipPlacement.Project(memory, normalized, words,
                new GuestRenderTarget(address, 512, 512, (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27));
            NativeDepthAssert(MetalColorMipPlacement.TryGet(target, out var original));
            Assert.Equal(address + 0xB0000, target.Address); Assert.Equal(chainBytes, original.BackingByteCount);
            var shaders = state.CreateNativeColorBorrowShaders();
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shaders.Pixel, [], [], 0, [target], shaders.Vertex,
                3, 1, 4, null, [], GuestRenderState.Default, null, 0);
            // Actual frontend preparation leaves BEFORE the queue consumer.
            MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
            uint[] raw = [(uint)(target.Address >> 8), 0xC4700000 | (uint)(target.Address >> 40), 0x007FC07F, 0x91B00FAC, 0, 0x00700000, 0, 0];
            NativeDepthAssert(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, false, 0, [], ResourceDescriptor: raw,
                Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
            snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
            Assert.Equal(0, fallbacks); Assert.Empty(snapshot.RgbaPixels); Assert.Null(snapshot.TiledSource);
            if (completedFirst)
            {
                state.DrainWithControlledBudget();
                Call("WaitForFlushedGuestCommands");
            }
            else
            {
                object?[] take = [null]; NativeDepthAssert((bool)Call("TryTakeGuestWork", take)!);
                var pending = take[0]!;
                Call("ExecuteOffscreenGuestDraw", state.NativeFaceDevice, state.NativeFaceQueue, pending.GetType().GetProperty("Work")!.GetValue(pending)!);
                Call("CompleteGuestWork", pending);
            }
            var command = (nint)Call("BeginBatchedGuestCommands", state.NativeFaceQueue)!;
            object?[] create = [state.NativeFaceDevice, command, snapshot, false];
            borrowed = (nint)Call("CreateDrawTexture", create)!;
            Assert.True((bool)create[3]!); Assert.NotEqual((nint)0, borrowed);
            Assert.Equal((nint)MtlPixelFormat.Rgba16Float, MetalNative.Send(borrowed, MetalNative.Selector("pixelFormat")));
            Assert.Equal((nint)2, MetalNative.Send(borrowed, MetalNative.Selector("textureType")));
            readback = MetalNative.SendNewBuffer(state.NativeFaceDevice, MetalNative.Selector("newBufferWithLength:options:"), 0x200000, 0);
            Assert.NotEqual((nint)0, readback);
            var blit = MetalNative.Send(command, MetalNative.Selector("blitCommandEncoder")); Assert.NotEqual((nint)0, blit);
            Exception? encodingFailure = null;
            try
            {
                MetalNative.SendCopyTextureToBuffer(blit, MetalNative.Selector("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                    borrowed, 0, 0, default, new MtlSize { Width = 512, Height = 512, Depth = 1 }, readback, 0, 4096, 0x200000);
            }
            catch (Exception error) { encodingFailure = error; }
            try { MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding")); } catch (Exception cleanup) { NativeDepthPreserve(ref encodingFailure, cleanup); }
            if (encodingFailure is not null) ExceptionDispatchInfo.Capture(encodingFailure).Throw();
            var encodedRetain = borrowed; borrowed = 0; MetalNative.SendVoid(encodedRetain, MetalNative.Selector("release"));
            Call("WaitForFlushedGuestCommands"); Assert.Equal((nint)4, MetalNative.Send(command, MetalNative.Selector("status"))); safe = true;
            var actual = new byte[0x200000];
            var contents = MetalNative.Send(readback, MetalNative.Selector("contents")); Assert.NotEqual((nint)0, contents);
            Marshal.Copy(contents, actual, 0, actual.Length);
            ushort[] expected = [BitConverter.HalfToUInt16Bits((Half)0.25f), BitConverter.HalfToUInt16Bits((Half)0.5f),
                BitConverter.HalfToUInt16Bits((Half)0.75f), BitConverter.HalfToUInt16Bits((Half)1f)];
            for (var pixel = 0; pixel < 512 * 512; pixel++) for (var component = 0; component < 4; component++)
                Assert.Equal(expected[component], BitConverter.ToUInt16(actual, pixel * 8 + component * 2));
            var unchanged = new byte[(int)chainBytes]; NativeDepthAssert(memory.TryRead(address, unchanged)); Assert.Equal(originalBytes, unchanged);
            if (failPredecessorCleanup)
            {
                var originalFailure = new NotSupportedException("controlled predecessor boundary failure");
                var cleanupFailure = new InvalidOperationException("controlled release failure after exact native release");
                var released = new List<nint>();
                var next = MetalColorMipPlacement.Project(memory, normalized, words,
                    new GuestRenderTarget(address, 512, 512, (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27));
                target = next;
                MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shaders.Pixel, [snapshot, snapshot], [], 0, [next], shaders.Vertex,
                    3, 1, 4, null, [], GuestRenderState.Default, null, 0);
                MetalVideoPresenter.ReleasePreparedNativeColorTarget(next);
                var completedSequence = Field("_completedGuestWorkSequence").GetValue(null);
                Field("_nativeColorPredecessorValidationForControl").SetValue(null, (Action)(() => throw originalFailure));
                Field("_computeTextureReleaseOverride").SetValue(null, (Action<nint>)(handle =>
                {
                    released.Add(handle); MetalNative.SendVoid(handle, MetalNative.Selector("release"));
                    if (released.Count == 1) throw cleanupFailure;
                }));
                var rejected = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                var fatal = Assert.IsType<GpuFatalException>(rejected.InnerException);
                Assert.Same(originalFailure, fatal.InnerException);
                Assert.Equal(2, released.Count); Assert.Equal(released[0], released[1]);
                Assert.Same(cleanupFailure, Field("_textureSourceCleanupFailure").GetValue(null));
                Assert.Equal(completedSequence, Field("_completedGuestWorkSequence").GetValue(null));
                Field("_nativeColorPredecessorValidationForControl").SetValue(null, oldPredecessorControl);
                Field("_computeTextureReleaseOverride").SetValue(null, oldTemporaryRelease);
                output.WriteLine("native-color-borrow predecessor_failure_primary_preserved=1 temporary_owned_retains=2 each_release_attempted=1 no_ack=1 status=4 initial_full_native_bytes=2097152");
                goto FixtureFinished;
            }
            if (completedFirst)
            {
                // An already admitted readonly consumer must keep the prior
                // status4 texture after a later writer is admitted. The later
                // partial-mask encoder then loads that exact predecessor.
                MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shaders.Pixel, [snapshot], [], 0,
                    [target with { Address = 0, Registers = null }], shaders.Vertex,
                    3, 1, 4, null, [], GuestRenderState.Default, null, 0);
                MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
                var next = MetalColorMipPlacement.Project(memory, normalized, words,
                    new GuestRenderTarget(address, 512, 512, (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27));
                target = next;
                var partialShaders = state.CreateNativeColorBorrowShaders(redOnlySuccessor: true);
                MetalVideoPresenter.SubmitOffscreenTranslatedDraw(partialShaders.Pixel, [], [], 0, [next], partialShaders.Vertex,
                    3, 1, 4, null, [], GuestRenderState.Default with { Blends = [GuestBlendState.Default with { WriteMask = 1 }] }, null, 0);
                MetalVideoPresenter.ReleasePreparedNativeColorTarget(next);
                state.DrainWithControlledBudget(); Call("WaitForFlushedGuestCommands");
                snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
                command = (nint)Call("BeginBatchedGuestCommands", state.NativeFaceQueue)!;
                create = [state.NativeFaceDevice, command, snapshot, false]; borrowed = (nint)Call("CreateDrawTexture", create)!;
                Assert.True((bool)create[3]!); Assert.NotEqual((nint)0, borrowed);
                blit = MetalNative.Send(command, MetalNative.Selector("blitCommandEncoder")); Assert.NotEqual((nint)0, blit);
                encodingFailure = null;
                try
                {
                    MetalNative.SendCopyTextureToBuffer(blit, MetalNative.Selector("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                        borrowed, 0, 0, default, new MtlSize { Width = 512, Height = 512, Depth = 1 }, readback, 0, 4096, 0x200000);
                }
                catch (Exception error) { encodingFailure = error; }
                try { MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding")); } catch (Exception cleanup) { NativeDepthPreserve(ref encodingFailure, cleanup); }
                if (encodingFailure is not null) ExceptionDispatchInfo.Capture(encodingFailure).Throw();
                encodedRetain = borrowed; borrowed = 0; MetalNative.SendVoid(encodedRetain, MetalNative.Selector("release"));
                Call("WaitForFlushedGuestCommands"); Assert.Equal((nint)4, MetalNative.Send(command, MetalNative.Selector("status")));
                Marshal.Copy(contents, actual, 0, actual.Length);
                expected[0] = BitConverter.HalfToUInt16Bits((Half)0.875f);
                for (var pixel = 0; pixel < 512 * 512; pixel++) for (var component = 0; component < 4; component++)
                    Assert.Equal(expected[component], BitConverter.ToUInt16(actual, pixel * 8 + component * 2));
                NativeDepthAssert(memory.TryRead(address, unchanged)); Assert.Equal(originalBytes, unchanged);
                output.WriteLine("native-color-borrow completed_predecessor_partial_mask=1 queued_consumer_before_next_writer=1 successor_full_half_components=1048576");
            }
            using (MetalVideoPresenter.EnterGuestQueue("other.native.color", 2))
                Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            // Same ORIGINAL watched proof must reject an actual overlapping ordinary alias write.
            NativeDepthAssert(physical.TryWriteBacking(target.Address, new byte[] { 0xF1 }));
            var rejection = Assert.Throws<TargetInvocationException>(() => Call("CreateDrawTexture", state.NativeFaceDevice, state.Buffer(), snapshot, false));
            Assert.IsType<NotSupportedException>(rejection.InnerException);
            output.WriteLine($"native-color-borrow status=4 full_half_components=1048576 full_native_bytes=2097152 unchanged_original_chain_bytes={chainBytes} same_CB={!completedFirst} frontend_release_before_consume=1 wrong_queue_veto=1 overlapping_ordinary_writer_veto=1");
            FixtureFinished: ;
        }
        catch (Exception error) { primary = error; }
        finally
        {
            Exception? cleanup = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { NativeDepthPreserve(ref cleanup, error); } }
            Attempt(() => Field("_nativeColorPredecessorValidationForControl").SetValue(null, oldPredecessorControl));
            Attempt(() => Field("_computeTextureReleaseOverride").SetValue(null, oldTemporaryRelease));
            if (state is not null)
                try { Call("WaitForFlushedGuestCommands"); safe = true; }
                catch (Exception error) { NativeDepthPreserve(ref cleanup, error); safe = false; }
            if (safe)
            {
                if (snapshot is not null) Attempt(() => MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]));
                if (target is not null) Attempt(() => MetalVideoPresenter.ReleasePreparedNativeColorTarget(target));
                Attempt(() => Field("_closed").SetValue(null, true));
                Attempt(() => Call("RetireNativeColorFromRenderOwner"));
                if (target is not null)
                {
                    var images = (IDictionary)Field("_guestImages").GetValue(null)!;
                    var image = images[target.Address]; images.Remove(target.Address);
                    ((IDictionary)Field("_guestImageExtents").GetValue(null)!).Remove(target.Address);
                    if (image is not null)
                    {
                        var owned = (nint)Get(image, "Texture")!;
                        image.GetType().GetField("Texture")!.SetValue(image, (nint)0);
                        if (owned != 0) Attempt(() => MetalNative.SendVoid(owned, MetalNative.Selector("release")));
                    }
                    ((IDictionary)Field("_colorPhysicalAllocations").GetValue(null)!).Remove(address);
                }
                foreach (var owned in new[] { borrowed, readback }) if (owned != 0) Attempt(() => MetalNative.SendVoid(owned, MetalNative.Selector("release")));
                borrowed = 0; readback = 0;
                if (state is not null) Attempt(state.DisposeNativeDepthFixture);
                if (address != 0) Attempt(() => GuestImageWriteTracker.Untrack(address + 0xB0000));
                if (cleanup is null && physical is not null) Attempt(physical.Dispose);
            }
            if (!safe || cleanup is not null) nativeColorFixtureQuarantine = new object?[] { state, physical, memory, target, snapshot, borrowed, readback };
            Attempt(() => queueScope?.Dispose());
            Attempt(() => Field("_guestMemory").SetValue(null, oldMemory));
            Attempt(() => Field("_renderOwner").SetValue(null, oldOwner));
            Attempt(() => Field("_batchQueue").SetValue(null, oldQueue));
            Attempt(() => Field("_textureSourceCleanupFailure").SetValue(null, oldTemporaryFailure));
            MetalColorMipPlacement.Enabled = oldPlacement; MetalVideoPresenter.NativeColorBorrowEnabled = oldBorrow;
            if (cleanup is not null) NativeDepthPreserve(ref primary, cleanup);
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private sealed class NativeColorBorrowFact : FactAttribute
    {
        public NativeColorBorrowFact()
        { if (Environment.GetEnvironmentVariable("SHARPEMU_METAL_NATIVE_COLOR_BORROW_TESTS") != "1") Skip = "Fresh required-device native color worker not requested."; }
    }
    private sealed partial class NativeState
    {
        public (MetalCompiledGuestShader Vertex, MetalCompiledGuestShader Pixel) CreateNativeColorBorrowShaders(bool redOnlySuccessor = false)
        {
            var vertex = new MetalCompiledGuestShader(new Gen5MslShader("""
                #include <metal_stdlib>
                using namespace metal;
                struct ColorVertex { float4 position [[position]]; };
                vertex ColorVertex native_color_vs(uint id [[vertex_id]]) {
                    const float2 p[3] = { float2(-1,-1), float2(3,-1), float2(-1,3) };
                    return ColorVertex{float4(p[id],0,1)};
                }
                """, "native_color_vs", Gen5MslStage.Vertex, 0));
            var pixel = new MetalCompiledGuestShader(new Gen5MslShader("""
                #include <metal_stdlib>
                using namespace metal;
                fragment float4 native_color_fs() { return float4(0.25,0.5,0.75,1.0); }
                """.Replace("0.25", redOnlySuccessor ? "0.875" : "0.25"), "native_color_fs", Gen5MslStage.Pixel, 0));
            colorShaders.Add(vertex); colorShaders.Add(pixel); return (vertex, pixel);
        }
    }
}
