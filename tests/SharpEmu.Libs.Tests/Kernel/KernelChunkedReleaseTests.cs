// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// Over the chunked guest backing, memory the guest frees goes back to the host a whole
/// 64 KiB chunk at a time, once every byte of the chunk is free: released direct memory,
/// unmapped flexible memory and decommitted pool pages. A released chunk reads zero; bytes the
/// guest still owns (allocated direct memory it only unmapped, a chunk partly free) keep their
/// contents.
/// </summary>
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed unsafe class KernelChunkedReleaseTests : IDisposable
{
    private const ulong Chunk = 0x10000;
    private const ulong FlexibleOffset = 0x4000000;
    private readonly bool _chunked = HostBackendUnderTest.SetChunkedBacking(true);

    public void Dispose() => HostBackendUnderTest.SetChunkedBacking(null);

    private static bool All(BackedKernelMemory test, ulong offset, ulong size, byte value) =>
        !new ReadOnlySpan<byte>((void*)(test.Memory.BackingAliasRange.Base + offset), (int)size).ContainsAnyExcept(value);

    private static void Fill(ulong address, ulong size, byte value) =>
        new Span<byte>((void*)address, (int)size).Fill(value);

    private static int Call(BackedKernelMemory test, Func<CpuContext, int> export, params ulong[] arguments)
    {
        CpuRegister[] registers = [CpuRegister.Rdi, CpuRegister.Rsi, CpuRegister.Rdx, CpuRegister.Rcx, CpuRegister.R8, CpuRegister.R9];
        for (var index = 0; index < arguments.Length; index++)
            test.Context[registers[index]] = arguments[index];
        return export(test.Context);
    }

    [Fact]
    public void ReleasedDirectMemoryGoesBackOnceEachChunkIsWhollyFree()
    {
        if (!_chunked) return;
        using var test = new BackedKernelMemory();
        Assert.Equal(Chunk, test.Memory.BackingChunkBytes);
        test.Allocate(0, 3 * Chunk);
        var address = test.Map(0, 3 * Chunk);
        Fill(address, 3 * Chunk, 0xA5);

        // Unmapping keeps the allocation, so it keeps the bytes.
        Assert.Equal(0, test.Unmap(address, 3 * Chunk));
        Assert.True(All(test, 0, 3 * Chunk, 0xA5));

        Assert.Equal(0, test.Release(Chunk, Chunk));
        Assert.True(All(test, 0, Chunk, 0xA5));
        Assert.True(All(test, Chunk, Chunk, 0));
        Assert.True(All(test, 2 * Chunk, Chunk, 0xA5));

        // Half of chunk 0 free: it stays until the other half is released too.
        Assert.Equal(0, test.Release(0, Chunk / 2));
        Assert.True(All(test, 0, Chunk, 0xA5));
        Assert.Equal(0, test.Release(Chunk / 2, Chunk / 2));
        Assert.True(All(test, 0, 2 * Chunk, 0));
        Assert.True(All(test, 2 * Chunk, Chunk, 0xA5));

        // Released while mapped: the release unmaps it first, then the chunk goes.
        var again = test.Map(2 * Chunk, Chunk);
        Assert.Equal(0xA5, *(byte*)again);
        Assert.Equal(0, test.Release(2 * Chunk, Chunk));
        Assert.True(All(test, 2 * Chunk, Chunk, 0));
    }

    [Fact]
    public void UnmappedFlexibleMemoryGoesBackChunkByChunk()
    {
        if (!_chunked) return;
        using var test = new BackedKernelMemory();
        var previous = KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(FlexibleOffset, FlexibleOffset));
        try
        {
            var flexible = test.Flexible(4 * Chunk);
            Assert.True(test.Memory.TryResolveBackingAlias(flexible, 4 * Chunk, out var alias));
            var offset = alias - test.Memory.BackingAliasRange.Base;
            Fill(flexible, 4 * Chunk, 0x3C);

            // The middle two chunks.
            Assert.Equal(0, test.Unmap(flexible + Chunk, 2 * Chunk));
            Assert.True(All(test, offset, Chunk, 0x3C));
            Assert.True(All(test, offset + Chunk, 2 * Chunk, 0));
            Assert.True(All(test, offset + 3 * Chunk, Chunk, 0x3C));

            // 16 KiB of the last chunk: not wholly free yet.
            Assert.Equal(0, test.Unmap(flexible + 3 * Chunk, 0x4000));
            Assert.True(All(test, offset + 3 * Chunk, Chunk, 0x3C));
            Assert.Equal(0, test.Unmap(flexible + 3 * Chunk + 0x4000, Chunk - 0x4000));
            Assert.True(All(test, offset + 3 * Chunk, Chunk, 0));
            Assert.True(All(test, offset, Chunk, 0x3C));
        }
        finally
        {
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
            KernelMemoryCompatExports.SetFlexibleBackingForTests(previous);
        }
    }

    [Fact]
    public void DecommittedPoolPagesGoBack()
    {
        if (!_chunked) return;
        using var test = new BackedKernelMemory();
        var previous = KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(FlexibleOffset, FlexibleOffset));
        try
        {
            Assert.Equal(0, Call(test, KernelMemoryCompatExports.KernelMemoryPoolReserve, 0, 0x400000, 0, 0, test.Output + 0x30));
            Assert.True(test.Context.TryReadUInt64(test.Output + 0x30, out var pool));
            Assert.Equal(0, Call(test, KernelMemoryCompatExports.KernelMemoryPoolExpand, 0, 0x2000000, 0x200000, 0, test.Output + 0x38));
            Assert.Equal(0, Call(test, KernelMemoryCompatExports.KernelMemoryPoolCommit, pool, 2 * Chunk, 0, 3, 0));
            Assert.True(test.Memory.TryResolveBackingAlias(pool, 2 * Chunk, out var alias));
            var offset = alias - test.Memory.BackingAliasRange.Base;
            Fill(pool, 2 * Chunk, 0x77);

            Assert.Equal(0, Call(test, KernelMemoryCompatExports.KernelMemoryPoolDecommit, pool, Chunk, 0));
            Assert.True(All(test, offset, Chunk, 0));
            Assert.True(All(test, offset + Chunk, Chunk, 0x77));
            Assert.Equal(0x77, *(byte*)(pool + Chunk));
            Assert.Equal(0, Call(test, KernelMemoryCompatExports.KernelMemoryPoolDecommit, pool + Chunk, Chunk, 0));
        }
        finally
        {
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
            KernelMemoryCompatExports.SetFlexibleBackingForTests(previous);
        }
    }
}
