// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Images;

public readonly record struct RenderTargetFormatInfo(GpuFormat HostFormat, uint BytesPerElement, ColorComponentMap ExportMapping);

public readonly record struct SurfaceFormatInfo(GpuFormat HostFormat, GuestPixelFormat ConversionFormat);

/// <summary>
/// Resolves a guest render-target or texture format description to the neutral
/// <see cref="GpuFormat"/> the engine binds, with the component remapping that makes a
/// guest channel order come out right.
///
/// It is guest-format semantics rather than an API mapping - every backend has to honour the
/// same guest channel order - so it stays in the shared engine while the staging/tiling
/// layout that consumes it belongs to the graphics backend.
/// </summary>
public static class GuestSurfaceFormats
{
    private static readonly ColorComponentMap[][] RenderTargetColorMappings =
    [
        [ColorComponentMap.Identity, ColorComponentMap.Identity, ColorComponentMap.Identity, ColorComponentMap.Identity],
        [ColorComponentMap.Gr, ColorComponentMap.Rabg, ColorComponentMap.Rgab, ColorComponentMap.Bgra],
        [ColorComponentMap.Bgra, ColorComponentMap.Gr, ColorComponentMap.Bgra, ColorComponentMap.Abgr],
        [ColorComponentMap.Agba, ColorComponentMap.Arbg, ColorComponentMap.Agbr, ColorComponentMap.Argb],
    ];

    private static (GpuFormat Format, ColorComponentMap HostToStorage) RenderTargetHostFormat(GuestPixelFormat guestFormat, ChannelOrder order)
    {
        if (order == ChannelOrder.Alternate)
        {
            switch (guestFormat)
            {
                case GuestPixelFormat.Bits8_8_8_8UNorm: return (GpuFormat.B8G8R8A8Unorm, ColorComponentMap.Bgra);
                case GuestPixelFormat.Bits8_8_8_8SNorm: return (GpuFormat.B8G8R8A8SNorm, ColorComponentMap.Bgra);
                case GuestPixelFormat.Bits8_8_8_8Srgb: return (GpuFormat.B8G8R8A8Srgb, ColorComponentMap.Bgra);
                case GuestPixelFormat.Bits10_10_10_2UNorm: return (GpuFormat.A2R10G10B10UnormPack32, ColorComponentMap.Bgra);
            }
        }

        return guestFormat switch
        {
            GuestPixelFormat.Bits5_5_5_1UNorm => (GpuFormat.A1R5G5B5UnormPack16, ColorComponentMap.Bgra),
            GuestPixelFormat.Bits1_5_5_5UNorm => (GpuFormat.R5G5B5A1UnormPack16, ColorComponentMap.Abgr),
            GuestPixelFormat.Bits4_4_4_4UNorm => (GpuFormat.R4G4B4A4UnormPack16, ColorComponentMap.Abgr),
            _ => (GuestPixelFormats.HostFormat(guestFormat), ColorComponentMap.Identity),
        };
    }

    public static RenderTargetFormatInfo RenderTargetFormat(ChannelLayout layout, ChannelType type, ChannelOrder order)
    {
        var encoding = GuestPixelFormats.ResolveRenderTargetEncoding(layout, type);
        if (encoding.IsValid && encoding.SupportsOrder(order))
        {
            var host = RenderTargetHostFormat(encoding.Format, order);
            var bytes = GuestPixelFormats.RenderTargetBytesPerElement(encoding.Format);
            if (host.Format != GpuFormat.Undefined && bytes != 0)
            {
                var orderMapping = RenderTargetColorMappings[(int)order][encoding.Components - 1];
                return new RenderTargetFormatInfo(host.Format, bytes, host.HostToStorage.Then(orderMapping));
            }
        }

        throw GpuFatal.Fatal($"The render-target format combination is not supported: layout={(uint)layout} type={(uint)type} order={(uint)order}.");
    }

    public static SurfaceFormatInfo SurfaceFormat(GuestPixelFormat format)
    {
        var backingFormat = GuestPixelFormats.RemapTextureFormat(format);
        var hostFormat = GuestPixelFormats.HostFormat(backingFormat);
        if (hostFormat == GpuFormat.Undefined)
        {
            throw GpuFatal.Fatal($"The guest texture format has no host format: format={(uint)format}.");
        }

        return new SurfaceFormatInfo(hostFormat, backingFormat != format ? format : GuestPixelFormat.Invalid);
    }

}
