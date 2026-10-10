// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The dump a GPU profile line is read with: a hash in a
/// [METAL-V2][SHADER-PROF] line has to name a file pair on disk, and the .gen5
/// listing has to carry the guest PCs the MSL dispatcher's cases refer to.
/// </summary>
public sealed class Gen5MslDumpTests
{
    [Fact]
    public void WritesSourceAndListingNamedByHash()
    {
        var fixture = Gen5ComputeFixtures.All.Single(candidate => candidate.Name == "loop");
        var request = Gen5ComputeFixtures.CreateComputeRequest(fixture);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var directory = Path.Combine(Path.GetTempPath(), "sharpemu-msl-dump-" + Guid.NewGuid().ToString("N"));
        try
        {
            var name = Gen5MslDump.WriteTo(directory, request, shader);
            Assert.Equal($"compute-0x{request.Hash:X16}", name);

            var source = File.ReadAllText(Path.Combine(directory, name + ".metal"));
            Assert.Equal(shader.Source, source);

            var listing = File.ReadAllText(Path.Combine(directory, name + ".gen5"));
            Assert.Contains($"hash=0x{request.Hash:X16}", listing, StringComparison.Ordinal);
            foreach (var instruction in request.Program.Instructions)
            {
                Assert.Contains($"0x{instruction.Pc:X4}  {instruction.Opcode}", listing, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
