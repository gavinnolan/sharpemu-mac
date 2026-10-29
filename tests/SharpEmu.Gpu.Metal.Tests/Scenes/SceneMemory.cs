// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>The guest address space a scene's page guard protects: everything succeeds.</summary>
internal sealed class SceneAddressSpace : IGuestAddressSpace
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

/// <summary>
/// One page-aligned native block seen as guest memory at <see cref="Base"/>: the GPU and
/// the CPU share it exactly as they share the guest's direct memory.
/// </summary>
internal sealed unsafe class SceneMemory : IGuestBackedSpace, ICpuMemory, IDisposable
{
    public const ulong Base = 0x2_0000_0000;
    // SHARPEMU_SCENE_MEMORY_MB=<n> sizes the block (64 by default). A larger one is mapped anonymously, so the
    // pages a scene never touches cost nothing, and it spans as many guest windows as a title's address table does.
    private const ulong DefaultSize = 64 * 1024 * 1024;
    public static readonly ulong Size =
        (ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SCENE_MEMORY_MB"), out var megabytes) && megabytes > 64 ? megabytes * 1024 * 1024 : DefaultSize);
    private readonly void* _block = Size > DefaultSize ? Map(Size) : NativeMemory.AlignedAlloc((nuint)Size, 16384);
    private ulong _next = Base;

    [DllImport("libSystem.dylib", EntryPoint = "mmap")]
    private static extern nint mmap(nint address, nuint length, int protection, int flags, int descriptor, long offset);

    [DllImport("libSystem.dylib", EntryPoint = "munmap")]
    private static extern int munmap(nint address, nuint length);

    private static void* Map(ulong size)
    {
        // PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON.
        var block = mmap(0, (nuint)size, 3, 0x1002, -1, 0);
        return block == -1 ? throw new InvalidOperationException($"Scene memory of 0x{size:X} bytes could not be mapped.") : (void*)block;
    }

    public SceneMemory()
    {
        if (Size <= DefaultSize) NativeMemory.Clear(_block, (nuint)Size);
    }

    public void Dispose()
    {
        if (Size > DefaultSize) munmap((nint)_block, (nuint)Size);
        else NativeMemory.AlignedFree(_block);
    }

    /// <summary>Hands out guest memory from a bump allocator.</summary>
    public ulong Allocate(ulong size, ulong alignment = 256)
    {
        var address = (_next + alignment - 1) & ~(alignment - 1);
        if (address + size > Base + Size)
        {
            throw new InvalidOperationException($"Scene memory is exhausted: size=0x{size:X}.");
        }

        _next = address + size;
        return address;
    }

    public ulong Upload(ReadOnlySpan<byte> data, ulong alignment = 256)
    {
        var address = Allocate((ulong)Math.Max(data.Length, 4), alignment);
        Write(address, data);
        return address;
    }

    public ulong Upload<T>(ReadOnlySpan<T> data, ulong alignment = 256) where T : unmanaged =>
        Upload(MemoryMarshal.AsBytes(data), alignment);

    public void Write(ulong address, ReadOnlySpan<byte> data)
    {
        if (!TryWrite(address, data))
        {
            throw new InvalidOperationException($"Scene memory write is out of range: 0x{address:X16}+0x{data.Length:X}.");
        }
    }

    public void Write<T>(ulong address, ReadOnlySpan<T> data) where T : unmanaged => Write(address, MemoryMarshal.AsBytes(data));

    public void Write32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Write(address, bytes);
    }

    public void Write64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Write(address, bytes);
    }

    /// <summary>The bytes as memory holds them now, without any GPU synchronization.</summary>
    public byte[] ReadRaw(ulong address, int length)
    {
        var bytes = new byte[length];
        if (!TryRead(address, bytes))
        {
            throw new InvalidOperationException($"Scene memory read is out of range: 0x{address:X16}+0x{length:X}.");
        }

        return bytes;
    }

    private static bool Contains(ulong address, ulong length) => address >= Base && address - Base + length <= Size;

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
        alias = Contains(address, size) ? (ulong)_block + (address - Base) : 0;
        return alias != 0;
    }

    public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => TryWrite(address, data);

    public bool TryReadBacking(ulong address, Span<byte> data) => TryRead(address, data);

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (!Contains(virtualAddress, (ulong)destination.Length)) return false;
        new ReadOnlySpan<byte>((byte*)_block + (virtualAddress - Base), destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        if (!Contains(virtualAddress, (ulong)source.Length)) return false;
        source.CopyTo(new Span<byte>((byte*)_block + (virtualAddress - Base), source.Length));
        return true;
    }

    public bool CanRead(ulong address, ulong size) => Contains(address, size);
}
