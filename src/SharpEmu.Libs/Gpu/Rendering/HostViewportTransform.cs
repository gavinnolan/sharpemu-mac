// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Rendering;

/// <summary>
/// The guest's path from the exported position to window coordinates (PA_CL_CLIP_CNTL and
/// viewport 0) split between the host's fixed-function viewport and the conversion the
/// vertex program applies. The pipeline cache keys the program on <see cref="ClipSpace"/>
/// and the draw state sets the rest, so both come from this one resolution.
/// </summary>
public readonly record struct HostViewportTransform(
    ClipSpaceTransform ClipSpace,
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth,
    float MaxDepth,
    bool DepthClip)
{
    public const uint MaxViewportDimension = 16384;

    /// <param name="maxWidth">The largest host viewport width, at most <see cref="MaxViewportDimension"/>.</param>
    /// <param name="maxHeight">The largest host viewport height, at most <see cref="MaxViewportDimension"/>.</param>
    public static HostViewportTransform Resolve(ContextRegisters context, uint maxWidth, uint maxHeight)
    {
        var clip = context.Clip;
        ref readonly var viewport = ref context.ScreenViewport.Viewports[0];
        float x;
        float y;
        float width;
        float height;
        ClipSpaceTransform clipSpace;
        if (clip.ClipDisable)
        {
            // Window coordinates: the program applies the viewport transform against the
            // largest host viewport, at the origin and y-down.
            width = Math.Min(maxWidth, MaxViewportDimension);
            height = Math.Min(maxHeight, MaxViewportDimension);
            x = 0;
            y = 0;
            clipSpace = new ClipSpaceTransform(
                true,
                viewport.XScale,
                viewport.YScale,
                viewport.XOffset,
                viewport.YOffset,
                width * 0.5f,
                height * 0.5f)
            {
                PositionYDown = true,
            };
        }
        else
        {
            // The host takes the guest's rectangle. A negative x scale mirrors it, which no
            // host viewport width expresses: the rectangle keeps its pixels with a positive
            // width and the program negates x. A positive y scale is a y-down viewport,
            // which a y-up host reaches by negating y.
            x = viewport.XOffset - Math.Abs(viewport.XScale);
            y = viewport.YOffset - viewport.YScale;
            width = Math.Abs(viewport.XScale) * 2f;
            height = viewport.YScale * 2f;
            clipSpace = new ClipSpaceTransform(false, 0, 0, 0, 0, 0, 0)
            {
                PositionXMirror = viewport.XScale < 0f,
                PositionYDown = viewport.YScale > 0f,
            };
        }

        var depth = ResolveDepth(context);
        clipSpace = clipSpace with { DepthNegativeOneToOne = !clip.DirectXClipSpace };
        return new HostViewportTransform(clipSpace, x, y, width, height, depth.MinDepth, depth.MaxDepth, depth.Clip);
    }

    /// <summary>Whether the host clips depth to its viewport range (else it clamps), as <see cref="Resolve"/> decides.</summary>
    public static bool DepthClipOf(ContextRegisters context) => ResolveDepth(context).Clip;

    private readonly record struct DepthTransform(float MinDepth, float MaxDepth, bool Clip);

    private static DepthTransform ResolveDepth(ContextRegisters context)
    {
        var clip = context.Clip;
        ref readonly var viewport = ref context.ScreenViewport.Viewports[0];
        return new DepthTransform(
            viewport.ZOffset - (clip.DirectXClipSpace ? 0f : viewport.ZScale),
            viewport.ZScale + viewport.ZOffset,
            clip.IsZClipEnabled);
    }
}
