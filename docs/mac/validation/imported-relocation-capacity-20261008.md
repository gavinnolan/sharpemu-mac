# Imported relocation capacity and owned-list scans — 2026-10-08

The private loader method now counts pointer imports before allocating its retained table, and scans its locally owned `List<RelocationDescriptor>` through a `ref readonly` span. Record order, duplicate NIDs/targets, empty non-null NIDs, addends and data-import classification are preserved. The return type and public loader contracts are unchanged.

Production source is `fef8ce726f44fc71430dd7826557fd79f0c80d1e` followed by `21b9d3b712b91212dbd4c2276ea885e0b6e550bd`, based on `a8c65ae1a2cb0f19ad17b35a0a25cc860d776656`. The initial count-first source remains preserved separately. The sole production caller builds the list locally (SelfLoader.cs:730), finishes appending descriptors, and calls the private method at line771. Neither scan mutates the input or calls callbacks; preserve that invariant when changing this method. The imported result can remain retained by `SelfImage`, so oversized capacity is not necessarily temporary.

## Verification

Release Libs tests build succeeded with zero errors and107 existing unrelated warnings. The final selected loader controls passed52/52 on ARM64 (128ms) and52/52 on Rosetta x64 (386ms), zero runner skips. This includes six actual private-method reflection cases checking empty input,0/1/1000/all8192 eligible imports, exact capacity, mixed kinds, duplicates, order and all retained fields. The initial narrow selector passed19/19 each on the naive count-first source and is preserved.

Five native instruction-execution rows were explicitly unselected: red-zone leaf(1), conditional-branch(2), previous-branch(1), and Rosetta vector-store(1). They were not silently counted as passed. No game, GPU, signal handler or native fault worker ran for this optimization. Root integration, cross-platform/runtime startup and game-loading qualification remain separate.

## Source-extracted microbenchmark

The neutral benchmark copies the exact old, naive count-first and final method bodies and descriptor definitions into a standalone executable without Core/HLE initialization. A normalized comparison verifies the final benchmark method matches production (only method name, comments and whitespace differ). Both architectures executed the identical benchmark DLL with explicit .NET10.0.3, stable local List inputs, unchanged production runtime/GC settings, and778191 descriptors.0/1/1000/389095/778191 pointer imports were modeled; all outputs assert count and first/last complete records. Actual production tests check every record.

Three fresh paired processes per architecture alternate ARM→x64, x64→ARM, ARM→x64. Each process uses before/naive/span/span/naive/before order repeated three times (18 samples per method/shape across processes). Separate JIT-disassembly runs are excluded from the following medians; raw samples/ranges and tier-transition/GC outliers are retained.

| Eligible imports | Old bytes | Final bytes | ARM old→final ms | Rosetta old→final ms |
|---:|---:|---:|---:|---:|
|0|24,902,240|0|1.8990→0.4137|4.4597→0.4614|
|1|24,902,240|88|2.6374→0.8434|7.0666→0.9169|
|1,000|24,902,240|32,056|2.0836→0.8492|5.1012→0.9821|
|389,095|24,902,240|12,451,096|3.1379→1.4802|7.0548→3.0987|
|778,191|24,902,240|24,902,168|4.3112→2.4236|10.5702→6.2180|

The naive interface-based count-first implementation reduced capacity but its extra scan was slower in these controls: at1000 imports, medians4.0989ms ARM and8.9611ms Rosetta versus the old2.0836/5.1012ms. It is preserved rather than described as a speed win. The final span version removes boxed interface enumeration (72B per scan in this control), reads only filter fields for rejected descriptors, and avoids copying each40B input descriptor.32B is the *output* record stride.

## JIT and host-load limits

Separate actual JIT dumps contain instrumented Tier0 and optimized Tier1-OSR bodies with synthesized PGO, OSR entry0x63(old),0x36(naive),0x45(span). ARM optimized body sizes were1044/1976/476B; x64 were983/1945/539B. Final loops use40B input stride and direct NID/kind loads; output construction loads only selected fields. These source-extracted dumps establish the generated-loop mechanism, not exact production JIT code or a game instruction count. Code-size differences alone are not a speed claim.

Process observations before all six no-disasm runs show five unrelated idle `semu-launch` daemons (reported0.0% CPU), left untouched. No dotnet/testhost/REmu/Kairo game process matched those narrow scans. This does not control all host services, thermal state, GC, tiering or Rosetta translation. Timings are short method microbenchmarks, not whole-startup or FPS measurements.

The historical Silent Hill log had778191 descriptors and2056 unique NIDs; unique NIDs are not the retained relocation count. This benchmark does not claim the actual game's import density or that its relocation table is all-relative. No gameplay/loading gain or Poppy recovery change is established.

## Evidence

Root private archive: `artifacts/validation/integrated-20261007/cpu-loader-import-capacity-21b9d3b7/`. It retains source/assembly hashes, actual loader TRX/logs, original count-first/source-extracted controls, exact benchmark source and DLL, all repeat JSONs/commands/process observations, JIT dumps, and a recursive SHA256 index. `final-source-manifest.json` labels the production versus extracted scope. No production .NET/runtime defaults, public API, package boundary or native recovery policy changed.
