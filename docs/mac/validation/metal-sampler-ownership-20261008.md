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
secondary descriptor/state cleanup error is retained in that exception's Data.
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
Existing R8 native sampler/filter fixtures will be rerun only in a separately
authorized device window. This change does not qualify in-flight sampler cache
teardown or other owned Objective-C descriptors such as compile options.
