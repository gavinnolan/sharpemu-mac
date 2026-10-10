# Poppy store continuation experiment

The existing concurrent-store probe demonstrates that a Disarmed tracker state
can be observed by a legitimate already-trapped writer. Returning from a fault
handler also precedes the retried instruction; a lifetime pin released inside
that handler cannot by itself cover the actual subsequent store.

Two new standalone x86_64 native experiments test a possible continuation
mechanism on this Mac's Rosetta. They execute Poppy's exact instruction bytes
`C4 C1 7D E7 04 16` against an owned private mapping, restore write permission
on the exact write trap, and set the saved x86 RFLAGS trap flag before returning.
Only known probe PCs are handled; unexpected signals/PCs terminate the process.
No emulator handler or game data is changed.

- Three fresh single-thread processes receive one actual write fault and two
  single-step traps: immediately after the retried store and after its following
  guest SFENCE. Each clears the trap flag after the fence and compares every
  owned byte, including padding; all pass.
- Six fresh processes test an alternate continuation: at the first trap after
  the store, the native handler executes its own SFENCE, clears the trap flag
  and releases the diagnostic continuation count. Three single-thread cases
  report one fault/one trap; three32-thread cases each report32 faults/32 traps.
  Every case exits0 with zero remaining continuation counts and all owned bytes
  correct. The binary disassembly retains both the exact store and handler fence.

These observations show that instruction-following notification is available
for this exact translated store on this system. They suggest a way to retain
an exact memory/protection lease across signal return and release it after the
retried store plus a same-thread ordering fence. The diagnostic counter is not
an actual provider lifetime lease, memory-writer receipt or production policy.

The experiments use immutable allocations until all worker joins. They do not
prove CLR signal chaining, debugger coexistence, arbitrary guest instruction
decoding, nested faults, rearm, concurrent protection owners, remap/retirement,
or dirty/content stability. Those need actual provider and signal-bridge tests
before a Poppy game replay can qualify any recovery change. No unconditional
Disarmed retry or guest instruction skip is justified.

All nine bounded native processes exit normally with no watchdog signals. They
ran in the root's serialized heavy slot. The original drivers incorrectly used
an advisory file lock at the harness's directory-lock path, leaving an empty
file that prevented the subsequent Sarah run from starting. No Sarah game
process launched. The owned file is removed and future drivers use the harness's
directory/pid protocol; original drivers/results remain retained. This correction
does not claim that the original runs acquired the shared directory lock.
The same nine cases were then repeated in fresh `directory-lock-rerun` folders
using the actual directory/pid lock protocol; all exit0 with matching full bytes
and zero watchdog signals. Each lock is released by its exact owning driver.
Full source, driver, binary,
disassembly, byte checks and process receipts are retained under
`artifacts/validation/all-games-20261008/poppy-rosetta-single-step` and
`poppy-rosetta-store-retirement`. Poppy gameplay remains unqualified.
