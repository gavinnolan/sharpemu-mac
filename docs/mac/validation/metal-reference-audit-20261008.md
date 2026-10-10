# Native Metal reference audit, 2026-10-08

This is a read-only audit and implementation proposal. It does not enable the
private texture-source graph, certify a reference emulator's correctness, or
establish 60 FPS/gameplay acceptance. The parent owns the serialized nine-game
baseline; runs during builds are blocker evidence, not uncontended benchmarks.

## Examined revisions and scope

- REmu: `/Users/xnetcat/Projects/xnetcat/REmu`,
  `e7d54559a205da2dd0fc1a6d8b0ac620644af8c0`. The existing untracked
  `docs/notes/upstream-audit-20260823.md` was left untouched.
- Kairo: `/Users/xnetcat/Projects/xnetcat/kairo`,
  `755e7657820161dd90ac93a93eb80d833eecc394`, clean.
- SharpEmu source expressions were read from root and indexed by exact revision
  and SHA256 in `artifacts/validation/metal-agent/reference-audit-20261008/host-source-results.json`.
  Frozen owned-BGRA prerequisite `eff3cbc` has a separate managed manifest.

No reference repository was edited or built. Comments describing reference-game
speedups were read as implementation motivation, not reproduced measurements.

## Current earliest blockers take precedence over historical aliases

| Title | Fresh parent baseline | Next bounded implementation/proof |
| --- | --- | --- |
| Demon's Souls | Storage selected mip1: 512x256, format4/7, tile9, resource mip chain9, view mip count1; `StorageTextureFormat` rejects | Materialize the selected mip's actual offset, extent, pitch and retained bytes. A metadata-only guard relaxation would upload allocation mip0. |
| Silent Hill | Storage type13, arrayed, one layer, 1x1, format10/0, tile27 | Allocate/bind a native 2D-array texture even with one layer; qualify production store/load, exact seed and singleton slice stride. |
| Quake | 8192x8192 format4/7 tile24, depth comparison, 268,435,456 logical bytes exceeds 134,217,728 snapshot cap | Qualified large-image admission, bounded staging, exact source ownership and independent tile24/depth-comparison oracles. |
| Poppy | Existing separately qualified R16Uint 16-cube thick3D path; later game outcome belongs to current parent sweep | Retain actual XYZ geometry. Extend a particular next descriptor only after raw metadata and independent address/upload/store/read proof. |

Fresh DS/SH details are parent-reported exact `8c19cd7` log findings; they supersede
the earlier producer219/consumer231 alias as the first failing operation. The
older cross-queue alias remains an unresolved downstream prerequisite.

## 1. Selected subresources: transport actual placement, not labels

Kairo's `texture.rs:mip_layouts`, `read_level_storage` (around1331-1425) and
`cube.rs` transport an explicit selected-level offset, width/height, layer count
and physical chain stride. `MAX_MIP` describes allocation layout; it does not
force the view to expose every allocation level. Its `render/resident_image.rs`
read-only resident path separately checks level, offset, shape, native format
and exact layout before using a GPU texture.

SharpEmu already has `GnmTiling.TryGetMipChainPlacement` and separately qualified
base0 storage materialization. Proposed next change:

1. Add a pure immutable selected-subresource plan derived from all eight raw
   descriptor words plus actual shader shape/access. Carry source offset,
   physical byte footprint, logical extent, row/slice pitch and native view
   level mapping; reject contradictory DTO/raw fields.
2. Extend CPU snapshot creation to read/detile the selected physical mip,
   including tail placement and array stride, before declaring it stable.
   Keep the original copied bytes and observation identity; never label mip0
   bytes as mip1 or stamp them with a later tracking generation.
3. Upload that selected storage level into a native one-level texture view with
   the dimensions the translated operation actually addresses. Preserve native
   type13 array semantics at one layer. Storage access must still be classified
   from Read/Write/Atomic intent rather than IsStorage.
4. Use different sentinel contents in every physical mip/layer. Exercise actual
   descriptor normalization -> snapshot -> production storage materializer ->
   translated MIMG store/read with full byte oracle. Include nonzero selected
   mip, tail, mismatched raw metadata, singleton array and last-row pitch.

The plan must decide whether compiled coordinates address an already selected
native view or include a separate guest mip operand. This is a decoded MIMG/view
semantic requirement, not simply `MipLevel == 1` metadata support.

## 2. Quake large images: bound staging and own the source

REmu `presenter/tiler/encode.rs:31-43,173-188,478-652` exposes only committed
shared guest backing as a native buffer, then compute-detiles and blits into the
native texture on the guest command buffer. Missing shared identity is rejected.
Its mode24 uses a dedicated address equation within the mode27 library and
explicitly refuses mode24 mip chains. It still allocates a full linear private
buffer (and can allocate a full alignment scratch buffer): adopting this code
alone would not bound our peak native memory.

Kairo `texture.rs:1353-1425` can detile directly into caller-provided staging;
read-only contiguous images can borrow arena backing instead of copying tiled
bytes. The caller settles producers first. That memory/ownership premise cannot
be inferred from our global mapping generation or ordinary write-watch token.

For 8192² R32 data, a 256-row band is8MiB and the final native image is256MiB.
A useful bounded strategy is a selected-subresource/band planner plus an
8MiB staging ring reclaimed only after its consuming command buffer completes.
Every band is covered exactly once, includes source tile rows and absolute
XOR coordinates, and writes its native destination origin. The final native
allocation and any retained immutable source/scratch bytes are separately charged
before admission; allocator size, not just logical texel bytes, drives telemetry.

There are two valid source premises to develop:

- A fully owned immutable CPU snapshot with explicit large-image admission and
  bounded total queued bytes (still256MiB retained, not free just because staging
  is8MiB).
- A real captured backing object/read lifetime plus ordering against CPU/GPU
  writers through completion. A late raw guest-address read is insufficient.

The second premise needs a cross-provider backing/read contract. REmu's
`remu-memory/address_space/shared.rs:1331-1420` shows a backing-retaining
`GuestRangeLease`; it distinguishes object identity from the current licence to
publish. A retained object alone also does not freeze concurrent CPU writes.
Until one premise is proven, retain explicit rejection instead of raising the
universal128MiB cap or lazy-reading tiles after submission.

Before integration, qualify: all-byte asymmetric tile24 data, band boundaries,
unaligned/padded views, source replacement during preparation, later writer
before consumer, resource-budget rejection/rollback, allocation/encoder failure,
and native completion/error/cancel release. Then rerun Quake's exact descriptor.

### Reference disagreement prevents blind equation transfer

An exhaustive read-only source-expression harness compared the current Sharp
`RbPlus64KDepthX` 4-byte table with REmu's current
`mode24_within_block_byte_offset_for_format`. Both map a128x128 block bijectively,
but15,360 of16,384 offsets differ. First mismatch: `(8,0)` is4096 in Sharp and256
in REmu. High-coordinate pipe-XOR probes disagree too.

Sharp labels the Oberon16-pipe/8-packer RB+ topology; REmu cites a KytyPS5 depth
reference. Neither label resolves the disagreement. Independently generate the
actual target topology with AMD's open AddrLib and keep the revision/topology,
raw descriptor, generated fixture and native oracle together. Existing Shader
CPU/GPU agreement is not independent when both use one shared wrong table.
The audit changes no equation.

## 3. Depth comparison is a separate shader qualification

Current `Gen5MslArgumentLayout.cs:150-163` declares ordinary `texture2d<float>`.
`Gen5MslTranslator.Pixel.cs:350-484` samples the texture, then compares the single
filtered red value against the reference and broadcasts the result. The comment
calls this manual PCF, but a comparison after interpolation differs from
interpolating per-texel comparisons. The arithmetic counterexample in the audit
uses depths0.2/0.4/0.6/0.8 with reference0.5: current result1, per-texel average0.5.
Nearest-filter sampling is a separate control.

Current R32Float materialization therefore matches the declared Metal slot;
there is no depth2d/R32 native-type mismatch in this current code. A future native
`sample_compare` path requires coordinated typed depth declarations, comparison
sampler decoding and native depth-format materialization. An alternate exact
manual path must implement the guest compare operation, filtering, addressing,
LOD and derivative semantics. Qualify decoded sample-C fixtures and actual Metal
nearest/linear comparison results before claiming shadow correctness. Budget
qualification alone does not certify those results.

## 4. DS aliases: byte authority and retained versions

REmu's `texture/resident_map.rs:130-225` centralizes retirement. Dirty/GPU-authored
residents cannot be deleted without an exact publication or transferred-authority
receipt. Its physical range sets in `texture/ranges.rs` distinguish byte
ownership from exact GPU publication debt; debt cannot widen over holes. Its
`texture/residency.rs` rematerializes from current GPU owners and shared backing
identity instead of substituting stale guest RAM. `texture/correlation.rs`
correlates backing ID/offset, not only virtual address.

Kairo's resident path avoids readback/detile/upload for exact native-compatible
images on a serial queue. Its `owed` state records that guest memory has not
received GPU results; a read-only sample does not retimestamp that debt. This
ordered synchronous-IB assumption cannot be transplanted unchanged into our
concurrent preparation/logical queue admission.

Transfer into the dormant Sharp graph in increments:

- Preserve exact producing view/format/type and producer-work identity. Matching
  address or a scored candidate is not sufficient certification.
- Capture immutable version before publishing own writer; reserve queued input
  ownership atomically with admission. A later writer or registry replacement
  cannot change the captured version.
- Only ready queue heads may execute, preserving logical FIFO and allowing the
  producer/unrelated queue to progress; native encoded-copy ordering remains
  distinct from hardware completion.
- Retire native storage only after completion pins drop. Preserve GPU-authority
  tombstones when bytes are not coherently back in guest RAM. Eager private
  versions remain a correctness-first option but copy traffic/budget must be
  measured before optimization.
- General format reinterpretation, depth/storage/presentation writers, CPU read
  backing ownership and all required mutators still need qualification. Keep
  the graph hardfalse rather than describing these reference mechanisms as an
  activation proof.

## 5. Faster loads: single-flight native libraries and pipeline archives

REmu `shader/archive.rs:480-560` claims an exact MSL source before dropping its
cache lock, compiles one stage once, and wakes all waiters with Ready/Failed.
Library loading and pipeline variants reuse the function. `shader/compile.rs`
persists metallibs by source/schema; `shader/archive.rs` persists typed render
and compute pipelines, quarantines invalid archives and bounds checkpoints.
The archive schema/device/architecture key is explicit. Its runtime worker
channels are bounded and hold Pending results; this is not proof that all Apple
compilation calls are themselves asynchronous.

Kairo's `program.rs` assigns a stable identity to identical code bytes despite
neighboring page writes; Unknown stamps force reread. It reuses content identity
across equal-code refreshes, avoiding variant multiplication. Its inspected
render/compute compile path remains synchronous and in-memory.

Sharp currently creates native Metal functions with `newFunctionWithName:` per
pipeline request in `GetShaderFunction`; its CachedLibrary is separate from the
native function/pipeline lifetime. Native pipeline creation is performed later
on the render path, while the shared pipeline interface already supports a
pending compute result. Proposed native-only increments:

1. Exact-key single-flight libraries/functions with explicit owned handles,
   failed-state diagnostics and balanced release. Keys include generated source,
   entry point, translator ABI/math options; pipeline keys include every render
   target/depth/sample/blend/vertex/layout variant.
2. Versioned persistent binary archives scoped by translator ABI, GPU identity,
   runtime architecture, compiler options and OS/driver compatibility. Atomic
   writes; stale/corrupt archives are quarantined and compilation stays native
   Metal. Do not serialize the growing archive per draw or silently return an
   approximate shader.
3. Bounded background compilation with retained owned inputs and one pending
   promise per key. Required work remains ordered while compilation completes;
   cancellation/failure wakes consumers. No skipped draws or placeholder
   pipeline. ABI/native block lifetime tests precede async API adoption.
4. Track cold vs warm translation/library/pipeline time separately. Repeated
   launch tests must prove hits execute the same shader and changed source/options
   miss correctly. Report first interactive frame/load duration, not only shader
   count or display refresh.

## 6. Completion and memory reclamation

Kairo `bump.rs:1-175` parks upload slabs on the exact committed command buffer,
then reclaims after completion; partial preparation holds prevent an intermediate
flush from recycling live bindings. Sharp already has an8MiB upload arena with
completion-tagged pages, so a new arena is not the next improvement.

REmu `completion.rs:18-120` registers a completion notification before commit and
uses a bounded condition wait, then rechecks the exact native status. Timeout
keeps resources and ordered completion effects retained. Its batch settlement
has distinct Completed/Failed/Pending states.

Sharp's `WaitForCommittedCommandBuffer` and dormant source shutdown still use
unbounded `waitUntilCompleted`. A narrow native completion bridge can replace
those waits and poll-heavy status paths with per-command-buffer terminal receipts.
It must retain objects/resources until true terminal status, surface the real
error, and quarantine on deadline. Register callbacks before commit; callbacks
must not acquire presenter locks or perform guest completion work. CPU close
completion and GPU drain completion remain independent. This is a future
implementation, not qualified by the managed borrow tests.

## Apple primary documentation consulted

- [CPU texture replacement](https://developer.apple.com/documentation/metal/mtltexture/replace(region:mipmaplevel:withbytes:bytesperrow:)) immediately copies source bytes but does not synchronize against GPU memory operations. Our new CPU borrow scope therefore does not certify reuse of one texture while prior GPU reads remain in flight.
- [Completion handlers](https://developer.apple.com/documentation/metal/mtlcommandbuffer/addcompletedhandler(_:)) must be registered before commit; the handler is a place to inspect exact status/error.
- [Asynchronous render pipeline creation](https://developer.apple.com/documentation/metal/mtldevice/makerenderpipelinestate(descriptor:completionhandler:)) allows compilation away from the caller, with an explicit completion result.
- [Binary archives](https://developer.apple.com/documentation/metal/mtlbinaryarchive) store compiled pipeline descriptors/code.
- [Recommended working set](https://developer.apple.com/documentation/metal/mtldevice/recommendedmaxworkingsetsize) is performance guidance, not a successful-allocation guarantee. Account actual texture/buffer footprints separately from retained CPU bytes.
- [AMD PAL](https://github.com/GPUOpen-Drivers/pal) exposes hardware swizzle equations and is a primary source direction for the independent topology oracle.

## Evidence and acceptance measurements

`owned-cpu-frames-eff3cbc/source-results.json`: exact clean source,177/177 ARM64
and177/177 Rosetta, identical cases, no failed/skipped controls. Fifteen added
cases exercise real accepted transfer/rejection, original-receipt borrowing,
Return/Rent replacement, close wait and accepted-then-thrown cleanup. Upload is
stopped before native access in the managed case; no GPU-copy claim.

`reference-audit-20261008/host-source-results.json`:4,096 R16 16-cube offsets
match Kairo's actual3D dispatch (its generated table index95 is not AMD's equation
number81); both cover8,192 bytes uniquely. The depth-table disagreement and
comparison counterexample are retained. The script parses actual source
expressions, not a native/CLR/guest-ISA implementation execution. Timings are
harness timings, not a detile-speed benchmark. The initial harness syntax failure
and assertion-triggered depth mismatch are preserved.

For all nine titles, acceptance remains: reproducible startup -> menu -> actual
playable scene, input and sustained motion, correct image/sound, graceful stop,
and separately measured cold/warm loading plus uncontended gameplay frame times.
60 FPS means sustained guest gameplay with frame-time distributions and stable
memory, not drawable refresh, encoded splash FPS or interpolated frames.
