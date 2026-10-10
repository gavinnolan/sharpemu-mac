# Distinct Metal depth-reference argument fields

Superliminal's signed87616e29 run reports duplicate `sampled_float_2d` struct
members before its bias name collision. Shared binding allocation already
separates depth-reference images into their own kinds43/44/45. Describe returns
their Float and2D/array shape, so the former Metal name builder erased that
distinction and gave both arrays the same name.

Metal now names only those three fields with `sampled_compare_`; ordinary image
names stay unchanged. Binding kind, numeric class, texture type, array count,
resource ID and byte offset are unchanged. Declarations and body lookup names
derive from the same builder. The shared helper reports the existing compare
kind range without changing allocation or normalization. The presenter binds
by kind and byte offset, not name. Compiled shader/source and its argument layout
are produced and retained together in the in-memory shader cache; prewarm
records preserve guest inputs and recompile, rather than serialize old MSL field
names. No persisted argument-layout migration is introduced.

Managed sources check uniqueness for every45 image kinds and unchanged numeric
layout, then exercise actual extraction→runtime descriptor materialization→
specialization→binding allocation→MSL for ordinary Float2D plus Dref2D. A separate
required-device source compiles that generated fragment shader. Exact frozen
f73f7b6a passed this actual Metal parser case on ARM64 and Rosetta, along with
four helper-collision cases, with no rebuild and required-device1. Full source,
managed/native manifests and execution limits are in
[the helper collision proof](msl-sample-helper-collision-20261008.md). It does
not establish scene rendering, depth compare numerical correctness or repair
the later strict live storage view mismatch.
