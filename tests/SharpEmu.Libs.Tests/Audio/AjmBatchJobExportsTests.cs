// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[Collection(AjmStateCollection.Name)]
public sealed class AjmBatchJobExportsTests : IDisposable
{
    private const int InvalidParameter = unchecked((int)0x80930005);
    private const int ResultInvalidParameter = 0x4;
    private const ulong ControlReset = 1UL << 13;
    private const ulong ControlInitialize = 1UL << 14;
    private const ulong SidebandGapless = 1UL << 45;
    private const ulong SidebandStream = 1UL << 47;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x100;
    private const ulong InstanceAddress = MemoryBase + 0x108;
    private const ulong BatchInfoAddress = MemoryBase + 0x180;
    private const ulong ControlInputAddress = MemoryBase + 0x200;
    private const ulong ResultAddress = MemoryBase + 0x300;
    private const ulong StackAddress = MemoryBase + 0x400;
    private const ulong BatchBufferAddress = MemoryBase + 0x800;
    private const ulong BatchBufferSize = 0x400;

    // Stereo 48 kHz ATRAC9: 384-byte superframes of four 256-sample frames.
    private static readonly byte[] StereoConfig = [0xFE, 0x74, 0x0B, 0xF0];

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x2000);
    private readonly CpuContext _ctx;

    public AjmBatchJobExportsTests()
    {
        AjmExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ControlInitializesResetsAndSetsGaplessThenStateJobsReportIt()
    {
        var instance = CreateInstance(codec: 1, flags: 0x401);
        InitializeBatch();

        // Sideband input: SceAjmSidebandGaplessDecode, then the AT9 initialize
        // parameters { configData[4], reserved }.
        Span<byte> input = stackalloc byte[16];
        input.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(input, 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(input[4..], 256);
        StereoConfig.CopyTo(input[8..]);
        Assert.True(_memory.TryWrite(ControlInputAddress, input));

        Fill(ResultAddress, 8, 0xCC);
        Assert.Equal(0, Control(instance, ControlReset | ControlInitialize | SidebandGapless, ControlInputAddress, 16, 8));
        Assert.Equal(0, ReadInt32(ResultAddress));
        Assert.Equal(0, ReadInt32(ResultAddress + 4));
        Assert.Equal(48UL, ReadUInt64(BatchInfoAddress + 8));

        // GetInfo: Result + SceAjmSidebandFormat.
        Fill(ResultAddress, 0x40, 0xCC);
        Assert.Equal(0, StateJob(AjmExports.AjmBatchJobGetInfo, instance));
        Assert.Equal(0, ReadInt32(ResultAddress));
        Assert.Equal(2u, ReadUInt32(ResultAddress + 8));      // numChannels
        Assert.Equal(3u, ReadUInt32(ResultAddress + 12));     // channelMask (FL|FR)
        Assert.Equal(48000u, ReadUInt32(ResultAddress + 16)); // sampleRate
        Assert.Equal(0xCCu, ReadByte(ResultAddress + 32));    // nothing past the 32-byte sideband
        Assert.Equal(48UL + 32, ReadUInt64(BatchInfoAddress + 8));

        // GetGaplessDecode: Result + SceAjmSidebandGaplessDecode.
        Fill(ResultAddress, 0x40, 0xCC);
        Assert.Equal(0, StateJob(AjmExports.AjmBatchJobGetGaplessDecode, instance));
        Assert.Equal(0, ReadInt32(ResultAddress));
        Assert.Equal(4096u, ReadUInt32(ResultAddress + 8));
        Assert.Equal((ushort)256, ReadUInt16(ResultAddress + 12));
        Assert.Equal((ushort)0, ReadUInt16(ResultAddress + 14));
        Assert.Equal(0xCCu, ReadByte(ResultAddress + 16));

        // GetCodecInfo: Result + SceAjmSidebandDecAt9CodecInfo.
        Fill(ResultAddress, 0x40, 0xCC);
        _ctx[CpuRegister.Rcx] = 24;
        Assert.Equal(0, StateJob(AjmExports.AjmBatchJobGetCodecInfo, instance, keepRcx: true));
        Assert.Equal(0, ReadInt32(ResultAddress));
        Assert.Equal(384u, ReadUInt32(ResultAddress + 8));  // superFrameSize
        Assert.Equal(4u, ReadUInt32(ResultAddress + 12));   // framesInSuperFrame
        Assert.Equal(384u, ReadUInt32(ResultAddress + 16)); // nextFrameSize
        Assert.Equal(256u, ReadUInt32(ResultAddress + 20)); // frameSamples
        Assert.Equal(0xCCu, ReadByte(ResultAddress + 24));
    }

    [Fact]
    public void ControlReportsInvalidJobsThroughTheSidebandAndBadPointersAsErrors()
    {
        var instance = CreateInstance(codec: 1, flags: 0x401);
        InitializeBatch();

        Assert.Equal(InvalidParameter, Control(instance, ControlReset, 0, 8, 8));
        Assert.Equal(0UL, ReadUInt64(BatchInfoAddress + 8));

        Assert.Equal(0, Control(instance, 0, 0, 0, 8));
        Assert.Equal(ResultInvalidParameter, ReadInt32(ResultAddress));

        Assert.Equal(0, Control(instance, ControlReset | (1UL << 3), 0, 0, 8));
        Assert.Equal(ResultInvalidParameter, ReadInt32(ResultAddress));

        // INITIALIZE without the 8-byte parameters.
        Assert.Equal(0, Control(instance, ControlInitialize, ControlInputAddress, 4, 8));
        Assert.Equal(ResultInvalidParameter, ReadInt32(ResultAddress));

        Assert.Equal(0, Control(instance + 1, ControlReset, 0, 0, 8));
        Assert.Equal(ResultInvalidParameter, ReadInt32(ResultAddress));

        Assert.Equal(0, Control(instance, ControlReset, 0, 0, 8));
        Assert.Equal(0, ReadInt32(ResultAddress));
    }

    [Fact]
    public void ResampleParametersValidateTheRatioAndRejectUnsupportedResampling()
    {
        var instance = CreateInstance(codec: 1, flags: 0x401);
        InitializeBatch();

        Assert.Equal(InvalidParameter, SetResample(instance, float.NaN));
        Assert.Equal(InvalidParameter, SetResample(instance, 0.0f));
        Assert.Equal(InvalidParameter, SetResample(instance, -1.0f));
        Assert.Equal(0UL, ReadUInt64(BatchInfoAddress + 8));

        Fill(ResultAddress, 8, 0xCC);
        Assert.Equal(0, SetResample(instance, 1.0f));
        Assert.Equal(0, ReadInt32(ResultAddress));

        Assert.Equal(0, SetResample(instance, 2.0f));
        Assert.Equal(ResultInvalidParameter, ReadInt32(ResultAddress));

        Fill(ResultAddress, 0x40, 0xCC);
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instance;
        _ctx[CpuRegister.Rdx] = ResultAddress;
        Assert.Equal(0, AjmExports.AjmBatchJobGetResampleInfo(_ctx));
        Assert.Equal(0, ReadInt32(ResultAddress));
        Assert.Equal(1.0f, BitConverter.Int32BitsToSingle(ReadInt32(ResultAddress + 8)));
        Assert.Equal(0, ReadInt32(ResultAddress + 12));
        Assert.Equal(0xCCu, ReadByte(ResultAddress + 48));
    }

    [Fact]
    public void EncodeCompletesWithAnInvalidParameterResultAndNothingProduced()
    {
        var instance = CreateInstance(codec: 1, flags: 0x401);
        InitializeBatch();
        Fill(ResultAddress, 0x20, 0xCC);

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instance;
        _ctx[CpuRegister.Rdx] = ControlInputAddress;
        _ctx[CpuRegister.Rcx] = 16;
        _ctx[CpuRegister.R8] = ControlInputAddress + 0x40;
        _ctx[CpuRegister.R9] = 16;
        WriteStackArg(0, ResultAddress);

        Assert.Equal(0, AjmExports.AjmBatchJobEncode(_ctx));
        Assert.Equal(ResultInvalidParameter, ReadInt32(ResultAddress));
        Assert.Equal(0, ReadInt32(ResultAddress + 8));  // inputConsumed
        Assert.Equal(0, ReadInt32(ResultAddress + 12)); // outputWritten
        Assert.Equal(0xCCu, ReadByte(ResultAddress + 24));
    }

    [Fact]
    public void DecodeSplitWritesOnlyTheResultAndStreamSideband()
    {
        const ulong inputDescriptors = MemoryBase + 0x500;
        const ulong outputDescriptors = MemoryBase + 0x540;
        const ulong inputData = MemoryBase + 0x600;
        const ulong outputData = MemoryBase + 0x700;

        var instance = CreateInstance(codec: 2, flags: 0x2_0000_0002);
        InitializeBatch();
        WriteUInt64(inputDescriptors, inputData);
        WriteUInt64(inputDescriptors + 8, 0x20);
        WriteUInt64(outputDescriptors, outputData);
        WriteUInt64(outputDescriptors + 8, 0x40);
        Fill(ResultAddress, 0x40, 0xCC);

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instance;
        _ctx[CpuRegister.Rdx] = inputDescriptors;
        _ctx[CpuRegister.Rcx] = 1;
        _ctx[CpuRegister.R8] = outputDescriptors;
        _ctx[CpuRegister.R9] = 1;
        WriteStackArg(0, ResultAddress);

        Assert.Equal(0, AjmExports.AjmBatchJobDecodeSplit(_ctx));
        Assert.Equal(0, ReadInt32(ResultAddress));
        Assert.Equal(0x20, ReadInt32(ResultAddress + 8));
        Assert.Equal(0xCCu, ReadByte(ResultAddress + 24));
        // SCE_AJM_JOB_RUN_SPLIT_SIZE(2).
        Assert.Equal(64UL, ReadUInt64(BatchInfoAddress + 8));
    }

    [Fact]
    public void StateJobsOnAnUnknownInstanceReportInvalidParameter()
    {
        InitializeBatch();
        Fill(ResultAddress, 0x40, 0xCC);

        Assert.Equal(0, StateJob(AjmExports.AjmBatchJobGetInfo, 0x4001));
        Assert.Equal(ResultInvalidParameter, ReadInt32(ResultAddress));
    }

    [Theory]
    [InlineData(unchecked((int)0x80930002), "SCE_AJM_ERROR_INVALID_CONTEXT")]
    [InlineData(unchecked((int)0x80930005), "SCE_AJM_ERROR_INVALID_PARAMETER")]
    [InlineData(unchecked((int)0x80930012), "SCE_AJM_ERROR_JOB_CREATION")]
    [InlineData(unchecked((int)0x80930015), "SCE_AJM_ERROR_CANCELLED")]
    [InlineData(0, null)]
    public void StrErrorNamesTheAjmErrorCodes(int code, string? name) =>
        Assert.Equal(name, AjmExports.GetErrorName(code));

    public void Dispose() => AjmExports.ResetForTests();

    private uint CreateInstance(uint codec, ulong flags)
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ContextAddress;
        Assert.Equal(0, AjmExports.AjmInitialize(_ctx));
        var contextId = ReadUInt32(ContextAddress);

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codec;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AjmExports.AjmModuleRegister(_ctx));

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codec;
        _ctx[CpuRegister.Rdx] = flags;
        _ctx[CpuRegister.Rcx] = InstanceAddress;
        Assert.Equal(0, AjmExports.AjmInstanceCreate(_ctx));
        return ReadUInt32(InstanceAddress);
    }

    private void InitializeBatch()
    {
        _ctx[CpuRegister.Rdi] = BatchBufferAddress;
        _ctx[CpuRegister.Rsi] = BatchBufferSize;
        _ctx[CpuRegister.Rdx] = BatchInfoAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));
    }

    private int Control(uint instance, ulong flags, ulong input, ulong inputSize, ulong outputSize)
    {
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instance;
        _ctx[CpuRegister.Rdx] = flags;
        _ctx[CpuRegister.Rcx] = input;
        _ctx[CpuRegister.R8] = inputSize;
        _ctx[CpuRegister.R9] = ResultAddress;
        WriteStackArg(0, outputSize);
        return AjmExports.AjmBatchJobControl(_ctx);
    }

    private int StateJob(Func<CpuContext, int> export, uint instance, bool keepRcx = false)
    {
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instance;
        _ctx[CpuRegister.Rdx] = ResultAddress;
        if (!keepRcx)
        {
            _ctx[CpuRegister.Rcx] = 0;
        }

        return export(_ctx);
    }

    private int SetResample(uint instance, float ratio)
    {
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instance;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = ResultAddress;
        _ctx.SetXmmRegister(0, unchecked((uint)BitConverter.SingleToInt32Bits(ratio)), 0);
        return AjmExports.AjmBatchJobSetResampleParameters(_ctx);
    }

    private void WriteStackArg(int index, ulong value)
    {
        _ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(StackAddress + 8 + ((ulong)index * 8), value);
    }

    private void Fill(ulong address, int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private byte ReadByte(ulong address)
    {
        Span<byte> value = stackalloc byte[1];
        Assert.True(_memory.TryRead(address, value));
        return value[0];
    }

    private ushort ReadUInt16(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ushort)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt16LittleEndian(value);
    }

    private int ReadInt32(ulong address) => unchecked((int)ReadUInt32(address));

    private uint ReadUInt32(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }
}
