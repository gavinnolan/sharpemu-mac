# Queued texture snapshot lifetime — 2026-10-07

Superliminal at source `9498713` stopped at offscreen draw sequence 34515,
submission 1743, with a missing skipped texture at `0x701529A000`. The producer
advertised no bytes after observing a native cache hit. Before consumption, the
render loop could evict dirty entries or flush the cache above its entry limit.
Recovery correctly rejected reading tiled/mip guest bytes as linear data.

Sampled CPU-backed texture preparation now keeps its faithful linear or deferred
tiled/mip payload even when the native cache advertises a hit. Prepared bindings
and their queued draw/dispatch records hold those arrays through consumption.
Live GPU image aliases and storage-image ordering remain their separate producer
path. No native texture lease or additional shared package contract is introduced.

Per-work reuse shares both decoded and deferred tiled payloads for matching
descriptors, mip selection and array shape, with each sampler kept separate.
Observed writes change the captured generation. Repeated generation changes
during all three capture attempts cause explicit rejection; an unstable snapshot
cannot pass through native alias/cache recovery.

Queued draws and dispatches now count retained backing arrays against the
existing 256 MiB pending-work budget, including tiled/linear texture bytes, mip
and descriptor data, buffers, vertex/index data, stage metadata and detile tables.
Shared array references count once within a work item; pooled allocations count
their full array size. GDS guest-copy bytes are counted too. This budget concerns
queued payloads, excludes object/native-resource overhead, and conservatively
counts arrays shared across separate work items again. Existing admission permits
one work item larger than the budget when the queue has no outstanding payload,
so this is not a hard 256 MiB bound on process memory. Producers also allocate the
current prepared item before waiting for queue admission.

The copy/detile work previously avoided by cross-work native cache hits returns
in this correctness fix. Per-work sharing remains. Runtime copy cost, memory,
FPS, and title progression must be measured after integration; no performance or
game-success claim follows from managed tests.

Quake's 8192×8192 format 4/7 resource requests 268435456 logical bytes, exceeding
the existing 134217728-byte per-texture snapshot cap. The diagnostic now prints
the required bytes, cap, layout and descriptor context. The cap is unchanged and
that resource remains unsupported.

## Verification

The isolated worktree is based on source `9498713` plus this fix; root's later
atomic-return change was not merged or tested here. Forty-four focused managed
cases passed on ARM64 and x64/Rosetta with zero skips. They cover:

- Advertised cache hit, including an older observed write generation, followed by
  faithful tiled or array/mip capture, queueing, another CPU write, synthetic dirty
  eviction/full cache-index flush, and actual ordered consumer drain using the
  production CPU upload-preparation helper. Captured old bytes survive; consumption
  never reads the newer guest memory. No GPU texture is allocated in the fixture.
- Per-work payload sharing with distinct samplers and separation across changed
  descriptors; recorded write generation retained through consumption.
- Three successive capture attempts with an asserted generation change on every
  attempt; explicit unstable-snapshot rejection before native cache recovery.
- Draw/compute payload accounting over actual enqueued records, including shared
  arrays and backing allocations larger than logical buffer lengths.
- Existing native shape policy, failure propagation, and extracted package guards.

Commands use `dotnet test ... -c Release -m:2 -nodeReuse:false
-p:UseSharedCompilation=false`; Rosetta uses `arch -x86_64
/Users/xnetcat/.dotnet-x64/dotnet test --no-build --no-restore`. The filter names
are `MetalQueuedTextureSnapshotTests`, `MetalGuestFailureTests`,
`MetalTextureShapeTests`, and `ExtractedPackageContractTests`. Logs are
`/tmp/sharpemu-queued-snapshot-{arm64,x64}.log`; TRX files are
`tests/SharpEmu.Libs.Tests/TestResults/queued-snapshot-{arm64,x64}.trx`.

## Limits

The synthetic cache index models cache advertisement/eviction, not native Metal
allocation, release, encoding or readback. Production CPU detile and real ordered
queue consumption run; actual Metal upload and game reruns remain separate.

Write tracking counts one generation per dirty epoch. The changed-generation
test models a concurrent cache owner consuming each epoch before the next write,
and verifies that each notification advances the generation. Repeated writes
within an already dirty epoch do not individually advance it. This work verifies
the observed-generation contract; it does not prove that the tracker detects
every concurrent CPU write or establishes an atomic snapshot across all writers.
