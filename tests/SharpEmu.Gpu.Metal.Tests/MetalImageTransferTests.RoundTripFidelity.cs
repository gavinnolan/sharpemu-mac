// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// Round-trip fidelity for the single-copy texture plan (stage 1): a guest copy
// may only be dropped when detile -> texture -> retile gives the guest bytes back. Every guest byte of the image is random here - padding inside blocks,
// mip-tail slack and the gaps between layers included - so the comparison sees exactly
// which bytes the texture holds. Each case is retiled twice: once into a zeroed buffer,
// which is what a restore writes into, and once over a sentinel pattern, which must
// survive wherever the image has no element (a transfer must never clobber bytes it
// does not own, the levels a BASE_LEVEL-cut texture left out included).
public sealed partial class MetalImageTransferTests
{
    internal sealed record FidelityCase(string Name, ImageDescription Description, uint FirstLevel);

    internal enum FidelityShape
    {
        // An odd extent: padding inside every edge block.
        Odd,
        // A full mip chain, which ends in a mip tail on the 4 KiB and 64 KiB modes.
        Chain,
        // Layers and levels together.
        Array,
        // Exactly one block per layer, which leaves no padding anywhere.
        BlockFit,
        // A full chain held from level 2, as a BASE_LEVEL-cut texture is.
        BaseLevel,
        // A 3D texture with two levels.
        Volume,
        // Several blocks across and down, so the per-block address bits are exercised.
        Large,
    }

    private static readonly GuestTileMode[] FidelityTiles =
    [
        GuestTileMode.Linear, GuestTileMode.Standard256B, GuestTileMode.Standard4KB, GuestTileMode.Standard64KB,
        GuestTileMode.Prt, GuestTileMode.RenderTarget, GuestTileMode.Depth,
    ];

    // Every guest format the engine gives a host format that Metal carries and the tiler
    // has an element layout for, plus the two depth formats a shader samples as colour and
    // the BGRA16 display swap.
    private static IEnumerable<(GuestPixelFormat Guest, GpuFormat Host, bool Bgra16)> FidelityFormats()
    {
        foreach (var guest in Enum.GetValues<GuestPixelFormat>())
        {
            var host = GuestPixelFormats.HostFormat(guest);
            if (host == GpuFormat.Undefined || GuestPixelFormats.IsFmaskFormat(guest) ||
                !MetalGuestFormats.TryDecodeHostFormat(host, out _) || !TileGeometry.TryGetElementLayout(guest, out _))
            {
                continue;
            }

            yield return (guest, host, false);
        }

        yield return (GuestPixelFormat.Bits16_16_16_16Float, GpuFormat.R16G16B16A16Sfloat, true);
        yield return (GuestPixelFormat.Bits16UNorm, GpuFormat.D16Unorm, false);
        yield return (GuestPixelFormat.Bits32Float, GpuFormat.D32Sfloat, false);
    }

    internal static IEnumerable<FidelityCase> FidelityCases()
    {
        foreach (var (guest, host, bgra16) in FidelityFormats())
        {
            foreach (var tile in FidelityTiles)
            {
                foreach (var shape in Enum.GetValues<FidelityShape>())
                {
                    if (TryFidelityColor(guest, host, bgra16, tile, shape, out var description, out var firstLevel))
                    {
                        var suffix = bgra16 ? "+bgra16"
                            : host is GpuFormat.D16Unorm or GpuFormat.D32Sfloat ? $"as{host}" : string.Empty;
                        yield return new FidelityCase($"{guest}{suffix}/{tile}/{shape}", description, firstLevel);
                    }
                }
            }
        }

        foreach (var format in new[] { GuestDepthFormat.Z16, GuestDepthFormat.Z32Float })
        {
            foreach (var stencil in new[] { false, true })
            {
                foreach (var (width, height, layers) in new[] { (37u, 23u, 1u), (64u, 64u, 3u), (128u, 128u, 1u) })
                {
                    yield return new FidelityCase(
                        $"{format}{(stencil ? "+S8" : string.Empty)}/Depth/{width}x{height}x{layers}",
                        Depth(format, stencil, width, height, layers), 0);
                }
            }

            yield return new FidelityCase($"{format}/Linear/40x24x2", LinearDepth(format, 40, 24, 2), 0);
        }
    }

    // A colour (or colour-tiled depth) description of one shape, or false when the tile
    // mode has no layout for the format or the shape: a description the cache could never
    // build is not a case.
    private static bool TryFidelityColor(
        GuestPixelFormat guest, GpuFormat host, bool bgra16, GuestTileMode tile, FidelityShape shape,
        out ImageDescription description, out uint firstLevel)
    {
        description = default;
        firstLevel = 0;
        var depthHost = host is GpuFormat.D16Unorm or GpuFormat.D32Sfloat;
        var block = GuestPixelFormats.BlockCompressedBytes(guest) != 0;
        var rtTiled = tile is GuestTileMode.RenderTarget or GuestTileMode.Depth;
        var elementBytes = GuestPixelFormats.BytesPerElement(guest);
        if (rtTiled && (block || elementBytes is not (1 or 2 or 4 or 8 or 16)))
        {
            return false;
        }

        GuestPixelFormat transferFormat;
        try
        {
            transferFormat = rtTiled ? ImageDescription.RenderTargetTransferFormat(elementBytes) : guest;
        }
        catch (GpuFatal.AbandonedWorkException)
        {
            return false;
        }

        if (transferFormat == GuestPixelFormat.Invalid)
        {
            return false;
        }

        // A depth texture is one level of one plane; it has no chain to cut.
        if (depthHost && shape is FidelityShape.Chain or FidelityShape.BaseLevel or FidelityShape.Volume)
        {
            return false;
        }

        if (bgra16 && shape is FidelityShape.BaseLevel or FidelityShape.Volume)
        {
            return false;
        }

        if (tile == GuestTileMode.Depth && !depthHost)
        {
            return false;
        }

        var volume = shape == FidelityShape.Volume;
        if (volume && tile is not (GuestTileMode.Standard4KB or GuestTileMode.Standard64KB or GuestTileMode.Prt))
        {
            return false;
        }

        var texel = block ? 4u : 1u;
        uint width, height, depth = 1, levels = 1, layers = 1;
        switch (shape)
        {
            case FidelityShape.Odd:
                (width, height) = (37, 23);
                break;
            case FidelityShape.Chain:
                (width, height, levels) = (64, 64, 7);
                break;
            case FidelityShape.Array:
                (width, height, levels, layers) = (40, 24, depthHost ? 1u : 3u, 3);
                break;
            case FidelityShape.BlockFit:
                if (tile == GuestTileMode.Linear)
                {
                    (width, height, layers) = (256, 4, 2);
                    break;
                }

                if (!TileGeometry.TryGetTextureBlockLayout(transferFormat, tile, false, out var fit))
                {
                    return false;
                }

                (width, height, layers) = (fit.Block.BlockWidth * fit.TexelWidth, fit.Block.BlockHeight * fit.TexelHeight, 2);
                break;
            case FidelityShape.BaseLevel:
                (width, height, levels, layers) = (64, 64, 7, 2);
                firstLevel = 2;
                break;
            case FidelityShape.Volume:
                (width, height, depth, levels) = (16, 8, 6, 2);
                break;
            case FidelityShape.Large:
                (width, height, levels) = (520, 260, depthHost ? 1u : 4u);
                break;
            default:
                return false;
        }

        if (block && shape != FidelityShape.Odd && shape != FidelityShape.BlockFit)
        {
            width = Math.Max(width, 4 * texel);
            height = Math.Max(height, 4 * texel);
        }

        TileSizeAndAlignment size;
        try
        {
            size = TileGeometry.TextureTotalSize(transferFormat, width, height, volume ? depth : layers, levels, tile, volume);
        }
        catch (GpuFatal.AbandonedWorkException)
        {
            return false;
        }

        if (size.Size == 0)
        {
            return false;
        }

        description = ImageDescription.Create();
        description.Data = new GuestSpan(GuestAddress, size.Size);
        description.GuestFormat = guest;
        description.PixelFormat = host;
        description.Type = volume ? GuestImageType.Color3D : GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(width, height, volume ? depth : 1);
        description.Resources = new SubresourceCount(levels, volume ? 1 : layers);
        description.Pitch = TileGeometry.TexturePitch(transferFormat, width, tile);
        description.BytesPerBlock = block
            ? GuestPixelFormats.BlockCompressedBytes(guest)
            : GuestPixelFormats.BytesPerElement(guest);
        description.TileMode = tile;
        description.Bgra16 = bgra16;
        if (firstLevel != 0 && MetalImageCache.DesiredFirstLevel(description, firstLevel) != firstLevel)
        {
            return false;
        }

        return true;
    }

    // A linear depth plane: rows of the element at a 64-element pitch, one plane per layer.
    private static ImageDescription LinearDepth(GuestDepthFormat depthFormat, uint width, uint height, uint layers)
    {
        var rule = DepthFormatRule.Find(depthFormat)!;
        var pitch = (width + 63) / 64 * 64;
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(GuestAddress, (ulong)pitch * height * rule.BytesPerElement * layers);
        description.GuestFormat = rule.GuestFormat;
        description.PixelFormat = rule.AttachmentFormat(false);
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(width, height, 1);
        description.Resources = new SubresourceCount(1, layers);
        description.Pitch = pitch;
        description.BytesPerBlock = rule.BytesPerElement;
        description.TileMode = GuestTileMode.Linear;
        return description;
    }

    // The guest bytes the transfer's elements occupy, from the shared layout code alone:
    // the colour plan's tile transfers or subresource rows, or the depth and stencil plane
    // transfers. Only the levels a cut texture holds count.
    internal static (bool[] Data, bool[] Stencil) FidelityCoverage(in ImageDescription description, uint firstLevel)
    {
        var data = new bool[description.Data.Size];
        var stencil = new bool[description.HasStencil ? description.Stencil.Size : 0];
        if (description.IsDepth)
        {
            var layers = description.Resources.Layers;
            var slice = description.Data.Size / layers;
            if (description.IsTiled)
            {
                var tilerFormat = description.TileMode is GuestTileMode.RenderTarget or GuestTileMode.Depth
                    ? ImageDescription.RenderTargetTransferFormat(description.BytesPerBlock)
                    : description.GuestFormat;
                Assert.True(TileGeometry.TryGetTextureBlockLayout(tilerFormat, description.TileMode, false, out var texture));
                Assert.True(TileGeometry.TryGetTiledTextureLayout(new TiledSurfaceDescription(
                    tilerFormat, description.TileMode, TileSurfaceDimension.Flat2D,
                    description.Extent.Width, description.Extent.Height), out var surface));
                var thinRt = texture.Block.Kind is TileBlockKind.Depth64KB or TileBlockKind.RenderTarget64KB or
                    TileBlockKind.RenderTarget64KBGen5;
                for (uint layer = 0; layer < layers; layer++)
                {
                    Mark(data, new MetalTileTransfer
                    {
                        Kind = texture.Block.Kind,
                        BytesPerElement = description.BytesPerBlock,
                        LinearOffset = 0,
                        LinearSize = slice,
                        TiledOffset = slice * layer,
                        TiledSize = slice,
                        Width = description.Extent.Width,
                        Height = description.Extent.Height,
                        Depth = 1,
                        Pitch = description.Pitch,
                        SurfaceZ = thinRt ? layer : 0,
                        TiledWidth = surface.Mips[0].PaddedWidth,
                        TiledHeight = surface.Mips[0].PaddedHeight,
                    });
                }

                if (stencil.Length != 0)
                {
                    var stencilSlice = description.Stencil.Size / layers;
                    for (uint layer = 0; layer < layers; layer++)
                    {
                        Mark(stencil, new MetalTileTransfer
                        {
                            Kind = TileBlockKind.Depth64KB,
                            BytesPerElement = 1,
                            LinearOffset = 0,
                            LinearSize = stencilSlice,
                            TiledOffset = stencilSlice * layer,
                            TiledSize = stencilSlice,
                            Width = description.Extent.Width,
                            Height = description.Extent.Height,
                            Depth = 1,
                            Pitch = TileGeometry.DepthPitch(description.Extent.Width, 1),
                            SurfaceZ = layer,
                        });
                    }
                }
            }
            else
            {
                var rowBytes = (ulong)description.Extent.Width * description.BytesPerBlock;
                var pitchBytes = (ulong)description.Pitch * description.BytesPerBlock;
                for (uint layer = 0; layer < layers; layer++)
                {
                    for (uint row = 0; row < description.Extent.Height; row++)
                    {
                        Array.Fill(data, true, (int)(slice * layer + row * pitchBytes), (int)rowBytes);
                    }
                }
            }

            return (data, stencil);
        }

        var (_, copies, tiles, element) = BuildLayout(description);
        if (description.IsTiled)
        {
            foreach (var transfer in tiles)
            {
                if (transfer.Level >= firstLevel)
                {
                    Mark(data, transfer);
                }
            }

            return (data, stencil);
        }

        foreach (var copy in copies)
        {
            if (copy.Level < firstLevel)
            {
                continue;
            }

            var rowTexels = copy.BufferRowLength != 0 ? copy.BufferRowLength : copy.Width;
            var guestRow = (ulong)Math.Max((rowTexels + element.TexelWidth - 1) / element.TexelWidth, 1) * element.Bytes;
            var liveRow = (ulong)Math.Max((copy.Width + element.TexelWidth - 1) / element.TexelWidth, 1) * element.Bytes;
            var rows = Math.Max((copy.Height + element.TexelHeight - 1) / element.TexelHeight, 1);
            for (uint row = 0; row < rows; row++)
            {
                Array.Fill(data, true, (int)(copy.BufferOffset + row * guestRow), (int)liveRow);
            }
        }

        return (data, stencil);
    }

    // Every guest byte one transfer's elements land on. A transfer may reach past the
    // bytes the description claims (the last subresource owns its whole block grid);
    // those bytes are outside the image and are not counted.
    private static void Mark(bool[] covered, in MetalTileTransfer transfer)
    {
        var linearBytes = transfer.LinearOffset + Math.Max(transfer.LinearSize,
            (MetalTileOracle.SliceBytes(transfer) * transfer.Depth) + ((ulong)transfer.Pitch * transfer.BytesPerElement));
        var linear = new byte[linearBytes];
        Array.Fill(linear, (byte)0xff);
        var tiled = new byte[Math.Max((ulong)covered.Length, transfer.TiledOffset + transfer.TiledSize)];
        MetalTileOracle.Transfer(linear, tiled, transfer, toTiled: true);
        for (var index = 0; index < covered.Length; index++)
        {
            covered[index] |= tiled[index] != 0;
        }
    }

    internal enum FidelityOutcome
    {
        // Every byte of the run came back, for any content.
        Exact,
        // Every element byte came back; the run also has padding no element maps to,
        // which a restore can only write as zero.
        ExactWhereCovered,
        // An element byte came back different.
        Inexact,
        // A byte no element maps to was written.
        Clobbered,
        // The transfer refused the description: the cache cannot hold it either.
        Unsupported,
    }

    internal sealed record FidelityResult(
        FidelityCase Case,
        FidelityOutcome Outcome,
        ulong RunBytes,
        ulong PaddingBytes,
        ulong MismatchBytes,
        ulong ClobberBytes,
        string Detail)
    {
        public MetalRoundTripVerdict Verdict { get; init; }

        // What the release predicate must say for what the device measured.
        public bool PredicateAgrees => Outcome switch
        {
            FidelityOutcome.Exact => Verdict.Fidelity == MetalRoundTripFidelity.Exact,
            FidelityOutcome.ExactWhereCovered => Verdict.Fidelity == MetalRoundTripFidelity.ElementsExact,
            _ => Verdict.Fidelity == MetalRoundTripFidelity.None,
        };
    }

    private static FidelityResult RunFidelityCase(MetalTransferHarness harness, FidelityCase fidelity, uint seed)
    {
        var description = fidelity.Description;
        var firstLevel = fidelity.FirstLevel;
        var size = description.Data.Size;
        var stencilSize = description.HasStencil ? description.Stencil.Size : 0;
        var (start, end) = firstLevel == 0 ? (0UL, size) : harness.Transfer.LevelBytes(description, firstLevel);
        var (covered, stencilCovered) = FidelityCoverage(description, firstLevel);
        // One element per guest byte and every element somewhere: the oracle's coverage is
        // exactly the elements' bytes, so a byte it leaves out is padding and nothing else.
        var (dataElements, stencilElements) = MetalImageTransfer.ElementBytes(description, firstLevel);
        Assert.Equal(dataElements, (ulong)covered.Count(flag => flag));
        Assert.Equal(stencilElements, (ulong)stencilCovered.Count(flag => flag));

        // Slack past the image's end catches a transfer that writes beyond it.
        const ulong Slack = 0x1000;
        var guest = MetalTileOracle.Pattern((int)(size + Slack), seed);
        var stencilGuest = MetalTileOracle.Pattern((int)(stencilSize + Slack), seed + 1);
        var sentinel = MetalTileOracle.Pattern((int)(size + Slack), seed + 2);
        var stencilSentinel = MetalTileOracle.Pattern((int)(stencilSize + Slack), seed + 3);

        var texture = harness.Texture(harness.Transfer.ShapeFor(description, firstLevel));
        var verdict = harness.Transfer.ClassifyRoundTrip(description, firstLevel, texture);
        Assert.Equal((start, end), (verdict.RunStart, verdict.RunEnd));
        var range = new SubresourceRange(firstLevel, description.Resources.Levels - firstLevel, 0, description.Resources.Layers);
        var source = harness.Buffer(guest);
        var stencilSource = stencilSize != 0 ? harness.Buffer(stencilGuest) : 0;
        var stencilIn = stencilSource != 0 ? new MetalGuestRange(stencilSource, 0, stencilSize, 0) : default;
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(harness, description, range,
            new MetalGuestRange(source, start, end - start, 0), stencilIn, texture, start, end - start, firstLevel);
        harness.Submit();

        byte[] Retile(byte[]? fill, byte[]? stencilFill, out byte[] stencilOut)
        {
            var target = fill is null ? harness.Buffer(size + Slack) : harness.Buffer(fill);
            var stencilTarget = stencilSize == 0 ? 0
                : stencilFill is null ? harness.Buffer(stencilSize + Slack) : harness.Buffer(stencilFill);
            harness.Begin();
            harness.Transfer.EncodeTextureToGuest(harness, description, range, texture,
                new MetalGuestRange(target, start, end - start, 0),
                stencilTarget != 0 ? new MetalGuestRange(stencilTarget, 0, stencilSize, 0) : default,
                start, end - start, firstLevel);
            harness.Submit();
            stencilOut = stencilTarget != 0 ? harness.Read(stencilTarget, 0, stencilSize + Slack) : [];
            return harness.Read(target, 0, size + Slack);
        }

        var zeroed = Retile(null, null, out var zeroedStencil);
        var over = Retile(sentinel, stencilSentinel, out var overStencil);

        var detail = new StringBuilder();
        ulong mismatch = 0, clobber = 0, padding = 0;

        // The cache moves an image a run at a time, and a restore of part of an image would
        // cut it the same way: the run split where AlignRunEnd allows, uploaded and retiled
        // piece by piece, must give what the whole transfer gave.
        var mid = harness.Transfer.AlignRunEnd(description, start, start + ((end - start) / 2));
        if (stencilSize == 0 && mid > start && mid < end)
        {
            var second = harness.Transfer.AlignRunStart(description, mid);
            Assert.Equal(mid, second);
            var pieced = harness.Texture(harness.Transfer.ShapeFor(description, firstLevel));
            var target = harness.Buffer(size + Slack);
            harness.Begin();
            foreach (var (from, to) in new[] { (start, mid), (mid, end) })
            {
                harness.Transfer.EncodeGuestToTexture(harness, description, range,
                    new MetalGuestRange(source, from, to - from, 0), default, pieced, from, to - from, firstLevel);
            }

            harness.Submit();
            harness.Begin();
            foreach (var (from, to) in new[] { (start, mid), (mid, end) })
            {
                harness.Transfer.EncodeTextureToGuest(harness, description, range, pieced,
                    new MetalGuestRange(target, from, to - from, 0), default, from, to - from, firstLevel);
            }

            harness.Submit();
            var piecewise = harness.Read(target, 0, size + Slack);
            var differs = new bool[piecewise.Length];
            for (var index = 0; index < piecewise.Length; index++)
            {
                differs[index] = piecewise[index] != zeroed[index];
            }

            mismatch += Describe($"data split at 0x{mid:x}", "differs from the whole transfer", differs, detail);
        }
        Compare("data", description.Data.Size, start, end, covered, guest, zeroed, over, sentinel,
            ref mismatch, ref clobber, ref padding, detail);
        if (stencilSize != 0)
        {
            Compare("stencil", stencilSize, 0, stencilSize, stencilCovered, stencilGuest, zeroedStencil, overStencil,
                stencilSentinel, ref mismatch, ref clobber, ref padding, detail);
        }

        var outcome = clobber != 0 ? FidelityOutcome.Clobbered
            : mismatch != 0 ? FidelityOutcome.Inexact
            : padding != 0 ? FidelityOutcome.ExactWhereCovered
            : FidelityOutcome.Exact;
        return new FidelityResult(fidelity, outcome, end - start + stencilSize, padding, mismatch, clobber, detail.ToString())
        {
            Verdict = verdict,
        };
    }

    // Inside the run, an element byte must come back as the guest had it on both retiles,
    // and a padding byte must stay zero on the zeroed retile and keep the sentinel on the
    // other. Outside the run, and past the image's end, nothing may be written at all.
    private static void Compare(
        string plane, ulong size, ulong start, ulong end, bool[] covered, byte[] guest, byte[] zeroed, byte[] over,
        byte[] sentinel, ref ulong mismatch, ref ulong clobber, ref ulong padding, StringBuilder detail)
    {
        var bad = new bool[zeroed.Length];
        var stray = new bool[zeroed.Length];
        for (var index = 0; index < zeroed.Length; index++)
        {
            var inside = (ulong)index >= start && (ulong)index < end && (ulong)index < size;
            if (inside && covered[index])
            {
                bad[index] = zeroed[index] != guest[index] || over[index] != guest[index];
            }
            else
            {
                stray[index] = zeroed[index] != 0 || over[index] != sentinel[index];
                if (inside)
                {
                    padding++;
                }
            }
        }

        mismatch += Describe(plane, "differs", bad, detail);
        clobber += Describe(plane, "clobbered", stray, detail);
    }

    private static ulong Describe(string plane, string what, bool[] flags, StringBuilder detail)
    {
        ulong count = 0;
        var ranges = new List<(int Start, int End)>();
        for (var index = 0; index < flags.Length; index++)
        {
            if (!flags[index])
            {
                continue;
            }

            count++;
            if (ranges.Count != 0 && ranges[^1].End == index)
            {
                ranges[^1] = (ranges[^1].Start, index + 1);
            }
            else
            {
                ranges.Add((index, index + 1));
            }
        }

        if (count != 0)
        {
            detail.Append($" {plane} {what}: {count} bytes in {ranges.Count} ranges [");
            detail.Append(string.Join(", ", ranges.Take(6).Select(range => $"0x{range.Start:x}..0x{range.End:x}")));
            detail.Append(ranges.Count > 6 ? ", ...]" : "]");
        }

        return count;
    }

    // GpuFatal ends the process; on this thread it throws instead, so a description the
    // transfer refuses is reported as such rather than taking the run down.
    private sealed class ThrowingFatals : IDisposable
    {
        private static readonly System.Reflection.FieldInfo Field = typeof(GpuFatal).GetField(
            "ThrowOnFatal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        public ThrowingFatals() => Field.SetValue(null, true);

        public void Dispose() => Field.SetValue(null, false);
    }

    [Fact]
    public void EveryTransferableSurfaceRoundTripsItsElementBytesExactly()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        using var fatals = new ThrowingFatals();
        var results = new List<FidelityResult>();
        uint seed = 9001;
        foreach (var fidelity in FidelityCases())
        {
            FidelityResult result;
            try
            {
                result = RunFidelityCase(harness, fidelity, seed);
            }
            catch (GpuFatal.AbandonedWorkException exception)
            {
                result = new FidelityResult(fidelity, FidelityOutcome.Unsupported, 0, 0, 0, 0, $" {exception.Message}");
            }

            seed += 17;
            results.Add(result);
        }

        foreach (var result in results)
        {
            output.WriteLine(
                $"{result.Case.Name}: {result.Outcome} run=0x{result.RunBytes:x} padding=0x{result.PaddingBytes:x} " +
                $"predicate={result.Verdict.Fidelity}" + result.Detail);
        }

        var summary = results.GroupBy(result => result.Outcome)
            .Select(group => $"{group.Key}={group.Count()}");
        output.WriteLine($"{results.Count} cases: {string.Join(" ", summary)}");

        var failures = results.Where(result => result.Outcome is FidelityOutcome.Inexact or FidelityOutcome.Clobbered)
            .Select(result => $"{result.Case.Name}: {result.Outcome}{result.Detail}")
            .Concat(results.Where(result => result.Outcome != FidelityOutcome.Unsupported && !result.PredicateAgrees)
                .Select(result => $"{result.Case.Name}: measured {result.Outcome} but the predicate says " +
                    $"{result.Verdict.Fidelity} ({result.Verdict.Reason})"))
            .ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // The images whose texture is not a copy of their guest bytes, whatever the matrix says
    // about the bytes themselves: their guest copy can never be released.
    [Fact]
    public void ImagesTheTextureDoesNotHoldExactlyAreNeverReleaseEligible()
    {
        using var transfer = new MetalImageTransfer(CreateHeadlessDevice());
        var plain = Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 128, 1, 1, GuestTileMode.Standard64KB);
        Assert.Equal(MetalRoundTripFidelity.Exact, transfer.ClassifyRoundTrip(plain).Fidelity);
        Assert.True(transfer.IsRoundTripExact(plain));

        var dcc = plain;
        dcc.Metadata = new MetadataDescription { Kind = MetadataKind.Dcc, Range = new GuestSpan(GuestAddress + 0x100000, 0x400) };
        var pendingDcc = plain;
        pendingDcc.Metadata = new MetadataDescription { Kind = MetadataKind.Dcc };
        var htile = Depth(GuestDepthFormat.Z32Float, stencil: false, 128, 64, 1);
        htile.Metadata = new MetadataDescription { Kind = MetadataKind.Htile, Range = new GuestSpan(GuestAddress + 0x200000, 0x800) };
        var msaa = plain;
        msaa.Samples = 4;
        var substituted = plain;
        substituted.PixelFormat = GpuFormat.B8G8R8A8Unorm;
        var chain = Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 64, 64, 7, 1, GuestTileMode.Standard64KB);
        var depth = Depth(GuestDepthFormat.Z16, stencil: true, 128, 64, 1);
        foreach (var (name, description, firstLevel) in new[]
                 {
                     ("dcc", dcc, 0u), ("pending dcc", pendingDcc, 0u), ("htile", htile, 0u), ("msaa", msaa, 0u),
                     ("substituted format", substituted, 0u), ("level past the chain", chain, 7u), ("cut depth", depth, 1u),
                 })
        {
            var verdict = transfer.ClassifyRoundTrip(description, firstLevel);
            output.WriteLine($"{name}: {verdict.Fidelity} ({verdict.Reason})");
            Assert.Equal(MetalRoundTripFidelity.None, verdict.Fidelity);
            Assert.False(transfer.IsRoundTripExact(description, firstLevel));
        }

        // Padding, and a cut texture's left-out levels, are bytes a restore cannot write back.
        Assert.Equal(MetalRoundTripFidelity.ElementsExact, transfer.ClassifyRoundTrip(chain).Fidelity);
        Assert.Equal(MetalRoundTripFidelity.ElementsExact, transfer.ClassifyRoundTrip(chain, 2).Fidelity);
        Assert.Equal(MetalRoundTripFidelity.ElementsExact, transfer.ClassifyRoundTrip(depth).Fidelity);
    }

    // A lossily compressed texture has no exact texels to retile.
    [Fact]
    public void ALossilyCompressedTextureIsNeverReleaseEligible()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 128, 1, 1, GuestTileMode.Standard64KB);
        var shape = harness.Transfer.ShapeFor(description);
        var plain = harness.Texture(shape);
        Assert.True(harness.Transfer.IsRoundTripExact(description, 0, plain));

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLTextureDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setTextureType:"), (nint)shape.TextureType);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setPixelFormat:"), (nint)shape.PixelFormat);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setWidth:"), (nint)shape.Width);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setHeight:"), (nint)shape.Height);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setStorageMode:"), 2);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setUsage:"), (nint)MtlTextureUsage.ShaderRead);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setCompressionType:"), 1);
        var lossy = MetalNative.Send(Device.Value, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
        if (lossy == 0)
        {
            output.WriteLine("this device makes no lossily compressed texture");
            return;
        }

        try
        {
            var verdict = harness.Transfer.ClassifyRoundTrip(description, 0, lossy);
            output.WriteLine($"lossy: {verdict.Fidelity} ({verdict.Reason})");
            if (MetalV2Native.SendValue(lossy, MetalNative.Selector("compressionType")) == 1)
            {
                Assert.Equal(MetalRoundTripFidelity.None, verdict.Fidelity);
            }
        }
        finally
        {
            MetalNative.SendVoid(lossy, MetalNative.Selector("release"));
        }
    }
}
