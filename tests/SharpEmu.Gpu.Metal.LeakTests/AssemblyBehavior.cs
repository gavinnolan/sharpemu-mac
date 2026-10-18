// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

// Every case measures the whole process's malloc blocks across its run, so a second class
// running beside it (its rig, its driver objects, its frees) is counted as this one's
// growth. One at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
