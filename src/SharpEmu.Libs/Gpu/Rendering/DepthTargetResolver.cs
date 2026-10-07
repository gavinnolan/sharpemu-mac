// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

public readonly record struct StencilOperations(GpuStencilOp FailOperation, GpuStencilOp PassOperation, GpuStencilOp DepthFailOperation, GpuCompareOp Compare)
{
    public static StencilOperations Default { get; } = new(GpuStencilOp.Keep, GpuStencilOp.Keep, GpuStencilOp.Keep, GpuCompareOp.Never);
}

public readonly record struct StencilMasks(uint CompareMask, uint WriteMask, uint Reference);

// The depth and stencil pipeline state of a draw, resolved from the context bank.
public readonly record struct DepthStencilState(
    bool DepthClearEnabled,
    float DepthClearValue,
    bool DepthTestEnabled,
    bool DepthWriteEnabled,
    GpuCompareOp DepthCompare,
    bool DepthBoundsTestEnabled,
    float DepthMinBounds,
    float DepthMaxBounds,
    bool StencilClearEnabled,
    byte StencilClearValue,
    bool StencilTestEnabled,
    StencilOperations FrontOperations,
    StencilMasks FrontMasks,
    StencilOperations BackOperations,
    StencilMasks BackMasks)
{
    // The aspects the draw can write; a clear counts, a test without a possible write does not.
    public GpuImageAspect AttachmentWriteAspects(GpuFormat format)
    {
        if (format == GpuFormat.Undefined)
        {
            return GpuImageAspect.None;
        }

        var available = ViewFormatRules.DepthAspects(format);
        var writes = GpuImageAspect.None;
        if ((available & GpuImageAspect.DepthBit) != 0 && (DepthClearEnabled || (DepthTestEnabled && DepthWriteEnabled)))
        {
            writes |= GpuImageAspect.DepthBit;
        }

        if ((available & GpuImageAspect.StencilBit) == 0)
        {
            return writes;
        }

        if (StencilClearEnabled || (StencilTestEnabled && (FaceWrites(FrontOperations, FrontMasks) || FaceWrites(BackOperations, BackMasks))))
        {
            writes |= GpuImageAspect.StencilBit;
        }

        return writes;
    }

    public bool IsReadOnly(GpuFormat format) => AttachmentWriteAspects(format) == GpuImageAspect.None;

    // The layout for an attachment the draw does not also sample: writable in every aspect the
    // format has, so draws that toggle depth or stencil writes keep one layout (and one rendering
    // scope). Only a sampled attachment needs the read-only layouts of AttachmentLayout.
    public static GpuImageLayout WritableAttachmentLayout(GpuFormat format)
    {
        var available = ViewFormatRules.DepthAspects(format);
        var hasDepth = (available & GpuImageAspect.DepthBit) != 0;
        var hasStencil = (available & GpuImageAspect.StencilBit) != 0;
        return hasDepth && hasStencil ? GpuImageLayout.DepthStencilAttachmentOptimal
            : hasStencil ? GpuImageLayout.StencilAttachmentOptimal
            : GpuImageLayout.DepthAttachmentOptimal;
    }

    public GpuImageLayout AttachmentLayout(GpuFormat format)
    {
        var available = ViewFormatRules.DepthAspects(format);
        var writes = AttachmentWriteAspects(format);
        var hasDepth = (available & GpuImageAspect.DepthBit) != 0;
        var hasStencil = (available & GpuImageAspect.StencilBit) != 0;
        var depthWrite = (writes & GpuImageAspect.DepthBit) != 0;
        var stencilWrite = (writes & GpuImageAspect.StencilBit) != 0;
        if (!hasStencil)
        {
            return depthWrite ? GpuImageLayout.DepthAttachmentOptimal : GpuImageLayout.DepthReadOnlyOptimal;
        }

        if (!hasDepth)
        {
            return stencilWrite ? GpuImageLayout.StencilAttachmentOptimal : GpuImageLayout.StencilReadOnlyOptimal;
        }

        if (depthWrite && stencilWrite)
        {
            return GpuImageLayout.DepthStencilAttachmentOptimal;
        }

        if (depthWrite)
        {
            return GpuImageLayout.DepthAttachmentStencilReadOnlyOptimal;
        }

        return stencilWrite ? GpuImageLayout.DepthReadOnlyStencilAttachmentOptimal : GpuImageLayout.DepthStencilReadOnlyOptimal;
    }

    private bool FaceWrites(in StencilOperations operations, in StencilMasks masks)
    {
        if (masks.WriteMask == 0)
        {
            return false;
        }

        var canPass = operations.Compare != GpuCompareOp.Never;
        var canFail = operations.Compare != GpuCompareOp.Always;
        if (masks.CompareMask == 0)
        {
            switch (operations.Compare)
            {
                case GpuCompareOp.Equal:
                case GpuCompareOp.LessOrEqual:
                case GpuCompareOp.GreaterOrEqual:
                case GpuCompareOp.Always:
                    canPass = true;
                    canFail = false;
                    break;
                case GpuCompareOp.Never:
                case GpuCompareOp.Less:
                case GpuCompareOp.Greater:
                case GpuCompareOp.NotEqual:
                    canPass = false;
                    canFail = true;
                    break;
            }
        }

        var depthPass = !DepthTestEnabled || DepthCompare != GpuCompareOp.Never;
        var depthFail = DepthTestEnabled && DepthCompare != GpuCompareOp.Always;
        return (canFail && operations.FailOperation != GpuStencilOp.Keep) ||
               (canPass && depthPass && operations.PassOperation != GpuStencilOp.Keep) ||
               (canPass && depthFail && operations.DepthFailOperation != GpuStencilOp.Keep);
    }
}

public readonly record struct DepthTargetState(DepthTargetResolution Target, DepthStencilState State);

// Resolves the depth target of a draw: the image request through the request builder, then the pipeline state.
public static class DepthTargetResolver
{
    private const byte ReplaceWithOperationValue = 0x04;
    private const byte ExclusiveOr = 0x0C;

    // Null means no depth or stencil state is active for the draw.
    public static DepthTargetState? Resolve(ContextRegisters context, IImageFormatSupport device, Func<string, Exception> fatal)
    {
        if (ImageRequestBuilders.DepthTarget(in context.DepthTarget, device) is not { } target)
        {
            return null;
        }

        return new DepthTargetState(target, ResolveState(context, target.HasStencil, fatal));
    }

    public static DepthStencilState ResolveState(ContextRegisters context, bool hasStencil, Func<string, Exception> fatal)
    {
        var depth = context.DepthTarget;
        var control = context.StencilControl;
        var masks = context.StencilMask;
        var stencilTest = hasStencil && depth.StencilTestEnabled;
        var front = StencilOperations.Default;
        var frontMasks = default(StencilMasks);
        var back = front;
        var backMasks = frontMasks;
        if (stencilTest)
        {
            // A stencil clear or a write-disabled view leaves the operations without effect.
            var operationsDisabled = depth.StencilClearEnabled || depth.StencilWriteDisabled;
            var frontWriteMask = operationsDisabled ? (byte)0 : masks.WriteMask;
            var backWriteMask = operationsDisabled ? (byte)0 : masks.WriteMaskBack;
            if (depth.StencilCompare > (uint)GpuCompareOp.Always ||
                (depth.BackFaceEnabled && depth.StencilCompareBack > (uint)GpuCompareOp.Always))
            {
                throw fatal(
                    $"The stencil compare or replacement state is not supported: depthControl=0x{depth.DepthControl:X8} " +
                    $"front=(fail={control.Fail} pass={control.Pass} depthFail={control.DepthFail} test=0x{masks.TestValue:X2} operation=0x{masks.OperationValue:X2} write=0x{frontWriteMask:X2}) " +
                    $"back=(fail={control.FailBack} pass={control.PassBack} depthFail={control.DepthFailBack} test=0x{masks.TestValueBack:X2} operation=0x{masks.OperationValueBack:X2} write=0x{backWriteMask:X2}).");
            }

            frontMasks = new StencilMasks(masks.Mask, frontWriteMask, masks.TestValue);
            front = ConvertFace(control.Fail, control.Pass, control.DepthFail,
                (GpuCompareOp)depth.StencilCompare, masks.OperationValue, ref frontMasks, fatal);
            if (depth.BackFaceEnabled)
            {
                backMasks = new StencilMasks(masks.MaskBack, backWriteMask, masks.TestValueBack);
                back = ConvertFace(control.FailBack, control.PassBack, control.DepthFailBack,
                    (GpuCompareOp)depth.StencilCompareBack, masks.OperationValueBack, ref backMasks, fatal);
            }
            else
            {
                back = front;
                backMasks = frontMasks;
            }
        }

        return new DepthStencilState(
            depth.DepthClearEnabled,
            context.DepthClearValue,
            depth.DepthTestEnabled,
            depth.DepthWriteEnabled && !depth.DepthWriteDisabled,
            (GpuCompareOp)depth.DepthCompare,
            depth.DepthBoundsEnabled,
            context.DepthBoundsMin,
            context.DepthBoundsMax,
            hasStencil && depth.StencilClearEnabled && !depth.StencilWriteDisabled,
            context.StencilClearValue,
            stencilTest,
            front,
            frontMasks,
            back,
            backMasks);
    }

    private static StencilOperations ConvertFace(byte fail, byte pass, byte depthFail, GpuCompareOp compare,
        byte operationValue, ref StencilMasks masks, Func<string, Exception> fatal)
    {
        ReadOnlySpan<byte> operations = [fail, pass, depthFail];
        Span<GpuStencilOp> converted = stackalloc GpuStencilOp[3];
        var reference = masks.Reference;
        var requiredBits = compare is GpuCompareOp.Always or GpuCompareOp.Never ? 0u : masks.CompareMask;
        for (var index = 0; index < operations.Length; index++)
        {
            converted[index] = ConvertOperation(operations[index], (byte)masks.WriteMask, operationValue, fatal);
            if (converted[index] != GpuStencilOp.Replace)
            {
                continue;
            }

            var replacement = operations[index] == ReplaceWithOperationValue ? operationValue : masks.Reference;
            // Vulkan shares one reference between comparison and all replacements on this face.
            if (((reference ^ replacement) & requiredBits & masks.WriteMask) != 0)
            {
                throw fatal($"The stencil replacement references conflict: compare={compare} compareMask=0x{masks.CompareMask:X2} " +
                    $"writeMask=0x{masks.WriteMask:X2} operation=0x{operationValue:X2} test=0x{masks.Reference:X2}.");
            }

            reference = (reference & ~masks.WriteMask) | (replacement & masks.WriteMask);
            requiredBits |= masks.WriteMask;
        }

        masks = masks with { Reference = reference };
        return new StencilOperations(converted[0], converted[1], converted[2], compare);
    }

    // A zero write mask makes every operation a keep; XOR maps to invert only over the written bits.
    private static GpuStencilOp ConvertOperation(byte operation, byte writeMask, byte operationValue, Func<string, Exception> fatal)
    {
        if (writeMask == 0)
        {
            return GpuStencilOp.Keep;
        }

        switch (operation)
        {
            case 0x00:
                return GpuStencilOp.Keep;
            case 0x01:
                return GpuStencilOp.Zero;
            case 0x03:
                return GpuStencilOp.Replace;
            case ReplaceWithOperationValue:
                return (operationValue & writeMask) == 0 ? GpuStencilOp.Zero : GpuStencilOp.Replace;
            case 0x05:
                return GpuStencilOp.IncrementAndClamp;
            case 0x06:
                return GpuStencilOp.DecrementAndClamp;
            case 0x07:
                return GpuStencilOp.Invert;
            case 0x08:
                return GpuStencilOp.IncrementAndWrap;
            case 0x09:
                return GpuStencilOp.DecrementAndWrap;
            case ExclusiveOr:
                if ((writeMask & operationValue) == 0)
                {
                    return GpuStencilOp.Keep;
                }

                if ((writeMask & ~operationValue) != 0)
                {
                    throw fatal($"The stencil XOR operands are not supported: writeMask=0x{writeMask:X2} operationValue=0x{operationValue:X2}.");
                }

                return GpuStencilOp.Invert;
            default:
                throw fatal($"The stencil operation is not supported: operation=0x{operation:X2}.");
        }
    }
}
