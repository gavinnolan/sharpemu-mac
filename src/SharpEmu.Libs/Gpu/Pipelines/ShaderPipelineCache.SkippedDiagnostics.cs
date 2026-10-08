// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    private readonly HashSet<ulong> _diagnosedSkippedExports = [];

    public void RecordUnsupportedGraphics(VertexStageRegisters vertex, ContextRegisters context)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SKIPPED_GRAPHICS") != "1") return;
            var directory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
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
                File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new
                {
                    stages = context.ShaderStages,
                    exportAddress = vertex.ExportAddress,
                    geometryAddress = vertex.GeometryAddress,
                    subgroupControl = context.ShaderInterface.PrimitiveShaderSubgroupControl,
                    maxOutput = context.ShaderInterface.MaxOutputPerSubgroup,
                    maxVerticesOut = context.ShaderInterface.GeometryMaxVerticesOut,
                    outputPrimitive = context.ShaderInterface.GeometryOutputPrimitiveType,
                    geometryResource1 = vertex.GeometryResource1,
                    geometryResource2 = vertex.GeometryResource2,
                    userData = vertex.GeometryUserScalars.Values.AsSpan().ToArray(),
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
    }
}
