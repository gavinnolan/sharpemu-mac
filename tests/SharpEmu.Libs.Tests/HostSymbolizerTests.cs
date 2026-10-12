// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HostSymbolizerTests
{
	[Fact]
	public void FormatsSymbolWithImageBasenameAndOffset()
	{
		Assert.Equal(
			"AGXMetalG16G_B0!foo+0x10",
			HostSymbolizer.Format("/System/Library/Ext/AGXMetalG16G_B0", "foo", 0x1010, 0x1000, 0x800));
	}

	[Fact]
	public void FormatsImageRelativeOffsetWithoutSymbol()
	{
		Assert.Equal("libfoo.dylib+0x300", HostSymbolizer.Format("/usr/lib/libfoo.dylib", null, 0x1100, 0, 0xE00));
	}
}
