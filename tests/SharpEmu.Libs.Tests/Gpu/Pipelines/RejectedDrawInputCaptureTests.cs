// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Reflection;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Managed diagnostic byte/provenance controls only. No GPU, pipeline,
// coherent snapshot, packed GS register or NGG admission is supplied here.
[Collection(SchedulingStateCollection.Name)]
public sealed class RejectedDrawInputCaptureTests
{
    [Theory]
    [InlineData(0u, 2)] [InlineData(1u, 4)] [InlineData(2u, 1)]
    public void IndexedDiagnosticCapturesExactSourceBytesAndKeepsHardwareDomainsUnknown(uint type, int element)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Capture(Indexed(3, type)));
        Assert.Equal(new (ulong, int)[] { (0x4000UL, 3 * element) }, fixture.Memory.Reads);
        using var result = fixture.Result(); var root = result.RootElement;
        Assert.Equal(0x4000UL, root.GetProperty("indices").GetProperty("Address").GetUInt64());
        Assert.Equal(3 * element, root.GetProperty("indices").GetProperty("capturedBytes").GetInt32());
        Assert.True(root.GetProperty("completeness").GetProperty("fullCpuIndexBytes").GetBoolean());
        Assert.False(root.GetProperty("completeness").GetProperty("cpuBytesAreGpuCurrent").GetBoolean());
        foreach (var name in new[] { "hardwarePackedGsInputsCaptured", "sharedLdsContentsCaptured",
            "allocationMessageResultCaptured", "emittedVertexPrimitiveCountsCaptured", "admitted" })
            Assert.False(root.GetProperty("completeness").GetProperty(name).GetBoolean());
        Assert.Equal(64, root.GetProperty("indices").GetProperty("ProvenanceSha256").GetString()!.Length);
        Assert.Equal(3 * element, File.ReadAllBytes(fixture.Stem + ".indices.bin").Length);
    }

    [Fact]
    public void LargeIndexCaptureIsBoundedAndExplicitlyIncomplete()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Capture(Indexed(1000000, 1)));
        Assert.Equal(new (ulong, int)[] { (0x4000UL, RejectedDrawInputCapture.MaximumIndexBytes) }, fixture.Memory.Reads);
        using var result = fixture.Result();
        Assert.False(result.RootElement.GetProperty("indices").GetProperty("Complete").GetBoolean());
        Assert.Equal(4000000UL, result.RootElement.GetProperty("indices").GetProperty("RequestedBytes").GetUInt64());
    }

    [Theory]
    [InlineData("zero")] [InlineData("overflow")] [InlineData("unsupported")]
    [InlineData("unreadable")] [InlineData("throws")]
    public void InvalidOrUnreadableInputDoesNotPublishPartialPayload(string reason)
    {
        using var fixture = new Fixture();
        var draw = Indexed(4, reason == "unsupported" ? 3u : 1u);
        if (reason == "zero") draw = draw with { Indexed_ = draw.Indexed_ with { IndexAddress = 0 } };
        if (reason == "overflow") draw = draw with { Indexed_ = draw.Indexed_ with { IndexAddress = ulong.MaxValue - 1 } };
        fixture.Memory.Fail = reason == "unreadable"; fixture.Memory.Throw = reason == "throws";
        Assert.True(fixture.Capture(draw));
        Assert.Equal(reason is "unreadable" or "throws" ? 1 : 0, fixture.Memory.Reads.Count);
        using var result = fixture.Result();
        Assert.Equal(0, result.RootElement.GetProperty("indices").GetProperty("capturedBytes").GetInt32());
        Assert.False(result.RootElement.GetProperty("indices").GetProperty("Complete").GetBoolean());
        Assert.False(File.Exists(fixture.Stem + ".indices.bin"));
    }

    [Fact]
    public void OriginalAndCpuResolvedIndirectCountsRemainDistinctWithoutRereadingOriginalArguments()
    {
        using var fixture = new Fixture();
        var original = Indexed(400, 0).Indexed_ with { IndirectArgumentsAddress = 0x8000, UnboundedIndexBuffer = true };
        var resolved = Indexed(3, 0).Indexed_ with { IndexAddress = 0x4008, BaseVertex = -3, FirstInstance = 7 };
        Assert.True(fixture.Capture(new(91, true, resolved, default), original));
        Assert.Equal(new (ulong, int)[] { (0x4008UL, 6) }, fixture.Memory.Reads);
        using var result = fixture.Result(); var inputs = result.RootElement.GetProperty("inputs");
        Assert.Equal(400u, inputs.GetProperty("original").GetProperty("Indexed_").GetProperty("IndexCount").GetUInt32());
        Assert.Equal(3u, inputs.GetProperty("boundary").GetProperty("Indexed_").GetProperty("IndexCount").GetUInt32());
        Assert.Equal(0x8000UL, inputs.GetProperty("originalIndirect").GetProperty("IndirectArgumentsAddress").GetUInt64());
        Assert.Equal("already-cpu-resolved-original-preserved", result.RootElement.GetProperty("indirectArguments").GetProperty("Status").GetString());
    }

    [Fact]
    public void GpuIndirectObservationDoesNotResolvePacketOrClaimActualCount()
    {
        using var fixture = new Fixture();
        var draw = Indexed(5, 0) with { Indexed_ = Indexed(5, 0).Indexed_ with { IndirectArgumentsAddress = 0x8000 } };
        Assert.True(fixture.Capture(draw));
        Assert.Equal(new (ulong, int)[] { (0x4000UL, 10), (0x8000UL, 20) }, fixture.Memory.Reads);
        using var result = fixture.Result();
        Assert.False(result.RootElement.GetProperty("completeness").GetProperty("packetCountsKnown").GetBoolean());
        Assert.False(result.RootElement.GetProperty("indices").GetProperty("CountIsPacketResolved").GetBoolean());
        Assert.Equal(5u, result.RootElement.GetProperty("inputs").GetProperty("boundary").GetProperty("Indexed_").GetProperty("IndexCount").GetUInt32());
    }

    [Fact]
    public void AutomaticInputHasOriginalCountsAndNoGuessedPointerReads()
    {
        using var fixture = new Fixture();
        var draw = new TargetlessDrawArguments(91, false, default, new(0x1000, 0x2D, 17, 3, 5, 7, DrawOffsetSource.Packet));
        Assert.True(fixture.Capture(draw)); Assert.Empty(fixture.Memory.Reads);
        using var result = fixture.Result();
        Assert.Equal(17u, result.RootElement.GetProperty("inputs").GetProperty("boundary").GetProperty("Auto").GetProperty("VertexCount").GetUInt32());
        Assert.False(result.RootElement.GetProperty("completeness").GetProperty("fullCpuIndexBytes").GetBoolean());
    }

    [Fact]
    public void FourDrawBudgetConsumesFailedAttemptsWithoutAnotherRead()
    {
        using var fixture = new Fixture(); fixture.Memory.Fail = true;
        for (var i = 0; i < RejectedDrawInputCapture.MaximumDrawsPerExport; i++) Assert.True(fixture.Capture(Indexed(3, 0)));
        Assert.False(fixture.Capture(Indexed(3, 0)));
        Assert.Equal(4, fixture.Memory.Reads.Count); Assert.Equal(4, Directory.GetFiles(fixture.Directory, "*.json").Length);
    }

    [Fact]
    public void EightExportBudgetRefusesNinthBeforeReading()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < RejectedDrawInputCapture.MaximumExports; i++)
        { fixture.Vertex.ExportAddress = (ulong)(0x10000 + i * 256); Assert.True(fixture.Capture(Indexed(1, 0))); }
        fixture.Vertex.ExportAddress = 0x20000; Assert.False(fixture.Capture(Indexed(1, 0)));
        Assert.Equal(8, fixture.Memory.Reads.Count);
    }

    [Fact]
    public void ProvenanceHashChangesWithOriginalPacketCountsEvenWhenCapturedBytesMatch()
    {
        using var fixture = new Fixture(); var draw = Indexed(3, 0);
        Assert.True(fixture.Capture(draw)); using var first = fixture.Result();
        var changed = draw with { Indexed_ = draw.Indexed_ with { InstanceCount = 5 } };
        Assert.True(fixture.Capture(changed)); using var second = fixture.Result(1);
        Assert.Equal(File.ReadAllBytes(fixture.Stem + ".indices.bin"), File.ReadAllBytes(fixture.StemFor(1) + ".indices.bin"));
        Assert.NotEqual(first.RootElement.GetProperty("indices").GetProperty("ProvenanceSha256").GetString(),
            second.RootElement.GetProperty("indices").GetProperty("ProvenanceSha256").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualRejectedPipelineDiagnosticReadsInputsOnlyWithExplicitCapture(bool enabled)
    {
        using var fixture = new Fixture();
        var priorEnabled = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SKIPPED_GRAPHICS");
        var priorDirectory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_DUMP_SKIPPED_GRAPHICS", enabled ? "1" : null);
            Environment.SetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR", fixture.Directory);
            var context = new CpuContext(fixture.Memory, Generation.Gen5);
            var host = new FakePipelineHost(fixture.Memory);
            var registry = new ShaderHeaderRegistry(context, _ => 0, _ => null);
            var cache = new ShaderPipelineCache(context, host, new FakeShaderCompiler(), registry);
            var draw = Indexed(3, 0);
            // A missing original code header stays rejected. Input diagnostics
            // neither create a module nor repair/register a shader program.
            cache.RecordUnsupportedGraphics(fixture.Vertex, fixture.Context, fixture.User, in draw);
            Assert.Empty(host.Modules); Assert.Empty(host.GraphicsPipelines);
            var helper = typeof(ShaderPipelineCache).GetField("_rejectedDrawInputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache);
            if (enabled)
            {
                Assert.NotNull(helper);
                Assert.Equal(new (ulong, int)[] { (0x4000UL, 6) }, fixture.Memory.Reads);
                Assert.True(File.Exists(fixture.Stem + ".json"));
            }
            else
            {
                Assert.Null(helper); Assert.Empty(fixture.Memory.Reads);
                Assert.False(System.IO.Directory.Exists(fixture.Directory));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_DUMP_SKIPPED_GRAPHICS", priorEnabled);
            Environment.SetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR", priorDirectory);
        }
    }

    [Fact]
    public void UnboundedIndexRangeNeverClaimsKnownResolvedPacketCount()
    {
        using var fixture = new Fixture();
        var draw = Indexed(3, 0) with { Indexed_ = Indexed(3, 0).Indexed_ with { UnboundedIndexBuffer = true } };
        Assert.True(fixture.Capture(draw));
        Assert.Equal(new (ulong, int)[] { (0x4000UL, 6) }, fixture.Memory.Reads);
        using var result = fixture.Result();
        Assert.False(result.RootElement.GetProperty("completeness").GetProperty("packetCountsKnown").GetBoolean());
        Assert.False(result.RootElement.GetProperty("indices").GetProperty("CountIsPacketResolved").GetBoolean());
        Assert.False(result.RootElement.GetProperty("inputs").GetProperty("sourceSpans").GetProperty("countIsPacketResolved").GetBoolean());
        Assert.Equal(3u, result.RootElement.GetProperty("inputs").GetProperty("boundary").GetProperty("Indexed_").GetProperty("IndexCount").GetUInt32());
    }

    [Fact]
    public void ReadCallbackCannotRebindOriginalExportFilenameOrProvenance()
    {
        using var fixture = new Fixture(); using var baseline = new Fixture();
        var originalStem = fixture.Stem;
        fixture.Memory.AfterRead = () =>
        {
            fixture.Vertex.ExportAddress = 0x22200;
            fixture.Vertex.ExportResource1 = uint.MaxValue;
        };
        Assert.True(fixture.Capture(Indexed(3, 0)));
        Assert.True(baseline.Capture(Indexed(3, 0)));
        Assert.True(File.Exists(originalStem + ".json"));
        Assert.True(File.Exists(originalStem + ".indices.bin"));
        Assert.False(File.Exists(fixture.Stem + ".json"));
        using var captured = JsonDocument.Parse(File.ReadAllText(originalStem + ".json"));
        using var expected = baseline.Result();
        Assert.Equal(0x10000UL, captured.RootElement.GetProperty("inputs").GetProperty("ExportAddress").GetUInt64());
        Assert.Equal(0u, captured.RootElement.GetProperty("inputs").GetProperty("ExportResource1").GetUInt32());
        Assert.Equal("skipped-export-0000000000010000.json", captured.RootElement.GetProperty("shaderCodeEvidence").GetProperty("file").GetString());
        Assert.Equal(expected.RootElement.GetProperty("sourceMetadataSha256").GetString(),
            captured.RootElement.GetProperty("sourceMetadataSha256").GetString());
        Assert.Equal(expected.RootElement.GetProperty("indices").GetProperty("ProvenanceSha256").GetString(),
            captured.RootElement.GetProperty("indices").GetProperty("ProvenanceSha256").GetString());
        Assert.Equal(new (ulong, int)[] { (0x4000UL, 6) }, fixture.Memory.Reads);
    }

    private static TargetlessDrawArguments Indexed(uint count, uint type) => new(91, true,
        new(0x1000, 0x27, count, 0x4000, type, 2, -1, 0, DrawOffsetSource.Packet), default);
    private sealed class Fixture : IDisposable
    {
        internal readonly RejectedDrawInputCapture CaptureHelper = new();
        internal readonly CaptureMemory Memory = new();
        internal readonly VertexStageRegisters Vertex = new() { ExportAddress = 0x10000 };
        internal readonly ContextRegisters Context = new();
        internal readonly UserConfigRegisters User = new();
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "sharpemu-rejected-input-" + Guid.NewGuid().ToString("N"));
        internal string Stem => StemFor(0);
        internal string StemFor(int ordinal) => Path.Combine(Directory, $"skipped-export-{Vertex.ExportAddress:X16}.draw-{ordinal}");
        internal bool Capture(TargetlessDrawArguments draw, DrawIndexedArguments? original = null) =>
            CaptureHelper.TryCapture(Memory, Vertex, Context, User, in draw, original, Directory);
        internal JsonDocument Result(int ordinal = 0) => JsonDocument.Parse(File.ReadAllText(StemFor(ordinal) + ".json"));
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }
    private sealed class CaptureMemory : ICpuMemory
    {
        internal readonly List<(ulong Address, int Bytes)> Reads = [];
        internal bool Fail, Throw;
        internal Action? AfterRead;
        public bool TryRead(ulong address, Span<byte> bytes)
        { Reads.Add((address, bytes.Length)); bytes.Fill(7); AfterRead?.Invoke(); if (Throw) throw new IOException("original diagnostic read"); return !Fail; }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("Diagnostic must never write guest memory");
    }
}
