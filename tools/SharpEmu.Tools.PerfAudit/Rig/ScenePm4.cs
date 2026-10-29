// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// Builds a raw PM4 type-3 stream the way the guest's command buffer holds it. Register
/// offsets are dword offsets inside their bank: context registers from 0xA000, shader
/// registers from 0x2C00 and user-config registers from 0xC000 (the mm* address / 4).
/// </summary>
internal sealed class ScenePm4
{
    public const uint OpNop = 0x10;
    public const uint OpIndexBufferSize = 0x13;
    public const uint OpDispatchDirect = 0x15;
    public const uint OpDispatchIndirect = 0x16;
    public const uint OpSetPredication = 0x20;
    public const uint OpDrawIndirect = 0x24;
    public const uint OpDrawIndexIndirect = 0x25;
    public const uint OpIndexBase = 0x26;
    public const uint OpDrawIndex2 = 0x27;
    public const uint OpIndexType = 0x2A;
    public const uint OpDrawIndexAuto = 0x2D;
    public const uint OpNumInstances = 0x2F;
    public const uint OpSetBase = 0x11;
    public const uint OpWriteData = 0x37;
    public const uint OpEventWrite = 0x46;
    public const uint OpAcquireMemory = 0x58;
    public const uint OpSetContextReg = 0x69;
    public const uint OpSetShReg = 0x76;
    public const uint OpSetUconfigReg = 0x79;

    // VGT_EVENT_TYPE values (mesa sid.h V_028A90_*).
    public const uint EventPsPartialFlush = 0x10;
    public const uint EventCacheFlushAndInvalidate = 0x16;
    public const uint EventCsPartialFlush = 0x07;
    public const uint EventPixelPipeStatControl = 0x38;
    public const uint EventPixelPipeStatDump = 0x39;
    public const uint EventPixelPipeStatReset = 0x3A;

    private readonly List<uint> _words = [];

    public int Count => _words.Count;

    public uint[] ToArray() => [.. _words];

    public static uint Header(uint dwords, uint opcode, bool predicated = false) =>
        0xC000_0000u | (((dwords - 2u) & 0x3FFFu) << 16) | ((opcode & 0xFFu) << 8) | (predicated ? 1u : 0u);

    public ScenePm4 Packet(uint opcode, params uint[] payload) => Packet(opcode, false, payload);

    public ScenePm4 Packet(uint opcode, bool predicated, params uint[] payload)
    {
        _words.Add(Header((uint)payload.Length + 1, opcode, predicated));
        _words.AddRange(payload);
        return this;
    }

    public ScenePm4 Context(uint offset, params uint[] values) => Packet(OpSetContextReg, [offset, .. values]);

    public ScenePm4 Shader(uint offset, params uint[] values) => Packet(OpSetShReg, [offset, .. values]);

    public ScenePm4 Uconfig(uint offset, params uint[] values) => Packet(OpSetUconfigReg, [offset, .. values]);

    public ScenePm4 ContextFloat(uint offset, params float[] values) =>
        Context(offset, values.Select(BitConverter.SingleToUInt32Bits).ToArray());

    /// <summary>DRAW_INDEX_AUTO: count, then VGT_DRAW_INITIATOR (auto-index source 2).</summary>
    public ScenePm4 DrawAuto(uint vertexCount, bool predicated = false) =>
        Packet(OpDrawIndexAuto, predicated, vertexCount, 2u);

    public ScenePm4 NumInstances(uint count) => Packet(OpNumInstances, count);

    /// <summary>INDEX_TYPE: 0 = 16-bit, 1 = 32-bit.</summary>
    public ScenePm4 IndexType(uint type) => Packet(OpIndexType, type);

    /// <summary>DRAW_INDEX_2: max size, index base lo/hi, count, initiator (DMA source 0).</summary>
    public ScenePm4 DrawIndexed(ulong indexAddress, uint maxIndices, uint count, bool predicated = false) =>
        Packet(OpDrawIndex2, predicated, maxIndices, (uint)indexAddress, (uint)(indexAddress >> 32), count, 0u);

    public ScenePm4 Event(uint eventType, uint eventIndex = 0) => Packet(OpEventWrite, eventType | (eventIndex << 8));

    public ScenePm4 EventWithAddress(uint eventType, uint eventIndex, ulong address) =>
        Packet(OpEventWrite, eventType | (eventIndex << 8), (uint)address, (uint)(address >> 32));

    /// <summary>
    /// SET_PREDICATION (gfx9+ layout): flags, address lo, address hi. Flags: PRED_BOOL bit 8,
    /// HINT bit 12, PRED_OP bits 16..18, CONTINUE bit 31.
    /// </summary>
    public ScenePm4 SetPredication(uint operation, bool drawIfVisible, bool noWaitHint, bool continueBit, ulong address) =>
        Packet(OpSetPredication,
            (operation << 16) | (drawIfVisible ? 1u << 8 : 0u) | (noWaitHint ? 1u << 12 : 0u) | (continueBit ? 1u << 31 : 0u),
            (uint)address, (uint)(address >> 32));

    public ScenePm4 DispatchDirect(uint x, uint y, uint z, uint initiator = 1u) => Packet(OpDispatchDirect, x, y, z, initiator);

    public ScenePm4 Raw(params uint[] words)
    {
        _words.AddRange(words);
        return this;
    }
}
