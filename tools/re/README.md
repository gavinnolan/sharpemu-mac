# tools/re — static analysis of PS5 ELFs (Demon's Souls)

Reusable reverse-engineering toolchain for fake-signed PS5 SELFs (eboot.bin, `*.prx`) and RDNA shaders.
Outputs go to `artifacts/re/` of the **main checkout** (git-ignored; the scripts resolve it even from a
`.claude/worktrees/*` checkout). Scripts are Python 3 stdlib only; no pip dependencies.

**Address convention.** Runtime address = image base + ELF `p_vaddr`; SharpEmu maps the DS eboot at
`0x800000000`, so `app+X == ELF vaddr X`. All TSVs/text outputs use ELF vaddrs (`app+X`). The Ghidra project
is loaded at base `0x800000000`, so Ghidra addresses are absolute runtime addresses; the Ghidra scripts accept
either form (values below `0x800000000` are treated as `app+X`).

## Everything at once

```
tools/re/ds-setup.sh                 # regenerate all DS outputs (Ghidra analysis takes a long time)
tools/re/ds-setup.sh --no-ghidra     # the Python outputs only (seconds)
```

Outputs in `artifacts/re/`: `ds/eboot.elf`, `ds/imports.tsv`, `ds/callsites.tsv`, `ds/functions.txt`,
`ds/modules/` (bundled `sce_module/*.prx` flattened + their import/export maps), `ghidra/DS.gpr`.
Overrides: `DS_APP0` (game dir), `RE_OUT` (output root).

## Tools

| Tool | Use |
|---|---|
| `fself2elf.py in.self out.elf` | Flatten a fake-signed SELF to a plain ELF (segments to their p_offset). Real/encrypted SELFs are not supported. |
| `nidmap.py x.elf imports.tsv [--exports e.tsv] [--names FILE]` | Parse the Sony dynamic section: every import slot (jump_slot / glob_dat / abs64) to PLT stub, NID, resolved name, library, module. Names come from `scripts/ps5_names.txt` (hashed), HLE exports under `src/`, Kyty `LIB_FUNC` registrations under `~/Documents/ps5emu/refs`, plus `--names` files (bare names or NID/name pairs). The `via` column says where a name came from; `hle-label`/`ref-kyty` names may be working labels. Prints the resolved percentage. |
| `callsites.py x.elf imports.tsv callsites.tsv` | Every direct reference to an import: `call`/`tail` (E8/E9 to the PLT stub), `call_got`/`jmp_got`, `load_got` (GLOB_DAT/data imports), `lea_stub`, `data` (abs64 slots). Caller function start comes from the ELF's `PT_GNU_EH_FRAME` table. |
| `funcstarts.py x.elf functions.txt` | Function starts from the eh_frame table (133k for DS). |
| `ghidra/import.sh elf imports.tsv functions.txt projdir NAME [-noanalysis]` | Headless import at base `0x800000000` (x86:LE:64 gcc), pre-scripts `SeedFunctions.java` (eh_frame starts as entry points) and `ApplyImports.java` (names PLT stubs `<import>` and GOT slots `got_<import>`, marks `__stack_chk_fail`/`abort`/... no-return), then auto-analysis and save. Env: `GHIDRA_HEADLESS_MAXMEM` (8G), `MAX_CPU` (8), `JAVA_HOME` (openjdk@21). |
| `ghidra/run.sh decompile <addr>...` | Decompile the function containing each address to C text (read-only, no re-analysis, ~20 s start-up). |
| `ghidra/run.sh xrefs <addr>...` | All references to an address: from, `app+X`, ref type, containing function. |
| `amdgcn-disasm.sh shader.bin\|record.corpus [--mcpu CPU] [--encoding]` | Disassemble raw RDNA shader code with `llvm-mc --triple=amdgcn`. Accepts SharpEmu `.corpus` records (`corpus2bin.py` extracts the guest bytes). |
| `corpus2bin.py record.corpus out.bin` | Raw guest shader bytes from a `SHARPEMU_SHADER_CORPUS_DIR` record. |
| `pslib.py` | Shared ELF/NID/name-db helpers. |

`ghidra/run.sh` uses project `DS` in `artifacts/re/ghidra` by default (`PROJECT_DIR`, `PROJECT`, `PROGRAM`
override it). Ghidra locks an open project: run one query at a time and not while `import.sh` runs.
Other modules: `python3 fself2elf.py m.prx m.elf && python3 nidmap.py m.elf imports.tsv --exports exports.tsv`;
for Ghidra, `funcstarts.py` then `import.sh` with a different project name.

## Gotchas

- NID = base64 (`/`→`-`, no padding) of the byte-reversed first 8 bytes of SHA1(name + 16-byte suffix); see
  `src/SharpEmu.SourceGenerators/Ps5Nid.cs`. Symbols are `NID#libid#modid`; the ids index `DT_SCE_IMPORT_LIB` /
  `DT_SCE_NEEDED_MODULE` entries (PS5 tag numbers 0x61000049 / 0x61000045; PS4 numbering also accepted).
- PS5 ELFs call imports through PLT stubs (`jmp [rip+GOT]`); real callers are `E8 rel32` to the stub, not to the GOT.
- Ghidra cannot find functions in these ELFs on its own (no section headers, so no eh_frame analyzer);
  `SeedFunctions.java` supplies them. Unresolved NIDs show up as `sce_unk_<NID>`.
- GFX target: `gfx1013` (the PS5-class LLVM target) decodes SharpEmu's captured shaders with far fewer invalid
  encodings than `gfx1030` (8 vs 59 warnings on one 1.4 KB pixel shader). Some MIMG forms (`0xF0......`)
  still do not decode; try `--mcpu gfx1030` for those.
- Real PS5 system modules (encrypted SELFs) are not on disk and cannot be flattened; only fake-signed SELFs work.
