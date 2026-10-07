// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>Guest command submission and completion operations, independent of the stream implementation.</summary>
public interface IGuestCommandStream
{
    void Submit(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots);
    IdleOutcome Done();
    BlockedSnapshot SnapshotBlocked();
}
