// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

// When a render pass's store is dead work. On a tile-based GPU a store writes the
// whole attachment back to memory, so a store nobody can observe is the most
// expensive kind of nothing, and the rule for proving it is small enough to state
// and test on its own.
internal static class MetalPassStores
{
    /// <summary>
    /// Whether the clear that the next pass performs on an attachment covers all of
    /// what the pass that is ending would have stored. Discarding a store throws away
    /// the entire subresource, not only the part the render area touched, so the clear
    /// has to reach every pixel and every layer of it: its render area at least as
    /// large as the mip, at least as many layers as the ending pass wrote, and the
    /// same sample count, since a clear of a differently sampled attachment is a clear
    /// of a different attachment.
    /// </summary>
    public static bool ClearCoversAttachment(
        uint nextWidth,
        uint nextHeight,
        uint nextLayers,
        uint nextSamples,
        uint endingLayers,
        uint endingSamples,
        uint subresourceWidth,
        uint subresourceHeight) =>
        subresourceWidth != 0 &&
        subresourceHeight != 0 &&
        nextSamples == endingSamples &&
        nextLayers >= endingLayers &&
        nextWidth >= subresourceWidth &&
        nextHeight >= subresourceHeight;
}
