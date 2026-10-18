# GOAL.md backlog: findings deferred to a later phase

Items found while working on one phase that belong to another (GOAL.md: strictly one phase at a time). Each names its source.

## Phase 2 (loading)
- Shader compilation blocks the GPU worker: cold MSL compile cost 213 s of worker waits before gameplay and pipeline-state creation another 82 s (run p1-b2, artifacts/phase1/batch2-analysis.md §1, §7 item 3). Create render/compute pipeline states on the async compiler (`MetalPipelineCompiler`, today compute-only behind SHARPEMU_METAL_V2_ASYNC_PIPELINES); give runtime misses priority over prewarm in `MetalLibraryCompilePool`; persist compiled code with `MTLBinaryArchive` so a cold or evicted OS Metal cache costs seconds, not minutes.

## Phase 3 (performance and stability)
- ~250-300 ms per in-world frame unattributed at 1.8 fps (batch2-analysis.md §6): needs a per-thread sample on a warm-cache run.
- Deferred-label 7 s wait and negative timestamp in slow-wait diagnostics (batch2-analysis.md §8).
