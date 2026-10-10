# First supplied POSIX trap diagnostic — source draft

Uncommitted SOURCE ONLY draft on frozen `638c3bdf85c770dd73b4dac631f784927621f647`. No compiler, build, discovery, test, signal, native provider, game, publication or remote operation was executed for this draft. The four boot fixes in its parent remain unqualified as a combined candidate. Existing game archives and qualified `e2535177` source are unchanged.

## Actual blocker and missing observation

The retained Poppy `e2535177` terminal manifest is `artifacts/validation/all-games-20261009/native-fixture-original-reader-identity-e2535177-poppy-reconnected-terminal/manifest.json`, SHA256 `da4697548725f536e7a6ad807812345a1c1d1bc3bdad31e97c7cc9fd43891b16`. It contains 80 published schema3 records, no drops and a SIGBUS exit after19s. Invocation3 observes owner12 epoch1 Armed, removes watchers with a known `mprotect` return0 and reports recovery. Invocations4/5 observe Disarmed and decline; a later epoch2 rearm is not the original permission state at their entry. The same RIP `0x80135FB0B` contains `C4 C1 7D E7 04 16` (`vmovntdq [r14+rdx],ymm0`). The two rejected contexts have RDX0 and R14 `0x900D837000` / `0x900D7B0000`, both32-byte aligned. Neither an unsupported instruction nor an alignment or permission repair is proved.

Schema3 records error6 and signal10/code2 but omits the supplied x86 trap number. The error scalar cannot distinguish a supplied page-fault14 from general-protection13 without the trap field. Even a new raw trap observation is a supplied context field, not proof of original hardware cause under Rosetta, current mapping, permissions or ownership.

## Exact non-effect change

Schema4 keeps the same160-byte record and fixed bounded4096 slots. For Kind1 only, `Operation` preserves all64 raw trap bits and `Reserved` is availability0/1. Available raw trap0 is distinct from unavailable. Signed storage is converted losslessly back to unsigned by the reader. All other record kinds retain their existing operation/CAS meanings. Schema1–3 decode with explicit `not-collected-before-schema4`; zero or arbitrary old reused scalars never become a trap observation. Invalid availability or an unavailable nonzero raw scalar is rejected. Unpublished/missing entries never donate a trap to later records.

The existing opt-in first-entry helper reads the same supplied register base once. Darwin reads unsigned16 `__trapno` at exception-state+0; adjacent `__cpu`+2 and `__err`+4 remain separate. Linux reads unsigned64 trapno at mcontext+160, established by both the kernel `sigcontext_64` and glibc `REG_TRAPNO`20. Missing context remains unavailable. Legacy explicit recording calls and the explicit HLE diagnostic warmup omit the new optional arguments and remain unavailable. The separate POSIX `WarmUpSignalPath` supplies a fabricated non-null zeroed context, so its raw trap0 is observed context metadata; the final `preflight-warmup` marker identifies that synthetic invocation and supplies no kernel-cause claim. When tracing is disabled the existing early return precedes all context reads and invocation reservation.

No routing, access classification, callback, tracker CAS, page protection, recovery, resampling, retry or lifetime policy changes. No handler query, cookie lookup, locks, allocation or new record write is added. Enabled first-entry overhead is one2-byte/8-byte load, availability branch and two scalar field writes. Default-off overhead is unchanged.

## Source controls and unresolved execution

New ordinary supplied-metadata controls are11 Libs rows on macOS,12 on Linux and2 neutral rows on Windows. They exercise the actual first-entry helper using controlled in-memory frames, including rawzero/6/13/14/48879/65535, adjacent CPU/error/mask sentinels, null Darwin machine context, null user context, disabled tracing with inaccessible pointer scalars, legacy availability and full-width explicit raw bits. Linux additionally supplies top-bit and all-one64-bit traps. These frames are fabricated metadata, not actual native signals or permission receipts. Diagnostic files are ordinary test-owned fixed storage and restored before disposal; no guest mapping is created or protected.

Six new offline Python decoder control methods cover rawzero/fullbits, missing availability, old schemas, malformed availability, separate Kind8/protection scalars and unpublished first entries. Existing decoder controls remain intact. The source inventory is not discovery or a test pass. All17 inherited native fixture sources and246 worker identities/oracles are unchanged; no new native cases.

The separate `d69faf76` private-allocation foundation and `81e10d48` actual-provider fixture remain outside this candidate. Their cookies are ordinary provenance without permission/access pins. `SnapshotPrivateAllocationOrigins` rejects mapped backing and can take ordinary locks; loaded-image transport does not identify dynamic faulting data. A future origin correlation must retain original mapping/backing identity on an ordinary preparation path before a handler can observe it. It must report missing/unknown provenance rather than adopting an address query. This diagnostic supplies no such link and no fault repair.

## Pinned primary headers

Durable original copies and SHA256 inventory: `artifacts/validation/all-games-20261009/first-posix-trap-number-primary-20261010/manifest.json` outside the frozen source tree. The installed SDK `mach/i386/_structs.h` is byte-identical to the pinned XNU copy, SHA256 `6003cc7f64c45d144be10f5ddb270e0133e0e94f852a98be9a78771dd442ab03`.

- Apple XNU `f6217f891ac0bb64f3d375211650a4c1ff8ca1ea`: [exception-state fields](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/osfmk/mach/i386/_structs.h), [trap14/13 and error bits](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/osfmk/i386/trap.h). Original Apple Public Source License2.0 declaration retained in each header.
- Linux `3857c2fe5449541e24afc5efdb0f81a8a8f9a3a0`: [sigcontext64](https://github.com/torvalds/linux/blob/3857c2fe5449541e24afc5efdb0f81a8a8f9a3a0/arch/x86/include/uapi/asm/sigcontext.h). Original `GPL-2.0 WITH Linux-syscall-note` declaration retained.
- glibc `04e750e75b73957cf1c791535a3f4319534a52fc`: [gregs and REG_TRAPNO](https://github.com/bminor/glibc/blob/04e750e75b73957cf1c791535a3f4319534a52fc/sysdeps/unix/sysv/linux/x86/sys/ucontext.h). Original LGPL declaration retained.

One attempted XNU ucontext path was unavailable and is explicitly listed as unavailable in the primary manifest; it supplies no ABI proof. Existing platform ucontext/register-base routing is unchanged.
