// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace SharpEmu.Gpu.Metal.V2;

// SHARPEMU_METAL_V2_DUMP_TARGETS=<dir>: writes the colour target of every render pass,
// and the texture every flip presents, as a PNG. A title that presents black frames has
// either drawn nothing, drawn somewhere else, or drawn something the flip never reaches,
// and those look identical from outside: the only way to tell is to look at the pixels
// at each stage.
//
// It reads back through a blit into a shared buffer and waits for the GPU, so it is as
// intrusive as the shader profiler and equally opt-in.
internal sealed partial class MetalRenderHost
{
    private static readonly string? _dumpTargetsDirectory =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DUMP_TARGETS") is { Length: > 0 } value
            ? value
            : null;

    // Enough to see a title screen appear and settle without filling a disk.
    private const int MaxTargetDumps = 240;
    private const int MaxDumpsPerTexture = 3;

    private int _targetDumps;
    private readonly Dictionary<nint, int> _dumpsByTexture = new();
    private bool _dumpDirectoryReady;

    private static bool DumpTargetsEnabled => _dumpTargetsDirectory is not null;

    private static readonly nint SelDumpCopyTextureToBuffer =
        MetalNative.Selector("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");

    private static readonly nint SelPixelFormat = MetalNative.Selector("pixelFormat");
    private static readonly nint SelWidth = MetalNative.Selector("width");
    private static readonly nint SelHeight = MetalNative.Selector("height");

    /// <summary>Dumps a texture, taking its shape from the texture itself.</summary>
    private void DumpTexture(nint texture, string label)
    {
        if (!DumpTargetsEnabled || texture == 0)
        {
            return;
        }

        DumpTexture(
            texture,
            (uint)MetalV2Native.SendValue(texture, SelWidth),
            (uint)MetalV2Native.SendValue(texture, SelHeight),
            MetalV2Native.SendValue(texture, SelPixelFormat),
            label);
    }

    /// <summary>
    /// Reads a colour texture back and writes it as a PNG. Flushes and waits, so it only
    /// ever runs under the dump env var.
    /// </summary>
    private void DumpTexture(nint texture, uint width, uint height, ulong pixelFormat, string label)
    {
        if (!DumpTargetsEnabled || texture == 0 || width == 0 || height == 0 || _targetDumps >= MaxTargetDumps)
        {
            return;
        }

        _dumpsByTexture.TryGetValue(texture, out var seen);
        if (seen >= MaxDumpsPerTexture)
        {
            return;
        }

        if (!TryDescribeDumpFormat(pixelFormat, out var sourceBytes, out var convert))
        {
            MetalV2Diagnostics.WarnOnce(
                $"dump-format-{pixelFormat}",
                $"A render target cannot be dumped because its format is not one the dumper reads: " +
                $"format={pixelFormat} {width}x{height}.");
            return;
        }

        try
        {
            var rowBytes = (ulong)sourceBytes * width;
            var total = rowBytes * height;
            var readback = _guestMemory.CreateReportBuffer(total, "SharpEmu TargetDump", out var contents, out _);
            try
            {
                EndEncoders();
                var blit = MetalNative.Send(EnsureCommandBuffer(), SelBlitEncoder);
                if (blit == 0)
                {
                    return;
                }

                MetalNative.SendCopyTextureToBufferOptions(
                    blit, SelDumpCopyTextureToBuffer, texture, 0, 0, default,
                    new MtlSize { Width = width, Height = height, Depth = 1 },
                    readback, 0, (nuint)rowBytes, (nuint)total, (nuint)MtlBlitOption.None);
                MetalNative.SendVoid(blit, SelEndEncoding);
                FlushAndWaitForGpu();

                var source = DumpSpan(contents, (int)total);
                var rgba = new byte[(int)(4UL * width * height)];
                convert(source, rgba);
                _dumpsByTexture[texture] = seen + 1;
                _targetDumps++;
                WriteDumpPng(rgba, width, height, label, seen);
            }
            finally
            {
                MetalNative.SendVoid(readback, MetalNative.Selector("release"));
            }
        }
        catch (Exception exception)
        {
            MetalV2Diagnostics.WarnOnce(
                "dump-failed",
                $"A render target could not be dumped: {exception.Message}");
        }
    }

    private static unsafe ReadOnlySpan<byte> DumpSpan(nint contents, int length) =>
        new((void*)contents, length);

    private void WriteDumpPng(byte[] rgba, uint width, uint height, string label, int index)
    {
        if (!_dumpDirectoryReady)
        {
            Directory.CreateDirectory(_dumpTargetsDirectory!);
            _dumpDirectoryReady = true;
        }

        var safe = new string(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        var name = string.Format(
            CultureInfo.InvariantCulture, "{0:D4}-{1}-{2}x{3}-{4}.png", _targetDumps, safe, width, height, index);
        var path = Path.Combine(_dumpTargetsDirectory!, name);
        File.WriteAllBytes(path, EncodePng(rgba, width, height));

        // Says whether the pixels are actually black, which is the whole question.
        ulong sum = 0;
        var opaque = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            sum += (ulong)(rgba[i] + rgba[i + 1] + rgba[i + 2]);
            if (rgba[i + 3] != 0)
            {
                opaque++;
            }
        }

        Console.Error.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[METAL-V2][DUMP] {0} {1}x{2} mean_rgb={3:F2} opaque={4}/{5} -> {6}",
            label, width, height, sum / (double)(rgba.Length / 4 * 3), opaque, rgba.Length / 4, name));
    }

    private static bool TryDescribeDumpFormat(
        ulong pixelFormat, out int sourceBytes, out Action<ReadOnlySpan<byte>, byte[]> convert)
    {
        switch (pixelFormat)
        {
            case MtlFormat.Rgba8Unorm:
            case MtlFormat.Rgba8UnormSrgb:
                sourceBytes = 4;
                convert = static (source, target) => source.CopyTo(target);
                return true;
            case MtlFormat.Bgra8Unorm:
            case MtlFormat.Bgra8UnormSrgb:
                sourceBytes = 4;
                convert = static (source, target) =>
                {
                    for (var i = 0; i + 3 < source.Length; i += 4)
                    {
                        target[i] = source[i + 2];
                        target[i + 1] = source[i + 1];
                        target[i + 2] = source[i];
                        target[i + 3] = source[i + 3];
                    }
                };
                return true;
            case MtlFormat.Rgb10A2Unorm:
            case MtlFormat.Bgr10A2Unorm:
                sourceBytes = 4;
                var blueFirst = pixelFormat == MtlFormat.Bgr10A2Unorm;
                convert = (source, target) =>
                {
                    for (var i = 0; i + 3 < source.Length; i += 4)
                    {
                        var packed = BinaryPrimitives.ReadUInt32LittleEndian(source[i..]);
                        var c0 = (byte)(((packed >> 0) & 0x3FF) >> 2);
                        var c1 = (byte)(((packed >> 10) & 0x3FF) >> 2);
                        var c2 = (byte)(((packed >> 20) & 0x3FF) >> 2);
                        target[i] = blueFirst ? c2 : c0;
                        target[i + 1] = c1;
                        target[i + 2] = blueFirst ? c0 : c2;
                        target[i + 3] = (byte)(((packed >> 30) & 3) * 85);
                    }
                };
                return true;
            case MtlFormat.Rgba16Float:
                sourceBytes = 8;
                convert = static (source, target) =>
                {
                    for (int i = 0, o = 0; i + 7 < source.Length; i += 8, o += 4)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            var half = (Half)BitConverter.UInt16BitsToHalf(
                                BinaryPrimitives.ReadUInt16LittleEndian(source[(i + (c * 2))..]));
                            var v = (float)half;
                            target[o + c] = (byte)Math.Clamp(v * 255f, 0f, 255f);
                        }
                    }
                };
                return true;
            case MtlFormat.Rgba16Unorm:
                sourceBytes = 8;
                convert = static (source, target) =>
                {
                    for (int i = 0, o = 0; i + 7 < source.Length; i += 8, o += 4)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            target[o + c] = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(
                                source[(i + (c * 2))..]) >> 8);
                        }
                    }
                };
                return true;
            default:
                sourceBytes = 0;
                convert = static (_, _) => { };
                return false;
        }
    }

    // A minimal PNG: one IDAT of zlib-deflated scanlines, each with filter byte zero.
    internal static byte[] EncodePng(byte[] rgba, uint width, uint height)
    {
        var raw = new byte[(int)((4UL * width + 1) * height)];
        for (var y = 0; y < height; y++)
        {
            var source = (int)(4UL * width * (ulong)y);
            var target = (int)((4UL * width + 1) * (ulong)y);
            raw[target] = 0;
            Array.Copy(rgba, source, raw, target + 1, (int)(4 * width));
        }

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // truecolour with alpha
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", deflated.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] _crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (var b in data)
        {
            c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
