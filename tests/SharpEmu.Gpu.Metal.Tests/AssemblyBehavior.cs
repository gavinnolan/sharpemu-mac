// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

// Every native case in this assembly runs behind the exclusive device gate: it creates
// the system default device, records on its own queue and waits for completion. Two
// classes doing that at once make each other's waits and snapshots flaky, which is what
// happened once the image-transfer cases joined the materialization cases. Collections
// here run one at a time instead; the whole assembly is seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
