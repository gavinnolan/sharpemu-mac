// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalV2DeviceAddressTableTests
{
    // The table takes as many runs as the shader's search covers; the next one ends the
    // run loudly rather than leaving a guest span that reads zero and drops its stores.
    [Fact]
    public void AFullDeviceAddressTableIsFatal()
    {
        var previous = GpuFatal.OnFatal;
        var reports = new List<string>();
        GpuFatal.OnFatal = reports.Add;
        try
        {
            var entries = new List<(ulong FirstPage, ulong PageCount, ulong Base)>();
            for (var page = 0UL; page < Gen5MslTranslator.MaxAddressRangeCount; page++)
            {
                MetalRenderHost.AddDeviceAddressEntry(entries, (page * 2, 1, 0x1000 + page), spanCount: (int)page);
            }

            Assert.Equal((int)Gen5MslTranslator.MaxAddressRangeCount, entries.Count);
            Assert.Empty(reports);

            var fatal = Assert.Throws<GpuFatalException>(() =>
                MetalRenderHost.AddDeviceAddressEntry(entries, (0x10_0000, 1, 0x2000), spanCount: 7));

            Assert.Contains("device-address table is full", fatal.Message, StringComparison.Ordinal);
            Assert.Contains($"limit={Gen5MslTranslator.MaxAddressRangeCount}", fatal.Message, StringComparison.Ordinal);
            Assert.Single(reports);
            Assert.Equal((int)Gen5MslTranslator.MaxAddressRangeCount, entries.Count);
        }
        finally
        {
            GpuFatal.OnFatal = previous;
        }
    }
}
