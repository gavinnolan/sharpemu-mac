// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests;

internal static partial class MetalNative
{
    /// <summary>Runs a hand-written kernel over one shared buffer bound at index 0, as a
    /// single threadgroup, and returns the buffer's contents afterwards.</summary>
    public static bool TryExecuteOnBuffer(
        nint library,
        string entryPoint,
        byte[] bufferContents,
        uint threadsPerThreadgroup,
        out byte[] result,
        out string error)
    {
        result = [];
        var buffer = SharedBuffer(bufferContents);
        if (buffer == 0)
        {
            error = "failed to create the data buffer";
            return false;
        }

        return RunKernel(
            library,
            entryPoint,
            [(buffer, 0)],
            [],
            threadsPerThreadgroup,
            buffer,
            bufferContents.Length,
            out result,
            out error);
    }
}
