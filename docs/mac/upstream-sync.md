<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Upstream integration

How `sharpemu/sharpemu-mac` tracks `sharpemu/sharpemu`. This is workstream W29
of [the implementation plan](implementation-plan.md): sync status, conflicts and
CI have to be visible, and no published history gets rewritten.

## Branch model

| Branch | Contents | Rules |
| --- | --- | --- |
| `upstream` | A pristine mirror of `sharpemu/sharpemu` `main`. Nothing of ours ever lands here. | Fast-forward only. A non-fast-forward means upstream rewrote history and needs a human decision. |
| `main` | Our work. Upstream commits arrive here only through a merge. | Never rebased, never force-pushed. It is published, so its history is not ours to rewrite. |
| `sync/upstream-<shortsha>` | One integration attempt: branched off `main`, with `upstream` merged into it. | Short-lived. Merged into `main` through a reviewed PR, or deleted. |

Three consequences worth stating plainly:

- **Merge, never rebase.** Rebasing upstream commits onto `main` would give them
  new SHAs, which breaks the "has upstream already fixed this?" lookup that
  patch-identity tracking depends on. After a merge, `git log upstream ^main`
  answers that question directly.
- **The mirror is not a backup of our work.** If `upstream` ever contains a
  commit that is not in `sharpemu/sharpemu`, something went wrong; the sync
  script refuses to move the branch rather than papering over it.
- **A conflicted sync stays visible.** The script leaves the conflicted merge in
  the working tree and records the attempt below, rather than aborting quietly.

## Running a sync

```sh
scripts/mac/sync-upstream.sh --dry-run   # what would happen
scripts/mac/sync-upstream.sh             # do it
```

It adds the `upstream` remote if missing, fetches it, fast-forwards the local
`upstream` branch, branches `sync/upstream-<shortsha>` off `main`, merges, and
appends a row to the log below. It never pushes. Review the result and build it
before opening the PR into `main`.

`.github/workflows/upstream-sync.yml` does the same thing on a daily schedule
(and on demand) in this fork only, opening the PR with `gh`. It exists so a
stalled sync is visible as an open PR rather than as nothing happening.

## Sync log

Fetched is the `upstream/main` commit the sync saw; integrated is the merge
commit it produced on the sync branch, or `conflicts` if the merge stopped and a
human had to finish it.

<!-- sync-log -->

| Date | Fetched upstream | New commits | Integrated | Branch |
| --- | --- | --- | --- | --- |
