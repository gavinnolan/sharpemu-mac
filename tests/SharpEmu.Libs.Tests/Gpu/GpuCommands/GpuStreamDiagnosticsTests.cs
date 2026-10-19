// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuStreamDiagnosticsTests
{
    [Fact]
    public void Hash_IsDeterministicAndSensitiveToEachDword()
    {
        var dwords = new uint[] { 0xC0004600u, 1, 2, 3, 4 };

        var first = GpuStreamDiagnostics.Hash(dwords);

        Assert.Equal(first, GpuStreamDiagnostics.Hash(dwords.AsSpan().ToArray()));
        for (var index = 0; index < dwords.Length; index++)
        {
            var changed = (uint[])dwords.Clone();
            changed[index] ^= 0x100u;
            Assert.NotEqual(first, GpuStreamDiagnostics.Hash(changed));
        }
    }
}
