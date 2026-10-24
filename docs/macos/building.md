<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Building SharpEmu on macOS

## Requirements

- macOS 15 or later. `scripts/macos/build-app.sh` writes `LSMinimumSystemVersion`
  15.0 into the bundle unless `SHARPEMU_MIN_MACOS` says otherwise.
- An Apple Silicon Mac with Rosetta 2, or an Intel Mac.
- The .NET SDK named in [`global.json`](../../global.json): version `10.0.103`,
  with roll-forward to later feature bands (`latestFeature`).
- The Xcode command line tools (`xcode-select --install`). The build hooks and
  `build-app.sh` use `python3`, `lipo`, `codesign`, `plutil`, `ditto` and
  `hdiutil`. Notarization also needs `xcrun notarytool`.
- A GPU that supports Metal. Mesh pipelines need a Metal 3 device; see
  [Metal backend](metal-backend.md).

Full Xcode is not needed to build or run SharpEmu. Metal shaders are compiled
at run time from generated source, so the build has no offline shader step.
Only `tools/SharpEmu.Tools.ShaderDump/check-corpus.sh` calls `xcrun metal`.

Install Rosetta 2 on Apple Silicon with `softwareupdate --install-rosetta`.

## Why the host process is x86-64

The CPU backend runs the guest's x86-64 code natively, so the host process has
to be x86-64. On Apple Silicon that means the `osx-x64` build running under
Rosetta 2. `osx-x64` is the only macOS runtime identifier:

- `Directory.Build.props` pins the macOS runtime identifier of `SharpEmu.CLI`
  to `osx-x64`, whatever the build machine's architecture.
- Building with `-r osx-arm64` fails with an explicit error
  (`src/SharpEmu.CLI/SharpEmu.CLI.csproj`).
- At start-up the CLI rejects a process that is not x86-64 and prints how to
  install Rosetta (`CheckHostArchitecture` in `src/SharpEmu.CLI/Program.cs`).

## Build and test

```sh
dotnet restore SharpEmu.slnx
dotnet build SharpEmu.slnx -c Release
dotnet test SharpEmu.slnx -c Release
```

The CLI and GUI are one executable. After a Release build it is
`artifacts/bin/Release/net10.0/osx-x64/SharpEmu` (`Debug` for a Debug build).
Without arguments it opens the GUI; with a game path it runs the game. See
[Running games](running-games.md).

After each build and publish of `SharpEmu.CLI`, the target in
`build/platforms/macos/macos.targets` runs
`build/platforms/macos/place-host-stack.py` on the executable. The script moves
the read-only `__LINKEDIT` segment so that Rosetta places the process's initial
stack above the guest address limit instead of inside address ranges that titles
map at fixed addresses. It needs `python3`.

[Testing](testing.md) describes the test suites and which of them need a
Metal device.

## Building SharpEmu.app

```sh
scripts/macos/build-app.sh
```

The script:

1. Publishes `SharpEmu.CLI` for `osx-x64`, self-contained, as a directory (not a
   single file) and with ReadyToRun code. The directory layout keeps
   `<base>/plugins` valid for the managed plugin resolver and for FFmpeg, and
   ReadyToRun shortens start-up under Rosetta.
2. Copies the publish output to `SharpEmu.app/Contents/MacOS` and writes
   `Info.plist` from `build/platforms/macos/Info.plist.in`.
3. Embeds `Contents/Resources/build-info.json` (source revision and SDK version,
   from `scripts/macos/build-metadata.py`). The script fails if the source tree
   changed while it was publishing.
4. Thins universal binaries (Skia, HarfBuzz) to their x86-64 slice.
5. Runs the output policy check (see below).
6. Signs the bundle, verifies the signature and, if credentials are present,
   notarizes it.
7. Writes `sharpemu-<version>-osx-x64-app.zip` with `ditto`, which keeps the
   attributes the signature covers. The version comes from `SharpEmuVersion` in
   `Directory.Build.props`.

Output goes to `artifacts/macos/`:
`SharpEmu.app`, the zip, and an optional disk image.

| Variable | Default | Effect |
| --- | --- | --- |
| `SHARPEMU_OUTPUT_DIR` | `artifacts/macos` | Where `SharpEmu.app` is written. |
| `SHARPEMU_BUNDLE_ID` | `io.github.sharpemu.mac` | `CFBundleIdentifier`. |
| `SHARPEMU_MIN_MACOS` | `15.0` | `LSMinimumSystemVersion`. |
| `SHARPEMU_CODESIGN_IDENTITY` | unset (ad-hoc) | Signing identity. |
| `SHARPEMU_MAC_R2R` | `true` | `false` publishes without ReadyToRun. |
| `SHARPEMU_MAC_FORBID_SDL` | `1` | `0` allows SDL3 in the output, for bisecting only. |
| `SHARPEMU_MAC_KEEP_UNIVERSAL` | `0` | `1` keeps arm64 slices in universal libraries. |
| `SHARPEMU_MAC_DMG` | `0` | `1` also writes `sharpemu-<version>-osx-x64.dmg`. |
| `SHARPEMU_BUILD_WORKERS` | `2` | Maximum MSBuild workers. |
| `SHARPEMU_MAC_GET_TASK_ALLOW` | `0` | `1` adds `get-task-allow`. For local measurement builds only. |

Run the result with `artifacts/macos/SharpEmu.app/Contents/MacOS/SharpEmu`, or
open the bundle from Finder.

### Output policy

The macOS build is Metal, AppKit, GameController and CoreAudio only. A publish
fails if its closure contains Vulkan, MoltenVK, SDL3, Silk.NET, the Windows or
X11 Avalonia backends, or the Vulkan and SDL adapter assemblies
(`build/platforms/macos/macos.targets`). The finished output must also contain
no forbidden native libraries, and every Mach-O file must have an x86-64 slice
(`build/platforms/macos/check-macos-output.sh`). The one exception is the
two-process split's native GPU host (`src/SharpEmu.GpuHost`, osx-arm64, not
used by the emulator yet), whose publish output is checked for an arm64 slice
instead.

## Signing and entitlements

The bundle is signed with the hardened runtime (`--options runtime`) and
`--deep`. Without `SHARPEMU_CODESIGN_IDENTITY` the signature is ad-hoc.
The entitlements come from `build/platforms/macos/SharpEmu.entitlements.in`,
which also records why each one is there:

| Entitlement | Why |
| --- | --- |
| `com.apple.security.cs.allow-jit` | The .NET runtime generates machine code at run time. |
| `com.apple.security.cs.allow-unsigned-executable-memory` | The emulator makes guest code pages executable. |
| `com.apple.security.cs.disable-library-validation` | The bundle loads third-party natives that do not share one signing team, and an ad-hoc signature has no team. |

`get-task-allow`, the debugger entitlement, `allow-dyld-environment-variables`
and the App Sandbox are deliberately absent.

Notarization runs only when `SHARPEMU_NOTARY_APPLE_ID`,
`SHARPEMU_NOTARY_PASSWORD` and `SHARPEMU_NOTARY_TEAM_ID` are all set, and it
needs a Developer ID identity in `SHARPEMU_CODESIGN_IDENTITY`. Local builds
never need it. An ad-hoc signed bundle is not accepted by `spctl`; see
[Troubleshooting](troubleshooting.md) for opening a downloaded build.

## Plain publish

CI also publishes the CLI as a flat directory for comparison:

```sh
dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r osx-x64 --self-contained true
```

The release workflow packs this directory as `sharpemu-<version>-osx-x64.tar.gz`
next to the app bundle zip.
