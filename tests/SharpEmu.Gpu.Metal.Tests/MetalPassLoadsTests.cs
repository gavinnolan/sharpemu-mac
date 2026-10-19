// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The rule that lets a render pass skip loading a colour attachment. A wrong yes leaves
// whatever tile memory held where the draw did not reach, so every condition is held to
// the conservative side and each one is shown to keep the load on its own.
public sealed class MetalPassLoadsTests
{
    private const uint Width = 3840;
    private const uint Height = 2160;
    private const nint AllChannels = 0xF;

    // The oversized triangle (-1,-1), (3,-1), (-1,3), and the quad corners in strip order.
    private static readonly float[] Triangle = Positions((-1, -1), (3, -1), (-1, 3), (0, 0), (0, 0), (0, 0));
    private static readonly float[] Quad = Positions((-1, 1), (1, 1), (-1, -1), (1, -1), (0, 0), (0, 0));

    [Fact]
    public void FullScreenTriangleOverwritesTheAttachment()
    {
        Assert.True(MetalPassLoads.OverwritesAttachment(FullScreen(), 0, Width, Height));
    }

    [Fact]
    public void FullScreenQuadOverwritesTheAttachment()
    {
        var strip = FullScreen() with { Positions = Quad, PrimitiveType = Mtl.PrimitiveTriangleStrip, VertexCount = 4 };
        Assert.True(MetalPassLoads.OverwritesAttachment(strip, 0, Width, Height));

        // Two triangles sharing the diagonal, as a list of six.
        var list = FullScreen() with
        {
            Positions = Positions((-1, 1), (1, 1), (-1, -1), (1, 1), (-1, -1), (1, -1)),
            VertexCount = 6,
        };
        Assert.True(MetalPassLoads.OverwritesAttachment(list, 0, Width, Height));

        // A rectangle list draws the quad from three corners.
        var rectangle = FullScreen() with { Positions = Quad, RectangleList = true };
        Assert.True(MetalPassLoads.OverwritesAttachment(rectangle, 0, Width, Height));
    }

    [Fact]
    public void BlendedOrMaskedAttachmentKeepsItsLoad()
    {
        Span<bool> blends = [true, false];
        Span<nint> masks = [AllChannels, AllChannels];
        Assert.Equal(0b10u, MetalPassLoads.OverwriteMask(true, 0b11, 1, blends, masks));

        masks[1] = 0x7;
        Assert.Equal(0u, MetalPassLoads.OverwriteMask(true, 0b11, 1, blends, masks));

        var draw = FullScreen() with { OverwriteMask = 0b10 };
        Assert.False(MetalPassLoads.OverwritesAttachment(draw, 0, Width, Height));
        Assert.True(MetalPassLoads.OverwritesAttachment(draw, 1, Width, Height));
    }

    [Fact]
    public void DiscardingOrSilentPixelProgramKeepsTheLoad()
    {
        Span<bool> blends = [false];
        Span<nint> masks = [AllChannels];
        Assert.Equal(1u, MetalPassLoads.OverwriteMask(true, 1, 1, blends, masks));
        Assert.Equal(0u, MetalPassLoads.OverwriteMask(false, 1, 1, blends, masks));
        // The program returns nothing for the attachment.
        Assert.Equal(0u, MetalPassLoads.OverwriteMask(true, 0, 1, blends, masks));
        Assert.Equal(0u, MetalPassLoads.OverwriteMask(true, 1, 4, blends, masks));

        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { OverwriteMask = 0 }, 0, Width, Height));
    }

    [Fact]
    public void PartialScissorOrViewportKeepsTheLoad()
    {
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { ScissorRight = (int)Width - 1 }, 0, Width, Height));
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { ScissorTop = 1 }, 0, Width, Height));

        var half = new MetalPassLoads.Viewport(0, 0, Width / 2, Height);
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { Viewport = half }, 0, Width, Height));

        // A scissor beyond the render area is clamped to it, which still covers everything.
        Assert.True(MetalPassLoads.OverwritesAttachment(
            FullScreen() with { ScissorLeft = -16, ScissorRight = 16384, ScissorBottom = 16384 }, 0, Width, Height));
    }

    [Fact]
    public void GeometryThatMissesAPixelKeepsTheLoad()
    {
        // Half the screen.
        var half = Positions((-1, -1), (1, -1), (-1, 1), (0, 0), (0, 0), (0, 0));
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { Positions = half }, 0, Width, Height));

        // Only the first triangle of the quad as a list of three.
        Assert.False(MetalPassLoads.OverwritesAttachment(
            FullScreen() with { Positions = Quad, VertexCount = 3 }, 0, Width, Height));

        // Unknown positions, an unknown count, and a range past what was evaluated.
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { Positions = null }, 0, Width, Height));
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { VertexCount = 0 }, 0, Width, Height));
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { FirstVertex = 4 }, 0, Width, Height));

        // Depth outside [0, w] is clipped away unless the pipeline clamps it.
        var far = (float[])Triangle.Clone();
        far[2] = 2f;
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { Positions = far }, 0, Width, Height));
        Assert.True(MetalPassLoads.OverwritesAttachment(
            FullScreen() with { Positions = far, DepthClamp = true }, 0, Width, Height));
    }

    [Fact]
    public void FragmentTestsCullingSamplesLayersAndSizeKeepTheLoad()
    {
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { FragmentTestsCanReject = true }, 0, Width, Height));
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { CullNone = false }, 0, Width, Height));
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { Samples = 4 }, 0, Width, Height));
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen() with { Layers = 2 }, 0, Width, Height));

        // The subresource is larger than the render area: the draw cannot reach all of it.
        Assert.False(MetalPassLoads.OverwritesAttachment(FullScreen(), 0, Width * 2, Height * 2));
    }

    private static MetalPassLoads.Draw FullScreen() => new(
        Triangle,
        Mtl.PrimitiveTriangle,
        RectangleList: false,
        VertexCount: 3,
        FirstVertex: 0,
        CullNone: true,
        DepthClamp: false,
        OverwriteMask: 1,
        new MetalPassLoads.Viewport(0, 0, Width, Height),
        ScissorLeft: 0,
        ScissorTop: 0,
        ScissorRight: (int)Width,
        ScissorBottom: (int)Height,
        FragmentTestsCanReject: false,
        Samples: 1,
        Layers: 1,
        RenderWidth: Width,
        RenderHeight: Height);

    private static float[] Positions(params (float X, float Y)[] vertices)
    {
        var positions = new float[vertices.Length * 4];
        for (var index = 0; index < vertices.Length; index++)
        {
            positions[(index * 4) + 0] = vertices[index].X;
            positions[(index * 4) + 1] = vertices[index].Y;
            positions[(index * 4) + 2] = 0.5f;
            positions[(index * 4) + 3] = 1f;
        }

        return positions;
    }
}
