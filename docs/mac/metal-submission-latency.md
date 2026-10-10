<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# What delays a Metal submission

Status: measured 2026-10-11 on an M4, macOS 27.2, x86-64 under Rosetta.

## The observation

Titles that submit rarely looked as though the driver sat on their command buffers.
Octopath Traveler II ran at five frames a second with

```
[METAL-V2][PERF] gpu_busy_ms=0.0 completions=20 completion_ms=7712.7 host_waits=0 idle_ms=1836
[METAL-V2][SLOW-WAIT] total_ms=312.2 commit_to_kernel_ms=339.3 gpu_exec_ms=0.00 kernel_ms=0.01
```

— three hundred and forty milliseconds between `-commit` and the driver starting to
schedule the submission, with the GPU then running it in ten microseconds, one guest
window resident and the worker idle for ninety per cent of the window. Void Terrarium
and Silent Hill showed the same shape, and titles that submit two thousand buffers
per two seconds did not.

## The control

`scripts/macos/metal-submit-latency.m` is the same experiment outside the emulator:
a hundred lines that commit trivial command buffers at a fixed interval and report
`commit → kernelStartTime` and `commit → completion`. It builds for x86-64 and runs
under Rosetta, as the emulator does.

On an **idle machine** every configuration is clean — median `commit → kernel` of
0.02 to 0.16 ms and median `commit → done` of 0.5 to 1.5 ms, with no completion over
fifty milliseconds in any case:

| case | `commit → kernel` median | `commit → done` max |
| --- | --- | --- |
| plain blit, every 1 / 20 / 100 ms | 0.02 / 0.06 / 0.11 ms | 2.2 / 2.2 / 8.2 ms |
| MTLSharedEvent signal + completion handler, every 100 ms | 0.08 ms | 6.0 ms |
| a fresh queue per submission, every 100 ms | 0.06 ms | 20.1 ms |
| 4 GiB `newBufferWithBytesNoCopy` window referenced, every 100 ms | 0.08 ms | 2.0 ms |
| the same, hazard-tracked instead of untracked | 0.06 ms | 2.7 ms |
| the same, pre-faulted instead of lazily backed | 0.06 ms | 2.4 ms |
| five such windows mapped at once, every 100 ms | 0.16 ms | 3.4 ms |
| 2048 pages `mprotect`ed read-only and back per round (2.3 M calls), every 100 ms | 0.04 ms | 0.7 ms |
| a second queue presenting to a CAMetalLayer at 120 Hz, reading the texture the first queue writes, every 100 ms | 0.02 ms | 2.7 ms |

So none of these is the cause: not the submission rate, not the GPU power state, not
the shared event, not the completion handler, not the size or no-copy mapping or
hazard mode or lazy backing of the guest windows, not the page guard's `mprotect`
traffic, and not the presenter reading a guest texture from a second queue while it
hands drawables to the compositor.

## What does cause it

**Another process using the GPU.** The same probe, run while a game was playing in
another window, produces exactly the reported symptom: one submission in eight stalls
for 350 to 960 ms, while `commit → kernel` stays at a tenth of a millisecond. Running
it again on an idle machine, with nothing else changed, gives zero stalls.

That is worth stating plainly, because it is easy to measure the wrong thing here:

- A GPU measurement on this machine is only valid when nothing else is using the GPU.
  `scripts/macos/run-game.sh` takes a machine-wide lock for *games*, but a probe, a
  capture tool, or a second agent's experiment is not covered by it.
- `commit_to_kernel_ms` in `[METAL-V2][SLOW-WAIT]` and `latency_ms` in
  `[METAL-V2][SLOW-EOP]` are both inflated by a neighbouring process, so a run that
  overlapped one cannot be used to conclude anything about this host.

## What the host does about the latency it can control

Independently of contention, an end-of-pipe write the guest waits on costs a full
submission round trip even when there is nothing for the GPU to do. The host tracks
the newest submission that carries GPU work, and when the GPU has already finished
it — and no earlier completion is outstanding, so ordering is kept — the end-of-pipe
work runs on the worker instead of being queued behind a commit.
`SHARPEMU_METAL_V2_INLINE_EOP=0` turns that off, and `[METAL-V2][WAITS]` counts it as
`inline_eop`.
