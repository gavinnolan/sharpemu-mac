// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Metal.Tests;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The render host's path through the process's archive: a translated compute program's
/// library and pipeline are stored by the first host and taken from the archive by the next.
/// </summary>
public sealed unsafe class MetalBinaryArchiveHostTests(ITestOutputHelper output) : IDisposable
{
    private const ulong GuestBase = 0x2_0000_0000;
    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharpemu-binary-archive-host-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_root))
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheNextHostTakesATranslatedComputeLibraryAndPipelineFromTheArchive()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Module libraries are built on the device only behind the exclusive device gate.");
            return;
        }

        Assert.NotEqual(0, Device.Value);
        var request = Gen5ComputeFixtures.CreateComputeRequest(Gen5ComputeFixtures.ExecStore);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        // Unique text, so the first host cannot find it in an archive another run left.
        shader = shader with { Source = $"// archive test {Guid.NewGuid():N}\n{shader.Source}" };

        var first = Run(shader);
        Assert.Equal(0, first.LibraryHits);
        Assert.Equal(1, first.LibrariesWritten);

        var second = Run(shader);
        Assert.Equal(1, second.LibraryHits);
        Assert.Equal(1, second.PipelineHits);
        Assert.Equal(0, second.PipelineMisses);
        Assert.Equal(0, second.LibrariesWritten);
    }

    private (int LibraryHits, int LibraryMisses, int Rejected, int LibrariesWritten,
        int PipelineHits, int PipelineMisses, int PipelinesAdded, int LoadedSegments) Run(Gen5MslShader shader)
    {
        var archive = MetalBinaryArchive.Open(_root, Device.Value, "translator-a", 64L << 20);
        var previous = MetalBinaryArchive.Install(archive);
        try
        {
            using (var rig = new Rig(Device.Value))
            {
                var metal = new MetalCompiledGuestShader(shader);
                var program = new ShaderProgram(1, rig.Host.CreateShaderModule(metal, ShaderStage.Compute, 0xA5C1, 1));
                rig.Host.CreateComputePipeline(new ComputePipelineDescription
                {
                    Input = new ComputeInputInfo { ThreadsX = 32, ThreadsY = 1, ThreadsZ = 1 },
                    Program = program,
                    Stage = new ShaderProgramInfo { Stage = ShaderStageKind.Compute, Hash = 0xA5C1 },
                });
            }

            // The host's disposal waited for the writer.
            return archive.Counters;
        }
        finally
        {
            MetalBinaryArchive.Install(previous);
            archive.Dispose();
        }
    }

    private sealed class AddressSpace : IGuestAddressSpace
    {
        public ulong ProtectionPageSize => 4096;
        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;
        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) => throw new NotSupportedException();
        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => throw new NotSupportedException();
        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress) => throw new NotSupportedException();
        public bool TryEnsureRangeCommitted(ulong address, ulong size) => throw new NotSupportedException();
        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => throw new NotSupportedException();
        public bool TryFreeGuestMemory(ulong address) => throw new NotSupportedException();
    }

    // One page-aligned native block seen as guest memory at GuestBase.
    private sealed class Backing : IGuestBackedSpace, ICpuMemory, IDisposable
    {
        public const ulong Size = 1024 * 1024;
        private readonly void* _block = NativeMemory.AlignedAlloc((nuint)Size, 16384);

        public Backing() => NativeMemory.Clear(_block, (nuint)Size);

        public void Dispose() => NativeMemory.AlignedFree(_block);

        private bool Contains(ulong address, ulong length) => address >= GuestBase && address - GuestBase + length <= Size;

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => ((ulong)_block, Size);

        public bool TryResolveBackingAlias(ulong address, ulong size, out ulong alias)
        {
            alias = Contains(address, size) ? (ulong)_block + (address - GuestBase) : 0;
            return alias != 0;
        }

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => TryWrite(address, data);

        public bool TryReadBacking(ulong address, Span<byte> data) => TryRead(address, data);

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!Contains(virtualAddress, (ulong)destination.Length)) return false;
            new ReadOnlySpan<byte>((byte*)_block + (virtualAddress - GuestBase), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!Contains(virtualAddress, (ulong)source.Length)) return false;
            source.CopyTo(new Span<byte>((byte*)_block + (virtualAddress - GuestBase), source.Length));
            return true;
        }

        public bool CanRead(ulong address, ulong size) => Contains(address, size);
    }

    private sealed class Rig : IDisposable
    {
        private readonly nint _queue;
        private readonly AddressSpace _space = new();
        private readonly PageGuard _pages;
        private readonly Backing _backing = new();

        public Rig(nint device)
        {
            _queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue"));
            _pages = new PageGuard(_space);
            Host = new MetalRenderHost(device, _queue, _backing, _backing, _pages, new MetalGpuWorkerRelay(static () => { }));
        }

        public MetalRenderHost Host { get; }

        public void Dispose()
        {
            Host.ImageCache.StopWatching();
            Host.Dispose();
            _pages.Dispose();
            _backing.Dispose();
            MetalNative.SendVoid(_queue, MetalNative.Selector("release"));
        }
    }
}
