// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

/// <summary>
/// The Metal backend's compiled shader: MSL source plus the reflection data
/// (<see cref="Gen5MslShader"/>) the presenter needs to create and bind pipeline
/// states. The diagnostics payload is the source text — Metal has no portable
/// binary form until an MTLBinaryArchive is introduced.
/// </summary>
internal sealed class MetalCompiledGuestShader(Gen5MslShader shader) : IGuestCompiledShader
{
    private byte[]? _payload;

    public Gen5MslShader Shader { get; } = shader;

    // Library creation may run on the command worker before render admission.
    // The per-program gate serializes it with renderer cache lookup/creation.
    internal readonly object LibraryGate = new();
    internal nint CachedLibrary;
    internal nint LibraryDevice;

    public byte[] Payload => _payload ??= Encoding.UTF8.GetBytes(Shader.Source);

    public string PayloadFileExtension => "msl";
}
