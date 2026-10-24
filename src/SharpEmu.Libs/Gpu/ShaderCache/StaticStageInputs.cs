// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.ShaderCache;

/// <summary>The pixel half of a derived graphics pair, with what its vertex half takes from it.</summary>
internal sealed record DerivedPixel(
    ShaderCorpusRecord Record,
    uint AttributeCount,
    uint MeshPerVertexParameterMask,
    IReadOnlyList<uint> VertexInterpolants,
    uint PushDataEnd);

/// <summary>
/// The translation inputs of a program the game has not drawn or dispatched yet, from its
/// AGC header alone - the registers sceAgcDcbSetShader would write - and, for a graphics
/// stage, a draw state the title used with a program of the same pixel signature (its color
/// targets and export routing, the vertex half's clip space and primitive-shader mode). The
/// records go through the same request builders as recorded ones (ShaderCorpus,
/// ShaderPrewarmCompiler), with the specialization of the plan before any draw: with portable
/// buffers that is what nearly every draw's specialization is (Demon's Souls: every vertex
/// record and 404 of 465 pixel records).
/// </summary>
internal static class StaticStageInputs
{
    public const ulong HeaderAddress = 0x20_0000_0000;
    private const uint VertexUserDataBase = 8;
    private const uint MaxPixelInputs = 32;
    private const uint DispatchInitiatorMask = 0xA038;
    private const uint DispatchInitiatorBase = 0x41;
    private const int MaxPixelInputVectors = 30;
    private static readonly int[] PointerFields = [8, 24, 32, 40, 48, 56];
    private const int UserDataPointerCount = 5;

    /// <summary>
    /// The registers that decide a pixel program's outputs: SPI_SHADER_COL_FORMAT, the
    /// export bits of DB_SHADER_CONTROL and CB_SHADER_MASK. Programs that share it take the
    /// same draw states.
    /// </summary>
    public static ulong PixelSignature(AgcShaderHeader header)
    {
        uint colorFormat = 0, depthControl = 0, colorMask = 0;
        foreach (var register in header.ContextRegisters)
        {
            switch (register.Offset)
            {
                case ContextRegisterOffset.SpiShaderColFormat:
                    colorFormat = register.Value;
                    break;
                case ContextRegisterOffset.DbShaderControl:
                    depthControl = register.Value & 0x107;
                    break;
                case ContextRegisterOffset.CbShaderMask:
                    colorMask = register.Value;
                    break;
            }
        }

        Span<uint> values = [colorFormat, depthControl, colorMask];
        return XxHash3.HashToUInt64(MemoryMarshal.AsBytes(values));
    }

    public static bool TryCompute(AgcShaderHeader header, ShaderCodeCapture code, bool computeWave64Supported,
        ShaderCorpusHostFlags host, out ComputePrewarmRecord record, out string reason)
    {
        record = null!;
        if (header.Type != AgcShaderType.Compute)
        {
            reason = "not a compute shader";
            return false;
        }

        if (!TryApplyRegisters(header, out var banks))
        {
            reason = "header registers";
            return false;
        }

        var compute = banks.Shader.Compute;
        var withHeader = WithHeader(code, header);
        var registered = Registered(withHeader, header);
        var initiator = (header.DispatchModifier & DispatchInitiatorMask) | DispatchInitiatorBase;
        var info = ComputeStageInputResolver.Resolve(compute, registered, initiator, !computeWave64Supported, 0, 0, 0);
        var systemRegisters = ShaderPipelineCache.DecodeComputeSystemRegisters(compute);
        var plan = new ShaderCorpusRecord
        {
            Code = withHeader,
            Stage = ShaderStage.Compute,
            Hash = code.Hash,
            Registered = registered,
            UserData = new uint[compute.UserScalarCount],
            UserDataBase = 0,
            Options = new StageCompileOptions { ComputeInfo = info, ComputeSystemRegisters = systemRegisters },
            Host = host,
        };
        if (!ShaderProgramCache.TryPredictSpecialization(plan, out var specialization, out reason) || specialization is null)
        {
            return false;
        }

        record = new ComputePrewarmRecord
        {
            Hash = code.Hash,
            CodeSize = code.CodeSize,
            Address = code.Address,
            UserDataBase = 0,
            UserDataCount = compute.UserScalarCount,
            PushDataCursor = 0,
            Info = info,
            SystemRegisters = systemRegisters,
            Specialization = specialization,
        };
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The pixel stage of <paramref name="pixel"/> under the draw state of
    /// <paramref name="state"/>, a recorded pixel record of the same signature; the
    /// interpolators come from the output semantics of the paired <paramref name="vertex"/>.
    /// </summary>
    public static bool TryPixel(AgcShaderHeader pixel, ShaderCodeCapture code, AgcShaderHeader? vertex, bool secondInterpolantVariant,
        ShaderCorpusRecord state, bool meshCapable, out DerivedPixel derived, out string reason)
    {
        derived = null!;
        if (pixel.Type != AgcShaderType.Pixel || state.Options?.PixelInfo is not { } statePixel)
        {
            reason = "not a pixel shader or state";
            return false;
        }

        var banks = NewBanks();
        if ((vertex is not null && !TryApplyRegisters(vertex, banks)) || !TryApplyRegisters(pixel, banks))
        {
            reason = "header registers";
            return false;
        }

        if (pixel.InputSemanticCount != 0)
        {
            if (vertex is null)
            {
                reason = "no vertex shader to map the inputs from";
                return false;
            }

            var interpolators = AgcExports.ComputeInterpolantMapping(pixel.InputSemantics, vertex.OutputSemantics, secondInterpolantVariant);
            for (var index = 0u; index < (uint)interpolators.Length; index++)
            {
                if (!TryWrite(RegisterWriteTable.ContextIndirect, banks, ContextRegisterOffset.SpiPsInputCntl0 + index, interpolators[index]))
                {
                    reason = "interpolator registers";
                    return false;
                }
            }
        }

        var shaderInterface = banks.Context.ShaderInterface;
        var withHeader = WithHeader(code, pixel);
        if (!Gen5ShaderTranslator.TryDecodeProgram(withHeader.CreateContext(), code.Address, out var program, out reason))
        {
            return false;
        }

        var attributeCount = ShaderPipelineCache.InterpolatedAttributeCount(program);
        var inputCount = shaderInterface.PixelInputControl & 0x3Fu;
        if (inputCount == 0)
        {
            inputCount = Math.Min(attributeCount, MaxPixelInputs);
        }

        if (inputCount > MaxPixelInputs)
        {
            reason = "too many interpolators";
            return false;
        }

        var registered = Registered(withHeader, pixel);
        var previousThrowOnFatal = Scheduling.GpuFatal.ThrowOnFatal;
        Scheduling.GpuFatal.ThrowOnFatal = true;
        PixelInputInfo info;
        try
        {
            info = PixelStageInputResolver.Resolve(withHeader.CreateContext(), registered, shaderInterface,
                statePixel.TargetOutputModes, statePixel.TargetExportMappings, inputCount,
                statePixel.DepthExportEnable, statePixel.StencilExportEnable);
        }
        catch (Exception exception) when (exception is Scheduling.GpuFatal.AbandonedWorkException or InvalidOperationException)
        {
            reason = exception.Message;
            return false;
        }
        finally
        {
            Scheduling.GpuFatal.ThrowOnFatal = previousThrowOnFatal;
        }

        var resource1 = banks.Shader.Pixel.Resource1;
        info.IeeeMode = resource1.IeeeMode;
        info.FloatMode = resource1.FloatMode;
        info.Dx10Clamp = resource1.Dx10Clamp;
        info.GuestWave32 = (shaderInterface.PixelInputControl & (1u << 15)) != 0;
        attributeCount = Math.Max(attributeCount, ShaderPipelineCache.ReadVertexOutputCount(ShaderPipelineCache.InterpolatedAttributesOf(program), info));

        var perVertexParameters = 0u;
        Gen5ManualInterpolationPlan? manual = null;
        if (!state.Host.PerVertexPixelInputs)
        {
            perVertexParameters = PerVertexInputs(program, info, shaderInterface.PixelInputAddress, attributeCount, meshCapable, out manual);
        }

        var options = new StageCompileOptions
        {
            PixelInfo = info,
            PixelOutputs = state.Options.PixelOutputs,
            PixelInputEnable = shaderInterface.PixelInputEnable,
            PixelInputAddress = shaderInterface.PixelInputAddress,
            PerVertexInputsFromPrimitive = perVertexParameters != 0,
            ManualInterpolation = manual is not null,
        };
        var record = new ShaderCorpusRecord
        {
            Code = withHeader,
            Stage = ShaderStage.Pixel,
            Hash = code.Hash,
            Registered = registered,
            UserData = new uint[banks.Shader.Pixel.Resource2.UserScalarCount],
            UserDataBase = 0,
            Options = options,
            PushDataCursor = 0,
            Host = state.Host,
        };
        if (!WithPredictedSpecialization(ref record, out reason) || !TryPushDataEnd(record, out var pushEnd, out reason))
        {
            return false;
        }

        derived = new DerivedPixel(record, attributeCount, perVertexParameters,
            manual is null ? [] : manual.Interpolants.Select(static interpolant => interpolant.Encoded).ToArray(), pushEnd);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The vertex stage of <paramref name="vertex"/> before <paramref name="pixel"/>, under
    /// the clip space and primitive-shader mode of <paramref name="state"/>, a recorded
    /// vertex record.
    /// </summary>
    public static bool TryVertex(AgcShaderHeader vertex, ShaderCodeCapture code, AgcShaderHeader pixelHeader, DerivedPixel pixel,
        ShaderCorpusRecord state, out ShaderCorpusRecord record, out string reason)
    {
        record = null!;
        if (vertex.Type != AgcShaderType.Geometry || state.Options?.VertexInfo is not { } stateVertex)
        {
            reason = "not a geometry shader or state";
            return false;
        }

        if (vertex.HasVertexFetchTables)
        {
            reason = "vertex fetch tables";
            return false;
        }

        var banks = NewBanks();
        if (!TryApplyRegisters(vertex, banks) || !TryApplyRegisters(pixelHeader, banks))
        {
            reason = "header registers";
            return false;
        }

        var shaderInterface = banks.Context.ShaderInterface;
        var stage = banks.Shader.Vertex;
        var withHeader = WithHeader(code, vertex);
        var registered = Registered(withHeader, vertex);
        var userData = new uint[stage.GeometryResource2.UserScalarCount];
        VertexInputInfo info;
        var previousThrowOnFatal = Scheduling.GpuFatal.ThrowOnFatal;
        Scheduling.GpuFatal.ThrowOnFatal = true;
        try
        {
            info = VertexInputResolver.ResolveVertexInputs(withHeader.CreateContext(), registered, userData,
                shaderInterface.VertexOutputControl, stateVertex.ClipSpace);
        }
        catch (Exception exception) when (exception is Scheduling.GpuFatal.AbandonedWorkException or InvalidOperationException)
        {
            reason = exception.Message;
            return false;
        }
        finally
        {
            Scheduling.GpuFatal.ThrowOnFatal = previousThrowOnFatal;
        }

        info.IeeeMode = stage.GeometryResource1.IeeeMode;
        info.FloatMode = stage.GeometryResource1.FloatMode;
        info.Dx10Clamp = stage.GeometryResource1.Dx10Clamp;
        Gen5NggStageInfo? primitiveShader = state.Options.PrimitiveShader is { } statePrimitive
            ? new Gen5NggStageInfo(
                shaderInterface.ExportVerticesPerSubgroup,
                shaderInterface.GeometryPrimitivesPerSubgroup,
                shaderInterface.MaxOutputPerSubgroup,
                // VGT_GS_OUT_PRIM_TYPE is the draw's, not the program's.
                statePrimitive.OutputPrimitiveType,
                shaderInterface.PrimitiveShaderSubgroupControl,
                (uint)stage.GeometryResource2.LocalDataShareSize * 128u * sizeof(uint),
                0,
                0)
            {
                IsWave64 = statePrimitive.IsWave64,
                Passthrough = statePrimitive.Passthrough,
                AmplifiedVerticesOut = statePrimitive.AmplifiedVerticesOut,
            }
            : null;
        var options = new StageCompileOptions
        {
            VertexInfo = info,
            RequiredVertexOutputCount = (int)pixel.AttributeCount,
            PrimitiveShader = primitiveShader,
            MeshPerVertexParameterMask = pixel.MeshPerVertexParameterMask,
            VertexInterpolants = pixel.VertexInterpolants,
        };
        record = new ShaderCorpusRecord
        {
            Code = withHeader,
            Stage = ShaderStage.Vertex,
            Hash = code.Hash,
            Registered = registered,
            UserData = userData,
            UserDataBase = VertexUserDataBase,
            Options = options,
            PushDataCursor = pixel.PushDataEnd,
            Host = state.Host,
        };
        return WithPredictedSpecialization(ref record, out reason);
    }

    // The code with the header beside it, its pointers absolute, as sceAgcCreateShader leaves it.
    public static ShaderCodeCapture WithHeader(ShaderCodeCapture code, AgcShaderHeader header) => new()
    {
        Hash = code.Hash,
        CodeSize = code.CodeSize,
        Address = code.Address,
        Generation = code.Generation,
        Fused = code.Fused,
        Ranges = [.. code.Ranges.Where(static range => range.Address != HeaderAddress), new CodeRange(HeaderAddress, Absolute(header, code.Address))],
    };

    private static byte[] Absolute(AgcShaderHeader header, ulong codeAddress)
    {
        var bytes = (byte[])header.Bytes.Clone();
        void Relocate(int field)
        {
            var relative = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field));
            if (relative != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(field), HeaderAddress + (ulong)(field + relative));
            }
        }

        var userData = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
        var userDataBlock = userData == 0 ? 0 : (int)(8 + userData);
        foreach (var field in PointerFields)
        {
            Relocate(field);
        }

        if (userDataBlock > 0 && userDataBlock + UserDataPointerCount * sizeof(ulong) <= bytes.Length)
        {
            for (var index = 0; index < UserDataPointerCount; index++)
            {
                Relocate(userDataBlock + index * sizeof(ulong));
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), codeAddress);
        return bytes;
    }

    private static RegisteredShader Registered(ShaderCodeCapture code, AgcShaderHeader header)
    {
        var bytes = code.Ranges.First(static range => range.Address == HeaderAddress).Bytes;
        return new RegisteredShader(
            code.Address,
            HeaderAddress,
            code.CodeSize,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(84)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(48)),
            header.InputSemanticCount,
            0,
            0);
    }

    private static bool WithPredictedSpecialization(ref ShaderCorpusRecord record, out string reason)
    {
        if (!ShaderProgramCache.TryPredictSpecialization(record, out var specialization, out reason) || specialization is null)
        {
            return false;
        }

        record = record with { Specialization = specialization };
        return true;
    }

    // Where the next stage's push data starts after this one's.
    private static bool TryPushDataEnd(ShaderCorpusRecord record, out uint end, out string reason)
    {
        end = record.PushDataCursor;
        if (!ShaderCorpus.TryBuildRequest(record, out var request, out reason) || request is null)
        {
            return false;
        }

        request.Bindings.AdvancePushData(ref end);
        return true;
    }

    // ShaderPipelineCache.ResolvePerVertexInputs for a program no draw has resolved yet.
    private static uint PerVertexInputs(Gen5ShaderProgram program, PixelInputInfo info, uint pixelInputAddress, uint attributeCount,
        bool meshCapable, out Gen5ManualInterpolationPlan? manual)
    {
        manual = null;
        var controls = info.InterpolatorSettings.AsSpan(0, (int)info.InputCount);
        var mask = Gen5PixelInputMapping.PerVertexParameterMask(program, controls);
        if (mask == 0)
        {
            return 0;
        }

        var mode = ShaderPipelineCache.PerVertexMode;
        if (mode == ShaderPipelineCache.PerVertexInputMode.Hybrid)
        {
            manual = Gen5ManualInterpolation.Analyze(program, controls, info.CustomInterpolationMask, pixelInputAddress, out _);
            if (manual is not null && manual.Interpolants.Count + (int)attributeCount > MaxPixelInputVectors)
            {
                manual = null;
            }
        }

        return manual is null && mode != ShaderPipelineCache.PerVertexInputMode.Off && meshCapable ? mask : 0;
    }

    private static RegisterBanks NewBanks() => new(static message => new InvalidDataException(message));

    private static bool TryApplyRegisters(AgcShaderHeader header, out RegisterBanks banks)
    {
        banks = NewBanks();
        return TryApplyRegisters(header, banks);
    }

    private static bool TryApplyRegisters(AgcShaderHeader header, RegisterBanks banks)
    {
        foreach (var register in header.ShaderRegisters)
        {
            if (!TryWrite(RegisterWriteTable.ShaderIndirect, banks, register.Offset, register.Value))
            {
                return false;
            }
        }

        foreach (var register in header.ContextRegisters)
        {
            if (!TryWrite(RegisterWriteTable.ContextIndirect, banks, register.Offset, register.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryWrite(RegisterWriter?[] table, RegisterBanks banks, uint offset, uint value)
    {
        if (offset >= (uint)table.Length || table[offset] is not { } writer)
        {
            return false;
        }

        try
        {
            writer(banks, offset, value);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
