// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// SHARPEMU_SHADER_CORPUS_DIR: a program is on disk before its translation, and replays to the same request.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderCorpusTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong BufferAddress = PipelineTestGuest.MemoryBase + 0x4_0000;
    private const uint Format32x4Float = 77;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
    private readonly FatalScope _fatal = new();

    public ShaderCorpusTests() => ShaderCorpus.UseDirectory(_directory);

    public void Dispose()
    {
        ShaderCorpus.UseDirectory(null);
        _fatal.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string[] Files() =>
        Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*" + ShaderCorpus.FileExtension).Order().ToArray() : [];

    [Fact]
    public void AComputeProgramIsRecordedBeforeItsTranslationAndReplaysToTheSameMsl()
    {
        var recorded = new List<string>();
        var guest = new PipelineTestGuest(request =>
        {
            // The record is on disk by the time the translator is asked.
            Assert.Single(Files());
            Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
            recorded.Add(shader.Source);
            return Encoding.UTF8.GetBytes(shader.Source);
        });
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var userData = PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, BufferDescriptorWordsFormat32UInt);
        var cursor = 0u;
        var source = guest.Source(CodeAddress, ShaderStage.Compute, userData);
        var options = PipelineTestGuest.ComputeOptions(threadsX: 64);
        guest.Programs.GetOrCompile(source, options, ref cursor, out _);

        var file = Assert.Single(Files());
        Assert.StartsWith($"compute-{source.Hash:X16}-", Path.GetFileName(file));

        // A second draw of the same program does not write it again.
        var again = 0u;
        guest.Programs.GetOrCompile(source, options, ref again, out _);
        Assert.Single(Files());

        var record = ShaderCorpus.Load(file);
        Assert.NotNull(record.Specialization);
        Assert.True(ShaderCorpus.TryBuildRequest(record, out var request, out var replayError), replayError);
        Assert.NotNull(request);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var replayed, out var error), error);
        Assert.Equal(Assert.Single(recorded), replayed.Source);
    }

    [Fact]
    public void AVertexProgramKeepsItsFetchStateAndReplaysToTheSameRequest()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress,
            [0xF4080303, 0xFA000000, 0x02020B08, 0xE00C2000, 0x80030401, 0xBF810000]);
        var source = guest.Source(CodeAddress, ShaderStage.Vertex, new uint[8]);
        var descriptor = PipelineTestGuest.BufferDescriptor(BufferAddress, 16, 4, Format32x4Float);
        var options = new StageCompileOptions
        {
            VertexInfo = new VertexInputInfo
            {
                FetchEmbedded = true,
                FetchAttributeRegister = 4,
                FetchBufferRegister = 6,
                Attributes = [new VertexAttributeResource(new(descriptor[0], descriptor[1], descriptor[2], descriptor[3]), 4, 4, 0, 0, 0, 0)],
                Buffers = [new VertexInputBuffer(BufferAddress, 16, 4)],
                ClipSpace = new ClipSpaceTransform(true, 1, -1, 0.5f, 0.5f, 2, 2) { PositionYDown = true },
            },
            RequiredVertexOutputCount = 2,
        };
        var cursor = 0u;
        guest.Programs.GetOrCompile(source, options, ref cursor, out _);

        var live = Assert.Single(guest.Compiler.Requests);
        var record = ShaderCorpus.Load(Assert.Single(Files()));
        Assert.True(ShaderCorpus.TryBuildRequest(record, out var request, out var error), error);
        Assert.NotNull(request);
        Assert.Equal(live.Stage, request.Stage);
        Assert.Equal(live.RequiredVertexOutputCount, request.RequiredVertexOutputCount);
        Assert.Equal(live.ClipSpace, request.ClipSpace);
        Assert.Equal(live.VertexInputs.Count, request.VertexInputs.Count);
        Assert.Equal(live.Program.Instructions.Count, request.Program.Instructions.Count);
        Assert.Equal(live.Resources.Info.Buffers.Count, request.Resources.Info.Buffers.Count);
    }

    [Fact]
    public void AProgramThatDoesNotDecodeLeavesItsBytesAndFailsTheReplayWithTheDecodeError()
    {
        var guest = new PipelineTestGuest();
        // An S_NOP with no S_ENDPGM before the end of memory: the decoder runs off the end looking for it.
        const ulong unterminated = PipelineTestGuest.MemoryBase + PipelineTestGuest.MemorySize - 4 * sizeof(uint);
        guest.RegisterProgram(unterminated, HeaderAddress, [0xBF800000]);
        var source = guest.Source(unterminated, ShaderStage.Compute, []);
        var cursor = 0u;
        Assert.ThrowsAny<Exception>(() =>
            guest.Programs.GetOrCompile(source, PipelineTestGuest.ComputeOptions(), ref cursor, out _));

        var file = Assert.Single(Files());
        Assert.EndsWith("-decode.corpus", file);
        var record = ShaderCorpus.Load(file);
        Assert.Null(record.Options);
        Assert.False(ShaderCorpus.TryBuildRequest(record, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void AnUnwritableDirectoryNeverFailsTheCompile()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "a file where the directory should be");
        try
        {
            ShaderCorpus.UseDirectory(Path.Combine(blocker, "corpus"));
            var guest = new PipelineTestGuest();
            guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
            var userData = PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, BufferDescriptorWordsFormat32UInt);
            var cursor = 0u;
            guest.Programs.GetOrCompile(
                guest.Source(CodeAddress, ShaderStage.Compute, userData), PipelineTestGuest.ComputeOptions(), ref cursor, out _);
            Assert.Single(guest.Compiler.Requests);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    private const uint BufferDescriptorWordsFormat32UInt = 20;
}
