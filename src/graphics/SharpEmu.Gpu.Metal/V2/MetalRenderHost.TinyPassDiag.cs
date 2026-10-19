// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Gpu.Metal.V2;

// Timing experiments for the tiny 244x150 single-draw passes (vertex shader
// 0B8F4E1B20E2D44A) that cost ~600 us of GPU time each. Diagnostics only, all off by
// default, all restricted to render passes whose colour target is exactly 244x150:
//   SHARPEMU_METAL_V2_PP_REPEAT=N     (2..16) issue each non-indexed or indexed draw N times.
//   SHARPEMU_METAL_V2_PP_PROBE=1      open and end an extra clear-only 16x16 pass first.
//   SHARPEMU_METAL_V2_PP_DUMMY_TEX=1  bind 1x1 private textures for the target vertex shader's images.
internal sealed partial class MetalRenderHost
{
    private const ulong TinyPassVertexHash = 0x0B8F4E1B20E2D44AUL;

    private static readonly int PpRepeat = ParsePpRepeat();
    private static readonly bool PpProbe = Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_PP_PROBE") == "1";
    private static readonly bool PpDummyTex =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_PP_DUMMY_TEX") == "1";

    private bool _tinyPass;
    private nint _ppProbeTexture;
    private nint _ppDummyR8;
    private nint _ppDummyRg8;

    private static int ParsePpRepeat() =>
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_PP_REPEAT"), out var value)
            ? Math.Clamp(value, 1, 16)
            : 1;

    private static bool IsTinyPass(in RenderingState state) =>
        state.ColorAttachmentCount > 0 && state.Width == 244 && state.Height == 150;

    /// <summary>E2: a clear-only 16x16 pass ahead of a tiny pass, labelled PROBE in the pass profile.</summary>
    private void EncodeTinyPassProbe(nint commandBuffer)
    {
        if (_ppProbeTexture == 0)
        {
            _ppProbeTexture = NewDiagTexture(
                MtlFormat.Bgra8Unorm, 16, 16, (nuint)Mtl.TextureUsageRenderTarget);
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (descriptor == 0 || _ppProbeTexture == 0)
        {
            return;
        }

        try
        {
            var colors = MetalNative.Send(descriptor, SelColorAttachments);
            var slot = MetalNative.SendAtIndex(colors, SelObjectAtIndex, 0);
            MetalNative.SendVoid(slot, SelSetTexture, _ppProbeTexture);
            MetalV2Native.SendVoidValue(slot, SelSetLoadAction, Mtl.LoadActionClear);
            MetalV2Native.SendVoidValue(slot, SelSetStoreAction, Mtl.StoreActionStore);
            MetalNative.SendVoidClearColor(slot, SelSetClearColor, default);
            if (PassProfileEnabled)
            {
                PassProfileAttachRender(descriptor);
                if (_ppOpen is { } record)
                {
                    record.Shape = "PROBE 16x16";
                    record.Shaders = new Dictionary<string, int>(StringComparer.Ordinal) { ["vs=PROBE/ps=PROBE"] = 0 };
                    record.End = "probe";
                }
            }

            var encoder = MetalNative.Send(commandBuffer, SelRenderEncoder, descriptor);
            MetalMemoryTelemetry.RenderEncoders.Add();
            if (encoder != 0)
            {
                MetalNative.SendVoid(encoder, SelEndEncoding);
            }

            PassProfileClose();
            ForgetEncoderState();
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }
    }

    private nint NewDiagTexture(ulong format, uint width, uint height, nuint usage)
    {
        var descriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"), SelTexture2DDescriptor, (nuint)format, width, height,
            mipmapped: false);
        if (descriptor == 0)
        {
            return 0;
        }

        MetalV2Native.SendVoidValue(descriptor, SelMovieSetUsage, usage);
        MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setStorageMode:"), Mtl.StorageModePrivate);
        return MetalNative.Send(_device, SelMovieNewTexture, descriptor);
    }

    /// <summary>E3: the 1x1 stand-in for an image the target vertex shader's pipeline samples, or 0 to keep it.</summary>
    private nint TinyPassDummyTexture(nint original, int resource)
    {
        if (!PpDummyTex || !_tinyPass || _pendingGraphicsPipeline?.VertexHash != TinyPassVertexHash)
        {
            return 0;
        }

        _ppDummyR8 = _ppDummyR8 != 0
            ? _ppDummyR8
            : NewDiagTexture(MtlFormat.R8Unorm, 1, 1, (nuint)Mtl.TextureUsageShaderRead);
        _ppDummyRg8 = _ppDummyRg8 != 0
            ? _ppDummyRg8
            : NewDiagTexture(MtlFormat.Rg8Unorm, 1, 1, (nuint)Mtl.TextureUsageShaderRead);
        var format = (ulong)MetalV2Native.SendValue(original, MetalNative.Selector("pixelFormat"));
        var rg = format == MtlFormat.Rg8Unorm || (format != MtlFormat.R8Unorm && (resource & 1) != 0);
        return rg ? _ppDummyRg8 : _ppDummyR8;
    }
}
