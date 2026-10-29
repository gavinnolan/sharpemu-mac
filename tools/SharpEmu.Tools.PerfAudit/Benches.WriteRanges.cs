// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;

namespace SharpEmu.PerfAudit;

/// <summary>8w: MetalGpuWriteRanges.PendingSerial with 512 live records inside one 2 MiB bucket (the bucket gate passes).</summary>
internal static class WriteRangeBenches
{
    public static void Run()
    {
        const ulong Base = 0x2_0000_0000;
        var ranges = new MetalGpuWriteRanges();
        for (var i = 0UL; i < 512; i++)
        {
            ranges.Note(Base + i * 4096, 256, 10 + i / 64);
        }

        Harness.Measure("8w1", "PendingSerial miss, 512 records in the read's 2 MiB bucket", 1, 20000,
            i => ranges.PendingSerial(Base + 1024 + (ulong)(i & 511) * 4096, 4, 0), unit: "read");
        Harness.Measure("8w2", "PendingSerial hit, 512 records in the read's 2 MiB bucket", 1, 20000,
            i => ranges.PendingSerial(Base + 8 + (ulong)(i & 511) * 4096, 4, 0), unit: "read");
        Harness.Measure("8w3", "reference: unindexed walk of the same 512 records (miss)", 1, 20000,
            i => ranges.UnindexedPendingSerial(Base + 1024 + (ulong)(i & 511) * 4096, 4, 0), unit: "read");
    }
}
