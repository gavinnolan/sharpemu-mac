// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static void RequireQualifiedColorTextureSourceWork(OffscreenGuestDraw work)
    {
        lock (_gate)
        {
            var draw = work.Draw;
            if (work.Targets.Length != 1 || work.Targets[0].Address == 0 || work.DepthTarget is not null ||
                draw.RenderState.Depth != GuestDepthState.Default || draw.PrimitiveType != 4 ||
                draw.VertexCount == 0 || draw.InstanceCount == 0 || draw.BaseVertex < 0 || draw.IndexBuffer is not null ||
                draw.RenderState.Blends.Count != 1)
                throw new NotSupportedException("Metal ordered color producer requires one nonzero exact 2D color target, nonindexed triangles and no depth/presentation writer.");
            if (draw.PixelShader.Shader.Stage != Gen5MslStage.Pixel ||
                draw.VertexShader is not { } vertex || vertex.Shader.Stage != Gen5MslStage.Vertex)
                throw new NotSupportedException("Metal ordered color producer requires actual vertex/pixel shader stages.");
            var target = work.Targets[0];
            if (target.Width == 0 || target.Height == 0 || target.Width > 16384 || target.Height > 16384 ||
                target.WriteMask == 0 || (target.WriteMask & ~0xFu) != 0 || draw.RenderState.Blends[0].WriteMask != target.WriteMask)
                throw new NotSupportedException("Metal ordered color producer has an unsupported extent or mismatched/empty write mask.");
            foreach (var texture in draw.Textures)
                if (StorageImageWrites(texture))
                    throw new NotSupportedException("Metal ordered color producer does not support shader-storage writers.");
            if (draw.Stages.Any(static stage => stage.UsesGlobalDataShare))
                throw new NotSupportedException("Metal ordered color producer does not support global-data-share mutation.");
            if (_pendingGuestImageInitialData.ContainsKey(target.Address))
                throw new NotSupportedException("Metal ordered color producer cannot discard pending CPU initialization.");
            if (!_textureSourceWorkOwners.TryGetValue(work, out var owner) || owner.Released || owner.Outputs.Length != 1)
                throw new InvalidOperationException("Metal ordered color producer has no exact admitted output owner.");
            var source = owner.Outputs[0];
            var actual = DescribeColorTextureSource(target);
            if (source.View != TextureSourceView.Of(actual, verifiedColorProducer: true) || source.Failed ||
                source.Descriptor is null || source.Descriptor.Shape != actual.Shape || source.Descriptor.Access != actual.Access)
                throw new NotSupportedException("Metal ordered color producer target no longer matches its reserved typed view.");
            if (source.Initializer is null && (!source.ClearsOutput ||
                _guestImages.TryGetValue(target.Address, out var live) && live.Initialized))
                throw new NotSupportedException("Metal ordered color producer requires a retained immutable predecessor or real first-use full-target clear.");
        }
    }

    private static void ValidateReservedColorTextureSourceTarget(OffscreenGuestDraw work, GuestImage image)
    {
        var source = ExecutingTextureSourceOwner().Outputs.Single();
        if (!ReferenceEquals(source.Image, image) || image.Texture != source.Texture ||
            image.TextureType != MtlTextureType2D || image.Depth != 1 ||
            image.Width != work.Targets[0].Width || image.Height != work.Targets[0].Height ||
            image.Format != MetalGuestFormats.DecodeTextureFormat(source.View.Format, source.View.NumberType).Format)
            throw new NotSupportedException("Metal color attachment does not match its reserved native output target.");
        ValidateTextureSourceNativeView(source, image.Texture);
    }
}
