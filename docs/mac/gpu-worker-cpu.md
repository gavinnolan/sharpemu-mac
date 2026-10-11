<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Where the GPU worker's own CPU goes

Status: measured 2026-10-11 on an M4, macOS 27.2, x86-64 under Rosetta, Demon's
Souls load phase, one title at a time on the machine.

## The chain of wrong guesses, and what settled it

Demon's Souls spends most of a run in the GPU worker's own CPU rather than waiting
for the GPU. Three instruments were needed to find out what it is doing, and the
first two answers were wrong.

**`[GPU][PACKETS]` (`SHARPEMU_GPU_PACKET_PROFILE=1`)** attributes the worker's packet
time per opcode. It said `DISPATCH_DIRECT` is ninety-five per cent of it, at eighty-
eight to four hundred and ninety-seven milliseconds per packet — so the cost is not
packet interpretation, not payload reads, not register writes, and not the retries of
suspended submissions.

**That looked like pipeline creation.** Both pipeline paths called
`newComputePipelineStateWithFunction:` straight from the worker, and a window with
forty-one pipelines also reported five seconds of driver time. It was a plausible
inference and it was wrong.

**`pipeline_ms` and `library_ms`** settled it by measuring the two compiles a shader
pays for separately. With pipeline states built on a background pool, `pipeline_ms`
is four to ten milliseconds per window and `library_ms` — the MSL front end — is
twelve to one hundred and twenty-four. Neither can account for six seconds of
dispatch time.

**`[PERF][RENDER]` (`SHARPEMU_PROFILE_RENDER=1`)** names it:

```
ProgramPreparation=56.1%/n465   CommandStream=23.7%/n150  ResourceMaterialization=6.0%/n487  PipelineCreation=0.6%/n451
ResourceMaterialization=30.9%/n1675  CommandStream=48.6%/n449  ProgramPreparation=10.0%/n1612  PipelineCreation=0.2%/n1563
ProgramPreparation=23.5%/n236   CommandStream=55.2%/n110  ResourceMaterialization=19.6%/n245  PipelineCreation=0.4%/n229
```

`ProgramPreparation` and `ResourceMaterialization` are the cost, and
`PipelineCreation` is a rounding error. Note the entry counts: sixteen hundred
materialisations in a window with seventy draws. The preparation runs many times per
draw or dispatch, which is the thing to look at next.

## What this says about the caches

Caching pipeline states across runs (`MTLBinaryArchive`) buys back `pipeline_ms`, and
caching generated MSL on disk buys back the translation behind `library_ms`. On this
title those are four to ten and twelve to one hundred and twenty-four milliseconds
per window against six seconds of preparation, so neither is where the load time is.
They are still worth having for a cold start; they are not the lever here.

## Asynchronous pipeline creation

`SHARPEMU_METAL_V2_ASYNC_PIPELINES=1` builds pipeline states on a bounded pool and
suspends the dispatch packet on a `Pipeline` wait condition until the compile reports
in, so a long compile cannot block every queue. It works — `pipeline_ms` leaves the
worker, every retry finds its pipeline ready, and the blocked-retry fallback almost
never fires — but it is off by default, because a suspended dispatch re-runs
`ProgramPreparation` and `ResourceMaterialization` in front of the pipeline request,
and on this title those are exactly what is expensive. It becomes worth turning on
once a retry no longer repeats that work.
