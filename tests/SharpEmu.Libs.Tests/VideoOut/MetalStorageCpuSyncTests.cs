// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Tests.Fixtures;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalStorageCpuSyncTests
{
    private const ulong Address = 0x71F5_3000_00;
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Presenter = typeof(MetalVideoPresenter);
    private static uint[] Raw(int kind) => kind switch
    {
        0 => [(uint)(Address >> 8), 0x01400000, 0, 0xD1B00204, 0, 0x00700000, 0, 0],
        1 => [(uint)(Address >> 8), 0xC3800000, 0x00FFC0FF, 0xD1B00F2E, 0, 0x00700000, 0, 0],
        _ => [(uint)(Address >> 8), 0xC0500000, 0x021BC3BF, 0xD1800204, 0, 0x00700000, 0, 0],
    };
    private static AgcExports.TranslatedImageBinding Binding(int kind)
    {
        var raw = Raw(kind);
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false,
                kind == 1 ? TextureNumericClass.Float : TextureNumericClass.Uint), Access: GuestImageAccess.Write);
    }
    private static byte[] Physical(int kind) => kind switch
    {
        0 => Enumerable.Repeat((byte)0xAD, 65536).ToArray(),
        1 => Bgra1024Oracle.PhysicalSeed(),
        _ => R8FourKOracle.PhysicalSeed(),
    };
    private static byte[] Linear(int kind) => kind switch
    {
        0 => Enumerable.Repeat((byte)0xAD, 4).ToArray(),
        1 => Bgra1024Oracle.LinearSeed(),
        _ => R8FourKOracle.LinearSeed(),
    };
    private static void ActualArchitecture()
    {
        var actual = RuntimeInformation.ProcessArchitecture.ToString();
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrWhiteSpace(expected)) Assert.Equal(expected, actual);
        Console.WriteLine($"typed-cpu-sync-managed architecture={actual} framework={RuntimeInformation.FrameworkDescription}");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ActualProducerLayoutKeepsPhysicalExtentAndIndependentFullTypedTexels(int kind)
    {
        ActualArchitecture(); using var state = new State();
        var memory = new Memory(Physical(kind)); var binding = Binding(kind);
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
        var layout = Assert.IsType<MetalStorageCpuLayout>(MetalStorageCpuLayout.TryCreate(texture));
        Assert.Equal((ulong)memory.Bytes.Length, layout.PhysicalBytes); Assert.Equal(Linear(kind).Length, layout.LinearBytes);
        Assert.Equal(Linear(kind), layout.Decode(memory.Bytes));
        Assert.Equal(1, memory.Reads);
        var zero = layout.Decode(new byte[memory.Bytes.Length]);
        Assert.Equal(layout.LinearBytes, zero.Length); Assert.All(zero, value => Assert.Equal((byte)0, value));
        var upload = layout.Upload(zero); var rawBefore = upload.Descriptor!.ToArray();
        texture.Descriptor![5] ^= 1;
        Assert.Equal(rawBefore, layout.Upload(zero).Descriptor); // caller cannot mutate immutable raw8
        Assert.Equal((3u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(upload));
        Assert.Throws<InvalidOperationException>(() => layout.Decode(memory.Bytes.AsSpan(0, memory.Bytes.Length - 1)));
        Assert.Throws<InvalidOperationException>(() => layout.Upload(zero[..^1]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualRgba1024PublicationInverseMatchesEveryIndependentPhysicalByte(bool zero)
    {
        ActualArchitecture(); using var state = new State();
        var original = Physical(1);
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(new Memory(original), Generation.Gen5), [Binding(1)], out _));
        var layout = Assert.IsType<MetalStorageCpuLayout>(MetalStorageCpuLayout.TryCreate(texture));
        var linear = zero ? new byte[4194304] : Linear(1);
        var physical = layout.Encode(linear);
        Assert.Equal(zero ? new byte[4194304] : original, physical);
        Assert.Equal(linear, layout.Decode(physical));
        Assert.Equal(Physical(1), original); // no old seed mutation or padding fabrication
    }

    [Theory]
    [InlineData(257, 511)] [InlineData(1023, 1023)]
    public void ActualRgba1024PublicationChangesOnlyIndependentSelectedPhysicalTexel(int x, int y)
    {
        ActualArchitecture(); using var state = new State();
        var original = Physical(1);
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(new Memory(original), Generation.Gen5), [Binding(1)], out _));
        var layout = Assert.IsType<MetalStorageCpuLayout>(MetalStorageCpuLayout.TryCreate(texture));
        var linear = Linear(1);
        byte[] nativeBytes = [0, 128, 255, 64];
        nativeBytes.CopyTo(linear, (y * 1024 + x) * 4);
        var expected = original.ToArray();
        nativeBytes.CopyTo(expected, Bgra1024Oracle.Offset(x, y));
        Assert.Equal(expected, layout.Encode(linear));
        Assert.Equal(Physical(1), original);
    }

    [Fact]
    public void ActualRgbaPublicationInverseRejectsIncompleteOrUnqualifiedLayouts()
    {
        ActualArchitecture(); using var state = new State();
        var rgba = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(new Memory(Physical(1)), Generation.Gen5), [Binding(1)], out _));
        var layout = Assert.IsType<MetalStorageCpuLayout>(MetalStorageCpuLayout.TryCreate(rgba));
        Assert.Throws<NotSupportedException>(() => layout.Encode(new byte[4194303]));
        var singleton = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(new Memory(Physical(0)), Generation.Gen5), [Binding(0)], out _));
        var other = Assert.IsType<MetalStorageCpuLayout>(MetalStorageCpuLayout.TryCreate(singleton));
        Assert.Throws<NotSupportedException>(() => other.Encode(new byte[4]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualProducerDistinguishesRetainedDirtySeedGenerationFromLaterManagedWrite(bool writeAfterSeed)
    {
        ActualArchitecture(); using var state = new State();
        var memory = new MappedMemory(Physical(0));
        GuestImageWriteTracker.NotifyManagedWrite(Address, 65536);
        Assert.True(GuestImageWriteTracker.PeekDirty(Address));
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        var origin = GetOrigin(texture);
        var observation = (MetalVideoPresenter.CpuTextureSnapshotObservation)origin.GetType().GetProperty("Observation")!.GetValue(origin)!;
        Assert.True(observation.Observed);
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var afterSeed));
        Assert.Equal(observation.Token, afterSeed);
        Assert.True(GuestImageWriteTracker.PeekDirty(Address)); // no acknowledgment from the original copy
        if (writeAfterSeed)
        {
            GuestImageWriteTracker.NotifyManagedWrite(Address + 31, 1);
            memory.Bytes[31] ^= 0x5B; // an actual managed fixture byte effect after notification
        }
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var current));
        Assert.Equal(afterSeed.ProtectedIdentity, current.ProtectedIdentity);
        Assert.Equal(afterSeed.WatchIdentity, current.WatchIdentity);
        Assert.Equal(writeAfterSeed ? afterSeed.WatchGeneration + 1 : afterSeed.WatchGeneration, current.WatchGeneration);
        Assert.Equal(!writeAfterSeed, observation.Token == current);
        Assert.True(GuestImageWriteTracker.ConsumeDirty(Address));
        Assert.False(GuestImageWriteTracker.PeekDirty(Address));
        Assert.Equal(1, memory.Copies);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void NearCreationLayoutsCannotBecomeTypedDirtyUpload(int kind)
    {
        ActualArchitecture(); using var state = new State(); var binding = Binding(0);
        var original = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(new Memory(Physical(0)), Generation.Gen5), [binding], out _));
        var changed = kind switch
        {
            0 => original with { Type = 9, ArrayedView = false },
            1 => original with { Depth = 2 },
            2 => original with { ArrayLayers = 2 },
            3 => original with { MipLevel = 1 },
            4 => original with { SourceByteCount = 4 },
            _ => original with { CpuSnapshotStable = false },
        };
        Assert.Null(MetalStorageCpuLayout.TryCreate(changed));
    }

    [Theory]
    [InlineData(CpuMappedCopyResult.Unavailable, 1, 1)]
    [InlineData(CpuMappedCopyResult.Rejected, 3, 0)]
    public void ActualProducerUnavailablePreservesLegacyButRejectedNeverFallsBack(CpuMappedCopyResult result, int copies, int ordinaryReads)
    {
        ActualArchitecture(); using var state = new State(); var memory = new MappedMemory(Physical(0)) { Result = result };
        if (result == CpuMappedCopyResult.Unavailable)
            Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        else Assert.Throws<InvalidOperationException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        Assert.Equal(copies, memory.Copies); Assert.Equal(ordinaryReads, memory.Reads);
    }

    [Fact]
    public void ActualProducerDiscardRetiredOriginalCopyWithoutOrdinaryRead()
    {
        ActualArchitecture(); using var state = new State(); var memory = new MappedMemory(Physical(0)) { Current = false };
        Assert.Throws<InvalidOperationException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        Assert.Equal(3, memory.Copies); Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void ActualProducerRejectsSessionResetDuringOriginalMappedCopyWithoutRecapture()
    {
        ActualArchitecture(); using var state = new State();
        var before = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        var memory = new MappedMemory(Physical(0))
        {
            AfterCopy = () => Presenter.GetMethod("ReleaseSelectedStorageMipImages", Private)!.Invoke(null, null),
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        Assert.Contains("session changed during the original copy", error.Message);
        Assert.Equal(before + 1, MetalVideoPresenter.CurrentStorageCpuSeedSession);
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        Assert.False(Images.Contains(Address)); // no native owner or seed accepted into the newer session
    }

    [Fact]
    public void OriginalMappedSeedResetRejectsEnsureStorageBeforeAnyNativeAllocation()
    {
        ActualArchitecture(); using var state = new State(); var memory = new MappedMemory(Physical(0));
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        Presenter.GetMethod("ReleaseSelectedStorageMipImages", Private)!.Invoke(null, null);
        var error = Assert.Throws<TargetInvocationException>(() =>
            Presenter.GetMethod("EnsureStorageImage", Private)!.Invoke(null, [(nint)0, snapshot]));
        Assert.Contains("original-seed-session-changed", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        Assert.False(Images.Contains(Address)); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void PublicationSessionResetRejectsBeforeTypedConstructorRetainTransfers()
    {
        ActualArchitecture(); using var state = new State(); var memory = new MappedMemory(Physical(0));
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        var image = InstallImage(GetOrigin(snapshot), memory); Images.Remove(Address);
        var hook = Field("_storageImageBeforeMetadataPublication"); var previous = hook.GetValue(null);
        try
        {
            hook.SetValue(null, (Action<nint>)(_ => Presenter.GetMethod("ReleaseSelectedStorageMipImages", Private)!.Invoke(null, null)));
            var error = Assert.Throws<TargetInvocationException>(() =>
            {
                lock (Field("_gate").GetValue(null)!)
                    Presenter.GetMethod("PublishStorageImageLocked", Private)!.Invoke(null, [snapshot, null, image, (1u, 1u, 4UL)]);
            });
            Assert.Contains("original-seed-session-changed", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.False(Images.Contains(Address));
            Assert.False(((IDictionary)Field("_guestImageExtents").GetValue(null)!).Contains(Address));
            Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image)); // still constructor-owned, no native call
        }
        finally { hook.SetValue(null, previous); Images.Remove(Address); }
    }

    [Theory]
    [InlineData("gpu-debt")] [InlineData("pending")] [InlineData("version")]
    [InlineData("session")] [InlineData("unobserved")] [InlineData("retired")]
    public void RealTypedDirtyCaptureRejectsMissingAuthorityBeforeAnyDeviceCall(string kind)
    {
        ActualArchitecture(); using var state = new State();
        var memory = new MappedMemory(Physical(0));
        GuestImageWriteTracker.TrackManagedWriter(Address, 65536, source: "typed-sync-managed-control");
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        var origin = GetOrigin(snapshot); var image = InstallImage(origin, memory);
        try
        {
            switch (kind)
            {
                case "gpu-debt": Set(image, "GpuWritten", true); break;
                case "pending": Field("_pendingGuestWorkCount").SetValue(null, 1); break;
                case "version": Set(image, "TextureType", 2u); break;
                case "session": Field("_selectedStorageMipSession").SetValue(null, (long)Field("_selectedStorageMipSession").GetValue(null)! + 1); break;
                case "unobserved": GuestImageWriteTracker.Untrack(Address); break;
                case "retired": memory.Current = false; break;
            }
            var error = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("SynchronizeTypedStorageCpuSeed", Private)!.Invoke(null, [(nint)0, Address, image]));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image));
        }
        finally { Images.Remove(Address); }
    }

    [Fact]
    public void RealTypedDirtyCaptureAcceptsCompleteZeroCopyWithoutPublishingOrDevice()
    {
        ActualArchitecture(); using var state = new State(); var memory = new MappedMemory(Physical(0));
        GuestImageWriteTracker.TrackManagedWriter(Address, 65536, source: "typed-sync-zero-control");
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding(0)], out _));
        var image = InstallImage(GetOrigin(snapshot), memory);
        try
        {
            GuestImageWriteTracker.NotifyManagedWrite(Address, 65536); memory.Bytes.AsSpan().Clear();
            var captured = Presenter.GetMethod("CaptureTypedStorageCpuSeed", Private)!.Invoke(null, [Address, image])!;
            var upload = Assert.IsType<GuestDrawTexture>(captured.GetType().GetProperty("Upload")!.GetValue(captured));
            Assert.Equal(4, upload.RgbaPixels.Length); Assert.All(upload.RgbaPixels, value => Assert.Equal((byte)0, value));
            Assert.Equal(65536UL, upload.SourceByteCount); Assert.Equal(0, memory.Reads); Assert.Equal(2, memory.Copies);
            Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image));
            Assert.False((bool)image.GetType().GetField("GpuWritten")!.GetValue(image)!);
        }
        finally { Images.Remove(Address); }
    }

    private static object GetOrigin(GuestDrawTexture texture) => Presenter.GetMethod("StorageCpuOrigin", Private)!.Invoke(null, [texture])!;
    private static IDictionary Images => (IDictionary)Field("_guestImages").GetValue(null)!;
    private static FieldInfo Field(string name) => Presenter.GetField(name, Private)!;
    private static void Set(object image, string field, object value) => image.GetType().GetField(field)!.SetValue(image, value);
    private static object InstallImage(object origin, ICpuMemory memory)
    {
        Assert.NotNull(origin); var layout = (MetalStorageCpuLayout)origin.GetType().GetProperty("Layout")!.GetValue(origin)!;
        var image = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, nonPublic: true)!;
        foreach (var (name, value) in new (string, object)[] { ("Texture", (nint)123), ("Width", 1u), ("Height", 1u),
            ("TextureType", 3u), ("Depth", 1u), ("ArrayLength", 1u), ("Format", layout.NativeFormat.Format),
            ("Initialized", true), ("ContentVersion", 1), ("StorageCpuOrigin", origin), ("StorageCpuLayout", layout) }) Set(image, name, value);
        Images.Add(Address, image); MetalVideoPresenter.AttachGuestMemory(memory); return image;
    }

    private class Memory(byte[] bytes) : ICpuMemory
    {
        internal byte[] Bytes = bytes; internal int Reads;
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads++; if (address != Address || destination.Length != Bytes.Length) return false;
            Bytes.CopyTo(destination); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
    // Synthetic immutable mapping adapter qualifies branch/byte behavior only,
    // never actual provider receipts, native writer completion or game currentness.
    private sealed class MappedMemory(byte[] bytes) : Memory(bytes), ICpuMappedCopySource
    {
        internal CpuMappedCopyResult Result = CpuMappedCopyResult.Copied; internal bool Current = true; internal int Copies;
        internal Action? AfterCopy;
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintByteCount, ulong sourceAddress,
            Span<byte> destination, out CpuMappedCopyObservation? observation)
        {
            Copies++; observation = null; if (Result != CpuMappedCopyResult.Copied) return Result;
            Assert.Equal(Address, footprintAddress); Assert.Equal(65536UL, footprintByteCount);
            Assert.Equal(Address, sourceAddress); Assert.Equal(Bytes.Length, destination.Length);
            Bytes.CopyTo(destination);
            AfterCopy?.Invoke();
            observation = new(this, Address, 65536, Address, 65536,
                [new(Address, 65536, Address, 65536, 1, 1, 65536, 0, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]);
            return Result;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => Current;
    }
    private sealed class State : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState presenter = new();
        private readonly object? backend = typeof(GuestGpu).GetField("_instance", Private)!.GetValue(null);
        private readonly object? memory = Field("_guestMemory").GetValue(null);
        private readonly object? session = Field("_selectedStorageMipSession").GetValue(null);
        private readonly bool tracker = GuestImageWriteTracker.Enabled;
        public State()
        {
            GuestImageWriteTracker.Configure(true);
            // Ordinary managed-only fixture coverage prevents BeginReadSnapshot
            // from attempting native protection on our synthetic guest address.
            GuestImageWriteTracker.TrackManagedWriter(Address, 8847360, source: "typed-sync-synthetic-managed-only");
            typeof(GuestGpu).GetField("_instance", Private)!.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend)));
        }
        public void Dispose()
        {
            GuestImageWriteTracker.Untrack(Address); GuestImageWriteTracker.Configure(tracker);
            Field("_guestMemory").SetValue(null, memory); Field("_selectedStorageMipSession").SetValue(null, session);
            typeof(GuestGpu).GetField("_instance", Private)!.SetValue(null, backend); presenter.Dispose();
        }
    }
}
