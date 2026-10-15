// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>A table the host binds for one pointer field of the argument buffer (the
/// device-address ranges, the fault bitmap): its buffer, the count the paired word field
/// holds, and the buffers it points into, which have to be resident too.</summary>
internal sealed record DeviceTable(DescriptorBindingKind Kind, nint Buffer, uint Count, IReadOnlyList<nint> Residents);

internal static partial class MetalNative
{
    private static DeviceTable? FindTable(IReadOnlyList<DeviceTable>? tables, DescriptorBindingKind kind) =>
        tables?.FirstOrDefault(table => table.Kind == kind);

    /// <summary>The address-range table and fault bitmap mapping one guest range onto a
    /// shared buffer holding <paramref name="guestBytes"/>, as the v2 host builds them
    /// (Gen5AddressRange: first page, page count, host pointer; 16 KB pages).</summary>
    public static (IReadOnlyList<DeviceTable> Tables, nint Faults) CreateGuestMapping(ulong guestBase, byte[] guestBytes, uint faultWords)
    {
        var pageBytes = 1ul << DeviceAddressPaging.PageBits;
        if (guestBase % pageBytes != 0 || (ulong)guestBytes.Length % pageBytes != 0)
            throw new ArgumentException("the guest range has to cover whole pages");

        var memory = SharedBuffer(guestBytes);
        var range = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(range, guestBase >> DeviceAddressPaging.PageBits);
        BinaryPrimitives.WriteUInt64LittleEndian(range.AsSpan(8), (ulong)guestBytes.Length / pageBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(range.AsSpan(16), SendULong(memory, Selector("gpuAddress")));
        var ranges = SharedBuffer(range);
        var faults = SharedBuffer(new byte[faultWords * sizeof(uint)]);
        return (
            [
                new DeviceTable(DescriptorBindingKind.DeviceAddressPageTable, ranges, 1, [memory]),
                new DeviceTable(DescriptorBindingKind.FaultBuffer, faults, faultWords, []),
            ],
            faults);
    }

    public static uint[] ReadWords(nint buffer, int count)
    {
        var contents = Send(buffer, Selector("contents"));
        var words = new int[count];
        Marshal.Copy(contents, words, 0, count);
        return Array.ConvertAll(words, static word => unchecked((uint)word));
    }
}
