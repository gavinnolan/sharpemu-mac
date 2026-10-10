# Combined boot original-storage candidate

Unqualified source only. No build, test discovery, managed suite, activated
worker, native worker, publication or game replay has run on this source.

This candidate combines:

- Qualified DS/Void source `5016bc82`, retaining the exact tested native R32,
  physical-placement, bounded rejected-input capture and boot-base behavior.
- Reviewed Superliminal Fill donor `aadd7248` and pending-only canonical donor
  `a1db7b57`, carried by source union `a13a75e7`.
- Same-lock DS first installation diagnostic `d792cdf4`, preserving all nine
  guards and their order without changing authority or bounds.
- Cold first-capture enrollment donor `a68edba2`, retaining the first provider
  mapping/pin and performing one copy only after strict watch enrollment.

The only merge conflict is the shared native test fixture's optional parameter
list. `pendingCanonicalWindow` remains in its existing position and
`coldEnrollmentWindow` is appended; both default null. Both instrumentation
paths and every existing full-byte/status-4/once/ordered-completion assertion
remain. Production and all other test paths inherit their respective donor
blobs exactly.

Expected qualification inventory, to be checked against fresh compiled
discovery and exact execution on both actual ARM64/x64 .NET 10.0.3 runtimes:

| Group | Cases per CLR | Scope |
| --- | ---: | --- |
| Ordinary Host | 238 | 208 inherited plus 30 real-ledger cold controls |
| Ordinary Libs | 793 | 779 inherited plus 14 cold registry/lifetime controls |
| Ordinary GPU | 105 | Exact DS/Void inheritance |
| Ordinary total | 1,136 | No activated tests included and no skipped rows accepted |
| Separate native/provider workers | 106 | 103 inherited plus three real cold foreign-admission scenarios |
| Separate cold frontend metadata workers | 13 | Fresh flag-enabled processes; synthetic adapter does not prove native authority |

Activated cold tests must be excluded from ordinary suite filters. Fresh
activation workers set `SHARPEMU_METAL_STORAGE_ORIGINAL_ENROLLMENT_WAIT=1`
before assembly load; the three native scenarios also request the actual device
and RGBA canonical publication. The production flag remains default off. No
flag is activated in a game until this exact source is qualified and published.

Actual DS/Void results retain their executable source `5016bc82` and immutable
archives. They are not attributed to this successor. Dreaming Sarah remains the
only accepted directional gameplay result; this source adds no entry or
performance claim.
