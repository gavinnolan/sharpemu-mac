// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// What naming a window costs, measured the way the driver charges it: a command buffer that
/// names a no-copy window wires and faults in every page of it, so the pages of the backing
/// resident afterwards (mincore on the alias) are the pages the named windows cover. Two words
/// written far apart in a 2 GiB shared mapping through granule windows leave about two
/// granules resident; through 1 GiB windows they leave two gigabytes.
/// </summary>
public sealed unsafe partial class MetalGuestWindowResidencyTests(ITestOutputHelper output)
{
    private const ulong GuestBase = 0x3_0000_0000;
    private const ulong BackingSize = 2UL * 1024 * 1024 * 1024;
    private const ulong Mib = 1024 * 1024;

    // Far apart: one near the start, one near the end of the mapping.
    private static readonly ulong[] Touched = [GuestBase + (100 * Mib) + 0x40, GuestBase + BackingSize - (100 * Mib) + 0x80];

    private const string KernelSource = """
        #include <metal_stdlib>
        using namespace metal;
        kernel void touch(device const ulong* addresses [[buffer(0)]], uint tid [[thread_position_in_grid]])
        {
            device uint* word = (device uint*)addresses[tid];
            *word = 0xC0DE0000u + tid;
        }
        """;

    // A guest backing like the real one: an anonymous shared mapping nothing has touched.
    private sealed class MappedBacking : IGuestBackedSpace, IDisposable
    {
        private const int ProtRead = 1;
        private const int ProtWrite = 2;
        private const int MapShared = 0x0001;
        private const int MapAnonymous = 0x1000;

        public MappedBacking()
        {
            Base = Mmap(0, (nuint)BackingSize, ProtRead | ProtWrite, MapShared | MapAnonymous, -1, 0);
            Assert.NotEqual(-1, Base);
        }

        public nint Base { get; }

        public void Dispose() => Munmap(Base, (nuint)BackingSize);

        /// <summary>Bytes of the mapping resident in memory now.</summary>
        public ulong ResidentBytes()
        {
            var pageSize = (ulong)Environment.SystemPageSize;
            var pages = BackingSize / pageSize;
            var vector = new byte[pages];
            fixed (byte* bytes = vector)
            {
                Assert.Equal(0, Mincore(Base, (nuint)BackingSize, bytes));
            }

            return (ulong)vector.Count(value => (value & 1) != 0) * pageSize;
        }

        private static bool Contains(ulong address, ulong length) =>
            address >= GuestBase && address - GuestBase + length <= BackingSize;

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => ((ulong)Base, BackingSize);

        public bool TryResolveBackingAlias(ulong address, ulong size, out ulong alias)
        {
            alias = Contains(address, size) ? (ulong)Base + (address - GuestBase) : 0;
            return alias != 0;
        }

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => false;

        public bool TryReadBacking(ulong address, Span<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            new ReadOnlySpan<byte>((byte*)Base + (address - GuestBase), data.Length).CopyTo(data);
            return true;
        }
    }

    private sealed class NoCpu : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
        public bool CanRead(ulong address, ulong size) => false;
    }

    // The granule default: two touched words leave about two granules resident, a sliver
    // of the 2 GiB the backing holds.
    [Fact]
    public void ResidentBytesFollowTheGranulesAKernelTouches()
    {
        if (!RequireDevice())
        {
            return;
        }

        var (resident, granule) = Measure(MetalGuestMemory.DefaultGranuleBytes);
        output.WriteLine($"granule {granule / Mib} MiB: resident {resident / Mib} MiB of {BackingSize / Mib} MiB");
        Assert.True(resident <= ((ulong)Touched.Length * granule) + (16 * Mib),
            $"resident 0x{resident:X} for two words in 0x{granule:X} granules");
    }

    // The same two words through 1 GiB granules - the old window size - fault in both
    // gigabytes: what the default avoids, and proof the measurement sees the wiring.
    [Fact]
    public void OneGibibyteWindowsFaultInTheWholeWindow()
    {
        if (!RequireDevice())
        {
            return;
        }

        var (resident, granule) = Measure(1024 * Mib);
        output.WriteLine($"granule {granule / Mib} MiB: resident {resident / Mib} MiB of {BackingSize / Mib} MiB");
        Assert.True(resident >= BackingSize * 3 / 4, $"resident only 0x{resident:X} through 1 GiB windows");
    }

    private (ulong Resident, ulong Granule) Measure(ulong granuleBytes)
    {
        using var backing = new MappedBacking();
        Assert.Equal(0UL, backing.ResidentBytes());
        using var memory = new MetalGuestMemory(Device.Value, backing, new NoCpu(), granuleBytes, 1024 * Mib);
        using var harness = new MetalTransferHarness(Device.Value);
        var pipeline = ComputePipeline(KernelSource, "touch");
        try
        {
            memory.BeginSubmission(1, 0);
            var addresses = new ulong[Touched.Length];
            for (var index = 0; index < Touched.Length; index++)
            {
                var range = memory.ResolveWritable(Touched[index], 4);
                Assert.True(range.IsValid);
                addresses[index] = range.GpuAddress;
            }

            harness.Begin();
            var encoder = MetalNative.Send(harness.CommandBuffer, MetalNative.Selector("computeCommandEncoder"));
            MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), pipeline);
            fixed (ulong* data = addresses)
            {
                MetalNative.SendSetBytes(encoder, MetalNative.Selector("setBytes:length:atIndex:"), (nint)data,
                    (nuint)(addresses.Length * sizeof(ulong)), 0);
            }

            // The backend names the windows the submission resolved, exactly as here.
            var resident = memory.ResidentBuffers.ToArray();
            fixed (nint* resources = resident)
            {
                MetalNative.SendUseResources(encoder, MetalNative.Selector("useResources:count:usage:"), resources,
                    (nuint)resident.Length, Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite);
            }

            MetalNative.SendDispatch(encoder, MetalNative.Selector("dispatchThreads:threadsPerThreadgroup:"),
                new MtlSize { Width = (nuint)addresses.Length, Height = 1, Depth = 1 },
                new MtlSize { Width = (nuint)addresses.Length, Height = 1, Depth = 1 });
            MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
            harness.Submit();

            var word = new byte[4];
            for (var index = 0; index < Touched.Length; index++)
            {
                Assert.True(backing.TryReadBacking(Touched[index], word));
                Assert.Equal(0xC0DE0000u + (uint)index, BitConverter.ToUInt32(word));
            }

            return (backing.ResidentBytes(), granuleBytes);
        }
        finally
        {
            MetalNative.SendVoid(pipeline, MetalNative.Selector("release"));
        }
    }

    internal static nint ComputePipeline(string source, string name)
    {
        nint error = 0;
        var library = MetalNative.Send(Device.Value, MetalNative.Selector("newLibraryWithSource:options:error:"),
            MetalNative.NsString(source), 0, ref error);
        Assert.True(library != 0, MetalNative.DescribeError(error));
        var function = MetalNative.Send(library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString(name));
        Assert.NotEqual(0, function);
        error = 0;
        var pipeline = MetalNative.Send(Device.Value, MetalNative.Selector("newComputePipelineStateWithFunction:error:"),
            function, ref error);
        Assert.True(pipeline != 0, MetalNative.DescribeError(error));
        MetalNative.SendVoid(function, MetalNative.Selector("release"));
        MetalNative.SendVoid(library, MetalNative.Selector("release"));
        return pipeline;
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Window residency runs on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    internal static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mmap")]
    private static partial nint Mmap(nint address, nuint length, int protection, int flags, int fd, long offset);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "munmap")]
    private static partial int Munmap(nint address, nuint length);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mincore")]
    private static partial int Mincore(nint address, nuint length, byte* vector);
}
