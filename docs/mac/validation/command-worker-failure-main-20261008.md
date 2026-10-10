# Prompt Metal command-worker failure handling — 2026-10-08

The locally integrated source `cd02e3cd8f044e85400bd64e1065d620035c366b` is based on local main
`f9ae7119`. A terminal command-worker exception now fails its queue and notifies
Metal immediately after releasing queue ownership. The presenter latches the
original cause and wakes its waiters without reporting FailFast before its
window loop can return. Existing fatal callers retain their fatal reporting.
Control cancellation attempts every callback outside the queue gate; cleanup
failures cannot replace the original worker exception. `Stop` preserves the
original exception and stack.

## Qualification

The Release test build succeeded, with one owned residual compiler process
stopped using TERM after the build; no owned process remains. The exact union
contains 129 managed checks per actual ARM64 and Rosetta/x64 runtime, including
six new failure controls, deferred-label/completion checks, packet memory/WAIT
checks, and queue/worker controls. Both runs passed all 129, with zero failures
or skips and unchanged before/after DLL hashes.

A separate fresh-process native-window probe passed on actual ARM64 and
Rosetta/x64 .NET 10.0.3. It registers the real Mac memory and platform providers
and the production Metal backend, uploads and presents a 128x128 image in a real
AppKit window, and verifies an actual Metal command buffer reaches status 4.
An owned pending-command exception then runs through the real CommandStreamWorker
and Metal failure observer. The production window loop returns before host stop;
the observer runs once, the exact original exception survives, and the probe
observes zero fatal-report callbacks. Response was 23.774 ms on ARM64 and
72.096 ms on x64 in these two samples. This is a lifecycle result, not a steady
performance benchmark or proof of full native GPU resource teardown.

The final native probe is `command-worker-main-cd02-native-window-retry5`.
Earlier fixture failures remain preserved: missing reflection access/assembly
resolution, missing GPU or memory registration, and late test-assembly module
initialization. The accepted probe uses its own empty-stream host, avoiding
that test module's global backend registration. The copied test payload contains
unused other-backend assemblies; this probe does not certify app packaging.
The separately published signed app is the native-only runtime artifact.

Managed receipts, full payload hashes, final native probe source/logs/receipts,
and exact changed source files are retained under
`artifacts/validation/all-games-20261008/command-worker-main-cd02e3cd`.
The source/qualification transfer manifest SHA256 is
`fbbf73e678e7c3d3a76128716c889d52f73d5ea2c99840c7c510fc37ba639de5`.

## Game evidence

The signed candidate's 35-second Octopath replay survived until owned host TERM,
peak 2423 MiB. It did not reach a command-worker exception before that stop, so
it does not verify prompt game failure handling or gameplay progression. The
first 90-second Sarah replay remained at the title screen because its early
input preceded gameplay, and is retained as an unqualified control. The separate
150-second replay with later input verifies outdoor right/left movement in the
110/125/135-second screenshots. Peak RSS was 702 MiB; it survived until owned
TERM and stopped within the first harness interval. Late two-second reported
frame windows ranged from 59.83 to 66.50 Hz. This is not sustained scanout FPS
or an A/B optimization result. All 239 signed app file hashes matched before
and after both replays. Runtime transfer manifest SHA256:
`82735d7964b91dda5821a4e6a8a797c4db405e7fe4d1a876df0047a24f2d39a5`.

Local main was fast-forwarded to the exact qualified source commit. This window
probe does not establish automatic whole-process or guest-thread termination.
No new title, sustained 60+ FPS or loading-time gate follows
from these controls. No remote publication or upstream merge is authorized.
