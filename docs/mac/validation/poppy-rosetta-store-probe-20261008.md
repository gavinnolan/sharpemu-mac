# Poppy streaming-store fault: isolated Rosetta concurrency evidence

The current game fault is `VMOVNTDQ ymm0,[r14+rdx]`, bytes
`C4 C1 7D E7 04 16`, at guest RIP `0x80135FB0B`. A standalone native
x86_64 diagnostic executes those exact bytes under Rosetta on this Mac. This is
an instruction/protection experiment, not a game fix or emulator recovery policy.

The retained source, commands, binary disassembly, fixed-width signal records,
per-process results and byte checks are in
`artifacts/validation/all-games-20261008/poppy-rosetta-store-probe/`.
The actual compiled binary SHA256 is
`99469f41f45dab34c19657d676c6b5db09b3fcfe45313c7a4d1b1215cbd2f85d`.
Its process explicitly verifies `sysctl.proc_translated == 1`; native page size is
4096 bytes. The owned 2,211,840-byte mapping uses an address hint rather than
replacing any mapping. Private mappings and file-backed shared mappings with a
second writable alias are both covered. No emulator, GPU, game files, reference
repository or existing native handler is modified.

## Observed results

The completed 62 cases contain 38 successful processes and 24 deliberate
declines exiting138/SIGBUS; no timeout. Each successful process compares every
owned byte and, for shared mappings, every alias byte including untouched padding.

- Aligned writable and single-thread readonly controls succeed for all three
  instruction sequences: original256-bit non-temporal store, ordinary256-bit
  store and two ordinary128-bit stores. Readonly single-thread controls take an
  actual write page fault and perform a successful `mprotect` restoration.
- With32 simultaneous writers, four repeats for each instruction and mapping,
  all24 processes using an actual permission call for each eligible fault finish
  with correct bytes. Original non-temporal stores deliver32 signal entries in
  every repeat; ordinary store controls can also deliver32 entries.
- With the same release barrier and a tracker-style Armed→Invalidating→Disarmed
  state transition, busy-Invalidating entries are accepted and Disarmed entries
  declined. All24 concurrent processes fail, across all three instruction
  sequences and both mapping types. Recorded write faults have trap14/error6;
  the successful restoring syscall reports result0.
- The two misaligned writable non-temporal observations succeed without a signal
  on this Rosetta version. That differs from the Intel32-byte alignment rule and
  is recorded rather than treated as emulated Intel correctness. Poppy's observed
  failing address is aligned, so this does not explain its crash.

Multiple fault handlers can observe a write trap after another handler has
restored permissions. Signal-entry timestamps describe handler observations,
not original kernel trap time. Declining solely because a range is now Disarmed
can therefore terminate legitimate concurrent stores. Splitting the store does
not resolve this reproduced concurrency failure.

The first verifier assumed the Intel alignment exception would occur in Rosetta
and stopped after31 measured cases. Its source and manifest are preserved.
The continuation changes that observational assertion, keeps the original binary
unchanged and executes the remaining31 cases; no instruction/protection source
change was used to obtain the result.

## Production implication and limits

The current tracker cannot safely accept every Disarmed address either: a real
readonly, unmapped, recycled or differently owned page must remain a fault.
Recovery needs an exact live mapping/backing reservation, native-page protection
ownership, a successful restoration bound to the current fault invocation, and
coordination with rearm and retirement. Dirty/content generations must be
published before write access opens. Unknown or partial restoration must retain
its failure and ownership obligations.

This diagnostic has one immutable allocation, no remap, no concurrent rearm and
no independent protection owners. It deliberately performs IO in its standalone
signal handler to preserve a fatal process's observations; that handler must not
be copied into production. It neither exercises the CLR signal bridge nor proves
the Poppy game has no other fault. Existing game/native traces remain authoritative
for the actual emulator failure.

[Apple documents current AVX/AVX2 Rosetta support](https://developer.apple.com/documentation/apple-silicon/about-the-rosetta-translation-environment).
[Intel's instruction reference](https://cdrdv2-public.intel.com/774492/325383-sdm-vol-2abcd.pdf)
requires32-byte alignment for the256-bit non-temporal store and specifies fence
ordering considerations. Neither source justifies converting Poppy's fault to
an unsupported-AVX diagnosis or an unconditional retry.
