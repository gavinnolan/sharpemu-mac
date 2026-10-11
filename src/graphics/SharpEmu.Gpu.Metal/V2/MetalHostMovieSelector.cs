// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Which guest textures of a draw or dispatch a host-decoded movie frame replaces.
///
/// A title that plays its Bink movies through the host bridge still runs its own
/// YUV-to-RGB shader over the decoder's output surfaces; the guest decoder never
/// fills them, so the host frame's luma and chroma planes are bound in their place.
/// The rules are the Vulkan presenter's (<c>VulkanVideoPresenter.HostMovie</c>):
/// a full-size R8 unorm luma plane at the frame's aspect, paired with an R8G8 unorm
/// chroma plane of half its extent; the pair found is remembered, and rediscovered
/// when the title flips to another surface pair. Device free, so it is testable.
/// </summary>
internal sealed class MetalHostMovieSelector
{
    // Guest data formats: 1 is 8, 3 is 8_8; number type 0 is unorm.
    private const uint FormatR8 = 1;
    private const uint FormatR8G8 = 3;
    private const uint NumberTypeUnorm = 0;

    private readonly HashSet<string> _tracedBindings = new(StringComparer.Ordinal);
    private ulong _lumaAddress;
    private ulong _chromaAddress;

    public readonly record struct Bindings(int Luma, int Chroma)
    {
        public static Bindings None { get; } = new(-1, -1);

        public bool Found => Luma >= 0 && Chroma >= 0;
    }

    /// <summary>The newest decoded frame: the luma plane, then the interleaved chroma plane.</summary>
    public byte[]? FramePixels { get; private set; }

    public uint FrameWidth { get; private set; }

    public uint FrameHeight { get; private set; }

    public long FrameSerial { get; private set; }

    public string? FramePath { get; private set; }

    /// <summary>Raised when the movie changes, so planes uploaded for the last one are not reused.</summary>
    public long PathEpoch { get; private set; }

    /// <summary>The destination selects of the pair last bound; the plane views follow them.</summary>
    public uint LumaDstSelect { get; private set; }

    public uint ChromaDstSelect { get; private set; }

    public bool HasFrame => FramePixels is not null && FrameWidth != 0 && FrameHeight != 0;

    /// <summary>
    /// Takes the bridge's answer for this tick. A failed decode keeps the last frame:
    /// movie sessions are queued asynchronously, and dropping it would expose the
    /// guest decoder's neutral surfaces between one movie's last frame and the next
    /// movie's first.
    /// </summary>
    public void Accept(
        bool decoded,
        byte[] pixels,
        uint width,
        uint height,
        bool advanced,
        long frameSerial,
        string hostPath)
    {
        if (!decoded)
        {
            return;
        }

        if (!string.Equals(FramePath, hostPath, StringComparison.OrdinalIgnoreCase))
        {
            FramePath = hostPath;
            _lumaAddress = 0;
            _chromaAddress = 0;
            LumaDstSelect = 0;
            ChromaDstSelect = 0;
            PathEpoch++;
        }

        if (!advanced && FramePixels is not null)
        {
            return;
        }

        FramePixels = pixels;
        FrameWidth = width;
        FrameHeight = height;
        FrameSerial = frameSerial;
    }

    /// <summary>The luma and chroma indices among <paramref name="textures"/>, or none.</summary>
    public Bindings Find(IReadOnlyList<GuestDrawTexture> textures)
    {
        if (!HasFrame)
        {
            return Bindings.None;
        }

        if (_lumaAddress != 0 && _chromaAddress != 0)
        {
            var remembered = SelectRemembered(textures, _lumaAddress, _chromaAddress, FrameWidth, FrameHeight);
            if (remembered.Found)
            {
                LumaDstSelect = textures[remembered.Luma].DstSelect;
                ChromaDstSelect = textures[remembered.Chroma].DstSelect;
                return remembered;
            }

            // Bluepoint alternates decoder output between several Y/UV surface
            // pairs. Fall through and discover the active pair instead of sampling
            // the stale guest surface on every other movie draw.
        }

        var bestLuma = -1;
        var bestChroma = -1;
        ulong bestArea = 0;
        for (var lumaIndex = 0; lumaIndex < textures.Count; lumaIndex++)
        {
            var luma = textures[lumaIndex];
            if (!IsLumaCandidateForFrame(luma, FrameWidth, FrameHeight))
            {
                continue;
            }

            for (var chromaIndex = 0; chromaIndex < textures.Count; chromaIndex++)
            {
                if (!IsChromaCandidateForLuma(luma, textures[chromaIndex]))
                {
                    continue;
                }

                var area = (ulong)luma.Width * luma.Height;
                if (bestLuma < 0 || area > bestArea)
                {
                    bestLuma = lumaIndex;
                    bestChroma = chromaIndex;
                    bestArea = area;
                }
            }
        }

        if (bestLuma < 0 || bestChroma < 0)
        {
            return Bindings.None;
        }

        var lumaTexture = textures[bestLuma];
        var chromaTexture = textures[bestChroma];
        _lumaAddress = lumaTexture.Address;
        _chromaAddress = chromaTexture.Address;
        LumaDstSelect = lumaTexture.DstSelect;
        ChromaDstSelect = chromaTexture.DstSelect;
        if (_tracedBindings.Add($"{FramePath}|{lumaTexture.Address:X16}|{chromaTexture.Address:X16}"))
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Bink2 YUV textures bound: " +
                $"{Path.GetFileName(FramePath)} " +
                $"y={bestLuma}:0x{lumaTexture.Address:X16}:" +
                $"{lumaTexture.Width}x{lumaTexture.Height}:dst=0x{lumaTexture.DstSelect:X} " +
                $"uv={bestChroma}:0x{chromaTexture.Address:X16}:" +
                $"{chromaTexture.Width}x{chromaTexture.Height}:dst=0x{chromaTexture.DstSelect:X} " +
                $"host={FrameWidth}x{FrameHeight}.");
        }

        return new Bindings(bestLuma, bestChroma);
    }

    /// <summary>The remembered pair, when this draw still binds both of its surfaces.</summary>
    internal static Bindings SelectRemembered(
        IReadOnlyList<GuestDrawTexture> textures,
        ulong lumaAddress,
        ulong chromaAddress,
        uint hostWidth,
        uint hostHeight)
    {
        var lumaIndex = -1;
        ulong lumaArea = 0;
        for (var index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            if (texture.Address != lumaAddress || !IsLumaCandidateForFrame(texture, hostWidth, hostHeight))
            {
                continue;
            }

            var area = (ulong)texture.Width * texture.Height;
            if (lumaIndex < 0 || area > lumaArea)
            {
                lumaIndex = index;
                lumaArea = area;
            }
        }

        if (lumaIndex < 0)
        {
            return Bindings.None;
        }

        var luma = textures[lumaIndex];
        for (var index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            if (texture.Address == chromaAddress && IsChromaCandidateForLuma(luma, texture))
            {
                return new Bindings(lumaIndex, index);
            }
        }

        return Bindings.None;
    }

    private static bool IsLumaCandidateForFrame(GuestDrawTexture texture, uint hostWidth, uint hostHeight)
    {
        if (texture.Address == 0 ||
            texture.IsStorage ||
            texture.IsFallback ||
            texture.ArrayedView ||
            texture.ArrayLayers > 1 ||
            texture.Format != FormatR8 ||
            texture.NumberType != NumberTypeUnorm ||
            texture.Width < 1280 ||
            texture.Height < 720)
        {
            return false;
        }

        // Within two per cent of the decoded frame's aspect.
        var guestAspect = (ulong)texture.Width * hostHeight;
        var hostAspect = (ulong)texture.Height * hostWidth;
        var difference = guestAspect > hostAspect ? guestAspect - hostAspect : hostAspect - guestAspect;
        return difference * 100 <= Math.Max(guestAspect, hostAspect) * 2;
    }

    private static bool IsChromaCandidateForLuma(GuestDrawTexture luma, GuestDrawTexture chroma) =>
        chroma.Address != 0 &&
        chroma.Address != luma.Address &&
        !chroma.IsStorage &&
        !chroma.IsFallback &&
        !chroma.ArrayedView &&
        chroma.ArrayLayers <= 1 &&
        chroma.Format == FormatR8G8 &&
        chroma.NumberType == NumberTypeUnorm &&
        luma.Width == chroma.Width * 2 &&
        luma.Height == chroma.Height * 2;
}
