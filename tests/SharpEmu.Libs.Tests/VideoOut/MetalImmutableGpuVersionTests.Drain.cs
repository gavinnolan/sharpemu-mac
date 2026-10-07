// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualRuntimeDrainConsumesDetachedGpuVersionAndReleasesExactOwner(bool storage)
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        using var memory = new NoGuestReadScope();
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var first = state.PrepareControlledProducer(new MtlClearColor { Red = 1, Alpha = 1 });
        var read = state.CaptureFromProductionPreparation(memory.Memory, storage);
        state.PrepareControlledProducer(new MtlClearColor { Green = 1, Alpha = 1 });
        var consumer = state.AdmitConsumer(read, writeBack: false);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        var observed = false;
        var checkpoint = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
        {
            Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
            var receipt = NativeState.ReleaseReceipt(consumer);
            Assert.True(receipt.Released); Assert.True(receipt.BeforeCommit); Assert.True(receipt.BatchIdentity > 0);
            // Actual ordered actions flush BEFORE their callback. The receipt
            // records the exact work's earlier uncommitted pin at completion.
            Call("WaitForFlushedGuestCommands");
            using var result = state.RetainDrainOutput();
            Assert.Equal(Rgba(255, 0, 0, 255), result.Read());
            observed = true;
        }, "native captured-source checkpoint");
        state.DrainWithControlledBudget();
        Assert.True(observed); Assert.True(MetalVideoPresenter.WaitForGuestWork(checkpoint, 0));
        Assert.Equal(0, memory.Memory.Reads);
        state.Recycle();
        Assert.Equal((nint)0, Get(first, "ImmutableTexture"));
        state.CloseThroughRuntimeHook();
        output.WriteLine($"Actual Drain Take→Execute→Complete→ordered checkpoint read all64old/red bytes with zeroCPUreads; storage={storage}, exact queued owner released before native commit, close owners0.");
    }

    [Fact]
    public void ActualRuntimeDrainFatalSelectionDiscardsConsumerAndCancelsMissingProducerOwners()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState(); using var memory = new NoGuestReadScope();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        state.PrepareControlledProducer(new MtlClearColor { Red = 1, Alpha = 1 });
        var oldRead = state.CaptureFromProductionPreparation(memory.Memory, false);
        state.AdmitAndTakeUnencodedProducer();
        var missingRead = state.CaptureFromProductionPreparation(memory.Memory, false);
        var valid = state.AdmitConsumer(oldRead, writeBack: false);
        var blocked = state.AdmitConsumer(missingRead, writeBack: false);
        MetalVideoPresenter.ReleasePreparedTextureSources([oldRead, missingRead]);
        var fatal = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
        Assert.IsType<GpuFatalException>(fatal.InnerException); Assert.Single(reports);
        Assert.Contains("no longer queued", reports[0]);
        var receipt = NativeState.ReleaseReceipt(valid);
        Assert.True(receipt.Released); Assert.True(receipt.BeforeCommit); Assert.True(receipt.BatchIdentity > 0);
        Assert.True(NativeState.ReleaseReceipt(blocked).Released);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(oldRead).References);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(missingRead).References);
        Assert.Equal((nint)0, Field("_lastFlushedCommandBuffer").GetValue(null));
        using var result = state.RetainDrainOutput();
        Assert.Equal(Enumerable.Repeat((byte)0xA5, 64).ToArray(), result.Read());
        Assert.Empty((IDictionary)Field("_textureSourceNativeBatches").GetValue(null)!);
        Assert.False((bool)Field("_batchOpen").GetValue(null)!);
        Assert.Equal(0UL, state.NativeBytes); Assert.Equal(0, memory.Memory.Reads);
        state.AssertNoCpuFallbackAfterFatal(memory.Memory);
        state.CloseThroughRuntimeHook();
        output.WriteLine("Actual Drain dependency fatal discarded uncommitted encoded consumer, kept all64A5 sentinel bytes, canceled missing producer/consumer owners exactly once with zeroCPUreads.");
    }

    [Fact]
    public void ActualRuntimeDrainFatalActionKeepsCommittedPinsUntilRuntimeCloseWait()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState(); using var memory = new NoGuestReadScope();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var first = state.PrepareControlledProducer(new MtlClearColor { Red = 1, Alpha = 1 });
        var read = state.CaptureFromProductionPreparation(memory.Memory, false);
        state.PrepareControlledProducer(new MtlClearColor { Green = 1, Alpha = 1 });
        var consumer = state.AdmitConsumer(read, writeBack: false);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        var cause = new NotSupportedException("controlled fatal after ordered flush");
        MetalVideoPresenter.SubmitOrderedGuestAction(() => throw cause, "committed consumer fatal");
        var failure = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
        Assert.Same(cause, Assert.IsType<GpuFatalException>(failure.InnerException).InnerException); Assert.Single(reports);
        Assert.True(NativeState.ReleaseReceipt(consumer).BeforeCommit);
        state.AssertNoCpuFallbackAfterFatal(memory.Memory);
        Assert.Equal(1, Get(first, "NativePins")); Assert.NotEqual((nint)0, Get(first, "ImmutableTexture"));
        using var result = state.RetainDrainOutput();
        Assert.NotEqual((nint)0, result.CommandBuffer);
        state.CloseThroughRuntimeHook();
        Assert.Equal((nint)4, MetalNative.Send(result.CommandBuffer, MetalNative.Selector("status")));
        Assert.Equal(Rgba(255, 0, 0, 255), result.Read());
        Assert.Equal(0, Get(first, "NativePins")); Assert.Equal((nint)0, Get(first, "ImmutableTexture"));
        Assert.Equal(0, memory.Memory.Reads);
        output.WriteLine("Actual Drain ordered-action fatal preserved committed reader pins; runtime close waited outside gate and all64red output bytes/owners0 verified.");
    }

    [Fact]
    public void ActualRuntimeCloseCancelsUnexecutedReaderAndWaitsCommittedProducer()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState(); using var memory = new NoGuestReadScope();
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var buffer = state.Buffer(); var source = state.AdmitColor();
        state.TakeAndEncodeControlledProducer(source, buffer, new MtlClearColor { Red = 1, Alpha = 1 });
        state.Commit(buffer); // no synthetic/native completion wait before close
        var read = state.CaptureFromProductionPreparation(memory.Memory, false);
        var consumer = state.AdmitConsumer(read, writeBack: false);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        state.CloseThroughRuntimeHook();
        Assert.Equal((nint)4, MetalNative.Send(buffer, MetalNative.Selector("status")));
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
        var receipt = NativeState.ReleaseReceipt(consumer);
        Assert.True(receipt.Released); Assert.False(receipt.BeforeCommit); Assert.Equal(0L, receipt.BatchIdentity);
        Assert.Equal(0, memory.Memory.Reads);
        output.WriteLine("Actual runtime close canceled unencoded queued reader, waited committed producer outside gate, source/registry/budget owners0 with zeroCPUreads.");
    }

    [Fact]
    public void ActualNativeReceiptRejectsUnencodedPeerWhileSameVersionHasUncommittedPin()
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState(); using var memory = new NoGuestReadScope();
        output.WriteLine($"Device={state.DeviceName}; process_arch={RuntimeInformation.ProcessArchitecture}");
        var source = state.PrepareControlledProducer(new MtlClearColor { Red = 1, Alpha = 1 });
        var read = state.CaptureFromProductionPreparation(memory.Memory, false);
        var encoded = state.AdmitConsumer(read, writeBack: false);
        var peer = state.AdmitConsumer(read, writeBack: false);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        NativeState.RetainedOutput result;
        Field("_executingTextureSourceWork").SetValue(null, encoded);
        try { result = state.ExecuteUnflushedConsumer(encoded); }
        finally { Field("_executingTextureSourceWork").SetValue(null, null); }
        using (result)
        {
            Assert.Equal(1, Get(source, "NativePins"));
            Call("ReleaseTextureSourceWorkOwnerLocked", peer);
            var peerReceipt = NativeState.ReleaseReceipt(peer);
            Assert.True(peerReceipt.Released); Assert.False(peerReceipt.BeforeCommit); Assert.Equal(0L, peerReceipt.BatchIdentity);
            Call("ReleaseTextureSourceWorkOwnerLocked", encoded);
            Assert.True(NativeState.ReleaseReceipt(encoded).BeforeCommit);
            Call("FlushBatchedGuestCommands"); state.Wait(result.CommandBuffer);
            Assert.Equal(Rgba(255, 0, 0, 255), result.Read());
        }
        state.CloseThroughRuntimeHook(); Assert.Equal(0, memory.Memory.Reads);
        output.WriteLine("Actual same-version uncommitted native pin cannot produce a false release receipt for an unencoded queued peer; all64red bytes and close owners0 verified.");
    }

    private sealed class NoGuestReadScope : IDisposable
    {
        private readonly object? previous = Field("_guestMemory").GetValue(null);
        public readonly NeverReadMemory Memory = new();
        public NoGuestReadScope() => Field("_guestMemory").SetValue(null, Memory);
        public void Dispose() => Field("_guestMemory").SetValue(null, previous);
    }

    private sealed partial class NativeState
    {
        public static (bool Released, bool BeforeCommit, long BatchIdentity) ReleaseReceipt(object work)
        {
            var table = Field("_textureSourceWorkOwners").GetValue(null)!;
            object?[] args = [work, null]; Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
            var owner = args[1]!;
            return ((bool)Get(owner, "Released")!, (bool)Get(owner, "ReleasedWithUncommittedNativeUse")!, (long)Get(owner, "NativeUseBatchIdentity")!);
        }
        public object PrepareControlledProducer(MtlClearColor color)
        {
            var buffer = Buffer(); var source = AdmitColor();
            TakeAndEncodeControlledProducer(source, buffer, color);
            Commit(buffer); Wait(buffer); Recycle(); return source;
        }
        public void TakeAndEncodeControlledProducer(object source, nint buffer, MtlClearColor color)
        {
            object?[] args = [null]; Assert.True((bool)Call("TryTakeGuestWork", args)!);
            Assert.Same(workByVersion[source], args[0]!.GetType().GetProperty("Work")!.GetValue(args[0]));
            EncodeClear(source, buffer, color); Call("CompleteGuestWork", args[0]);
        }
        public void AdmitAndTakeUnencodedProducer()
        {
            var source = AdmitColor(); object?[] args = [null]; Assert.True((bool)Call("TryTakeGuestWork", args)!);
            Assert.Same(workByVersion[source], args[0]!.GetType().GetProperty("Work")!.GetValue(args[0]));
            // Required real admitted producer deliberately fails to encode;
            // dependency lookup must reject its now-missing queued prerequisite.
        }
        public GuestDrawTexture CaptureFromProductionPreparation(NeverReadMemory memory, bool storage)
        {
            var words = MetalTextureSourceLeaseTests.ResourceWords();
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(words, out var descriptor));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, storage, 0, [], ResourceDescriptor: words,
                Shape: new ShaderImageShape(false, false, storage, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
            var read = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            Assert.Empty(read.RgbaPixels); Assert.False(read.CpuSnapshotStable); Assert.Equal(0, memory.Reads);
            captures.Add(read); return read;
        }
        public void AssertNoCpuFallbackAfterFatal(NeverReadMemory memory)
        {
            var words = MetalTextureSourceLeaseTests.ResourceWords();
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(words, out var descriptor));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, false, 0, [], ResourceDescriptor: words,
                Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
            var failure = Assert.Throws<NotSupportedException>(() =>
                MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            Assert.Contains("requires a captured immutable source", failure.Message);
            Assert.Equal(0, memory.Reads);
        }
        public void DrainWithControlledBudget() => Call("DrainGuestWorkCore", device, queue, 5L * System.Diagnostics.Stopwatch.Frequency);
        public RetainedOutput RetainDrainOutput()
        {
            var page = Field("_currentUploadPage").GetValue(null)!; Assert.NotNull(page);
            return new RetainedOutput((nint)Get(page, "Buffer")!, (nint)Field("_lastFlushedCommandBuffer").GetValue(null)!);
        }
        public void CloseThroughRuntimeHook()
        {
            Call("CompletePresenterShutdown");
            Assert.True((bool)Field("_closed").GetValue(null)!); Assert.Null(Field("_thread").GetValue(null));
            Assert.Equal(0UL, NativeBytes); Assert.Empty((IDictionary)Field("_textureSourceNativeBatches").GetValue(null)!);
            Assert.False(((IDictionary)Field("_guestImages").GetValue(null)!).Contains(Address));
            Assert.False(((IDictionary)Field("_guestImageExtents").GetValue(null)!).Contains(Address));
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0UL, Field("_pendingGuestWorkBytes").GetValue(null));
        }
    }
}
