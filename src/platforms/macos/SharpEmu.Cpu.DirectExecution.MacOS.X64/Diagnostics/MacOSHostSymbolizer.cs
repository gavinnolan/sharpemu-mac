// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native.Platform;

namespace SharpEmu.Cpu.DirectExecution.MacOS.X64;

/// <summary>
/// Resolves host addresses inside dyld-loaded images (system libraries, the GPU
/// driver, the runtime) with dladdr. Guest modules are not dyld images, so they
/// never resolve here.
/// </summary>
internal static unsafe class MacOSHostSymbolizer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public byte* FileName;
        public void* BaseAddress;
        public byte* SymbolName;
        public void* SymbolAddress;
    }

    [DllImport("libSystem.B.dylib")]
    private static extern int dladdr(void* addr, DlInfo* info);

    internal static bool TryResolve(ulong address, out HostImageSymbol symbol)
    {
        symbol = default;
        try
        {
            DlInfo info = default;
            if (dladdr((void*)address, &info) == 0 || info.FileName == null)
            {
                return false;
            }

            string image = Marshal.PtrToStringUTF8((IntPtr)info.FileName) ?? string.Empty;
            string? name = info.SymbolName != null ? Marshal.PtrToStringUTF8((IntPtr)info.SymbolName) : null;
            symbol = new HostImageSymbol(image, name, (ulong)info.SymbolAddress, (ulong)info.BaseAddress);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
