// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using SharpEmu.HLE;
using SharpEmu.Libs.ImageCodec;
using SharpEmu.Platform.MacOS.Imaging;
using Xunit;

namespace SharpEmu.Libs.Tests.ImageCodec;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageCodecStateCollection
{
    public const string Name = "ImageCodecState";
}

/// <summary>
/// A guest with room for parameter structs, encoded files and images, plus helpers to call
/// the four image libraries. The host codec registry is global; each test starts from
/// "nothing registered" and the module initializer's registration is restored afterwards.
/// </summary>
public abstract class ImageCodecTestBase : IDisposable
{
    protected const ulong MemoryBase = 0x1_0000_0000;
    protected const int MemorySize = 0x10_0000;

    protected const ulong ParamAddress = MemoryBase + 0x100;
    protected const ulong InfoAddress = MemoryBase + 0x200;
    protected const ulong HandleAddress = MemoryBase + 0x300;
    protected const ulong WorkAddress = MemoryBase + 0x1000;
    protected const ulong SecondWorkAddress = MemoryBase + 0x2000;
    protected const ulong FileAddress = MemoryBase + 0x1_0000;
    protected const ulong ImageAddress = MemoryBase + 0x8_0000;

    private protected readonly FakeCpuMemory Memory = new(MemoryBase, MemorySize);
    protected readonly CpuContext Ctx;

    protected ImageCodecTestBase()
    {
        HostImageCodec.ResetForTests();
        Ctx = new CpuContext(Memory, Generation.Gen5);
    }

    public void Dispose()
    {
        HostImageCodec.ResetForTests();
        if (OperatingSystem.IsMacOS())
        {
            HostImageCodec.RegisterProvider("ImageIO", MacImageIOCodec.Instance);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Registers the ImageIO codec; false (test becomes a no-op) on other hosts.</summary>
    protected static bool UseMacCodec()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        HostImageCodec.RegisterProvider("ImageIO", MacImageIOCodec.Instance);
        return true;
    }

    protected int Call(Func<CpuContext, int> export, ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0)
    {
        Ctx[CpuRegister.Rdi] = rdi;
        Ctx[CpuRegister.Rsi] = rsi;
        Ctx[CpuRegister.Rdx] = rdx;
        Ctx[CpuRegister.Rcx] = rcx;
        var result = export(Ctx);
        Assert.Equal(unchecked((ulong)result), Ctx[CpuRegister.Rax]);
        return result;
    }

    protected void Write(ulong address, ReadOnlySpan<byte> bytes) => Assert.True(Memory.TryWrite(address, bytes));

    protected byte[] Read(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(Memory.TryRead(address, bytes));
        return bytes;
    }

    protected uint ReadU32(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4));

    protected ushort ReadU16(ulong address) => BinaryPrimitives.ReadUInt16LittleEndian(Read(address, 2));

    protected ulong ReadU64(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(Read(address, 8));

    /// <summary>A create param of the {thisSize, attribute, maxImageWidth[, maxFilterNumber]} shape.</summary>
    protected void WriteCreateParam(params uint[] fields)
    {
        var bytes = new byte[fields.Length * 4];
        for (var index = 0; index < fields.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), fields[index]);
        }

        Write(ParamAddress, bytes);
    }

    protected void WritePngDecodeParam(
        ulong png, ulong image, uint pngSize, uint imageSize, ushort pixelFormat, ushort alpha, uint pitch)
    {
        var bytes = new byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, png);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x08), image);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), pngSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), imageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x18), pixelFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x1A), alpha);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C), pitch);
        Write(ParamAddress, bytes);
    }

    protected void WriteParseParam(ulong file, uint size, ushort decodeMode = 0, ushort downScale = 1)
    {
        var bytes = new byte[0x10];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, file);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x08), size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x0C), decodeMode);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x0E), downScale);
        Write(ParamAddress, bytes);
    }

    protected void WriteJpegDecodeParam(
        ulong jpeg, ulong image, uint jpegSize, uint imageSize, ushort pixelFormat, ushort alpha, uint pitch,
        ushort decodeMode = 0, ushort downScale = 1)
    {
        var bytes = new byte[0x30];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, jpeg);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x08), image);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), jpegSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C), imageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x24), decodeMode);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x26), downScale);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x28), pixelFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x2A), alpha);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C), pitch);
        Write(ParamAddress, bytes);
    }

    protected void WritePngEncodeParam(
        ulong image, ulong png, uint imageSize, uint pngSize, uint width, uint height, uint pitch,
        ushort pixelFormat, ushort colorSpace, ushort bitDepth = 8, ushort clut = 0, ushort filter = 0,
        ushort compression = 6)
    {
        var bytes = new byte[0x30];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, image);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x08), png);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), imageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), pngSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C), height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x20), pitch);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x24), pixelFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x26), colorSpace);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x28), bitDepth);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x2A), clut);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x2C), filter);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x2E), compression);
        Write(ParamAddress, bytes);
    }

    protected void WriteJpegEncodeParam(
        ulong image, ulong jpeg, uint imageSize, uint jpegSize, uint width, uint height, uint pitch,
        ushort pixelFormat, ushort encodeMode, ushort colorSpace, byte sampling, byte ratio, int restart = 0)
    {
        var bytes = new byte[0x30];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, image);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x08), jpeg);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), imageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), jpegSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C), height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x20), pitch);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x24), pixelFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x26), encodeMode);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x28), colorSpace);
        bytes[0x2A] = sampling;
        bytes[0x2B] = ratio;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x2C), restart);
        Write(ParamAddress, bytes);
    }

    /// <summary>Creates a decoder/encoder in <paramref name="work"/> and returns its handle.</summary>
    protected ulong Create(Func<CpuContext, int> create, ulong work, uint workSize)
    {
        Assert.Equal(0, Call(create, ParamAddress, work, workSize, HandleAddress));
        return ReadU64(HandleAddress);
    }
}

/// <summary>Builds minimal PNG files (no ancillary chunks except tRNS/PLTE) for the decoders.</summary>
internal static class TestPng
{
    public static byte[] Build(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte[] rawRows,
        bool interlaced = false,
        byte[]? palette = null,
        byte[]? transparency = null,
        bool includeImageData = true)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[12] = interlaced ? (byte)1 : (byte)0;
        WriteChunk(stream, "IHDR", ihdr);

        if (palette is not null)
        {
            WriteChunk(stream, "PLTE", palette);
        }

        if (transparency is not null)
        {
            WriteChunk(stream, "tRNS", transparency);
        }

        if (includeImageData)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(rawRows);
            }

            WriteChunk(stream, "IDAT", compressed.ToArray());
        }

        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    /// <summary>Prefixes every row with filter type 0.</summary>
    public static byte[] Unfiltered(byte[] packed, int rowBytes)
    {
        var rows = packed.Length / rowBytes;
        var output = new byte[rows * (rowBytes + 1)];
        for (var row = 0; row < rows; row++)
        {
            packed.AsSpan(row * rowBytes, rowBytes).CopyTo(output.AsSpan(row * (rowBytes + 1) + 1));
        }

        return output;
    }

    private static void WriteChunk(Stream stream, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        stream.Write(length);
        var typeAndBody = new byte[4 + body.Length];
        for (var index = 0; index < 4; index++)
        {
            typeAndBody[index] = (byte)type[index];
        }

        body.CopyTo(typeAndBody, 4);
        stream.Write(typeAndBody);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndBody));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

/// <summary>JPEG headers only: SOI, an APP0 segment and a frame header (enough for ParseHeader).</summary>
internal static class TestJpeg
{
    public static byte[] Header(ushort width, ushort height, byte marker, params (byte Id, byte Sampling)[] components)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        // APP0 with a 2-byte body, to check segments before the frame header are skipped.
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00]);

        var length = 8 + components.Length * 3;
        bytes.AddRange([0xFF, marker, (byte)(length >> 8), (byte)length, 8]);
        bytes.AddRange([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components.Length]);
        foreach (var (id, sampling) in components)
        {
            bytes.AddRange([id, sampling, 0]);
        }

        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }
}
