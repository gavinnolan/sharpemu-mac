#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Pulls sharpemu/sharpemu into this fork, by merge and never by rewrite.
#
# The branch model, in short:
#   upstream  pristine mirror of sharpemu/sharpemu main, fast-forward only
#   main      our work; upstream arrives through merge PRs
#   sync/*    the integration branch for one such merge
#
# What this does:
#   1. Makes sure an `upstream` remote points at sharpemu/sharpemu and fetches it.
#   2. Fast-forwards the local `upstream` branch to upstream/main, refusing
#      anything that is not a fast-forward (that would mean upstream rewrote
#      history, which is a decision for a human).
#   3. Branches sync/upstream-<shortsha> off main and merges `upstream` into it.
#
# It never pushes, never rebases, and never touches main itself.
#
# Usage: sync-upstream.sh [--dry-run]

set -euo pipefail

UPSTREAM_REMOTE="upstream"
UPSTREAM_URL="https://github.com/sharpemu/sharpemu.git"
UPSTREAM_BRANCH="main"
MIRROR_BRANCH="upstream"
INTEGRATION_BASE="main"

dry_run=false
if [ "${1:-}" = "--dry-run" ]; then
    dry_run=true
    shift
fi
if [ "$#" -gt 0 ]; then
    echo "sync-upstream.sh: unexpected argument $1" >&2
    exit 2
fi

repo_root="$(git rev-parse --show-toplevel)"
cd "${repo_root}"

run() {
    if [ "${dry_run}" = true ]; then
        echo "   [dry-run] $*"
    else
        "$@"
    fi
}

# ------------------------------------------------------------- 1. remote ----

if git remote get-url "${UPSTREAM_REMOTE}" >/dev/null 2>&1; then
    current_url="$(git remote get-url "${UPSTREAM_REMOTE}")"
    if [ "${current_url}" != "${UPSTREAM_URL}" ]; then
        echo "The '${UPSTREAM_REMOTE}' remote points at ${current_url}, not ${UPSTREAM_URL}." >&2
        echo "Fix it deliberately; this script will not repoint an existing remote." >&2
        exit 1
    fi
else
    echo "==> Adding remote ${UPSTREAM_REMOTE} -> ${UPSTREAM_URL}"
    run git remote add "${UPSTREAM_REMOTE}" "${UPSTREAM_URL}"
fi

echo "==> Fetching ${UPSTREAM_REMOTE}"
run git fetch --prune "${UPSTREAM_REMOTE}"

if [ "${dry_run}" = true ] && \
   ! git rev-parse --verify --quiet "${UPSTREAM_REMOTE}/${UPSTREAM_BRANCH}" >/dev/null; then
    echo "   [dry-run] ${UPSTREAM_REMOTE}/${UPSTREAM_BRANCH} not fetched yet; stopping here."
    exit 0
fi

fetched_sha="$(git rev-parse "${UPSTREAM_REMOTE}/${UPSTREAM_BRANCH}")"
fetched_short="$(git rev-parse --short "${UPSTREAM_REMOTE}/${UPSTREAM_BRANCH}")"
echo "==> ${UPSTREAM_REMOTE}/${UPSTREAM_BRANCH} is at ${fetched_short}"

# ------------------------------------------------ 2. mirror branch (ff) ----

if git rev-parse --verify --quiet "refs/heads/${MIRROR_BRANCH}" >/dev/null; then
    if git merge-base --is-ancestor "refs/heads/${MIRROR_BRANCH}" "${fetched_sha}"; then
        if [ "$(git rev-parse "refs/heads/${MIRROR_BRANCH}")" = "${fetched_sha}" ]; then
            echo "==> ${MIRROR_BRANCH} is already at ${fetched_short}"
        else
            echo "==> Fast-forwarding ${MIRROR_BRANCH} to ${fetched_short}"
            # -f fails outright if the branch is checked out in this worktree
            # or any other, which is the behaviour we want: an in-progress
            # checkout of the mirror must not be moved under someone's feet.
            run git branch -f "${MIRROR_BRANCH}" "${fetched_sha}"
        fi
    else
        echo "Refusing to move ${MIRROR_BRANCH}: ${fetched_short} is not a descendant of it." >&2
        echo "Upstream rewrote history, or the mirror has local commits it should not have." >&2
        echo "Inspect with: git log --oneline ${MIRROR_BRANCH} ^${UPSTREAM_REMOTE}/${UPSTREAM_BRANCH}" >&2
        exit 1
    fi
else
    echo "==> Creating mirror branch ${MIRROR_BRANCH} at ${fetched_short}"
    run git branch "${MIRROR_BRANCH}" "${fetched_sha}"
fi

# ------------------------------------------------- 3. integration branch ----

if git merge-base --is-ancestor "${fetched_sha}" "refs/heads/${INTEGRATION_BASE}"; then
    echo "==> ${INTEGRATION_BASE} already contains ${fetched_short}; nothing to integrate."
    exit 0
fi

new_commits="$(git rev-list --count "${fetched_sha}" "^refs/heads/${INTEGRATION_BASE}")"
echo "==> ${new_commits} upstream commit(s) not yet in ${INTEGRATION_BASE}"

if [ "${dry_run}" = true ]; then
    echo "   [dry-run] would branch sync/upstream-${fetched_short} off ${INTEGRATION_BASE} and merge ${MIRROR_BRANCH} into it"
    exit 0
fi

if [ -n "$(git status --porcelain)" ]; then
    echo "The working tree is dirty; commit or set aside your changes first." >&2
    exit 1
fi

sync_branch="sync/upstream-${fetched_short}"
if git rev-parse --verify --quiet "refs/heads/${sync_branch}" >/dev/null; then
    echo "Branch ${sync_branch} already exists; delete it or finish the sync it holds." >&2
    exit 1
fi

original_ref="$(git symbolic-ref --quiet --short HEAD || git rev-parse HEAD)"
echo "==> Creating ${sync_branch} from ${INTEGRATION_BASE}"
git switch --create "${sync_branch}" "refs/heads/${INTEGRATION_BASE}"

# A merge, deliberately, and never a rebase: main is published, so its history
# is not ours to rewrite, and a merge keeps each upstream commit identifiable
# for tracking which patches are local and which came from upstream.
merge_status=0
git merge --no-ff --no-edit \
    -m "Merge upstream ${fetched_short} into ${INTEGRATION_BASE}" \
    "refs/heads/${MIRROR_BRANCH}" || merge_status=$?

if [ "${merge_status}" -ne 0 ]; then
    echo
    echo "==> Merge stopped with conflicts in:"
    git diff --name-only --diff-filter=U | sed 's/^/      /'
    echo
    echo "    Resolve them, 'git add' the results and 'git commit', then open a PR"
    echo "    from ${sync_branch} into ${INTEGRATION_BASE}."
    echo "    To abandon: git merge --abort && git switch ${original_ref} && git branch -D ${sync_branch}"
    integrated="conflicts"
else
    integrated="$(git rev-parse --short HEAD)"
    echo "==> Merged cleanly as ${integrated}"
fi

echo
echo "Next: review, run the build, then open a PR from ${sync_branch} into ${INTEGRATION_BASE}."
echo "      Nothing has been pushed."
