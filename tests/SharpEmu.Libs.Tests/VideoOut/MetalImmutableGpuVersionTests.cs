// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.VideoOut;

// Explicit device-only qualification of the private dormant lifecycle. Real
// admission/materialization/copy/commit/recycle helpers run; render-pass clear
// is the GPU writer. No translated draw/compute or game qualification is implied.
[Collection(SchedulingStateCollection.Name)]
public sealed partial class MetalImmutableGpuVersionTests(ITestOutputHelper output)
{
    private const ulong Address = 0x71F0_0000_00;
    private bool DeviceRequested()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Native version fixture not executed; run require-device1 under the exclusive game lock.");
            return false;
        }
        Assert.True(OperatingSystem.IsMacOS());
        return true;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedOldGpuVersionSurvivesLaterWriterAndRegistryRetirement(bool oneBuffer)
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        var firstBuffer = state.Buffer();
        var first = state.AdmitColor();
        state.EncodeClear(first, firstBuffer, new MtlClearColor { Red = 1, Alpha = 1 });
        var read = state.Capture(); // capture before later writer admission
        var second = state.AdmitColor();
        var secondBuffer = oneBuffer ? firstBuffer : state.Buffer();
        if (!oneBuffer) state.Commit(firstBuffer);
        state.EncodeClear(second, secondBuffer, new MtlClearColor { Green = 1, Alpha = 1 });
        var oldHandle = state.Resolve(read, secondBuffer);
        var newHandle = (nint)Get(second, "ImmutableTexture")!;
        state.Commit(secondBuffer);
        state.Wait(secondBuffer);
        state.Recycle();
        Assert.True((bool)Get(first, "NativeCompleted")!);
        Assert.True((bool)Get(second, "NativeCompleted")!);
        Assert.Equal(Rgba(255, 0, 0, 255), Read(oldHandle));
        Assert.Equal(Rgba(0, 255, 0, 255), Read(newHandle));
        Assert.True((bool)Get(first, "Retired")!);
        Assert.NotEqual((nint)0, Get(first, "ImmutableTexture"));
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        state.Recycle();
        Assert.Equal((nint)0, Get(first, "ImmutableTexture"));
        Assert.Equal(128UL, state.NativeBytes);
        output.WriteLine($"Actual GPU old/red and new/green verified; same_buffer={oneBuffer}, old native version pruned after read release.");
    }

    [Fact]
    public void SecondBufferCannotConsumeEncodedButUncommittedProducerCopy()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        var producerBuffer = state.Buffer(); var consumerBuffer = state.Buffer();
        var first = state.AdmitColor();
        state.EncodeClear(first, producerBuffer, new MtlClearColor { Blue = 1, Alpha = 1 });
        var read = state.Capture();
        var failure = Assert.Throws<TargetInvocationException>(() => state.Resolve(read, consumerBuffer));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Contains("ordered producing command buffer", failure.InnerException!.Message);
        Assert.False((bool)Get(first, "NativeCompleted")!);
        // Commit the independent empty consumer first. It cannot publish a
        // producing-copy completion or justify stale native bytes.
        state.Commit(consumerBuffer); state.Wait(consumerBuffer); state.Recycle();
        Assert.False((bool)Get(first, "NativeCompleted")!);
        state.Commit(producerBuffer); state.Wait(producerBuffer); state.Recycle();
        Assert.True((bool)Get(first, "NativeCompleted")!);
        var after = state.Buffer();
        var handle = state.Resolve(read, after);
        state.Commit(after); state.Wait(after); state.Recycle();
        Assert.Equal(Rgba(0, 0, 255, 255), Read(handle));
        output.WriteLine("Actual reversed empty-consumer commit rejected pending producer; later ordered blue read verified.");
    }

    [Fact]
    public void DiscardedBatchInvalidatesEveryEarlierEncodedOutput()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        var buffer = state.Buffer();
        var first = state.AdmitColor(); state.EncodeClear(first, buffer, new MtlClearColor { Red = 1 });
        var read = state.Capture();
        var second = state.AdmitColor(); state.EncodeClear(second, buffer, new MtlClearColor { Green = 1 });
        Call("AbortTextureSourceNativeBatch", buffer);
        Assert.True((bool)Get(first, "Failed")!); Assert.True((bool)Get(second, "Failed")!);
        Assert.False((bool)Get(first, "Completed")!); Assert.False((bool)Get(second, "Completed")!);
        Assert.Equal(0, Get(first, "NativePins")); Assert.Equal(0, Get(second, "NativePins"));
        var consumer = state.Buffer();
        Assert.Throws<TargetInvocationException>(() => state.Resolve(read, consumer));
        output.WriteLine("Real uncommitted batch discarded; both prior and final source versions invalidated without commit.");
    }

    [Fact]
    public void CommittedCloseWaitsBeforeReleasingVersionsAndRegistryRetains()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        var buffer = state.Buffer();
        var source = state.AdmitColor(); state.EncodeClear(source, buffer, new MtlClearColor { Red = 1 });
        state.Commit(buffer);
        Assert.True((int)Get(source, "NativePins")! > 0);
        Call("ShutdownTextureSourceNativeOwners");
        Assert.Equal((nint)4, MetalNative.Send(buffer, MetalNative.Selector("status")));
        Assert.Equal(0, Get(source, "NativePins"));
        Assert.Equal((nint)0, Get(source, "Texture")); Assert.Equal((nint)0, Get(source, "ImmutableTexture"));
        Assert.Equal(0UL, state.NativeBytes);
        Assert.False(((IDictionary)Field("_guestImages").GetValue(null)!).Contains(Address));
        output.WriteLine("Actual committed close waited for completion and released private versions plus exact live registry owner.");
    }

    [Fact]
    public void EvictedGpuSourceCannotFallBackToCpuSnapshotOrMutableRegistry()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        var buffer = state.Buffer();
        var source = state.AdmitColor(); state.EncodeClear(source, buffer, new MtlClearColor { Red = 1 });
        state.Commit(buffer); state.Wait(buffer);
        // Synthetic pressure exercises actual retirement of a genuinely GPU
        // written version; it does not claim a192MiB allocation stress test.
        Field("_immutableTextureSourceBytes").SetValue(null, 192UL * 1024 * 1024 + 128);
        state.Recycle();
        Assert.Equal((nint)0, Get(source, "ImmutableTexture"));
        Assert.False(((IDictionary)Field("_guestImages").GetValue(null)!).Contains(Address));
        var memory = new NeverReadMemory();
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(MetalTextureSourceLeaseTests.ResourceWords(), out var descriptor));
        var binding = new AgcExports.TranslatedImageBinding(descriptor, false, 0, [],
            ResourceDescriptor: MetalTextureSourceLeaseTests.ResourceWords(),
            Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
        var failure = Assert.Throws<NotSupportedException>(() =>
            MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Contains("requires a captured immutable source", failure.Message);
        Assert.Equal(0, memory.Reads);
        output.WriteLine("Actual native eviction retains GPU provenance; production preparation rejected with zero guest-memory reads.");
    }
    private sealed class NeverReadMemory : ICpuMemory
    {
        public int Reads;
        public bool TryRead(ulong address, Span<byte> bytes) { Reads++; throw new InvalidOperationException("CPU reads are forbidden for the GPU-only source fixture."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("No guest memory writes in GPU-only fixture.");
        public bool CanRead(ulong address, ulong bytes) => true;
    }

    private static byte[] Rgba(byte r, byte g, byte b, byte a) => Enumerable.Range(0, 16).SelectMany(_ => new[] { r, g, b, a }).ToArray();
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void GetBytes(nint texture, nint selector, nint bytes, nuint row, MtlRegion region, nuint level);
    private static unsafe byte[] Read(nint texture)
    {
        var bytes = new byte[64];
        fixed (byte* pointer = bytes)
            GetBytes(texture, MetalNative.Selector("getBytes:bytesPerRow:fromRegion:mipmapLevel:"), (nint)pointer, 16,
                new MtlRegion { Width = 4, Height = 4, Depth = 1 }, 0);
        return bytes;
    }
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static object? Call(string name, params object?[] arguments) => typeof(MetalVideoPresenter)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
    private static object? Get(object source, string field) => source.GetType().GetField(field)!.GetValue(source);

    private sealed partial class NativeState : IDisposable
    {
        private readonly MetalTextureSourceLeaseTests.State trackerState = new();
        private readonly bool oldGraph = (bool)Field("_textureSourceGraphEnabled").GetValue(null)!;
        private readonly nint pool;
        private readonly nint device;
        private readonly nint queue;
        private readonly UploadArenaScope uploadArena;
        private readonly List<nint> buffers = [];
        private readonly List<GuestDrawTexture> captures = [];
        private readonly Dictionary<object, object> workByVersion = new(ReferenceEqualityComparer.Instance);
        public ulong NativeBytes => (ulong)Field("_immutableTextureSourceBytes").GetValue(null)!;
        public NativeState()
        {
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Assert.Equal((nint)0, Field("_batchCommandBuffer").GetValue(null));
            Assert.Empty((IDictionary)Field("_textureSourceNativeBatches").GetValue(null)!);
            pool = MetalNative.Send(MetalNative.Send(MetalNative.Class("NSAutoreleasePool"), MetalNative.Selector("alloc")), MetalNative.Selector("init"));
            device = MetalNative.MTLCreateSystemDefaultDevice(); Assert.NotEqual((nint)0, device);
            queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue")); Assert.NotEqual((nint)0, queue);
            uploadArena = new UploadArenaScope();
            Field("_textureSourceGraphEnabled").SetValue(null, true);
        }
        public nint Buffer()
        {
            var buffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
            Assert.NotEqual((nint)0, buffer);
            MetalNative.Send(buffer, MetalNative.Selector("retain")); buffers.Add(buffer); return buffer;
        }
        public object AdmitColor()
        {
            var target = new GuestRenderTarget(Address, 4, 4, 10, 0,
                Registers: new ColorTargetWords(Address, 0, 10u << 2, 0, 3u | 3u << 14, 1u << 24, 0, 0, 0, 0, 0));
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(new MetalCompiledGuestShader(new Gen5MslShader("", "native-version", Gen5MslStage.Pixel, 0)),
                [], [], 0, [target], null, 3, 1, 4, null, null, null, null, 0);
            var version = ((IDictionary)Field("_latestTextureSources").GetValue(null)!)[Address]!;
            var sequence = (long)Get(version, "ProducerSequence")!;
            var pending = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<IEnumerable>()
                .SelectMany(static queue => queue.Cast<object>()).Single(item => (long)item.GetType().GetProperty("Sequence")!.GetValue(item)! == sequence);
            workByVersion.Add(version, pending.GetType().GetProperty("Work")!.GetValue(pending)!);
            return version;
        }
        public void EncodeClear(object version, nint buffer, MtlClearColor color)
        {
            Field("_executingTextureSourceWork").SetValue(null, workByVersion[version]);
            Call("MaterializeTextureSourceOutputs", device, buffer);
            var target = (nint)Get(version, "Texture")!;
            var pass = (nint)Call("CreateClearPass", target, color)!;
            var encoder = MetalNative.Send(buffer, MetalNative.Selector("renderCommandEncoderWithDescriptor:"), pass);
            Assert.NotEqual((nint)0, encoder); MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
            var image = Get(version, "Image")!;
            image.GetType().GetMethod("MarkContentChanged")!.Invoke(image, []);
            Call("EncodeTextureSourceOutputCopies", device, buffer);
            Call("ReleaseTextureSourceWorkOwnerLocked", workByVersion[version]);
            Field("_executingTextureSourceWork").SetValue(null, null);
        }
        public GuestDrawTexture Capture(bool storage = false)
        {
            var read = new GuestDrawTexture(Address, 4, 4, 10, 0, [], false, storage, Pitch: 4,
                Descriptor: MetalTextureSourceLeaseTests.ResourceWords(),
                Shape: new ShaderImageShape(false, false, storage, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
            Assert.True(MetalVideoPresenter.TryCaptureTextureSource(read, 64, false)); captures.Add(read); return read;
        }
        public nint Resolve(GuestDrawTexture texture, nint buffer)
        {
            object?[] args = [buffer, texture, (nint)0];
            Assert.True((bool)Call("TryResolveCapturedTextureSource", args)!); return (nint)args[2]!;
        }
        public void Commit(nint buffer) => Call("CommitTextureSourceNativeCommands", buffer);
        public void Wait(nint buffer) => MetalNative.SendVoid(buffer, MetalNative.Selector("waitUntilCompleted"));
        public void Recycle() => Call("RecycleTextureSourceNativeBatches");
        public void Dispose()
        {
            AbortBorrowedConsumerBatch();
            MetalVideoPresenter.ReleasePreparedTextureSources(captures);
            Call("ShutdownTextureSourceNativeOwners");
            uploadArena.Dispose();
            ReleaseConsumerShaderResources();
            Field("_executingTextureSourceWork").SetValue(null, null);
            Field("_textureSourceGraphEnabled").SetValue(null, oldGraph);
            foreach (var buffer in buffers) MetalNative.SendVoid(buffer, MetalNative.Selector("release"));
            MetalNative.SendVoid(queue, MetalNative.Selector("release"));
            MetalNative.SendVoid(device, MetalNative.Selector("release"));
            MetalNative.SendVoid(pool, MetalNative.Selector("release"));
            trackerState.Dispose();
        }
    }
}
