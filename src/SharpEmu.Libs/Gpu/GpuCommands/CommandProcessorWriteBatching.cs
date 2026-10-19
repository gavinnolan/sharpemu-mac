// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>
/// The switches of the command-processor write batching: the synthetic occlusion dump's sixteen
/// qword stores are recorded as one write, and its pending labels are noted in one pass.
/// </summary>
public static class CommandProcessorWriteBatching
{
    /// <summary>SHARPEMU_GPU_CP_WRITE_BATCH=0 records every store on its own, as before.</summary>
    public static readonly bool Enabled =
        PerfSwitches.Enabled("SHARPEMU_GPU_CP_WRITE_BATCH");

    /// <summary>
    /// SHARPEMU_GPU_CP_WRITE_BATCH_VERIFY=1 (or SHARPEMU_PERF_VERIFY=1): a batch is read back
    /// through the pending-write overlay and a mismatch is logged.
    /// </summary>
    public static readonly bool Verify = PerfSwitches.Verify("SHARPEMU_GPU_CP_WRITE_BATCH_VERIFY");
}
