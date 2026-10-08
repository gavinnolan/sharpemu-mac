// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Metal.Tests;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private const ulong SelectedMipAddress = 0x7092C80000;
    private static uint[] SelectedMipRaw() => [0x7092C800, 0xC1600000, 0x003FC07F, 0x90911FAC, 511, 0x80, 0, 0];

    // Independent fixture construction: the mode9 32-bit AMD standard64K
    // bit order is XXYYYXYXYXYXYX (byte bits2..15). The whole chain has
    // one64K tail, one64K mip2, one128K mip1, and one512K mip0. Neither
    // production placement nor its detiler is used to build this seed.
    private static (byte[] Guest, byte[] Selected) SelectedMipSeed()
    {
        var guest = Enumerable.Repeat((byte)0xCD, 786432).ToArray();
        var selected = new byte[256 * 128 * 4];
        for (var y = 0; y < 128; y++)
            for (var x = 0; x < 256; x++)
            {
                var xx = x & 127;
                var offset = ((x >> 7) * 65536) +
                    ((xx & 3) << 2) + ((y & 7) << 4) + ((xx & 4) << 5) +
                    ((y & 8) << 5) + ((xx & 8) << 6) + ((y & 16) << 6) +
                    ((xx & 16) << 7) + ((y & 32) << 7) + ((xx & 32) << 8) +
                    ((y & 64) << 8) + ((xx & 64) << 9);
                var bits = BitConverter.SingleToUInt32Bits(y * 256 + x + .25f);
                BinaryPrimitives.WriteUInt32LittleEndian(guest.AsSpan(131072 + offset), bits);
                BinaryPrimitives.WriteUInt32LittleEndian(selected.AsSpan((y * 256 + x) * 4), bits);
            }
        return (guest, selected);
    }

    private static IReadOnlyList<GuestDrawTexture> SelectedMipSnapshots(ICpuMemory memory, uint[]? raw = null,
        uint mip = 1, ShaderImageShape? shape = null, GuestImageAccess access = GuestImageAccess.Read | GuestImageAccess.Write)
    {
        raw ??= SelectedMipRaw();
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, mip, [], ResourceDescriptor: raw,
            Shape: shape ?? new ShaderImageShape(false, false, true, false, TextureNumericClass.Float),
            Access: access);
        var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
        return (IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
            [new CpuContext(memory, Generation.Gen5), new[] { binding }, 0])!;
    }

    [Fact]
    public void SelectedMipSnapshotUsesOriginalChainPlacementAndOwnedRows()
    {
        var (guest, expected) = SelectedMipSeed();
        var before = guest.ToArray();
        var texture = Assert.Single(SelectedMipSnapshots(new SnapshotMemory(SelectedMipAddress, guest)));
        Assert.Equal(256u, texture.Width); Assert.Equal(128u, texture.Height);
        Assert.Equal(256u, texture.Pitch); Assert.Equal(1u, texture.MipLevel);
        Assert.Equal(1u, texture.BaseMipLevel); Assert.Equal(9u, texture.ResourceMipLevels);
        Assert.Equal(262144UL, texture.SourceByteCount);
        Assert.Equal(SelectedMipRaw(), texture.Descriptor);
        Assert.Equal(expected, texture.RgbaPixels); Assert.Equal(before, guest);
        Array.Fill(guest, (byte)0x44); Assert.Equal(expected, texture.RgbaPixels);
        Presenter.GetMethod("StorageTextureFormat", Methods)!.Invoke(null, [texture]);
        GuestImageWriteTracker.Untrack(SelectedMipAddress);
    }

    [Theory]
    [InlineData(0)] // missing complete descriptor
    [InlineData(1)] // metadata/flags
    [InlineData(2)] // selected level disagrees
    [InlineData(3)] // wrong backing geometry
    [InlineData(4)] // dynamic compiled mip
    public void SelectedMipUnsupportedViewsFailBeforeGuestRead(int kind)
    {
        var raw = SelectedMipRaw();
        if (kind == 0) raw = raw[..4];
        if (kind == 1) raw[6] = 1;
        if (kind == 3) raw[2] ^= 1;
        var memory = new CountingSelectedMemory([]);
        var shape = new ShaderImageShape(false, false, true, kind == 4, TextureNumericClass.Float);
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => SelectedMipSnapshots(memory, raw, kind == 2 ? 2u : 1u, shape));
        Assert.IsType<NotSupportedException>(ex.InnerException); Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void SelectedMipShortBackingFailsWithoutZeroOrBaseFallback()
    {
        var memory = new CountingSelectedMemory(new byte[262143]);
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => SelectedMipSnapshots(memory));
        Assert.IsType<InvalidOperationException>(ex.InnerException); Assert.Equal(1, memory.Reads);
        GuestImageWriteTracker.Untrack(SelectedMipAddress);
    }

    [Fact]
    public void SelectedMipAssociationsSeparateEqualAddressesInDifferentMemoryDomains()
    {
        var (guest, _) = SelectedMipSeed();
        var a = Assert.Single(SelectedMipSnapshots(new SnapshotMemory(SelectedMipAddress, guest)));
        var b = Assert.Single(SelectedMipSnapshots(new SnapshotMemory(SelectedMipAddress, guest)));
        var method = Presenter.GetMethod("SelectedStorageMipDomainOf", Methods)!;
        Assert.NotEqual(method.Invoke(null, [a]), method.Invoke(null, [b]));
        GuestImageWriteTracker.Untrack(SelectedMipAddress);
    }

    [Fact]
    public void SelectedMipAcceptedWriterRejectsOverlappingCpuAliasBeforeReadingAndSeparatesDomains()
    {
        using var state = new SelectedMipQueueScope();
        var (guest, _) = SelectedMipSeed();
        var memory = new CountingSelectedMemory(guest);
        var texture = Assert.Single(SelectedMipSnapshots(memory));
        Assert.True(EnqueueSelectedMip(texture) > 0);
        var reads = memory.Reads;
        var raw = SelectedMipRaw(); raw[3] &= ~0x000FF000u; // base view0, same backing
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => SelectedMipSnapshots(memory, raw, 0));
        Assert.IsType<NotSupportedException>(ex.InnerException); Assert.Equal(reads, memory.Reads);
        // The same VA in another ICpuMemory has independent canonical identity.
        Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(guest)));
        Assert.False(((IDictionary)Presenter.GetField("_availableGuestImages", Methods)!.GetValue(null)!).Contains(SelectedMipAddress));
        Assert.False(((IDictionary)Presenter.GetField("_guestImageWorkSequences", Methods)!.GetValue(null)!).Contains(SelectedMipAddress));
        var alias = SelectedMipRaw(); alias[0] += 512; alias[3] &= ~0x000FF000u;
        ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => SelectedMipSnapshots(memory, alias, 0));
        Assert.IsType<NotSupportedException>(ex.InnerException); Assert.Equal(reads, memory.Reads);
    }

    [Fact]
    public void SelectedMipCrossQueueConsumerIsRejectedBeforeQueueMutation()
    {
        using var state = new SelectedMipQueueScope();
        var (guest, _) = SelectedMipSeed();
        var texture = Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(guest)));
        using ((IDisposable)Presenter.GetMethod("EnterGuestQueue", Methods)!.Invoke(null, ["graphics", 1UL])!)
            Assert.Equal(1, EnqueueSelectedMip(texture));
        using ((IDisposable)Presenter.GetMethod("EnterGuestQueue", Methods)!.Invoke(null, ["compute", 2UL])!)
        {
            var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => EnqueueSelectedMip(texture));
            Assert.IsType<NotSupportedException>(ex.InnerException);
        }
        Assert.Equal(1L, Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null));
        Assert.Equal(1, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
        Assert.Equal(2, EnqueueSelectedMipInQueue(texture, "graphics"));
    }

    [Fact]
    public void SelectedMipClosedAdmissionDoesNotPublishAndOldSessionCannotMaterialize()
    {
        using var state = new SelectedMipQueueScope();
        var (guest, _) = SelectedMipSeed(); var memory = new CountingSelectedMemory(guest);
        var texture = Assert.Single(SelectedMipSnapshots(memory));
        Presenter.GetField("_closed", Methods)!.SetValue(null, true);
        Assert.Equal(0, EnqueueSelectedMip(texture));
        Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!);
        Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [(nint)0, texture]));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void SelectedMipCapturedSeedRejectsTrackerLifetimeAbaBeforeNativeAllocation()
    {
        var (guest, _) = SelectedMipSeed();
        GuestImageWriteTracker.TrackManagedWriter(SelectedMipAddress, 786432);
        try
        {
            var texture = Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(guest)));
            GuestImageWriteTracker.NotifyManagedWrite(SelectedMipAddress + 131072, 4);
            GuestImageWriteTracker.Untrack(SelectedMipAddress);
            GuestImageWriteTracker.TrackManagedWriter(SelectedMipAddress, 786432);
            var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [(nint)0, texture]));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }
        finally { GuestImageWriteTracker.Untrack(SelectedMipAddress); }
    }

    [Fact]
    public void SelectedMipNativeQueueMismatchRejectsBeforeMaterialization()
    {
        using var state = new SelectedMipQueueScope();
        var (guest, _) = SelectedMipSeed(); var texture = Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(guest)));
        var method = Presenter.GetMethod("ValidateSelectedStorageMipExecution", Methods)!;
        method.Invoke(null, [texture, (nint)1]);
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [texture, (nint)2]));
        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    [Fact]
    public void SelectedMipUnobservedOwnerCannotAuthorizeLiveReuse()
    {
        var enabled = GuestImageWriteTracker.Enabled;
        GuestImageWriteTracker.Configure(false);
        try
        {
            var (guest, _) = SelectedMipSeed();
            var texture = Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(guest)));
            var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                Presenter.GetMethod("ValidateSelectedStorageMipOwner", Methods)!.Invoke(null, [SelectedMipAssociation(texture), true]));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Contains("complete content observation", ex.InnerException!.Message);
        }
        finally { GuestImageWriteTracker.Untrack(SelectedMipAddress); GuestImageWriteTracker.Configure(enabled); }
    }

    private sealed class SelectedMipObservationScope : IDisposable
    {
        private readonly bool enabled = GuestImageWriteTracker.Enabled;
        public SelectedMipObservationScope()
        {
            // Explicit serial test-memory premise, not a claim about arbitrary
            // native guest stores or managed-copy serialization.
            GuestImageWriteTracker.Configure(true);
            GuestImageWriteTracker.TrackManagedWriter(SelectedMipAddress, 786432);
        }
        public void Dispose()
        {
            GuestImageWriteTracker.Untrack(SelectedMipAddress);
            GuestImageWriteTracker.Configure(enabled);
        }
    }

    private static long EnqueueSelectedMipInQueue(GuestDrawTexture texture, string name)
    {
        using var queue = (IDisposable)Presenter.GetMethod("EnterGuestQueue", Methods)!.Invoke(null, [name, 1UL])!;
        return EnqueueSelectedMip(texture);
    }

    private static long EnqueueSelectedMip(GuestDrawTexture texture)
    {
        var type = Presenter.GetNestedType("ComputeGuestDispatch", System.Reflection.BindingFlags.NonPublic)!;
        var work = Activator.CreateInstance(type, [0UL, null, new[] { texture }, Array.Empty<GuestMemoryBuffer>(),
            1u, 1u, 1u, 0u, 0u, 0u, null])!;
        lock (Presenter.GetField("_gate", Methods)!.GetValue(null)!)
            return (long)Presenter.GetMethod("EnqueueGuestWorkLocked", Methods)!.Invoke(null, [work])!;
    }

    private sealed class SelectedMipQueueScope : IDisposable
    {
        private readonly List<(System.Reflection.FieldInfo Field, object? Value)> saved = new();
        public SelectedMipQueueScope()
        {
            foreach (var (name, value) in new (string, object?)[] { ("_closed", false), ("_thread", Thread.CurrentThread),
                ("_guestWorkFailure", null), ("_enqueuedGuestWorkSequence", 0L), ("_pendingGuestWorkCount", 0),
                ("_pendingGuestWorkBytes", 0UL), ("_pendingGuestQueueCursor", 0), ("_submittingGuestQueue", null) })
            {
                var field = Presenter.GetField(name, Methods)!; saved.Add((field, field.GetValue(null))); field.SetValue(null, value);
            }
            Assert.Empty((IDictionary)Presenter.GetField("_pendingGuestWorkByQueue", Methods)!.GetValue(null)!);
            Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!);
        }
        public void Dispose()
        {
            foreach (var name in new[] { "_pendingGuestWorkByQueue", "_lastEnqueuedGuestWorkByQueue" })
                ((IDictionary)Presenter.GetField(name, Methods)!.GetValue(null)!).Clear();
            ((IList)Presenter.GetField("_pendingGuestQueueSchedule", Methods)!.GetValue(null)!).Clear();
            Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
            foreach (var (field, value) in saved) field.SetValue(null, value);
            GuestImageWriteTracker.Untrack(SelectedMipAddress);
        }
    }

    private sealed class CountingSelectedMemory(byte[] bytes) : ICpuMemory
    {
        public int Reads;
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads++;
            if (address < SelectedMipAddress || address - SelectedMipAddress > (ulong)bytes.Length ||
                (ulong)destination.Length > (ulong)bytes.Length - (address - SelectedMipAddress)) return false;
            bytes.AsSpan((int)(address - SelectedMipAddress), destination.Length).CopyTo(destination); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedMipNativeStoreLoadPreservesEveryOtherSelectedTexelAndGuestNeighbor(bool outsideSelectedMip)
    {
        using var observation = new SelectedMipObservationScope();
        var (guest, expected) = SelectedMipSeed(); var original = guest.ToArray();
        var memory = new CountingSelectedMemory(guest);
        var snapshot = Assert.Single(SelectedMipSnapshots(memory));
        uint[] coordinate = outsideSelectedMip ? [300, 200] : [201, 117];
        Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 9, out var store, out var error,
            dmask: 1, unifiedFormat: 22, addressValues: coordinate,
            storeValues: [BitConverter.SingleToUInt32Bits(42.5f), 0, 0, 0]), error);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 9, out var load, out error,
            dmask: 1, unifiedFormat: 22, addressValues: coordinate, storeResults: true), error);
        if (!RequireDevice()) return;
        nint storeLibrary = 0, loadLibrary = 0;
        try
        {
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            var texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(256UL, ReadInteger(texture, Selector("width"))); Assert.Equal(128UL, ReadInteger(texture, Selector("height")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("mipmapLevelCount")));
            Assert.Equal(expected, ReadNativeRows(texture, 256, 128, 4));
            Assert.True(DeviceShader.TryCompileLibrary(store!.Source, out storeLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(storeLibrary, store.EntryPoint, new byte[16], new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
            Presenter.GetMethod("RecordStorageImageWrites", Methods)!.Invoke(null, [new[] { snapshot }]);
            if (!outsideSelectedMip)
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan((117 * 256 + 201) * 4), BitConverter.SingleToUInt32Bits(42.5f));
            Assert.Equal(expected, ReadNativeRows(texture, 256, 128, 4));
            var readCount = memory.Reads;
            var nextSnapshot = Assert.Single(SelectedMipSnapshots(memory));
            Assert.Empty(nextSnapshot.RgbaPixels); Assert.Equal(readCount, memory.Reads);
            var again = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, nextSnapshot])!;
            Assert.Same(image, again); Assert.Equal(expected, ReadNativeRows(texture, 256, 128, 4));
            Assert.True(DeviceShader.TryCompileLibrary(load!.Source, out loadLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16], new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, texture), error);
            Assert.Equal(outsideSelectedMip ? 0u : BitConverter.SingleToUInt32Bits(42.5f), BinaryPrimitives.ReadUInt32LittleEndian(result));
            Assert.Equal(original, guest);
            Assert.False(((IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!).Contains(SelectedMipAddress));
            output.WriteLine($"Out-of-selected extent={outsideSelectedMip}; actual selected resource mip1 -> native level0 R32Float; all32768 texels compared, one translated MIMG store/load verified, complete guest chain/neighbor sentinels unchanged, repeat materialization preserved GPU store.");
        }
        finally
        {
            if (loadLibrary != 0) SendVoid(loadLibrary, Selector("release"));
            if (storeLibrary != 0) SendVoid(storeLibrary, Selector("release"));
            Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
            GuestImageWriteTracker.Untrack(SelectedMipAddress);
        }
    }
}
