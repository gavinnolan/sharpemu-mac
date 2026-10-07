// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ConsumerNativePinOutlivesReleasedQueuedOwnerBeforeCommit(bool storage, bool discard)
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var firstBuffer = state.Buffer();
        var first = state.AdmitColor();
        state.EncodeClear(first, firstBuffer, new MtlClearColor { Red = 1, Alpha = 1 });
        state.Commit(firstBuffer); state.Wait(firstBuffer); state.Recycle();
        var oldRead = state.Capture(storage);
        var consumer = state.AdmitConsumer(oldRead, writeBack: false);
        MetalVideoPresenter.ReleasePreparedTextureSources([oldRead]);
        var secondBuffer = state.Buffer();
        var second = state.AdmitColor();
        state.EncodeClear(second, secondBuffer, new MtlClearColor { Green = 1, Alpha = 1 });
        state.Commit(secondBuffer); state.Wait(secondBuffer); state.Recycle();

        using var result = state.ExecuteUnflushedConsumer(consumer);
        var capturedHandle = (nint)Get(first, "ImmutableTexture")!;
        Assert.NotEqual((nint)0, capturedHandle);
        Assert.Equal(1, Get(first, "NativePins"));
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(oldRead).References);
        Call("ReleaseTextureSourceWorkOwnerLocked", consumer);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(oldRead).References);
        state.Recycle();
        Assert.Equal(capturedHandle, Get(first, "ImmutableTexture"));
        Assert.Equal(1, Get(first, "NativePins"));
        Assert.Equal((nint)0, MetalNative.Send(result.CommandBuffer, MetalNative.Selector("status")));

        if (discard)
        {
            NativeState.DiscardBorrowedConsumerBatch();
            Assert.Equal((nint)0, MetalNative.Send(result.CommandBuffer, MetalNative.Selector("status")));
            Assert.Equal(Enumerable.Repeat((byte)0xA5, 64).ToArray(), result.Read());
            Assert.True((bool)Get(first, "Failed")!);
            Assert.False((bool)Get(second, "Failed")!);
        }
        else
        {
            Assert.Equal(result.CommandBuffer, Call("FlushBatchedGuestCommands"));
            state.Wait(result.CommandBuffer);
            Assert.Equal((nint)4, MetalNative.Send(result.CommandBuffer, MetalNative.Selector("status")));
            Assert.Equal(Rgba(255, 0, 0, 255), result.Read());
        }
        state.Recycle();
        Assert.Equal(0, Get(first, "NativePins"));
        Assert.Equal((nint)0, Get(first, "ImmutableTexture"));
        Assert.Equal((nint)0, Get(first, "Texture"));
        Assert.False((bool)Field("_batchOpen").GetValue(null)!);
        Assert.Equal((nint)0, Field("_batchCommandBuffer").GetValue(null));
        Assert.Empty((IList)Field("_touchedUploadPages").GetValue(null)!);
        state.ShutdownAndAssertNoSourceOwners();
        output.WriteLine($"Actual encoded consumer queued owner released before native commit; storage={storage}, discard={discard}; native pin kept captured old texture until completion/discard, all64outputbytes verified and source/registry owners released.");
    }

    private sealed partial class NativeState
    {
        public sealed class RetainedOutput : IDisposable
        {
            private readonly nint buffer;
            public nint CommandBuffer { get; }
            public RetainedOutput(nint buffer, nint commandBuffer)
            {
                CommandBuffer = commandBuffer;
                this.buffer = MetalNative.Send(buffer, MetalNative.Selector("retain"));
            }
            public byte[] Read()
            {
                var result = new byte[64];
                Marshal.Copy(MetalNative.Send(buffer, MetalNative.Selector("contents")), result, 0, result.Length);
                return result;
            }
            public void Dispose() => MetalNative.SendVoid(buffer, MetalNative.Selector("release"));
        }

        public RetainedOutput ExecuteUnflushedConsumer(object work)
        {
            // A fresh isolated arena guarantees the first actual UploadDrawBuffers
            // slice begins at0. Retain the real MTLBuffer independently of arena
            // pooling so no guest writeback or artificial completion is required.
            Assert.Null(Field("_currentUploadPage").GetValue(null));
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Assert.False((bool)Field("_skipAllCompute").GetValue(null)!);
            Call("ExecuteComputeDispatch", device, queue, work);
            var shader = (MetalCompiledGuestShader)work.GetType().GetProperty("Shader")!.GetValue(work)!;
            Assert.NotEqual((nint)0, shader.CachedLibrary);
            var pipelines = (IDictionary)Field("_computePipelineCache").GetValue(null)!;
            Assert.True(pipelines.Contains(shader)); Assert.NotEqual((nint)0, pipelines[shader]);
            Assert.True((bool)Field("_batchOpen").GetValue(null)!);
            var commandBuffer = (nint)Field("_batchCommandBuffer").GetValue(null)!;
            Assert.NotEqual((nint)0, commandBuffer);
            Assert.Equal((nint)0, Field("_lastFlushedCommandBuffer").GetValue(null));
            var page = Field("_currentUploadPage").GetValue(null)!;
            return new RetainedOutput((nint)Get(page, "Buffer")!, commandBuffer);
        }
        public static void DiscardBorrowedConsumerBatch() => AbortBorrowedConsumerBatch();
        public void ShutdownAndAssertNoSourceOwners()
        {
            Call("ShutdownTextureSourceNativeOwners");
            Assert.Equal(0UL, NativeBytes);
            Assert.Empty((IDictionary)Field("_textureSourceNativeBatches").GetValue(null)!);
            Assert.False(((IDictionary)Field("_guestImages").GetValue(null)!).Contains(Address));
            Assert.False(((IDictionary)Field("_guestImageExtents").GetValue(null)!).Contains(Address));
        }
    }
}
