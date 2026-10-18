"""Shared helpers for the tools/re scripts: Sony ELF parsing, NID hashing and name databases.

Image-base convention for every script in tools/re: runtime address = image base + ELF p_vaddr, and
SharpEmu maps the DS eboot at 0x800000000, so "app+X == ELF vaddr X". All addresses printed by the
scripts (imports.tsv, callsites.tsv, ...) are ELF vaddrs, i.e. app+X offsets, not absolute addresses.
"""
import base64
import bisect
import hashlib
import re
import struct
from pathlib import Path

NID_SUFFIX = bytes.fromhex('518d64a635ded8c1e6b039b1c3e55230')  # src/SharpEmu.SourceGenerators/Ps5Nid.cs
ID_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-'
REPO = Path(__file__).resolve().parents[2]
if REPO.parent.name == 'worktrees' and REPO.parent.parent.name == '.claude':
    REPO = REPO.parents[2]  # inside a .claude/worktrees/<n> checkout: the main checkout owns artifacts/

# Dynamic tags. PS4 numbering and the PS5 (Prospero) numbering both appear; accept either.
DT_NEEDED = 1
DT_PLTRELSZ, DT_PLTGOT, DT_STRTAB, DT_SYMTAB, DT_RELA, DT_RELASZ, DT_STRSZ, DT_JMPREL = 2, 3, 5, 6, 7, 8, 10, 0x17
DT_SCE_NEEDED_MODULE = (0x6100000F, 0x61000045)
DT_SCE_IMPORT_LIB = (0x61000015, 0x61000049)
DT_SCE_EXPORT_LIB = (0x61000013, 0x61000047)
DT_SCE_MODULE_INFO = (0x6100000D, 0x61000043)
DT_SCE_SYMTAB, DT_SCE_STRTAB, DT_SCE_STRSIZE = 0x61000039, 0x61000035, 0x61000037
DT_SCE_JMPREL, DT_SCE_PLTRELSZ, DT_SCE_RELA, DT_SCE_RELASZ, DT_SCE_PLTGOT = 0x61000029, 0x6100002D, 0x6100002F, 0x61000031, 0x61000027
R_X86_64_64, R_X86_64_GLOB_DAT, R_X86_64_JUMP_SLOT, R_X86_64_RELATIVE = 1, 6, 7, 8
PT_LOAD, PT_DYNAMIC, PT_GNU_EH_FRAME = 1, 2, 0x6474E550
PF_X = 1


def compute_nid(name: str) -> str:
    h = hashlib.sha1(name.encode('utf-8') + NID_SUFFIX).digest()
    return base64.b64encode(h[:8][::-1]).decode('ascii').rstrip('=').replace('/', '-')


def decode_id(text: str) -> int:
    v = 0
    for c in text:
        v = v * 64 + ID_ALPHABET.index(c)
    return v


class Elf:
    """Minimal view of a flattened Sony ELF: program headers, vaddr<->file offset, dynamic table."""

    def __init__(self, path):
        self.path = str(path)
        self.data = Path(path).read_bytes()
        d = self.data
        if d[:4] != b'\x7fELF':
            raise SystemExit(f'{path}: not an ELF (run fself2elf.py first)')
        self.e_type, self.e_machine = struct.unpack_from('<HH', d, 16)
        self.entry, = struct.unpack_from('<Q', d, 24)
        phoff, = struct.unpack_from('<Q', d, 0x20)
        phentsize, phnum = struct.unpack_from('<HH', d, 0x36)
        self.phdrs = [dict(zip(('type', 'flags', 'offset', 'vaddr', 'paddr', 'filesz', 'memsz', 'align'),
                               struct.unpack_from('<IIQQQQQQ', d, phoff + i * phentsize)))
                      for i in range(phnum)]
        self.loads = [p for p in self.phdrs if p['type'] == PT_LOAD and p['filesz']]
        self.dyn = self._parse_dynamic()

    def va2off(self, va, size=1):
        for p in self.loads:
            if p['vaddr'] <= va and va + size <= p['vaddr'] + p['filesz']:
                return va - p['vaddr'] + p['offset']
        raise ValueError(f'vaddr 0x{va:x} not file-backed')

    def read(self, va, size):
        o = self.va2off(va, size)
        return self.data[o:o + size]

    def cstr(self, va):
        o = self.va2off(va)
        return self.data[o:self.data.index(b'\0', o)].decode('utf-8', 'replace')

    def _parse_dynamic(self):
        dyn = []
        for p in self.phdrs:
            if p['type'] == PT_DYNAMIC:
                for i in range(p['filesz'] // 16):
                    tag, val = struct.unpack_from('<qQ', self.data, p['offset'] + i * 16)
                    if tag == 0:
                        break
                    dyn.append((tag & 0xFFFFFFFFFFFFFFFF, val))
        return dyn

    def tag(self, *tags):
        for t, v in self.dyn:
            if t in tags:
                return v
        return None

    def tags(self, *tags):
        return [v for t, v in self.dyn if t in tags]

    def exec_ranges(self):
        return [(p['vaddr'], p['offset'], p['filesz']) for p in self.loads if p['flags'] & PF_X]

    def modinfo(self):
        """Return ({id: module name}, {id: library name}, {id: export library name}) from the dynamic table."""
        strtab = self.tag(DT_STRTAB, DT_SCE_STRTAB)

        def table(tags):
            out = {}
            for v in self.tags(*tags):
                out[v >> 48] = self.cstr(strtab + (v & 0xFFFFFFFF))
            return out
        return table(DT_SCE_NEEDED_MODULE), table(DT_SCE_IMPORT_LIB), table(DT_SCE_EXPORT_LIB)

    def symbols(self):
        """Yield (index, raw_name, info, shndx, value) for every dynamic symbol."""
        strtab = self.tag(DT_STRTAB, DT_SCE_STRTAB)
        symtab = self.tag(DT_SYMTAB, DT_SCE_SYMTAB)
        size = self.tag(0x6100003F)  # DT_SCE_SYMTABSZ
        if size is None:
            raise SystemExit('no DT_SCE_SYMTABSZ')
        base = self.va2off(symtab)
        for i in range(size // 24):
            name, info, other, shndx, value, _ = struct.unpack_from('<IBBHQQ', self.data, base + i * 24)
            yield i, self.cstr(strtab + name) if name else '', info, shndx, value

    def relocs(self):
        """Yield (kind, r_offset, sym_index, addend) from DT_JMPREL ('jmprel') and DT_RELA ('rela')."""
        for kind, addr_tags, size_tags in (('jmprel', (DT_JMPREL, DT_SCE_JMPREL), (DT_PLTRELSZ, DT_SCE_PLTRELSZ)),
                                           ('rela', (DT_RELA, DT_SCE_RELA), (DT_RELASZ, DT_SCE_RELASZ))):
            addr, size = self.tag(*addr_tags), self.tag(*size_tags)
            if addr is None or not size:
                continue
            base = self.va2off(addr)
            for i in range(size // 24):
                off, info, addend = struct.unpack_from('<QQq', self.data, base + i * 24)
                yield kind, off, info & 0xFFFFFFFF, info >> 32, addend

    def function_starts(self):
        """Sorted function start vaddrs from PT_GNU_EH_FRAME's binary-search table (empty if absent)."""
        for p in self.phdrs:
            if p['type'] != PT_GNU_EH_FRAME:
                continue
            hdr = p['vaddr']
            d = self.read(hdr, 4)
            ver, ptr_enc, cnt_enc, tab_enc = d
            if ver != 1 or cnt_enc != 0x03 or tab_enc != 0x3B:  # udata4 count, sdata4 datarel table
                raise SystemExit(f'unsupported eh_frame_hdr encoding {d.hex()}')
            # eh_frame_ptr: pcrel encoding in ptr_enc (0x1B = pcrel sdata4); skip it
            cnt, = struct.unpack('<I', self.read(hdr + 8, 4))
            raw = self.read(hdr + 12, cnt * 8)
            starts = set()
            for i in range(cnt):
                loc, _fde = struct.unpack_from('<ii', raw, i * 8)
                starts.add(hdr + loc)
            return sorted(starts)
        return []


class NameDb:
    """NID -> export name. Sources: scripts/ps5_names.txt (hashed), extra name lists, HLE exports in src/."""

    def __init__(self):
        self.map = {}
        self.sources = {}
        self.source_of = {}

    def add_name(self, name, source):
        nid = compute_nid(name)
        if nid not in self.map:
            self.map[nid] = name
            self.source_of[nid] = source
            self.sources[source] = self.sources.get(source, 0) + 1

    def add_pair(self, nid, name, source):
        if nid not in self.map:
            self.map[nid] = name
            self.source_of[nid] = source
            self.sources[source] = self.sources.get(source, 0) + 1

    def load_names_file(self, path, source=None):
        source = source or Path(path).name
        for line in Path(path).read_text(encoding='utf-8', errors='ignore').splitlines():
            line = line.strip()
            if line and not line.startswith('#'):
                self.add_name(line, source)

    def load_pairs_file(self, path, source=None):
        """Lines of 'NID<sep>name' (tab/comma/space separated, either order); anything else is a bare name."""
        source = source or Path(path).name
        nid_re = re.compile(r'^[A-Za-z0-9+\-]{11}$')
        for line in Path(path).read_text(encoding='utf-8', errors='ignore').splitlines():
            parts = [x for x in re.split(r'[\t, ]+', line.strip()) if x]
            if len(parts) >= 2 and nid_re.match(parts[0]) and not nid_re.match(parts[1]):
                self.add_pair(parts[0], parts[1], source)
            elif len(parts) >= 2 and nid_re.match(parts[1]) and not nid_re.match(parts[0]):
                self.add_pair(parts[1], parts[0], source)
            elif len(parts) == 1 and parts[0] and not parts[0].startswith('#'):
                self.add_name(parts[0], source)

    def load_src_strings(self, src_root):
        """HLE exports under src/: [SysAbiExport(Nid=..., ExportName=...)] pairs (authoritative NID, working
        label for uncatalogued ones), `"NID" or // name` comment pairs, and every identifier-like string literal
        hashed as a candidate export name."""
        lit = re.compile(r'"((?:[A-Za-z_][A-Za-z0-9_]{3,}))"')
        nid_attr = re.compile(r'Nid\s*=\s*"([^"]+)"\s*,\s*ExportName\s*=\s*"([^"]+)"')
        nid_cmt = re.compile(r'"([A-Za-z0-9+\-]{11})"[^\n/]*//\s*([A-Za-z_]\w+)')
        for p in Path(src_root).rglob('*.cs'):
            try:
                text = p.read_text(errors='ignore')
            except OSError:
                continue
            for nid, name in nid_attr.findall(text):
                self.add_pair(nid, name, 'hle-label')
            for nid, name in nid_cmt.findall(text):
                self.add_pair(nid, name, 'hle-comment')
            for n in set(lit.findall(text)):
                self.add_name(n, 'hle-string')

    def load_ref_libfuncs(self, root):
        """Kyty-style LIB_FUNC("NID", Ns::Name) registrations (semantic names, namespace stripped)."""
        pat = re.compile(r'LIB_FUNC\(\s*"([A-Za-z0-9+\-]{11})"\s*,\s*(?:\w+::)*(\w+)\s*\)')
        for p in Path(root).rglob('*.cpp'):
            try:
                text = p.read_text(errors='ignore')
            except OSError:
                continue
            for nid, name in pat.findall(text):
                self.add_pair(nid, name, 'ref-kyty')

    def get(self, nid):
        return self.map.get(nid)


def default_namedb(extra_files=()):
    db = NameDb()
    names = REPO / 'scripts' / 'ps5_names.txt'
    if names.exists():
        db.load_names_file(names)
    for f in extra_files:
        db.load_pairs_file(f)
    src = REPO / 'src'
    if src.exists():
        db.load_src_strings(src)
    refs = Path.home() / 'Documents' / 'ps5emu' / 'refs'
    if refs.exists():
        db.load_ref_libfuncs(refs)
    return db


def plt_stubs(elf: Elf, slots):
    """Map GOT slot vaddr -> PLT stub vaddr by scanning executable segments for `jmp [rip+disp32]` (FF 25)."""
    slotset = set(slots)
    out = {}
    pat = re.compile(rb'(?=\xff\x25(....))', re.S)  # lookahead: stubs may overlap other matches
    for va, off, size in elf.exec_ranges():
        buf = elf.data[off:off + size]
        for m in pat.finditer(buf, 0):
            pos = m.start()
            d, = struct.unpack('<i', m.group(1))
            tgt = va + pos + 6 + d
            if tgt in slotset and tgt not in out:
                out[tgt] = va + pos
    return out


def containing_function(starts, addr):
    i = bisect.bisect_right(starts, addr) - 1
    return starts[i] if i >= 0 else None
