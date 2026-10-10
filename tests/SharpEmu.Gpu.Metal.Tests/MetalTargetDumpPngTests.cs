// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The render-target dumper writes its own PNGs, so the file has to be one a decoder
// reads back unchanged: a dump nobody can open is worse than no dump at all.
public sealed class MetalTargetDumpPngTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(7u, 3u)]
    [InlineData(64u, 48u)]
    public void AnEncodedTargetDumpDecodesBackToItsPixels(uint width, uint height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)((i * 37) + (i / 4));
        }

        var png = MetalRenderHost.EncodePng(rgba, width, height);
        Assert.Equal(Signature, png[..8]);

        var chunks = ReadChunks(png);
        var header = Assert.Single(chunks.Where(chunk => chunk.Type == "IHDR")).Data;
        Assert.Equal(width, BinaryPrimitives.ReadUInt32BigEndian(header));
        Assert.Equal(height, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
        Assert.Equal(8, header[8]);   // bit depth
        Assert.Equal(6, header[9]);   // truecolour with alpha
        Assert.Equal(0, header[10]);  // deflate
        Assert.Equal(0, header[11]);  // adaptive filtering
        Assert.Equal(0, header[12]);  // no interlace
        Assert.Contains(chunks, chunk => chunk.Type == "IEND");

        var idat = Assert.Single(chunks.Where(chunk => chunk.Type == "IDAT")).Data;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        var stride = (int)(width * 4) + 1;
        Assert.Equal(stride * (int)height, raw.Length);
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * stride]); // filter type: none
            Assert.Equal(
                rgba.AsSpan(y * (int)width * 4, (int)width * 4).ToArray(),
                raw.AsSpan((y * stride) + 1, (int)width * 4).ToArray());
        }
    }

    // The depth attachment is what says whether a pass rasterised anything at all, so
    // the dumper has to read the depth formats a render pass actually uses - including
    // the combined one, whose stencil half the blit leaves behind.
    [Theory]
    [InlineData(252ul)]  // MTLPixelFormatDepth32Float
    [InlineData(260ul)]  // MTLPixelFormatDepth32Float_Stencil8
    public void ADepthTargetDumpsAsGrey(ulong pixelFormat)
    {
        Assert.True(MetalRenderHost.TryDescribeDumpFormat(pixelFormat, out var sourceBytes, out var convert));
        Assert.Equal(4, sourceBytes);

        var source = new byte[3 * sizeof(float)];
        BitConverter.TryWriteBytes(source.AsSpan(0), 0f);
        BitConverter.TryWriteBytes(source.AsSpan(4), 0.5f);
        BitConverter.TryWriteBytes(source.AsSpan(8), 1f);
        var rgba = new byte[3 * 4];
        convert(source, rgba);

        Assert.Equal<byte[]>([0, 0, 0, 255], rgba[..4]);
        Assert.Equal<byte[]>([127, 127, 127, 255], rgba[4..8]);
        Assert.Equal<byte[]>([255, 255, 255, 255], rgba[8..]);
    }

    // A colour format the dumper cannot read has to say so rather than write a PNG of
    // reinterpreted bytes; the caller warns once and skips the target.
    [Fact]
    public void ACompressedTargetIsNotDumped() =>
        Assert.False(MetalRenderHost.TryDescribeDumpFormat(131ul, out _, out _));

    // Every chunk's CRC has to be right, or a strict decoder rejects the whole file.
    private static List<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length).ToArray();
            var stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(Crc32(png.AsSpan(offset + 4, 4 + length)), stored);
            chunks.Add((type, data));
            offset += 12 + length;
        }

        Assert.Equal(png.Length, offset);
        return chunks;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xFFFFFFFFu;
    }
}
