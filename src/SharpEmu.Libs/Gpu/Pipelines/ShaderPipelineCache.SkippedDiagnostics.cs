// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    private readonly HashSet<ulong> _diagnosedSkippedExports = [];
    private RejectedDrawInputCapture? _rejectedDrawInputs;

    public void RecordUnsupportedGraphics(VertexStageRegisters vertex, ContextRegisters context)
        => RecordUnsupportedGraphicsCore(vertex, context, null, null);

    public void RecordUnsupportedGraphics(VertexStageRegisters vertex, ContextRegisters context,
        UserConfigRegisters userConfig, in TargetlessDrawArguments draw,
        DrawIndexedArguments? originalIndirectIndexedDraw = null)
        => RecordUnsupportedGraphicsCore(vertex, context, userConfig, draw, originalIndirectIndexedDraw);

    private uint[] ReadDwords(ulong address, int count)
    {
        var bytes = new byte[count * sizeof(uint)];
        if (address == 0 || !_context.Memory.TryRead(address, bytes)) return [];
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    private ulong ReadQword(ulong address)
    {
        var words = ReadDwords(address, 2);
        return words.Length == 2 ? words[0] | ((ulong)words[1] << 32) : 0;
    }

    private void RecordUnsupportedGraphicsCore(VertexStageRegisters vertex, ContextRegisters context,
        UserConfigRegisters? userConfig, TargetlessDrawArguments? draw,
        DrawIndexedArguments? originalIndirectIndexedDraw = null)
    {
        string? captureDirectory = null;
        try
        {
            if (Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SKIPPED_GRAPHICS") != "1") return;
            var directory = CompiledShaderDump.GetDirectory();
            captureDirectory = directory;
            lock (_gate)
            {
                if (_diagnosedSkippedExports.Count >= 8 || !_diagnosedSkippedExports.Add(vertex.ExportAddress)) return;
                if (!_registry.TryGetDiagnosticCodeRanges(vertex.ExportAddress, out var ranges))
                {
                    Console.Error.WriteLine("[SHADER][WARN] Skipped graphics code has no bounded readable header.");
                    return;
                }
                var captures = new List<object>();
                var bytes = new List<byte[]>();
                foreach (var range in ranges)
                {
                    var data = new byte[checked((int)range.SizeBytes)];
                    if (!_context.Memory.TryRead(range.Address, data))
                    {
                        Console.Error.WriteLine("[SHADER][WARN] Skipped graphics code range is unreadable.");
                        return;
                    }
                    bytes.Add(data);
                    captures.Add(new { address = range.Address, size = range.SizeBytes, sha256 = Convert.ToHexString(SHA256.HashData(data)) });
                }
                Directory.CreateDirectory(directory);
                var stem = Path.Combine(directory, $"skipped-export-{vertex.ExportAddress:X16}");
                for (var i = 0; i < bytes.Count; i++) File.WriteAllBytes($"{stem}.range-{i}.bin", bytes[i]);
                // A tessellated draw's merged local/hull program feeds the export stage its patches.
                var localCaptures = new List<object>();
                if (vertex.LocalAddress != 0 && _registry.TryGetDiagnosticCodeRanges(vertex.LocalAddress, out var localRanges))
                {
                    for (var i = 0; i < localRanges.Length; i++)
                    {
                        var data = new byte[checked((int)localRanges[i].SizeBytes)];
                        if (!_context.Memory.TryRead(localRanges[i].Address, data)) break;
                        File.WriteAllBytes($"{stem}.local-range-{i}.bin", data);
                        localCaptures.Add(new { address = localRanges[i].Address, size = localRanges[i].SizeBytes });
                    }
                }
                File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new
                {
                    stages = context.ShaderStages,
                    exportAddress = vertex.ExportAddress,
                    geometryAddress = vertex.GeometryAddress,
                    subgroupControl = context.ShaderInterface.PrimitiveShaderSubgroupControl,
                    maxOutput = context.ShaderInterface.MaxOutputPerSubgroup,
                    maxVerticesOut = context.ShaderInterface.GeometryMaxVerticesOut,
                    outputPrimitive = context.ShaderInterface.GeometryOutputPrimitiveType,
                    originalDraw = draw,
                    originalIndirectIndexedDraw,
                    primitiveAssembly = userConfig is null ? null : new
                    {
                        userConfig.PrimitiveType,
                        userConfig.IndexOffset,
                        userConfig.ObjectId,
                        userConfig.PrimitiveResetControl,
                        context.PrimitiveResetIndex,
                        userConfig.GeometryEngineControl,
                        userConfig.GeometryEngineUserVectorEnable,
                    },
                    inputGeometry = new
                    {
                        context.ShaderInterface.GeometryOnChipControl,
                        context.ShaderInterface.ExportVerticesPerSubgroup,
                        context.ShaderInterface.GeometryPrimitivesPerSubgroup,
                        context.ShaderInterface.GeometryInstancedPrimitivesInSubgroup,
                        context.ShaderInterface.GeometryInstanceCount,
                        context.ShaderInterface.ExportRingItemSize,
                        context.ShaderInterface.PrimitiveIdEnable,
                        context.ShaderInterface.VertexReuseOff,
                        context.ShaderInterface.IndexExportFormat,
                    },
                    geometryResource1 = vertex.GeometryResource1,
                    geometryResource2 = vertex.GeometryResource2,
                    userData = ((ReadOnlySpan<uint>)vertex.GeometryUserScalars.Values).ToArray(),
                    tessellation = new
                    {
                        localAddress = vertex.LocalAddress,
                        hullAddress = vertex.HullAddress,
                        hullUserDataAddress = vertex.HullUserDataAddress,
                        vertex.HullResource1,
                        vertex.HullResource2,
                        hullUserData = ((ReadOnlySpan<uint>)vertex.HullUserScalars.Values).ToArray(),
                        localHullConfiguration = context.ShaderInterface.LocalHullConfiguration,
                        tessellationFactorParameter = context.ShaderInterface.TessellationFactorParameter,
                        context.ShaderInterface.MaxTessellationLevel,
                        context.ShaderInterface.MinTessellationLevel,
                        context.ShaderInterface.TessellationDistribution,
                        localRanges = localCaptures,
                        hullUserTable = ReadDwords(vertex.HullUserDataAddress, 16),
                        ringTable = ReadDwords(ReadQword(vertex.HullUserDataAddress + 48), 16),
                    },
                    ranges = captures,
                    rejectedDraw = true,
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                Console.Error.WriteLine($"[SHADER][INFO] Captured rejected graphics export: {stem}");
            }
        }
        catch (Exception exception)
        {
            // A diagnostic failure cannot change the rejected draw's execution outcome.
            try { Console.Error.WriteLine($"[SHADER][WARN] Cannot capture rejected graphics: {exception.Message}"); }
            catch (Exception) { }
        }
        finally
        {
            // No helper allocation, formatter or additional guest read when
            // off. Reads/file IO are outside the pipeline-cache gate.
            if (captureDirectory is not null && userConfig is not null && draw is { } capturedDraw)
                try
                {
                    RejectedDrawInputCapture inputs;
                    lock (_gate) inputs = _rejectedDrawInputs ??= new();
                    inputs.TryCapture(_context.Memory, vertex, context, userConfig, in capturedDraw,
                        originalIndirectIndexedDraw, captureDirectory);
                }
                catch (Exception) { } // Optional capture must preserve the original rejection/primary.
        }
    }
}
