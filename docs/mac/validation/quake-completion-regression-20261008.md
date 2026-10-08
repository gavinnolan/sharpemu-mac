# Quake early-abort regression isolation

Three existing signed apps were replayed sequentially with the same 100-second
bound, Enter at 20/40/60 seconds and performance logging. No builds or native
tests overlapped them. These controls isolate progression; elapsed times are
not loading benchmarks or gameplay FPS.

| Binary source | Actual result | Limit reached |
| --- | --- | --- |
| `2adf328d` | Host exit 134 after 70 s; peak 2450 MiB | Intro/warning visible, then 8192² R32Float snapshot requires 256 MiB against the 128 MiB cap |
| `115316f0` | Host exit 134 after 33 s; peak 1761 MiB | Warning visible, then the same 256 MiB depth-image limit |
| `724d906d` | Host alive at 100 s; peak 1198 MiB; owned TERM exits without KILL | Guest calls abort around 8 s after only three presentations; black final image |

The earlier current-main `8783b897` and diagnostic `7e46c803` replays also
abort inside guest code after only three presentations. The failure therefore
appears between the mapping-copy source and the combined completion source;
the later depth-comparison change is not required for it. This is a narrowed
regression interval, not proof of the particular faulty label/flip operation.
The completion increment includes deferred end-of-pipe labels, ordered label
and flip acknowledgement, checked writeback and native completion checks.
Correct ordering must be repaired without making labels visible before their
producer work has completed.

The 115 and 724 apps matched their original complete 239-file bundle manifests
before replay. The 2ad app matched its original build information and all
originally recorded SharpEmu payload hashes; an original complete native
closure manifest is unavailable. All three pass deep/strict ad-hoc signature
verification, and their current complete file hashes stay unchanged through
replay. The non-SharpEmu payloads of the current 2ad and 724 apps are byte-equal.

Read-only disassembly of the supplied Quake SELF identifies the reported abort
return address `8002C9F65` immediately after the call to its abort import. It is
the game's generic fatal-error route, which prints a message and opens a dialog;
that callsite alone does not identify the earlier failing operation. No game
files, reference repositories, save/config data or foreign processes changed.

Evidence is retained in
`artifacts/validation/all-games-20261008/quake-completion-regression`: exact
drivers, commands, bundle hashes, original run indexes, logs, selected screenshots
and the small callsite disassembly. No Quake gameplay, scene fidelity, loading
improvement or sustained 60+ FPS follows.
