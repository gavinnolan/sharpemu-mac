// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualQueuedColorDrawsPreserveCapturedOldAndPartialNewVersions(bool firstScissoredAndMasked)
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState(); using var memory = new NoGuestReadScope();
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var first = state.AdmitActualColor(red: true, mask: firstScissoredAndMasked ? 1u : 0xFu,
            scissor: firstScissoredAndMasked ? new GuestRect(0, 0, 2, 4) : null);
        var oldRead = state.CaptureFromProductionPreparation(memory.Memory, false);
        // Later writer is admitted after capture but before the old consumer.
        // Its green-only right-half writes must preserve every other channel.
        var second = state.AdmitActualColor(red: false, mask: 2, scissor: new GuestRect(2, 0, 2, 4));
        var newRead = state.CaptureFromProductionPreparation(memory.Memory, false);
        Assert.Same(first.Source, Get(second.Source, "Initializer"));
        var oldExpected = ColorPixels(firstScissoredAndMasked, newVersion: false);
        var newExpected = ColorPixels(firstScissoredAndMasked, newVersion: true);
        var oldBytes = state.DrainColorConsumer(oldRead);
        state.AssertColorPipelineSucceeded(first.Work); state.AssertColorPipelineSucceeded(second.Work);
        Assert.Equal(oldExpected, oldBytes);
        Assert.True((bool)Get(first.Source, "NativeCompleted")!);
        Assert.True((bool)Get(second.Source, "NativeCompleted")!);
        Assert.Equal(newExpected, state.DrainColorConsumer(newRead));
        Assert.Equal(0, memory.Memory.Reads);
        state.Recycle(); state.CloseThroughRuntimeHook();
        output.WriteLine($"Actual queued Draw→render endEncoding→immutable copy→Compute consumer Drain verified ALL64 old and new bytes; first_scissored_and_red_masked={firstScissoredAndMasked}; zeroCPUreads; close source/registry owners0. First-use clear is a host initializer, not proven guest clear intent.");
    }

    [Fact]
    public void ActualQueuedColorPipelineFailureNeverPublishesEncodedVersion()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState(); using var memory = new NoGuestReadScope();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var producer = state.AdmitActualColor(red: true, badPixel: true);
        var read = state.CaptureFromProductionPreparation(memory.Memory, false);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        var failure = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
        var fatal = Assert.IsType<GpuFatalException>(failure.InnerException);
        Assert.Contains("pipeline creation failed", Assert.IsType<InvalidOperationException>(fatal.InnerException).Message);
        Assert.Single(reports); Assert.False((bool)Get(producer.Source, "Completed")!);
        Assert.True((bool)Get(producer.Source, "Failed")!);
        Assert.Equal((nint)0, Get(producer.Source, "Texture"));
        Assert.Equal((nint)0, Get(producer.Source, "ImmutableTexture"));
        Assert.Empty((IDictionary)Field("_textureSourceNativeBatches").GetValue(null)!);
        Assert.Equal(0UL, state.NativeBytes); Assert.Equal(0, memory.Memory.Reads);
        state.AssertNoCpuFallbackAfterFatal(memory.Memory);
        state.CloseThroughRuntimeHook();
        output.WriteLine("Actual invalid native pixel compilation rejected before output allocation/copy; no encoded readiness or CPU fallback; source/registry owners0.");
    }

    [Fact]
    public void ActualColorProducerCopiesBeforeRealBufferWritebackFlush()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var producer = state.AdmitActualColor(red: true, writeBack: true);
        var sourceMemory = new NeverReadMemory();
        var read = state.CaptureFromProductionPreparation(sourceMemory, false);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        var sink = new ColorWritebackMemory(producer.Source);
        var oldMemory = Field("_guestMemory").GetValue(null);
        var oldHook = Field("_textureSourceBeforeNativeCommit").GetValue(null);
        var precommit = 0;
        Field("_guestMemory").SetValue(null, sink);
        Field("_textureSourceBeforeNativeCommit").SetValue(null, (Action)(() =>
        {
            var buffer = (nint)Field("_batchCommandBuffer").GetValue(null)!;
            // Flush clears the borrowed field before invoking this hook.
            Assert.Equal((nint)0, buffer);
            Assert.True((bool)Get(producer.Source, "Completed")!);
            Assert.NotEqual((nint)0, Get(producer.Source, "ImmutableTexture"));
            var batch = Get(producer.Source, "CopyBatch")!;
            Assert.Contains(producer.Source, ((IEnumerable)Get(batch, "ProducedVersions")!).Cast<object>());
            Assert.False((bool)Get(batch, "Committed")!);
            precommit++;
        }));
        try
        {
            state.DrainWithControlledBudget();
            state.AssertColorPipelineSucceeded(producer.Work);
            Assert.Equal(1, precommit); Assert.Equal(1, sink.Writes);
            Assert.Equal(Enumerable.Repeat((byte)0xA5, 64).ToArray(), sink.Bytes);
            Assert.Equal(0, sink.Reads); Assert.Equal(0, sourceMemory.Reads);
            state.Recycle(); state.CloseThroughRuntimeHook();
            output.WriteLine("Actual render writer immutable-copy encoding preceded real writable-buffer Flush→commit→wait→ICpuMemory writeback; completion-time immutable ALL64 red oracle and unchanged ALL64 A5 trigger buffer verified; zero source CPUreads; close owners0. This is writeback ordering, not a shader buffer-write proof.");
        }
        finally
        {
            Field("_textureSourceBeforeNativeCommit").SetValue(null, oldHook);
            Field("_guestMemory").SetValue(null, oldMemory);
        }
    }

    private static byte[] ColorPixels(bool firstPartial, bool newVersion) => Enumerable.Range(0, 16)
        .SelectMany(index => new byte[] { (byte)(!firstPartial || index % 4 < 2 ? 255 : 0),
            (byte)(newVersion && index % 4 >= 2 ? 255 : 0), 0, 255 }).ToArray();

    private sealed class ColorWritebackMemory(object source) : ICpuMemory
    {
        public int Writes, Reads;
        public byte[] Bytes = [];
        public bool TryRead(ulong address, Span<byte> bytes) { Reads++; throw new InvalidOperationException("No CPU source reads in draw/writeback fixture."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes)
        {
            Assert.Equal(0x71F1_0000_00UL, address); Assert.Equal(64, bytes.Length);
            var committed = (nint)Field("_lastFlushedCommandBuffer").GetValue(null)!;
            Assert.NotEqual((nint)0, committed);
            Assert.Equal((nint)4, MetalNative.Send(committed, MetalNative.Selector("status")));
            Assert.Equal(Rgba(255, 0, 0, 255), Read((nint)Get(source, "ImmutableTexture")!));
            Bytes = bytes.ToArray(); Writes++; return true;
        }
    }

    private sealed partial class NativeState
    {
        private readonly List<MetalCompiledGuestShader> colorShaders = [];
        public (object Work, object Source) AdmitActualColor(bool red, uint mask = 0xF, GuestRect? scissor = null,
            bool badPixel = false, bool writeBack = false)
        {
            // Independent native VS/PS exercise production pipeline, attachment,
            // draw and scheduling routes. They are not guest ISA translation.
            var vertex = new MetalCompiledGuestShader(new Gen5MslShader("""
                #include <metal_stdlib>
                using namespace metal;
                struct ColorVertexOut { float4 position [[position]]; };
                vertex ColorVertexOut color_vertex(uint id [[vertex_id]]) {
                    constexpr float2 points[6] = { float2(-1,-1), float2(1,-1), float2(-1,1), float2(1,-1), float2(1,1), float2(-1,1) };
                    ColorVertexOut result; result.position = float4(points[id % 6u], 0.0f, 1.0f); return result;
                }
                """, "color_vertex", Gen5MslStage.Vertex, 0));
            var pixel = new MetalCompiledGuestShader(new Gen5MslShader(badPixel ? "deliberately invalid native pixel source" : $$"""
                #include <metal_stdlib>
                using namespace metal;
                fragment float4 color_pixel() { return float4({{(red ? "1.0f,0.0f,0.0f,1.0f" : "0.0f,1.0f,0.0f,1.0f")}}); }
                """, "color_pixel", Gen5MslStage.Pixel, 0));
            colorShaders.Add(vertex); colorShaders.Add(pixel);
            var target = new GuestRenderTarget(Address, 4, 4, 10, 0,
                Registers: new ColorTargetWords(Address, 0, 10u << 2, 0, 3u | 3u << 14, 1u << 24, 0, 0, 0, 0, 0), WriteMask: mask);
            GuestMemoryBuffer[] globals = writeBack ? [new(OutputAddress, Enumerable.Repeat((byte)0xA5, 64).ToArray(), 64, 64, false,
                Writable: true, WriteBackToGuest: true)] : [];
            var render = GuestRenderState.Default with { Blends = [GuestBlendState.Default with { WriteMask = mask }], Scissor = scissor };
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(pixel, [], globals, 0, [target], vertex, 6, 1, 4, null, [], render, null, 0);
            var source = ((IDictionary)Field("_latestTextureSources").GetValue(null)!)[Address]!;
            var sequence = (long)Get(source, "ProducerSequence")!;
            var pending = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<IEnumerable>()
                .SelectMany(static queue => queue.Cast<object>()).Single(item => (long)item.GetType().GetProperty("Sequence")!.GetValue(item)! == sequence);
            return (pending.GetType().GetProperty("Work")!.GetValue(pending)!, source);
        }
        public void AssertColorPipelineSucceeded(object work)
        {
            var draw = work.GetType().GetProperty("Draw")!.GetValue(work)!;
            var vs = (MetalCompiledGuestShader)draw.GetType().GetProperty("VertexShader")!.GetValue(draw)!;
            var ps = (MetalCompiledGuestShader)draw.GetType().GetProperty("PixelShader")!.GetValue(draw)!;
            Assert.NotEqual((nint)0, vs.CachedLibrary); Assert.NotEqual((nint)0, ps.CachedLibrary);
            var cache = (IDictionary)Field("_pipelineCache").GetValue(null)!;
            var keys = cache.Keys.Cast<object>().Where(key => ReferenceEquals(key.GetType().GetProperty("VertexShader")!.GetValue(key), vs)
                && ReferenceEquals(key.GetType().GetProperty("PixelShader")!.GetValue(key), ps)).ToArray();
            Assert.Single(keys); Assert.NotEqual((nint)0, cache[keys[0]]);
        }
        public byte[] DrainColorConsumer(GuestDrawTexture read)
        {
            // Isolate arena so the consumer's first real buffer slice starts0.
            // No draw stream/global data is uploaded in the two-version test.
            using var arena = new UploadArenaScope();
            var consumer = AdmitConsumer(read, writeBack: false);
            MetalVideoPresenter.ReleasePreparedTextureSources([read]);
            var observed = false; byte[] result = [];
            MetalVideoPresenter.SubmitOrderedGuestAction(() =>
            {
                Call("WaitForFlushedGuestCommands");
                var shader = (MetalCompiledGuestShader)consumer.GetType().GetProperty("Shader")!.GetValue(consumer)!;
                Assert.NotEqual((nint)0, shader.CachedLibrary);
                var pipelines = (IDictionary)Field("_computePipelineCache").GetValue(null)!;
                Assert.True(pipelines.Contains(shader)); Assert.NotEqual((nint)0, pipelines[shader]);
                using var retained = RetainDrainOutput();
                Assert.Equal((nint)4, MetalNative.Send(retained.CommandBuffer, MetalNative.Selector("status")));
                result = retained.Read();
                var receipt = ReleaseReceipt(consumer);
                Assert.True(receipt.Released); Assert.True(receipt.BeforeCommit);
                observed = true;
            }, "actual color native consumer checkpoint");
            DrainWithControlledBudget(); Assert.True(observed); return result;
        }
        private void ReleaseColorShaderResources()
        {
            var cache = (IDictionary)Field("_pipelineCache").GetValue(null)!;
            var keys = cache.Keys.Cast<object>().Where(key =>
                colorShaders.Contains((MetalCompiledGuestShader)key.GetType().GetProperty("PixelShader")!.GetValue(key)!)).ToArray();
            foreach (var key in keys)
            {
                var pipeline = (nint)cache[key]!; cache.Remove(key);
                if (pipeline != 0) MetalNative.SendVoid(pipeline, MetalNative.Selector("release"));
            }
            foreach (var shader in colorShaders)
                if (shader.CachedLibrary != 0)
                {
                    MetalNative.SendVoid(shader.CachedLibrary, MetalNative.Selector("release")); shader.CachedLibrary = 0;
                }
        }
    }
}
