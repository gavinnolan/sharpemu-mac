# Finite typed storage CPU synchronization prerequisite

Source-only checkpoint based on `115316f0`. No build, managed/native execution,
game repair or performance qualification has been performed on this candidate.

The actual failure path consumes a dirty flag, reads `_guestImageExtents`
logical bytes from the tiled guest address, discards all-zero data and invokes
the display-oriented 2D/RGBA upload. A type3 array exception currently prevents
that reinterpretation. Allowing the shape alone is insufficient: singleton
arrays own 64KiB physical blocks, R8Uint 3840x2160 owns 8,847,360 physical bytes
but only 8,294,400 native texels, and the exact BGRA1024 class owns 4MiB. Dirty
means some participating CPU write; it does not imply a complete CPU overwrite
of a previously GPU-produced image.

`MetalStorageCpuLayout` clones exact raw8 and a payload-free descriptor, retains
the qualified guest format, number type, swizzle, pitch, tile and shape, and
records separate physical and native-linear sizes. Qualification reuses the
existing finite singleton, BGRA1024 and R8FourK predicates. The data is always
present on qualified native creation, independent of `DiagnosticOrigin` and
diagnostic opt-in. Selected mip classes, general arrays, volumes and other
formats remain excluded. Existing `Require2DGuestImage` calls are unchanged.

The finite producer optionally uses the real `ICpuMappedCopySource` for its
ORIGINAL physical copy, detiles those owned bytes, retains the original opaque
mapping observation and propagates it through descriptor/sampler copies. It
does not reread at native creation or reconstruct an observation from Query.
Unavailable uses the unchanged legacy producer; that native image has layout
metadata but no dirty-sync mapping capability. Rejected discards/retries at
most three original captures, never falls back to an ordinary virtual read.
Existing upload-known selection and the dormant graph path are preserved.

The new dirty path is seed-only. It rejects `GpuWritten`, queued work/open batch,
selected/native-source aliases, unknown or overlapping native image extents,
closed/mismatched memory/session, image replacement, stale version and unknown
native cleanup. Its original mapping must remain current; the original global
mapping epoch is not weakened or rebased. Original tracker owner identities and
footprints must survive. A fresh read snapshot/content observation and the new
mapping copy are checked before and after detiling/upload. Provider copying and
currentness checks occur outside the presenter gate. Actual zero bytes are a
complete clear; there is no nonzero-content heuristic in this typed path.

Fresh native allocation preserves format, type3, depth1/layer1 and shader-only
usage. It uploads full typed rows through the existing layered upload primitive,
then validates exact owner/session/version/copy again before swapping handles.
Old command buffers retain their own objects; the prior texture is not mutated.
Construction failure never publishes the replacement, releases its one retain
once, and preserves a primary error over a secondary release error. Unknown
release is sticky and never retried. After accepted publication, the old snapshot
and image retains are consumed before native release; a failure quarantines
future typed-sync reuse rather than claiming complete cleanup.

The optional mapping-copy contract protects alias-copy lifetime and vetoes
mapping retirement; it does NOT serialize content. Tracker generations are
conservative vetoes, NOT proof of completed managed/raw/native writers. No
atomic content, permission, signal recovery, native guest store handoff, GPU
source certification or PS5 topology claim is added. This checkpoint must be
reviewed and genuinely qualified before integration.

## Required next GPU writeback protocol

To handle the actual GPU-produced dirty array, completed native typed pixels
must be returned to the original canonical backing through an exact mapping-
bound writer transaction. That needs original backing/physical offsets and
rights, immutable session/image/content version, native command-buffer completion,
pre-effect mapping retirement coordination, participating ordinary/native writer
ordering and conservative failure outcomes. Alias-copy lifetime alone is not
virtual mapping or write entitlement. Singleton writeback updates only its
qualified origin texel; full R8/BGRA updates only qualified voxel locations.
Do not overwrite padding from an old CPU snapshot or fabricate zero padding.
R16 selected physical views require their own original selected-domain machinery.

No CPU dirty flag, sampler/layout metadata or successful native readback grants
those write rights. The finite writeback/completion owner is separate work.

## Prepared controls (unbuilt/unrun)

`MetalStorageCpuSyncTests` contains 19 managed case rows: three actual producer /
independent full-byte layouts and zeros, six invalid layouts, two optional-copy
decline routes, original retirement, six real capture refusals and zero-copy
capture without device/publication. Its synthetic mapping adapter proves branch
and byte behavior only; it is explicitly not a native receipt or content lease.
Every row prints actual architecture/framework and checks an optional expected
architecture. Managed-only tracking prevents native protection on synthetic VA.

Four declared opt-in native rows invoke the production fresh typed allocation /
upload primitive for R8FourK, BGRA1024, singleton R32Uint and RGBA16Float. Each
requires device/actual architecture/framework, first verifies every byte of a
distinct varied nonzero replacement, then every byte of a second zero
replacement. Original and varied textures must remain unchanged after zero
creation; this avoids accepting an omitted upload merely because fresh memory
was zero. Native type/depth/layers/format/usage are checked for both replacements.
Cleanup attempts every independently owned release and global restoration,
consumes retains before release, and preserves primary and secondary failures.
They qualify native allocation/upload only, not the full mapping /
content admission path or translated writeback. Without device opt-in the rows
are declared skipped. A separate root-granted bounded fresh worker and shared
machine lock are required for execution.
