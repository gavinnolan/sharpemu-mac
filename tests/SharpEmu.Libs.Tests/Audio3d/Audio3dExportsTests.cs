// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Audio3d;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio3d;

[CollectionDefinition("Audio3dState", DisableParallelization = true)]
public sealed class Audio3dStateCollection
{
}

[Collection("Audio3dState")]
public sealed class Audio3dExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong ParamsAddress = Base + 0x100;
    private const ulong OutAddress = Base + 0x200;
    private const ulong OutAddress2 = Base + 0x210;
    private const ulong DataAddress = Base + 0x1000;
    private const ulong AttributeAddress = Base + 0x400;
    private const ulong PcmStructAddress = Base + 0x500;
    private const ulong ObjectDataAddress = Base + 0x2000;

    private const int InvalidPort = unchecked((int)0x80EA0002);
    private const int InvalidObject = unchecked((int)0x80EA0003);
    private const int InvalidParameter = unchecked((int)0x80EA0004);
    private const int OutOfResources = unchecked((int)0x80EA0006);
    private const int NotReady = unchecked((int)0x80EA0007);

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;
    private readonly List<RecordingStream> _streams = [];
    private long _now;
    private readonly List<long> _waits = [];

    public Audio3dExportsTests()
    {
        Audio3dExports.ResetForTests();
        Audio3dExports.SetStreamFactoryForTests(_ =>
        {
            var stream = new RecordingStream();
            _streams.Add(stream);
            return stream;
        });
        // 48000 ticks per second: one tick per sample, so a 256-sample frame is 256 ticks.
        Audio3dExports.SetClockForTests(() => _now, 48000, target => _waits.Add(target));
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => Audio3dExports.ResetForTests();

    [Fact]
    public void GetDefaultOpenParameters_WritesDefaultsAndLeavesNumBeds()
    {
        Fill(ParamsAddress, 0xCC, 0x30);
        var result = Call(Audio3dExports.Audio3dGetDefaultOpenParameters, ParamsAddress);

        Assert.Equal(0, result);
        Assert.Equal(0x20UL, ReadU64(ParamsAddress));
        Assert.Equal(256U, ReadU32(ParamsAddress + 0x08));
        Assert.Equal(0U, ReadU32(ParamsAddress + 0x0C));
        Assert.Equal(512U, ReadU32(ParamsAddress + 0x10));
        Assert.Equal(2U, ReadU32(ParamsAddress + 0x14));
        Assert.Equal(2U, ReadU32(ParamsAddress + 0x18));
        Assert.Equal(0U, ReadU32(ParamsAddress + 0x1C));
        Assert.Equal(0xCCCCCCCCU, ReadU32(ParamsAddress + 0x20));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dGetDefaultOpenParameters, 0));
    }

    [Fact]
    public void Initialize_RejectsReservedAndPortOpenNeedsInitialize()
    {
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dInitialize, 1));
        WriteParams(0x20, 256, 0, 16, 2, 2);
        Assert.Equal(NotReady, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, OutAddress));

        Assert.Equal(0, Call(Audio3dExports.Audio3dInitialize, 0));
        Assert.Equal(0, Call(Audio3dExports.Audio3dInitialize, 0));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, OutAddress));
        Assert.Equal(0U, ReadU32(OutAddress));
    }

    [Theory]
    [InlineData(0x18UL, 256U, 0U, 16U, 2U, 2U)]
    [InlineData(0x20UL, 256U, 1U, 16U, 2U, 2U)]
    [InlineData(0x20UL, 100U, 0U, 16U, 2U, 2U)]
    [InlineData(0x20UL, 0U, 0U, 16U, 2U, 2U)]
    [InlineData(0x20UL, 256U, 0U, 0U, 2U, 2U)]
    [InlineData(0x20UL, 256U, 0U, 16U, 0U, 2U)]
    [InlineData(0x20UL, 256U, 0U, 16U, 2U, 3U)]
    public void PortOpen_RejectsInvalidParameters(
        ulong size, uint granularity, uint rate, uint objects, uint depth, uint mode)
    {
        Initialize();
        WriteParams(size, granularity, rate, objects, depth, mode);
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, OutAddress));
    }

    [Fact]
    public void PortOpen_ValidatesBedCountAndPointers()
    {
        Initialize();
        WriteParams(0x28, 256, 0, 16, 2, 2, numBeds: 4);
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, OutAddress));
        WriteParams(0x28, 256, 0, 16, 2, 2, numBeds: 3);
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, OutAddress));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortOpen, 0xFF, 0, OutAddress));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, 0));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortOpen, -1, ParamsAddress, OutAddress));
    }

    [Fact]
    public void PortOpen_RunsOutOfPortsAndFreesSlots()
    {
        Initialize();
        for (uint i = 0; i < Audio3dExports.MaxPorts; i++)
        {
            Assert.Equal(i, OpenPort());
        }

        WriteDefaultParams();
        Assert.Equal(OutOfResources, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, OutAddress));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortClose, 1));
        Assert.Equal(1U, OpenPort());
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dPortClose, 99));
    }

    [Fact]
    public void Terminate_RequiresInitializeAndNoOpenPorts()
    {
        Assert.Equal(NotReady, Call(Audio3dExports.Audio3dTerminate));
        Initialize();
        var port = OpenPort();
        Assert.Equal(NotReady, Call(Audio3dExports.Audio3dTerminate));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortClose, port));
        Assert.True(_streams[0].IsDisposed);
        Assert.Equal(0, Call(Audio3dExports.Audio3dTerminate));
        Assert.Equal(NotReady, Call(Audio3dExports.Audio3dTerminate));
    }

    [Fact]
    public void ClosedPortRejectsEveryCall()
    {
        Initialize();
        var port = OpenPort();
        Call(Audio3dExports.Audio3dPortClose, port);

        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dPortAdvance, port));
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dPortPush, port, 0));
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dPortFlush, port));
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dPortGetQueueLevel, port, OutAddress, 0));
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dObjectReserve, port, OutAddress));
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dBedWrite, port, 2, 0, DataAddress, 256));
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dPortSetAttribute, port, 0x10001, DataAddress, 4));
    }

    [Fact]
    public void ObjectReserve_AssignsIdsEnforcesLimitAndWritesInvalidMarker()
    {
        Initialize();
        var port = OpenPort(maxObjects: 2);

        Assert.Equal(0, Call(Audio3dExports.Audio3dObjectReserve, port, OutAddress));
        Assert.Equal(1U, ReadU32(OutAddress));
        Assert.Equal(0, Call(Audio3dExports.Audio3dObjectReserve, port, OutAddress2));
        Assert.Equal(2U, ReadU32(OutAddress2));
        Assert.Equal(OutOfResources, Call(Audio3dExports.Audio3dObjectReserve, port, OutAddress));
        Assert.Equal(0xFFFFFFFFU, ReadU32(OutAddress));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dObjectReserve, port, 0));

        Assert.Equal(0, Call(Audio3dExports.Audio3dObjectUnreserve, port, 1));
        Assert.Equal(InvalidObject, Call(Audio3dExports.Audio3dObjectUnreserve, port, 1));
        Assert.Equal(0, Call(Audio3dExports.Audio3dObjectReserve, port, OutAddress));
        Assert.Equal(3U, ReadU32(OutAddress));
    }

    [Fact]
    public void QueueLevel_TracksAdvancePushAndTime()
    {
        Initialize();
        var port = OpenPort(depth: 2);

        Assert.Equal(0, Call(Audio3dExports.Audio3dPortGetQueueLevel, port, OutAddress, OutAddress2));
        Assert.Equal((0U, 2U), (ReadU32(OutAddress), ReadU32(OutAddress2)));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortGetQueueLevel, port, 0, 0));

        Assert.Equal(0, Call(Audio3dExports.Audio3dPortAdvance, port));
        Call(Audio3dExports.Audio3dPortGetQueueLevel, port, OutAddress, OutAddress2);
        Assert.Equal((1U, 1U), (ReadU32(OutAddress), ReadU32(OutAddress2)));

        Assert.Equal(0, Call(Audio3dExports.Audio3dPortPush, port, 0));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortAdvance, port));
        Assert.Equal(NotReady, Call(Audio3dExports.Audio3dPortAdvance, port));
        Call(Audio3dExports.Audio3dPortGetQueueLevel, port, OutAddress, 0);
        Assert.Equal(2U, ReadU32(OutAddress));

        _now = 256;
        Call(Audio3dExports.Audio3dPortGetQueueLevel, port, OutAddress, OutAddress2);
        Assert.Equal((1U, 1U), (ReadU32(OutAddress), ReadU32(OutAddress2)));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortFlush, port));
        Call(Audio3dExports.Audio3dPortGetQueueLevel, port, OutAddress, OutAddress2);
        Assert.Equal((0U, 2U), (ReadU32(OutAddress), ReadU32(OutAddress2)));
    }

    [Fact]
    public void Push_RejectsUnknownBlockingMode_AndSyncWaitsForQueueRoom()
    {
        Initialize();
        var port = OpenPort(depth: 2);
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortPush, port, 2));

        Call(Audio3dExports.Audio3dPortAdvance, port);
        Call(Audio3dExports.Audio3dPortPush, port, 1);
        Assert.Empty(_waits);

        Call(Audio3dExports.Audio3dPortAdvance, port);
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortPush, port, 1));
        // Two 256-sample frames are playing: wait until the first has finished.
        Assert.Equal([256L], _waits);
    }

    [Fact]
    public void BedWrite_StereoPassesThroughToHost()
    {
        Initialize();
        var port = OpenPort();
        var samples = new short[256 * 2];
        samples[0] = 1000;
        samples[1] = -2000;
        samples[2] = 32767;
        samples[3] = -32768;
        WriteShorts(DataAddress, samples);

        Assert.Equal(0, Call(Audio3dExports.Audio3dBedWrite, port, 2, 0, DataAddress, 256));
        Call(Audio3dExports.Audio3dPortAdvance, port);
        Call(Audio3dExports.Audio3dPortPush, port, 0);

        var frame = Assert.Single(_streams[0].Submissions);
        Assert.Equal(256 * 4, frame.Length);
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(frame));
        Assert.Equal(-2000, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(2)));
        // s16 -> float (/32768) -> s16 (*32767) loses one LSB at full scale.
        Assert.Equal(32766, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(4)));
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(6)));
    }

    [Fact]
    public void BedWrite_DownmixesSurroundAndFloat()
    {
        Initialize();
        var port = OpenPort();
        // 5.1 float: only the centre channel is non-zero (0.5).
        var surround = new float[256 * 6];
        surround[2] = 0.5f;
        WriteFloats(DataAddress, surround);
        Assert.Equal(0, Call(Audio3dExports.Audio3dBedWrite2, port, 6, 1, DataAddress, 256, 0, 0));
        Call(Audio3dExports.Audio3dPortAdvance, port);
        Call(Audio3dExports.Audio3dPortPush, port, 0);

        var frame = _streams[0].Submissions[0];
        var expected = (short)Math.Round(0.5f * 0.70710677f * 32767f);
        Assert.Equal(expected, BinaryPrimitives.ReadInt16LittleEndian(frame));
        Assert.Equal(expected, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(2)));
    }

    [Fact]
    public void BedWrite_RejectsInvalidArguments()
    {
        Initialize();
        var port = OpenPort();
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dBedWrite, port, 4, 0, DataAddress, 256));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dBedWrite, port, 2, 2, DataAddress, 256));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dBedWrite, port, 2, 0, 0, 256));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dBedWrite, port, 2, 0, DataAddress, 0));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dBedWrite, port, 2, 0, DataAddress, 257));
    }

    [Fact]
    public void ObjectPcm_IsMixedCentredAndClearedAfterAdvance()
    {
        Initialize();
        var port = OpenPort();
        Call(Audio3dExports.Audio3dObjectReserve, port, OutAddress);
        var objectId = ReadU32(OutAddress);

        var mono = new short[256];
        mono[0] = 16384;
        WriteShorts(ObjectDataAddress, mono);
        // SceAudio3dPcm: format s16, samples, count.
        WriteU32(PcmStructAddress, 0);
        WriteU64(PcmStructAddress + 8, ObjectDataAddress);
        WriteU32(PcmStructAddress + 0x10, 256);
        // SceAudio3dAttribute: id 1 (PCM), value pointer, size.
        WriteU32(AttributeAddress, 1);
        WriteU64(AttributeAddress + 8, PcmStructAddress);
        WriteU64(AttributeAddress + 0x10, 0x18);

        Assert.Equal(0, Call(Audio3dExports.Audio3dObjectSetAttributes, port, objectId, 1, AttributeAddress));
        Call(Audio3dExports.Audio3dPortAdvance, port);
        Call(Audio3dExports.Audio3dPortPush, port, 0);
        var expected = (short)Math.Round(0.5f * 0.70710677f * 32767f);
        var first = _streams[0].Submissions[0];
        Assert.Equal(expected, BinaryPrimitives.ReadInt16LittleEndian(first));
        Assert.Equal(expected, BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(2)));

        // The next frame has no new PCM: silence.
        _now = 1000;
        Call(Audio3dExports.Audio3dPortAdvance, port);
        Call(Audio3dExports.Audio3dPortPush, port, 0);
        Assert.All(_streams[0].Submissions[1], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ObjectSetAttributes_ValidatesArguments()
    {
        Initialize();
        var port = OpenPort();
        Call(Audio3dExports.Audio3dObjectReserve, port, OutAddress);
        var objectId = ReadU32(OutAddress);
        WriteU32(AttributeAddress, 1);
        WriteU64(AttributeAddress + 8, 0);
        WriteU32(AttributeAddress + 0x18, 0x20000);
        WriteU64(AttributeAddress + 0x20, 0);

        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dObjectSetAttributes, port, objectId, 0, AttributeAddress));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dObjectSetAttributes, port, objectId, 1, 0));
        Assert.Equal(InvalidObject, Call(Audio3dExports.Audio3dObjectSetAttributes, port, 77, 1, AttributeAddress));
        // PCM with a null value is invalid; the reset-state attribute needs none.
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dObjectSetAttributes, port, objectId, 1, AttributeAddress));
        Assert.Equal(0, Call(Audio3dExports.Audio3dObjectSetAttributes, port, objectId, 1, AttributeAddress + 0x18));
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dObjectSetAttributes, 9, objectId, 1, AttributeAddress));
    }

    [Fact]
    public void PortSetAttribute_AcceptsKnownAttributesOnly()
    {
        Initialize();
        var port = OpenPort();
        WriteU32(DataAddress, BitConverter.SingleToUInt32Bits(0.5f));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortSetAttribute, port, 0x10001, DataAddress, 4));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortSetAttribute, port, 0x10002, DataAddress, 4));
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortSetAttribute, port, 0x10003, DataAddress, 4));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortSetAttribute, port, 0x10004, DataAddress, 4));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortSetAttribute, port, 0x10001, DataAddress, 8));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortSetAttribute, port, 0x10001, 0, 4));
    }

    [Fact]
    public void GetAttributesSupported_ReportsPortAttributes()
    {
        Initialize();
        var port = OpenPort();
        WriteU32(OutAddress, 8);
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortGetAttributesSupported, port, DataAddress, OutAddress));
        Assert.Equal(3U, ReadU32(OutAddress));
        Assert.Equal(0x10001U, ReadU32(DataAddress));
        Assert.Equal(0x10003U, ReadU32(DataAddress + 8));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dPortGetAttributesSupported, port, DataAddress, 0));
    }

    [Fact]
    public void AudioOut_OpenOutputClose()
    {
        Initialize();
        var port = OpenPort();
        // Stack argument 0 carries the format (S16 stereo = 1).
        _ctx.SetImportStackArguments(1, 0, 0, 0, 0, 0);
        var handle = Call(
            Audio3dExports.Audio3dAudioOutOpen, port, 0xFF, 0, 0, 256, 48000);
        Assert.True(handle > 0);

        var pcm = new short[256 * 2];
        pcm[0] = 123;
        pcm[1] = -456;
        WriteShorts(DataAddress, pcm);
        // _streams[0] is the port's stream; the AudioOut handle owns _streams[1].
        Assert.Equal(256, Call(Audio3dExports.Audio3dAudioOutOutput, handle, DataAddress));
        var frame = Assert.Single(_streams[1].Submissions);
        Assert.Equal(123, BinaryPrimitives.ReadInt16LittleEndian(frame));
        Assert.Equal(-456, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(2)));

        WriteU32(OutAddress, (uint)handle);
        WriteU64(OutAddress + 8, DataAddress);
        Assert.Equal(256, Call(Audio3dExports.Audio3dAudioOutOutputs, OutAddress, 1));
        Assert.Equal(2, _streams[1].Submissions.Count);
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dAudioOutOutputs, OutAddress, 0));

        Assert.Equal(0, Call(Audio3dExports.Audio3dAudioOutClose, handle));
        Assert.True(_streams[1].IsDisposed);
        Assert.Equal(AudioOutInvalid, Call(Audio3dExports.Audio3dAudioOutClose, handle));
        Assert.Equal(AudioOutInvalid, Call(Audio3dExports.Audio3dAudioOutOutput, handle, DataAddress));
    }

    [Fact]
    public void AudioOutOpen_ValidatesPortRateAndFormat()
    {
        Initialize();
        var port = OpenPort();
        _ctx.SetImportStackArguments(1, 0, 0, 0, 0, 0);
        Assert.Equal(InvalidPort, Call(Audio3dExports.Audio3dAudioOutOpen, 9, 0xFF, 0, 0, 256, 48000));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dAudioOutOpen, port, 0xFF, 0, 0, 256, 44100));
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dAudioOutOpen, port, 0xFF, 0, 0, 0, 48000));
        _ctx.SetImportStackArguments(99, 0, 0, 0, 0, 0);
        Assert.Equal(InvalidParameter, Call(Audio3dExports.Audio3dAudioOutOpen, port, 0xFF, 0, 0, 256, 48000));
    }

    private const int AudioOutInvalid = unchecked((int)0x80260003);

    private void Initialize() => Assert.Equal(0, Call(Audio3dExports.Audio3dInitialize, 0));

    private uint OpenPort(uint granularity = 256, uint maxObjects = 16, uint depth = 2, uint mode = 2)
    {
        WriteParams(0x20, granularity, 0, maxObjects, depth, mode);
        Assert.Equal(0, Call(Audio3dExports.Audio3dPortOpen, 0xFF, ParamsAddress, OutAddress));
        return ReadU32(OutAddress);
    }

    private void WriteDefaultParams() => WriteParams(0x20, 256, 0, 16, 2, 2);

    private void WriteParams(
        ulong size, uint granularity, uint rate, uint objects, uint depth, uint mode, uint numBeds = 2)
    {
        var raw = new byte[0x28];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, size);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x08), granularity);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x0C), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x10), objects);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x14), depth);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x18), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x20), numBeds);
        Assert.True(_memory.TryWrite(ParamsAddress, raw));
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
        long l => unchecked((ulong)l),
        _ => throw new ArgumentException("unsupported argument type"),
    };

    private uint ReadU32(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private ulong ReadU64(ulong address)
    {
        Assert.True(_ctx.TryReadUInt64(address, out var value));
        return value;
    }

    private void WriteU32(ulong address, uint value) => Assert.True(_ctx.TryWriteUInt32(address, value));

    private void WriteU64(ulong address, ulong value) => Assert.True(_ctx.TryWriteUInt64(address, value));

    private void Fill(ulong address, byte value, int length) =>
        Assert.True(_memory.TryWrite(address, Enumerable.Repeat(value, length).ToArray()));

    private void WriteShorts(ulong address, short[] values)
    {
        var raw = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * 2), values[i]);
        }

        Assert.True(_memory.TryWrite(address, raw));
    }

    private void WriteFloats(ulong address, float[] values)
    {
        var raw = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), values[i]);
        }

        Assert.True(_memory.TryWrite(address, raw));
    }

    private sealed class RecordingStream : IHostAudioStream
    {
        public List<byte[]> Submissions { get; } = [];
        public bool IsDisposed { get; private set; }

        public bool Submit(ReadOnlySpan<byte> stereoPcm16)
        {
            Submissions.Add(stereoPcm16.ToArray());
            return true;
        }

        public void Dispose() => IsDisposed = true;
    }
}
