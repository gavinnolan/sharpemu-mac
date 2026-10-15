// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AvPlayerStreamControlTests : IDisposable
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const int OperationFailed = unchecked((int)0x806A0002);
    private const int NotSupported = unchecked((int)0x806A0004);
    private const ulong Handle = 0xA0_0000_3400;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StartInfoAddress = MemoryBase + 0x100;
    private const string SourcePath = "/nonexistent/sharpemu-avplayer-test.mp4";

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public AvPlayerStreamControlTests()
    {
        _context = new CpuContext(_memory, Generation.Gen5);
        Register(started: false);
    }

    [Fact]
    public void DisableStreamStopsSelectionAndEnableStreamRestoresIt()
    {
        Assert.Equal(0, Call(AvPlayerExports.AvPlayerDisableStream, 1));
        Assert.Equal((true, false), Enabled());

        Assert.Equal(0, Call(AvPlayerExports.AvPlayerDisableStream, 0));
        Assert.Equal((false, false), Enabled());

        Assert.Equal(0, Call(AvPlayerExports.AvPlayerEnableStream, 1));
        Assert.Equal((false, true), Enabled());
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
    }

    [Fact]
    public void StreamSelectionRejectsUnknownStreamsAndStartedPlayers()
    {
        Assert.Equal(OperationFailed, Call(AvPlayerExports.AvPlayerDisableStream, 2));
        Assert.Equal(OperationFailed, Call(AvPlayerExports.AvPlayerEnableStream, 7));

        Register(started: true);
        Assert.Equal(OperationFailed, Call(AvPlayerExports.AvPlayerDisableStream, 0));
        Assert.Equal(OperationFailed, Call(AvPlayerExports.AvPlayerEnableStream, 0));
        Assert.Equal((true, true), Enabled());
    }

    [Fact]
    public void StreamSelectionRejectsUnknownPlayerAndMissingSource()
    {
        _context[CpuRegister.Rdi] = Handle + 1;
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerDisableStream(_context));
        Assert.Equal(unchecked((ulong)InvalidParameters), _context[CpuRegister.Rax]);

        AvPlayerExports.RegisterPlayerForTest(Handle, 16, 16, 1000, hasAudio: true);
        Assert.Equal(InvalidParameters, Call(AvPlayerExports.AvPlayerDisableStream, 0));
        Assert.Equal(InvalidParameters, Call(AvPlayerExports.AvPlayerEnableStream, 0));
    }

    [Fact]
    public void ChangeStreamAcceptsOnlyTheActiveStreamOfTheSameType()
    {
        Assert.Equal(0, Change(0, 0));
        Assert.Equal(0, Change(1, 1));

        // Audio and video are different types.
        Assert.Equal(OperationFailed, Change(0, 1));
        Assert.Equal(OperationFailed, Change(1, 0));
        // Out of range.
        Assert.Equal(OperationFailed, Change(2, 2));

        // A disabled stream is not active, so it cannot be changed from.
        Assert.Equal(0, Call(AvPlayerExports.AvPlayerDisableStream, 1));
        Assert.Equal(OperationFailed, Change(1, 1));

        _context[CpuRegister.Rdi] = Handle + 1;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerChangeStream(_context));
    }

    [Fact]
    public void TrickSpeedAcceptsNormalAndReportsOtherSpeedsUnsupported()
    {
        Assert.Equal(0, Call(AvPlayerExports.AvPlayerSetTrickSpeed, 100));
        Assert.Equal(NotSupported, Call(AvPlayerExports.AvPlayerSetTrickSpeed, 400));
        Assert.Equal(NotSupported, Call(AvPlayerExports.AvPlayerSetTrickSpeed, unchecked((ulong)-800)));
        Assert.Equal(unchecked((ulong)NotSupported), _context[CpuRegister.Rax]);
        Assert.Equal(100, AvPlayerExports.GetStreamControlStateForTest(Handle).TrickSpeed);
    }

    [Fact]
    public void TrickSpeedRejectsZeroUnknownPlayerAndStoppedPlayer()
    {
        Assert.Equal(InvalidParameters, Call(AvPlayerExports.AvPlayerSetTrickSpeed, 0));

        _context[CpuRegister.Rdi] = Handle + 1;
        _context[CpuRegister.Rsi] = 100;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerSetTrickSpeed(_context));

        AvPlayerExports.RegisterPlayerForTest(
            Handle, 16, 16, 1000, hasAudio: true, sourcePath: SourcePath, stopped: true);
        Assert.Equal(OperationFailed, Call(AvPlayerExports.AvPlayerSetTrickSpeed, 100));
    }

    [Fact]
    public void StartExRejectsUnknownPlayerMissingSourceAndUnreadableStartInfo()
    {
        _context[CpuRegister.Rdi] = Handle + 1;
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerStartEx(_context));

        _context[CpuRegister.Rdi] = Handle;
        _context[CpuRegister.Rsi] = 0xDEAD_0000;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerStartEx(_context));
        Assert.Equal(unchecked((ulong)InvalidParameters), _context[CpuRegister.Rax]);

        AvPlayerExports.RegisterPlayerForTest(Handle, 16, 16, 1000, hasAudio: true);
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerStartEx(_context));
    }

    [Fact]
    public void StartExReadsTheStartTimeAndClampsItToTheDuration()
    {
        // SceAvPlayerStartInfoEx { size_t thisSize; uint64_t startTimeMs; }
        Assert.True(_context.TryWriteUInt64(StartInfoAddress, 16));
        Assert.True(_context.TryWriteUInt64(StartInfoAddress + 8, 5_000));
        _context[CpuRegister.Rdi] = Handle;
        _context[CpuRegister.Rsi] = StartInfoAddress;

        // The test source does not exist, so opening the decoder fails after the
        // start position has been applied.
        Assert.Equal(OperationFailed, AvPlayerExports.AvPlayerStartEx(_context));
        Assert.Equal(1000UL, AvPlayerExports.GetStreamControlStateForTest(Handle).StartTimeMilliseconds);

        Assert.True(_context.TryWriteUInt64(StartInfoAddress + 8, 250));
        Assert.Equal(OperationFailed, AvPlayerExports.AvPlayerStartEx(_context));
        Assert.Equal(250UL, AvPlayerExports.GetStreamControlStateForTest(Handle).StartTimeMilliseconds);
    }

    public void Dispose() => AvPlayerExports.RemovePlayerForTest(Handle);

    private void Register(bool started) =>
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            width: 16,
            height: 16,
            durationMilliseconds: 1000,
            hasAudio: true,
            sourcePath: SourcePath,
            started: started);

    private int Call(Func<CpuContext, int> export, ulong argument)
    {
        _context[CpuRegister.Rdi] = Handle;
        _context[CpuRegister.Rsi] = argument;
        return export(_context);
    }

    private int Change(ulong oldStreamId, ulong newStreamId)
    {
        _context[CpuRegister.Rdi] = Handle;
        _context[CpuRegister.Rsi] = oldStreamId;
        _context[CpuRegister.Rdx] = newStreamId;
        return AvPlayerExports.AvPlayerChangeStream(_context);
    }

    private (bool Video, bool Audio) Enabled()
    {
        var state = AvPlayerExports.GetStreamControlStateForTest(Handle);
        return (state.VideoEnabled, state.AudioEnabled);
    }
}
