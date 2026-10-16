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

## Modules and libc
