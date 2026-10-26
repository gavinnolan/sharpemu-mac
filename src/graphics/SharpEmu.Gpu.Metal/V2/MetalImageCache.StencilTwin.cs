// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

// A multisampled depth-stencil surface whose stencil plane a shader reads through an 8-bit integer
// view binds a stencil view (X32_Stencil8) of the depth texture, as a depth surface sampled as its
// colour twin binds the depth texture (MetalImageCache.DepthTwin.cs). Nothing else can serve that
// read: the hand-over between a depth image and an 8-bit image over its stencil bytes goes through a
// buffer (MetalImageTransfer.TryEncodeStencilFromColor) or guest memory, and a multisampled texture
// has neither a blit to a buffer nor an upload. Ghost of Tsushima copies its two-sample checkerboard
// G-buffer depth and stencil into the depth target its terrain draws against; the stencil came through
// an 8-bit image nothing had filled, so every object's stencil reference read zero and the terrain
// material passes treated the objects' pixels by stale stencil.
//
// Only sampling goes this way; an image the guest stores into keeps its own texture.
internal sealed partial class MetalImageCache
{
    // SHARPEMU_METAL_V2_STENCIL_TWIN=0 gives such a read its own (unfilled) 8-bit texture again.
    private static readonly bool StencilTwinsEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_STENCIL_TWIN") != "0";

    private long _stencilTwinBinds;

    /// <summary>Sampled stencil reads served by a depth texture's stencil view since the last read.</summary>
    public long TakeStencilTwinBinds() => Interlocked.Exchange(ref _stencilTwinBinds, 0);

    /// <summary>
    /// The multisampled depth image whose stencil plane <paramref name="request"/> samples as 8-bit
    /// integers, if one is cached: the caller binds it, and its view of the request is the stencil view.
    /// </summary>
    public bool TryFindStencilTwin(in ImageRequest request, out ResourceSlotIdentifier depthImage)
    {
        depthImage = ResourceSlotIdentifier.Invalid;
        ref readonly var requested = ref request.Description;
        if (!StencilTwinsEnabled || request.Role != ImageRole.Texture || requested.Samples <= 1 ||
            requested.PixelFormat != GpuFormat.R8Uint || request.View.Format != GpuFormat.R8Uint ||
            requested.Resources.Levels != 1 || requested.TransferLayers != 1)
        {
            return false;
        }

        using var held = _lock.Hold();
        foreach (var ownerIdentifier in _stencilOwners)
        {
            if (_slots.TryGet(ownerIdentifier) is not { } depth || !depth.Description.HasStencil ||
                depth.Description.Stencil.Address != requested.Data.Address || depth.Texture == 0 ||
                depth.FirstLevel != 0 || depth.Shape.PixelFormat != MtlFormat.Depth32FloatStencil8 ||
                (depth.Shape.Usage & Mtl.TextureUsagePixelFormatView) == 0 ||
                depth.Description.Samples != requested.Samples ||
                depth.Description.Extent.Width != requested.Extent.Width ||
                depth.Description.Extent.Height != requested.Extent.Height)
            {
                continue;
            }

            _stencilTwinBinds++;
            depthImage = ownerIdentifier;
            return true;
        }

        return false;
    }
}
