// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ImageCodec;

/// <summary>
/// Guest-memory plumbing shared by the four image libraries. Their decoder/encoder handles
/// live in the work memory the title passes to Create: the handle is that memory (aligned up)
/// and its first quadword points at itself while the handle is live, so a deleted, foreign or
/// misaligned handle is told apart without any host-side table.
/// </summary>
internal static class ImageLibraryGuest
{
    // Larger guest buffers than this are not copied into a managed array.
    private const ulong MaxCopyBytes = int.MaxValue;

    public static ulong AlignUp(ulong address, ulong alignment) =>
        (address + alignment - 1) & ~(alignment - 1);

    /// <summary>Writes the self pointer at <paramref name="handle"/>, marking it live.</summary>
    public static bool TryMarkLive(CpuContext ctx, ulong handle) => ctx.TryWriteUInt64(handle, handle);

    public static bool IsLive(CpuContext ctx, ulong handle, ulong alignment) =>
        handle != 0 &&
        handle % alignment == 0 &&
        ctx.TryReadUInt64(handle, out var self) &&
        self == handle;

    public static bool TryReadBytes(CpuContext ctx, ulong address, ulong size, out byte[] bytes)
    {
        bytes = [];
        if (size > MaxCopyBytes)
        {
            return false;
        }

        var buffer = GC.AllocateUninitializedArray<byte>((int)size);
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        bytes = buffer;
        return true;
    }

    /// <summary>
    /// Reads <paramref name="height"/> rows of <paramref name="rowBytes"/> from a pitched guest
    /// image into a packed array.
    /// </summary>
    public static bool TryReadRows(
        CpuContext ctx,
        ulong address,
        ulong pitch,
        int rowBytes,
        int height,
        out byte[] packed)
    {
        packed = [];
        if ((long)rowBytes * height > (long)MaxCopyBytes)
        {
            return false;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(rowBytes * height);
        for (var y = 0; y < height; y++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)y * pitch, buffer.AsSpan(y * rowBytes, rowBytes)))
            {
                return false;
            }
        }

        packed = buffer;
        return true;
    }

    /// <summary>Writes packed rows to a pitched guest image; the bytes between rows are left alone.</summary>
    public static bool TryWriteRows(CpuContext ctx, ulong address, ulong pitch, ReadOnlySpan<byte> packed, int rowBytes, int height)
    {
        for (var y = 0; y < height; y++)
        {
            if (!ctx.Memory.TryWrite(address + (ulong)y * pitch, packed.Slice(y * rowBytes, rowBytes)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The decoders' success value: width in the high half, height in the low half.</summary>
    public static int PackDimensions(uint width, uint height) =>
        width > 0x7FFF || height > 0x7FFF ? 0 : (int)((width << 16) | height);
}
