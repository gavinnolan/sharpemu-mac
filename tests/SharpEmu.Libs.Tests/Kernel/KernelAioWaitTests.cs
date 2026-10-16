// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelAioWaitTests : IDisposable
{
    private const ulong MemoryBase = 0x10000;
    private const ulong PathAddress = MemoryBase + 0x40;
    private const ulong RequestAddress = MemoryBase + 0x100;
    private const ulong IdAddress = MemoryBase + 0x200;
    private const ulong IdsArrayAddress = MemoryBase + 0x1800;
    private const ulong StateAddress = MemoryBase + 0x300;
    private const ulong ResultAddress = MemoryBase + 0x400;
    private const ulong BufferAddress = MemoryBase + 0x800;

    private const int Efault = unchecked((int)0x8002000E);
    private const int Einval = unchecked((int)0x80020016);
    private const int Ebadf = unchecked((int)0x80020009);
    private const int Etimedout = unchecked((int)0x8002003C);

    private const uint Submitted = 1;
    private const uint Processing = 2;
    private const uint Completed = 3;
    private const uint Aborted = 4;

    private static readonly byte[] Seed = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"u8.ToArray();

    private readonly string _hostRoot;
    private readonly CpuContext _context;
    private readonly int _fd;

    public KernelAioWaitTests()
    {
        var identity = Guid.NewGuid().ToString("N");
        var mountPoint = $"/aio-{identity}";
        _hostRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-aio-{identity}");
        Directory.CreateDirectory(_hostRoot);
        File.WriteAllBytes(Path.Combine(_hostRoot, "data.bin"), Seed);
        KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, _hostRoot);

        var memory = new FakeCpuMemory(MemoryBase, 0x4000);
        _context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(PathAddress, $"{mountPoint}/data.bin");
        _context[CpuRegister.Rdi] = PathAddress;
        _context[CpuRegister.Rsi] = 0x2;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelExports.KernelOpen(_context));
        _fd = unchecked((int)_context[CpuRegister.Rax]);
    }

    public void Dispose()
    {
        _context[CpuRegister.Rdi] = unchecked((uint)_fd);
        _ = KernelMemoryCompatExports.KernelClose(_context);
        try
        {
            Directory.Delete(_hostRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void PollSingle_IsExported(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        Assert.True(manager.TryGetExport("2pOuoWoCxdk", out var export));
        Assert.Equal("sceKernelAioPollRequest", export.Name);
        Assert.Equal("libKernel", export.LibraryName);
        Assert.True(manager.TryGetExport("xT3Cpz0yh6Y", out export));
        Assert.Equal("sceKernelAioSubmitWriteCommandsMultiple", export.Name);
    }

    [Fact]
    public void BatchReadCompletesInOrderAndPublishesResults()
    {
        WriteRequest(0, offset: 0, length: 16, BufferAddress, ResultAddress);
        WriteRequest(1, offset: 32, length: 16, BufferAddress + 16, ResultAddress + 16);
        var id = Submit(KernelMemoryCompatExports.KernelAioSubmitReadCommands, 2);
        Assert.True(id > 0);

        Assert.Equal(0, WaitOne(id, timeout: null));
        Assert.Equal(Completed, ReadUInt32(StateAddress));
        Assert.Equal((16L, Completed), ReadResult(ResultAddress));
        Assert.Equal((16L, Completed), ReadResult(ResultAddress + 16));
        Assert.Equal(Seed[..16], ReadBytes(BufferAddress, 16));
        Assert.Equal(Seed[32..48], ReadBytes(BufferAddress + 16, 16));

        Assert.Equal(0, Poll(id));
        Assert.Equal(Completed, ReadUInt32(StateAddress));
        Assert.Equal(0, Delete(id, out var ret));
        Assert.Equal(0, ret);
        Assert.Equal(Einval, Poll(id));
    }

    [Fact]
    public void MultipleSubmitReturnsOneIdPerRequestWithPerRequestState()
    {
        WriteRequest(0, offset: 8, length: 4, BufferAddress, ResultAddress);
        WriteRequest(1, offset: -1, length: 4, BufferAddress + 8, ResultAddress + 16);
        WriteRequest(2, offset: 0, length: 4, BufferAddress + 16, ResultAddress + 32, fd: 0x7FFF_0000);
        _context[CpuRegister.Rdi] = RequestAddress;
        _context[CpuRegister.Rsi] = 3;
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = IdAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioSubmitReadCommandsMultiple(_context));
        var ids = new[] { (int)ReadUInt32(IdAddress), (int)ReadUInt32(IdAddress + 4), (int)ReadUInt32(IdAddress + 8) };
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));

        Assert.Equal(0, WaitMany(ids, mode: 1, timeoutUsec: null));
        Assert.Equal([Completed, Aborted, Aborted], ReadStates(3));
        Assert.Equal((4L, Completed), ReadResult(ResultAddress));
        Assert.Equal((Einval, Aborted), ReadResult(ResultAddress + 16));
        Assert.Equal((Ebadf, Aborted), ReadResult(ResultAddress + 32));
        Assert.Equal(Seed[8..12], ReadBytes(BufferAddress, 4));

        Assert.Equal(0, DeleteMany(ids));
    }

    [Fact]
    public void WriteMultipleTransfersAtOffsetWithoutMovingThePosition()
    {
        Assert.True(_context.Memory.TryWrite(BufferAddress, "WXYZ"u8));
        WriteRequest(0, offset: 8, length: 4, BufferAddress, ResultAddress);
        _context[CpuRegister.Rdi] = RequestAddress;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = 1;
        _context[CpuRegister.Rcx] = IdAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioSubmitWriteCommandsMultiple(_context));
        var id = (int)ReadUInt32(IdAddress);
        Assert.Equal(0, WaitOne(id, timeout: null));
        Assert.Equal((4L, Completed), ReadResult(ResultAddress));

        WriteRequest(0, offset: 6, length: 6, BufferAddress + 0x40, ResultAddress + 16);
        var readId = Submit(KernelMemoryCompatExports.KernelAioSubmitReadCommands, 1);
        Assert.Equal(0, WaitOne(readId, timeout: null));
        Assert.Equal("67WXYZ"u8.ToArray(), ReadBytes(BufferAddress + 0x40, 6));
        _ = Delete(id, out _);
        _ = Delete(readId, out _);
    }

    [Fact]
    public void ValidatesArguments()
    {
        WriteRequest(0, offset: 0, length: 4, BufferAddress, ResultAddress);
        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rcx] = IdAddress;
        Assert.Equal(Efault, KernelMemoryCompatExports.KernelAioSubmitReadCommands(_context));
        _context[CpuRegister.Rdi] = RequestAddress;
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelAioSubmitReadCommands(_context));
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rcx] = 0;
        Assert.Equal(Efault, KernelMemoryCompatExports.KernelAioSubmitReadCommands(_context));
        WriteRequest(0, offset: 0, length: 4, BufferAddress, resultAddress: 0);
        _context[CpuRegister.Rcx] = IdAddress;
        Assert.Equal(Efault, KernelMemoryCompatExports.KernelAioSubmitReadCommandsMultiple(_context));

        Assert.Equal(Einval, WaitOne(9999, timeout: null));
        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = StateAddress;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelAioPollRequest(_context));

        WriteUInt32(StateAddress, 7);
        WriteUInt32(StateAddress + 4, 7);
        Assert.Equal(Einval, WaitMany([9998, 9999], mode: 1, timeoutUsec: null));
        Assert.Equal(7u, ReadUInt32(StateAddress));
        Assert.Equal(Einval, WaitMany([9998], mode: 3, timeoutUsec: null));
    }

    [Fact]
    public void CancelAbortsQueuedRequestsAndReportsOthers()
    {
        // A large batch on one priority keeps later submissions queued long enough to cancel.
        var ids = new List<int>();
        for (var i = 0; i < 64; i++)
        {
            WriteRequest(0, offset: 0, length: 64, BufferAddress, ResultAddress + (ulong)(16 * (i % 32)));
            ids.Add(Submit(KernelMemoryCompatExports.KernelAioSubmitReadCommands, 1, priority: 3));
        }

        var last = ids[^1];
        _context[CpuRegister.Rdi] = unchecked((uint)last);
        _context[CpuRegister.Rsi] = StateAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioCancelRequest(_context));
        var cancelState = ReadUInt32(StateAddress);
        Assert.Contains(cancelState, new[] { Processing, Aborted });

        Assert.Equal(0, WaitMany(ids.ToArray()[..32], mode: 1, timeoutUsec: null));
        Assert.Equal(0, WaitOne(last, timeout: null));
        Assert.Equal(Aborted, ReadUInt32(StateAddress));

        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = StateAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioCancelRequest(_context));
        Assert.Equal(Processing, ReadUInt32(StateAddress));

        foreach (var chunk in ids.Chunk(32))
        {
            Assert.Equal(0, WaitMany(chunk, mode: 1, timeoutUsec: null));
            Assert.Equal(0, DeleteMany(chunk));
        }
    }

    [Fact]
    public void SamePriorityCompletesInSubmissionOrder()
    {
        var ids = new int[8];
        for (var i = 0; i < ids.Length; i++)
        {
            WriteRequest(0, offset: i, length: 32, BufferAddress + (ulong)(i * 32), ResultAddress + (ulong)(i * 16));
            ids[i] = Submit(KernelMemoryCompatExports.KernelAioSubmitReadCommands, 1, priority: 2);
        }

        // Waiting for the last one is enough: everything before it has finished.
        Assert.Equal(0, WaitMany([ids[^1]], mode: 2, timeoutUsec: 5_000_000));
        WriteIds(ids);
        _context[CpuRegister.Rdi] = IdsArrayAddress;
        _context[CpuRegister.Rsi] = (ulong)ids.Length;
        _context[CpuRegister.Rdx] = StateAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioPollRequests(_context));
        Assert.All(ReadStates(ids.Length), state => Assert.Equal(Completed, state));
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal((32L, Completed), ReadResult(ResultAddress + (ulong)(i * 16)));
        }

        Assert.Equal(0, DeleteMany(ids));
    }

    [Fact]
    public void SetParamValidatesAndWritesTheParameterBlock()
    {
        _context[CpuRegister.Rdi] = StateAddress;
        _context[CpuRegister.Rsi] = 64;
        _context[CpuRegister.Rdx] = 16;
        _context[CpuRegister.Rcx] = 1;
        _context[CpuRegister.R8] = 0x200000;
        _context[CpuRegister.R9] = 0x40000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioSetParam(_context));
        Assert.Equal(64u, ReadUInt32(StateAddress));
        Assert.Equal(16u, ReadUInt32(StateAddress + 4));
        Assert.Equal(1u, ReadUInt32(StateAddress + 8));
        Assert.Equal(0x200000u, ReadUInt32(StateAddress + 12));
        Assert.Equal(0x40000u, ReadUInt32(StateAddress + 16));

        _context[CpuRegister.Rsi] = 129;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelAioSetParam(_context));
        _context[CpuRegister.Rsi] = 64;
        _context[CpuRegister.Rcx] = 2;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelAioSetParam(_context));
        _context[CpuRegister.Rdi] = 0;
        Assert.Equal(Efault, KernelMemoryCompatExports.KernelAioSetParam(_context));
    }

    // ---- helpers ----

    private void WriteRequest(int index, long offset, ulong length, ulong buffer, ulong resultAddress, int? fd = null)
    {
        Span<byte> raw = stackalloc byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(raw, offset);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[8..], length);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[16..], buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[24..], resultAddress);
        BinaryPrimitives.WriteInt32LittleEndian(raw[32..], fd ?? _fd);
        Assert.True(_context.Memory.TryWrite(RequestAddress + (ulong)(index * 40), raw));
    }

    private int Submit(Func<CpuContext, int> submit, int count, int priority = 0)
    {
        _context[CpuRegister.Rdi] = RequestAddress;
        _context[CpuRegister.Rsi] = (ulong)count;
        _context[CpuRegister.Rdx] = (ulong)priority;
        _context[CpuRegister.Rcx] = IdAddress;
        Assert.Equal(0, submit(_context));
        return (int)ReadUInt32(IdAddress);
    }

    private int WaitOne(int id, uint? timeout)
    {
        if (timeout is { } usec)
        {
            WriteUInt32(StateAddress + 0x80, usec);
        }

        _context[CpuRegister.Rdi] = unchecked((uint)id);
        _context[CpuRegister.Rsi] = StateAddress;
        _context[CpuRegister.Rdx] = timeout is null ? 0 : StateAddress + 0x80;
        return KernelMemoryCompatExports.KernelAioWaitRequest(_context);
    }

    private int Poll(int id)
    {
        _context[CpuRegister.Rdi] = unchecked((uint)id);
        _context[CpuRegister.Rsi] = StateAddress;
        return KernelMemoryCompatExports.KernelAioPollRequest(_context);
    }

    private int WaitMany(int[] ids, uint mode, uint? timeoutUsec)
    {
        WriteIds(ids);
        if (timeoutUsec is { } usec)
        {
            WriteUInt32(StateAddress + 0x80, usec);
        }

        _context[CpuRegister.Rdi] = IdsArrayAddress;
        _context[CpuRegister.Rsi] = (ulong)ids.Length;
        _context[CpuRegister.Rdx] = StateAddress;
        _context[CpuRegister.Rcx] = mode;
        _context[CpuRegister.R8] = timeoutUsec is null ? 0 : StateAddress + 0x80;
        return KernelMemoryCompatExports.KernelAioWaitRequests(_context);
    }

    private int Delete(int id, out int ret)
    {
        _context[CpuRegister.Rdi] = unchecked((uint)id);
        _context[CpuRegister.Rsi] = StateAddress + 0x90;
        var result = KernelMemoryCompatExports.KernelAioDeleteRequest(_context);
        ret = (int)ReadUInt32(StateAddress + 0x90);
        return result;
    }

    private int DeleteMany(int[] ids)
    {
        WriteIds(ids);
        _context[CpuRegister.Rdi] = IdsArrayAddress;
        _context[CpuRegister.Rsi] = (ulong)ids.Length;
        _context[CpuRegister.Rdx] = StateAddress + 0x100;
        return KernelMemoryCompatExports.KernelAioDeleteRequests(_context);
    }

    private void WriteIds(int[] ids)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            WriteUInt32(IdsArrayAddress + (ulong)(i * 4), unchecked((uint)ids[i]));
        }
    }

    private uint[] ReadStates(int count) =>
        Enumerable.Range(0, count).Select(i => ReadUInt32(StateAddress + (ulong)(i * 4))).ToArray();

    private (long Value, uint State) ReadResult(ulong address)
    {
        Span<byte> bytes = stackalloc byte[12];
        Assert.True(_context.Memory.TryRead(address, bytes));
        return (BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
    }

    private byte[] ReadBytes(ulong address, int count)
    {
        var bytes = new byte[count];
        Assert.True(_context.Memory.TryRead(address, bytes));
        return bytes;
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_context.Memory.TryWrite(address, bytes));
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(_context.Memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
