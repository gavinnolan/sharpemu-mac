# Storage placement dependency correction for the DS and Void source stack

The frozen `bfc988ed8898d2fde164b91a9d8eb5f4d93661c9` stack first failed its
Libs build with CS0246 because `MetalStoragePhysicalPlacement` was missing.
Host built successfully; GPU build and all tests, native workers, publication
and games were unrun. Its first-failure manifest and logs are retained in
`artifacts/validation/all-games-20261009/void-input-on-watched-ds-boot-bfc988ed-prepared/`.
The earlier source-only mechanical port proof did not establish build readiness.

This successor adds only the exact previously reviewed donor `6c33e428`
`src/graphics/SharpEmu.Gpu.Metal/MetalStoragePhysicalPlacement.cs`, plus this
note. It makes no policy adjustment and changes no inherited source or tests.
The helper supplies the existing `OrdinaryStorageOriginal` and
`NativeR32Snapshot` callers and the retained storage-placement test fixture.
Its HLE mapped-copy metadata, guest-page rights, AGC descriptor decode and
normalized image-layout dependencies already exist in the base. The existing
Metal project references HLE and Libs and includes the new C# file through the
SDK default compile glob; no package or project-reference change is needed.

The helper retains the original mapping observation, cloned descriptor,
complete allocation geometry and complete potential write spans. Its eight
probe bytes are discarded and do not become upload data or GPU-completion,
content, native-permission or write authority. It checks readable and writable
original physical coverage and rejects unsupported metadata, incomplete
coverage, original mapping replacement and uncertain placement. No provider
contract or caller is loosened to make the dependency compile.

All inherited BFC blobs must remain exact. Removing the helper and this note
must reconstruct the entire BFC tree. The source expectation remains 1064
ordinary managed rows per CLR (208 Host, 751 Libs, 105 GPU) and 89 separate
native workers per CLR. These are inherited source expectations, not discovery
or execution results for this successor. This correction is source-only:
no compiler, test, native worker, publish or game was run while freezing it.
