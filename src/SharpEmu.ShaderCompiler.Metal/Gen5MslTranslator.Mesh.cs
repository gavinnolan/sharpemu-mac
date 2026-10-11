// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal;

// The NGG primitive-shader path: a merged ES/GS program emitted as an MSL [[mesh]]
// function instead of a per-vertex vertex function.
//
// The hardware stage is a workgroup, not a vertex invocation: its waves exchange data
// through LDS, synchronize with S_BARRIER, ask for export space with
// S_SENDMSG GS_ALLOC_REQ and then export positions, parameters and primitive
// connectivity. None of that survives the per-vertex model, which is why the vertex path
// only ever worked for passthrough NGG programs. The mesh mapping is one-to-one:
//
//   NGG subgroup        -> one Metal threadgroup (one mesh grid element)
//   wave lane           -> one Metal thread; simdgroup lane, so EXEC/ballots stay real
//   LDS                 -> threadgroup memory
//   S_BARRIER           -> a workgroup rendezvous in the dispatcher loop (see below)
//   GS_ALLOC_REQ        -> set_primitive_count
//   EXP POS0/PARAMn     -> set_vertex at the exporting lane's index
//   EXP target 20       -> set_index for the exporting lane's primitive
//
// Barriers need care because the translated program runs inside the
// while(active){switch(pc)} dispatcher: only scalar branches move the pc, so the pc is
// wave-uniform, but two waves of the same subgroup can sit at different pc values. A
// threadgroup_barrier emitted at the barrier's own pc would then be reached by a subset
// of the threadgroup, which Metal leaves undefined (in practice a hung queue). Instead
// S_BARRIER ends its basic block and parks the wave, and the dispatcher's own loop body
// carries the only barriers: every thread runs the loop the same number of times, counts
// how many threads are still live and how many are parked, and releases the parked ones
// once every live thread has arrived. That is the hardware rule, including the part where
// a wave that has already ended stops holding the barrier.
public static partial class Gen5MslTranslator
{
    // Metal needs the mesh output extents as compile-time constants, and the whole output
    // must fit in the mesh stage's threadgroup budget, so the subgroup registers are
    // clamped into a range the device accepts.
    private const uint MeshMaxVertices = 256;
    private const uint MeshMaxPrimitives = 256;
    private const uint MeshMaxWaves = 8;
    private const uint MeshWaveLanes = 32;

    // Guest LDS for a primitive shader is sized from the stage's own allocation, not from
    // the compute path's full 32 KB: the mesh output payload shares the threadgroup
    // budget. The count stays a power of two because LDS addressing is mask-based.
    private const uint MeshLdsDwordFloor = 512;
    private const uint MeshLdsDwordCeiling = 4096;

    private const string MeshVertexStructName = "Gen5MeshVertex";
    private const string MeshDrawInfoStructName = "Gen5MeshDrawInfo";
    private const string MeshOutputTypeName = "Gen5MeshOutput";
    private const string MeshOutputName = "sharpemu_mesh";
    private const string MeshDrawInfoName = "sharpemu_draw";
    private const string MeshIndexBufferName = "sharpemu_indices";

    private const uint MeshDrawInfoBufferIndex = 2;
    private const uint MeshIndexBufferIndex = 3;

    /// <summary>True when this request must be emitted as a mesh function: the draw selected
    /// the NGG primitive-shader stage and the program is not a passthrough one.</summary>
    public static bool RequiresMeshEmission(ShaderCompileRequest request) =>
        request.Stage == ShaderStage.Vertex &&
        request.PrimitiveShader is not null &&
        ShaderCompileRequest.RequiresPrimitiveShaderWorkgroup(request.Program);

    // The subgroup budget the geometry-engine registers describe, clamped to what a Metal
    // mesh threadgroup can carry.
    private static Gen5MslMeshInfo BuildMeshInfo(ShaderCompileRequest request, uint ldsDwords)
    {
        var ngg = request.PrimitiveShader ?? default;
        var fallback = ngg.MaxOutputPerSubgroup != 0 ? ngg.MaxOutputPerSubgroup : MeshWaveLanes;
        var vertices = Clamp(ngg.VerticesPerSubgroup != 0 ? ngg.VerticesPerSubgroup : fallback, 1, MeshMaxVertices);
        var primitives = Clamp(ngg.PrimitivesPerSubgroup != 0 ? ngg.PrimitivesPerSubgroup : fallback, 1, MeshMaxPrimitives);
        var waves = Clamp((Math.Max(vertices, primitives) + MeshWaveLanes - 1) / MeshWaveLanes, 1, MeshMaxWaves);
        var topology = ngg.OutputPrimitiveType switch
        {
            Gen5NggExports.OutputPrimitivePointList => Gen5MslMeshTopology.Point,
            Gen5NggExports.OutputPrimitiveLineStrip => Gen5MslMeshTopology.Line,
            _ => Gen5MslMeshTopology.Triangle,
        };

        return new Gen5MslMeshInfo(
            ThreadsPerThreadgroup: waves * MeshWaveLanes,
            WavesPerThreadgroup: waves,
            MaxVertices: vertices,
            MaxPrimitives: primitives,
            VerticesPerPrimitive: Gen5NggExports.VerticesPerPrimitive(ngg.OutputPrimitiveType),
            Topology: topology,
            ThreadgroupMemoryBytes: ldsDwords * sizeof(uint),
            DrawInfoBufferIndex: MeshDrawInfoBufferIndex,
            IndexBufferIndex: MeshIndexBufferIndex,
            ReadsIndexBuffer: true);
    }

    // Round the stage's LDS allocation up to a power of two inside the mesh budget; the
    // mask-based LDS addressing needs the power of two, and a program that asks for
    // nothing still gets the floor so a stray address stays in bounds.
    private static uint MeshLdsDwordCount(ShaderCompileRequest request)
    {
        var requested = request.PrimitiveShader is { LocalDataShareBytes: var bytes } && bytes != 0
            ? (bytes + sizeof(uint) - 1) / sizeof(uint)
            : request.LocalDataShareDwords;
        var count = MeshLdsDwordFloor;
        while (count < requested && count < MeshLdsDwordCeiling)
        {
            count *= 2;
        }

        return count;
    }

    private static uint Clamp(uint value, uint low, uint high) => Math.Min(Math.Max(value, low), high);

    private sealed partial class CompilationContext
    {
        private readonly Gen5MslMeshInfo? _meshInfo;
        private readonly uint _meshLdsDwords;

        /// <summary>Threadgroup memory this compute dispatch declares, in dwords.</summary>
        private readonly uint _computeLdsDwords;

        /// <summary>Whether the wave64 ballot bridge has to share the LDS allocation.
        /// Only a dispatch that asked for the whole 32 KB leaves no room for its own
        /// three dwords.</summary>
        private bool AliasesWaveScratchIntoLds =>
            _stage == Gen5MslStage.Compute && _usesLds &&
            _computeLdsDwords + 3 > LdsDwordCount;

        private Gen5MslMeshInfo Mesh => _meshInfo ??
            throw new InvalidOperationException("the mesh interface is only built for the mesh stage");

        private static string MeshTopologyName(Gen5MslMeshTopology topology) => topology switch
        {
            Gen5MslMeshTopology.Point => "topology::point",
            Gen5MslMeshTopology.Line => "topology::line",
            _ => "topology::triangle",
        };

        // ---- module ----

        // The vertex payload, the launch inputs and the mesh output type. The vertex struct
        // deliberately matches Gen5VsOut field for field: the paired pixel program is
        // translated against that interface, so a mesh pipeline reuses it unchanged.
        private void EmitMeshTypes(StringBuilder source)
        {
            var mesh = Mesh;
            source.AppendLine($"struct {MeshVertexStructName}");
            source.AppendLine("{");
            source.AppendLine("    float4 sharpemu_position [[position]];");
            foreach (var location in _vertexOutputs)
            {
                source.AppendLine($"    float4 param{location} [[user(locn{location})]];");
            }

            source.AppendLine("};");
            source.AppendLine();

            // What the geometry engine would hand the merged stage in hardware registers.
            // The host fills one block per draw; docs/mac/ngg-mesh-shaders.md is the
            // field-by-field contract and must stay in step with this struct.
            source.AppendLine($"struct {MeshDrawInfoStructName}");
            source.AppendLine("{");
            source.AppendLine("    uint vertices_per_subgroup;");
            source.AppendLine("    uint primitives_per_subgroup;");
            source.AppendLine("    uint index_count;");
            source.AppendLine("    uint index_type;");
            source.AppendLine("    uint first_index;");
            source.AppendLine("    int base_vertex;");
            source.AppendLine("    uint instance_count;");
            source.AppendLine("    uint first_instance;");
            source.AppendLine("    uint vertex_group_size;");
            source.AppendLine("    uint primitive_group_size;");
            source.AppendLine("    uint user_vector_enable;");
            source.AppendLine("    uint primitive_amplification;");
            source.AppendLine("    uint object_id;");
            source.AppendLine("    uint reserved0;");
            source.AppendLine("    uint reserved1;");
            source.AppendLine("    uint reserved2;");
            source.AppendLine("};");
            source.AppendLine();
            source.AppendLine(
                $"using {MeshOutputTypeName} = mesh<{MeshVertexStructName}, void, " +
                $"{mesh.MaxVertices}, {mesh.MaxPrimitives}, {MeshTopologyName(mesh.Topology)}>;");
            source.AppendLine();

            // Guest indices arrive as a raw dword stream; a 16-bit index buffer is read as
            // the containing dword and shifted, which keeps one binding for both types.
            source.AppendLine("static inline uint sharpemu_mesh_index(");
            source.AppendLine($"    device const uint* indices, constant {MeshDrawInfoStructName}& draw, uint slot)");
            source.AppendLine("{");
            source.AppendLine("    if (draw.index_type == 0u || slot >= draw.index_count)");
            source.AppendLine("    {");
            source.AppendLine("        return slot;");
            source.AppendLine("    }");
            source.AppendLine();
            source.AppendLine("    const uint offset = draw.first_index + slot;");
            source.AppendLine("    if (draw.index_type == 1u)");
            source.AppendLine("    {");
            source.AppendLine("        return (indices[offset >> 1u] >> ((offset & 1u) * 16u)) & 0xFFFFu;");
            source.AppendLine("    }");
            source.AppendLine();
            source.AppendLine("    return indices[offset];");
            source.AppendLine("}");
            source.AppendLine();
        }

        private void EmitMeshSignature(StringBuilder source)
        {
            var mesh = Mesh;
            source.AppendLine(
                $"[[mesh, max_total_threads_per_threadgroup({mesh.ThreadsPerThreadgroup})]]");
            source.AppendLine($"void {EntryPointName}(");
            source.AppendLine($"    {MeshOutputTypeName} {MeshOutputName},");
        }

        private void EmitMeshSystemParameters(StringBuilder source)
        {
            source.AppendLine(
                $"    constant {MeshDrawInfoStructName}& {MeshDrawInfoName} [[buffer({MeshDrawInfoBufferIndex})]],");
            source.AppendLine(
                $"    device const uint* {MeshIndexBufferName} [[buffer({MeshIndexBufferIndex})]],");
            source.AppendLine("    uint sharpemu_tg_index [[thread_index_in_threadgroup]],");
            source.AppendLine("    uint3 sharpemu_group_id [[threadgroup_position_in_grid]],");
            source.AppendLine("    uint sharpemu_lane [[thread_index_in_simdgroup]],");
        }

        // ---- body ----

        // The threadgroup state the mesh body needs on top of the shared register file:
        // the barrier rendezvous counters and the export allocation GS_ALLOC_REQ writes.
        // Threadgroup variables cannot carry initializers, so thread 0 seeds them and one
        // barrier publishes them before the dispatcher starts.
        private void EmitMeshDeclarations(StringBuilder source)
        {
            var mesh = Mesh;
            source.AppendLine($"    const uint sharpemu_mesh_wave = sharpemu_tg_index / {MeshWaveLanes}u;");
            source.AppendLine("    const uint sharpemu_mesh_subgroup = sharpemu_group_id.x;");
            source.AppendLine("    const uint sharpemu_mesh_instance = sharpemu_group_id.y;");
            source.AppendLine("    threadgroup atomic_uint sharpemu_mesh_live;");
            source.AppendLine("    threadgroup atomic_uint sharpemu_mesh_parked;");
            source.AppendLine("    threadgroup uint sharpemu_mesh_alloc[2];");
            source.AppendLine("    bool sharpemu_mesh_wait = false;");
            source.AppendLine($"    {MeshVertexStructName} sharpemu_out = {{}};");

            // A primitive no lane exported stays culled; a degenerate index triple is how
            // Metal expresses that, since a mesh primitive cannot be dropped in place.
            source.AppendLine("    uint3 sharpemu_mesh_prim = uint3(0u);");
            source.AppendLine("    bool sharpemu_mesh_culled = true;");
            source.AppendLine("    if (sharpemu_tg_index == 0u)");
            source.AppendLine("    {");
            source.AppendLine(
                $"        sharpemu_mesh_alloc[0] = min({MeshDrawInfoName}.vertices_per_subgroup, {mesh.MaxVertices}u);");
            source.AppendLine(
                $"        sharpemu_mesh_alloc[1] = min({MeshDrawInfoName}.primitives_per_subgroup, {mesh.MaxPrimitives}u);");
            source.AppendLine("    }");
            source.AppendLine();
            source.AppendLine("    threadgroup_barrier(mem_flags::mem_threadgroup);");
        }

        // The merged ES/GS launch state. Hardware splits one subgroup across waves and
        // hands each wave its own counts plus packed per-lane inputs; the same values are
        // reconstructed here from the thread's position and the draw block.
        private void EmitMeshInitialState(StringBuilder source)
        {
            var mesh = Mesh;
            var lanes = $"{MeshWaveLanes}u";
            source.AppendLine($"    const uint sharpemu_mesh_wave_first = sharpemu_mesh_wave * {lanes};");
            source.AppendLine(
                "    const uint sharpemu_mesh_subgroup_verts = sharpemu_mesh_alloc[0];");
            source.AppendLine(
                "    const uint sharpemu_mesh_subgroup_prims = sharpemu_mesh_alloc[1];");
            source.AppendLine(
                "    const uint sharpemu_mesh_wave_verts = sharpemu_mesh_subgroup_verts > sharpemu_mesh_wave_first");
            source.AppendLine(
                $"        ? min(sharpemu_mesh_subgroup_verts - sharpemu_mesh_wave_first, {lanes}) : 0u;");
            source.AppendLine(
                "    const uint sharpemu_mesh_wave_prims = sharpemu_mesh_subgroup_prims > sharpemu_mesh_wave_first");
            source.AppendLine(
                $"        ? min(sharpemu_mesh_subgroup_prims - sharpemu_mesh_wave_first, {lanes}) : 0u;");

            // The lane's ES vertex and GS primitive inside the subgroup, and the guest
            // vertex the geometry engine's index fetch would have produced for it.
            source.AppendLine(
                "    const uint sharpemu_mesh_vertex_slot = sharpemu_mesh_subgroup * " +
                $"{mesh.MaxVertices}u + sharpemu_tg_index;");
            source.AppendLine(
                "    const uint sharpemu_mesh_prim_slot = sharpemu_mesh_subgroup * " +
                $"{mesh.MaxPrimitives}u + sharpemu_tg_index;");
            source.AppendLine(
                $"    const uint sharpemu_mesh_vertex_index = (uint)((int)sharpemu_mesh_index({MeshIndexBufferName}, " +
                $"{MeshDrawInfoName}, sharpemu_mesh_vertex_slot) + {MeshDrawInfoName}.base_vertex);");
            source.AppendLine(
                $"    const uint sharpemu_mesh_instance_index = {MeshDrawInfoName}.first_instance + sharpemu_mesh_instance;");
            source.AppendLine();

            // Merged wave info: this wave's ES vertex count in bits 0-7, its GS primitive
            // count in bits 8-15, the GS wave id in bits 16-23, this wave's subgroup id in
            // bits 24-27 and the workgroup's wave count in bits 28-31. The guest rebuilds
            // EXEC from the first pair, so a wrong value either kills every lane or leaves
            // a waterfall loop waiting on lanes that do not exist. Written only when the
            // register is not also user data for this program.
            if (_request.UserDataBase > Gen5NggExports.MergedWaveInfoRegister)
            {
                source.AppendLine(
                    $"    s[{Gen5NggExports.MergedWaveInfoRegister}] = sharpemu_mesh_wave_verts |");
                source.AppendLine(
                    $"        (sharpemu_mesh_wave_prims << {Gen5NggExports.MergedWavePrimitiveCountShift}u) |");
                source.AppendLine(
                    $"        (sharpemu_mesh_wave << {Gen5NggExports.MergedWaveGsWaveIdShift}u) |");
                source.AppendLine(
                    $"        (sharpemu_mesh_wave << {Gen5NggExports.MergedWaveSubgroupIdShift}u) |");
                source.AppendLine(
                    $"        ({mesh.WavesPerThreadgroup}u << {Gen5NggExports.MergedWaveSubgroupCountShift}u);");
            }

            // Merged group info: the workgroup's ES vertex count in bits 12-20 and its
            // input primitive count in bits 22-30. The ordered-append id below them stays
            // zero; no primitive shader seen so far consumes it.
            if (_request.UserDataBase > Gen5NggExports.MergedGroupInfoRegister)
            {
                source.AppendLine(
                    $"    s[{Gen5NggExports.MergedGroupInfoRegister}] =");
                source.AppendLine(
                    $"        ((sharpemu_mesh_subgroup_verts & 0x{Gen5NggExports.GroupCountMask:X}u) << " +
                    $"{Gen5NggExports.GroupVertexCountShift}u) |");
                source.AppendLine(
                    $"        ((sharpemu_mesh_subgroup_prims & 0x{Gen5NggExports.GroupCountMask:X}u) << " +
                    $"{Gen5NggExports.GroupPrimitiveCountShift}u);");
            }

            // The GS vector inputs pack two 16-bit ES vertex offsets per register
            // (v0 = vertices 0/1, v1 = 2/3, v4 = 4/5), with the primitive id in v2 and the
            // GS invocation id in v3. The ES/VS block follows the GS one, which is why the
            // vertex index lands in v5 and the instance index in v8 — the same registers
            // the per-vertex path seeds.
            var perPrimitive = Math.Max(mesh.VerticesPerPrimitive, 1u);
            source.AppendLine(
                $"    const uint sharpemu_mesh_prim_base = sharpemu_tg_index * {perPrimitive}u;");
            source.AppendLine(
                $"    v[{Gen5NggExports.GsVertexOffset01Register}] = (sharpemu_mesh_prim_base & 0xFFFFu) |");
            source.AppendLine("        (((sharpemu_mesh_prim_base + 1u) & 0xFFFFu) << 16u);");
            source.AppendLine(
                $"    v[{Gen5NggExports.GsVertexOffset23Register}] = (sharpemu_mesh_prim_base + 2u) & 0xFFFFu;");
            source.AppendLine($"    v[{Gen5NggExports.GsPrimitiveIdRegister}] = sharpemu_mesh_prim_slot;");
            source.AppendLine($"    v[{Gen5NggExports.GsInvocationIdRegister}] = 0u;");
            source.AppendLine($"    v[{Gen5NggExports.GsVertexOffset45Register}] = 0u;");
            source.AppendLine(
                $"    v[{Gen5NggExports.EsVertexIndexRegister}] = sharpemu_mesh_vertex_index;");
            source.AppendLine(
                $"    v[{Gen5NggExports.EsInstanceIndexRegister}] = sharpemu_mesh_instance_index;");
        }

        // The dispatcher loop. Unlike the other stages the loop body is the barrier point:
        // see the file header for why S_BARRIER cannot be emitted where it appears.
        private void EmitMeshDispatcher(StringBuilder source, string body)
        {
            source.AppendLine("    for (;;)");
            source.AppendLine("    {");
            source.AppendLine("        if (active && !sharpemu_mesh_wait)");
            source.AppendLine("        {");
            source.AppendLine("            switch (pc)");
            source.AppendLine("            {");
            source.Append(body);
            source.AppendLine("            default:");
            source.AppendLine("                active = false;");
            source.AppendLine("                break;");
            source.AppendLine("            }");
            if (_maxDispatcherSteps > 0)
            {
                source.AppendLine($"            if (++steps >= {_maxDispatcherSteps}u)");
                source.AppendLine("            {");
                source.AppendLine("                active = false;");
                source.AppendLine($"                {StepCapReportCall}");
                source.AppendLine("            }");
            }

            source.AppendLine("        }");
            source.AppendLine();

            // Workgroup rendezvous: every thread reaches it every iteration, so it is the
            // one convergent point in the program. A parked wave resumes only once every
            // thread that is still running has parked too, and a thread that has ended
            // stops being counted, which is how S_BARRIER behaves in hardware.
            source.AppendLine("        if (sharpemu_tg_index == 0u)");
            source.AppendLine("        {");
            source.AppendLine("            atomic_store_explicit(&sharpemu_mesh_live, 0u, memory_order_relaxed);");
            source.AppendLine("            atomic_store_explicit(&sharpemu_mesh_parked, 0u, memory_order_relaxed);");
            source.AppendLine("        }");
            source.AppendLine();
            source.AppendLine("        threadgroup_barrier(mem_flags::mem_threadgroup);");
            source.AppendLine("        if (active)");
            source.AppendLine("        {");
            source.AppendLine("            atomic_fetch_add_explicit(&sharpemu_mesh_live, 1u, memory_order_relaxed);");
            source.AppendLine("            if (sharpemu_mesh_wait)");
            source.AppendLine("            {");
            source.AppendLine("                atomic_fetch_add_explicit(&sharpemu_mesh_parked, 1u, memory_order_relaxed);");
            source.AppendLine("            }");
            source.AppendLine("        }");
            source.AppendLine();
            source.AppendLine("        threadgroup_barrier(mem_flags::mem_threadgroup | mem_flags::mem_device);");
            source.AppendLine(
                "        const uint sharpemu_mesh_live_count = atomic_load_explicit(&sharpemu_mesh_live, memory_order_relaxed);");
            source.AppendLine("        if (sharpemu_mesh_live_count == 0u)");
            source.AppendLine("        {");
            source.AppendLine("            break;");
            source.AppendLine("        }");
            source.AppendLine();
            source.AppendLine(
                "        if (atomic_load_explicit(&sharpemu_mesh_parked, memory_order_relaxed) == sharpemu_mesh_live_count)");
            source.AppendLine("        {");
            source.AppendLine("            sharpemu_mesh_wait = false;");
            source.AppendLine("        }");
            source.AppendLine("    }");
        }

        // Publish the subgroup's output. Exports accumulate into per-thread state during
        // the dispatcher, so the mesh writes happen once, after the whole workgroup has
        // finished and the allocation request is visible to every thread.
        private void EmitMeshEpilogue(StringBuilder source)
        {
            var mesh = Mesh;
            source.AppendLine("    threadgroup_barrier(mem_flags::mem_threadgroup);");
            source.AppendLine($"    if (sharpemu_tg_index < {mesh.MaxVertices}u)");
            source.AppendLine("    {");
            source.AppendLine($"        {MeshOutputName}.set_vertex(sharpemu_tg_index, sharpemu_out);");
            source.AppendLine("    }");
            source.AppendLine();
            source.AppendLine($"    if (sharpemu_tg_index < {mesh.MaxPrimitives}u)");
            source.AppendLine("    {");

            // A culled or never-exported primitive becomes degenerate: Metal has no
            // per-primitive discard, and repeating one index rasterizes nothing.
            source.AppendLine(
                "        const uint3 indices = sharpemu_mesh_culled ? uint3(0u) : sharpemu_mesh_prim;");
            var perPrimitive = Math.Max(mesh.VerticesPerPrimitive, 1u);
            source.AppendLine($"        const uint base = sharpemu_tg_index * {perPrimitive}u;");
            for (var component = 0u; component < perPrimitive; component++)
            {
                source.AppendLine(
                    $"        {MeshOutputName}.set_index(base + {component}u, indices[{Math.Min(component, 2u)}]);");
            }

            source.AppendLine("    }");
            source.AppendLine();
            source.AppendLine("    if (sharpemu_tg_index == 0u)");
            source.AppendLine("    {");
            source.AppendLine(
                $"        {MeshOutputName}.set_primitive_count(min(sharpemu_mesh_alloc[1], {mesh.MaxPrimitives}u));");
            source.AppendLine("    }");
        }

        // ---- instructions ----

        /// <summary>S_SENDMSG GS_ALLOC_REQ reserves the subgroup's export space. M0 carries
        /// the vertex count in bits 0-11 and the primitive count in bits 12-22; the request
        /// is wave-uniform and hardware takes it from the first wave, so the first thread
        /// of the workgroup publishes it for the epilogue.</summary>
        private void EmitMeshAllocationRequest()
        {
            var mesh = Mesh;
            var m0 = $"s[{M0Register}]";
            Line("if (sharpemu_tg_index == 0u)");
            Line("{");
            _indent++;
            Line($"sharpemu_mesh_alloc[0] = min((({m0} >> {Gen5NggExports.AllocationVertexCountShift}u) & " +
                $"0x{Gen5NggExports.AllocationVertexCountMask:X}u), {mesh.MaxVertices}u);");
            Line($"sharpemu_mesh_alloc[1] = min((({m0} >> {Gen5NggExports.AllocationPrimitiveCountShift}u) & " +
                $"0x{Gen5NggExports.AllocationPrimitiveCountMask:X}u), {mesh.MaxPrimitives}u);");
            _indent--;
            Line("}");
        }

        /// <summary>The mesh stage's EXP: positions and parameters land in this lane's
        /// vertex, target 20 decodes the packed primitive connectivity into this lane's
        /// primitive. A lane EXEC removed it keeps the previous value, like the other
        /// stages.</summary>
        private bool TryEmitMeshExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            if (export.Target == Gen5NggExports.PrimitiveTarget)
            {
                EmitMeshPrimitiveExport(instruction, export);
                return true;
            }

            string field;
            if (export.Target == Gen5NggExports.FirstPositionTarget)
            {
                field = "sharpemu_out.sharpemu_position";
            }
            else if (export.Target >= Gen5NggExports.FirstParameterTarget &&
                     export.Target < Gen5NggExports.FirstParameterTarget + Gen5NggExports.ParameterTargetCount &&
                     _vertexOutputs.Contains(export.Target - Gen5NggExports.FirstParameterTarget))
            {
                field = $"sharpemu_out.param{export.Target - Gen5NggExports.FirstParameterTarget}";
            }
            else
            {
                // POS1..POS3 carry clip/cull distances and point size, which the mesh
                // vertex interface does not expose yet; MRTZ never reaches this stage.
                return true;
            }

            var values = new string[4];
            for (var component = 0; component < 4; component++)
            {
                if ((export.EnableMask & (1u << component)) == 0)
                {
                    values[component] = $"{field}[{component}]";
                    continue;
                }

                if (export.Compressed)
                {
                    var packed = $"v[{instruction.Sources[component >> 1].Value}]";
                    values[component] = $"(float)as_type<half2>({packed})[{component & 1}]";
                    continue;
                }

                values[component] = $"as_type<float>(v[{instruction.Sources[component].Value}])";
            }

            Line(ExecGuarded($"{field} = float4({values[0]}, {values[1]}, {values[2]}, {values[3]});"));
            return true;
        }

        // Target 20 exports one dword of primitive connectivity: a 9-bit subgroup-local
        // vertex index per 10-bit field (the field's top bit is that vertex's edge flag)
        // and a null-primitive marker in bit 31. The exporting lane owns one primitive.
        private void EmitMeshPrimitiveExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            var packed = (export.EnableMask & 1u) != 0
                ? $"v[{instruction.Sources[0].Value}]"
                : "0u";
            var value = Temp("uint", packed);
            BeginExecGuard();
            var perPrimitive = Math.Max(Mesh.VerticesPerPrimitive, 1u);
            for (var vertex = 0u; vertex < perPrimitive; vertex++)
            {
                var shift = vertex * Gen5NggExports.PrimitiveIndexStride;
                Line($"sharpemu_mesh_prim[{vertex}] = ({value} >> {shift}u) & " +
                    $"0x{Gen5NggExports.PrimitiveIndexMask:X}u;");
            }

            for (var vertex = perPrimitive; vertex < 3u; vertex++)
            {
                Line($"sharpemu_mesh_prim[{vertex}] = sharpemu_mesh_prim[{perPrimitive - 1}];");
            }

            Line($"sharpemu_mesh_culled = (({value} >> {Gen5NggExports.PrimitiveNullBit}u) & 1u) != 0u;");
            EndExecGuard();
        }
    }
}
