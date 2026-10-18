// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Gpu.ShaderCache;

/// <summary>
/// A title's shader seed (user/shader_seeds/&lt;TITLE&gt;.seed): what a fresh install needs
/// to precompile programs before it has drawn them - the vertex/pixel pairs, the draw
/// states each pixel signature was used with and the interpolant-variant votes. The format
/// is upstream's SESD v2 (PR #1074 ShaderSeed), so seeds move between the two:
/// <list type="bullet">
/// <item>u32 magic "SESD", u32 version (2; 1 has no record blob), title and version
/// strings (BinaryWriter, length-prefixed UTF-8), i32 first-variant and i32 second-variant
/// votes.</item>
/// <item>i32 group count; per group u64 pixel signature, i32 state count, per state i32
/// uses and upstream's Vulkan state (u32 color count, 8 i32 color formats, i32 depth and
/// i32 stencil format, 166 bytes of static parameters, i32 output count and 17 bytes each,
/// a bool and six floats of clip space). This build reads and skips them (they are Vulkan
/// formats, not Metal draw states) and writes none.</item>
/// <item>i32 pair count, u64 vertex and u64 pixel program identity each - the same
/// identities as upstream's (XxHash3 of code hash, code size and normalised header).</item>
/// <item>u32 record schema, i32 length, the record blob. A reader ignores a blob of another
/// schema, as upstream does. This build's blob (schema <see cref="ForkSchema"/>): i32
/// count, then per draw state u64 pixel signature (0 for a vertex state), i32 uses, i32
/// length and the record's body as the graphics prewarm list stores it (deflated JSON of
/// a ShaderCorpusRecord, guest code left out).</item>
/// </list>
/// </summary>
internal readonly record struct SeedState(ulong Signature, int Uses, ShaderCorpusRecord Record);

internal sealed class ShaderSeedFile
{
    public const string Extension = ".seed";
    private const uint Magic = 0x44534553;
    private const uint FormatVersion = 2;
    private const uint StatesOnlyFormatVersion = 1;
    private const int UpstreamColorFormats = 8;
    private const int UpstreamStaticParameterBytes = 166;
    private const int UpstreamOutputBytes = 4 + 4 + 4 + 1 + 4;
    private const int UpstreamClipBytes = 1 + 6 * sizeof(float);
    private const int MaxStates = 1 << 16;

    /// <summary>This build's record blob: the graphics prewarm list's schema, so a seed of an
    /// older record layout is not decoded as this one.</summary>
    public static uint ForkSchema { get; } = uint.Parse(
        GraphicsPrewarmList.Stem.AsSpan(GraphicsPrewarmList.Stem.LastIndexOf('-') + 1),
        System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture) ^ 0x4B524F46;

    public string TitleId { get; set; } = string.Empty;

    public string GameVersion { get; set; } = string.Empty;

    public int FirstVariantVotes { get; set; }

    public int SecondVariantVotes { get; set; }

    /// <summary>States upstream's seed held, read and skipped.</summary>
    public int UpstreamStates { get; private set; }

    public HashSet<(ulong Vertex, ulong Pixel)> Pairs { get; } = [];

    /// <summary>Draw states (pixel and vertex records, without code) and how often each was used.</summary>
    public List<SeedState> States { get; } = [];

    public static string PathFor(string userDirectory, string titleId) =>
        Path.Combine(userDirectory, "shader_seeds", titleId + Extension);

    /// <summary>The seed at <paramref name="path"/>, or null when there is none or it does not read.</summary>
    public static ShaderSeedFile? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
            var magic = reader.ReadUInt32();
            var version = reader.ReadUInt32();
            if (magic != Magic || version is not (FormatVersion or StatesOnlyFormatVersion))
            {
                return null;
            }

            var seed = new ShaderSeedFile
            {
                TitleId = reader.ReadString(),
                GameVersion = reader.ReadString(),
                FirstVariantVotes = reader.ReadInt32(),
                SecondVariantVotes = reader.ReadInt32(),
            };
            for (var groups = reader.ReadInt32(); groups > 0; groups--)
            {
                reader.ReadUInt64();
                for (var states = reader.ReadInt32(); states > 0; states--)
                {
                    reader.ReadInt32();
                    SkipUpstreamState(reader);
                    seed.UpstreamStates++;
                }
            }

            for (var pairs = reader.ReadInt32(); pairs > 0; pairs--)
            {
                seed.Pairs.Add((reader.ReadUInt64(), reader.ReadUInt64()));
            }

            if (version == FormatVersion && reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var schema = reader.ReadUInt32();
                var blob = reader.ReadBytes(reader.ReadInt32());
                if (schema == ForkSchema)
                {
                    seed.ReadStates(blob);
                }
            }

            return seed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or
                                              ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader seed {path} could not be read: {exception.Message}");
            return null;
        }
    }

    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(TitleId);
            writer.Write(GameVersion);
            writer.Write(FirstVariantVotes);
            writer.Write(SecondVariantVotes);
            writer.Write(0);
            writer.Write(Pairs.Count);
            foreach (var (vertex, pixel) in Pairs.Order())
            {
                writer.Write(vertex);
                writer.Write(pixel);
            }

            var blob = WriteStates();
            writer.Write(ForkSchema);
            writer.Write(blob.Length);
            writer.Write(blob);
        }

        return stream.ToArray();
    }

    public bool TrySave(string path)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, Serialize());
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader seed {path} could not be written: {exception.Message}");
            return false;
        }
    }

    private static void SkipUpstreamState(BinaryReader reader)
    {
        reader.ReadUInt32();
        reader.BaseStream.Position += (UpstreamColorFormats + 2) * sizeof(int) + UpstreamStaticParameterBytes;
        var outputs = reader.ReadInt32();
        if (outputs is < 0 or > 16)
        {
            throw new InvalidDataException("The seed's state has too many pixel outputs.");
        }

        reader.BaseStream.Position += outputs * UpstreamOutputBytes + UpstreamClipBytes;
        if (reader.BaseStream.Position > reader.BaseStream.Length)
        {
            throw new EndOfStreamException();
        }
    }

    private byte[] WriteStates()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(States.Count);
            foreach (var (signature, uses, record) in States)
            {
                var body = Deflate(ShaderCorpus.SerializeBody(record with { UserData = new uint[record.UserData.Length] }));
                writer.Write(signature);
                writer.Write(uses);
                writer.Write(body.Length);
                writer.Write(body);
            }
        }

        return stream.ToArray();
    }

    private void ReadStates(byte[] blob)
    {
        using var reader = new BinaryReader(new MemoryStream(blob));
        var count = reader.ReadInt32();
        if (count is < 0 or > MaxStates)
        {
            throw new InvalidDataException("The seed has too many states.");
        }

        for (; count > 0; count--)
        {
            var signature = reader.ReadUInt64();
            var uses = reader.ReadInt32();
            var body = reader.ReadBytes(reader.ReadInt32());
            States.Add(new SeedState(signature, uses, ShaderCorpus.DeserializeBody(Inflate(body))));
        }
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] bytes)
    {
        using var input = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
