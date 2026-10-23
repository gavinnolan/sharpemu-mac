// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Gpu.Metal.V2;

// Tessellated draws run as a hull kernel and a draw of every patch's corners
// (SharpEmu.Libs.Gpu.Rendering.RenderExecutor.Tessellation.cs); this side binds the kernel's launch.
internal sealed partial class MetalRenderHost
{
    public bool SupportsTessellationEmulation => true;

    // The launch the next dispatch binds: patch count and first instance, and the vertex ids in the ring.
    private bool _hullLaunchPending;
    private uint _hullPatchCount;
    private uint _hullFirstInstance;
    private nint _hullVertexBuffer;
    private ulong _hullVertexOffset;

    public void BindHullLaunch(ReadOnlySpan<uint> vertexIds, uint patchCount, uint firstInstance)
    {
        var binding = UploadTransient(MemoryMarshal.AsBytes(vertexIds.IsEmpty ? [0u] : vertexIds), 16);
        _hullVertexBuffer = (nint)binding.Handle;
        _hullVertexOffset = binding.Offset;
        _hullPatchCount = patchCount;
        _hullFirstInstance = firstInstance;
        _hullLaunchPending = true;
    }

    // Right before the dispatch: the encoder may be a new one by then.
    private unsafe void ApplyHullLaunch(nint encoder)
    {
        if (!_hullLaunchPending)
        {
            return;
        }

        _hullLaunchPending = false;
        var launch = stackalloc uint[4] { _hullPatchCount, _hullFirstInstance, 0, 0 };
        MetalNative.SendSetBytes(encoder, SelHullSetBytes, (nint)launch, 4 * sizeof(uint), Gen5TessellationLaunch.HullLaunchBufferIndex);
        MetalNative.SendSetBuffer(
            encoder, SelSetComputeBuffer, _hullVertexBuffer, (nuint)_hullVertexOffset, Gen5TessellationLaunch.HullVertexBufferIndex);
    }

    private static readonly nint SelHullSetBytes = MetalNative.Selector("setBytes:length:atIndex:");
}
