#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Record source before publish and reject changes before embedding provenance."""

import argparse
import datetime
import hashlib
import json
import pathlib
import subprocess


SOURCE_PATHS = [
    "src", "build", "scripts", "tools", "native", "assets",
    "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
    "global.json", "nuget.config",
]


def git(repo, *args):
    return subprocess.check_output(["git", "-C", str(repo), *args])


def source_state(repo):
    untracked = git(repo, "ls-files", "--others", "--exclude-standard", "-z", "--", *SOURCE_PATHS)
    extra = {}
    for raw in untracked.split(b"\0"):
        if not raw:
            continue
        name = raw.decode("utf-8")
        path = repo / name
        extra[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    diff = git(repo, "diff", "HEAD", "--binary", "--", *SOURCE_PATHS)
    return {
        "revision": git(repo, "rev-parse", "HEAD").decode().strip(),
        "trackedSourceDiffSha256": hashlib.sha256(diff).hexdigest(),
        "dirtySource": bool(diff or extra),
        "untrackedSourceSha256": extra,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["snapshot", "embed"])
    parser.add_argument("--repo", type=pathlib.Path, required=True)
    parser.add_argument("--snapshot", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--sdk")
    args = parser.parse_args()
    state = source_state(args.repo)
    if args.mode == "snapshot":
        args.snapshot.write_text(json.dumps(state, indent=2) + "\n")
        return
    if args.output is None or args.sdk is None:
        parser.error("embed requires --output and --sdk")
    expected = json.loads(args.snapshot.read_text())
    if expected != state:
        raise SystemExit("Source changed during publish; rebuild before assigning artifact provenance.")
    try:
        upstream = git(args.repo, "merge-base", "HEAD", "refs/heads/upstream").decode().strip()
    except subprocess.CalledProcessError:
        upstream = None
    record = {
        "schemaVersion": 1,
        "builtAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "source": state,
        "integratedUpstreamRevision": upstream,
        "sdk": args.sdk,
        "rid": "osx-x64",
        "graphicsBackend": "Metal",
        "hostStack": ["AppKit", "GameController", "CoreAudio"],
    }
    args.output.write_text(json.dumps(record, indent=2) + "\n")


if __name__ == "__main__":
    main()
