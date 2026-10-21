// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;

// Every type in this assembly calls Darwin-specific APIs (Mach VM, dispatch,
// shm_open, Darwin pthread key widths). Annotated at assembly scope so the
// platform-compatibility analyzer flags any consumer that reaches it from
// non-macOS code instead of relying on reviewers to spot it.
[assembly: SupportedOSPlatform("macos")]
