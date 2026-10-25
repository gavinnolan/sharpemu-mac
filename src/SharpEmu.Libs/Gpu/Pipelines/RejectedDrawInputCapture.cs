// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Diagnostic CPU observations only. No captured byte or register supplies
// hardware launch state, freshness, a resource owner or execution admission.
internal sealed class RejectedDrawInputCapture
{
    internal const int MaximumExports = 8, MaximumDrawsPerExport = 4, MaximumIndexBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, int> _drawCounts = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    internal bool TryCapture(ICpuMemory memory, VertexStageRegisters vertex, ContextRegisters context,
        UserConfigRegisters userConfig, in TargetlessDrawArguments boundary,
        DrawIndexedArguments? originalIndirect, string directory)
    {
        var exportAddress = vertex.ExportAddress; // original identity before any callback/read
        int ordinal;
        lock (_gate)
        {
            if (!_drawCounts.TryGetValue(exportAddress, out ordinal) && _drawCounts.Count >= MaximumExports) return false;
            if (ordinal >= MaximumDrawsPerExport) return false;
            _drawCounts[exportAddress] = ordinal + 1; // consume even failed diagnostics; bounded, no retries
        }
        // Copy original scalar facts BEFORE any read or output callback.
        var original = originalIndirect is { } packet
            ? new TargetlessDrawArguments(boundary.SubmitId, true, packet, default) : boundary;
        var indexed = boundary.Indexed ? boundary.Indexed_ : default;
        var requestedIndexBytes = boundary.Indexed ? (ulong)indexed.IndexCount * IndexBytes(indexed.IndexTypeAndSize) : 0;
        var indirectAddress = boundary.Indexed ? indexed.IndirectArgumentsAddress : 0;
        var inputs = new
        {
            boundary, original, originalIndirect,
            sourceSpans = new { indexAddress = boundary.Indexed ? indexed.IndexAddress : 0,
                requestedIndexBytes, indirectAddress, requestedIndirectBytes = indirectAddress == 0 ? 0 : 20,
                countIsPacketResolved = !boundary.Indexed || indirectAddress == 0 && !indexed.UnboundedIndexBuffer },
            classification = "transported CPU packet/register facts; no hardware GS/LDS/result observation",
            ExportAddress = exportAddress, vertex.GeometryAddress, vertex.ExportResource1, vertex.ExportResource2,
            geometryResource1 = vertex.GeometryResource1, geometryResource2 = vertex.GeometryResource2,
            geometryUserData = ((ReadOnlySpan<uint>)vertex.GeometryUserScalars.Values).ToArray(),
            exportUserData = ((ReadOnlySpan<uint>)vertex.ExportUserScalars.Values).ToArray(),
            context.ShaderStages,
            primitiveAssembly = new { userConfig.PrimitiveType, userConfig.IndexOffset, userConfig.PrimitiveResetControl,
                userConfig.ObjectId, userConfig.GeometryEngineControl, userConfig.GeometryEngineUserVectorEnable,
                context.PrimitiveResetIndex },
            launchControls = new { context.ShaderInterface.GeometryOnChipControl,
                context.ShaderInterface.ExportVerticesPerSubgroup, context.ShaderInterface.GeometryPrimitivesPerSubgroup,
                context.ShaderInterface.GeometryInstancedPrimitivesInSubgroup, context.ShaderInterface.GeometryInstanceCount,
                context.ShaderInterface.ExportRingItemSize, context.ShaderInterface.PrimitiveShaderSubgroupControl,
                context.ShaderInterface.PrimitiveIdEnable, context.ShaderInterface.VertexReuseOff },
            exportControls = new { context.ShaderInterface.IndexExportFormat, context.ShaderInterface.VertexOutputConfiguration,
                context.ShaderInterface.PositionExportFormat, context.ShaderInterface.VertexOutputControl,
                context.ShaderInterface.MaxOutputPerSubgroup, context.ShaderInterface.GeometryMaxVerticesOut,
                context.ShaderInterface.GeometryOutputPrimitiveType },
        };
        var inputJson = JsonSerializer.SerializeToUtf8Bytes(inputs, Json);
        var inputHash = Hash(inputJson);
        var stem = Path.Combine(directory, $"skipped-export-{exportAddress:X16}.draw-{ordinal}");
        var index = boundary.Indexed
            ? CapturePayload(memory, indexed.IndexAddress, requestedIndexBytes,
                MaximumIndexBytes, inputHash, "index-cpu-observation", indexed.IndexTypeAndSize <= 2,
                indexed.IndirectArgumentsAddress == 0 && !indexed.UnboundedIndexBuffer)
            : new Payload(0, 0, [], "automatic-draw-no-index-bytes", false, false, null);
        // An unresolved argument buffer is observed as exactly 20 bytes. Never
        // resolve it into the live draw or extrapolate an index pointer from it.
        var indirect = indirectAddress == 0
            ? new Payload(0, 0, [], originalIndirect is null ? "no-transported-indirect-address" : "already-cpu-resolved-original-preserved",
                false, false, null)
            : CapturePayload(memory, indirectAddress, 20, 20, inputHash, "indirect-cpu-observation", true, false);
        Directory.CreateDirectory(directory);
        WritePayload(stem, "indices", index);
        WritePayload(stem, "indirect", indirect);
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new
        {
            schema = "rejected-draw-input-v1", sourceMetadataSha256 = inputHash, inputs,
            shaderCodeEvidence = new { file = $"skipped-export-{exportAddress:X16}.json",
                relation = "same export address; separate first-code capture; no mutable-code equivalence inferred" },
            indices = Metadata(index, "indices"), indirectArguments = Metadata(indirect, "indirect"),
            rawIndexDomain = IndexDomain(index, indexed.IndexTypeAndSize),
            completeness = new
            {
                packetCountsKnown = !boundary.Indexed || indexed.IndirectArgumentsAddress == 0 && !indexed.UnboundedIndexBuffer,
                fullCpuIndexBytes = index.Status == "copied" && index.Complete,
                cpuBytesAreGpuCurrent = false,
                hardwarePackedGsInputsCaptured = false, sharedLdsContentsCaptured = false, allocationMessageResultCaptured = false,
                emittedVertexPrimitiveCountsCaptured = false, admitted = false,
            },
            observation = "independent best-effort CPU reads after draw rejection; no atomic/coherent GPU snapshot or mapping lifetime receipt",
        }, Json));
        return true;
    }

    private sealed record Payload(ulong Address, ulong RequestedBytes, byte[] Bytes, string Status,
        bool Complete, bool CountIsPacketResolved, string? ProvenanceSha256);
    private static uint IndexBytes(uint type) => type switch { 0 => 2, 1 => 4, 2 => 1, _ => 0 };
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Payload CapturePayload(ICpuMemory memory, ulong address, ulong requested, int bound,
        string inputHash, string classification, bool supported, bool resolved)
    {
        byte[] bytes = [];
        var status = !supported ? "unsupported-index-type" : requested == 0 ? "empty" :
            address == 0 || address > ulong.MaxValue - requested ? "invalid-address-or-overflow" : "copied";
        if (status == "copied")
        {
            bytes = new byte[(int)Math.Min(requested, (ulong)bound)];
            try { if (!memory.TryRead(address, bytes)) { bytes = []; status = "unreadable"; } }
            catch { bytes = []; status = "read-threw"; }
        }
        var complete = status == "copied" && (ulong)bytes.Length == requested;
        // The provenance hash binds source span, exact original/resolved packet
        // metadata hash, completeness, CPU classification and raw content hash.
        var proof = JsonSerializer.SerializeToUtf8Bytes(new { address, requested, capturedBytes = bytes.Length,
            inputHash, classification, resolved, complete, status, contentSha256 = Hash(bytes) }, Json);
        return new(address, requested, bytes, status, complete, resolved, Hash(proof));
    }
    private static void WritePayload(string stem, string name, Payload payload)
    { if (payload.Bytes.Length != 0) File.WriteAllBytes(stem + "." + name + ".bin", payload.Bytes); }
    private static object Metadata(Payload payload, string name) => new
    {
        payload.Address, payload.RequestedBytes, capturedBytes = payload.Bytes.Length, payload.Status,
        payload.Complete, payload.CountIsPacketResolved, payload.ProvenanceSha256,
        fileSuffix = payload.Bytes.Length == 0 ? null : "." + name + ".bin",
    };
    private static object? IndexDomain(Payload payload, uint type)
    {
        var element = IndexBytes(type);
        if (payload.Status != "copied" || element == 0 || payload.Bytes.Length == 0) return null;
        uint minimum = uint.MaxValue, maximum = 0;
        for (var i = 0; i <= payload.Bytes.Length - element; i += (int)element)
        {
            var value = element == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Bytes.AsSpan(i, 4)) :
                element == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Bytes.AsSpan(i, 2)) : payload.Bytes[i];
            minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
        }
        return new { minimum, maximum, capturedElements = payload.Bytes.Length / element,
            interpretation = "raw captured CPU indices including restart values; no vertex/primitive assembly or packed GS seed inferred" };
    }
}
