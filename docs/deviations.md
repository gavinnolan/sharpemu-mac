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
