// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// How the v2 presenter drives an HDR display, kept free of any Metal object so the
/// decisions can be tested. It mirrors the Vulkan presenter's scRGB path on macOS's
/// extended-dynamic-range model: the drawable is RGBA16Float in extended linear sRGB,
/// where 1.0 is SDR white and the screen's EDR headroom is the brightest value shown.
/// </summary>
internal static class MetalV2HdrOutput
{
    /// <summary>The colour space of the EDR drawable (CGColorSpace name).</summary>
    public const string ColorSpaceName = "kCGColorSpaceExtendedLinearSRGB";

    /// <summary>The EDR drawable's pixel format.</summary>
    public const ulong PixelFormat = MtlFormat.Rgba16Float;

    /// <summary>
    /// The luminance a PQ frame shows at SDR white. The Vulkan presenter writes PQ as
    /// scRGB, where 1.0 is 80 nits, and scales SDR frames by the display's SDR white
    /// level, which macOS reports as 1.0; 80 nits at 1.0 is therefore what the same
    /// guest buffer looks like there.
    /// </summary>
    public const float PqReferenceWhiteNits = 80f;

    /// <summary>Whether the screen can show anything brighter than SDR white: the macOS
    /// counterpart of the "display HDR enabled" state the Vulkan presenter reads.</summary>
    public static bool IsEdrDisplay(double potentialHeadroom) => potentialHeadroom > 1.0;

    /// <summary>
    /// What sceVideoOutGetOutputStatus reports. An EDR drawable is always available on
    /// Metal, so On always reports HDR and Auto reports it on an EDR-capable screen.
    /// </summary>
    public static bool GuestHdrSupported(HostVideoOptions options, double potentialHeadroom) =>
        options.CanUseHdr(IsEdrDisplay(potentialHeadroom), surfaceSupportsHdr: true);

    /// <summary>
    /// Whether the drawable should be the EDR one, as the Vulkan presenter picks its
    /// swapchain: always under On, under Auto only while the screen has EDR and the guest
    /// is flipping a PQ display buffer, so SDR titles keep the plain BGRA8 path.
    /// </summary>
    public static bool WantsEdrLayer(HostHdrMode mode, double potentialHeadroom, bool guestFlipsPq) => mode switch
    {
        HostHdrMode.On => true,
        HostHdrMode.Auto => IsEdrDisplay(potentialHeadroom) && guestFlipsPq,
        _ => false,
    };

    /// <summary>The fragment parameters for one presented frame.</summary>
    public static PresentHdrParameters Parameters(bool pqSource, double currentHeadroom) => new()
    {
        SdrScale = 1f,
        PqScale = 1f / PqReferenceWhiteNits,
        Headroom = (float)Math.Max(1.0, currentHeadroom),
        Pq = pqSource ? 1u : 0u,
    };

    /// <summary>Matches PresentHdrParameters in present_hdr_fragment.msl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PresentHdrParameters
    {
        public float SdrScale;
        public float PqScale;
        public float Headroom;
        public uint Pq;
    }
}
