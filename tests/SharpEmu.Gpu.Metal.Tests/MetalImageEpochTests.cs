// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The image-cache mutation counts a cached image bumps itself: each state change counts
/// once, a call that changes nothing counts nothing, and an image that is not in a cache
/// (or was retired from one) counts nothing. The cache-side counts run on the device, in
/// MetalDccMaterializationTests.Epochs.cs.
/// </summary>
public sealed class MetalImageEpochTests
{
    private const ulong Size = 0x10000;

    private static MetalCachedImage CreateImage(MetalImageEpochs epochs)
    {
        var description = ImageDescription.Create();
        description.PixelFormat = GpuFormat.R8G8B8A8Unorm;
        description.BytesPerBlock = 4;
        description.Pitch = 1;
        description.Data = new GuestSpan(0x2_0000_0000, Size);
        var shape = new MetalTextureShape(
            MtlFormat.Rgba8Unorm, Mtl.TextureType2D, 1, 1, 1, 1, 1, 1, Mtl.TextureUsageShaderRead);
        var image = new MetalCachedImage(in description, in shape, 0) { Epochs = epochs };
        // A new image starts dirty; the counts below start from a clean one.
        image.RefreshComplete();
        return image;
    }

    private static MetalTextureShape Shape(MetalCachedImage image) => image.Shape;

    private readonly record struct Counts(long Shape, long GpuModified, long Dcc, long Binding)
    {
        public static Counts Of(MetalImageEpochs epochs) => new(epochs.Shape, epochs.GpuModified, epochs.Dcc, epochs.Binding);
    }

    [Fact]
    public void CpuDirtyCountsTheTransitionAndAWiderRange()
    {
        var epochs = new MetalImageEpochs();
        var image = CreateImage(epochs);
        var start = Counts.Of(epochs);

        image.MarkCpuDirty(MetalImageDirtyCause.CpuWrite, 0x1000, 0x2000);
        Assert.Equal(start with { Binding = start.Binding + 1 }, Counts.Of(epochs));

        image.MarkCpuDirty(MetalImageDirtyCause.CpuWrite, 0x1000, 0x2000);
        image.MarkCpuDirty(MetalImageDirtyCause.CpuWrite, 0x1400, 0x1800);
        Assert.Equal(start.Binding + 1, epochs.Binding);

        image.MarkCpuDirty(MetalImageDirtyCause.CpuWrite, 0x1800, 0x3000);
        Assert.Equal(start.Binding + 2, epochs.Binding);
    }

    [Fact]
    public void RefreshCountsOnlyWhatItCleans()
    {
        var epochs = new MetalImageEpochs();
        var image = CreateImage(epochs);
        var start = epochs.Binding;

        image.RefreshComplete();
        image.RefreshComplete(0, Size);
        Assert.Equal(start, epochs.Binding);

        image.MarkCpuDirty(MetalImageDirtyCause.CpuWrite, 0x1000, 0x4000);
        // A cut from the front narrows the range; a cut from the middle cannot.
        image.RefreshComplete(0x1000, 0x2000);
        Assert.Equal(start + 2, epochs.Binding);
        image.RefreshComplete(0x2800, 0x3000);
        Assert.Equal(start + 2, epochs.Binding);
        image.RefreshComplete(0x2000, 0x4000);
        Assert.Equal(start + 3, epochs.Binding);
        Assert.False(image.IsCpuDirty);
    }

    [Fact]
    public void GpuModifiedCountsTheCleanToModifiedTransition()
    {
        var epochs = new MetalImageEpochs();
        var image = CreateImage(epochs);
        var start = Counts.Of(epochs);

        image.MarkGpuModified();
        Assert.Equal(start with { GpuModified = start.GpuModified + 1, Binding = start.Binding + 1 }, Counts.Of(epochs));

        // Drawn into again: nothing a binding sees has changed.
        image.MarkGpuModified();
        image.MarkGpuModified(depthPlaneChanged: false);
        Assert.Equal(start with { GpuModified = start.GpuModified + 1, Binding = start.Binding + 1 }, Counts.Of(epochs));
    }

    // A writeback (or an unmap) leaves the image clean: counted once, and not again for an
    // image that was clean already.
    [Fact]
    public void WritebackCountsTheModifiedToCleanTransition()
    {
        var epochs = new MetalImageEpochs();
        var image = CreateImage(epochs);
        image.ClearGpuModified();
        var start = Counts.Of(epochs);
        Assert.Equal(start, Counts.Of(epochs));

        image.MarkGpuModified();
        var modified = Counts.Of(epochs);
        image.ClearGpuModified();
        Assert.Equal(modified with { Binding = modified.Binding + 1 }, Counts.Of(epochs));
        image.ClearGpuModified();
        Assert.Equal(modified.Binding + 1, epochs.Binding);
    }

    [Fact]
    public void AliasPublicationCountsWhenThePublishedRangeChanges()
    {
        var epochs = new MetalImageEpochs();
        var image = CreateImage(epochs);
        image.MarkGpuModified();
        var start = epochs.Binding;

        image.MarkPublished(0x0000, 0x2000);
        Assert.Equal(start + 1, epochs.Binding);
        image.MarkPublished(0x0000, 0x2000);
        image.MarkPublished(0x0800, 0x1000);
        Assert.Equal(start + 1, epochs.Binding);
        image.MarkPublished(0x1000, 0x4000);
        Assert.Equal(start + 2, epochs.Binding);

        // A new GPU write forgets the publication: counted though the image stays modified.
        image.MarkGpuModified();
        Assert.Equal(start + 3, epochs.Binding);
        Assert.Equal(1, epochs.GpuModified);
    }

    public enum Replacement
    {
        PromoteToOwned,
        ReplaceLevels,
        ReplaceWithCopy,
        ReplaceTexture,
    }

    // Every way an image's texture and views are replaced counts as a shape change, and the
    // retired image that takes the old texture counts nothing from then on.
    [Theory]
    [InlineData(Replacement.PromoteToOwned)]
    [InlineData(Replacement.ReplaceLevels)]
    [InlineData(Replacement.ReplaceWithCopy)]
    [InlineData(Replacement.ReplaceTexture)]
    public void TextureReplacementCountsAsAShapeChange(Replacement replacement)
    {
        var epochs = new MetalImageEpochs();
        var image = CreateImage(epochs);
        var start = Counts.Of(epochs);
        var shape = Shape(image);

        MetalCachedImage? retired = replacement switch
        {
            Replacement.PromoteToOwned => image.PromoteToOwned(0, in shape),
            Replacement.ReplaceLevels => image.ReplaceLevels(0, in shape, 0, 0, Size),
            Replacement.ReplaceWithCopy => image.ReplaceWithCopy(0, in shape, 1),
            _ => null,
        };
        if (replacement == Replacement.ReplaceTexture)
        {
            image.ReplaceTexture(0, in shape);
        }

        Assert.Equal(start.Shape + 1, epochs.Shape);
        Assert.True(epochs.Binding > start.Binding);
        Assert.Equal(start.GpuModified, epochs.GpuModified);

        if (retired is not null)
        {
            Assert.Null(retired.Epochs);
            var after = Counts.Of(epochs);
            retired.MarkGpuModified();
            retired.MarkCpuDirty(MetalImageDirtyCause.CpuWrite);
            Assert.Equal(after, Counts.Of(epochs));
        }
    }

    [Fact]
    public void AnImageOutsideACacheCountsNothing()
    {
        var epochs = new MetalImageEpochs();
        var image = CreateImage(epochs);
        image.Epochs = null;
        var start = Counts.Of(epochs);

        image.MarkCpuDirty(MetalImageDirtyCause.CpuWrite);
        image.MarkGpuModified();
        image.ClearGpuModified();

        Assert.Equal(start, Counts.Of(epochs));
    }

    [Fact]
    public void SameMetadataComparesEveryField()
    {
        var left = new MetadataDescription
        {
            Range = new GuestSpan(0x1000, 0x1000),
            Kind = MetadataKind.Dcc,
            DccAlphaMsb = true,
        };
        var right = left;
        Assert.True(MetalImageEpochs.SameMetadata(in left, in right));

        right.PackedColorClear.Uint32_3 = 1;
        Assert.False(MetalImageEpochs.SameMetadata(in left, in right));
        right = left with { ColorMetadataBaseLayer = 2 };
        Assert.False(MetalImageEpochs.SameMetadata(in left, in right));
        right = left with { Range = new GuestSpan(0x1000, 0x2000) };
        Assert.False(MetalImageEpochs.SameMetadata(in left, in right));
    }
}
