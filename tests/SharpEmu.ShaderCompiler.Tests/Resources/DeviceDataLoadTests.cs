// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DeviceDataLoadTests
{
    // The registry is process-wide, so this test owns a hash no other test plans.
    private const ulong LearnedHash = 0xD47A_10AD_5EED_0001;
    private const ulong TableBase = 0x1000;
    private const int DataOffset = 32;

    // s[8:11] is a V# loaded from the table (a binding read); s16 is a dword loaded from
    // the same table that the shader only stores (a data read).
    private static Gen5ShaderProgram BindingAndDataProgram() => Program(
        ScalarLoad(0, 0, destination: 8, count: 4),
        ScalarLoad(8, 0, destination: 16, immediateOffset: DataOffset),
        Vop1(16, "VMovB32", 4, Gen5Operand.Scalar(16)),
        BufferStore(20, 8),
        EndProgram(28));

    [Fact]
    public void LearnedDataLoad_IsLeftToTheDevice_WhileBindingReadsStayFlattened()
    {
        var program = BindingAndDataProgram();
        var flattened = ShaderResourcePlan.Extract(program, ShaderStage.Compute, LearnedHash, 0, 2);
        Assert.True(flattened.Memory.TryGetIndex(8, 0, out var dataMemory));
        Assert.Equal(5, flattened.TableReads.Count);
        Assert.Equal(4, flattened.DataSlotStart);
        var dataSlot = Assert.Single(flattened.DeviceDataSlots);
        Assert.Equal([dataMemory], dataSlot.Value);
        Assert.True(flattened.Memory[dataMemory].PlanningOnly);

        // A bind whose data dword is pending a GPU write learns it, and still reads it.
        var memory = new TestWordMemory { Base = TableBase, Words = new uint[16] };
        memory.Words[DataOffset / sizeof(uint)] = 0x1234;
        var inputs = new ResourceRuntimeInputs
        {
            UserData = [(uint)TableBase, 0],
            ReadMemory = memory.Read,
            DataWritePending = address => address == TableBase + DataOffset,
        };
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(flattened, inputs, out var table));
        Assert.Equal(0x1234u, table[dataSlot.Key]);
        Assert.Equal(1, DeviceDataLoads.CountFor(LearnedHash));
        Assert.True(DeviceDataLoads.Contains(LearnedHash, dataMemory));

        var replanned = ShaderResourcePlan.Extract(program, ShaderStage.Compute, LearnedHash, 0, 2);
        Assert.Equal(4, replanned.TableReads.Count);
        Assert.Empty(replanned.DeviceDataSlots);
        Assert.False(replanned.Memory[dataMemory].PlanningOnly);
        Assert.False(replanned.FlattenedSlotByMemoryIndex.ContainsKey(dataMemory));
        for (uint component = 0; component < 4; component++)
        {
            Assert.True(replanned.Memory.TryGetIndex(0, component, out var bindingMemory));
            Assert.True(replanned.Memory[bindingMemory].PlanningOnly);
            Assert.Equal(component, replanned.FlattenedSlotByMemoryIndex[bindingMemory]);
        }

        var range = Assert.Single(replanned.DeviceAddressRanges);
        Assert.True(range.Plannable && range.Bounded);
        Assert.Equal(DataOffset, range.FirstByte);

        var resources = ResourceMaterializer.ApplyTo(replanned, ResourceSpecialization.Default(replanned.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 2, replanned.Graph.ExcludedUserDataRegisters),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(replanned, resources),
            BindingLayout.ReadsShaderBase(program),
            0);
        var request = new ShaderCompileRequest(replanned, resources, layout);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var error), error);
        Assert.Contains("sharpemu_load_device_dword", metal.Source[metal.Source.IndexOf("kernel void ", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void BindingReads_AreNeverLearned()
    {
        var program = BindingAndDataProgram();
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, LearnedHash + 1, 0, 2);
        var memory = new TestWordMemory { Base = TableBase, Words = new uint[16] };
        var inputs = new ResourceRuntimeInputs
        {
            UserData = [(uint)TableBase, 0],
            ReadMemory = memory.Read,
            // Every word pending, the V# words included.
            DataWritePending = _ => true,
        };
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, inputs, out _));
        Assert.Equal(1, DeviceDataLoads.CountFor(LearnedHash + 1));
        Assert.Equal(5 - 1, ShaderResourcePlan.Extract(program, ShaderStage.Compute, LearnedHash + 1, 0, 2).TableReads.Count);
    }
}
