// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Platform;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Symbolizes host addresses that live inside dyld-loaded images (system
/// libraries, the GPU driver, the runtime) using dladdr. Guest modules are
/// not dyld images, so they never resolve here.
/// </summary>
internal static class HostSymbolizer
{
	public static bool TryFormat(ulong address, out string text)
	{
		text = string.Empty;
		if (!DirectExecutionPlatform.IsRegistered)
		{
			return false;
		}

		try
		{
			if (!DirectExecutionPlatform.Current.TryResolveHostImageSymbol(address, out HostImageSymbol symbol))
			{
				return false;
			}

			text = Format(symbol.ImagePath, symbol.SymbolName, address, symbol.SymbolAddress, symbol.ImageBase);
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
