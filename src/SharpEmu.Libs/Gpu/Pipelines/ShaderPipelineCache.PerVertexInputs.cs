// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    // How a pixel program that reads the three vertex values of an input runs on a host without
    // per-vertex pixel inputs (SHARPEMU_METAL_V2_PERVERTEX):
    //   hybrid (default)  host interpolants where the program only interpolates them by hand
    //                     (Gen5ManualInterpolation), the mesh path for the rest
    //   mesh              a primitive shader of a triangle list runs as a mesh function whose
    //                     primitives carry the vertex values, for every such program
    //   off               neither: the inputs are flat (P0), as before either existed
    internal enum PerVertexInputMode
    {
        Hybrid,
        Mesh,
        Off,
    }

    internal static readonly PerVertexInputMode PerVertexMode =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_PERVERTEX")?.Trim().ToLowerInvariant() switch
        {
            "mesh" => PerVertexInputMode.Mesh,
            "off" or "0" or "flat" => PerVertexInputMode.Off,
            _ => PerVertexInputMode.Hybrid,
        };

    // Metal allows 124 interpolated components a fragment function: 31 float4, one of them
    // the position.
    private const int MaxPixelInputVectors = 30;

    private readonly Dictionary<(ulong Hash, uint Custom, uint Address, int Controls), (Gen5ManualInterpolationPlan? Plan, string Reason)>
        _manualInterpolationPlans = new();

    private readonly HashSet<(ulong Hash, string Path)> _loggedPerVertexPaths = new();

    private static readonly bool CachePixelInputAnalysis =
        Environment.GetEnvironmentVariable("SHARPEMU_PIXEL_INPUT_ANALYSIS") != "0";

    // Decoded programs have the same immutable lifetime as InterpolatedAttributeCache.
    // Cache by object identity, not shader address or a draw's mutable control words.
    // The public compiler analysis stays fresh for callers constructing or editing IR.
    internal static uint PerVertexParametersOf(Gen5ShaderProgram program, ReadOnlySpan<uint> controls, bool cached)
    {
        if (!cached)
        {
            return Gen5PixelInputMapping.PerVertexParameterMask(program, controls);
        }

        var attributes = PixelAnalysisOf(program).PerVertexAttributes;
        var parameters = 0u;
        while (attributes != 0)
        {
            var attribute = System.Numerics.BitOperations.TrailingZeroCount(attributes);
            var offset = attribute < controls.Length ? controls[attribute] & 0x1Fu : (uint)attribute;
            parameters |= 1u << (int)offset;
            attributes &= attributes - 1;
        }

        return parameters;
    }

    /// <summary>
    /// Where the per-vertex values of a pixel program's inputs come from: host interpolants
    /// (the plan), the mesh primitives (the returned param mask), or nowhere.
    /// </summary>
    private uint ResolvePerVertexInputs(
        ShaderSource pixelSource,
        Gen5ShaderProgram pixelProgram,
        PixelInputInfo pixelInfo,
        uint pixelInputAddress,
        uint attributeCount,
        bool meshCapable,
        out Gen5ManualInterpolationPlan? manualInterpolation)
    {
        manualInterpolation = null;
        var controls = pixelInfo.InterpolatorSettings.AsSpan(0, (int)pixelInfo.InputCount);
        var mask = PerVertexParametersOf(pixelProgram, controls, CachePixelInputAnalysis);
        if (mask == 0)
        {
            return 0;
        }

        var reason = "mode";
        if (PerVertexMode == PerVertexInputMode.Hybrid)
        {
            var controlsHash = new HashCode();
            foreach (var control in controls)
            {
                controlsHash.Add(control);
            }

            var key = (pixelSource.Hash, pixelInfo.CustomInterpolationMask, pixelInputAddress, controlsHash.ToHashCode());
            if (!_manualInterpolationPlans.TryGetValue(key, out var entry))
            {
                var plan = Gen5ManualInterpolation.Analyze(pixelProgram, controls, pixelInfo.CustomInterpolationMask, pixelInputAddress,
                    out var analysisReason);
                if (plan is not null && plan.Interpolants.Count + (int)attributeCount > MaxPixelInputVectors)
                {
                    (plan, analysisReason) = (null, $"{plan.Interpolants.Count} interpolants over the input limit");
                }

                entry = (plan, analysisReason);
                _manualInterpolationPlans.Add(key, entry);
            }

            (manualInterpolation, reason) = entry;
        }

        var parameters = manualInterpolation is null && PerVertexMode != PerVertexInputMode.Off && meshCapable ? mask : 0;
        var path = manualInterpolation is not null ? "hybrid" : parameters != 0 ? "mesh" : "flat";
        if (_loggedPerVertexPaths.Add((pixelSource.Hash, path)))
        {
            Console.Error.WriteLine(
                $"[GPU][PERVERTEX] ps=0x{pixelSource.Hash:X16} path={path} params=0x{mask:X} " +
                $"interpolants={manualInterpolation?.Interpolants.Count ?? 0} rewrites={manualInterpolation?.Rewrites.Count ?? 0} " +
                $"reason={reason}");
        }

        return parameters;
    }
}
