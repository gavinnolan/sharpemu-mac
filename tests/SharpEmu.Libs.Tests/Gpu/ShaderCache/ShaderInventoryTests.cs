// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.ShaderCache;

// The shader inventory, its scanner and the title seed: AGC headers parse and normalise as
// upstream's (PR #1074 tests), what is recorded reads back after a reopen, and a seed
// round-trips and reads upstream's layout.
public sealed class ShaderInventoryTests : IDisposable
{
    private const byte ComputeType = 0;
    private const byte PixelType = 1;
    private const byte GeometryType = 2;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static byte[] ProgramCode => AgcHeaderBuilder.Code(PipelineTestGuest.FormatLoadProgram);

    private ShaderInventoryFile Open() => ShaderInventoryFile.Open(_directory) ?? throw new InvalidOperationException("The inventory did not open.");

    [Fact]
    public void AnAgcHeaderExposesItsRegistersSemanticsAndDispatchModifier()
    {
        var bytes = AgcHeaderBuilder.Build(
            ComputeType, 12,
            shaderRegisters: [(0x207, 64), (0x213, 0x90)],
            contextRegisters: [(0x1B3, 2)],
            inputSemantics: [0x11, 0x22],
            outputSemantics: [0x33],
            dispatchModifier: 0x8000);

        Assert.True(AgcShaderHeader.TryParse(bytes, 0, out var header));
        Assert.Equal(AgcShaderType.Compute, header.Type);
        Assert.Equal(12u, header.CodeSize);
        Assert.Equal([new AgcRegister(0x207, 64), new AgcRegister(0x213, 0x90)], header.ShaderRegisters);
        Assert.Equal([new AgcRegister(0x1B3, 2)], header.ContextRegisters);
        Assert.Equal(new uint[] { 0x11, 0x22 }, header.InputSemantics);
        Assert.Equal(new uint[] { 0x33 }, header.OutputSemantics);
        Assert.Equal(0x8000u, header.DispatchModifier);
    }

    [Fact]
    public void ARelocatedHeaderNormalizesToTheFileLayout()
    {
        const ulong headerAddress = 0x7_0000_1000;
        var bytes = AgcHeaderBuilder.Build(PixelType, 12, contextRegisters: [(0x1B3, 2)], inputSemantics: [0x11]);
        var relocated = AgcHeaderBuilder.WithAbsolutePointers(bytes, headerAddress);

        Assert.True(AgcShaderHeader.TryParse(relocated, headerAddress, out var header));
        Assert.True(AgcShaderHeader.TryParse(bytes, 0, out var original));
        Assert.Equal(original.Bytes, header.Bytes);
    }

    [Fact]
    public void AHeaderWithAForeignSignatureOrSizeIsRejected()
    {
        var bytes = AgcHeaderBuilder.Build(ComputeType, 12);
        bytes[0] = 0;
        Assert.False(AgcShaderHeader.TryParse(bytes, 0, out _));
        var wrongSize = AgcHeaderBuilder.Build(ComputeType, 12);
        Assert.False(AgcShaderHeader.TryParse(wrongSize.AsSpan(0, wrongSize.Length - 4), 0, out _));
    }

    [Fact]
    public void TheScannerFindsCodeBeforeEachHeaderPairsAdjacentStagesAndSkipsWhatItRead()
    {
        var game = Path.Combine(_directory, "game");
        Directory.CreateDirectory(game);
        var code = ProgramCode;
        var bundle = new MemoryStream();
        foreach (var (type, semantics) in new[] { (ComputeType, 0u), (GeometryType, 1u), (PixelType, 2u) })
        {
            bundle.Write(code);
            bundle.Write(AgcHeaderBuilder.Build(type, code.Length,
                inputSemantics: type == PixelType ? [semantics] : [], outputSemantics: type == GeometryType ? [semantics] : []));
        }

        File.WriteAllBytes(Path.Combine(game, "shaders.csdr"), bundle.ToArray());
        File.WriteAllBytes(Path.Combine(game, "movie.mp4"), bundle.ToArray());
        File.WriteAllBytes(Path.Combine(game, "level.dat"), bundle.ToArray());
        using (var file = Open())
        {
            // The default scan reads the files that name shaders only.
            var result = GameShaderScanner.Scan(game, file, 2, null, CancellationToken.None, GameShaderScanner.IsLikelyShaderFile);
            Assert.Equal(1, result.FilesScanned);
            Assert.Equal(3, result.Programs);
            Assert.Equal(3, file.ProgramCount);
            var (vertex, pixel) = Assert.Single(file.Pairs());
            Assert.Equal([vertex], file.VerticesOf(pixel));
            Assert.True(file.TryReadHeader(vertex, out var vertexHeader));
            Assert.Equal(AgcShaderType.Geometry, vertexHeader.Type);
            Assert.True(file.TryRead(pixel, out var pixelHeader, out var pixelCode));
            Assert.Equal(AgcShaderType.Pixel, pixelHeader.Type);
            Assert.Equal(code, pixelCode.Ranges[0].Bytes.AsSpan(0, code.Length).ToArray());
        }

        using var reopened = Open();
        var again = GameShaderScanner.Scan(game, reopened, 1, null, CancellationToken.None);
        Assert.Equal(1, again.FilesScanned);
        Assert.Equal(1, again.FilesSkipped);
        Assert.Equal(0, again.Programs);
        Assert.Equal(3, reopened.ProgramCount);
        Assert.Single(reopened.Pairs());
    }

    [Fact]
    public void AScannedProgramTheGameCreatesLaterIsMarkedCreatedAcrossAReopen()
    {
        var code = ProgramCode;
        Assert.True(AgcShaderHeader.TryParse(AgcHeaderBuilder.Build(ComputeType, code.Length), 0, out var header));
        Assert.True(ShaderInventory.TryHash(code, (uint)code.Length, ShaderInventoryFile.CodeAddress, out var hash));
        ulong identity;
        using (var file = Open())
        {
            identity = file.RecordProgram(header, hash, (uint)code.Length, code, InventorySource.Scanned);
            Assert.Equal(identity, file.RecordProgram(header, hash, (uint)code.Length, code, InventorySource.Created));
            Assert.True(file.TryGetProgram(identity, out var program));
            Assert.Equal(InventorySource.Created, program.Source);
        }

        using var reopened = Open();
        Assert.True(reopened.TryGetProgram(identity, out var again));
        Assert.Equal(InventorySource.Created, again.Source);
        Assert.Equal(1, reopened.ProgramCount);
    }

    [Fact]
    public void ATornTailIsCutAndTheFileKeepsAppending()
    {
        var code = ProgramCode;
        Assert.True(AgcShaderHeader.TryParse(AgcHeaderBuilder.Build(ComputeType, code.Length), 0, out var header));
        Assert.True(ShaderInventory.TryHash(code, (uint)code.Length, ShaderInventoryFile.CodeAddress, out var hash));
        string path;
        using (var file = Open())
        {
            file.RecordProgram(header, hash, (uint)code.Length, code, InventorySource.Scanned);
            file.RecordPair(1, 2);
            path = file.Path;
        }

        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 3).ToArray());
        using (var file = Open())
        {
            Assert.Equal(1, file.ProgramCount);
            Assert.Empty(file.Pairs());
            file.RecordPair(3, 4);
        }

        using var reopened = Open();
        Assert.Equal([(3UL, 4UL)], reopened.Pairs());
    }

    [Fact]
    public void ASeedRoundTripsItsPairsVotesAndStates()
    {
        var seed = new ShaderSeedFile { TitleId = "PPSA01342", GameVersion = "01.000", FirstVariantVotes = 1, SecondVariantVotes = 7 };
        seed.Pairs.Add((10, 20));
        seed.Pairs.Add((11, 21));
        var state = new ShaderCorpusRecord
        {
            Stage = ShaderStage.Pixel,
            Hash = 0x1234,
            Registered = new RegisteredShader(0, 0, 0, 0, 0, 0, 0, 0, 0),
            UserData = [5, 6],
            Options = new StageCompileOptions(),
        };
        seed.States.Add(new SeedState(0xABCD, 3, state));
        var path = ShaderSeedFile.PathFor(_directory, "PPSA01342");
        Assert.True(seed.TrySave(path));

        var loaded = ShaderSeedFile.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal("PPSA01342", loaded.TitleId);
        Assert.Equal("01.000", loaded.GameVersion);
        Assert.Equal((1, 7), (loaded.FirstVariantVotes, loaded.SecondVariantVotes));
        Assert.Equal(seed.Pairs.Order(), loaded.Pairs.Order());
        var (signature, uses, record) = Assert.Single(loaded.States);
        Assert.Equal((0xABCDUL, 3), (signature, uses));
        Assert.Equal(ShaderStage.Pixel, record.Stage);
        Assert.Equal(0x1234UL, record.Hash);
        // The user data's values are a draw's; a seed keeps their count only.
        Assert.Equal(new uint[2], record.UserData);
    }

    [Fact]
    public void AnUpstreamSeedGivesItsPairsAndVotesAndItsVulkanStatesAreSkipped()
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x44534553u);
            writer.Write(2u);
            writer.Write("PPSA01342");
            writer.Write("01.000");
            writer.Write(2);
            writer.Write(9);
            writer.Write(1);
            writer.Write(0x55UL);
            writer.Write(1);
            writer.Write(4);
            // Upstream's Vulkan state: color count, 8 formats, depth, stencil, 166 bytes of
            // static parameters, one output, the clip space.
            writer.Write(1u);
            for (var index = 0; index < 10; index++)
            {
                writer.Write(37);
            }

            writer.Write(new byte[166]);
            writer.Write(1);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0);
            writer.Write((byte)0xE4);
            writer.Write(0u);
            writer.Write(true);
            for (var index = 0; index < 6; index++)
            {
                writer.Write(1.0f);
            }

            writer.Write(1);
            writer.Write(0x77UL);
            writer.Write(0x88UL);
            writer.Write(0xDEADBEEFu);
            writer.Write(3);
            writer.Write(new byte[] { 1, 2, 3 });
        }

        var path = Path.Combine(_directory, "upstream.seed");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(path, stream.ToArray());
        var loaded = ShaderSeedFile.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal((2, 9), (loaded.FirstVariantVotes, loaded.SecondVariantVotes));
        Assert.Equal(1, loaded.UpstreamStates);
        Assert.Equal([(0x77UL, 0x88UL)], loaded.Pairs);
        Assert.Empty(loaded.States);
    }
}
