#!/usr/bin/env python3
"""Source/command manifest for a separately authorized protection-intent stage.

Does not execute a build, test, native process or game. --verify-source checks
only tracked files. Native runs still require a parent grant and shared lock.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--verify-source', action='store_true')
args = parser.parse_args()
head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
status = subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=ROOT, text=True)
if args.verify_source and status:
    raise SystemExit('Tracked source is not frozen; refusing readiness assertion.')
paths = subprocess.check_output(['git', 'diff', '--name-only', '615ecc174e430c63d1c3cfc9ab0dd34ccc4d05f4', 'HEAD'], cwd=ROOT, text=True).splitlines()
sha = {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in paths if (ROOT / name).is_file()}
print(json.dumps({
    'source': head, 'base': '615ecc174e430c63d1c3cfc9ab0dd34ccc4d05f4',
    'tracked_clean': not status, 'state': 'source-only; no execution evidence',
    'files_sha256': sha,
    'managed_expected_rows': 27,
    'managed_filter': 'FullyQualifiedName~MappedCopyProtectionTests|FullyQualifiedName~PageGuardProtectionIntentTests',
    'native_expected_rows_per_clr': 4,
    'native_filter': 'FullyQualifiedName~MacMappedCopyProtectionNativeTests',
    'native_cases': ['derived-rw', 'guest-readonly', 'partial-derived', 'maximum-failure'],
    'test_project': 'tests/SharpEmu.Host.Memory.Tests/SharpEmu.Host.Memory.Tests.csproj',
    'separate_core_build_required': 'src/SharpEmu.Core/SharpEmu.Core.csproj',
    'build_constraints': '-m:2 -nr:false -p:UseSharedCompilation=false; bounded owned process group',
    'managed_env': {'SHARPEMU_TEST_EXPECT_ARCH': '<Arm64 or X64>'},
    'native_env': {'SHARPEMU_TEST_REQUIRE_MAPPED_COPY_NATIVE': '1', 'SHARPEMU_TEST_EXPECT_ARCH': '<Arm64 or X64>'},
    'native_constraints': 'separate grant; shared gamelock; frozen DLLs; ARM then X64; child30s; TERM10/KILL5 owned only; stop first failure',
    'qualification': 'mapping-copy availability only; no recovery/signal/content/guestwriter authority'
}, indent=2))
