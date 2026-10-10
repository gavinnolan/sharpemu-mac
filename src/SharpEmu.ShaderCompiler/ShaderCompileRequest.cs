// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler;

// One fixed-function vertex attribute a fetch instruction reads instead of its buffer.
public sealed record ShaderVertexInput(
    uint Pc,
    uint Location,
    uint FetchComponentCount,
    uint ComponentCount,
    uint NumberFormat,
    uint DestinationSelect,
    bool PerInstance,
    IReadOnlyList<uint> AliasPcs)
{
    public ShaderVertexInput(
        uint pc,
        uint location,
        uint componentCount,
        uint numberFormat,
        bool perInstance,
        IReadOnlyList<uint> aliasPcs)
        : this(
            pc,
            location,
            componentCount,
            componentCount,
            numberFormat,
            componentCount switch
            {
                1u => 4u,
                2u => 4u | (5u << 3),
                3u => 4u | (5u << 3) | (6u << 6),
                4u => 4u | (5u << 3) | (6u << 6) | (7u << 9),
                _ => 0u,
            },
            perInstance,
            aliasPcs)
    {
    }
}

public readonly record struct ShaderClipSpaceTransform(
    bool Enabled,
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY,
    float HalfExtentX,
    float HalfExtentY);

// One bounded runtime V# table as the emitter sees it: a contiguous run of native buffer
// candidates plus the flattened key mapping that selects among them.
public readonly record struct BufferCandidateTableUse(
    uint FirstCandidate,
    uint CandidateCount,
    uint MappingOffset,
    uint SearchIterations);

// Everything an emitter needs to compile one permutation of a program: the decoded
// program, its resource plan applied to one specialization, and the binding layout.
public sealed class ShaderCompileRequest
{
    public const uint UnboundedThreadCount = uint.MaxValue;
    public const int WrittenRangeDwordCount = 3;
    public bool TraceDeviceAddressFaults { get; init; }

    public ShaderCompileRequest(ShaderResourcePlan plan, SpecializedResourceInfo resources, BindingLayout bindings)
    {
        Program = plan.Graph.Program;
        Stage = plan.Stage;
        Hash = plan.Hash;
        Memory = plan.Memory;
        Resources = resources;
        Bindings = bindings;
        UserDataBase = plan.UserDataBase;
        UserDataCount = plan.UserDataCount;
        UsesFlattenedTable = RequiresFlattenedTable(plan, resources);
        UsesGlobalDataShare = BindingLayout.UsesGlobalDataShare(Program);
        ReadsShaderBase = BindingLayout.ReadsShaderBase(Program);

        FlattenedSlotByMemoryIndex = new Dictionary<int, uint>(plan.FlattenedSlotByMemoryIndex);
        FlattenedTableReservedWords = (uint)plan.FlattenedTableReservedCount;
        IndirectKeyMemoryIndices = plan.IndirectImages.Select(access => access.Key.MemoryIndex).ToHashSet();
        IndirectOffsetKeyMemoryIndices = plan.IndirectImages.Where(access => access.KeyIsAddressOffset)
            .Select(access => access.Key.MemoryIndex).ToHashSet();
        IndirectRootByMemoryIndex = plan.IndirectImages.ToDictionary(access => access.MemoryIndex, access => access.Key.MemoryIndex);

        var writtenSlots = new Dictionary<int, uint>();
        foreach (var range in plan.DeviceAddressRanges)
        {
            if (!range.Written || !plan.WrittenRangeSlotByHandle.TryGetValue(range.Handle, out var slot))
            {
                continue;
            }

            foreach (var memoryIndex in range.MemoryIndices)
            {
                writtenSlots[memoryIndex] = slot;
            }
        }

        WrittenRangeSlotByMemoryIndex = writtenSlots;

        var candidateTables = new Dictionary<int, BufferCandidateTableUse>();
        for (var index = 0; index < plan.BufferCandidateTables.Count && index < resources.Info.BufferCandidateTables.Count; index++)
        {
            var info = resources.Info.BufferCandidateTables[index];
            var use = new BufferCandidateTableUse(info.FirstCandidate, info.CandidateCount, info.MappingOffset, info.SearchIterations);
            foreach (var memoryIndex in plan.BufferCandidateTables[index].MemoryIndices)
            {
                candidateTables[memoryIndex] = use;
            }
        }

        BufferCandidateTableByMemoryIndex = candidateTables;
    }

    // The flattened table is bound when host reads, written ranges, indirect mappings or
    // bounded buffer candidate mappings fill it.
    public static bool RequiresFlattenedTable(ShaderResourcePlan plan, SpecializedResourceInfo resources) =>
        plan.TableReads.Count != 0 || plan.WrittenRangeCount != 0 ||
        plan.BufferCandidateTables.Count != 0 ||
        resources.Info.Images.Any(image => image.IndirectSearchIterations != 0);

    public Gen5ShaderProgram Program { get; }
    public ShaderStage Stage { get; }
    public ulong Hash { get; }
    public MemoryAccessTable Memory { get; }
    public SpecializedResourceInfo Resources { get; }
    public BindingLayout Bindings { get; }
    public uint UserDataBase { get; }
    public uint UserDataCount { get; }
    public bool UsesFlattenedTable { get; }
    public bool UsesGlobalDataShare { get; }
    public bool ReadsShaderBase { get; }

    // Host-flattened scalar reads: memory index → flattened table slot.
    public IReadOnlyDictionary<int, uint> FlattenedSlotByMemoryIndex { get; }

    // The materialized flattened table always holds at least this many words (the planned table
    // reads and written ranges), so a constant slot below it needs no bounds check.
    public uint FlattenedTableReservedWords { get; }

    // Scalar reads whose loaded dword selects an indirect image at a later instruction.
    public IReadOnlySet<int> IndirectKeyMemoryIndices { get; }

    public IReadOnlySet<int> IndirectOffsetKeyMemoryIndices { get; }

    // Indirect image accesses: memory index → the memory index of the key read.
    public IReadOnlyDictionary<int, int> IndirectRootByMemoryIndex { get; }

    // Bounded runtime V# accesses: memory index → the candidate run and its key mapping.
    public IReadOnlyDictionary<int, BufferCandidateTableUse> BufferCandidateTableByMemoryIndex { get; }

    // Written device-address accesses: memory index → the flattened slot of their range.
    public IReadOnlyDictionary<int, uint> WrittenRangeSlotByMemoryIndex { get; }

    public uint WaveSize { get; init; } = 32;
    public bool EnableExecGuardElision { get; init; } = true;
    public uint ScratchDwords { get; init; }
    public bool EnableGraphicsSubgroupOperations { get; init; } = true;

    // The device supports 64-bit integer atomics on workgroup memory
    // (VkPhysicalDeviceFeatures.shaderSharedInt64Atomics). When set, the LDS
    // 64-bit atomics are emitted as real 64-bit atomics instead of a pair of
    // 32-bit ones, which is not atomic as a pair.
    public bool SupportsSharedInt64Atomics { get; init; }
    public bool ShaderSignedZeroInfNanPreserveFloat32Supported { get; init; }
    public Gen5ComputeSystemRegisters? ComputeSystemRegisters { get; init; }

    public IReadOnlyList<Gen5PixelOutputBinding> PixelOutputs { get; init; } = [];
    public uint PixelInputEnable { get; init; }
    public uint PixelCustomInterpolationMask { get; init; }

    // False when the device cannot read one vertex's value of a pixel input (PerVertexKHR).
    // MoltenVK advertises fragment shader barycentrics but cannot translate PerVertexKHR to MSL.
    public bool SupportsPerVertexPixelInputs { get; init; } = true;
    public uint PixelInputAddress { get; init; }
    public IReadOnlyList<uint>? PixelInputCntl { get; init; }

    // Set when the draw's VGT_SHADER_STAGES_EN selects the NGG primitive-shader path, so the
    // program is the merged ES/GS stage rather than a plain vertex shader. A backend that can
    // run the merged stage as a workgroup (the Metal mesh path) uses it; the ones that cannot
    // keep translating the program as a per-vertex shader, which only works for passthrough
    // NGG programs.
    public Gen5NggStageInfo? PrimitiveShader { get; init; }

    /// <summary>True when the program cannot be expressed per-vertex: its lanes exchange
    /// data through LDS or synchronize the workgroup, so it needs a real workgroup stage.
    ///
    /// GS_ALLOC_REQ and the primitive export are deliberately not part of the test. Every
    /// NGG program issues them, passthrough ones included, and those already run on the
    /// per-vertex path; cross-lane LDS and S_BARRIER are what that path provably cannot
    /// express, because it gives each invocation private LDS and one logical lane.</summary>
    public static bool RequiresPrimitiveShaderWorkgroup(Gen5ShaderProgram program)
    {
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Opcode is "SBarrier" ||
                (instruction.Control is Gen5DataShareControl { Gds: false } &&
                    instruction.Opcode is not ("DsSwizzleB32" or "DsBpermuteB32")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>False when the fixed-function vertex-fetch substitution must not be applied
    /// to this program.
    ///
    /// The substitution rewrites the program's own <c>buffer_load_format</c> fetches into
    /// host vertex attributes and drops the scalar loads that produced their V#, because a
    /// vertex stage gets those attributes from a vertex descriptor instead. A primitive
    /// shader compiled as the merged ES/GS workgroup stage has no such input: a mesh
    /// function has no <c>[[stage_in]]</c>, and the host binds no vertex buffers for it. Its
    /// ES part therefore has to keep fetching for itself, V# scalar loads included -
    /// otherwise the resource plan never sees those loads, the fetch has no buffer binding,
    /// and the program cannot be compiled.</summary>
    public static bool AllowsEmbeddedVertexFetch(
        Gen5ShaderProgram program,
        Gen5NggStageInfo? primitiveShader) =>
        primitiveShader is null || !RequiresPrimitiveShaderWorkgroup(program);

    public int RequiredVertexOutputCount { get; init; }
    public IReadOnlyList<ShaderVertexInput> VertexInputs { get; init; } = [];
    public uint PositionExportControl { get; init; }
    public bool SupportsClipDistance { get; init; } = true;
    public ShaderClipSpaceTransform ClipSpace { get; init; }

    // The LDS the dispatch allocates (COMPUTE_PGM_RSRC2.LDS_SIZE), 0 when unknown.
    public uint LocalDataShareDwords { get; init; }

    public uint LocalSizeX { get; init; } = 1;
    public uint LocalSizeY { get; init; } = 1;
    public uint LocalSizeZ { get; init; } = 1;

    // Fixed bounds for standalone modules; runtime-limit layouts read the dispatch data.
    public uint ThreadCountX { get; init; } = UnboundedThreadCount;
    public uint ThreadCountY { get; init; } = UnboundedThreadCount;
    public uint ThreadCountZ { get; init; } = UnboundedThreadCount;
}
