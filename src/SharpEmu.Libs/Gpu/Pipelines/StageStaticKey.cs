// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The static state of a stage as one word list; two draws with equal lists share a program entry.
public static class StageStaticKey
{
    // The nine trailing words are the NGG primitive-shader state (see the vertex key).
    public const int MaxWords = 16 + 9 + VertexInputInfo.MaxBuffers * 13;

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static uint Bit(bool value) => value ? 1u : 0u;

    // The vertex key adds the required output count the paired pixel program declares.
    public static void Build(VertexInputInfo info, int requiredOutputCount, List<uint> key) =>
        Build(info, requiredOutputCount, null, key);

    // A primitive-shader draw compiles the same program differently - as the merged ES/GS
    // workgroup stage, sized by the geometry-engine registers - so that state belongs in
    // the key or two draws would share one permutation.
    public static void Build(
        VertexInputInfo info,
        int requiredOutputCount,
        Gen5NggStageInfo? primitiveShader,
        List<uint> key)
    {
        key.Clear();
        key.Add(Bit(info.FetchEmbedded));
        key.Add((uint)info.FetchAttributeRegister);
        key.Add((uint)info.FetchBufferRegister);
        key.Add((uint)info.Attributes.Length);
        key.Add(Bit(info.IeeeMode));
        key.Add(info.FloatMode);
        key.Add(Bit(info.Dx10Clamp));
        key.Add(info.ScratchDwords);
        key.Add(info.PositionExportControl);
        key.Add(Bit(info.ClipSpace.Enabled));
        if (info.ClipSpace.Enabled)
        {
            key.Add(Bits(info.ClipSpace.ScaleX));
            key.Add(Bits(info.ClipSpace.ScaleY));
            key.Add(Bits(info.ClipSpace.OffsetX));
            key.Add(Bits(info.ClipSpace.OffsetY));
            key.Add(Bits(info.ClipSpace.HalfExtentX));
            key.Add(Bits(info.ClipSpace.HalfExtentY));
        }

        key.Add(Bit(info.ClipSpace.DepthNegativeOneToOne) | (Bit(info.ClipSpace.PositionYDown) << 1) |
            (Bit(info.ClipSpace.PositionXMirror) << 2));

        key.Add((uint)requiredOutputCount);
        key.Add(Bit(primitiveShader.HasValue));
        if (primitiveShader is { } ngg)
        {
            key.Add(ngg.VerticesPerSubgroup);
            key.Add(ngg.PrimitivesPerSubgroup);
            key.Add(ngg.MaxOutputPerSubgroup);
            key.Add(ngg.OutputPrimitiveType);
            key.Add(ngg.PrimitiveAmplificationFactor);
            key.Add(ngg.LocalDataShareBytes);
            key.Add(ngg.VertexGroupSize);
            key.Add(ngg.PrimitiveGroupSize);
            key.Add(ngg.GeometryVerticesOut);
        }

        foreach (var attribute in info.Attributes)
        {
            var descriptor = attribute.Descriptor;
            key.Add((uint)attribute.RegisterStart);
            key.Add((uint)attribute.RegisterCount);
            key.Add(attribute.FetchIndex);
            key.Add((uint)attribute.AttributeId);
            key.Add(descriptor.Stride);
            key.Add(Bit(descriptor.SwizzleEnabled));
            key.Add(descriptor.DestinationSelectX);
            key.Add(descriptor.DestinationSelectY);
            key.Add(descriptor.DestinationSelectZ);
            key.Add(descriptor.DestinationSelectW);
            key.Add(descriptor.Format);
            key.Add(descriptor.OutOfBounds);
            key.Add(Bit(descriptor.AddThreadId));
        }
    }

    public static void Build(PixelInputInfo info, List<uint> key)
    {
        key.Clear();
        key.Add(Bit(info.GuestWave32));
        key.Add(Bit(info.IeeeMode));
        key.Add(info.FloatMode);
        key.Add(Bit(info.Dx10Clamp));
        key.Add(info.ScratchDwords);
        key.Add(info.InputCount);
        key.Add(info.SystemInputBase);
        key.Add(info.CustomInterpolationMask);
        key.Add(info.PerspectiveCenterRegister);
        key.Add(Bit(info.PositionX));
        key.Add(Bit(info.PositionY));
        key.Add(Bit(info.PositionZ));
        key.Add(Bit(info.PositionW));
        key.Add(Bit(info.FrontFace));
        key.Add(Bit(info.FrontFaceAllBits));
        key.Add(Bit(info.NoPerspective));
        key.Add(Bit(info.KillEnable));
        key.Add(Bit(info.DepthExportEnable));
        key.Add(Bit(info.SampleMaskExportEnable));
        key.Add(Bit(info.StencilExportEnable));
        key.Add(info.DepthExportFormat is { } depthFormat ? (uint)depthFormat : uint.MaxValue);
        key.Add(Bit(info.EarlyDepth));
        foreach (var mode in info.TargetOutputModes)
        {
            key.Add(mode);
        }

        for (var first = 0; first < PixelInputInfo.TargetCount; first += 4)
        {
            var packed = 0u;
            for (var index = 0; index < 4; index++)
            {
                packed |= (uint)info.TargetExportMappings[first + index].Packed << (index * 8);
            }

            key.Add(packed);
        }

        for (var index = 0; index < info.InputCount; index++)
        {
            key.Add(info.InterpolatorSettings[index]);
        }
    }

    // The dispatch mode is static; exact thread limits arrive with each dispatch.
    public static void Build(ComputeInputInfo info, List<uint> key)
    {
        key.Clear();
        key.Add((uint)info.WorkgroupRegister);
        key.Add(info.WaveSize);
        key.Add((uint)info.ThreadIdCount);
        key.Add(info.LocalDataShareDwords);
        key.Add(Bit(info.IeeeMode));
        key.Add(info.FloatMode);
        key.Add(Bit(info.Dx10Clamp));
        key.Add(info.ScratchDwords);
        key.Add(Bit(info.NeedsLocalDataShareBarriers));
        key.Add(Bit(info.DispatchThreadDimensions));
        key.Add(info.ThreadsX);
        key.Add(Bit(info.GroupIdX));
        key.Add(info.ThreadsY);
        key.Add(Bit(info.GroupIdY));
        key.Add(info.ThreadsZ);
        key.Add(Bit(info.GroupIdZ));
        key.Add(Bit(info.ThreadGroupSizeEnabled));
    }
}
