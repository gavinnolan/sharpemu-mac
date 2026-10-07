// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

/// <summary>Compiles a recorded shader through the same shared resource plan as live submissions.</summary>
public static class ShaderPrewarmCompiler
{
    public static bool TryCompilePrewarm(ComputePrewarmRecord record, ShaderCodeCapture code, IGuestGpuBackend compiler,
        bool sharedInt64Atomics, bool execGuardElision, out IGuestCompiledShader? compiled, out BindingLayout? layout, out string error) =>
        ShaderProgramCache.TryCompilePrewarm(record, code, compiler, sharedInt64Atomics, execGuardElision, out compiled, out layout, out error);
}
