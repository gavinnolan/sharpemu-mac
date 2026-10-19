// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Codec;
using Xunit;

namespace SharpEmu.Libs.Tests.Codec;

[CollectionDefinition("VdecswState", DisableParallelization = true)]
public sealed class VdecswStateCollection
{
}

[Collection("VdecswState")]
public sealed class VdecswExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong ConfigAddress = Base + 0x100;
    private const ulong MemoryAddress = Base + 0x200;
    private const ulong HandleAddress = Base + 0x300;
    private const ulong InputAddress = Base + 0x400;
    private const ulong ResultAddress = Base + 0x500;
    private const ulong FrameAddress = Base + 0x600;
    private const ulong OutputAddress = Base + 0x700;
    private const ulong InfoAddress = Base + 0x800;
    private const ulong Info2Address = Base + 0x900;
    private const ulong AuAddress = Base + 0x1000;
    private const ulong BufferAddress = Base + 0x10000;
    private const uint Width = 16;
    private const uint Height = 16;
    private const uint Pitch = 256;
    private const int FrameBytes = (int)(Pitch * Height * 3 / 2);

    private readonly FakeCpuMemory _memory = new(Base, 0x100000);
    private readonly CpuContext _ctx;
    private readonly FakeCodec _codec = new();

    public VdecswExportsTests()
    {
        VdecswExports.ResetForTests();
        VdecswExports.SetCodecFactoryForTests(() => _codec);
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => VdecswExports.ResetForTests();

    [Fact]
    public void QueryComputeMemoryInfo_WritesSizeAndRejectsBadArguments()
    {
        Assert.Equal(VdecswExports.ErrorArgumentPointer, Call(VdecswExports.VdecswQueryComputeMemoryInfo, 0UL));
        WriteU64(MemoryAddress, 8);
        Assert.Equal(VdecswExports.ErrorStructSize, Call(VdecswExports.VdecswQueryComputeMemoryInfo, MemoryAddress));
        WriteU64(MemoryAddress, 0x18);
        Assert.Equal(0, Call(VdecswExports.VdecswQueryComputeMemoryInfo, MemoryAddress));
        Assert.Equal(VdecswExports.MinimumMemoryBytes, ReadU64(MemoryAddress + 8));
        Assert.Equal(0UL, ReadU64(MemoryAddress + 0x10));
    }

    [Fact]
    public void ComputeQueue_AllocateAndReleaseLifetime()
    {
        WriteU64(ConfigAddress, 0x10);
        WriteU64(MemoryAddress, 0x18);
        WriteU64(MemoryAddress + 8, VdecswExports.MinimumMemoryBytes);
        WriteU64(MemoryAddress + 0x10, 0);
        Assert.Equal(VdecswExports.ErrorMemoryPointer,
            Call(VdecswExports.VdecswAllocateComputeQueue, ConfigAddress, MemoryAddress, HandleAddress));
        WriteU64(MemoryAddress + 0x10, Base + 0x40000);
        Assert.Equal(0, Call(VdecswExports.VdecswAllocateComputeQueue, ConfigAddress, MemoryAddress, HandleAddress));
        var queue = ReadU64(HandleAddress);
        Assert.NotEqual(0UL, queue);
        Assert.Equal(0, Call(VdecswExports.VdecswReleaseComputeQueue, queue));
        Assert.Equal(VdecswExports.ErrorComputeQueue, Call(VdecswExports.VdecswReleaseComputeQueue, queue));
        WriteU64(MemoryAddress + 8, 1);
        Assert.Equal(VdecswExports.ErrorMemorySize,
            Call(VdecswExports.VdecswAllocateComputeQueue, ConfigAddress, MemoryAddress, HandleAddress));
    }

    [Fact]
    public void QueryDecoderMemoryInfo_ComputesFrameBufferSize()
    {
        WriteConfig(1920, 1080);
        WriteU64(MemoryAddress, 0x48);
        Assert.Equal(0, Call(VdecswExports.VdecswQueryDecoderMemoryInfo, ConfigAddress, MemoryAddress));
        Assert.Equal(VdecswExports.MinimumMemoryBytes, ReadU64(MemoryAddress + 0x08));
        Assert.Equal(VdecswExports.MinimumMemoryBytes, ReadU64(MemoryAddress + 0x18));
        Assert.Equal(VdecswExports.MinimumMemoryBytes, ReadU64(MemoryAddress + 0x28));
        // 2048 pitch * 1088 rows luma + 2048 * 544 chroma rows.
        Assert.Equal(2048UL * 1088 + 2048UL * 544, ReadU64(MemoryAddress + 0x38));
        Assert.Equal(0x100U, ReadU32(MemoryAddress + 0x40));
    }

    [Fact]
    public void QueryDecoderMemoryInfo_RejectsUnsupportedConfigs()
    {
        WriteConfig(1920, 1080, codecType: 2);
        WriteU64(MemoryAddress, 0x48);
        Assert.Equal(VdecswExports.ErrorCodecType, Call(VdecswExports.VdecswQueryDecoderMemoryInfo, ConfigAddress, MemoryAddress));
        WriteConfig(0, 1080);
        Assert.Equal(VdecswExports.ErrorFrameWidthHeight, Call(VdecswExports.VdecswQueryDecoderMemoryInfo, ConfigAddress, MemoryAddress));
        WriteConfig(1920, 1080, size: 0x20);
        Assert.Equal(VdecswExports.ErrorStructSize, Call(VdecswExports.VdecswQueryDecoderMemoryInfo, ConfigAddress, MemoryAddress));
        WriteConfig(1920, 1080);
        WriteU64(MemoryAddress, 0x10);
        Assert.Equal(VdecswExports.ErrorStructSize, Call(VdecswExports.VdecswQueryDecoderMemoryInfo, ConfigAddress, MemoryAddress));
        Assert.Equal(VdecswExports.ErrorArgumentPointer, Call(VdecswExports.VdecswQueryDecoderMemoryInfo, 0UL, MemoryAddress));
    }

    [Fact]
    public void CreateDecoder_ValidatesAndReportsMissingFfmpeg()
    {
        PrepareMemoryInfo();
        VdecswExports.SetCodecFactoryForTests(() => null);
        Assert.Equal(VdecswExports.ErrorApiFail, Call(VdecswExports.VdecswCreateDecoder, ConfigAddress, MemoryAddress, HandleAddress));

        VdecswExports.SetCodecFactoryForTests(() => _codec);
        WriteConfig(Width, Height, codecType: 3);
        Assert.Equal(VdecswExports.ErrorCodecType, Call(VdecswExports.VdecswCreateDecoder, ConfigAddress, MemoryAddress, HandleAddress));
        WriteConfig(Width, Height);
        Assert.Equal(VdecswExports.ErrorArgumentPointer, Call(VdecswExports.VdecswCreateDecoder, ConfigAddress, MemoryAddress, 0UL));
        WriteU64(MemoryAddress + 0x38, 0);
        Assert.Equal(VdecswExports.ErrorMemorySize, Call(VdecswExports.VdecswCreateDecoder, ConfigAddress, MemoryAddress, HandleAddress));
    }

    [Fact]
    public void DecoderHandleLifetime()
    {
        var handle = CreateDecoder();
        Assert.NotEqual(0UL, handle);
        Assert.Equal(VdecswExports.ErrorDecoderInstance, Call(VdecswExports.VdecswResetDecoder, handle + 1));
        Assert.Equal(0, Call(VdecswExports.VdecswDeleteDecoder, handle));
        Assert.True(_codec.Disposed);
        Assert.Equal(VdecswExports.ErrorDecoderInstance, Call(VdecswExports.VdecswDeleteDecoder, handle));
        Assert.Equal(VdecswExports.ErrorDecoderInstance, Call(VdecswExports.VdecswSetDecodeInput, handle, InputAddress));
        Assert.Equal(VdecswExports.ErrorDecoderInstance, Call(VdecswExports.VdecswFinalizeDecodeSequence, handle));
    }

    [Fact]
    public void FullSequence_DeliversPicturesInOrderWithPictureInfo()
    {
        var handle = CreateDecoder();
        WriteU64(InfoAddress, 0x78);
        WriteU64(Info2Address, 0x78);
        Assert.Equal(VdecswExports.ErrorInputQueueEmpty, TrySyncInput(handle));

        // Three access units decode to pictures; draining yields one more buffered picture.
        _codec.Drained.Add(MakePicture(pts: 1003, dts: 3, attached: 0xA3, key: false));
        SetInput(handle, 0, pts: 1000, dts: 0, attached: 0xA0);
        Assert.Equal(0, TrySyncInput(handle));
        Assert.Equal(AuAddress, ReadU64(ResultAddress + 8));
        Assert.Equal(1U, ReadU32(ResultAddress + 0x10));
        Assert.Equal(VdecswExports.ErrorInputQueueEmpty, TrySyncInput(handle));

        // No output buffer yet: not ready.
        Assert.Equal(VdecswExports.ErrorOutputPending, TrySyncOutput(handle));
        SetOutput(handle, BufferAddress);
        // The only picture is held back until a successor shows it is not the last.
        Assert.Equal(VdecswExports.ErrorOutputPending, TrySyncOutput(handle));

        SetInput(handle, 1, pts: 1001, dts: 1, attached: 0xA1);
        SetInput(handle, 2, pts: 1002, dts: 2, attached: 0xA2);
        Assert.Equal(0, TrySyncInput(handle));
        Assert.Equal(AuAddress + 0x100, ReadU64(ResultAddress + 8));
        Assert.Equal(0, TrySyncInput(handle));
        Assert.Equal(AuAddress + 0x200, ReadU64(ResultAddress + 8));

        Assert.Equal(0, TrySyncOutput(handle));
        AssertOutput(isLast: false, buffer: BufferAddress);
        Assert.Equal(0, Call(VdecswExports.VdecswGetAvcPictureInfo, OutputAddress, InfoAddress, 0UL));
        Assert.Equal(1UL, ReadByte(InfoAddress + 8));
        Assert.Equal(1000UL, ReadU64(InfoAddress + 0x10));
        Assert.Equal(0UL, ReadU64(InfoAddress + 0x18));
        Assert.Equal(0xA0UL, ReadU64(InfoAddress + 0x20));
        Assert.Equal(1UL, ReadByte(InfoAddress + 0x28));
        Assert.Equal(100UL, ReadByte(InfoAddress + 0x29));
        Assert.Equal(42UL, ReadByte(InfoAddress + 0x2A));
        Assert.Equal(0U, ReadU32(InfoAddress + 0x2C));
        Assert.Equal(0U, ReadU32(InfoAddress + 0x30));
        Assert.Equal(1UL, ReadByte(InfoAddress + 0x34));
        Assert.Equal(0UL, ReadByte(InfoAddress + 0x35));
        // The picture pixels landed in the guest buffer.
        Assert.Equal(0xA0, ReadByte(BufferAddress + 1));

        Assert.Equal(0, Call(VdecswExports.VdecswFinalizeDecodeSequence, handle));
        for (ulong i = 1; i <= 4; i++)
        {
            SetOutput(handle, BufferAddress + (i * 0x10000));
        }

        Assert.Equal(0, TrySyncOutput(handle));
        AssertOutput(isLast: false, buffer: BufferAddress + 0x10000);
        Assert.Equal(0xA1, ReadByte(BufferAddress + 0x10001));
        Assert.Equal(0, TrySyncOutput(handle));
        AssertOutput(isLast: false, buffer: BufferAddress + 0x20000);
        // The drained picture is the last one.
        Assert.Equal(0, TrySyncOutput(handle));
        AssertOutput(isLast: true, buffer: BufferAddress + 0x30000);
        Assert.Equal(0, Call(VdecswExports.VdecswGetAvcPictureInfo, OutputAddress, InfoAddress, Info2Address));
        Assert.Equal(1003UL, ReadU64(InfoAddress + 0x10));
        Assert.Equal(0xA3UL, ReadU64(InfoAddress + 0x20));

        // End-of-sequence marker.
        Assert.Equal(0, TrySyncOutput(handle));
        Assert.Equal(0UL, ReadByte(OutputAddress + 8));
        Assert.Equal(1UL, ReadByte(OutputAddress + 9));
        Assert.Equal(0UL, ReadByte(OutputAddress + 0xB));
        Assert.Equal(0, Call(VdecswExports.VdecswGetAvcPictureInfo, OutputAddress, InfoAddress, 0UL));
        Assert.Equal(0UL, ReadByte(InfoAddress + 8));
        Assert.Equal(VdecswExports.ErrorInputQueueEmpty, TrySyncInput(handle));
        Assert.Equal(VdecswExports.ErrorApiFail, SetInputStatus(handle, 3));
    }

    [Fact]
    public void Decoding_StopsWhenPictureQueueIsFull_AndResumesAsOutputsDrain()
    {
        var handle = CreateDecoder();
        for (var i = 0; i < 6; i++)
        {
            SetInput(handle, i, pts: (ulong)i, dts: (ulong)i, attached: 0);
        }

        Assert.Equal(VdecswExports.MaxQueuedPictures, _codec.DecodeCalls);
        for (var i = 0; i < VdecswExports.MaxQueuedPictures; i++)
        {
            Assert.Equal(0, TrySyncInput(handle));
        }

        Assert.Equal(VdecswExports.ErrorDecodePending, TrySyncInput(handle));
        SetOutput(handle, BufferAddress);
        Assert.Equal(0, TrySyncOutput(handle));
        Assert.Equal(0, TrySyncInput(handle));
        Assert.Equal(5, _codec.DecodeCalls);
    }

    [Fact]
    public void BlockingSyncVariantsReportStatusInsteadOfBlocking()
    {
        var handle = CreateDecoder();
        WriteU64(ResultAddress, 0x18);
        WriteU64(OutputAddress, 0x38);
        Assert.Equal(VdecswExports.ErrorInputQueueEmpty,
            Call(VdecswExports.VdecswSyncDecodeInput, handle, ResultAddress));
        SetOutput(handle, BufferAddress);
        Assert.Equal(VdecswExports.ErrorOutputPending,
            Call(VdecswExports.VdecswSyncDecodeOutput, handle, OutputAddress));
    }

    [Fact]
    public void SetDecodeInput_ValidatesArguments()
    {
        var handle = CreateDecoder();
        Assert.Equal(VdecswExports.ErrorArgumentPointer, Call(VdecswExports.VdecswSetDecodeInput, handle, 0UL));
        WriteU64(InputAddress, 0x10);
        Assert.Equal(VdecswExports.ErrorStructSize, Call(VdecswExports.VdecswSetDecodeInput, handle, InputAddress));
        WriteInput(0, 1, 0, 0);
        WriteU64(InputAddress + 8, 0);
        Assert.Equal(VdecswExports.ErrorAccessUnitPointer, Call(VdecswExports.VdecswSetDecodeInput, handle, InputAddress));
        WriteU64(InputAddress + 8, AuAddress);
        WriteU64(InputAddress + 0x10, 0);
        Assert.Equal(VdecswExports.ErrorAccessUnitSize, Call(VdecswExports.VdecswSetDecodeInput, handle, InputAddress));
        WriteU64(InputAddress + 0x10, 64UL * 1024 * 1024);
        Assert.Equal(VdecswExports.ErrorAccessUnitSize, Call(VdecswExports.VdecswSetDecodeInput, handle, InputAddress));
        Assert.Equal(0, _codec.DecodeCalls);
    }

    [Fact]
    public void SetDecodeOutput_ValidatesArguments()
    {
        var handle = CreateDecoder();
        Assert.Equal(VdecswExports.ErrorArgumentPointer, Call(VdecswExports.VdecswSetDecodeOutput, handle, 0UL));
        WriteU64(FrameAddress, 0x10);
        Assert.Equal(VdecswExports.ErrorStructSize, Call(VdecswExports.VdecswSetDecodeOutput, handle, FrameAddress));
        WriteFrame(0, FrameBytes);
        Assert.Equal(VdecswExports.ErrorFrameBufferPointer, Call(VdecswExports.VdecswSetDecodeOutput, handle, FrameAddress));
        WriteFrame(BufferAddress, FrameBytes - 1);
        Assert.Equal(VdecswExports.ErrorFrameBufferSize, Call(VdecswExports.VdecswSetDecodeOutput, handle, FrameAddress));
        WriteFrame(BufferAddress, FrameBytes);
        Assert.Equal(0, Call(VdecswExports.VdecswSetDecodeOutput, handle, FrameAddress));
    }

    [Fact]
    public void ResetDecoder_DropsInputsOutputsAndPictures()
    {
        var handle = CreateDecoder();
        SetInput(handle, 0, 1, 1, 0);
        SetOutput(handle, BufferAddress);
        Assert.Equal(0, Call(VdecswExports.VdecswResetDecoder, handle));
        Assert.True(_codec.WasReset);
        Assert.Equal(VdecswExports.ErrorInputQueueEmpty, TrySyncInput(handle));
        Assert.Equal(VdecswExports.ErrorOutputPending, TrySyncOutput(handle));
        Assert.Equal(0, SetInputStatus(handle, 1));
    }

    [Fact]
    public void PictureInfo_RejectsForeignAndUnsupportedOutputs()
    {
        var handle = CreateDecoder();
        Assert.Equal(VdecswExports.ErrorArgumentPointer, Call(VdecswExports.VdecswGetAvcPictureInfo, 0UL, InfoAddress, 0UL));
        WriteU64(OutputAddress, 0x10);
        Assert.Equal(VdecswExports.ErrorStructSize, Call(VdecswExports.VdecswGetAvcPictureInfo, OutputAddress, InfoAddress, 0UL));

        // A valid-looking OutputInfo whose frame buffer no decoder returned.
        var output = new byte[0x38];
        BinaryPrimitives.WriteUInt64LittleEndian(output, 0x38);
        output[8] = 1;
        output[0x0B] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x0C), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(0x20), BufferAddress + 0x5000);
        Assert.True(_memory.TryWrite(OutputAddress, output));
        WriteU64(InfoAddress, 0x78);
        Assert.Equal(VdecswExports.ErrorOutputInfo, Call(VdecswExports.VdecswGetAvcPictureInfo, OutputAddress, InfoAddress, 0UL));

        output[0x0B] = 2;
        Assert.True(_memory.TryWrite(OutputAddress, output));
        Assert.Equal(VdecswExports.ErrorOutputInfo, Call(VdecswExports.VdecswGetAvcPictureInfo, OutputAddress, InfoAddress, 0UL));

        output[0x0B] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x0C), 2);
        Assert.True(_memory.TryWrite(OutputAddress, output));
        Assert.Equal(VdecswExports.ErrorOutputInfo, Call(VdecswExports.VdecswGetPictureInfo, OutputAddress, InfoAddress, 0UL));
        Assert.Equal(VdecswExports.ErrorOutputInfo, Call(VdecswExports.VdecswGetHevcPictureInfo, OutputAddress, InfoAddress, 0UL));
        Assert.Equal(VdecswExports.ErrorOutputInfo, Call(VdecswExports.VdecswGetVp9PictureInfo, OutputAddress, InfoAddress, 0UL));
        Assert.Equal(VdecswExports.ErrorArgumentPointer, Call(VdecswExports.VdecswGetHevcPictureInfo, 0UL, InfoAddress, 0UL));
        _ = handle;
    }

    private void AssertOutput(bool isLast, ulong buffer)
    {
        Assert.Equal(1UL, ReadByte(OutputAddress + 8));
        Assert.Equal(isLast ? 1UL : 0UL, ReadByte(OutputAddress + 9));
        Assert.Equal(0UL, ReadByte(OutputAddress + 0xA));
        Assert.Equal(1UL, ReadByte(OutputAddress + 0xB));
        Assert.Equal(1U, ReadU32(OutputAddress + 0x0C));
        Assert.Equal(Width, ReadU32(OutputAddress + 0x10));
        Assert.Equal(Pitch, ReadU32(OutputAddress + 0x14));
        Assert.Equal(Height, ReadU32(OutputAddress + 0x18));
        Assert.Equal(0UL, ReadByte(OutputAddress + 0x1C));
        Assert.Equal(buffer, ReadU64(OutputAddress + 0x20));
        Assert.Equal((ulong)FrameBytes, ReadU64(OutputAddress + 0x28));
        Assert.Equal(0U, ReadU32(OutputAddress + 0x30));
        Assert.Equal(Pitch, ReadU32(OutputAddress + 0x34));
    }

    private ulong CreateDecoder()
    {
        PrepareMemoryInfo();
        Assert.Equal(0, Call(VdecswExports.VdecswCreateDecoder, ConfigAddress, MemoryAddress, HandleAddress));
        return ReadU64(HandleAddress);
    }

    private void PrepareMemoryInfo()
    {
        WriteConfig(Width, Height);
        WriteU64(MemoryAddress, 0x48);
        Assert.Equal(0, Call(VdecswExports.VdecswQueryDecoderMemoryInfo, ConfigAddress, MemoryAddress));
        // Make the reported frame size match the 16x16 fake pictures exactly.
        WriteU64(MemoryAddress + 0x38, FrameBytes);
    }

    private void WriteConfig(uint width, uint height, uint codecType = 1, ulong size = 0x50) =>
        WriteConfig((int)width, (int)height, codecType, size);

    private void WriteConfig(int width, int height, uint codecType = 1, ulong size = 0x50)
    {
        var raw = new byte[0x50];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, size);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x0C), codecType);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x18), width);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x1C), height);
        Assert.True(_memory.TryWrite(ConfigAddress, raw));
    }

    private void WriteInput(int index, ulong pts, ulong dts, ulong attached)
    {
        WriteU64(InputAddress, 0x30);
        WriteU64(InputAddress + 0x08, AuAddress + (ulong)(index * 0x100));
        WriteU64(InputAddress + 0x10, 0x40);
        WriteU64(InputAddress + 0x18, pts);
        WriteU64(InputAddress + 0x20, dts);
        WriteU64(InputAddress + 0x28, attached);
    }

    private int SetInputStatus(ulong handle, int index)
    {
        WriteInput(index, (ulong)index, (ulong)index, 0);
        return Call(VdecswExports.VdecswSetDecodeInput, handle, InputAddress);
    }

    private void SetInput(ulong handle, int index, ulong pts, ulong dts, ulong attached)
    {
        // The fake codec derives each picture from the access unit's first byte.
        Assert.True(_memory.TryWrite(AuAddress + (ulong)(index * 0x100), new byte[0x40]));
        WriteInput(index, pts, dts, attached);
        _codec.NextAttached = attached;
        Assert.Equal(0, Call(VdecswExports.VdecswSetDecodeInput, handle, InputAddress));
    }

    private void WriteFrame(ulong pointer, int size)
    {
        WriteU64(FrameAddress, 0x18);
        WriteU64(FrameAddress + 8, pointer);
        WriteU64(FrameAddress + 0x10, (ulong)size);
    }

    private void SetOutput(ulong handle, ulong buffer)
    {
        WriteFrame(buffer, FrameBytes);
        Assert.Equal(0, Call(VdecswExports.VdecswSetDecodeOutput, handle, FrameAddress));
    }

    private int TrySyncInput(ulong handle)
    {
        WriteU64(ResultAddress, 0x18);
        return Call(VdecswExports.VdecswTrySyncDecodeInput, handle, ResultAddress);
    }

    private int TrySyncOutput(ulong handle)
    {
        WriteU64(OutputAddress, 0x38);
        return Call(VdecswExports.VdecswTrySyncDecodeOutput, handle, OutputAddress);
    }

    private static Videodec2DecodedFrame MakePicture(ulong pts, ulong dts, ulong attached, bool key)
    {
        var nv12 = new byte[FrameBytes];
        nv12[1] = (byte)attached;
        return new Videodec2DecodedFrame(nv12, Width, Height, Pitch, false)
        {
            Pts = pts,
            Dts = dts,
            AttachedData = attached,
            KeyFrame = key,
            Profile = 100,
            Level = 42,
        };
    }

    private int Call(Func<CpuContext, int> export, params object[] args)
    {
        CpuRegister[] registers =
        [
            CpuRegister.Rdi, CpuRegister.Rsi, CpuRegister.Rdx,
            CpuRegister.Rcx, CpuRegister.R8, CpuRegister.R9,
        ];
        for (var i = 0; i < registers.Length; i++)
        {
            _ctx[registers[i]] = i < args.Length ? ToRegister(args[i]) : 0;
        }

        return export(_ctx);
    }

    private static ulong ToRegister(object value) => value switch
    {
        ulong u => u,
        uint u => u,
        int i => unchecked((ulong)(long)i),
        _ => throw new ArgumentException("unsupported argument type"),
    };

    private ulong ReadU64(ulong address)
    {
        Assert.True(_ctx.TryReadUInt64(address, out var value));
        return value;
    }

    private uint ReadU32(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private byte ReadByte(ulong address)
    {
        Assert.True(_ctx.TryReadByte(address, out var value));
        return value;
    }

    private void WriteU64(ulong address, ulong value) => Assert.True(_ctx.TryWriteUInt64(address, value));

    /// <summary>Returns one picture per access unit (PTS/DTS/attached echoed); drained pictures are scripted.</summary>
    private sealed class FakeCodec : IVdecswCodec
    {
        public int DecodeCalls { get; private set; }
        public bool Disposed { get; private set; }
        public bool WasReset { get; private set; }
        public ulong NextAttached { get; set; }
        public List<Videodec2DecodedFrame> Drained { get; } = [];

        public bool Decode(
            byte[] accessUnit, ulong pts, ulong dts, ulong attachedData, out Videodec2DecodedFrame? picture)
        {
            picture = MakePicture(pts, dts, attachedData, key: DecodeCalls == 0);
            DecodeCalls++;
            return true;
        }

        public void Drain(List<Videodec2DecodedFrame> pictures) => pictures.AddRange(Drained);

        public void Reset() => WasReset = true;

        public void Dispose() => Disposed = true;
    }
}
