// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>
/// Short names for packet opcodes, so a profile line names what it measured rather
/// than printing hexadecimal.
/// </summary>
public static class PacketOpcodeNames
{
    private static readonly string[] _names = Build();

    public static string Of(uint opcode) =>
        opcode < _names.Length && _names[opcode] is { } name ? name : $"op{opcode:X2}";

    private static string[] Build()
    {
        var names = new string[PacketProfile.OpcodeCount];
        names[PacketOpcode.Nop] = "NOP";
        names[PacketOpcode.SetBase] = "SET_BASE";
        names[PacketOpcode.ClearState] = "CLEAR_STATE";
        names[PacketOpcode.IndexBufferSize] = "INDEX_BUFFER_SIZE";
        names[PacketOpcode.DispatchDirect] = "DISPATCH_DIRECT";
        names[PacketOpcode.DispatchIndirect] = "DISPATCH_INDIRECT";
        names[PacketOpcode.AtomicMemory] = "ATOMIC_MEM";
        names[PacketOpcode.SetPredication] = "SET_PREDICATION";
        names[PacketOpcode.ConditionalExecute] = "COND_EXEC";
        names[PacketOpcode.DrawIndirect] = "DRAW_INDIRECT";
        names[PacketOpcode.DrawIndexIndirect] = "DRAW_INDEX_INDIRECT";
        names[PacketOpcode.IndexBase] = "INDEX_BASE";
        names[PacketOpcode.DrawIndex2] = "DRAW_INDEX_2";
        names[PacketOpcode.ContextControl] = "CONTEXT_CONTROL";
        names[PacketOpcode.IndirectBufferConstant] = "INDIRECT_BUFFER_CNST";
        names[PacketOpcode.SurfaceSync] = "SURFACE_SYNC";
        names[PacketOpcode.IndexAttributesIndirect] = "INDEX_ATTRIBUTES_INDIRECT";
        names[PacketOpcode.IndexType] = "INDEX_TYPE";
        names[PacketOpcode.DrawIndirectMulti] = "DRAW_INDIRECT_MULTI";
        names[PacketOpcode.DrawIndexAuto] = "DRAW_INDEX_AUTO";
        names[PacketOpcode.NumInstances] = "NUM_INSTANCES";
        names[PacketOpcode.DrawIndexMultiAuto] = "DRAW_INDEX_MULTI_AUTO";
        names[PacketOpcode.DrawIndexOffset2] = "DRAW_INDEX_OFFSET_2";
        names[PacketOpcode.WriteData] = "WRITE_DATA";
        names[PacketOpcode.DrawIndexIndirectMulti] = "DRAW_INDEX_INDIRECT_MULTI";
        names[PacketOpcode.MemorySemaphore] = "MEM_SEMAPHORE";
        names[PacketOpcode.DispatchDrawPreamble] = "DISPATCH_DRAW_PREAMBLE";
        names[PacketOpcode.WaitRegisterMemory] = "WAIT_REG_MEM";
        names[PacketOpcode.IndirectBuffer] = "INDIRECT_BUFFER";
        names[PacketOpcode.CopyData] = "COPY_DATA";
        names[PacketOpcode.PfpSyncMe] = "PFP_SYNC_ME";
        names[PacketOpcode.ConditionalWrite] = "COND_WRITE";
        names[PacketOpcode.EventWrite] = "EVENT_WRITE";
        names[PacketOpcode.EventWriteEndOfPipe] = "EVENT_WRITE_EOP";
        names[PacketOpcode.EventWriteEndOfShader] = "EVENT_WRITE_EOS";
        names[PacketOpcode.ReleaseMemory] = "RELEASE_MEM";
        names[PacketOpcode.DmaData] = "DMA_DATA";
        names[PacketOpcode.AcquireMemory] = "ACQUIRE_MEM";
        names[PacketOpcode.Rewind] = "REWIND";
        names[PacketOpcode.SetShaderRegisterIndirect] = "SET_SH_REG_INDIRECT";
        names[PacketOpcode.SetUserConfigRegisterIndirect] = "SET_UCONFIG_REG_INDIRECT";
        names[PacketOpcode.SetContextRegister] = "SET_CONTEXT_REG";
        names[PacketOpcode.SetShaderRegister] = "SET_SH_REG";
        names[PacketOpcode.SetUserConfigRegister] = "SET_UCONFIG_REG";
        names[PacketOpcode.SetUserConfigRegisterIndex] = "SET_UCONFIG_REG_INDEX";
        names[PacketOpcode.WriteConstantRam] = "WRITE_CONST_RAM";
        names[PacketOpcode.DumpConstantRam] = "DUMP_CONST_RAM";
        names[PacketOpcode.IncrementCeCounter] = "INCREMENT_CE";
        names[PacketOpcode.IncrementDeCounter] = "INCREMENT_DE";
        names[PacketOpcode.WaitOnCeCounter] = "WAIT_ON_CE";
        names[PacketOpcode.WaitOnDeCounterDiff] = "WAIT_ON_DE_DIFF";
        names[PacketOpcode.GetLodStats] = "GET_LOD_STATS";
        names[PacketOpcode.WaitRegisterMemory64] = "WAIT_REG_MEM64";
        names[PacketOpcode.SetContextRegisterIndirect] = "SET_CONTEXT_REG_INDIRECT";
        return names;
    }
}
