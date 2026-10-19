// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Audio;
using SharpEmu.Libs.Pad;
using SharpEmu.Libs.Tests.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[Collection(AudioOutStateCollection.Name)]
public sealed class AudioOut2ChatAndHapticsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextParamAddress = MemoryBase + 0x100;
    private const ulong ContextOutAddress = MemoryBase + 0x200;
    private const ulong PortParamAddress = MemoryBase + 0x300;
    private const ulong PortOutAddress = MemoryBase + 0x400;
    private const ulong AttributeAddress = MemoryBase + 0x500;
    private const ulong PcmAttributeValueAddress = MemoryBase + 0x600;
    private const ulong ContextMemoryAddress = MemoryBase + 0x1000;
    private const ulong PcmAddress = MemoryBase + 0x4000;
    private const uint Grain = 64;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10000);
    private readonly CpuContext _ctx;
    private readonly RecordingHostInput _input = new();
    private readonly List<RecordingAudioStream> _streams = [];

    public AudioOut2ChatAndHapticsTests()
    {
        AudioOut2Exports.ResetForTests();
        AudioOut2Exports.SetStreamFactoryForTests(_ =>
        {
            var stream = new RecordingAudioStream();
            _streams.Add(stream);
            return stream;
        });
        PadHaptics.ResetForTests();
        PadHaptics.SetHostInputForTests(_input);
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose()
    {
        AudioOut2Exports.ResetForTests();
        PadHaptics.ResetForTests();
    }

    [Fact]
    public void EnableAndDisableChat_AreRepeatableAndRecorded()
    {
        Assert.Equal(0, AudioOut2Exports.AudioOut2EnableChat(_ctx));
        Assert.Equal(0, AudioOut2Exports.AudioOut2EnableChat(_ctx));
        Assert.True(AudioOut2Exports.ChatEnabled);
        Assert.Equal(0, AudioOut2Exports.AudioOut2DisableChat(_ctx));
        Assert.Equal(0, AudioOut2Exports.AudioOut2DisableChat(_ctx));
        Assert.False(AudioOut2Exports.ChatEnabled);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void MasteringTerm_IsAcceptedForEachInit()
    {
        Assert.Equal(0, AudioOut2Exports.AudioOut2MasteringInit(_ctx));
        Assert.Equal(0, AudioOut2Exports.AudioOut2MasteringInit(_ctx));
        Assert.Equal(0, AudioOut2Exports.AudioOut2MasteringTerm(_ctx));
        Assert.Equal(0, AudioOut2Exports.AudioOut2MasteringTerm(_ctx));
    }

    [Fact]
    public void VibrationPort_DrivesPadHapticsAtPushWithoutReachingSpeakers()
    {
        var context = CreateContext();
        var port = CreatePort(context, portType: 6);

        // S16 stereo grain: left actuator RMS 0.25 (128), right silent.
        var pcm = new byte[Grain * 2 * sizeof(short)];
        for (var frame = 0; frame < Grain; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 4), (short)(frame % 2 == 0 ? 8192 : -8192));
        }

        Assert.True(_memory.TryWrite(PcmAddress, pcm));
        SetPcmAttribute(port);
        Push(context);

        Assert.Equal([((byte)128, (byte)0)], _input.Rumble);
        Assert.All(_streams, stream => Assert.Empty(stream.Submissions));

        _ctx[CpuRegister.Rdi] = port;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortDestroy(_ctx));
        Assert.Equal(((byte)0, (byte)0), _input.Rumble[^1]);
    }

    [Fact]
    public void MainPort_DoesNotDriveHaptics()
    {
        var context = CreateContext();
        var port = CreatePort(context, portType: 0);
        Assert.True(_memory.TryWrite(PcmAddress, Enumerable.Repeat((byte)0x40, (int)Grain * 4).ToArray()));
        SetPcmAttribute(port);
        Push(context);

        Assert.Empty(_input.Rumble);
    }

    private ulong CreateContext()
    {
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], Grain);
        Assert.True(_memory.TryWrite(ContextParamAddress, param));

        _ctx[CpuRegister.Rdi] = ContextParamAddress;
        _ctx[CpuRegister.Rsi] = ContextMemoryAddress;
        _ctx[CpuRegister.Rdx] = 0x2000;
        _ctx[CpuRegister.Rcx] = ContextOutAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(_ctx));
        return ReadUInt64(ContextOutAddress);
    }

    private ulong CreatePort(ulong context, ushort portType)
    {
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(param, portType);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 0x201);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        Assert.True(_memory.TryWrite(PortParamAddress, param));

        _ctx[CpuRegister.Rdi] = context;
        _ctx[CpuRegister.Rsi] = PortParamAddress;
        _ctx[CpuRegister.Rdx] = PortOutAddress;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortCreate(_ctx));
        return ReadUInt64(PortOutAddress);
    }

    private void SetPcmAttribute(ulong port)
    {
        Span<byte> pcm = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(pcm, PcmAddress);
        Assert.True(_memory.TryWrite(PcmAttributeValueAddress, pcm));

        Span<byte> attribute = stackalloc byte[0x18];
        attribute.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(attribute[0x08..], PcmAttributeValueAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute[0x10..], 8);
        Assert.True(_memory.TryWrite(AttributeAddress, attribute));

        _ctx[CpuRegister.Rdi] = port;
        _ctx[CpuRegister.Rsi] = AttributeAddress;
        _ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortSetAttributes(_ctx));
    }

    private void Push(ulong context)
    {
        _ctx[CpuRegister.Rdi] = context;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(_ctx));
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[8];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private sealed class RecordingAudioStream : IHostAudioStream
    {
        public List<byte[]> Submissions { get; } = [];

        public bool Submit(ReadOnlySpan<byte> stereoPcm16)
        {
            Submissions.Add(stereoPcm16.ToArray());
            return true;
        }

        public void Dispose()
        {
        }
    }
}
