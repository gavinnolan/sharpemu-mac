# Void NGG original draw prerequisite

This source-only checkpoint is based on `d263927890143a98590d37da45480f346463f0db`, whose native mesh transport fixture qualifies the host transport for a synthetic two-primitive quad. That does not translate the captured guest shader or prove its launch ABI.

The preserved actual guest capture is outside the repository source tree at `artifacts/validation/void-skipped-shader-400e5c64/shader-capture/skipped-export-00000070E58AEE00.{json,range-0.bin}`. The original 4,352-byte binary SHA256 is `ee791016d39cf2b95512cf7b2819c9e1f151ebaf80fcf527692fdf85d6cfec89`. No game shader bytes are added to source or test fixtures.

Its 707 decoded instructions include 19 `DsWriteB128`, ten `DsReadB128`, six `SBarrier`, `SSendmsg` message9 and an `Exp` primitive target20. The preserved text has 20 scalar conditional branches, all forward. This makes forward reconverging structured emission a relevant next implementation; it is not proof of the required wave/cohort uniformity. A native mesh implementation must use shared threadgroup LDS, execute every workgroup barrier through a proven reconverged path, interpret the guest allocation request and primitive/vertex exports, and seed the actual GS system registers. The current single-invocation/private-LDS vertex translator and its message no-op cannot implement this program.

The original capture includes stage mask `0x00402020`, subgroup control2, max output128, max GS vertices4, triangle output2, LDS size field18 and 12 user scalar registers. It omits the draw's input count, instance/first-instance, base/first vertex, index address/type/offset source, primitive assembly group sizes, on-chip input counts and ES/GS ring item size. Therefore the original data does not establish the inputs required to seed merged-wave/workgroup information or packed GS input vertex offsets. Neither the absent geometry program address nor the stage mask alone proves a passthrough ABI.

With the existing default-off `SHARPEMU_DUMP_SKIPPED_GRAPHICS=1` and `SHARPEMU_SHADER_SPIRV_DUMP_DIR` settings, this successor captures the original `TargetlessDrawArguments` at both public rejection sites. The automatic or indexed packet record retains its submission, packet address/opcode, original count/instance/base/first inputs, index address/type, indirect argument address, offset source and unbounded-index flag. The JSON adds primitive type/index offset/reset/object/group/vector-enable state and original on-chip input counts, geometry instance count, ring size, primitive-ID/reuse/index-export registers.

No additional indirect arguments, index contents or resource descriptors are read to invent resolved inputs. When the existing CPU-indirect path already resolves a packet, the diagnostic retains both its original packet record and the actual boundary-resolved record; it does not silently lose the original indirect address when that path clears it. For a GPU-indirect draw, the original packet fields may describe a bound rather than the actual runtime count; the explicit indirect address and source must remain part of the qualification. The capture still retains only the first eight distinct rejected export addresses and preserves its existing bounded code range checks and best-effort failure behavior. The stage mask, skip predicate, register validation, vertex translation and all successful draw paths remain unchanged. Legacy pipeline-provider diagnostic implementations keep the original default callback through an overload.

Four prepared managed rows exercise actual public indexed/automatic draw rejection with capture on and off, asserting exact original arguments/context transport, no guest reads, no image/pipeline resolution, and no draw execution. A fifth actual public CPU-indirect row retains original and resolved counts/index/offsets, with exactly the inherited one argument read and no added index or descriptor read. The six existing unsupported-geometry rows and admitted primitive-vertex regressions remain relevant. Nothing has been built or run under this source-only checkpoint.

Read-only reference: `REmu/crates/remu-shader-metal/src/compiler/{driver,control_flow,export,module}.rs` and `src/lib.rs` expose the separate structured-barrier and `NggRegisters` prerequisites. Its mesh driver refuses dispatcher barriers and unproven packed allocation inputs, rather than replacing them with private LDS or a guessed output count. This is architectural guidance; the SharpEmu source must independently transport and validate its own original draw/register inputs before any guest mesh admission. The next implementation must retain the existing rejection until the draw ABI, wave-uniform scalar branches, exact shared LDS bound, allocation/export domain and native full-byte/primitive controls are qualified.

## Bounded rejected-input capture successor (source-only)

This separate successor starts from qualified main `000b688f3fb92ba032b9b2d6bf969c3082e81346`. It preserves the original rejected-draw/code capture and every rejection/admission/translation predicate. It adds a diagnostic CPU input observation under the same explicit capture settings, using only the transported original/boundary draw addresses and counts. No game binary, shader or draw payload is committed.

For each of at most eight export addresses, four attempted draws may capture a maximum 256 KiB index prefix and an unresolved indexed-indirect argument record of exactly 20 bytes. Attempts consume the budget even when memory/file capture fails. Zero/overflow addresses and unsupported element types refuse before a read; unreadable/throwing reads discard partial bytes. CPU-resolved indirect records retain both original and boundary counts and do not reread the earlier argument buffer. GPU-indirect observations never resolve or rewrite the live draw; transported index counts may be buffer bounds rather than actual draw counts. Automatic draws perform no index/indirect pointer reads.

Files are written only inside the privately configured dump directory: `skipped-export-<address>.draw-<ordinal>.json`, with optional `.indices.bin` and `.indirect.bin` companions. The JSON detaches original and resolved packet records, original scalar controls and ES/GS user-data arrays before input reads. Source metadata hashes include exact source addresses/requested sizes, original/resolved counts and the CPU observation classification. Each payload provenance hash also includes captured size, completeness, read status, that metadata hash and its raw content digest. The schema references the separate original code capture by address/file, explicitly without equating mutable code at different times.

Raw CPU index min/max includes restart values and is not a reconstructed vertex/primitive domain. Actual packed GS launch inputs, shared LDS contents, allocation-message results and emitted vertex/primitive counts remain explicitly **uncaptured**. Neither bounded CPU bytes nor register scalars certify a coherent GPU snapshot, mapping lifetime, launch ABI or executable NGG program. No register value is guessed to be a resource pointer, and no payload is used for admission, shader lowering or drawing.

The default-off path creates no input helper, additional formatter, file or guest read. Input reads/output occur after the existing pipeline-cache gate is released. Optional diagnostic failures preserve the original rejected-draw result. Seventeen prepared managed rows cover exact 8/16/32-bit source bytes, truncation, invalid/overflow/unreadable/throwing reads, original versus resolved indirect transport, automatic no-pointer behavior, both attempt budgets, provenance changes and the actual pipeline diagnostic entry with capture enabled/disabled. They supply no native, coherent-content or game execution evidence; none have been built or run at this source checkpoint.

The additive diagnostic-consistency successor preserves c7 unchanged. An unbounded index range now reports counts unknown consistently with its source/payload metadata even when no indirect address remains. One original export address is captured before budget reservation and reused for metadata, output filenames and code-reference paths across all read callbacks. Two new managed controls cover the unbounded-count case and a guest-read callback that mutates live export registers; original filename and both provenance hashes must remain those of the detached pre-read facts. Updated prepared count: nineteen managed rows, still unbuilt/unrun. No draw-policy or input-read bound changes.

## Current boot candidate selective port (source-only)

This isolated candidate ports the exact corrected four-path 7dcda7d6 delta
(c7acd286 plus its two reviewed consistency corrections) onto frozen
45d97101d3056de105d4dbfd97930d194eea7f00. The patch applied without conflicts;
no DS Draws, queue, typed native scope or CPU continuation source changed. All
three imported C# blobs match 7dc exactly; reversing that exact source delta
and restoring the inherited document restores the full 45 baseline.
The prior c7 and 7dc statements above describe historical source checkpoints,
not runtime qualification of this new port.

Nineteen source-derived managed rows are added, so the combined expected count
is 1,016 (199 Host, 712 Libs, 105 GPU) per runtime. The 87 native workers are
unchanged. These counts require compiled discovery by the exclusive executor.
Neither diagnostic flag is activated; no input payload, shader binary or game
data is committed. No build, test, discovery, native worker, publish or game
ran for this candidate. The actual Void shader remains rejected, and the
uncaptured packed GS/LDS/export domains remain prerequisites.
