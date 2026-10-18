// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Per-title display-size patches, chosen by <see cref="EnvironmentVariable"/>:
/// <list type="bullet">
/// <item>unset or <c>1</c>: rewrite every compiled 3840x2160 size to the display while the
/// title stays in its 4K mode. The fastest, but Demon's Souls' UI keeps its 4K layout on the
/// shrunken canvas, so the HUD comes out 2x (1080p) or 3x (720p) and its item cells break.</item>
/// <item><c>mode</c>: run the title in its own HD display mode, held there
/// (<see cref="DisplayModeProfile"/>). Its UI canvas, layout and UI coordinates stay native HD;
/// below 1080p only the HD mode's render targets shrink to the display.</item>
/// <item><c>0</c>: the title's own sizes, as a PS5 runs it: Demon's Souls outputs 4K
/// whatever the television is and the system scales it.</item>
/// </list>
/// Every site is verified against the bytes the known build carries before anything is
/// written; a different build is left untouched.
/// </summary>
internal static class TitleDisplayPatches
{
    public const string EnvironmentVariable = "SHARPEMU_TITLE_DISPLAY_PATCH";

    /// <summary>What a site encodes. 32-bit widths and heights are little-endian integers;
    /// a <see cref="Slot.WidthHeight"/> site packs width in the low and height in the high
    /// dword of one 64-bit immediate; the float slots are IEEE singles.</summary>
    internal enum Slot
    {
        Width,
        Height,
        WidthHeight,
        WidthF32,
        HeightF32,
    }

    internal enum Role
    {
        /// <summary>The output buffer, display size or UI canvas: becomes the display size.</summary>
        Output,

        /// <summary>The internal render size: never grows, shrinks to the display size.</summary>
        Internal,
    }

    internal readonly record struct Site(ulong Offset, Slot Slot, uint OldWidth, uint OldHeight, Role Role);

    internal enum Strategy
    {
        /// <summary>The title's own sizes.</summary>
        Off,

        /// <summary>Every compiled 4K size rewritten to the display; the title stays in its 4K mode.</summary>
        AllSites,

        /// <summary>The title's own HD display mode; see <see cref="DisplayModeProfile"/>.</summary>
        DisplayMode,
    }

    /// <summary>A code patch that is not a size: <see cref="New"/> replaces <see cref="Old"/>.</summary>
    internal readonly record struct CodeSite(ulong Offset, byte[] Old, byte[] New);

    /// <summary>A title that picks its display mode as "override, else a compiled default".
    /// Each <see cref="DefaultModeSites"/> offset is the imm32 of a <c>mov r32, OldDefaultMode</c>
    /// that becomes <see cref="HdMode"/>; <see cref="OverrideSites"/> drop the overrides (a saved
    /// visual-mode option among them) so the mode holds. Below the HD size,
    /// <see cref="HdOutputSites"/> (the HD output and display-buffer sizes) become the display
    /// size and <see cref="HdInternalSites"/> shrink to it.</summary>
    internal sealed record DisplayModeProfile(
        IReadOnlyList<ulong> DefaultModeSites,
        uint OldDefaultMode,
        uint HdMode,
        IReadOnlyList<CodeSite> OverrideSites,
        uint HdWidth,
        uint HdHeight,
        IReadOnlyList<Site> HdOutputSites,
        IReadOnlyList<Site> HdInternalSites)
    {
        /// <summary>The mode for the display; null keeps the title's own 4K mode.</summary>
        public uint? ModeFor(uint width, uint height) =>
            width >= 3840 || height >= 2160 ? null : HdMode;
    }

    // Unset keeps the fast rewrite of every site until the display mode has been checked in a run.
    internal static Strategy SelectedStrategy =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) switch
        {
            "0" => Strategy.Off,
            "mode" => Strategy.DisplayMode,
            _ => Strategy.AllSites,
        };

    // Demon's Souls (PPSA01342, v01.004.000). The video-out setup creates its "DisplayBuffer"
    // textures and registers them at 3840x2160 (immediates at the call sites and in the two
    // display-size getters), the render configs in read-only data carry the output and
    // display size, the mode tables (index 2 performance, 3 cinematic) carry the display and
    // internal sizes, and the 4K entries of the UI canvas tables (0x2172C04 / 0x2172A0C) and
    // the 3840.0 / 2160.0 literals (0x1135E17, 0x1FF9C60, 0x1FFEA50, 0x1FFE588) normalise
    // positions authored in 4K pixels. The title stays in mode 3, so its UI keeps the 4K layout.
    private static readonly Site[] DemonsSouls =
    [
        new(0x94535C, Slot.Width, 3840, 2160, Role.Output),
        new(0x945361, Slot.Height, 3840, 2160, Role.Output),
        new(0x94542E, Slot.Width, 3840, 2160, Role.Output),
        new(0x945433, Slot.Height, 3840, 2160, Role.Output),
        new(0x944A48, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x945004, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x945579, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x9E7F88, Slot.Width, 3840, 2160, Role.Output),
        new(0x9E7FB8, Slot.Height, 3840, 2160, Role.Output),
        new(0x20010AC, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20010B4, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20013EC, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20013F4, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20010A4, Slot.Width, 2560, 1440, Role.Internal),
        new(0x20010A8, Slot.Height, 2560, 1440, Role.Internal),
        new(0x20013E4, Slot.WidthHeight, 3840, 2160, Role.Internal),
        new(0x1FFA5D8, Slot.Width, 3840, 2160, Role.Output),
        new(0x1FFA5DC, Slot.Width, 3840, 2160, Role.Output),
        new(0x1FFBE38, Slot.Height, 3840, 2160, Role.Output),
        new(0x1FFBE3C, Slot.Height, 3840, 2160, Role.Output),
        new(0x1FFBE28, Slot.Width, 2560, 1440, Role.Internal),
        new(0x1FFBE2C, Slot.Width, 3840, 2160, Role.Internal),
        new(0x1FFD758, Slot.Height, 2560, 1440, Role.Internal),
        new(0x1FFD75C, Slot.Height, 3840, 2160, Role.Internal),
        new(0x1135E17, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x1135E1B, Slot.HeightF32, 3840, 2160, Role.Output),
        new(0x1FF9C60, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x1FF9C64, Slot.HeightF32, 3840, 2160, Role.Output),
        new(0x1FFEA50, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x1FFE588, Slot.HeightF32, 3840, 2160, Role.Output),
        new(0x2172C04, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x2172A0C, Slot.HeightF32, 3840, 2160, Role.Output),
    ];

    // Demon's Souls display modes: 1 and 2 output 1920x1080 (internal 1600x900, 1920x1080),
    // 3 and 4 output 3840x2160 (internal 2560x1440, 3840x2160); tables at 0x1FFA5D0 / 0x1FFBE30
    // (output width / height) and 0x1FFBE20 / 0x1FFD750 (internal width / height). The mode is
    // r_Wants4K, else r_Wants4K_Game, else a compiled 3, worked out in three places: the
    // video-out mode set (0xA63E40), the renderer (0xBC8055) and the UI (0xEB436B). Every 4K
    // decision keys off mode 3 or 4: the registered display buffers (0x9449B0), the
    // display-size getters (0x9E7F80 / 0x9E7FB0), the window margins (0x945745), the UI canvas
    // ({1920,3840} x {1080,2160} at 0x2172C00 / 0x2172A08, read at 0xEB4022 and 0xECBCCE) and
    // the UI's 4K layout switch (0xEB43B9). A saved visual mode (r_Wants4K_Game 3 or 4) takes
    // over from the default once the save loads, so the two cmovne at each site go.
    //
    // Below 1080p the HD mode's render targets shrink: the output size (table index 1) and the
    // 1920x1080 views of the display buffers (0x9453E1 / 0x94547A, whose sizes 0x944CB7 hands
    // to sceVideoOutSetBufferAttribute2 in the HD mode) become the display size, and the
    // internal size shrinks to it. The UI keeps its native HD units: its canvas tables and the
    // display-size getters, which the UI uses for safe-area coordinates (0xC29B56, 0xC3DE66,
    // 0xC43570), stay 1920x1080. The UI maps its canvas onto the target rather than assuming
    // canvas pixels are target pixels: a 1920x1080 canvas on a 4K target is a state it handles
    // (the 0.5 / 2.0 canvas scale at 0xEB43DA).
    private static readonly byte[] Nop3 = [0x0F, 0x1F, 0x00];

    private static readonly DisplayModeProfile DemonsSoulsModes = new(
        [0xA63E41, 0xBC8056, 0xEB436C],
        OldDefaultMode: 3,
        HdMode: 2,
        [
            new(0xA63E47, [0x0F, 0x45, 0xF1], Nop3),
            new(0xA63E4C, [0x0F, 0x45, 0xF0], Nop3),
            new(0xBC806F, [0x0F, 0x45, 0xF1], Nop3),
            new(0xBC8074, [0x0F, 0x45, 0xF0], Nop3),
            new(0xEB4372, [0x0F, 0x45, 0xC1], Nop3),
            new(0xEB437A, [0x0F, 0x45, 0xC3], Nop3),
        ],
        HdWidth: 1920,
        HdHeight: 1080,
        [
            new(0x1FFA5D4, Slot.Width, 1920, 1080, Role.Output),
            new(0x1FFBE34, Slot.Height, 1920, 1080, Role.Output),
            new(0x9453E2, Slot.Width, 1920, 1080, Role.Output),
            new(0x9453E7, Slot.Height, 1920, 1080, Role.Output),
            new(0x94547B, Slot.Width, 1920, 1080, Role.Output),
            new(0x945480, Slot.Height, 1920, 1080, Role.Output),
        ],
        [
            new(0x1FFBE24, Slot.Width, 1920, 1080, Role.Internal),
            new(0x1FFD754, Slot.Height, 1920, 1080, Role.Internal),
        ]);

    internal static DisplayModeProfile? DisplayModeFor(string? titleId) =>
        titleId switch
        {
            "PPSA01342" => DemonsSoulsModes,
            _ => null,
        };

    internal static IReadOnlyList<Site>? SitesFor(string? titleId) =>
        titleId switch
        {
            "PPSA01342" => DemonsSouls,
            _ => null,
        };

    /// <summary>Rewrites the title's compiled display sizes to <paramref name="width"/> x
    /// <paramref name="height"/>. Returns true when the sites were rewritten.</summary>
    public static bool Apply(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        string? titleId,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(programHeaders);
        var strategy = SelectedStrategy;
        if (strategy == Strategy.Off || width == 0 || height == 0)
        {
            return false;
        }

        List<GuestBytePatcher.Patch>? patches;
        if (strategy == Strategy.DisplayMode)
        {
            var profile = DisplayModeFor(titleId);
            patches = profile is null ? null : BuildDisplayModePatches(profile, width, height);
        }
        else
        {
            var sites = SitesFor(titleId);
            patches = sites is null ? null : BuildPatches(sites, width, height);
        }

        if (patches is null || patches.Count == 0)
        {
            return false;
        }

        // A build other than the one the sites were taken from keeps every byte: verify all
        // of them before the patcher (which verifies again) writes any.
        var current = new byte[8];
        foreach (var patch in patches)
        {
            var span = current.AsSpan(0, patch.New.Length);
            if (!memory.TryRead(imageBase + patch.Offset, span) || !span.SequenceEqual(patch.Old))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] {titleId}: display size patch skipped, the executable does not carry the " +
                    $"expected bytes at 0x{patch.Offset:X} (a different build?).");
                return false;
            }
        }

        GuestBytePatcher.Apply(memory, programHeaders, imageBase, patches);
        Console.Error.WriteLine(strategy == Strategy.DisplayMode
            ? $"[LOADER][INFO] {titleId}: starts in its display mode {DisplayModeFor(titleId)!.ModeFor(width, height)} " +
              $"for a {width}x{height} display ({patches.Count} sites, {EnvironmentVariable}=mode)."
            : $"[LOADER][INFO] {titleId}: compiled display sizes rewritten to {width}x{height} at {patches.Count} sites " +
              $"(the title's UI keeps its 4K layout and comes out oversized; {EnvironmentVariable}=mode runs its HD " +
              $"mode, =0 keeps its own sizes).");
        return true;
    }

    /// <summary>The rewrites that hold the title in its HD mode and, below the HD size, shrink
    /// that mode's render targets to the display. Nothing the UI lays out in is touched.</summary>
    internal static List<GuestBytePatcher.Patch> BuildDisplayModePatches(
        DisplayModeProfile profile, uint width, uint height)
    {
        var patches = new List<GuestBytePatcher.Patch>();
        if (profile.ModeFor(width, height) is not { } mode)
        {
            return patches;
        }

        var old = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(old, profile.OldDefaultMode);
        var replacement = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(replacement, mode);
        foreach (var offset in profile.DefaultModeSites)
        {
            patches.Add(CreatePatch(offset, old, replacement));
        }

        foreach (var site in profile.OverrideSites)
        {
            patches.Add(CreatePatch(site.Offset, site.Old, site.New));
        }

        // Render targets only shrink: a display between 1080p and 4K keeps the HD sizes.
        var targetWidth = Math.Min(width, profile.HdWidth);
        var targetHeight = Math.Min(height, profile.HdHeight);
        patches.AddRange(BuildPatches(profile.HdOutputSites, targetWidth, targetHeight));
        patches.AddRange(BuildPatches(profile.HdInternalSites, targetWidth, targetHeight));
        return patches;
    }

    private static GuestBytePatcher.Patch CreatePatch(ulong offset, byte[] old, byte[] replacement) =>
        new(
            offset,
            replacement,
            old,
            $"0x{offset:X}={Convert.ToHexString(replacement).ToLowerInvariant()}?{Convert.ToHexString(old).ToLowerInvariant()}");

    internal static List<GuestBytePatcher.Patch> BuildPatches(IReadOnlyList<Site> sites, uint width, uint height)
    {
        var patches = new List<GuestBytePatcher.Patch>(sites.Count);
        foreach (var site in sites)
        {
            var newWidth = site.Role == Role.Internal ? Math.Min(width, site.OldWidth) : width;
            var newHeight = site.Role == Role.Internal ? Math.Min(height, site.OldHeight) : height;
            if (newWidth == site.OldWidth && newHeight == site.OldHeight)
            {
                continue;
            }

            var old = Encode(site.Slot, site.OldWidth, site.OldHeight);
            var replacement = Encode(site.Slot, newWidth, newHeight);
            if (old.AsSpan().SequenceEqual(replacement))
            {
                continue;
            }

            patches.Add(new GuestBytePatcher.Patch(
                site.Offset,
                replacement,
                old,
                $"0x{site.Offset:X}={Convert.ToHexString(replacement).ToLowerInvariant()}?{Convert.ToHexString(old).ToLowerInvariant()}"));
        }

        return patches;
    }

    private static byte[] Encode(Slot slot, uint width, uint height)
    {
        switch (slot)
        {
            case Slot.Width:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, width);
                return bytes;
            }

            case Slot.Height:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, height);
                return bytes;
            }

            case Slot.WidthHeight:
            {
                var bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, width | ((ulong)height << 32));
                return bytes;
            }

            case Slot.WidthF32:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, width);
                return bytes;
            }

            case Slot.HeightF32:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, height);
                return bytes;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }
    }
}
