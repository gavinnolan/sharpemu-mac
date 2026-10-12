// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Symbolizes host addresses that live inside dyld-loaded images (system
/// libraries, the GPU driver, the runtime) using dladdr. Guest modules are
/// not dyld images, so they never resolve here.
/// </summary>
internal static unsafe class HostSymbolizer
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

	public static bool TryFormat(ulong address, out string text)
	{
		text = string.Empty;
		if (!OperatingSystem.IsMacOS())
		{
			return false;
		}

		try
		{
			DlInfo info = default;
			if (dladdr((void*)address, &info) == 0 || info.FileName == null)
			{
				return false;
			}

			string image = Marshal.PtrToStringUTF8((IntPtr)info.FileName) ?? string.Empty;
			string? symbol = info.SymbolName != null ? Marshal.PtrToStringUTF8((IntPtr)info.SymbolName) : null;
			text = Format(
				image,
				symbol,
				address,
				(ulong)info.SymbolAddress,
				(ulong)info.BaseAddress);
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static string Format(string imagePath, string? symbol, ulong address, ulong symbolAddress, ulong imageBase)
	{
		int slash = imagePath.LastIndexOf('/');
		string image = slash >= 0 ? imagePath[(slash + 1)..] : imagePath;
		if (!string.IsNullOrEmpty(symbol) && symbolAddress != 0 && address >= symbolAddress)
		{
			return $"{image}!{symbol}+0x{address - symbolAddress:X}";
		}

		return $"{image}+0x{address - imageBase:X}";
	}
}
