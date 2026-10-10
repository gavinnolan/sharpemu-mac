# Package integration review — 2026-10-07

Reviewed the integrated CPU extraction, backend namespaces, and shared service
contracts at `7aa2614`, then fixed the Metal materialization and failure handling
findings below. All commands ran in the isolated package-contract worktree. No
game or GPU device suite ran during this review.

## Findings and changes

- `834b79c` preserves declared texture type for singleton arrays and depth-one
  volumes. Layered upload selection and 3D image stride now follow texture type,
  and a failed upload releases its newly allocated retained texture. Nine managed
  cases cover shape/stride and rejecting unsupported MSAA before allocation.
- Guest execution failures previously logged an error and completed the failed
  sequence. Ordered actions also swallowed exceptions, and the frame boundary
  logged exceptions and continued. These paths now use shared `GpuFatal`, retain
  the original exception plus queue/submission/sequence/action context, stop
  acknowledging failed work, unblock work waits with failure, reject further
  enqueue/drain, and discard the uncommitted borrowed batch without committing
  partial commands. The creating autorelease pool owns that command buffer.
  Minimized windows and missing drawables still return normally.
- Four tests execute the real ordered managed-work drain without a device and
  verify successful earlier work, failed/following work completion, fatal context,
  returning and throwing fatal hooks, and the frame exception boundary.
- The guest FPS test observes the published title summary: 120 host redraws and
  guest submissions produce zero guest FPS; one guest presentation produces one
  guest frame in a one-second reporting window despite another 120 redraws.
  Existing isolated scheduling collection and saved/restored static state prevent
  the test from corrupting unrelated timing tests.
- Extracted packages retain only their existing `SharpEmu.Libs.Tests` friend. The
  proposed separate Metal test project uses reflection for production upload
  fixtures, so no additional friend exception is allowed by architecture guards.

## Verification

| Check | Result | Scope |
| --- | --- | --- |
| CLI `win-x64`, Release, at integrated `7aa2614` | Passed; 0 errors | Cross-build on macOS; 9 existing warnings |
| CLI `linux-x64`, Release, at integrated `7aa2614` | Passed; 0 errors | Cross-build on macOS; 3 existing warnings |
| Integrated contract/host/timing selection after texture-shape fix | 222 passed on ARM64 and 222 on x64/Rosetta | 0 framework skips; two Windows-only bodies return early on macOS |
| Architecture/scheduling/Metal shape/FPS/failure selection after fatal handling | 155 passed on ARM64 and 155 on x64/Rosetta | Managed execution; no device suites; 0 skips |

Builds use `-m:2 -nodeReuse:false -p:UseSharedCompilation=false`. x64 test runs use
`arch -x86_64 /Users/xnetcat/.dotnet-x64/dotnet test --no-build --no-restore` against
the same AnyCPU test build.

Evidence logs: `/tmp/sharpemu-integrated-contract-win.log`,
`/tmp/sharpemu-integrated-contract-linux.log`,
`/tmp/sharpemu-integrated-contract-host-arm64.log`,
`/tmp/sharpemu-integrated-contract-host-x64.log`,
`/tmp/sharpemu-integrated-fatal-contract-arm64.log`, and
`/tmp/sharpemu-integrated-fatal-contract-x64.log`. Corresponding test TRX files
are in the worktree's `tests/SharpEmu.Libs.Tests/TestResults` with
`integrated-contract-host-*` and `integrated-fatal-contract-*` names.

## Evidence limits and remaining validation

The two Windows-only host cases counted as passed on macOS are
`OccupiedAddress_IsRejectedByWindowsPlaceholders` and
`ProtectFailure_PreservesThePlaceholderOnWindows`. Their native Windows bodies
were not exercised. Other supported host-view cases execute the macOS VM backend;
cross-builds do not establish Windows/Linux runtime correctness.

The new Metal shader fixtures also require care when reading aggregate results:
22 ALU cases return before compilation/execution when
`SHARPEMU_TEST_REQUIRE_DEVICE` is not `1`; 16 image read/store cases translate
guest shaders then return before GPU execution with that gate unset. The runtime
compiler test has a separate Metal-availability return. Framework pass/skip counts
alone therefore do not establish Metal execution. Production upload/readback
fixtures and game runs remain separately serialized by the parent task.

The managed texture-shape tests establish routing and stride decisions. They do
not establish actual Metal upload/readback correctness. Multi-depth volume mip
snapshots and multisampled snapshots without per-sample data remain explicitly
unsupported. The failure-handling change makes such runtime failures fatal and
diagnosable; it does not implement missing texture payload semantics.
