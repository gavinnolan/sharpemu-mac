# Deviations from PS5 behaviour

SPEC §0.1.4: where the host cannot match the console, the deviation is named
here with its observable effect, the chosen behaviour, and the test that pins
it. No silent deviations.

Each entry: **Name** — observable effect · chosen behaviour · pinning test.

## Clocks and time

- **TSC rate** — `sceKernelGetTscFrequency`, `sceKernelReadTsc` and
  `machdep.tsc_freq` report the host (Rosetta) TSC rate, measured at start,
  not the console's. Guest code executes `rdtsc` natively, so a console
  constant would disagree with every inline `rdtsc` the title runs; the three
  sources stay mutually consistent instead. Pinned by
  `KernelClocksTests.TscFrequencyMatchesSysctl`.
- **Process-time counter** — `sceKernelGetProcessTimeCounterFrequency` is a
  fixed 1 GHz on every host and `sceKernelGetProcessTimeCounter` is scaled to
  it, so counter / frequency is real seconds since process start (the console
  counts from process creation too). Pinned by
  `KernelClocksTests.ProcessTimeCounterRunsAtTheFixedFrequency`.
- **Clock resolutions** — `clock_getres` reports the console values FreeBSD
  derives (1 ns for the timecounter clocks, 1 ms for VIRTUAL/PROF at hz=1000,
  1 µs for the CPU-time clocks, 1 s for SECOND). The host realtime clock
  actually advances in 100 ns steps. Pinned by
  `KernelClocksTests.ResolutionMatchesFreeBsd`.
- **Monotonic epoch** — CLOCK_MONOTONIC/UPTIME count from process start, not
  console boot; only differences are meaningful to a title either way.

## Kernel and process

- **Process ids** — `getpid` returns a fixed 74 and `getppid` 1, the same
  values `kern.proc.pathname.<pid>` accepts; the host pid never reaches the
  guest. The console's actual pid varies per boot. Pinned by
  `KernelSystemInfoExportsTests.ProcessIdentityIsFixed`.
- **System identity** — `sceKernelGetSystemSwVersion` reports "01.000.000"
  (0x01000000), `hw.physmem`/`hw.realmem` the console's 16 GiB, `hw.ncpu` the
  13 app-visible CPUs, uid/gid 0, development mode off. Pinned by the
  sysctl and identity tests in `KernelSystemInfoExportsTests`.
- **sceKernelGetCurrentCpu** — each host thread is assigned a guest CPU
  0..12 round-robin on first call and keeps it; host threads migrate freely.
  Pinned by `KernelSystemInfoExportsTests.CurrentCpuIsStablePerThread`.
- **sigaction** — dispositions are recorded and returned exactly, but no
  host signal is installed and nothing is delivered through them yet (guest
  faults go through sceKernelInstallExceptionHandler). Pinned by
  `KernelSystemInfoExportsTests.SigactionRoundTripsAndRejectsSigkill`.
- **Memory residency calls** — `sceKernelMlock`/`Munlock`/`Msync` validate
  their arguments and succeed: guest pages are host memory that is coherent
  for every reader and never written back to a file. Pinned by
  `KernelSystemInfoExportsTests.MsyncRejectsAsyncWithInvalidate`.

## Syscalls

- **nosys slots do not raise SIGSYS** — FreeBSD `nosys` sends SIGSYS to the
  thread and then returns ENOSYS; with SIGSYS at its default action the
  console kills the process. Guest signal delivery has no SIGSYS path today
  (`sceKernelInstallExceptionHandler` accepts no SIGSYS and guest `sigaction`
  handlers are not delivered), so a raw syscall to an unimplemented slot
  (`sys_obsolete*`, `sys_number*`, `sys_obs_*`, `sys_lkmnosys`, a number past
  0x2D2, a nested indirect syscall) returns ENOSYS with CF set, as if SIGSYS
  were ignored, and logs the slot once · `KernelSyscallDispatchTests.UnservicedSlotsFailWithEnosys`.
- **Syscalls with no HLE equivalent return ENOSYS** — the console runs them;
  here a real syscall that no registered libkernel export serves (the `thr_*`,
  `_umtx_op`, `dynlib_*` and other libkernel-internal calls until an export
  exists, PS5-private calls, FreeBSD `sys_compat*` shims, and `pipe`/`fork`/
  context calls whose libc wrapper has a different ABI) fails with ENOSYS and
  no SIGSYS, logged once per number with its name · same test.
- **Raw syscall detection is idiom-based** — the loader patches a raw
  `syscall` only where the FreeBSD stub idiom loads its number (see
  `GuestSyscallScanner`). On macOS an unpatched one reaches XNU as a classless
  syscall, which raises SIGSYS and is serviced through the same trap; on Linux
  and Windows hosts it reaches the host kernel with a Prospero number ·
  `GuestSyscallPatcherTests`.
- **AVX upper halves across a trapped syscall** — the FreeBSD kernel
  preserves all vector state across `syscall`. The trap thunk saves and
  restores the SSE state (`fxsave64`: x87, MXCSR, XMM0-15) but not the upper
  YMM halves, which host code run for the syscall may change ·
  `GuestSyscallTrapTests.ThunkPreservesTheSseStateAroundTheCall`.

## Filesystem

- **Permission bits** — stat reports synthetic modes: directories 0755,
  files 0644, symlinks 0755, device nodes 0666 and pipes 0600, all owned by
  uid/gid 0. Host permissions are not consulted. `access(X_OK)` follows these
  bits, so it is EACCES for a regular file.
- **Inode and device numbers** — `st_ino`/`d_fileno` are a 32-bit hash of the
  host path, and `st_dev` is a hash of the mount root. They are stable and
  never 0, but two paths can collide.
- **Descriptor limit** — the guest fd table holds 4096 descriptors
  (lowest free first, from 3). EMFILE comes from that table, not from the host
  limit.
- **"$/" app-root alias** — a path beginning "$/" resolves under /app0,
  because AMPR/APR asset tables use that spelling. "$\\" is not an alias:
  '\\' is an ordinary filename character on the console.
- **Mount names are case-sensitive** — "/app0" matches, "/APP0" does not.
  Below the mount, case follows the host filesystem, which is
  case-insensitive on default macOS and Windows volumes.
- **Symlinks** — a host symlink inside a mount resolves only when its target
  stays inside that mount. One that leaves the mount makes the path resolve to
  nothing (ENOENT), as a sandbox guard.
- **Negative stat cache** — misses are cached only for the read-only game
  image (/app0, when it is a plain read-only directory). Writable mounts are
  always re-probed.
- **getcwd** — reports "/app0", the directory relative paths resolve
  against. `getcwd(NULL, 0)` is EINVAL because no guest allocator backs it.
- **pipe** — pipes have an unbounded buffer, so writes never block. A write
  with no reader is EPIPE without SIGPIPE. Both ends are one-way, whereas
  FreeBSD pipes are bidirectional.
- **statfs** — the file-system type names ("pfs" for the game image,
  "exfatfs" elsewhere, "devfs") and the 64 KiB block size are synthetic. Block
  counts come from the host volume; the game image reports no free space.

## Modules and libc

- **libc routing** — by default libc imports bind to HLE, except for a short
  allowlist (mem*, the allocator family, sprintf and FILE functions when the
  module's copies are usable) that goes to the title's libc.prx.
  `SHARPEMU_LIBC_LLE_FIRST=1` makes the loaded libc.prx win for every export
  it provides; HLE then serves only symbols the module lacks and these named
  deviations (`LibcRoutingPolicy.HleDeviations`):
  - `memset` — the shipped body has a PS5-specific `int 45h` path.
  - `memcpy` — the same mem* family; the bounds-aware HLE copy is kept.
  - `fputs` — HLE only in the default routing, where HLE stdio owns FILE
    objects. Under LLE-first the module's FILE layout is used throughout.
- **dlsym allocator aliases** — `sceKernelDlsym` maps Unity's
  `scriptingGetMem`/`scriptingFreeMem`/`scriptingRealloc`/`scriptingCalloc` to
  the loaded libc's malloc/free/realloc/calloc when the normal lookup fails.
  Unity titles depend on this (commit ce8f75411).
- **dlsym scope** — a mapped module's handle searches that module and the
  modules it needs, breadth-first. Handle 0 and HLE-backed handles search
  every loaded module.
- **Module start** — preloaded modules start in dependency order, but only
  DT_INIT runs (no DT_PREINIT_ARRAY/DT_INIT_ARRAY/module_start arguments).
  Fini and module_stop are not run at exit. The PS5 rtld order of DT_INIT
  against module_start is not established.
- **sceSysmodule ids** — only id 0 is rejected (INVALID_VALUE 0x805A1000).
  No id-to-PRX table is available outside GPL sources, so any other id
  "loads" as its HLE library, or as the title's mapped module when one of the
  few known names is preloaded. Load and unload are reference-counted.
  Unloading or querying a module that is not loaded gives UNLOADED
  (0x805A1001).
- **HLE libc data** — when no loaded libc module defines them, `environ`
  points to an empty `{ NULL }` vector, and `__stdinp`/`__stdoutp`/`__stderrp`
  point to opaque zeroed FILE objects that are also exported as
  `_Stdin`/`_Stdout`/`_Stderr`. HLE stdio writes to `_Stderr` reach the
  host's stderr, and to any other FILE it does not own reach stdout.
- **Module info** — `SceKernelModuleInfo(Ex)` lists up to four PT_LOAD
  segments with their protections, plus the eh_frame fields and a ref_count
  of 1. The SELF fingerprint, the TLS fields and eh_frame_hdr_size are zero.
- **sceKernelDlclose** — checks the handle (ESRCH when unknown) but never
  unmaps a module.

## Shader ALU

Checked by the RDNA2 ALU conformance harness
(`tests/SharpEmu.ShaderCompiler.Metal.Tests/Conformance`), whose reference
model asserts each deviation below rather than the ISA behaviour.

- **f32 denormals** — Metal flushes f32 denormal inputs and results to a zero
  of their sign whatever MODE.FP_DENORM asks, so a program that keeps f32
  denormals (FP_DENORM[5:4] != 0) sees them flushed; titles run with 0xC0,
  which flushes them anyway. OMOD still follows the requested output-denormal
  bit (ignored when it is set, ISA 6.2.2). f16 and f64 denormals follow the
  mode. Pinned by `Rdna2AluConformanceTests` (mode 0xF0 forms, model
  `Deviations.F32DenormalsFlushed`).
- **NaN payloads** — the ISA does not define the payload or sign of a NaN an
  arithmetic op produces; the harness requires a NaN there, not particular
  bits. Ops whose pseudo-code names the NaN (moves, abs/neg, min/max returning
  an operand, quieting) are compared bit-exactly.
