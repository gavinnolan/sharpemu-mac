// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class MetalNativeSnapshotObservationTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public void NativeSingletonAdmissionKeepsContentGuardsAndStrictGraphProvenance(bool graphEnabled, int mutation)
    {
        if (!OperatingSystem.IsMacOS()) return; // real registered Mac host protection behavior
        using var presenter = new MetalGuestFailureTests.PresenterState();
        var enabled = GuestImageWriteTracker.Enabled;
        var graphField = typeof(MetalVideoPresenter).GetField("_textureSourceGraphEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        var priorGraph = (bool)graphField.GetValue(null)!;
        graphField.SetValue(null, graphEnabled);
        void* allocation = null;
        void* unrelated = null;
        ulong address = 0;
        try
        {
            GuestImageWriteTracker.Configure(true);
            allocation = HostMemory.Alloc(null, 131072, HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
            unrelated = HostMemory.Alloc(null, 16384, HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
            Assert.NotEqual((nint)0, (nint)allocation);
            Assert.NotEqual((nint)0, (nint)unrelated);
            address = ((ulong)allocation + 65535) & ~65535UL;
            new Span<byte>((void*)address, 65536).Fill(0x37);
            uint[] words = [(uint)(address >> 8), (uint)((address >> 40) & 0xFF) | 0x03C00000,
                0, 0xA1B00FAC, 0, 0x00700000, 0, 0];
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(words, out var descriptor));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [],
                ResourceDescriptor: words,
                Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint),
                Access: GuestImageAccess.Write);
            var memory = new Memory(address, (nint)unrelated, mutation);
            var mappingBefore = HostMemory.MappingGeneration;
            if (mutation >= 2 || mutation == 1 && graphEnabled)
            {
                var error = Assert.Throws<InvalidOperationException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                    new CpuContext(memory, Generation.Gen5), [binding], out _));
                Assert.Contains("observed_before=True observed_now=True", error.Message);
                Assert.Contains("protect=True armed=True", error.Message);
                Assert.Equal(3, memory.Reads);
                if (mutation == 1)
                {
                    Assert.Contains("generation_before=0 generation_present=True generation_now=0", error.Message);
                    Assert.Equal(mappingBefore + 3, HostMemory.MappingGeneration);
                }
                else Assert.Equal(mappingBefore, HostMemory.MappingGeneration);
            }
            else
            {
                var image = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
                    new CpuContext(memory, Generation.Gen5), [binding], out _));
                Assert.True(image.CpuSnapshotStable);
                Assert.Equal(new byte[] { 0x37, 0x37, 0x37, 0x37 }, image.RgbaPixels);
                Assert.Equal(1, memory.Reads);
                Assert.Equal(mappingBefore + (mutation == 1 ? 1 : 0), HostMemory.MappingGeneration);
                // A graph-disabled mapping-raced copy stays usable as owned
                // bytes, but cannot be promoted to certified graph provenance.
                var table = typeof(MetalVideoPresenter).GetField("_cpuTextureSnapshotProvenance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                object?[] args = [image, null];
                Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
                Assert.Equal(mutation == 0, (bool)args[1]!.GetType().GetProperty("Stable")!.GetValue(args[1])!);
            }
            Assert.True(GuestImageWriteTracker.TryGetProtectionState(address, out var protect, out var armed));
            Assert.True(protect && armed);
            Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(address, 65536, out var token));
            Assert.Equal(mutation == 2 ? 3 : 0, token.ProtectedGeneration);
            Assert.Equal(65536UL, token.ProtectedByteCount);
            if (mutation == 0 || mutation == 1) Assert.Equal(memory.FirstToken, token);
            else Assert.NotEqual(memory.FirstToken, token);
            Assert.Equal(HostMemory.PAGE_READONLY, SharpEmu.Libs.Tests.Memory.NativePageProtection.Query(address));
        }
        finally
        {
            if (address != 0) GuestImageWriteTracker.Untrack(address);
            if (allocation != null) Assert.True(HostMemory.Free(allocation, 0, HostMemory.MEM_RELEASE));
            if (unrelated != null) Assert.True(HostMemory.Free(unrelated, 0, HostMemory.MEM_RELEASE));
            GuestImageWriteTracker.Configure(enabled);
            graphField.SetValue(null, priorGraph);
        }
    }

    private sealed class Memory(ulong address, nint unrelated, int mutation) : ICpuMemory
    {
        public int Reads;
        public GuestImageWriteTracker.ContentToken FirstToken;
        public bool CanRead(ulong a, ulong size) => a == address && size == 65536;
        public bool TryRead(ulong a, Span<byte> bytes)
        {
            Assert.True(CanRead(a, (ulong)bytes.Length));
            Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(a, (ulong)bytes.Length, out var token));
            if (Reads == 0) FirstToken = token;
            else if (mutation <= 1) Assert.Equal(FirstToken, token);
            Assert.Equal(HostMemory.PAGE_READONLY, SharpEmu.Libs.Tests.Memory.NativePageProtection.Query(a));
            Reads++;
            new ReadOnlySpan<byte>((void*)a, bytes.Length).CopyTo(bytes);
            if (mutation == 1) Assert.True(HostMemory.Protect((void*)unrelated, 16384, HostMemory.PAGE_READWRITE, out _));
            else if (mutation == 2)
            {
                GuestImageWriteTracker.NotifyManagedWrite(a, 4);
                *(uint*)a = (uint)Reads; // actual overlapping write after its managed notification
                GuestImageWriteTracker.Track(a, 65536); // owner rearmed, generation still advanced
            }
            else if (mutation == 3)
            {
                GuestImageWriteTracker.Untrack(a);
                GuestImageWriteTracker.Track(a, 65536); // generation0 reused with a different owner identity
            }
            return true;
        }
        public bool TryWrite(ulong a, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("No writes to snapshot backing.");
    }
}
