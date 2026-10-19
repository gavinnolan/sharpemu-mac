// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

internal sealed partial class MetalImageCache
{
    // SHARPEMU_METAL_V2_DCC_TRACE=1 logs what each DCC slice scan decided, up to
    // SHARPEMU_METAL_V2_DCC_TRACE_MAX lines (default 600).
    private static readonly bool DccTraceEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DCC_TRACE") == "1";

    private static readonly int DccTraceMax =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DCC_TRACE_MAX"), out var max) ? max : 600;

    // SHARPEMU_METAL_V2_DCC_TRACE_ADDR=<hex> keeps only the slices at that metadata address.
    private static readonly ulong DccTraceAddress =
        ulong.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DCC_TRACE_ADDR")?.Replace("0x", string.Empty),
            System.Globalization.NumberStyles.HexNumber, null, out var traced) ? traced : 0;

    private int _dccTraceLines;

    // SHARPEMU_METAL_V2_DCC_TRACE_AFTER_SECONDS=<n> starts the trace n seconds after the cache loads.
    private static readonly double DccTraceAfterSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DCC_TRACE_AFTER_SECONDS"), out var after) ? after : 0;

    private static readonly System.Diagnostics.Stopwatch DccTraceClock = System.Diagnostics.Stopwatch.StartNew();

    private static bool DccTraceActive => DccTraceEnabled && DccTraceClock.Elapsed.TotalSeconds >= DccTraceAfterSeconds;

    private static bool DccTraceCovers(ulong address, ulong size) =>
        DccTraceAddress == 0 || (DccTraceAddress >= address && DccTraceAddress - address < Math.Max(size, 1));

    private void TraceDcc(ResourceSlotIdentifier image, in ImageRequest request, ulong address, ulong size, string what)
    {
        if (!DccTraceActive || !DccTraceCovers(address, size) || Interlocked.Increment(ref _dccTraceLines) > DccTraceMax)
        {
            return;
        }

        var d = request.Description;
        Console.Error.WriteLine(
            $"[METAL-V2][DCC] image={image} role={request.Role} fmt={request.View.Format} " +
            $"{d.Extent.Width}x{d.Extent.Height} at=0x{d.Data.Address:X}+0x{d.Data.Size:X} meta=0x{address:X}+0x{size:X} {what}");
    }

    // The first byte that differs from the code, and how many bytes of the slice hold it.
    private string DescribeDccSlice(ulong address, ulong size, byte code)
    {
        var bytes = new byte[size];
        if (!_backing.TryReadBacking(address, bytes))
        {
            return "unreadable";
        }

        var counts = new int[256];
        foreach (var value in bytes)
        {
            counts[value]++;
        }

        var mismatch = bytes.AsSpan().IndexOfAnyExcept(code);
        var lastCode = bytes.AsSpan().LastIndexOf(code);
        var top = Enumerable.Range(0, 256).Where(v => counts[v] != 0).OrderByDescending(v => counts[v]).Take(6)
            .Select(v => $"{v:X2}x{counts[v]}");
        return $"first_mismatch=0x{(mismatch < 0 ? 0 : mismatch):X} last_code=0x{lastCode:X} values=[{string.Join(',', top)}]";
    }

    // Whether TraceDccWriter could print for this range, so a caller builds its text only then.
    public static bool TracesDccWriter(ulong address, ulong size) =>
        DccTraceActive && DccTraceAddress != 0 && DccTraceCovers(address, size);

    public void TraceDccWriter(ulong address, ulong size, string what)
    {
        if (!DccTraceActive || DccTraceAddress == 0 || !DccTraceCovers(address, size) ||
            Interlocked.Increment(ref _dccTraceLines) > DccTraceMax)
        {
            return;
        }

        Console.Error.WriteLine($"[METAL-V2][DCC] writer range=0x{address:X}+0x{size:X} {what}");
    }
}
