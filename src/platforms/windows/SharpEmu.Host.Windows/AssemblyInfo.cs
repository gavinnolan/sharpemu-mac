// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;

// Every type in this assembly calls Win32. Annotated at assembly scope so the
// platform-compatibility analyzer flags any consumer that reaches it from
// non-Windows code instead of relying on reviewers to spot it.
[assembly: SupportedOSPlatform("windows")]
