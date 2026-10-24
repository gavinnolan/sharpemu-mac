// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial exposes the shader headers the create export registered.

    internal static ulong GetShaderHeaderAddress(ulong codeAddress)
    {
        SplitCrossingStats.Count(SplitCrossing.ShaderHeaderLookup);
        return _shaderHeadersByCode.TryGetValue(codeAddress, out var header) ? header : 0;
    }

    internal static ShaderHeaderRegistry CreateShaderHeaderRegistry(CpuContext context) =>
        new(
            context,
            GetShaderHeaderAddress,
            codeAddress => ShaderCompiler.Gen5ShaderTranslator.TryGetFusedProgramParts(context, codeAddress, out var continuation, out var continuationHeader)
                ? new FusedProgramParts(continuation, continuationHeader)
                : null);

    // The two-process split keeps a copy of the table in the GPU process, where the command
    // streams look headers up ~100K times a second: the guest process reports every registration
    // (code, header) here, and the GPU process writes them into its own table.
    internal static Action<ulong, ulong>? ShaderHeaderObserver { get; set; }

    internal static KeyValuePair<ulong, ulong>[] SnapshotShaderHeaders() => _shaderHeadersByCode.ToArray();

    internal static void MirrorShaderHeader(ulong codeAddress, ulong headerAddress) =>
        _shaderHeadersByCode[codeAddress] = headerAddress;
}
