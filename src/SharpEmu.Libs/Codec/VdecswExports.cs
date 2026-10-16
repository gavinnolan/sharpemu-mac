// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// libSceVdecsw (software-assisted video decoder, asynchronous set/sync API).
/// H.264 only, decoded by the FFmpeg session behind <see cref="IVdecswCodec"/>.
/// Decoding is synchronous inside the set/sync calls: an access unit is decoded
/// when it is submitted (or as soon as the picture queue has room), so a "Sync"
/// call can never be satisfied later than the matching "TrySync" call; the
/// blocking Sync exports therefore behave like their Try variants and report
/// the pending/empty status instead of blocking forever. When FFmpeg cannot be
/// loaded, sceVdecswCreateDecoder fails with API_FAIL rather than faking a
/// decoder. Other codec types are rejected with CODEC_TYPE.
///
/// Output pictures are NV12 (luma plane then interleaved CbCr, both with the
/// 256-byte aligned pitch reported in OutputInfo) at the stream's cropped size;
/// the picture info therefore reports no frame cropping. The newest decoded
/// picture is held back until a successor (or sceVdecswFinalizeDecodeSequence)
/// shows whether it is the last one.
///
/// ABI (all structures start with u64 thisSize):
///   DecoderConfigInfo (0x50): +0x0C u32 codecType (1 = AVC), +0x18 s32
///     maxFrameWidth, +0x1C s32 maxFrameHeight.
///   DecoderMemoryInfo (0x48): +0x08 cpuMemorySize, +0x10 cpuMemory, +0x18
///     gpuMemorySize, +0x20 gpuMemory, +0x28 cpuGpuMemorySize, +0x30
///     cpuGpuMemory, +0x38 maxFrameBufferSize, +0x40 u32 frameBufferAlignment.
///   InputData (0x30): +0x08 auData, +0x10 auSize, +0x18 pts, +0x20 dts,
///     +0x28 attachedData.   InputResult (0x18): +0x08 decodedAu, +0x10 u32
///     outputFrameCount, +0x14 u32 reserved.   FrameBuffer (0x18): +0x08 ptr,
///     +0x10 size.
///   OutputInfo (0x38): +0x08 u8 isValid, +0x09 isLastFrame, +0x0A isErrorFrame,
///     +0x0B pictureCount, +0x0C u32 codecType, +0x10 frameWidth, +0x14
///     framePitch, +0x18 frameHeight, +0x1C u8 isDiscardedFrame, +0x20
///     frameBuffer, +0x28 frameBufferSize, +0x30 u32 frameFormat (0 = NV12),
///     +0x34 u32 framePitchInBytes.
///   AvcPictureInfo (0x78): +0x08 u8 isValid, +0x10 pts, +0x18 dts, +0x20
///     attachedData, +0x28 idrPictureFlag, +0x29 profileIdc, +0x2A levelIdc,
///     +0x2C picWidthInMbsMinus1, +0x30 picHeightInMapUnitsMinus1, +0x34
///     frameMbsOnlyFlag, +0x35 frameCroppingFlag, +0x38/0x3C/0x40/0x44 crop
///     left/right/top/bottom, +0x48 vui[44].
/// </summary>
public static class VdecswExports
{
    // Verified against titles' asserts (the codes each call is allowed to return).
    internal const int ErrorOutputPending = unchecked((int)0x81510115);
    internal const int ErrorInputQueueEmpty = unchecked((int)0x81510116);
    internal const int ErrorDecodePending = unchecked((int)0x81510117);

    // The remaining codes mirror the sibling libSceVideodec2 numbering under this
    // library's 0x8151 prefix; they are inferred, not measured.
    internal const int ErrorApiFail = unchecked((int)0x81510100);
    internal const int ErrorStructSize = unchecked((int)0x81510101);
    internal const int ErrorArgumentPointer = unchecked((int)0x81510102);
    internal const int ErrorDecoderInstance = unchecked((int)0x81510103);
    internal const int ErrorMemorySize = unchecked((int)0x81510104);
    internal const int ErrorMemoryPointer = unchecked((int)0x81510105);
    internal const int ErrorFrameBufferSize = unchecked((int)0x81510106);
    internal const int ErrorFrameBufferPointer = unchecked((int)0x81510107);
    internal const int ErrorAccessUnitSize = unchecked((int)0x8151010D);
    internal const int ErrorAccessUnitPointer = unchecked((int)0x8151010E);
    internal const int ErrorOutputInfo = unchecked((int)0x8151010F);
    internal const int ErrorComputeQueue = unchecked((int)0x81510110);
    internal const int ErrorConfigInfo = unchecked((int)0x81510200);
    internal const int ErrorCodecType = unchecked((int)0x81510204);
    internal const int ErrorFrameWidthHeight = unchecked((int)0x8151020A);

    internal const uint CodecTypeAvc = 1;
    internal const ulong MinimumMemoryBytes = 16UL * 1024 * 1024;
    internal const uint FrameBufferAlignment = 0x100;
    internal const int MaxQueuedPictures = 4;

    private const int ComputeMemoryInfoSize = 0x18;
    private const int ComputeConfigInfoSize = 0x10;
    private const int DecoderConfigInfoSize = 0x50;
    private const int DecoderMemoryInfoSize = 0x48;
    private const int InputDataSize = 0x30;
    private const int InputResultSize = 0x18;
    private const int FrameBufferSize = 0x18;
    private const int OutputInfoSize = 0x38;
    private const int AvcPictureInfoSize = 0x78;
    private const ulong MaxAccessUnitBytes = 32UL * 1024 * 1024;
    private const int MaxFrameDimension = 8192;
    private const int MaxOutputBuffers = 64;

    private static readonly ConcurrentDictionary<ulong, Decoder> Decoders = new();
    private static readonly ConcurrentDictionary<ulong, byte> ComputeQueues = new();
    private static readonly ConcurrentDictionary<ulong, (ulong Handle, byte[] Info)> PictureInfos = new();
    private static long _nextDecoder = unchecked((long)0x5644_5357_0000_0000UL);
    private static long _nextQueue = unchecked((long)0x5644_5351_0000_0000UL);
    private static Func<IVdecswCodec?> _codecFactory = FfmpegVdecswCodec.TryCreate;

    private sealed class Decoder(ulong handle, IVdecswCodec codec, ulong maxFrameBufferSize) : IDisposable
    {
        public readonly object Gate = new();
        public ulong Handle { get; } = handle;
        public IVdecswCodec Codec { get; } = codec;
        public ulong MaxFrameBufferSize { get; } = maxFrameBufferSize;
        public readonly Queue<PendingInput> Inputs = new();
        public readonly Queue<(ulong AuPointer, uint Frames)> Consumed = new();
        public readonly Queue<(ulong Pointer, ulong Size)> Outputs = new();
        public readonly Queue<Videodec2DecodedFrame> Pictures = new();
        public bool Finalized;

        public void Dispose() => Codec.Dispose();
    }

    private readonly record struct PendingInput(
        ulong AuPointer, byte[] Data, ulong Pts, ulong Dts, ulong Attached);

    [SysAbiExport(
        Nid = "0moTubWCsTM",
        ExportName = "sceVdecswQueryComputeMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswQueryComputeMemoryInfo(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(address, out var size) || size < ComputeMemoryInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        return ctx.SetReturn(
            ctx.TryWriteUInt64(address + 0x08, MinimumMemoryBytes) && ctx.TryWriteUInt64(address + 0x10, 0)
                ? 0
                : ErrorArgumentPointer);
    }

    [SysAbiExport(
        Nid = "hIgrg5h4V6s",
        ExportName = "sceVdecswAllocateComputeQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswAllocateComputeQueue(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var queueAddress = ctx[CpuRegister.Rdx];
        if (configAddress == 0 || memoryAddress == 0 || queueAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(configAddress, out var configSize) || configSize < ComputeConfigInfoSize ||
            !ctx.TryReadUInt64(memoryAddress, out var memoryStructSize) || memoryStructSize < ComputeMemoryInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!ctx.TryReadUInt64(memoryAddress + 0x08, out var memorySize) || memorySize < MinimumMemoryBytes)
        {
            return ctx.SetReturn(ErrorMemorySize);
        }

        if (!ctx.TryReadUInt64(memoryAddress + 0x10, out var memoryPointer) || memoryPointer == 0)
        {
            return ctx.SetReturn(ErrorMemoryPointer);
        }

        var queue = unchecked((ulong)Interlocked.Increment(ref _nextQueue));
        ComputeQueues[queue] = 0;
        if (!ctx.TryWriteUInt64(queueAddress, queue))
        {
            ComputeQueues.TryRemove(queue, out _);
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "fX-zOOefbbs",
        ExportName = "sceVdecswReleaseComputeQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswReleaseComputeQueue(CpuContext ctx) =>
        ctx.SetReturn(ComputeQueues.TryRemove(ctx[CpuRegister.Rdi], out _) ? 0 : ErrorComputeQueue);

    [SysAbiExport(
        Nid = "A+2M7EivuOU",
        ExportName = "sceVdecswQueryDecoderMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswQueryDecoderMemoryInfo(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        if (configAddress == 0 || memoryAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        var status = ReadConfig(ctx, configAddress, out var width, out var height);
        if (status == 0 &&
            (!ctx.TryReadUInt64(memoryAddress, out var memorySize) || memorySize < DecoderMemoryInfoSize))
        {
            status = ErrorStructSize;
        }

        if (status != 0)
        {
            return ctx.SetReturn(status);
        }

        // Pointers stay zero for the guest to fill after allocating.
        var ok = ctx.TryWriteUInt64(memoryAddress + 0x08, MinimumMemoryBytes) &&
            ctx.TryWriteUInt64(memoryAddress + 0x10, 0) &&
            ctx.TryWriteUInt64(memoryAddress + 0x18, MinimumMemoryBytes) &&
            ctx.TryWriteUInt64(memoryAddress + 0x20, 0) &&
            ctx.TryWriteUInt64(memoryAddress + 0x28, MinimumMemoryBytes) &&
            ctx.TryWriteUInt64(memoryAddress + 0x30, 0) &&
            ctx.TryWriteUInt64(memoryAddress + 0x38, MaxFrameBufferBytes(width, height)) &&
            ctx.TryWriteUInt32(memoryAddress + 0x40, FrameBufferAlignment) &&
            ctx.TryWriteUInt32(memoryAddress + 0x44, 0);
        return ctx.SetReturn(ok ? 0 : ErrorArgumentPointer);
    }

    [SysAbiExport(
        Nid = "+L5ArV1tPGA",
        ExportName = "sceVdecswCreateDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswCreateDecoder(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var handleAddress = ctx[CpuRegister.Rdx];
        if (configAddress == 0 || memoryAddress == 0 || handleAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        var status = ReadConfig(ctx, configAddress, out _, out _);
        if (status != 0)
        {
            return ctx.SetReturn(status);
        }

        if (!ctx.TryReadUInt64(memoryAddress, out var memoryStructSize) || memoryStructSize < DecoderMemoryInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!ctx.TryReadUInt64(memoryAddress + 0x38, out var maxFrameBytes) || maxFrameBytes == 0)
        {
            return ctx.SetReturn(ErrorMemorySize);
        }

        var codec = _codecFactory();
        if (codec is null)
        {
            // FFmpeg (or its H.264 decoder) is not loadable on this host.
            return ctx.SetReturn(ErrorApiFail);
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextDecoder));
        var decoder = new Decoder(handle, codec, maxFrameBytes);
        Decoders[handle] = decoder;
        if (!ctx.TryWriteUInt64(handleAddress, handle))
        {
            Decoders.TryRemove(handle, out _);
            decoder.Dispose();
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ecUtPX+dBYk",
        ExportName = "sceVdecswDeleteDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswDeleteDecoder(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (!Decoders.TryRemove(handle, out var decoder))
        {
            return ctx.SetReturn(ErrorDecoderInstance);
        }

        lock (decoder.Gate)
        {
            DropPictureInfos(handle);
            decoder.Dispose();
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "aqMiF0AgUYI",
        ExportName = "sceVdecswSetDecodeInput",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswSetDecodeInput(CpuContext ctx)
    {
        var inputAddress = ctx[CpuRegister.Rsi];
        if (!Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var decoder))
        {
            return ctx.SetReturn(ErrorDecoderInstance);
        }

        if (inputAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(inputAddress, out var size) || size < InputDataSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!ctx.TryReadUInt64(inputAddress + 0x08, out var auPointer) || auPointer == 0)
        {
            return ctx.SetReturn(ErrorAccessUnitPointer);
        }

        if (!ctx.TryReadUInt64(inputAddress + 0x10, out var auSize) || auSize == 0 || auSize > MaxAccessUnitBytes)
        {
            return ctx.SetReturn(ErrorAccessUnitSize);
        }

        _ = ctx.TryReadUInt64(inputAddress + 0x18, out var pts);
        _ = ctx.TryReadUInt64(inputAddress + 0x20, out var dts);
        _ = ctx.TryReadUInt64(inputAddress + 0x28, out var attached);
        var data = new byte[auSize];
        if (!ctx.Memory.TryRead(auPointer, data))
        {
            return ctx.SetReturn(ErrorAccessUnitPointer);
        }

        lock (decoder.Gate)
        {
            if (decoder.Finalized)
            {
                // The sequence was finalized: reset the decoder before new input.
                return ctx.SetReturn(ErrorApiFail);
            }

            decoder.Inputs.Enqueue(new PendingInput(auPointer, data, pts, dts, attached));
            Advance(decoder, ignoreCapacity: false);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "l4sQYy5wPkc",
        ExportName = "sceVdecswTrySyncDecodeInput",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswTrySyncDecodeInput(CpuContext ctx) =>
        ctx.SetReturn(SyncInput(ctx));

    // Decoding is synchronous, so a blocking sync cannot wait for anything.
    [SysAbiExport(
        Nid = "AAMM-Q1X0g0",
        ExportName = "sceVdecswSyncDecodeInput",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswSyncDecodeInput(CpuContext ctx) =>
        ctx.SetReturn(SyncInput(ctx));

    private static int SyncInput(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsi];
        if (!Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var decoder))
        {
            return ErrorDecoderInstance;
        }

        if (resultAddress == 0)
        {
            return ErrorArgumentPointer;
        }

        if (!ctx.TryReadUInt64(resultAddress, out var size) || size < InputResultSize)
        {
            return ErrorStructSize;
        }

        int status;
        ulong auPointer = 0;
        uint frames = 0;
        lock (decoder.Gate)
        {
            Advance(decoder, ignoreCapacity: false);
            if (decoder.Consumed.Count > 0)
            {
                (auPointer, frames) = decoder.Consumed.Dequeue();
                status = 0;
            }
            else
            {
                status = decoder.Inputs.Count == 0 ? ErrorInputQueueEmpty : ErrorDecodePending;
            }
        }

        return ctx.TryWriteUInt64(resultAddress + 0x08, auPointer) &&
            ctx.TryWriteUInt32(resultAddress + 0x10, frames) &&
            ctx.TryWriteUInt32(resultAddress + 0x14, 0)
                ? status
                : ErrorArgumentPointer;
    }

    [SysAbiExport(
        Nid = "rgtMCOpyBSc",
        ExportName = "sceVdecswSetDecodeOutput",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswSetDecodeOutput(CpuContext ctx)
    {
        var frameAddress = ctx[CpuRegister.Rsi];
        if (!Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var decoder))
        {
            return ctx.SetReturn(ErrorDecoderInstance);
        }

        if (frameAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(frameAddress, out var size) || size < FrameBufferSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!ctx.TryReadUInt64(frameAddress + 0x08, out var pointer) || pointer == 0)
        {
            return ctx.SetReturn(ErrorFrameBufferPointer);
        }

        if (!ctx.TryReadUInt64(frameAddress + 0x10, out var bufferSize) || bufferSize < decoder.MaxFrameBufferSize)
        {
            return ctx.SetReturn(ErrorFrameBufferSize);
        }

        lock (decoder.Gate)
        {
            if (decoder.Outputs.Count >= MaxOutputBuffers)
            {
                return ctx.SetReturn(ErrorApiFail);
            }

            decoder.Outputs.Enqueue((pointer, bufferSize));
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "kMBw37oH8nI",
        ExportName = "sceVdecswTrySyncDecodeOutput",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswTrySyncDecodeOutput(CpuContext ctx) =>
        ctx.SetReturn(SyncOutput(ctx));

    [SysAbiExport(
        Nid = "tWiSgXov8GM",
        ExportName = "sceVdecswSyncDecodeOutput",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswSyncDecodeOutput(CpuContext ctx) =>
        ctx.SetReturn(SyncOutput(ctx));

    private static int SyncOutput(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var decoder))
        {
            return ErrorDecoderInstance;
        }

        if (outputAddress == 0)
        {
            return ErrorArgumentPointer;
        }

        if (!ctx.TryReadUInt64(outputAddress, out var size) || size < OutputInfoSize)
        {
            return ErrorStructSize;
        }

        lock (decoder.Gate)
        {
            Advance(decoder, ignoreCapacity: false);

            // No buffer to deliver into yet: the output is not ready.
            var ended = decoder.Finalized;
            if (decoder.Outputs.Count == 0 || decoder.Pictures.Count < (ended ? 1 : 2))
            {
                if (!ended || decoder.Outputs.Count == 0 || decoder.Pictures.Count > 0)
                {
                    return ErrorOutputPending;
                }

                // Finalized and fully drained: the end-of-sequence marker.
                var marker = new byte[OutputInfoSize];
                BinaryPrimitives.WriteUInt64LittleEndian(marker, size);
                marker[0x09] = 1;
                if (!ctx.Memory.TryWrite(outputAddress, marker))
                {
                    return ErrorArgumentPointer;
                }

                decoder.Outputs.Dequeue();
                return 0;
            }

            var picture = decoder.Pictures.Peek();
            var target = decoder.Outputs.Peek();
            decoder.Outputs.Dequeue();
            decoder.Pictures.Dequeue();
            if ((ulong)picture.Nv12.Length > target.Size ||
                !ctx.Memory.TryWrite(target.Pointer, picture.Nv12))
            {
                // The picture cannot be delivered into this buffer; it is dropped.
                return ErrorFrameBufferSize;
            }

            var info = new byte[OutputInfoSize];
            BinaryPrimitives.WriteUInt64LittleEndian(info, size);
            info[0x08] = 1;
            info[0x09] = (byte)(ended && decoder.Pictures.Count == 0 ? 1 : 0);
            info[0x0A] = (byte)(picture.ErrorFrame ? 1 : 0);
            info[0x0B] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x0C), CodecTypeAvc);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x10), picture.Width);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x14), picture.Pitch);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x18), picture.Height);
            BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(0x20), target.Pointer);
            BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(0x28), (ulong)picture.Nv12.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x30), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x34), picture.Pitch);
            if (!ctx.Memory.TryWrite(outputAddress, info))
            {
                return ErrorArgumentPointer;
            }

            PictureInfos[target.Pointer] = (decoder.Handle, BuildAvcPictureInfo(picture));
            return 0;
        }
    }

    [SysAbiExport(
        Nid = "ihNT-uuEAr4",
        ExportName = "sceVdecswGetAvcPictureInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswGetAvcPictureInfo(CpuContext ctx) =>
        ctx.SetReturn(GetPictureInfo(ctx, requireAvcCodec: false));

    [SysAbiExport(
        Nid = "FzECy3Wxxas",
        ExportName = "sceVdecswGetPictureInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswGetPictureInfo(CpuContext ctx) =>
        ctx.SetReturn(GetPictureInfo(ctx, requireAvcCodec: true));

    // The decoder only produces AVC pictures, so an OutputInfo can never refer
    // to an HEVC or VP9 picture.
    [SysAbiExport(
        Nid = "PzF+L5zXoyg",
        ExportName = "sceVdecswGetHevcPictureInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswGetHevcPictureInfo(CpuContext ctx) =>
        ctx.SetReturn(ctx[CpuRegister.Rdi] == 0 || ctx[CpuRegister.Rsi] == 0
            ? ErrorArgumentPointer
            : ErrorOutputInfo);

    [SysAbiExport(
        Nid = "LEqH+dkMszo",
        ExportName = "sceVdecswGetVp9PictureInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswGetVp9PictureInfo(CpuContext ctx) =>
        ctx.SetReturn(ctx[CpuRegister.Rdi] == 0 || ctx[CpuRegister.Rsi] == 0
            ? ErrorArgumentPointer
            : ErrorOutputInfo);

    private static int GetPictureInfo(CpuContext ctx, bool requireAvcCodec)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var firstAddress = ctx[CpuRegister.Rsi];
        var secondAddress = ctx[CpuRegister.Rdx];
        if (outputAddress == 0 || firstAddress == 0)
        {
            return ErrorArgumentPointer;
        }

        if (!ctx.TryReadUInt64(outputAddress, out var outputSize) || outputSize < OutputInfoSize ||
            !ctx.TryReadUInt64(firstAddress, out var firstSize) || firstSize < sizeof(ulong))
        {
            return ErrorStructSize;
        }

        Span<byte> header = stackalloc byte[0x10];
        if (!ctx.Memory.TryRead(outputAddress, header) ||
            !ctx.TryReadUInt64(outputAddress + 0x20, out var framePointer))
        {
            return ErrorOutputInfo;
        }

        var pictureCount = header[0x0B];
        var codecType = BinaryPrimitives.ReadUInt32LittleEndian(header[0x0C..]);
        if (pictureCount > 1 || (requireAvcCodec && codecType != CodecTypeAvc && pictureCount != 0))
        {
            // Field pictures (a second picture info) are not produced.
            return ErrorOutputInfo;
        }

        byte[] info;
        if (pictureCount == 0 || header[0x08] == 0)
        {
            // No picture (end-of-sequence marker): an invalid, otherwise empty info.
            info = new byte[AvcPictureInfoSize];
        }
        else if (!PictureInfos.TryGetValue(framePointer, out var found))
        {
            return ErrorOutputInfo;
        }
        else
        {
            info = found.Info;
        }

        if (!WriteInfo(ctx, firstAddress, firstSize, info))
        {
            return ErrorArgumentPointer;
        }

        if (secondAddress != 0)
        {
            if (!ctx.TryReadUInt64(secondAddress, out var secondSize) || secondSize < sizeof(ulong))
            {
                return ErrorStructSize;
            }

            if (!WriteInfo(ctx, secondAddress, secondSize, new byte[AvcPictureInfoSize]))
            {
                return ErrorArgumentPointer;
            }
        }

        return 0;
    }

    // Writes the body after thisSize, clamped to the caller's declared size.
    private static bool WriteInfo(CpuContext ctx, ulong address, ulong declaredSize, byte[] info)
    {
        var length = (int)Math.Min(declaredSize, AvcPictureInfoSize) - sizeof(ulong);
        return length <= 0 ||
            ctx.Memory.TryWrite(address + sizeof(ulong), info.AsSpan(sizeof(ulong), length));
    }

    [SysAbiExport(
        Nid = "5Y6nZqIZvBg",
        ExportName = "sceVdecswFinalizeDecodeSequence",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswFinalizeDecodeSequence(CpuContext ctx)
    {
        if (!Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var decoder))
        {
            return ctx.SetReturn(ErrorDecoderInstance);
        }

        lock (decoder.Gate)
        {
            if (!decoder.Finalized)
            {
                Advance(decoder, ignoreCapacity: true);
                var drained = new List<Videodec2DecodedFrame>();
                decoder.Codec.Drain(drained);
                foreach (var picture in drained)
                {
                    decoder.Pictures.Enqueue(picture);
                }

                decoder.Finalized = true;
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "veb-YBrOqo0",
        ExportName = "sceVdecswResetDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVdecsw")]
    public static int VdecswResetDecoder(CpuContext ctx)
    {
        if (!Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var decoder))
        {
            return ctx.SetReturn(ErrorDecoderInstance);
        }

        lock (decoder.Gate)
        {
            decoder.Inputs.Clear();
            decoder.Consumed.Clear();
            decoder.Outputs.Clear();
            decoder.Pictures.Clear();
            decoder.Finalized = false;
            DropPictureInfos(decoder.Handle);
            decoder.Codec.Reset();
        }

        return ctx.SetReturn(0);
    }

    /// <summary>Decodes pending inputs while the picture queue has room (or always when told to).</summary>
    private static void Advance(Decoder decoder, bool ignoreCapacity)
    {
        while (decoder.Inputs.Count > 0 && (ignoreCapacity || decoder.Pictures.Count < MaxQueuedPictures))
        {
            var input = decoder.Inputs.Dequeue();
            uint frames = 0;
            if (decoder.Codec.Decode(input.Data, input.Pts, input.Dts, input.Attached, out var picture) &&
                picture is not null)
            {
                decoder.Pictures.Enqueue(picture);
                frames = 1;
            }

            decoder.Consumed.Enqueue((input.AuPointer, frames));
        }
    }

    private static byte[] BuildAvcPictureInfo(Videodec2DecodedFrame picture)
    {
        var info = new byte[AvcPictureInfoSize];
        BinaryPrimitives.WriteUInt64LittleEndian(info, AvcPictureInfoSize);
        info[0x08] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(0x10), picture.Pts);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(0x18), picture.Dts);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(0x20), picture.AttachedData);
        info[0x28] = (byte)(picture.KeyFrame ? 1 : 0);
        info[0x29] = (byte)Math.Min(picture.Profile, byte.MaxValue);
        info[0x2A] = (byte)Math.Min(picture.Level, byte.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x2C), ((picture.Width + 15u) / 16u) - 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0x30), ((picture.Height + 15u) / 16u) - 1u);
        info[0x34] = 1; // frame_mbs_only_flag
        // frameCroppingFlag and the crop offsets stay 0: pictures are delivered cropped.
        return info;
    }

    private static int ReadConfig(CpuContext ctx, ulong address, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!ctx.TryReadUInt64(address, out var size) || size < DecoderConfigInfoSize)
        {
            return ErrorStructSize;
        }

        if (!ctx.TryReadUInt32(address + 0x0C, out var codecType) ||
            !ctx.TryReadInt32(address + 0x18, out width) ||
            !ctx.TryReadInt32(address + 0x1C, out height))
        {
            return ErrorConfigInfo;
        }

        if (codecType != CodecTypeAvc)
        {
            return ErrorCodecType;
        }

        return width is < 1 or > MaxFrameDimension || height is < 1 or > MaxFrameDimension
            ? ErrorFrameWidthHeight
            : 0;
    }

    /// <summary>NV12 size at the decoder's 256-byte aligned pitch, for macroblock-aligned height.</summary>
    internal static ulong MaxFrameBufferBytes(int width, int height)
    {
        var pitch = (ulong)((width + 255) & ~255);
        var rows = (ulong)((height + 15) & ~15);
        return (pitch * rows) + (pitch * ((rows + 1) / 2));
    }

    private static void DropPictureInfos(ulong handle)
    {
        foreach (var pair in PictureInfos)
        {
            if (pair.Value.Handle == handle)
            {
                PictureInfos.TryRemove(pair.Key, out _);
            }
        }
    }

    internal static void ResetForTests()
    {
        foreach (var decoder in Decoders.Values)
        {
            decoder.Dispose();
        }

        Decoders.Clear();
        ComputeQueues.Clear();
        PictureInfos.Clear();
        _codecFactory = FfmpegVdecswCodec.TryCreate;
    }

    internal static void SetCodecFactoryForTests(Func<IVdecswCodec?> factory) => _codecFactory = factory;
}
