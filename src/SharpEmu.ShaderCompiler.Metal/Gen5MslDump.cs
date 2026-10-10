// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

/// <summary>
/// Writes a translated program to disk so a profiler's shader hash can be read back as
/// source. <c>SHARPEMU_SHADER_MSL_DUMP_DIR</c> names the directory; every compiled
/// permutation lands there as <c>&lt;stage&gt;-&lt;hash&gt;.metal</c> beside a
/// <c>&lt;stage&gt;-&lt;hash&gt;.gen5</c> listing of the guest instructions it came from,
/// which is what identifies a mistranslated loop. <c>SHARPEMU_SHADER_MSL_DUMP_HASH</c>
/// narrows the dump to one comma-separated list of hashes.
/// </summary>
public static class Gen5MslDump
{
    private static readonly string? _directory =
        Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MSL_DUMP_DIR") is { Length: > 0 } value ? value : null;

    private static readonly HashSet<ulong> _hashes = ParseHashes(
        Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MSL_DUMP_HASH"));

    private static readonly object _gate = new();
    private static bool _failed;

    /// <summary>Whether any program is being dumped at all.</summary>
    public static bool Enabled => _directory is not null;

    /// <summary>Writes one compiled permutation; never throws and never runs twice for a hash.</summary>
    public static void Write(ShaderCompileRequest request, Gen5MslShader shader)
    {
        if (_directory is null || _failed)
        {
            return;
        }

        if (_hashes.Count != 0 && !_hashes.Contains(request.Hash))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                WriteTo(_directory, request, shader);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failed = true;
                Console.Error.WriteLine($"[MSL-DUMP] cannot write to {_directory}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Writes the pair of files for one permutation into a directory and returns the
    /// base name they share. An existing pair is left alone: the first permutation
    /// compiled for a hash is the one a profile line refers to.
    /// </summary>
    public static string WriteTo(string directory, ShaderCompileRequest request, Gen5MslShader shader)
    {
        var name = $"{shader.Stage.ToString().ToLowerInvariant()}-0x{request.Hash:X16}";
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, name + ".metal");
        if (!File.Exists(source))
        {
            File.WriteAllText(source, shader.Source);
            File.WriteAllText(Path.Combine(directory, name + ".gen5"), Listing(request, shader));
        }

        return name;
    }

    // The guest program as the decoder saw it: one line per instruction, so a PC in the
    // dispatcher's "case" comments maps straight back onto an opcode and its operands.
    private static string Listing(ShaderCompileRequest request, Gen5MslShader shader)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(string.Create(
            culture,
            $"; hash=0x{request.Hash:X16} stage={shader.Stage} entry={shader.EntryPoint}"));
        text.AppendLine(string.Create(
            culture,
            $"; address=0x{request.Program.Address:X} instructions={request.Program.Instructions.Count}"));
        text.AppendLine(string.Create(
            culture,
            $"; threadgroup={shader.ThreadgroupSizeX}x{shader.ThreadgroupSizeY}x{shader.ThreadgroupSizeZ} " +
            $"attributes={shader.AttributeCount}"));
        if (shader.MeshInfo is { } mesh)
        {
            text.AppendLine(string.Create(
                culture,
                $"; mesh threads={mesh.ThreadsPerThreadgroup} waves={mesh.WavesPerThreadgroup} " +
                $"max_vertices={mesh.MaxVertices} max_primitives={mesh.MaxPrimitives} topology={mesh.Topology}"));
        }

        foreach (var instruction in request.Program.Instructions)
        {
            text.Append(string.Create(culture, $"0x{instruction.Pc:X4}  {instruction.Opcode,-24}"));
            for (var index = 0; index < instruction.Destinations.Count; index++)
            {
                text.Append(index == 0 ? " " : ", ");
                text.Append(instruction.Destinations[index].ToString());
            }

            if (instruction.Destinations.Count != 0 && instruction.Sources.Count != 0)
            {
                text.Append(" <-");
            }

            for (var index = 0; index < instruction.Sources.Count; index++)
            {
                text.Append(index == 0 ? " " : ", ");
                text.Append(instruction.Sources[index].ToString());
            }

            if (instruction.Control is { } control)
            {
                text.Append("  ").Append(control.ToString());
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static HashSet<ulong> ParseHashes(string? value)
    {
        var hashes = new HashSet<ulong>();
        if (value is null)
        {
            return hashes;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
            {
                hashes.Add(hash);
            }
        }

        return hashes;
    }
}
