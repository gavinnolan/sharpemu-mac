// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Keyboard;
using Xunit;

namespace SharpEmu.Libs.Tests.Keyboard;

[CollectionDefinition("KeyboardState", DisableParallelization = true)]
public sealed class KeyboardStateCollection
{
}

[Collection("KeyboardState")]
public sealed class KeyboardExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong DataAddress = Base + 0x100;
    private const ulong CharAddress = Base + 0x1000;

    private const int InvalidArg = unchecked((int)0x80DA0001);
    private const int InvalidHandle = unchecked((int)0x80DA0003);
    private const int AlreadyOpened = unchecked((int)0x80DA0004);
    private const int NotInitialized = unchecked((int)0x80DA0005);

    private readonly FakeCpuMemory _memory = new(Base, 0x4000);
    private readonly CpuContext _ctx;
    private readonly HashSet<int> _down = [];
    private bool _connected = true;
    private ulong _time = 100;

    public KeyboardExportsTests()
    {
        KeyboardExports.ResetForTests();
        KeyboardExports.SetHostForTests(_down.Contains, () => _connected, () => _time++);
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => KeyboardExports.ResetForTests();

    [Fact]
    public void Open_RequiresInitAndValidatesArguments()
    {
        Assert.Equal(NotInitialized, Open());
        Assert.Equal(0, Call(KeyboardExports.KeyboardInit));
        Assert.Equal(InvalidArg, Open(type: 1));
        Assert.Equal(InvalidArg, Open(index: 1));
        Assert.Equal(InvalidArg, Open(userId: -1));
        Assert.Equal(1, Open());
        Assert.Equal(AlreadyOpened, Open());
        Assert.Equal(0, Call(KeyboardExports.KeyboardClose, 1));
        Assert.Equal(InvalidHandle, Call(KeyboardExports.KeyboardClose, 1));
        Assert.Equal(1, Open());
    }

    [Fact]
    public void Read_ValidatesHandleAndArguments()
    {
        Assert.Equal(NotInitialized, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 1));
        Init();
        Assert.Equal(InvalidHandle, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 1));
        Assert.Equal(1, Open());
        Assert.Equal(InvalidHandle, Call(KeyboardExports.KeyboardRead, 7, DataAddress, 1));
        Assert.Equal(InvalidArg, Call(KeyboardExports.KeyboardRead, 1, 0, 1));
        Assert.Equal(InvalidArg, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 0));
        Assert.Equal(InvalidArg, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 17));
        Assert.Equal(InvalidArg, Call(KeyboardExports.KeyboardReadState, 1, 0));
        Assert.Equal(InvalidHandle, Call(KeyboardExports.KeyboardReadState, 9, DataAddress));
    }

    [Fact]
    public void Open_QueuesIdleSnapshotWithExactLayout()
    {
        Init();
        Open();
        Fill(DataAddress, 0xEE, 0x60);

        Assert.Equal(1, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 4));
        var raw = ReadData(0);
        Assert.Equal(100UL, BinaryPrimitives.ReadUInt64LittleEndian(raw));
        Assert.Equal(0, raw[0x08]);
        Assert.Equal(1, raw[0x10]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x14)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x18)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x1C)));
        Assert.All(raw.AsSpan(0x20, 0x20).ToArray(), b => Assert.Equal(0, b));
        Assert.All(raw.AsSpan(0x40, 0x20).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 4));
    }

    [Fact]
    public void Read_ReportsKeyChangesAsHidUsagesInOrder()
    {
        Init();
        Open();
        Call(KeyboardExports.KeyboardRead, 1, DataAddress, 1);

        _down.Add(0xA0); // Left Shift
        Assert.Equal(1, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 4));
        Assert.Equal(2U, ReadU32(DataAddress + 0x1C));

        _down.Add(0x41); // A
        Assert.Equal(1, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 4));
        Assert.Equal(1, ReadI32(DataAddress + 0x14));
        Assert.Equal(0x04, ReadU16(DataAddress + 0x20));

        _down.Add(0x0D); // Enter
        Assert.Equal(1, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 4));
        Assert.Equal(2, ReadI32(DataAddress + 0x14));
        Assert.Equal(0x04, ReadU16(DataAddress + 0x20));
        Assert.Equal(0x28, ReadU16(DataAddress + 0x22));

        _down.Remove(0x41);
        Assert.Equal(0, Call(KeyboardExports.KeyboardReadState, 1, DataAddress));
        Assert.Equal(1, ReadI32(DataAddress + 0x14));
        Assert.Equal(0x28, ReadU16(DataAddress + 0x20));
        Assert.Equal(2U, ReadU32(DataAddress + 0x1C));
    }

    [Fact]
    public void Read_DrainsQueuedEventsInBatchesAndCapsAtSixteen()
    {
        Init();
        Open();
        Call(KeyboardExports.KeyboardRead, 1, DataAddress, 1);
        for (var i = 0; i < 20; i++)
        {
            _down.Add(0x41 + i);
            Call(KeyboardExports.KeyboardReadState, 1, DataAddress); // polls, queues one event per change
        }

        Assert.Equal(16, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 16));
        Assert.Equal(0, Call(KeyboardExports.KeyboardRead, 1, DataAddress, 16));
        // The oldest 4 events were dropped: the batch starts at 5 keys and ends at 16.
        Assert.Equal(5, ReadI32(DataAddress + 0x14));
        Assert.Equal(16, ReadI32(DataAddress + (15 * 0x60) + 0x14));
    }

    [Fact]
    public void LockKeysToggleLedAndConnectedFlagFollowsHost()
    {
        Init();
        Open();
        Call(KeyboardExports.KeyboardRead, 1, DataAddress, 1);

        _down.Add(0x14); // CapsLock
        Call(KeyboardExports.KeyboardReadState, 1, DataAddress);
        Assert.Equal(KeyboardExports.LedCapsLock, ReadU32(DataAddress + 0x18));
        _down.Remove(0x14);
        Call(KeyboardExports.KeyboardReadState, 1, DataAddress);
        Assert.Equal(KeyboardExports.LedCapsLock, ReadU32(DataAddress + 0x18));
        _down.Add(0x14);
        Call(KeyboardExports.KeyboardReadState, 1, DataAddress);
        Assert.Equal(0U, ReadU32(DataAddress + 0x18));

        _connected = false;
        Call(KeyboardExports.KeyboardReadState, 1, DataAddress);
        Assert.Equal(0, ReadData(0)[0x10]);
    }

    [Theory]
    [InlineData(0, 0U, 0U, 0x04, 'a')]
    [InlineData(0, 0U, 2U, 0x04, 'A')]
    [InlineData(0, 2U, 0U, 0x04, 'A')]
    [InlineData(0, 2U, 2U, 0x04, 'a')]
    [InlineData(0, 0U, 0U, 0x1E, '1')]
    [InlineData(0, 0U, 32U, 0x1E, '!')]
    [InlineData(0, 0U, 2U, 0x34, '"')]
    [InlineData(1, 0U, 2U, 0x1F, '"')]
    [InlineData(1, 0U, 0U, 0x89, '\\')]
    [InlineData(0, 1U, 0U, 0x59, '1')]
    [InlineData(0, 0U, 0U, 0x59, '\0')]
    [InlineData(0, 0U, 0U, 0x58, '\n')]
    [InlineData(0, 0U, 0U, 0xFF, '\0')]
    public void GetKey2Char_Translates(int arrange, uint led, uint modifier, int key, char expected)
    {
        Init();
        Open();
        Fill(CharAddress, 0xEE, 0x14);
        Assert.Equal(0, Call(KeyboardExports.KeyboardGetKey2Char, 1, arrange, led, modifier, key, CharAddress));
        var raw = new byte[0x14];
        Assert.True(_memory.TryRead(CharAddress, raw));
        Assert.Equal(expected != '\0' ? 1 : 0, raw[0]);
        Assert.Equal(expected != '\0' ? 1 : 0, BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4)));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8)));
    }

    [Fact]
    public void GetKey2Char_ValidatesArguments()
    {
        Init();
        Assert.Equal(InvalidHandle, Call(KeyboardExports.KeyboardGetKey2Char, 1, 0, 0, 0, 4, CharAddress));
        Open();
        Assert.Equal(InvalidArg, Call(KeyboardExports.KeyboardGetKey2Char, 1, 0, 0, 0, 4, 0));
        Assert.Equal(InvalidArg, Call(KeyboardExports.KeyboardGetKey2Char, 1, 2, 0, 0, 4, CharAddress));
        Assert.Equal(InvalidArg, Call(KeyboardExports.KeyboardGetKey2Char, 1, 1, 16, 0, 4, CharAddress));
        Assert.Equal(0, Call(KeyboardExports.KeyboardGetKey2Char, 1, 0, 16, 0, 4, CharAddress));
    }

    private void Init() => Assert.Equal(0, Call(KeyboardExports.KeyboardInit));

    private int Open(int userId = 1, int type = 0, int index = 0) =>
        Call(KeyboardExports.KeyboardOpen, userId, type, index, 0);

    private byte[] ReadData(int index)
    {
        var raw = new byte[0x60];
        Assert.True(_memory.TryRead(DataAddress + (ulong)(index * 0x60), raw));
        return raw;
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

    private uint ReadU32(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private int ReadI32(ulong address)
    {
        Assert.True(_ctx.TryReadInt32(address, out var value));
        return value;
    }

    private ushort ReadU16(ulong address)
    {
        Assert.True(_ctx.TryReadUInt16(address, out var value));
        return value;
    }

    private void Fill(ulong address, byte value, int length) =>
        Assert.True(_memory.TryWrite(address, Enumerable.Repeat(value, length).ToArray()));
}
