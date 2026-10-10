// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A 4K depth-stencil surface is the most expensive transfer Silent Hill records, so both
// directions are timed whole and then stage by stage. Every number is bytes moved per
// second: the figure of merit is how close a stage comes to the memory system, which a
// 4K colour detile reaches at around 60-90 GB/s on the same device.
public sealed partial class MetalImageTransferTests
{
    private const int ThroughputRounds = 5;

    private static readonly nint SelCopyBufferToTexture = MetalNative.Selector(
        "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:" +
        "toTexture:destinationSlice:destinationLevel:destinationOrigin:options:");

    private static readonly nint SelCopyTextureToBuffer = MetalNative.Selector(
        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:" +
        "toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");

    private static List<MetalTileTransfer> PlaneTransfers(uint bytes, uint pitch, uint width, uint height, ulong sliceSize) =>
    [
        new MetalTileTransfer
        {
            Kind = TileBlockKind.Depth64KB,
            BytesPerElement = bytes,
            LinearOffset = 0,
            LinearSize = sliceSize,
            TiledOffset = 0,
            TiledSize = sliceSize,
            Width = width,
            Height = height,
            Depth = 1,
            Pitch = pitch,
        },
    ];

    // The best of several submissions, each its own command buffer, after one warm-up
    // round that pays for the library, the pipeline and the staging slab's first touch.
    private void Measure(MetalTransferHarness harness, string name, ulong traffic, Action encode)
    {
        var best = double.MaxValue;
        for (var round = 0; round <= ThroughputRounds; round++)
        {
            harness.Begin();
            encode();
            var started = Stopwatch.GetTimestamp();
            harness.Submit();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (round != 0)
            {
                best = Math.Min(best, elapsed);
            }
        }

        output.WriteLine($"  {name,-28} {best * 1000,8:F2} ms  {traffic / best / 1e9,7:F2} GB/s");
    }

    [Fact]
    public void DepthStencilTransferThroughputIsBandwidthBound()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        const uint width = 3840;
        const uint height = 2160;
        var description = Depth(GuestDepthFormat.Z32Float, stencil: true, width, height, 1);
        var depthBytes = description.Data.Size;
        var stencilBytes = description.Stencil.Size;
        var depthPitch = description.Pitch;
        var stencilPitch = TileGeometry.DepthPitch(width, 1);
        output.WriteLine($"4K D32S8: depth=0x{depthBytes:x} ({depthBytes / 1048576.0:F1} MiB) pitch={depthPitch} " +
                         $"stencil=0x{stencilBytes:x} ({stencilBytes / 1048576.0:F1} MiB) pitch={stencilPitch}");

        var guestDepth = harness.Buffer(depthBytes);
        var guestStencil = harness.Buffer(stencilBytes);
        var linearDepth = harness.Buffer(depthBytes);
        var linearStencil = harness.Buffer(stencilBytes);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var depthTiles = PlaneTransfers(4, depthPitch, width, height, depthBytes);
        var stencilTiles = PlaneTransfers(1, stencilPitch, width, height, stencilBytes);
        var range = new SubresourceRange(0, 1, 0, 1);
        var depthRange = new MetalGuestRange(guestDepth, 0, depthBytes, 0);
        var stencilRange = new MetalGuestRange(guestStencil, 0, stencilBytes, 0);
        var extent = new MtlSize { Width = width, Height = height, Depth = 1 };

        // The whole transfer's traffic is the guest surface; its stages move it twice
        // whenever the plane has to land in a staging buffer on the way.
        output.WriteLine("whole transfers:");
        Measure(harness, "upload (guest->texture)", depthBytes + stencilBytes,
            () => harness.Transfer.EncodeGuestToTexture(harness, description, range, depthRange, stencilRange, texture));
        Measure(harness, "writeback (texture->guest)", depthBytes + stencilBytes,
            () => harness.Transfer.EncodeTextureToGuest(harness, description, range, texture, depthRange, stencilRange));

        output.WriteLine("stages:");
        Measure(harness, "detile depth (4B)", depthBytes * 2, () =>
        {
            var encoder = harness.Tiler.BeginCompute(harness);
            harness.Tiler.EncodeDetile(encoder, new MetalBufferSpan(guestDepth, 0, depthBytes),
                new MetalBufferSpan(linearDepth, 0, depthBytes), depthTiles);
            MetalTiler.EndEncoding(encoder);
        });
        Measure(harness, "detile stencil (1B)", stencilBytes * 2, () =>
        {
            var encoder = harness.Tiler.BeginCompute(harness);
            harness.Tiler.EncodeDetile(encoder, new MetalBufferSpan(guestStencil, 0, stencilBytes),
                new MetalBufferSpan(linearStencil, 0, stencilBytes), stencilTiles);
            MetalTiler.EndEncoding(encoder);
        });
        Measure(harness, "retile depth (4B)", depthBytes * 2, () =>
        {
            var encoder = harness.Tiler.BeginCompute(harness);
            harness.Tiler.EncodeTile(encoder, new MetalBufferSpan(linearDepth, 0, depthBytes),
                new MetalBufferSpan(guestDepth, 0, depthBytes), depthTiles);
            MetalTiler.EndEncoding(encoder);
        });
        Measure(harness, "retile stencil (1B)", stencilBytes * 2, () =>
        {
            var encoder = harness.Tiler.BeginCompute(harness);
            harness.Tiler.EncodeTile(encoder, new MetalBufferSpan(linearStencil, 0, stencilBytes),
                new MetalBufferSpan(guestStencil, 0, stencilBytes), stencilTiles);
            MetalTiler.EndEncoding(encoder);
        });
        Measure(harness, "blit buffer->depth", depthBytes * 2, () =>
        {
            var blit = harness.Tiler.BeginBlit(harness);
            MetalNative.SendCopyBufferToTextureOptions(blit, SelCopyBufferToTexture,
                linearDepth, 0, (nuint)((ulong)depthPitch * 4), (nuint)depthBytes, extent,
                texture, 0, 0, default, (nuint)MtlBlitOption.DepthFromDepthStencil);
            MetalTiler.EndEncoding(blit);
        });
        Measure(harness, "blit buffer->stencil", stencilBytes * 2, () =>
        {
            var blit = harness.Tiler.BeginBlit(harness);
            MetalNative.SendCopyBufferToTextureOptions(blit, SelCopyBufferToTexture,
                linearStencil, 0, stencilPitch, (nuint)stencilBytes, extent,
                texture, 0, 0, default, (nuint)MtlBlitOption.StencilFromDepthStencil);
            MetalTiler.EndEncoding(blit);
        });
        Measure(harness, "blit depth->buffer", depthBytes * 2, () =>
        {
            var blit = harness.Tiler.BeginBlit(harness);
            MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                texture, 0, 0, default, extent,
                linearDepth, 0, (nuint)((ulong)depthPitch * 4), (nuint)depthBytes,
                (nuint)MtlBlitOption.DepthFromDepthStencil);
            MetalTiler.EndEncoding(blit);
        });
        Measure(harness, "blit stencil->buffer", stencilBytes * 2, () =>
        {
            var blit = harness.Tiler.BeginBlit(harness);
            MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                texture, 0, 0, default, extent,
                linearStencil, 0, stencilPitch, (nuint)stencilBytes,
                (nuint)MtlBlitOption.StencilFromDepthStencil);
            MetalTiler.EndEncoding(blit);
        });

        // What a game's writeback actually finds: an attachment a render pass just wrote,
        // with the driver's depth compression metadata live.
        output.WriteLine("after a depth+stencil render pass on the texture:");
        Measure(harness, "clear render pass", 1, () => ClearDepthStencil(harness, texture));
        Measure(harness, "upload (guest->texture)", depthBytes + stencilBytes, () =>
        {
            ClearDepthStencil(harness, texture);
            harness.Transfer.EncodeGuestToTexture(harness, description, range, depthRange, stencilRange, texture);
        });
        Measure(harness, "writeback (texture->guest)", depthBytes + stencilBytes, () =>
        {
            ClearDepthStencil(harness, texture);
            harness.Transfer.EncodeTextureToGuest(harness, description, range, texture, depthRange, stencilRange);
        });

        output.WriteLine($"staging buffers={harness.Tiler.StagingBufferCount} bytes={harness.Tiler.StagingBytes}");
    }

    // A clear-only depth+stencil render pass, which is what leaves the attachment in the
    // state a game's writeback finds it in.
    private static void ClearDepthStencil(MetalTransferHarness harness, nint texture)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("renderPassDescriptor"));
        foreach (var (name, depth) in new[] { ("depthAttachment", true), ("stencilAttachment", false) })
        {
            var attachment = MetalNative.Send(descriptor, MetalNative.Selector(name));
            MetalNative.SendVoid(attachment, MetalNative.Selector("setTexture:"), texture);
            MetalNative.SendVoid(attachment, MetalNative.Selector("setLoadAction:"), (nint)Mtl.LoadActionClear);
            MetalNative.SendVoid(attachment, MetalNative.Selector("setStoreAction:"), (nint)Mtl.StoreActionStore);
            if (depth)
            {
                MetalNative.SendVoidDouble(attachment, MetalNative.Selector("setClearDepth:"), 0.5);
            }
            else
            {
                MetalV2Native.SendVoidValue(attachment, MetalNative.Selector("setClearStencil:"), 0x5a);
            }
        }

        var encoder = MetalNative.Send(harness.CommandBuffer,
            MetalNative.Selector("renderCommandEncoderWithDescriptor:"), descriptor);
        Assert.NotEqual(0, encoder);
        MetalTiler.EndEncoding(encoder);
    }

    // The 4K colour writeback for comparison: the same extent through the render-target
    // tiling the game's colour buffers use.
    [Fact]
    public void ColorTransferThroughputIsBandwidthBound()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm,
            3840, 2160, 1, 1, GuestTileMode.RenderTarget);
        var size = description.Data.Size;
        output.WriteLine($"4K RGBA8 render target: 0x{size:x} ({size / 1048576.0:F1} MiB) pitch={description.Pitch}");
        var guest = harness.Buffer(size);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var range = new SubresourceRange(0, 1, 0, 1);
        var guestRange = new MetalGuestRange(guest, 0, size, 0);
        Measure(harness, "upload (guest->texture)", size,
            () => harness.Transfer.EncodeGuestToTexture(harness, description, range, guestRange, default, texture));
        Measure(harness, "writeback (texture->guest)", size,
            () => harness.Transfer.EncodeTextureToGuest(harness, description, range, texture, guestRange, default));
    }
}
