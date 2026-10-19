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
    /// What one attachment costs a tile-based GPU for one pass: the render area, every
    /// sample of it, and the bytes one pixel of that attachment really occupies. A load
    /// pays it once and a store pays it again, which is why a pass split is expensive
    /// however few draws the pass contained.
    /// </summary>
    public static long AttachmentBytes(uint width, uint height, uint samples, uint texelBytes) =>
        (long)width * height * Math.Max(1u, samples) * texelBytes;

    /// <summary>
    /// Whether a clear that is waiting to be encoded is exactly what a pass about to
    /// begin would do to one of its attachments as a load action. Exactly is the whole
    /// rule, in both directions: a load action clears the render area and nothing else,
    /// so a pass whose area is smaller than the clear leaves pixels uncleared, and a
    /// pass whose area is larger clears pixels the clear would have left alone. One
    /// subresource, one layer, and the render area, the clear and the attachment all the
    /// same size.
    /// </summary>
    public static bool ClearIsExactlyTheAttachment(
        uint clearLevelCount,
        uint clearLayerCount,
        uint passLayers,
        uint passWidth,
        uint passHeight,
        uint clearWidth,
        uint clearHeight,
        uint attachmentWidth,
        uint attachmentHeight) =>
        clearLevelCount == 1 &&
        clearLayerCount == 1 &&
        passLayers == 1 &&
        clearWidth != 0 &&
        clearHeight != 0 &&
        passWidth == clearWidth &&
        passHeight == clearHeight &&
        attachmentWidth == clearWidth &&
        attachmentHeight == clearHeight;

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
