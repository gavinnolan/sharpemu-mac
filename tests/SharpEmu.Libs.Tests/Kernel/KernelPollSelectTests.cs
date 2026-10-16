// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelPollSelectTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong PathAddress = BaseAddress + 0x40;
    private const ulong SockaddrAddress = BaseAddress + 0x200;
    private const ulong PollFdsAddress = BaseAddress + 0x300;
    private const ulong ReadSetAddress = BaseAddress + 0x400;
    private const ulong WriteSetAddress = BaseAddress + 0x500;
    private const ulong TimevalAddress = BaseAddress + 0x600;

    private const short PollIn = 0x1;
    private const short PollOut = 0x4;
    private const short PollNval = 0x20;

    [Fact]
    public void Poll_ReportsFilesReadyAndUnknownDescriptorsInvalid()
    {
        var (memory, ctx) = NewContext();
        var identity = Guid.NewGuid().ToString("N");
        var hostRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-poll-{identity}");
        Directory.CreateDirectory(hostRoot);
        File.WriteAllBytes(Path.Combine(hostRoot, "data.bin"), [1, 2, 3]);
        KernelMemoryCompatExports.RegisterGuestPathMount($"/poll-{identity}", hostRoot);
        memory.WriteCString(PathAddress, $"/poll-{identity}/data.bin");
        ctx[CpuRegister.Rdi] = PathAddress;
        ctx[CpuRegister.Rsi] = 0x2;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelExports.KernelOpen(ctx));
        var fd = unchecked((int)ctx[CpuRegister.Rax]);
        try
        {
            WritePollFd(memory, 0, fd, PollIn | PollOut);
            WritePollFd(memory, 1, 0x7FFF_0000, PollIn);
            WritePollFd(memory, 2, -1, PollIn);
            Assert.Equal(2, Poll(ctx, 3, timeoutMs: -1));
            Assert.Equal(PollIn | PollOut, ReadRevents(memory, 0));
            Assert.Equal(PollNval, ReadRevents(memory, 1));
            Assert.Equal(0, ReadRevents(memory, 2));
        }
        finally
        {
            ctx[CpuRegister.Rdi] = unchecked((uint)fd);
            _ = KernelMemoryCompatExports.KernelClose(ctx);
            Directory.Delete(hostRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Poll_HonoursTimeoutAndWakesWhenSocketDataArrives()
    {
        var (memory, ctx) = NewContext();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var fd = ConnectSocket(memory, ctx, listener);
        using var server = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            WritePollFd(memory, 0, fd, PollIn);
            var stopwatch = Stopwatch.StartNew();
            Assert.Equal(0, Poll(ctx, 1, timeoutMs: 40));
            Assert.True(stopwatch.ElapsedMilliseconds >= 35, $"returned after {stopwatch.ElapsedMilliseconds} ms");
            Assert.Equal(0, ReadRevents(memory, 0));

            var sender = Task.Run(async () =>
            {
                await Task.Delay(50);
                await server.GetStream().WriteAsync(new byte[] { 0x42 });
            });
            stopwatch.Restart();
            Assert.Equal(1, Poll(ctx, 1, timeoutMs: 10_000));
            Assert.True(stopwatch.ElapsedMilliseconds < 5_000);
            Assert.Equal(PollIn, ReadRevents(memory, 0));
            await sender;

            // select reports the same readiness through the fd sets.
            ClearSets(memory, fd);
            SetBit(memory, ReadSetAddress, fd);
            WriteTimeval(memory, 1, 0);
            Assert.Equal(1, Select(ctx, fd + 1, TimevalAddress));
            Assert.True(IsBitSet(memory, ReadSetAddress, fd));
        }
        finally
        {
            ctx[CpuRegister.Rdi] = unchecked((uint)fd);
            _ = KernelMemoryCompatExports.KernelClose(ctx);
        }
    }

    [Fact]
    public void Select_RejectsUnknownDescriptorsAndTimesOut()
    {
        var (memory, ctx) = NewContext();
        const int unknownFd = 1000;
        ClearSets(memory, unknownFd);
        SetBit(memory, ReadSetAddress, unknownFd);
        WriteTimeval(memory, 0, 0);
        Assert.Equal(-1, Select(ctx, unknownFd + 1, TimevalAddress));

        // stdin is never readable: a zero timeout polls and a short one expires.
        ClearSets(memory, unknownFd);
        SetBit(memory, ReadSetAddress, 0);
        WriteTimeval(memory, 0, 0);
        Assert.Equal(0, Select(ctx, 1, TimevalAddress));
        Assert.False(IsBitSet(memory, ReadSetAddress, 0));

        SetBit(memory, ReadSetAddress, 0);
        SetBit(memory, WriteSetAddress, 1);
        WriteTimeval(memory, 0, 20_000);
        Assert.Equal(1, Select(ctx, 2, TimevalAddress));
        Assert.False(IsBitSet(memory, ReadSetAddress, 0));
        Assert.True(IsBitSet(memory, WriteSetAddress, 1));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) NewContext()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static int ConnectSocket(FakeCpuMemory memory, CpuContext ctx, TcpListener listener)
    {
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelSocketCompatExports.Socket(ctx));
        var fd = unchecked((int)ctx[CpuRegister.Rax]);
        Span<byte> sockaddr = stackalloc byte[16];
        sockaddr[0] = 16;
        sockaddr[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..], unchecked((ushort)((IPEndPoint)listener.LocalEndpoint).Port));
        IPAddress.Loopback.GetAddressBytes().CopyTo(sockaddr[4..]);
        Assert.True(memory.TryWrite(SockaddrAddress, sockaddr));
        ctx[CpuRegister.Rdi] = unchecked((uint)fd);
        ctx[CpuRegister.Rsi] = SockaddrAddress;
        ctx[CpuRegister.Rdx] = 16;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelSocketCompatExports.Connect(ctx));
        return fd;
    }

    private static int Poll(CpuContext ctx, int count, int timeoutMs)
    {
        ctx[CpuRegister.Rdi] = PollFdsAddress;
        ctx[CpuRegister.Rsi] = (ulong)count;
        ctx[CpuRegister.Rdx] = unchecked((ulong)(long)timeoutMs);
        return KernelMemoryCompatExports.PosixPoll(ctx);
    }

    private static int Select(CpuContext ctx, int nfds, ulong timeout)
    {
        ctx[CpuRegister.Rdi] = (ulong)nfds;
        ctx[CpuRegister.Rsi] = ReadSetAddress;
        ctx[CpuRegister.Rdx] = WriteSetAddress;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = timeout;
        return KernelMemoryCompatExports.PosixSelect(ctx);
    }

    private static void WritePollFd(FakeCpuMemory memory, int index, int fd, int events)
    {
        Span<byte> record = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(record, fd);
        BinaryPrimitives.WriteInt16LittleEndian(record[4..], (short)events);
        BinaryPrimitives.WriteInt16LittleEndian(record[6..], 0x7777);
        Assert.True(memory.TryWrite(PollFdsAddress + (ulong)(index * 8), record));
    }

    private static short ReadRevents(FakeCpuMemory memory, int index)
    {
        Span<byte> bytes = stackalloc byte[2];
        Assert.True(memory.TryRead(PollFdsAddress + (ulong)(index * 8) + 6, bytes));
        return BinaryPrimitives.ReadInt16LittleEndian(bytes);
    }

    private static void ClearSets(FakeCpuMemory memory, int maxFd)
    {
        var zero = new byte[(maxFd / 8) + 1];
        Assert.True(memory.TryWrite(ReadSetAddress, zero));
        Assert.True(memory.TryWrite(WriteSetAddress, zero));
    }

    private static void SetBit(FakeCpuMemory memory, ulong set, int fd)
    {
        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(set + (ulong)(fd >> 3), value));
        value[0] |= (byte)(1 << (fd & 7));
        Assert.True(memory.TryWrite(set + (ulong)(fd >> 3), value));
    }

    private static bool IsBitSet(FakeCpuMemory memory, ulong set, int fd)
    {
        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(set + (ulong)(fd >> 3), value));
        return (value[0] & (1 << (fd & 7))) != 0;
    }

    private static void WriteTimeval(FakeCpuMemory memory, long seconds, long microseconds)
    {
        Span<byte> timeval = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(timeval, seconds);
        BinaryPrimitives.WriteInt64LittleEndian(timeval[8..], microseconds);
        Assert.True(memory.TryWrite(TimevalAddress, timeval));
    }
}
