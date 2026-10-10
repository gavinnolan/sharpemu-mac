<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# NGG primitive shaders as Metal mesh shaders

Status: translator landed 2026-10-10. The render-host side described here is not built yet;
Metal reports the capability only when `SHARPEMU_METAL_MESH_SHADERS=1`, so nothing changes
until the host implements this contract.

## Why

A Gen5 (RDNA2) primitive shader is the merged ES/GS hardware stage. It runs as a
**workgroup**: its waves write vertex data into LDS, synchronize with `S_BARRIER`, reserve
export space with `S_SENDMSG GS_ALLOC_REQ`, and then export positions (`pos0..pos4`),
parameters (`param0..param31`) and primitive connectivity (EXP target 20). A per-vertex
Metal vertex function has none of that: one logical lane, private LDS, no barrier, and a
vertex count fixed by the draw. That is why only *passthrough* primitive shaders ever
worked, and why the executor skipped the rest as "unsupported geometry".

`Gen5MslTranslator` now emits a real culling primitive shader as an MSL `[[mesh]]`
function. The mapping is one-to-one:

| Hardware | Metal |
| --- | --- |
| NGG subgroup | one threadgroup / one mesh grid element |
| wave lane | one thread, on a real simdgroup lane (EXEC and ballots stay exact) |
| LDS | `threadgroup` memory |
| `S_BARRIER` | a workgroup rendezvous in the dispatcher loop (see below) |
| `GS_ALLOC_REQ` | `mesh.set_primitive_count` |
| `EXP pos0` / `EXP param*` | `mesh.set_vertex(thread_index_in_threadgroup, …)` |
| `EXP` target 20 | `mesh.set_index` for this thread's primitive |

There is no `[[object]]` stage. The grid is driven from the host: one threadgroup per NGG
subgroup.

## Selecting the path

`Gen5MslTranslator.RequiresMeshEmission(request)` is true when **both** hold:

1. The request carries `ShaderCompileRequest.PrimitiveShader` — i.e. the draw's
   `VGT_SHADER_STAGES_EN` selected the NGG stage and the backend reports
   `SupportsPrimitiveShaderMeshPipelines`.
2. `ShaderCompileRequest.RequiresPrimitiveShaderWorkgroup(program)` — the program uses
   LDS (any non-GDS `DS_*` other than the swizzle/bpermute pair) or `S_BARRIER`.

`GS_ALLOC_REQ` and the primitive export are deliberately **not** part of the test: every
NGG program issues them, passthrough ones included, and those already work on the
per-vertex path. Only cross-lane LDS and the barrier are things that path provably cannot
express.

The compiled result then has `Stage == Gen5MslStage.Mesh`, `EntryPoint == "gen5_ms"`, and a
non-null `MeshInfo`.

## What the host needs: `Gen5MslMeshInfo`

Everything needed to build the pipeline and launch the draw is on the compiled shader:

| Field | Use |
| --- | --- |
| `ThreadsPerThreadgroup` | `threadsPerMeshThreadgroup` of `drawMeshThreadgroups`, and `MTLMeshRenderPipelineDescriptor.maxTotalThreadsPerMeshThreadgroup`. Always a multiple of 32 (one guest wave32 per simdgroup), at most 256. Also mirrored in `Gen5MslShader.ThreadgroupSizeX`. |
| `WavesPerThreadgroup` | `ThreadsPerThreadgroup / 32`; how many merged waves one subgroup holds. |
| `MaxVertices`, `MaxPrimitives` | The `mesh<>` extents the function declares. The host must not launch a subgroup that needs more. |
| `VerticesPerPrimitive` | 1, 2 or 3, from `VGT_GS_OUT_PRIM_TYPE`. Indices per primitive. |
| `Topology` | `Point` / `Line` / `Triangle`, matching the declared `topology::*`. |
| `ThreadgroupMemoryBytes` | Guest LDS the mesh function declares. Counts against the mesh stage's threadgroup budget together with the output payload. |
| `DrawInfoBufferIndex` (2) | Where to bind the `Gen5MeshDrawInfo` block (below). |
| `IndexBufferIndex` (3) | Where to bind the draw's index buffer as a raw dword stream. |
| `ReadsIndexBuffer` | Currently always true: the slot must be bound even for an automatic draw. |

Buffer slots 0 and 1 keep their existing meaning for every stage:
`Gen5MslArgumentLayout.ResourcesBufferIndex` (0) is the stage argument buffer and
`PushDataBufferIndex` (1) is push data. Bind them to the **mesh** stage
(`setMeshBuffer:offset:atIndex:`), not the vertex stage.

## Pipeline creation

```objc
MTLMeshRenderPipelineDescriptor *d = [MTLMeshRenderPipelineDescriptor new];
d.meshFunction     = gen5_ms;                 // from the translated library
d.fragmentFunction = gen5_ps;                 // the existing pixel translation, unchanged
d.maxTotalThreadsPerMeshThreadgroup = mesh.ThreadsPerThreadgroup;
d.colorAttachments[i].pixelFormat = …;        // same as the vertex path
// no objectFunction, no payloadMemoryLength
id<MTLRenderPipelineState> p =
    [device newRenderPipelineStateWithMeshDescriptor:d options:0 reflection:nil error:&e];
```

**No `MTLVertexDescriptor`.** A mesh function has no `[[stage_in]]`; a primitive shader
fetches its own attributes through buffer loads, which the resource path already
translates. The host therefore binds no vertex buffers for a mesh draw, and
`request.VertexInputs` is ignored on this path.

The fragment interface is unchanged. The mesh vertex payload is declared field for field
like `Gen5VsOut`:

```c
struct Gen5MeshVertex {
    float4 sharpemu_position [[position]];
    float4 param0 [[user(locn0)]];
    …                                  // one per RequiredVertexOutputCount / exported param
};
```

so the paired pixel program built for the vertex path links against a mesh pipeline with
no changes. `Gen5MslShader.AttributeCount` is the param count, as for the vertex stage.

## Launching the draw

```objc
[encoder setRenderPipelineState:p];
[encoder setMeshBuffer:argumentBuffer offset:0 atIndex:0];
[encoder setMeshBuffer:pushData       offset:0 atIndex:1];
[encoder setMeshBuffer:drawInfo       offset:0 atIndex:mesh.DrawInfoBufferIndex];
[encoder setMeshBuffer:indexBuffer    offset:0 atIndex:mesh.IndexBufferIndex];
[encoder drawMeshThreadgroups:MTLSizeMake(subgroups, instances, 1)
        threadsPerObjectThreadgroup:MTLSizeMake(1, 1, 1)
          threadsPerMeshThreadgroup:MTLSizeMake(mesh.ThreadsPerThreadgroup, 1, 1)];
```

- `threadgroupsPerGrid.x` is the number of NGG subgroups:
  `ceil(primitiveCount / mesh.MaxPrimitives)`, and never fewer than
  `ceil(vertexCount / mesh.MaxVertices)`. `primitiveCount` is the draw's primitive count
  for its `VGT_PRIMITIVE_TYPE` (`indexCount / 3` for a triangle list, and so on).
- `threadgroupsPerGrid.y` is the instance count. The shader reads
  `threadgroup_position_in_grid.y` as the instance, so instancing does not need a second
  draw.
- `threadsPerObjectThreadgroup` is `(1,1,1)`; there is no object stage.
- Draw state the vertex path sets (viewport, scissor, depth/stencil, blend, cull mode)
  is unchanged. Cull mode should stay as the guest programs it: the primitive shader
  culls by marking primitives null, which this path lowers to degenerate triangles.

## `Gen5MeshDrawInfo` (buffer 2)

Sixteen dwords, 64 bytes — `Gen5MslMeshInfo.DrawInfoByteSize` /
`DrawInfoDwordCount`. It is what the geometry engine would have put in hardware registers;
the mesh prologue reconstructs the merged ES/GS launch state from it plus the thread's
position.

| Dword | Field | Value |
| --- | --- | --- |
| 0 | `vertices_per_subgroup` | ES vertices in one subgroup. `VGT_GS_ONCHIP_CNTL.ES_VERTS_PER_SUBGRP`, else `GE_MAX_OUTPUT_PER_SUBGROUP`. Clamped in-shader to `MaxVertices`. |
| 1 | `primitives_per_subgroup` | `VGT_GS_ONCHIP_CNTL.GS_PRIMS_PER_SUBGRP`, else `GE_MAX_OUTPUT_PER_SUBGROUP`. Clamped to `MaxPrimitives`. |
| 2 | `index_count` | Indices the draw has. Zero means an automatic (non-indexed) draw. |
| 3 | `index_type` | 0 none, 1 = 16-bit indices, 2 = 32-bit. |
| 4 | `first_index` | Index of the draw's first index, in elements. |
| 5 | `base_vertex` | Signed; added to each fetched index. |
| 6 | `instance_count` | Matches `threadgroupsPerGrid.y`. |
| 7 | `first_instance` | Added to `threadgroup_position_in_grid.y`. |
| 8 | `vertex_group_size` | `GE_CNTL.VERT_GRP_SIZE`. Informational today. |
| 9 | `primitive_group_size` | `GE_CNTL.PRIM_GRP_SIZE`. Informational today. |
| 10 | `user_vector_enable` | `GE_USER_VGPR_EN` bits 0-2. Informational today. |
| 11 | `primitive_amplification` | `GE_NGG_SUBGRP_CNTL`. Informational today. |
| 12 | `object_id` | `VGT_OBJECT_ID`. Informational today. |
| 13-15 | reserved | Zero. |

The index buffer at slot 3 is read as a dword stream: a 16-bit index is the containing
dword shifted, so one binding serves both types. For an automatic draw the lane's vertex
index is its slot in the grid, and the slot must still be bound (any non-empty buffer).

Nothing in this block is part of the compiled program's cache key, so one pipeline serves
every draw that differs only in counts, index buffer or instancing. The subgroup *budget*
(dwords 0-1 and the output primitive type) **is** part of the key, via
`Gen5NggStageInfo`, because the `mesh<>` extents are compile-time constants.

## How the generated shader seeds the NGG system values

From `Gen5NggExports` (the one place these encodings live) and the thread's position:

- **`s3`, merged wave info** — ES vertex count of *this wave* in bits 0-7, GS primitive
  count in bits 8-15, GS wave id in 16-23, subgroup id in 24-27, wave count in 28-31.
  The guest rebuilds EXEC from the first pair, so this has to be right or the program
  either kills every lane or waits forever on lanes that do not exist.
- **`s2`, merged group info** — the workgroup's ES vertex count in bits 12-20 and input
  primitive count in bits 22-30. The ordered-append id below them stays zero.
- **`v0`/`v1`/`v4`** — GS vertex offsets, two 16-bit ES slots per register.
- **`v2`** — primitive id; **`v3`** — GS invocation id.
- **`v5`** — ES vertex index (after the index fetch and `base_vertex`); **`v8`** —
  instance index. These are the registers the per-vertex path already seeds.

A system scalar is written **only** when it is not also a user-data register for this
program (`UserDataBase > n`). For a graphics stage `UserDataBase` is 8
(`ShaderPipelineCache.VertexUserDataBase`), so `s0..s7` are system scalars in practice.

## The barrier, and why it is not where it looks

The translated program runs inside the `while (active) { switch (pc) }` dispatcher. Only
scalar branches move the pc, so the pc is **wave**-uniform — but two waves of the same
subgroup can sit at different pc values, and a `threadgroup_barrier` reached by a subset of
the threadgroup is undefined in Metal (in practice, a hung queue).

So `S_BARRIER` ends its basic block and parks the wave (`sharpemu_mesh_wait = true`), and
the dispatcher's own loop body carries the only barriers:

```c
for (;;) {
    if (active && !sharpemu_mesh_wait) { switch (pc) { … } /* step limit */ }

    // zero the counters, barrier, count live and parked threads, barrier
    if (live == 0u) break;
    if (parked == live) sharpemu_mesh_wait = false;
}
```

Every thread runs the loop the same number of times, so both barriers are convergent. A
parked wave resumes only once every thread that is still running has parked too, and a
thread that has ended stops being counted — which is also what the hardware does when a
wave ends while others wait on the barrier. `SHARPEMU_SHADER_MAX_STEPS` still bounds the
loop.

Cost is two threadgroup barriers and a few atomics per **block** execution, not per
instruction. A single-wave subgroup degenerates to nearly free.

## Culling

Metal cannot drop a mesh primitive in place. A primitive whose export set the
null-primitive bit (31), or that no lane exported at all, is emitted with three identical
indices: a zero-area triangle rasterizes nothing. `set_primitive_count` additionally
reflects what `GS_ALLOC_REQ` asked for, so slots past the allocation are never read.

## Open questions

These are the parts the synthetic fixtures pin but the guest has not confirmed:

1. **Primitive assembly for the GS vertex offsets.** `v0`/`v1`/`v4` are seeded as if the
   subgroup were a triangle *list*: primitive *p* uses ES slots `3p, 3p+1, 3p+2`. Strips,
   fans and indexed draws with vertex reuse pack the subgroup differently, and
   `GE_CNTL`'s group sizes are what bound that packing. A program that reads these offsets
   rather than exporting connectivity it computed itself will be wrong.
2. **Passthrough primitive input.** In NGG passthrough mode the hardware hands the shader a
   pre-packed primitive in a VGPR instead of letting it build one. That register is not
   seeded, because such programs still take the per-vertex path (no LDS, no barrier). If
   one ever needs the mesh path, the packing it arrives in has to be settled first.
3. **`GE_NGG_SUBGRP_CNTL`.** The Void capture has 2 here with no geometry program, which
   reads like `PRIM_AMP_FACTOR = 2` — primitive amplification without a GS. It is
   transported as `PrimitiveAmplificationFactor` and otherwise unused; a program that
   really amplifies would need `MaxPrimitives` scaled by it.
4. **`VGT_GS_ONCHIP_CNTL` field offsets.** The register's `ES_VERTS_PER_SUBGRP` /
   `GS_PRIMS_PER_SUBGRP` field *names* and their role are public (Mesa's `G_028A44_*`
   macros); the exact bit positions are not, so `ShaderInterfaceRegisters`' decode is
   inherited rather than verified. The clamp to Metal's extents makes a wrong decode a
   performance or coverage problem, not a crash.
5. **Barrier uniformity across waves.** The rendezvous makes a non-uniform barrier safe
   rather than fatal, but it does not make it *correct*: if one wave takes a scalar branch
   that skips a barrier the others execute, the parked waves are released early. Mesa
   emits NGG barriers at top level precisely because the hardware requires workgroup
   uniformity, so real programs should not do this — but nothing here proves it.
6. **Position slots beyond `pos0`.** `pos1..pos4` (clip/cull distances, point size) are
   dropped, as on the per-vertex path. The mesh vertex payload would need the
   corresponding `[[clip_distance]]` / `[[point_size]]` members.

## Tests

`tests/SharpEmu.ShaderCompiler.Metal.Tests`:

- `NggMeshTranslationTests` — structure of the emitted MSL and of `Gen5MslMeshInfo`,
  including that passthrough programs and programs without the draw's primitive-shader
  state keep the per-vertex path. Runs everywhere.
- `NggMeshRuntimeTests` — needs a Metal device
  (`SHARPEMU_TEST_REQUIRE_DEVICE=1 arch -x86_64 ~/.dotnet-x64/dotnet test …`). The emitted
  MSL must compile with the OS Metal compiler, and a synthetic primitive shader must
  rasterize through a real mesh pipeline: it exchanges its vertex value across the
  subgroup's two waves through LDS, synchronizes, allocates one triangle, exports it, and
  the render target has to show that triangle with the exported parameter interpolated to
  its exact barycentric value. The null-primitive bit must leave the target empty.
- The preserved Void Terrarium capture (707 instructions, 19 `DS_WRITE_B128`, six
  `S_BARRIER`, `S_SENDMSG` message 9, `EXP` target 20) is translated and compiled when it
  is present on the machine, and skipped otherwise. No guest shader bytes are in the
  repository.
