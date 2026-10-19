// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

[Collection("Ngs2State")]
public sealed class Ngs2InfoExportsTests : IDisposable
{
    private const int Ok = 0;
    private const int InvalidSystemHandle = unchecked((int)0x804A0230);
    private const int InvalidRackHandle = unchecked((int)0x804A0261);
    private const int InvalidOutAddress = unchecked((int)0x804A8010);
    private const int InvalidOutSize = unchecked((int)0x804A8011);
    private const int InvalidWaveformFormat = unchecked((int)0x804A8431);
    private const int UnknownWaveformFormat = unchecked((int)0x804A8432);
    private const int InvalidArgument = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;

    private readonly PhysicalVirtualMemory _memory = new();
    private readonly CpuContext _context;
    private readonly ulong _page;

    public Ngs2InfoExportsTests()
    {
        Ngs2Exports.ResetStateForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
        _page = _memory.AllocateAt(0, 0x4000, executable: false);
        Assert.NotEqual(0UL, _page);
    }

    public void Dispose()
    {
        Ngs2Exports.ResetStateForTests();
        _memory.Dispose();
    }

    [Fact]
    public void SystemGetInfo_ReportsOptionAndLiveState()
    {
        var system = CreateSystem("sfx-system", maxGrain: 1024);
        CreateRack(system, 0x1000, "voices", maxVoices: 32, channelWorks: 64);
        var info = _page + 0x1000;

        Assert.Equal(Ok, Ngs2Exports.Ngs2SystemGetInfo(Reg(rdi: system, rsi: info, rdx: 184)));

        Assert.Equal("sfx-system", ReadName(info));
        Assert.Equal(system, ReadU64(info + 64));
        Assert.Equal(1u, ReadU32(info + 136));
        Assert.Equal(64u, ReadU32(info + 140));
        Assert.Equal(1024u, ReadU32(info + 144));
        Assert.Equal(1u, ReadU32(info + 152));
        Assert.Equal(0UL, ReadU64(info + 168));
        Assert.Equal(48000u, ReadU32(info + 176));
        Assert.Equal(256u, ReadU32(info + 180));
    }

    [Fact]
    public void SystemGetInfo_RejectsBadArguments()
    {
        var system = CreateSystem("s", maxGrain: 512);
        var info = _page + 0x1000;

        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2SystemGetInfo(Reg(rdi: system, rsi: 0, rdx: 184)));
        Assert.Equal(InvalidOutSize, Ngs2Exports.Ngs2SystemGetInfo(Reg(rdi: system, rsi: info, rdx: 183)));
        Assert.Equal(InvalidSystemHandle, Ngs2Exports.Ngs2SystemGetInfo(Reg(rdi: 0x1234, rsi: info, rdx: 184)));
    }

    [Fact]
    public void RackGetInfo_ReportsSamplerLimits()
    {
        var system = CreateSystem("s", maxGrain: 512);
        var rack = CreateRack(system, 0x1000, "sampler", maxVoices: 32, channelWorks: 64);
        var info = _page + 0x1000;

        Assert.Equal(Ok, Ngs2Exports.Ngs2RackGetInfo(Reg(rdi: rack, rsi: info, rdx: 216)));

        Assert.Equal("sampler", ReadName(info));
        Assert.Equal(rack, ReadU64(info + 64));
        Assert.Equal(system, ReadU64(info + 136));
        Assert.Equal(1u, ReadU32(info + 144));
        Assert.Equal(0x1000u, ReadU32(info + 148));
        Assert.Equal(2u, ReadU32(info + 152));
        Assert.Equal(64u, ReadU32(info + 156));
        Assert.Equal(512u, ReadU32(info + 160));
        Assert.Equal(32u, ReadU32(info + 164));
        Assert.Equal(64u, ReadU32(info + 168));
        Assert.Equal(1u, ReadU32(info + 176));
        Assert.Equal(8u, ReadU32(info + 180));
        Assert.Equal(0u, ReadU32(info + 208));
    }

    [Fact]
    public void RackGetInfo_RejectsBadArguments()
    {
        var system = CreateSystem("s", maxGrain: 512);
        var rack = CreateRack(system, 0x3000, "master", maxVoices: 1, channelWorks: 0);
        var info = _page + 0x1000;

        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2RackGetInfo(Reg(rdi: rack, rsi: 0, rdx: 216)));
        Assert.Equal(InvalidOutSize, Ngs2Exports.Ngs2RackGetInfo(Reg(rdi: rack, rsi: info, rdx: 215)));
        Assert.Equal(InvalidRackHandle, Ngs2Exports.Ngs2RackGetInfo(Reg(rdi: 0x1234, rsi: info, rdx: 216)));
    }

    [Fact]
    public void UserData_RoundTripsAndValidatesHandles()
    {
        var system = CreateSystem("s", maxGrain: 512);
        var rack = CreateRack(system, 0x2000, "submix", maxVoices: 1, channelWorks: 0);
        var output = _page + 0x1000;

        Assert.Equal(Ok, Ngs2Exports.Ngs2SystemSetUserData(Reg(rdi: system, rsi: 0xABCDEF)));
        Assert.Equal(Ok, Ngs2Exports.Ngs2SystemGetUserData(Reg(rdi: system, rsi: output)));
        Assert.Equal(0xABCDEFUL, ReadU64(output));

        Assert.Equal(Ok, Ngs2Exports.Ngs2RackSetUserData(Reg(rdi: rack, rsi: 0x1122334455667788)));
        Assert.Equal(Ok, Ngs2Exports.Ngs2RackGetUserData(Reg(rdi: rack, rsi: output)));
        Assert.Equal(0x1122334455667788UL, ReadU64(output));

        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2RackGetUserData(Reg(rdi: rack, rsi: 0)));
        Assert.Equal(InvalidSystemHandle, Ngs2Exports.Ngs2SystemSetUserData(Reg(rdi: 0x1234, rsi: 1)));
        Assert.Equal(InvalidSystemHandle, Ngs2Exports.Ngs2SystemGetUserData(Reg(rdi: 0x1234, rsi: output)));
        Assert.Equal(InvalidRackHandle, Ngs2Exports.Ngs2RackSetUserData(Reg(rdi: 0x1234, rsi: 1)));
        Assert.Equal(InvalidRackHandle, Ngs2Exports.Ngs2RackGetUserData(Reg(rdi: 0x1234, rsi: output)));
    }

    [Fact]
    public void RackLockAndUnlock_ValidateTheHandle()
    {
        var system = CreateSystem("s", maxGrain: 512);
        var rack = CreateRack(system, 0x1000, "sampler", maxVoices: 4, channelWorks: 4);

        Assert.Equal(Ok, Ngs2Exports.Ngs2RackLock(Reg(rdi: rack)));
        Assert.Equal(Ok, Ngs2Exports.Ngs2RackUnlock(Reg(rdi: rack)));
        Assert.Equal(InvalidRackHandle, Ngs2Exports.Ngs2RackLock(Reg(rdi: 0x1234)));
        Assert.Equal(InvalidRackHandle, Ngs2Exports.Ngs2RackUnlock(Reg(rdi: 0x1234)));
    }

    [Fact]
    public void SystemResetOption_WritesDefaults()
    {
        var option = _page + 0x1000;
        Fill(option, 144, 0xCC);

        Assert.Equal(Ok, Ngs2Exports.Ngs2SystemResetOption(Reg(rdi: option)));

        Assert.Equal(144UL, ReadU64(option));
        Assert.Equal(string.Empty, ReadName(option + 8));
        Assert.Equal(0u, ReadU32(option + 104));
        Assert.Equal(512u, ReadU32(option + 108));
        Assert.Equal(256u, ReadU32(option + 112));
        Assert.Equal(48000u, ReadU32(option + 116));
        Assert.Equal(0u, ReadU32(option + 120));
        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2SystemResetOption(Reg(rdi: 0)));
    }

    [Fact]
    public void PanInit_DefaultAndCustomLayouts()
    {
        var work = _page + 0x1000;

        Assert.Equal(Ok, PanInit(work, angles: null, unit: 360.0f, speakers: 2));
        Assert.Equal([90.0f, 270.0f, 450.0f], ReadFloats(work, 3));
        Assert.Equal(360.0f, ReadFloat(work + 32));
        Assert.Equal(2u, ReadU32(work + 36));

        // A unit other than degrees or radians falls back to degrees for the defaults.
        Assert.Equal(Ok, PanInit(work, angles: null, unit: 100.0f, speakers: 4));
        Assert.Equal(360.0f, ReadFloat(work + 32));
        Assert.Equal(400.0f, ReadFloat(work + 16));

        // Caller order is FL, FR, C, SL, SR; the work holds them clockwise from the centre.
        Assert.Equal(Ok, PanInit(work, [-30.0f, 30.0f, 0.0f, -110.0f, 470.0f], 360.0f, 5));
        Assert.Equal([0.0f, 30.0f, 110.0f, 250.0f, 330.0f, 360.0f], ReadFloats(work, 6));

        Assert.Equal(InvalidArgument, PanInit(work, angles: null, unit: 360.0f, speakers: 3));
        Assert.Equal(InvalidArgument, PanInit(work, [-30.0f, 30.0f], 180.0f, 2));
        Assert.Equal(InvalidArgument, PanInit(work, [720.0f, 30.0f], 360.0f, 2));
        Assert.Equal(Ok, PanInit(work, [-360.0f, 30.0f], 360.0f, 2));
        Assert.Equal(InvalidArgument, PanInit(work, [0.0f, float.NaN], 360.0f, 2));
        Assert.Equal(InvalidOutAddress, PanInit(0, angles: null, unit: 360.0f, speakers: 2));
    }

    [Fact]
    public void PanGetVolumeMatrix_StereoCircle()
    {
        var work = _page + 0x1000;
        Assert.Equal(Ok, PanInit(work, angles: null, unit: 360.0f, speakers: 2));

        var matrix = PanMatrix(
            work,
            [(0, 1, 1, 0), (45, 1, 1, 0), (90, 1, 1, 0), (135, 1, 1, 0),
             (180, 1, 1, 0), (225, 1, 1, 0), (270, 1, 1, 0), (315, 1, 1, 0)],
            matrixFormat: 2);

        // Left row then right row; 90 degrees is the right speaker.
        AssertNear(
            [0.70710677f, 0.38268343f, 0.0f, 0.38268343f, 0.70710677f, 0.9238795f, 1.0f, 0.9238795f,
             0.70710677f, 0.9238795f, 1.0f, 0.9238795f, 0.70710677f, 0.38268343f, 0.0f, 0.38268343f],
            matrix);
    }

    [Fact]
    public void PanGetVolumeMatrix_DistanceSpreadsTowardTheOppositeSide()
    {
        var work = _page + 0x1000;
        Assert.Equal(Ok, PanInit(work, angles: null, unit: 360.0f, speakers: 2));

        var matrix = PanMatrix(
            work,
            [(30, 0, 1, 0), (30, -1, 1, 0), (30, 0.5f, 0.5f, 0), (30, 3, 1, 0), (-30, -0.25f, 1, 0)],
            matrixFormat: 2);

        AssertNear(
            [0.9659258f, 0.86602545f, 0.39667666f, 0.5f, 0.89687276f,
             0.9659258f, 0.5f, 0.49572244f, 0.86602545f, 0.99785894f],
            matrix);
    }

    [Fact]
    public void PanGetVolumeMatrix_FoldsLayoutsIntoTheMatrixFormat()
    {
        var work = _page + 0x1000;

        Assert.Equal(Ok, PanInit(work, angles: null, unit: 360.0f, speakers: 4));
        AssertNear([0.0f, 0.22252092f, 0.0f, 0.25f, 0.0f, 0.9749279f], PanMatrix(work, [(100, 1, 1, 0.25f)], 6));
        AssertNear([0.0f, 0.7099849f], PanMatrix(work, [(100, 1, 1, 0.25f)], 2));

        Assert.Equal(Ok, PanInit(work, angles: null, unit: 360.0f, speakers: 5));
        AssertNear([0.6916666f], PanMatrix(work, [(70, 1, 0.8f, 0.5f)], 1));

        Assert.Equal(Ok, PanInit(work, angles: null, unit: 360.0f, speakers: 7));
        AssertNear(
            [0.0f, 0.0f, 0.0f, 0.25f, 0.0f, 0.0f, 0.70710677f, 0.70710677f],
            PanMatrix(work, [(180, 1, 1, 0.25f)], 8));
        AssertNear([0.0f, 0.0f, 0.0f, 0.25f, 0.70710677f, 0.70710677f], PanMatrix(work, [(180, 1, 1, 0.25f)], 6));
    }

    [Fact]
    public void PanGetVolumeMatrix_RejectsBadArgumentsWithoutWriting()
    {
        var work = _page + 0x1000;
        var parameters = _page + 0x1100;
        var output = _page + 0x1200;
        Assert.Equal(Ok, PanInit(work, angles: null, unit: 360.0f, speakers: 2));
        WriteParams(parameters, [(0, 1, 1, 0), (float.NaN, 1, 1, 0)]);
        Fill(output, 16, 0xCC);

        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2PanGetVolumeMatrix(Reg(rdi: work, rsi: parameters, rdx: 1, rcx: 2, r8: 0)));
        Assert.Equal(InvalidArgument, Ngs2Exports.Ngs2PanGetVolumeMatrix(Reg(rdi: work, rsi: parameters, rdx: 1, rcx: 4, r8: output)));
        Assert.Equal(InvalidArgument, Ngs2Exports.Ngs2PanGetVolumeMatrix(Reg(rdi: work, rsi: parameters, rdx: 9, rcx: 2, r8: output)));
        Assert.Equal(InvalidArgument, Ngs2Exports.Ngs2PanGetVolumeMatrix(Reg(rdi: 0, rsi: parameters, rdx: 1, rcx: 2, r8: output)));
        Assert.Equal(InvalidArgument, Ngs2Exports.Ngs2PanGetVolumeMatrix(Reg(rdi: work, rsi: parameters, rdx: 2, rcx: 2, r8: output)));
        Assert.All(ReadBytes(output, 16), b => Assert.Equal(0xCC, b));
    }

    [Fact]
    public void GeomCalcListener_BuildsTheWorldToListenerTransform()
    {
        var param = _page + 0x1000;
        var work = _page + 0x1100;

        Assert.Equal(Ok, Ngs2Exports.Ngs2GeomResetListenerParam(Reg(rdi: param)));
        Assert.Equal([0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 343.0f], ReadFloats(param, 13));

        WriteFloats(param, [3, 4, 5, 0, 0, 1, 0, 1, 0, 2, -3, 4, 1500]);
        Assert.Equal(Ok, Ngs2Exports.Ngs2GeomCalcListener(Reg(rdi: param, rsi: work, rdx: 0)));
        Assert.Equal([1, 0, 0, -3, 0, 1, 0, -4, 0, 0, 1, -5, 0, 0, 0, 1], ReadFloats(work, 16));
        Assert.Equal([2.0f, -3.0f, 4.0f, 1500.0f], ReadFloats(work + 64, 4));
        Assert.Equal(0u, ReadU32(work + 80));

        WriteFloats(param + 12, [2, 0, 0]);
        Assert.Equal(Ok, Ngs2Exports.Ngs2GeomCalcListener(Reg(rdi: param, rsi: work, rdx: 0)));
        Assert.Equal([0, 0, -1, 5, 0, 1, 0, -4, 1, 0, 0, -3, 0, 0, 0, 1], ReadFloats(work, 16));

        // Flag 1 (right-handed) mirrors the right axis.
        WriteFloats(param + 12, [0, 0, -2]);
        Assert.Equal(Ok, Ngs2Exports.Ngs2GeomCalcListener(Reg(rdi: param, rsi: work, rdx: 1)));
        Assert.Equal([1, 0, 0, -3, 0, 1, 0, -4, 0, 0, -1, 5, 0, 0, 0, 1], ReadFloats(work, 16));
        Assert.Equal(1u, ReadU32(work + 80));

        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2GeomCalcListener(Reg(rdi: param, rsi: 0, rdx: 0)));
        Assert.Equal(InvalidArgument, Ngs2Exports.Ngs2GeomCalcListener(Reg(rdi: param, rsi: work, rdx: 2)));
        Assert.Equal(InvalidArgument, Ngs2Exports.Ngs2GeomCalcListener(Reg(rdi: 0, rsi: work, rdx: 0)));
    }

    [Fact]
    public void GeomResetSourceParam_WritesDefaults()
    {
        var param = _page + 0x1000;
        Fill(param, 108, 0xCC);

        Assert.Equal(Ok, Ngs2Exports.Ngs2GeomResetSourceParam(Reg(rdi: param)));

        Assert.Equal(
            [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 360, 1, 360, 0, 1000000, 1, 1, 1, 1, 1, 1, 0, 0],
            ReadFloats(param, 23));
        Assert.Equal(2u, ReadU32(param + 92));
        Assert.Equal(2u, ReadU32(param + 96));
        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2GeomResetSourceParam(Reg(rdi: 0)));
    }

    [Fact]
    public void CalcWaveformBlock_Atrac9CoversWholeSuperframes()
    {
        var format = _page + 0x1000;
        var block = _page + 0x1100;
        WriteFormat(format, type: 0x40, channels: 2, rate: 48000, config: [0xFE, 0x74, 0x0B, 0xF0]);

        Assert.Equal(Ok, CalcBlock(format, 1500, 600, block));
        Assert.Equal(384UL, ReadU64(block));
        Assert.Equal(768UL, ReadU64(block + 8));
        Assert.Equal(476u, ReadU32(block + 20));
        Assert.Equal(600u, ReadU32(block + 24));

        Assert.Equal(Ok, CalcBlock(format, 1500, 0, block));
        Assert.Equal(384UL, ReadU64(block));
        Assert.Equal(0UL, ReadU64(block + 8));
        Assert.Equal(0u, ReadU32(block + 20));

        Assert.Equal(InvalidOutAddress, CalcBlock(format, 0, 1, 0));
        WriteFormat(format, type: 0x40, channels: 1, rate: 48000, config: [0xFE, 0x74, 0x0B, 0xF0]);
        Assert.Equal(InvalidWaveformFormat, CalcBlock(format, 0, 1, block));
        WriteFormat(format, type: 0x40, channels: 0, rate: 48000, config: [0xFE, 0x74, 0x0B, 0xF0]);
        Assert.Equal(InvalidWaveformFormat, CalcBlock(format, 0, 1, block));
    }

    [Fact]
    public void CalcWaveformBlock_PcmIsFrameAligned()
    {
        var format = _page + 0x1000;
        var block = _page + 0x1100;

        WriteFormat(format, type: 0x12, channels: 2, rate: 48000, config: [0, 0, 0, 0]);
        Assert.Equal(Ok, CalcBlock(format, 10, 5, block));
        Assert.Equal(40UL, ReadU64(block));
        Assert.Equal(20UL, ReadU64(block + 8));
        Assert.Equal(0u, ReadU32(block + 20));
        Assert.Equal(5u, ReadU32(block + 24));

        WriteFormat(format, type: 0x18, channels: 1, rate: 48000, config: [0, 0, 0, 0]);
        Assert.Equal(Ok, CalcBlock(format, 3, 2, block));
        Assert.Equal(12UL, ReadU64(block));
        Assert.Equal(8UL, ReadU64(block + 8));

        WriteFormat(format, type: 0x99, channels: 1, rate: 48000, config: [0, 0, 0, 0]);
        Assert.Equal(UnknownWaveformFormat, CalcBlock(format, 0, 1, block));
    }

    private ulong CreateSystem(string name, uint maxGrain)
    {
        var option = _page + 0x200;
        var output = _page + 0x100;
        Span<byte> bytes = stackalloc byte[144];
        bytes.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 144);
        Encoding.ASCII.GetBytes(name).CopyTo(bytes[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[108..], maxGrain);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[112..], 256);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[116..], 48000);
        Assert.True(_memory.TryWrite(option, bytes));

        Assert.Equal(Ok, Ngs2Exports.Ngs2SystemCreateWithAllocator(Reg(rdi: option, rdx: output)));
        return ReadU64(output);
    }

    private ulong CreateRack(ulong system, uint rackId, string name, uint maxVoices, uint channelWorks)
    {
        var option = _page + 0x400;
        var output = _page + 0x108;
        Span<byte> bytes = stackalloc byte[192];
        bytes.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 180);
        Encoding.ASCII.GetBytes(name).CopyTo(bytes[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[76..], 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[80..], maxVoices);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[88..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[92..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[176..], channelWorks);
        Assert.True(_memory.TryWrite(option, bytes));

        Assert.Equal(
            Ok,
            Ngs2Exports.Ngs2RackCreateWithAllocator(Reg(rdi: system, rsi: rackId, rdx: option, r8: output)));
        return ReadU64(output);
    }

    private int PanInit(ulong work, float[]? angles, float unit, uint speakers)
    {
        var anglesAddress = 0UL;
        if (angles is not null)
        {
            anglesAddress = _page + 0x800;
            WriteFloats(anglesAddress, angles);
        }

        var context = Reg(rdi: work, rsi: anglesAddress, rdx: speakers);
        context.SetXmmRegister(0, (uint)BitConverter.SingleToInt32Bits(unit), 0);
        return Ngs2Exports.Ngs2PanInit(context);
    }

    private float[] PanMatrix(
        ulong work,
        (float Angle, float Distance, float Fbw, float Lfe)[] parameters,
        uint matrixFormat)
    {
        var paramsAddress = _page + 0x900;
        var output = _page + 0xA00;
        WriteParams(paramsAddress, parameters);
        var count = parameters.Length * (int)matrixFormat;
        Fill(output, count * sizeof(float), 0xCC);

        Assert.Equal(
            Ok,
            Ngs2Exports.Ngs2PanGetVolumeMatrix(
                Reg(rdi: work, rsi: paramsAddress, rdx: (ulong)parameters.Length, rcx: matrixFormat, r8: output)));
        return ReadFloats(output, count);
    }

    private void WriteParams(ulong address, (float Angle, float Distance, float Fbw, float Lfe)[] parameters)
    {
        var floats = new List<float>();
        foreach (var (angle, distance, fbw, lfe) in parameters)
        {
            floats.AddRange([angle, distance, fbw, lfe]);
        }

        WriteFloats(address, [.. floats]);
    }

    private int CalcBlock(ulong format, uint position, uint count, ulong block) =>
        Ngs2Exports.Ngs2CalcWaveformBlock(Reg(rdi: format, rsi: position, rdx: count, rcx: block));

    private void WriteFormat(ulong address, uint type, uint channels, uint rate, byte[] config)
    {
        Span<byte> bytes = stackalloc byte[24];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], rate);
        config.CopyTo(bytes[12..]);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private static void AssertNear(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(
                Math.Abs(expected[i] - actual[i]) < 1e-5f,
                $"index {i}: expected {expected[i]}, got {actual[i]}");
        }
    }

    private CpuContext Reg(ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0, ulong r8 = 0)
    {
        _context[CpuRegister.Rdi] = rdi;
        _context[CpuRegister.Rsi] = rsi;
        _context[CpuRegister.Rdx] = rdx;
        _context[CpuRegister.Rcx] = rcx;
        _context[CpuRegister.R8] = r8;
        return _context;
    }

    private void Fill(ulong address, int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private byte[] ReadBytes(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private string ReadName(ulong address)
    {
        var bytes = ReadBytes(address, 64);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private ulong ReadU64(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(address, 8));

    private uint ReadU32(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(address, 4));

    private float ReadFloat(ulong address) => BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(address, 4));

    private float[] ReadFloats(ulong address, int count)
    {
        var bytes = ReadBytes(address, count * sizeof(float));
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return values;
    }

    private void WriteFloats(ulong address, float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        Assert.True(_memory.TryWrite(address, bytes));
    }
}
