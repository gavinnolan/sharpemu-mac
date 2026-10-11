// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The rule that decides whether a render pass's store is dead work. Discarding one
// is worth a whole attachment of bandwidth on a tile-based GPU and costs correctness
// if the clear that follows does not really cover what was discarded, so the rule is
// held to the conservative side on purpose.
public sealed class MetalPassStoresTests
{
    private const uint Width = 3840;
    private const uint Height = 2160;

    [Fact]
    public void AnAttachmentCostsItsAreaItsSamplesAndItsPixelSize()
    {
        // A 4K RGBA16F colour target is 31.6 MiB to load and the same again to store,
        // which is the whole reason a pass split is worth counting.
        Assert.Equal(3840L * 2160 * 8, MetalPassStores.AttachmentBytes(Width, Height, 1, 8));
        Assert.Equal(3840L * 2160 * 4 * 4, MetalPassStores.AttachmentBytes(Width, Height, 4, 4));

        // A sample count of zero is the shared state saying nothing rather than saying
        // no samples, and an attachment is never worth no pixels.
        Assert.Equal(3840L * 2160 * 4, MetalPassStores.AttachmentBytes(Width, Height, 0, 4));
    }

    [Fact]
    public void AnAttachmentWhosePixelSizeWasNeverRecordedCostsNothing()
    {
        Assert.Equal(0, MetalPassStores.AttachmentBytes(Width, Height, 1, 0));
    }

    [Fact]
    public void AWaitingClearBecomesALoadActionOnlyOverExactlyItsOwnPixels()
    {
        Assert.True(Folds(passWidth: Width, passHeight: Height, clearWidth: Width, clearHeight: Height));

        // Smaller render area: pixels the clear reached would stay as they were.
        Assert.False(Folds(passWidth: Width / 2, passHeight: Height, clearWidth: Width, clearHeight: Height));

        // Larger render area: pixels the clear left alone would be cleared.
        Assert.False(Folds(passWidth: Width, passHeight: Height, clearWidth: Width / 2, clearHeight: Height / 2));
    }

    [Fact]
    public void AClearOfMoreThanOneSubresourceIsMoreThanALoadActionCanDo()
    {
        Assert.False(MetalPassStores.ClearIsExactlyTheAttachment(
            clearLevelCount: 2, clearLayerCount: 1, passLayers: 1,
            passWidth: Width, passHeight: Height, clearWidth: Width, clearHeight: Height,
            attachmentWidth: Width, attachmentHeight: Height));
        Assert.False(MetalPassStores.ClearIsExactlyTheAttachment(
            clearLevelCount: 1, clearLayerCount: 6, passLayers: 1,
            passWidth: Width, passHeight: Height, clearWidth: Width, clearHeight: Height,
            attachmentWidth: Width, attachmentHeight: Height));

        // A layered pass clears every layer it renders, which is more than the one the
        // waiting clear covered.
        Assert.False(MetalPassStores.ClearIsExactlyTheAttachment(
            clearLevelCount: 1, clearLayerCount: 1, passLayers: 6,
            passWidth: Width, passHeight: Height, clearWidth: Width, clearHeight: Height,
            attachmentWidth: Width, attachmentHeight: Height));
    }

    [Fact]
    public void AnAttachmentBiggerThanTheClearKeepsThePixelsOutsideIt()
    {
        Assert.False(MetalPassStores.ClearIsExactlyTheAttachment(
            clearLevelCount: 1, clearLayerCount: 1, passLayers: 1,
            passWidth: Width, passHeight: Height, clearWidth: Width, clearHeight: Height,
            attachmentWidth: Width * 2, attachmentHeight: Height));
    }

    private static bool Folds(uint passWidth, uint passHeight, uint clearWidth, uint clearHeight) =>
        MetalPassStores.ClearIsExactlyTheAttachment(
            clearLevelCount: 1,
            clearLayerCount: 1,
            passLayers: 1,
            passWidth: passWidth,
            passHeight: passHeight,
            clearWidth: clearWidth,
            clearHeight: clearHeight,
            attachmentWidth: clearWidth,
            attachmentHeight: clearHeight);

    [Fact]
    public void AClearOverTheWholeSubresourceCoversTheStoreItReplaces()
    {
        Assert.True(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width,
            nextHeight: Height,
            nextLayers: 1,
            nextSamples: 1,
            endingLayers: 1,
            endingSamples: 1,
            subresourceWidth: Width,
            subresourceHeight: Height));
    }

    [Fact]
    public void AClearSmallerThanTheSubresourceLeavesPixelsNobodyWouldRewrite()
    {
        Assert.False(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width / 2,
            nextHeight: Height,
            nextLayers: 1,
            nextSamples: 1,
            endingLayers: 1,
            endingSamples: 1,
            subresourceWidth: Width,
            subresourceHeight: Height));
        Assert.False(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width,
            nextHeight: Height - 1,
            nextLayers: 1,
            nextSamples: 1,
            endingLayers: 1,
            endingSamples: 1,
            subresourceWidth: Width,
            subresourceHeight: Height));
    }

    [Fact]
    public void AClearLargerThanTheSubresourceStillCoversIt()
    {
        // The render area may exceed the mip it writes; what matters is that no pixel
        // of the subresource is left behind.
        Assert.True(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width,
            nextHeight: Height,
            nextLayers: 1,
            nextSamples: 1,
            endingLayers: 1,
            endingSamples: 1,
            subresourceWidth: Width / 2,
            subresourceHeight: Height / 2));
    }

    [Fact]
    public void AClearOfFewerLayersThanThePassWroteCoversNothing()
    {
        Assert.False(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width,
            nextHeight: Height,
            nextLayers: 1,
            nextSamples: 1,
            endingLayers: 6,
            endingSamples: 1,
            subresourceWidth: Width,
            subresourceHeight: Height));
        Assert.True(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width,
            nextHeight: Height,
            nextLayers: 6,
            nextSamples: 1,
            endingLayers: 6,
            endingSamples: 1,
            subresourceWidth: Width,
            subresourceHeight: Height));
    }

    [Fact]
    public void ADifferentlySampledClearIsAClearOfADifferentAttachment()
    {
        Assert.False(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width,
            nextHeight: Height,
            nextLayers: 1,
            nextSamples: 1,
            endingLayers: 1,
            endingSamples: 4,
            subresourceWidth: Width,
            subresourceHeight: Height));
    }

    [Fact]
    public void AnAttachmentOfUnknownSizeIsNeverProvenCovered()
    {
        // The pixel size and the subresource size both arrive with the attachment; a
        // zero means nothing was recorded, and nothing recorded proves nothing.
        Assert.False(MetalPassStores.ClearCoversAttachment(
            nextWidth: Width,
            nextHeight: Height,
            nextLayers: 1,
            nextSamples: 1,
            endingLayers: 1,
            endingSamples: 1,
            subresourceWidth: 0,
            subresourceHeight: Height));
    }
}
