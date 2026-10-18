# Poppy volume descriptor replay

The frozen `34fc6d752350b8bcf0210f50b97e47f26a48e84e` Poppy diagnostic
reported this storage descriptor for shader `DB87BCC8ED30AD37`, at image store
pc `0xB4`:

```text
7AC109C0 C0B00000 0003C003 A0500004 0000000F 00700000 00000000 00000000
```

The unified image format is `(word1 >> 20) & 0x1FF`. For `C0B00000` this is
`0x0B` (11), R16 Uint. It is **not** `0xB0` (176), BC4 SNORM. With the same
width-low bits, `CB000000` would encode the latter. Earlier BC4 diagnosis of
this record came from reading the wrong hexadecimal position.

AMD's [GFX10 image-format enum](https://github.com/GPUOpen-Drivers/pal/blob/dev/src/core/hw/gfxip/gfx9/chip/gfx9_plus_merged_enum.h)
assigns `IMG_FMT_16_UINT` to 11 and `IMG_FMT_BC4_SNORM__GFX10` to 176.
Its [image descriptor layout](https://github.com/GPUOpen-Drivers/pal/blob/dev/src/core/hw/gfxip/gfx9/chip/gfx10_sq_ko_reg.h)
places GFX10 image FORMAT at word1 bit20. The emulator's descriptor decoder
uses that same extraction. No PS5 format override or integer reinterpretation
of BC4 is needed to explain this record.

`PoppyVolumeDescriptorReplayTests` runs the captured descriptor through the
production `ShaderProgramCache`, resource materializer, permutation selection
and stage resource assembly. The shader fixture uses the exact captured
`F0200F10 00000004` ImageStore encoding followed by end-program; it does not
replay the other 30 instructions or game execution. A fake compiler and host
prevent native device activity.

The regression first specializes the captured R16 Uint descriptor, then a
genuine BC4 SNORM descriptor, then the original descriptor again. It proves
distinct Uint/Float permutations, an original-Uint cache hit, matching 3D
write-only stage metadata and detached descriptor snapshots after caller
array mutation. Both tests passed on ARM64 and Rosetta x64 (2/2 each, no
skips or platform early returns).

No production cache defect or mutation was found in this replay. The actual
Poppy failure remains the unsupported tile5 16x16x16 R16 Uint volume layout.
The classifier test does not qualify BC4 storage, native 3D materialization,
full shader behavior, gameplay, or performance. This change adds regression
coverage and corrects the diagnostic interpretation; it changes no production
format or cache behavior.
