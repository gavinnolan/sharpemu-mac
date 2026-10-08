// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static AgcExports.TranslatedImageBinding VolumeBinding()
    {
        uint[] raw = [(uint)(Address >> 8), 0x03C00000, 0, 0xA1B00FAC, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, true, 0, [], IsArrayed: false, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
    }
    private static byte[] VolumePhysical()
    {
        var bytes = Enumerable.Range(0, 65536).Select(index => (byte)(index * 37 + 11)).ToArray();
        new byte[] { 3, 127, 239, 251 }.CopyTo(bytes, 0);
        return bytes;
    }
    private static GuestDrawTexture VolumeSnapshot(VolumeMemory memory) => Assert.Single(
        MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [VolumeBinding()], out _));
    private static object VolumeWatch(object origin) => origin.GetType().GetProperty("VolumeWatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(origin)!;
    private static object? InvokeVolume(string name, params object?[] args) => Presenter.GetMethod(name, Private)!.Invoke(null, args);
    private static object VolumeImage(object origin, ICpuMemory memory)
    {
        var image = InstallImage(origin, memory); Set(image, "TextureType", 7u); return image;
    }
    private static void PublishVolume(GuestDrawTexture snapshot, object image)
    {
        Images.Remove(Address);
        lock (Field("_gate").GetValue(null)!) InvokeVolume("PublishStorageImageLocked", snapshot, null, image, (1u, 1u, 4UL));
    }

    [Fact]
    public void SingletonVolumeActualProducerRetainsFullOriginalWatchAndFourUintOriginBytes()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        try
        {
            Assert.Equal(memory.Bytes[..4], snapshot.RgbaPixels); Assert.Equal(65536UL, snapshot.SourceByteCount);
            Assert.Equal((7u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
            var layout = Assert.IsType<MetalStorageCpuLayout>(origin.GetType().GetProperty("Layout")!.GetValue(origin));
            Assert.Equal(MtlPixelFormat.Rgba8Uint, layout.NativeFormat.Format); Assert.Equal(4, layout.LinearBytes);
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            Assert.Equal(MetalVideoPresenter.CurrentStorageCpuSeedSession, origin.GetType().GetProperty("Session")!.GetValue(origin));
            var watch = VolumeWatch(origin);
            Assert.Same(memory.LastMapping, watch.GetType().GetField("Mapping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch));
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Releases);
            var copy = snapshot with { Sampler = new(1, 2, 3, 4) };
            MetalVideoPresenter.CopyCapturedTextureSource(snapshot, copy);
            Assert.Same(origin, GetOrigin(copy)); // same immutable original, never a lookup recapture
            Assert.Throws<NotSupportedException>(() => layout.Encode(new byte[4])); // no volume publisher expansion
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot, copy]);
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
            Assert.Equal(1, memory.Releases);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Theory]
    [InlineData("raw-perf")] [InlineData("raw-flags")] [InlineData("shape")] [InlineData("unknown-access")]
    public void SingletonVolumeNearCapturedClassRefusesBeforeAnyMappedRead(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var binding = VolumeBinding(); var raw = binding.ResourceDescriptor!.ToArray();
        if (change == "raw-perf") raw[5] ^= 1;
        if (change == "raw-flags") raw[6] = 1;
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        binding = binding with { Descriptor = descriptor, ResourceDescriptor = raw };
        if (change == "shape") binding = binding with { Shape = binding.Shape with { Volume = false } };
        if (change == "unknown-access") binding = binding with { Access = GuestImageAccess.Unknown };
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Releases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SingletonVolumeLegacyUnavailableOrDisabledDoesNotManufactureOriginalOwner(bool disabled)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical()) { Result = CpuMappedCopyResult.Unavailable };
        if (disabled) Field("_finiteStorageWritebackEnabled").SetValue(null, false);
        var snapshot = VolumeSnapshot(memory);
        Assert.Null(InvokeVolume("StorageCpuOrigin", snapshot));
        Assert.Equal(disabled ? 0 : 1, memory.Copies); Assert.Equal(1, memory.Reads); Assert.Equal(0, memory.Releases);
        Assert.Equal(memory.Bytes[..4], snapshot.RgbaPixels);
    }

    [Theory]
    [InlineData("rejected")] [InlineData("stale-mapping")] [InlineData("stale-watch")]
    public void SingletonVolumeRejectedOrStaleOriginalNeverFallsBackToOrdinaryMemory(string reason)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        if (reason == "rejected") memory.Result = CpuMappedCopyResult.Rejected;
        if (reason == "stale-mapping") memory.Current = false;
        if (reason == "stale-watch") memory.WatchCurrent = false;
        Assert.Throws<InvalidOperationException>(() => VolumeSnapshot(memory));
        Assert.Equal(3, memory.Copies); Assert.Equal(0, memory.Reads);
        Assert.Equal(reason == "rejected" ? 0 : 3, memory.Releases);
    }

    [Fact]
    public void SingletonVolumeResetDuringCopyPreservesPrimaryAndConsumesUnknownCleanupOnce()
    {
        ActualArchitecture(); using var state = new VolumeState();
        var cleanup = new InvalidOperationException("watch release unknown");
        var memory = new VolumeMemory(VolumePhysical()) { ReleaseFailure = cleanup,
            AfterCopy = () => Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1) };
        var error = Assert.Throws<InvalidOperationException>(() => VolumeSnapshot(memory));
        Assert.Contains("session changed during the original copy", error.Message);
        Assert.Same(cleanup, error.Data["MetalSingletonVolumeUnregisteredCleanup"]);
        Assert.Equal(1, memory.Copies); Assert.Equal(1, memory.Releases); Assert.Equal(0, memory.Reads);
        Assert.False(Images.Contains(Address));
    }

    [Theory]
    [InlineData("watch")] [InlineData("token")]
    public void SingletonVolumeOriginalVetoRejectsEnsureBeforeAnyNativeAllocation(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory);
        MetalVideoPresenter.AttachGuestMemory(memory);
        try
        {
            if (change == "watch") memory.WatchCurrent = false;
            else protection.Generation(1);
            var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("EnsureStorageImage", (nint)0, snapshot));
            Assert.Contains("ORIGINAL", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.False(Images.Contains(Address)); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Fact]
    public void SingletonVolumeOriginalPublicationTransfersOnlyExactNativeOwnerAndRetirementConsumesOnce()
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        var image = VolumeImage(origin, memory);
        try
        {
            PublishVolume(snapshot, image);
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0, memory.Releases);
            lock (Field("_gate").GetValue(null)!) Assert.True((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked", image, origin)!);
            Field("_executingGuestWorkSequence").SetValue(null, 24L);
            MetalVideoPresenter.RecordStorageImageWrites([snapshot]);
            Assert.Equal(2, image.GetType().GetField("ContentVersion")!.GetValue(image));
            lock (Field("_gate").GetValue(null)!) Assert.True((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked", image, origin)!);
            // Actual retirement hook, synthetic native pointer never dispatched/released.
            lock (Field("_gate").GetValue(null)!)
            {
                InvokeVolume("NoteFiniteStorageOwnerRetirementLocked", image);
                Images.Remove(Address);
                InvokeVolume("NoteFiniteStorageOwnerRetirementLocked", image);
            }
            Assert.Equal(1, memory.Releases); Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image));
        }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Theory]
    [InlineData("session")] [InlineData("watch")]
    public void SingletonVolumePublicationCallbackCannotTransferResetOrStaleOriginal(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var image = VolumeImage(GetOrigin(snapshot), memory);
        var hook = Field("_storageImageBeforeMetadataPublication"); var old = hook.GetValue(null);
        try
        {
            hook.SetValue(null, (Action<nint>)(_ =>
            {
                if (change == "watch") memory.WatchCurrent = false;
                else Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
            }));
            Assert.Throws<TargetInvocationException>(() => PublishVolume(snapshot, image));
            Assert.False(Images.Contains(Address));
            Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image)); // unpublished constructor owns native retain
        }
        finally { hook.SetValue(null, old); Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1, memory.Releases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SingletonVolumeQueuedPreparationReleaseCannotStealPendingOwnerAndCancelBalancesOnce(bool closeBeforeCancel)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory);
        // This actual queue-ownership hook is metadata-only, no fake GPU dispatch success.
        var type = Presenter.GetNestedType("ComputeGuestDispatch", BindingFlags.NonPublic)!;
        var work = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [0UL, null, new[] { snapshot }, Array.Empty<GuestMemoryBuffer>(), 1u, 1u, 1u, 0u, 0u, 0u, null], null)!;
        lock (Field("_gate").GetValue(null)!) InvokeVolume("RetainQueuedTextureSourcesLocked", work);
        MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0, memory.Releases);
        if (closeBeforeCancel) { InvokeVolume("ReleaseSingletonVolumeNativeOriginals"); Assert.Equal(1, memory.Releases); }
        lock (Field("_gate").GetValue(null)!)
        {
            InvokeVolume("ReleaseQueuedTextureSourcesLocked", work);
            InvokeVolume("ReleaseQueuedTextureSourcesLocked", work);
        }
        Assert.Equal(1, memory.Releases);
    }

    [Fact]
    public void SingletonVolumeUnknownNativeProducerCannotRefreshOriginalVersionProof()
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        var image = VolumeImage(origin, memory);
        try
        {
            PublishVolume(snapshot, image);
            Field("_executingGuestWorkSequence").SetValue(null, 24L);
            image.GetType().GetMethod("MarkContentChangedWithDiagnostic")!.Invoke(image, ["guest-blit"]);
            lock (Field("_gate").GetValue(null)!)
                Assert.False((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked", image, origin)!);
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            InvokeVolume("ReleaseSingletonVolumeNativeOriginals");
            Assert.Equal(1, memory.Releases);
        }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Fact]
    public void SingletonVolumePostPublicationChangedTokenStillRejectsOriginalOutsidePresenterGate()
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        var image = VolumeImage(origin, memory); var hook = Field("_storageImageBeforeMetadataPublication"); var old = hook.GetValue(null);
        try
        {
            hook.SetValue(null, (Action<nint>)(_ => GuestImageWriteTracker.NotifyManagedWrite(Address + 31, 1)));
            PublishVolume(snapshot, image); // scalar bookkeeping cannot sample the tracker while this gate is held
            var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateSingletonVolumeOriginalContent", origin));
            Assert.Contains("ORIGINAL", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            // No positive native execution/ack is asserted by this synthetic construction control.
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            InvokeVolume("ReleaseSingletonVolumeNativeOriginals"); Assert.Equal(1, memory.Releases);
        }
        finally { hook.SetValue(null, old); Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Fact]
    public void SingletonVolumeCanceledQueuedWatchCleanupAttemptsAllAndNeverRetriesUnknownRelease()
    {
        ActualArchitecture(); using var state = new VolumeState();
        var first = new VolumeMemory(VolumePhysical()) { ReleaseFailure = new InvalidOperationException("first exact release unknown") };
        var second = new VolumeMemory(VolumePhysical());
        var snapshots = new[] { VolumeSnapshot(first), VolumeSnapshot(second) };
        var type = Presenter.GetNestedType("ComputeGuestDispatch", BindingFlags.NonPublic)!;
        var work = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [0UL, null, snapshots, Array.Empty<GuestMemoryBuffer>(), 1u, 1u, 1u, 0u, 0u, 0u, null], null)!;
        lock (Field("_gate").GetValue(null)!) InvokeVolume("RetainQueuedTextureSourcesLocked", work);
        MetalVideoPresenter.ReleasePreparedTextureSources(snapshots);
        lock (Field("_gate").GetValue(null)!)
        {
            var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ReleaseQueuedTextureSourcesLocked", work));
            Assert.Same(first.ReleaseFailure, error.InnerException);
            InvokeVolume("ReleaseQueuedTextureSourcesLocked", work);
        }
        Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases);
        Assert.Same(first.ReleaseFailure, Field("_singletonVolumeOriginalCleanupFailure").GetValue(null));
        MetalVideoPresenter.ReleasePreparedTextureSources(snapshots);
        Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases);
    }

    [Fact]
    public void SingletonVolumeInventoryBoundRejects129thOriginalAndCloseReleasesUntakenPreparations()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshots = new List<GuestDrawTexture>();
        for (var index = 0; index < 128; index++) snapshots.Add(VolumeSnapshot(memory));
        var failure = Assert.Throws<InvalidOperationException>(() => VolumeSnapshot(memory));
        Assert.Contains("inventory", failure.Message);
        Assert.Equal(129, memory.Copies); Assert.Equal(1, memory.Releases); // exact failed129th watch, no positive origin
        InvokeVolume("ReleaseSingletonVolumeNativeOriginals"); // includes all private, never-queued originals
        Assert.Equal(129, memory.Releases);
        MetalVideoPresenter.ReleasePreparedTextureSources(snapshots);
        Assert.Equal(129, memory.Releases); // close/caller cleanup cannot replay an exact release
    }

    [Theory]
    [InlineData("legacy")] [InlineData("different-original")]
    public void SingletonVolumeLiveLookupCannotAdoptAnotherOwnersNewlyCapturedOrigin(string kind)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var incoming = VolumeSnapshot(memory);
        var existingSeed = VolumeSnapshot(memory); var image = VolumeImage(GetOrigin(existingSeed), memory);
        try
        {
            if (kind == "legacy") { Set(image, "StorageCpuOrigin", null!); Set(image, "StorageCpuLayout", null!); }
            else PublishVolume(existingSeed, image);
            var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("EnsureStorageImage", (nint)0, incoming));
            Assert.Contains("cannot adopt", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image));
            Assert.Equal(2, memory.Copies); Assert.Equal(0, memory.Reads); // lookup performs no new mapping/token capture
            InvokeVolume("ReleaseSingletonVolumeNativeOriginals"); Assert.Equal(2, memory.Releases);
        }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([incoming, existingSeed]); }
    }

    private sealed class VolumeMemory(byte[] bytes) : Memory(bytes), ICpuMappedReadWatchSource
    {
        internal CpuMappedCopyResult Result = CpuMappedCopyResult.Copied;
        internal bool Current = true, WatchCurrent = true;
        internal int Copies, Releases;
        internal CpuMappedCopyObservation? LastMapping;
        internal Exception? ReleaseFailure; internal Action? AfterCopy;
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation)
        {
            Copies++; observation = null; if (Result != CpuMappedCopyResult.Copied) return Result;
            Assert.Equal(Address, footprintAddress); Assert.Equal(65536UL, footprintByteCount);
            Assert.Equal(Address, sourceAddress); Assert.Equal(65536, destination.Length);
            Bytes.CopyTo(destination); AfterCopy?.Invoke();
            observation = LastMapping = new(this, Address, 65536, Address, 65536,
                [new(Address, 65536, Address, 65536, 1, 1, 65536, 0, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]);
            return Result;
        }
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintByteCount, ulong sourceAddress,
            Span<byte> destination, out CpuMappedCopyObservation? observation) => throw new InvalidOperationException("Volume original must use watched copy.");
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => Current;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation) => WatchCurrent;
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation)
        { Releases++; if (ReleaseFailure is not null) throw ReleaseFailure; }
    }
    private sealed class VolumeState : IDisposable
    {
        private readonly State state = new();
        private readonly string[] names = ["_finiteStorageWritebackEnabled", "_singletonVolumeOriginalCleanupFailure", "_finiteStorageRetiredOrigins", "_finiteStorageRetiredOriginCount", "_finiteStorageRetiredUnknown"];
        private readonly object?[] values;
        private readonly object[] oldWatches;
        internal VolumeState()
        {
            var watches = (IEnumerable)Field("_singletonVolumeOriginalWatches").GetValue(null)!;
            oldWatches = watches.Cast<object>().ToArray();
            watches.GetType().GetMethod("Clear")!.Invoke(watches, []);
            values = names.Select(name => Field(name).GetValue(null)).ToArray();
            Field("_finiteStorageWritebackEnabled").SetValue(null, true);
            Field("_singletonVolumeOriginalCleanupFailure").SetValue(null, null);
            Field("_finiteStorageRetiredOrigins").SetValue(null, null);
            Field("_finiteStorageRetiredOriginCount").SetValue(null, 0);
            Field("_finiteStorageRetiredUnknown").SetValue(null, false);
        }
        public void Dispose()
        {
            Images.Remove(Address);
            var watches = Field("_singletonVolumeOriginalWatches").GetValue(null)!;
            watches.GetType().GetMethod("Clear")!.Invoke(watches, []);
            foreach (var watch in oldWatches) watches.GetType().GetMethod("Add")!.Invoke(watches, [watch]);
            for (var index = 0; index < names.Length; index++) Field(names[index]).SetValue(null, values[index]);
            state.Dispose();
        }
    }
    // Owner metadata only: no syscall, signal, native write completion or permission claim.
    private sealed class VolumeProtectedScalar : IDisposable
    {
        private readonly IDictionary ranges = (IDictionary)typeof(GuestImageWriteTracker).GetField("_rangesByAddress", Private)!.GetValue(null)!;
        private readonly object gate = typeof(GuestImageWriteTracker).GetField("_gate", Private)!.GetValue(null)!;
        private readonly object owner;
        private readonly object? previous;
        internal VolumeProtectedScalar()
        {
            owner = Activator.CreateInstance(typeof(GuestImageWriteTracker).GetNestedType("TrackedRange", BindingFlags.NonPublic)!, true)!;
            void SetRange(string name, object value) => owner.GetType().GetField(name)!.SetValue(owner, value);
            SetRange("Address", Address); SetRange("ByteCount", 65536UL); SetRange("Start", Address); SetRange("End", Address + 65536);
            SetRange("Protect", true); SetRange("Armed", 1); SetRange("WriteGeneration", 0L);
            lock (gate) { previous = ranges[Address]; ranges[Address] = owner; }
        }
        internal void Generation(long value) => owner.GetType().GetField("WriteGeneration")!.SetValue(owner, value);
        public void Dispose() { lock (gate) { if (previous is null) ranges.Remove(Address); else ranges[Address] = previous; } }
    }
}
