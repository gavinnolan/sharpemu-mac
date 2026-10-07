// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native.Platform;

namespace SharpEmu.Cpu.DirectExecution.Windows.X64;

/// <summary>
/// Thread stack bounds in the Windows TEB, and the emitted code that swaps
/// them so the runtime sees the stack guest code is actually running on.
/// </summary>
/// <remarks>
/// Moved verbatim out of
/// <c>SharpEmu.Core/Cpu/Native/DirectExecutionBackend.StackBounds.cs</c> by
/// W10, minus the Windows checks this assembly no longer needs. Only Windows
/// keeps stack bounds at <c>gs:[8]</c>/<c>gs:[16]</c>: on macOS gs addresses
/// the pthread TSD, where those slots hold errno's address and another key.
/// </remarks>
internal static unsafe class WindowsStackBounds
{
    private static delegate* managed<ulong, ulong*, ulong*, bool> _tryGetStackBounds;

    internal static void SetRecovery(in DirectExecutionFaultHandlers handlers) =>
        _tryGetStackBounds = handlers.TryGetStackBounds;

    private static bool TryGetStackBounds(ulong stackPointer, out ulong bottom, out ulong top)
    {
        ulong low = 0, high = 0;
        var found = _tryGetStackBounds != null && _tryGetStackBounds(stackPointer, &low, &high);
        bottom = low;
        top = high;
        return found;
    }

    internal static void EmitByte(byte* code, ref int offset, byte value) => code[offset++] = value;

    internal static void EmitUInt32(byte* code, ref int offset, uint value)
    {
        *(uint*)(code + offset) = value;
        offset += sizeof(uint);
    }

    /// <summary>
    /// Bounds of the committed allocation holding <paramref name="stackPointer"/>,
    /// from the memory-information query that reports a thread stack's whole
    /// reservation rather than one committed region of it.
    /// </summary>
    internal static bool TryGetCommittedStackRegion(ulong stackPointer, out ulong bottom, out ulong top)
    {
        if (QueryVirtualMemoryInformation(-1, (void*)stackPointer, 0, out var allocation,
                (nuint)sizeof(StackMemoryRegionInformation), null) != 0 &&
            ((allocation.Flags & 1) == 0 || allocation.CommitSize == allocation.RegionSize) &&
            allocation.RegionSize != 0 && allocation.RegionSize <= ulong.MaxValue - allocation.AllocationBase &&
            stackPointer >= allocation.AllocationBase && stackPointer - allocation.AllocationBase < allocation.RegionSize)
        {
            bottom = allocation.AllocationBase;
            top = bottom + allocation.RegionSize;
            return true;
        }

        bottom = 0;
        top = 0;
        return false;
    }

    private static void EmitStackBound(byte* code, ref int offset, byte register, uint field, bool store)
    {
        EmitByte(code, ref offset, 0x65);
        EmitByte(code, ref offset, (byte)(0x48 | (register >= 8 ? 4 : 0)));
        EmitByte(code, ref offset, store ? (byte)0x89 : (byte)0x8B);
        EmitByte(code, ref offset, (byte)(0x04 | ((register & 7) << 3)));
        EmitByte(code, ref offset, 0x25);
        EmitUInt32(code, ref offset, field);
    }

    // Fiber switches replace the guest stack without passing through a guest-entry stub, so the
    // context-transfer stub keeps the Windows TEB bounds in sync from the target state in R11
    // (stack top at +160, bottom at +168).
    internal static void EmitFiberStackBounds(byte* code, ref int offset)
    {
        ReadOnlySpan<byte> loadTop = [0x4D, 0x8B, 0x93, 160, 0, 0, 0];     // mov r10, [r11+160]
        foreach (var value in loadTop) EmitByte(code, ref offset, value);
        EmitStackBound(code, ref offset, 10, 8, store: true);
        ReadOnlySpan<byte> loadBottom = [0x4D, 0x8B, 0x93, 168, 0, 0, 0];  // mov r10, [r11+168]
        foreach (var value in loadBottom) EmitByte(code, ref offset, value);
        EmitStackBound(code, ref offset, 10, 16, store: true);
    }

    // R10 points to the entry state. R11 is scratch; RAX remains unchanged.
    internal static void EmitHostStackBounds(byte* code, ref int offset, bool save)
    {
        for (byte field = 8; field <= 16; field += 8)
        {
            if (save) EmitStackBound(code, ref offset, 11, field, store: false);
            EmitByte(code, ref offset, 0x4D);
            EmitByte(code, ref offset, save ? (byte)0x89 : (byte)0x8B);
            EmitByte(code, ref offset, 0x5A);
            EmitByte(code, ref offset, field);
            if (!save) EmitStackBound(code, ref offset, 11, field, store: true);
        }
    }

    internal static void EmitGuestStackBounds(byte* code, ref int offset, ulong bottom, ulong top)
    {

        EmitHostStackBounds(code, ref offset, save: true);
        for (uint field = 8; field <= 16; field += 8)
        {
            EmitByte(code, ref offset, 0x49);
            EmitByte(code, ref offset, 0xBB);
            *(ulong*)(code + offset) = field == 8 ? top : bottom;
            offset += sizeof(ulong);
            EmitStackBound(code, ref offset, 11, field, store: true);
        }
    }

    internal static void EmitSavedStackBounds(byte* code, ref int offset, bool restore)
    {
        EmitStackBound(code, ref offset, 14, 8, restore);
        EmitStackBound(code, ref offset, 15, 16, restore);
    }

    internal static void EmitExceptionHostStackBounds(
        byte* code, ref int offset, uint hostRspSlotTlsIndex, nint tlsGetValueAddress)
    {
        EmitSavedStackBounds(code, ref offset, restore: false);
        ReadOnlySpan<byte> reserve = [0x48, 0x83, 0xEC, 0x28, 0xB9];
        foreach (var value in reserve) EmitByte(code, ref offset, value);
        EmitUInt32(code, ref offset, hostRspSlotTlsIndex);
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
        *(nint*)(code + offset) = tlsGetValueAddress;
        offset += sizeof(nint);
        ReadOnlySpan<byte> lookup = [0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x28, 0x48, 0x85, 0xC0, 0x0F, 0x84];
        foreach (var value in lookup) EmitByte(code, ref offset, value);
        var missingState = offset;
        EmitUInt32(code, ref offset, 0);
        // The state is zeroed at allocation; bounds the entry stub has not saved yet stay untouched.
        ReadOnlySpan<byte> unsaved = [0x48, 0x83, 0x78, 0x08, 0x00, 0x0F, 0x84];
        foreach (var value in unsaved) EmitByte(code, ref offset, value);
        var missingBounds = offset;
        EmitUInt32(code, ref offset, 0);
        EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xC2);
        // A host fault already has live bounds. Do not replace a grown limit with an old one.
        ReadOnlySpan<byte> sameStack = [0x4C, 0x3B, 0x70, 0x08, 0x0F, 0x85];
        foreach (var value in sameStack) EmitByte(code, ref offset, value);
        var guestStack = offset;
        EmitUInt32(code, ref offset, 0);
        EmitHostStackBounds(code, ref offset, save: true);
        EmitByte(code, ref offset, 0xE9);
        var hostStack = offset;
        EmitUInt32(code, ref offset, 0);
        *(int*)(code + guestStack) = offset - guestStack - sizeof(int);
        EmitHostStackBounds(code, ref offset, save: false);
        *(int*)(code + hostStack) = offset - hostStack - sizeof(int);
        *(int*)(code + missingState) = offset - missingState - sizeof(int);
        *(int*)(code + missingBounds) = offset - missingBounds - sizeof(int);
    }

    [UnmanagedCallersOnly]
    private static int SelectExceptionStackBounds(void* exceptionInfo, int disposition, ulong* bounds)
    {
        if (disposition != -1) return disposition;
        var context = ((DirectExecutionFaultFrame.Pointers*)exceptionInfo)->ContextRecord;
        if (context == null) return 0;
        var stackPointer = DirectExecutionFaultFrame.ReadRegister(context, 152);
        if (stackPointer >= bounds[1] && stackPointer < bounds[0]) return disposition;

        GetCurrentThreadStackLimits(out var nativeBottom, out var nativeTop);
        if (stackPointer >= nativeBottom && stackPointer < nativeTop)
        {
            bounds[0] = nativeTop;
            bounds[1] = nativeBottom;
            return disposition;
        }

        if (!TryGetStackBounds(stackPointer, out var bottom, out var top)) return 0;
        bounds[0] = top;
        bounds[1] = bottom;
        return disposition;
    }

    internal static void EmitExceptionStackSelection(byte* code, ref int offset)
    {
        // The caller reserves 72 bytes: ABI home space and two stack bounds.
        ReadOnlySpan<byte> arguments = [0x4C, 0x89, 0x74, 0x24, 0x28, 0x4C, 0x89, 0x7C, 0x24, 0x30,
            0x89, 0xC2, 0x4C, 0x89, 0xE9, 0x4C, 0x8D, 0x44, 0x24, 0x28, 0x48, 0xB8];
        foreach (var value in arguments) EmitByte(code, ref offset, value);
        *(nint*)(code + offset) = (nint)(delegate* unmanaged<void*, int, ulong*, int>)&SelectExceptionStackBounds;
        offset += sizeof(nint);
        ReadOnlySpan<byte> result = [0xFF, 0xD0, 0x4C, 0x8B, 0x74, 0x24, 0x28, 0x4C, 0x8B, 0x7C, 0x24, 0x30];
        foreach (var value in result) EmitByte(code, ref offset, value);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void GetCurrentThreadStackLimits(out ulong lowLimit, out ulong highLimit);

    [StructLayout(LayoutKind.Sequential)]
    private struct StackMemoryRegionInformation
    {
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Flags;
        public ulong RegionSize;
        public ulong CommitSize;
    }

    [DllImport("api-ms-win-core-memory-l1-1-4.dll", ExactSpelling = true)]
    private static extern int QueryVirtualMemoryInformation(nint process, void* address, int informationClass,
        out StackMemoryRegionInformation information, nuint informationSize, nuint* returnSize);
}
