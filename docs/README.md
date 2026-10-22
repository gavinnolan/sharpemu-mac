<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu documentation

## macOS

This fork targets macOS with the Metal backend. Start here.

- [Building on macOS](macos/building.md): requirements, build and test commands, and how the app bundle and release assets are made.
- [Running games on macOS](macos/running-games.md): GUI and command line, game layouts, controls, caches and the data folder.
- [macOS configuration](macos/configuration.md): environment variables for the host, memory, shader translation and diagnostics.
- [Metal backend](macos/metal-backend.md): how the Metal renderer is structured, with pointers into the source.
- [Testing on macOS](macos/testing.md): the .NET and Python tests, CI, and the game harness scripts.
- [macOS benchmarks](macos/benchmarks.md): frame rates, memory and known issues per tested game on the first release.
- [macOS troubleshooting](macos/troubleshooting.md): startup, performance and rendering problems and what to check.

## General

- [Performance display](performance-overlay.md): the FPS, CPU and GPU display, its modes and shortcuts.
- [Release use](release-use.md): how to prepare and publish a release with `scripts/release.py`.
- [Game patches](patches.md): how to install and write PS-Game-Patch XML patches.
- [Live debug server](debugger-server.md): a TCP server that lets an external process inspect and control a running guest.
- [Import results and diagnostic logging](import-result-logging.md): import-result warnings and the related diagnostic switches.
- [Environment variables not on the GUI options pages](sharpemu-gui-undocumented-env-vars.md): the full list of hidden `SHARPEMU_*` variables. Many entries are Vulkan-only or Windows-only.
- [Aerolib catalog](aerolib-catalog.md): look up export names and NIDs with `scripts/aerolib_catalog.py`.
- [Bink 2 bridge](bink2-bridge.md): how Demon's Souls plays Bink 2 video.
- [Guest write watch](guest-write-watch.md): optional tool that finds managed code that writes guest memory.
- [Guest ownership trace](guest-ownership-trace.md): inspect memory at selected guest import calls with a JSON plan.

## Diagnostics for the Vulkan renderer

These guides describe tracing in the Vulkan renderer, which the macOS build does not include. Parts of some of them are renderer-neutral.

- [Buffer parameter tracing](buffer-parameter-tracing.md): compare CPU backing bytes with a RenderDoc buffer.
- [Image clear tracing](image-clear-tracing.md): trace color-target and volume clear decisions and take draw snapshots.
- [Image memory diagnostics](image-memory-diagnostics.md): image cache memory counters and failure reports.
- [Indexed draw tracing](indexed-draw-tracing.md): trace indexed draws of a chosen index count.
- [Mesh draw tracing](mesh-draw-tracing.md): trace mesh draws and permutation compiles.
- [Visibility result tracing](visibility-result-tracing.md): trace occlusion query records.
- [Shader rejection diagnostics](compute-rejection-diagnostics.md): why compute dispatches and draws are rejected, and `SHARPEMU_STRICT_COMPUTE`.

## Windows-only

- [Windows crash capture](windows-crash-capture.md): crash dumps from the launcher.
- [Native function tracing](native-function-tracing.md): a temporary diagnostic that records native function entry and return hits.
