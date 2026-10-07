#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# macOS output policy check. Given a directory (a publish output or an
# assembled SharpEmu.app), report — and optionally delete — any file that must
# never ship in a macOS SharpEmu artifact, and verify that every Mach-O binary
# has an x86_64 slice.
#
# Called from build/platforms/macos/macos.targets after an osx-* publish and
# again from scripts/macos/build-app.sh on the finished bundle. The matching
# lives here rather than in MSBuild item globs because MSBuild's glob engine
# silently matches nothing for patterns like "**/*SDL3*.dylib" (a wildcard on
# both sides of a filename segment underneath "**"), which would turn the whole
# policy into a no-op that still looks green.
#
# Usage:
#   check-macos-output.sh <directory> [--strip] [--forbid-sdl] [--skip-arch]

set -euo pipefail

directory=""
strip_forbidden=false
forbid_sdl=false
check_arch=true

while [ "$#" -gt 0 ]; do
    case "$1" in
        --strip) strip_forbidden=true ;;
        --forbid-sdl) forbid_sdl=true ;;
        --skip-arch) check_arch=false ;;
        -*) echo "check-macos-output.sh: unknown option $1" >&2; exit 2 ;;
        *)
            if [ -n "${directory}" ]; then
                echo "check-macos-output.sh: unexpected argument $1" >&2
                exit 2
            fi
            directory="$1"
            ;;
    esac
    shift
done

if [ -z "${directory}" ]; then
    echo "check-macos-output.sh: a directory to check is required" >&2
    exit 2
fi
if [ ! -d "${directory}" ]; then
    echo "check-macos-output.sh: ${directory} is not a directory" >&2
    exit 2
fi

# Metal-only: a Vulkan loader, an ICD manifest or a MoltenVK dylib in the
# output means something re-introduced a GPU dependency this fork removed.
# Scoped to native payloads and loader manifests: the managed Vulkan/SDL
# assemblies are now kept out of a macOS closure by project reference instead
# (SharpEmu.Gpu.Vulkan, SharpEmu.Adapters.Sdl and SharpEmu.ShaderCompiler.Vulkan
# are referenced only for win-*/linux-* RIDs), which SharpEmuMacValidatePublishClosure
# in macos.targets enforces before single-file bundling - the place this
# directory scan cannot reach. Avalonia.Vulkan.dll is still expected: it is
# Avalonia's own managed assembly and ships on every platform.
forbidden_find_args=(
    '(' -iname '*moltenvk*' ')'
    -o '(' -iname 'libvulkan*' ')'
    -o '(' -iname '*vulkan*.dylib' ')'
    -o '(' -iname '*_icd.json' ')'
    -o '(' -iname 'vklayer_*' ')'
    # Sibling-OS native payloads. An unscoped native asset package is the only
    # way an ELF shared object reaches a macOS output.
    -o '(' -name '*.so' ')'
    -o '(' -name '*.so.*' ')'
)

# The macOS runtime path is native (Metal, AppKit, GameController), so SDL3 is
# forbidden; SharpEmuMacForbidSdl defaults to true and the bundle script passes
# SHARPEMU_MAC_FORBID_SDL. It stays a flag so a bisect can turn it off.
if [ "${forbid_sdl}" = true ]; then
    forbidden_find_args+=(-o '(' -iname '*sdl3*' ')')
fi

# Match symlinks too: renaming a forbidden payload behind a library alias must
# not make the policy pass. The host split retired all SDL exceptions.
forbidden="$(find "${directory}" '(' -type f -o -type l ')' '(' "${forbidden_find_args[@]}" ')' | sort)"

# RID-specific publish flattens the natives it selects, so a surviving
# runtimes/<other-os> tree means asset selection did not happen at all.
sibling_runtimes="$(find "${directory}" -type d -path '*/runtimes/*' \
    '(' -name 'win*' -o -name 'linux*' -o -name 'android*' -o -name 'browser*' ')' | sort)"

if [ -n "${sibling_runtimes}" ]; then
    forbidden="$(printf '%s\n%s\n' "${forbidden}" "${sibling_runtimes}" | sed '/^$/d' | sort)"
fi

if [ -n "${forbidden}" ]; then
    if [ "${strip_forbidden}" = true ]; then
        echo "macOS output policy: stripping forbidden entries from ${directory}:"
        while IFS= read -r entry; do
            [ -n "${entry}" ] || continue
            printf '  %s\n' "${entry}"
            rm -rf -- "${entry}"
        done <<EOF
${forbidden}
EOF
        forbidden=""
    else
        echo "macOS output policy violation in ${directory}:" >&2
        printf '%s\n' "${forbidden}" >&2
        echo "The macOS build is Metal-only and osx-x64-only, so it must not carry" >&2
        echo "Vulkan/MoltenVK assets or sibling-OS natives. Scope the offending native" >&2
        echo "asset package to its RID; see build/platforms/macos/macos.targets." >&2
        exit 1
    fi
fi

if [ "${check_arch}" != true ]; then
    exit 0
fi

# Every Mach-O in the artifact has to be able to run in the x86_64 process the
# emulator requires. A universal binary with an x86_64 slice is fine; an
# arm64-only one would fail to load at runtime with a confusing dyld error, so
# catch it here instead.
arch_failures=""
while IFS= read -r candidate; do
    [ -n "${candidate}" ] || continue
    case "$(file -b "${candidate}")" in
        *Mach-O*) ;;
        *) continue ;;
    esac
    slices="$(lipo -archs "${candidate}" 2>/dev/null || true)"
    case " ${slices} " in
        *" x86_64 "*) ;;
        *) arch_failures="${arch_failures}${candidate} [${slices:-unreadable}]"$'\n' ;;
    esac
done <<EOF
$(find "${directory}" -type f '(' -perm -u+x -o -name '*.dylib' -o -name '*.so' ')')
EOF

if [ -n "${arch_failures}" ]; then
    echo "macOS architecture policy violation in ${directory}:" >&2
    printf '%s' "${arch_failures}" >&2
    echo "Every Mach-O binary needs an x86_64 slice: SharpEmu executes guest x86-64" >&2
    echo "code natively, so the host process is always x86-64 (Rosetta 2 on Apple" >&2
    echo "Silicon)." >&2
    exit 1
fi

echo "macOS output policy: ${directory} is clean (no forbidden assets, all Mach-O binaries have an x86_64 slice)."
