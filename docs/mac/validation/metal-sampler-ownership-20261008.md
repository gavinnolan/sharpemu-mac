# Metal sampler ownership

The guest sampler cache and presentation linear sampler allocated owned
MTLSamplerDescriptor objects without releasing them. Guest reserved mip-filter
rejection happened after allocation, nil sampler states could enter the cache,
and concurrent cache misses could replace an independently retained state.

Both creation paths now use one private ownership helper. It releases the owned
descriptor after configuration/state creation, rejects nil descriptor/state,
and releases a newly created state if descriptor cleanup fails before transfer.
Every owned cleanup is attempted once before error diagnostics. The original
creation/configuration/publication failure keeps its identity and stack; any
secondary descriptor/state cleanup error is attached to that exception's Data
when diagnostic recording is possible. Recording is best effort; a throwing
Data property cannot replace the original cause.
A cleanup error is thrown directly when it is the sole cause.
Guest mip-filter validation occurs before native allocation. Cache publication
rechecks the winner under the existing lock and releases only the incoming
owned retain on duplicate or failed publication. Accepted cache retains and
the presentation state's returned retain remain with their existing owners.
Sampler address/filter/LOD/argument-buffer settings are unchanged.

Managed controls exercise that actual helper and the actual cache publication
with nil/configuration/state/descriptor-release failures, reserved mip rejection
before Device0 access, two concurrent publications, and three dual-failure
controls preserving the primary cause and exact cleanup attempts. Delegates isolate owned
object operations; they do not emulate Objective-C or prove native deallocation.
The exact clean source `8b3c0f69dad3c3e3652b2c15da3460a1b015d5ba` passed
46/46 genuine managed controls on ARM64 and 46/46 on Rosetta, including
13 ownership controls. Four disabled-device R8 source/codegen cases per CLR
are separate from native execution. Both builds and runs use the frozen Debug
neutral assembly, with no native performance claim.

The authorized native rerun passed four R8 numerical cases on ARM64 and the
same four on Rosetta, required-device enabled and no rebuild. Actual production
guest sampler creation/filtering preserved all256 native voxel/sample/filter
results, signed bounds, and the translated nonzero alpha oracle. This does not
prove native descriptor deallocation, retain counts, concurrent cache teardown,
front presentation rendering, gameplay or FPS. Presentation linear configuration
is unchanged and shares the helper, but it was not rendered in this fixture.
Other owned descriptors such as compile options remain separate work.

Evidence: `artifacts/validation/integrated-20261007/metal-sampler-ownership-8b3c0f69/`.
Source/managed manifest SHA256:
`8dae236b5aa5b495a63001a57f75a466c7d4121a9f3d95331bee1d87b801b102`.
Native manifest SHA256:
`588a33fc3b31a61011d576748f0198f48a2d99abf814a75f1e9a2957b241f100`.
The lock was released and both owned worker process groups were absent.
