// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal enum InventorySource : byte
{
    Created = 1,
    Scanned = 2,
}

// One program of the inventory as the index keeps it; the header and code stay on disk.
internal readonly record struct InventoryProgram(
    ulong Identity, ulong Hash, uint CodeSize, AgcShaderType Type, InventorySource Source, long Offset);

internal readonly record struct ScannedFile(string Path, long Size, long WriteTicks, int Programs, int Scanner);

/// <summary>
/// The title's shader inventory: every AGC shader the game created (sceAgcCreateShader) or
/// the scanner found in its files, with its normalised AGC header and its code; the
/// vertex/pixel pairs the game created together or a file stores next to each other; and
/// the files already scanned, so a later scan reads only what changed.
/// </summary>
/// <remarks>
/// pipeline_cache/&lt;TITLE&gt;/shader-inventory-v1.bin, backend-neutral. A 16-byte header
/// ("SINV", format version, reserved) and then records, each [i32 body length][u64 XxHash3
/// of the body][body], so a torn tail from a killed process is cut on open:
/// <list type="bullet">
/// <item>1 program: u64 identity, u64 code hash, u32 code size, u8 AGC type, u8 source
/// (1 created, 2 scanned), i32 header length, header (pointers self-relative, the code
/// pointer zeroed), i32 deflated length, the code and 256 bytes after it, deflated.</item>
/// <item>2 pair: u64 vertex (geometry) identity, u64 pixel identity.</item>
/// <item>3 scanned file: i64 size, i64 last write (UTC ticks), i32 programs found, i32
/// scanner version, the path below app0 (UTF-8).</item>
/// <item>4 created: u64 identity of a scanned program the game created later.</item>
/// </list>
/// The identity is XxHash3 of the code hash, the code size and the header, so the same code
/// under two register setups is two programs and a program the game created is the one
/// the scanner finds. Only the index (about 48 bytes a program) stays in memory; a
/// precompile reads a program's header and code when it gets to it.
/// </remarks>
internal sealed class ShaderInventoryFile : IDisposable
{
    public const string FileName = "shader-inventory-v1.bin";
    public const ulong CodeAddress = 0x10_0000_0000;
    public const int CodeSlackBytes = 256;
    private const uint Magic = 0x564E4953;
    private const uint FormatVersion = 1;
    private const int FileHeaderBytes = 16;
    private const int RecordHeaderBytes = sizeof(int) + sizeof(ulong);
    private const int MaxBodyBytes = 1 << 24;
    private const byte ProgramKind = 1;
    private const byte PairKind = 2;
    private const byte ScannedFileKind = 3;
    private const byte CreatedKind = 4;

    private readonly object _gate = new();
    private readonly FileStream _file;
    private readonly Dictionary<ulong, InventoryProgram> _programs = new();
    private readonly List<ulong> _programOrder = [];
    private readonly HashSet<(ulong Vertex, ulong Pixel)> _pairs = [];
    private readonly List<(ulong Vertex, ulong Pixel)> _pairOrder = [];
    private readonly Dictionary<ulong, List<ulong>> _verticesOf = new();
    private readonly Dictionary<string, ScannedFile> _scanned = new(StringComparer.Ordinal);
    private bool _failed;

    private ShaderInventoryFile(FileStream file, string path)
    {
        _file = file;
        Path = path;
    }

    public string Path { get; }

    public int ProgramCount
    {
        get
        {
            lock (_gate)
            {
                return _programs.Count;
            }
        }
    }

    public int PairCount
    {
        get
        {
            lock (_gate)
            {
                return _pairs.Count;
            }
        }
    }

    public long Length
    {
        get
        {
            lock (_gate)
            {
                return _file.Length;
            }
        }
    }

    public static ShaderInventoryFile? Open(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, FileName);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var file = new ShaderInventoryFile(stream, path);
            try
            {
                file.Load();
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[SHADER CACHE][WARN] Shader inventory unavailable: {exception.Message}");
            return null;
        }
    }

    public static ulong ProgramIdentity(ulong hash, uint codeSize, ReadOnlySpan<byte> header)
    {
        var identity = new XxHash3();
        Span<byte> prefix = stackalloc byte[sizeof(ulong) + sizeof(uint)];
        BinaryPrimitives.WriteUInt64LittleEndian(prefix, hash);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[sizeof(ulong)..], codeSize);
        identity.Append(prefix);
        identity.Append(header);
        return identity.GetCurrentHashAsUInt64();
    }

    public bool Contains(ulong identity)
    {
        lock (_gate)
        {
            return _programs.ContainsKey(identity);
        }
    }

    public bool TryGetProgram(ulong identity, out InventoryProgram program)
    {
        lock (_gate)
        {
            return _programs.TryGetValue(identity, out program);
        }
    }

    public IReadOnlyList<InventoryProgram> Programs()
    {
        lock (_gate)
        {
            return _programOrder.Select(identity => _programs[identity]).ToArray();
        }
    }

    public IReadOnlyList<(ulong Vertex, ulong Pixel)> Pairs()
    {
        lock (_gate)
        {
            return _pairOrder.ToArray();
        }
    }

    /// <summary>The geometry programs paired with <paramref name="pixel"/>, in the order they were paired.</summary>
    public IReadOnlyList<ulong> VerticesOf(ulong pixel)
    {
        lock (_gate)
        {
            return _verticesOf.TryGetValue(pixel, out var vertices) ? vertices.ToArray() : [];
        }
    }

    public bool TryGetScannedFile(string path, out ScannedFile file)
    {
        lock (_gate)
        {
            return _scanned.TryGetValue(path, out file);
        }
    }

    /// <summary>Adds a program unless it is known; returns its identity either way.</summary>
    public ulong RecordProgram(AgcShaderHeader header, ulong hash, uint codeSize, ReadOnlySpan<byte> code, InventorySource source)
    {
        var identity = ProgramIdentity(hash, codeSize, header.Bytes);
        lock (_gate)
        {
            if (_programs.TryGetValue(identity, out var known) &&
                known.Source == InventorySource.Scanned && source == InventorySource.Created && !_failed)
            {
                // The game uses it: the precompile takes it before the programs only found.
                var mark = new byte[1 + sizeof(ulong)];
                mark[0] = CreatedKind;
                BinaryPrimitives.WriteUInt64LittleEndian(mark.AsSpan(1), identity);
                Append(mark);
                _programs[identity] = known with { Source = InventorySource.Created };
            }

            if (_programs.ContainsKey(identity) || _failed)
            {
                return identity;
            }
        }

        var deflated = Deflate(code);
        var body = new byte[1 + 8 + 8 + 4 + 1 + 1 + 4 + header.Bytes.Length + 4 + deflated.Length];
        var span = body.AsSpan();
        span[0] = ProgramKind;
        BinaryPrimitives.WriteUInt64LittleEndian(span[1..], identity);
        BinaryPrimitives.WriteUInt64LittleEndian(span[9..], hash);
        BinaryPrimitives.WriteUInt32LittleEndian(span[17..], codeSize);
        span[21] = (byte)header.Type;
        span[22] = (byte)source;
        BinaryPrimitives.WriteInt32LittleEndian(span[23..], header.Bytes.Length);
        header.Bytes.CopyTo(span[27..]);
        var at = 27 + header.Bytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], deflated.Length);
        deflated.CopyTo(span[(at + 4)..]);
        lock (_gate)
        {
            if (_programs.ContainsKey(identity))
            {
                return identity;
            }

            if (Append(body) is { } offset)
            {
                _programs[identity] = new InventoryProgram(identity, hash, codeSize, header.Type, source, offset);
                _programOrder.Add(identity);
            }
        }

        return identity;
    }

    public void RecordPair(ulong vertex, ulong pixel)
    {
        lock (_gate)
        {
            if (!_pairs.Add((vertex, pixel)))
            {
                return;
            }

            _pairOrder.Add((vertex, pixel));
            IndexPair((vertex, pixel));
            var body = new byte[1 + 2 * sizeof(ulong)];
            body[0] = PairKind;
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(1), vertex);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(9), pixel);
            Append(body);
        }
    }

    public void RecordScannedFile(ScannedFile scanned)
    {
        var path = Encoding.UTF8.GetBytes(scanned.Path);
        var body = new byte[1 + 8 + 8 + 4 + 4 + path.Length];
        body[0] = ScannedFileKind;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(1), scanned.Size);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(9), scanned.WriteTicks);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(17), scanned.Programs);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(21), scanned.Scanner);
        path.CopyTo(body.AsSpan(25));
        lock (_gate)
        {
            _scanned[scanned.Path] = scanned;
            Append(body);
        }
    }

    /// <summary>Reads a program's header back, without inflating its code.</summary>
    public bool TryReadHeader(ulong identity, out AgcShaderHeader header)
    {
        header = null!;
        byte[] body;
        lock (_gate)
        {
            if (!_programs.TryGetValue(identity, out var program) || ReadBody(program.Offset) is not { } read)
            {
                return false;
            }

            body = read;
        }

        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(23));
        return headerLength > 0 && 27 + headerLength <= body.Length &&
            AgcShaderHeader.TryParse(body.AsSpan(27, headerLength), 0, out header);
    }

    /// <summary>Reads a program's header and code back; the code sits at <see cref="CodeAddress"/>.</summary>
    public bool TryRead(ulong identity, out AgcShaderHeader header, out ShaderCodeCapture code)
    {
        header = null!;
        code = null!;
        InventoryProgram program;
        byte[] body;
        lock (_gate)
        {
            if (!_programs.TryGetValue(identity, out program) || ReadBody(program.Offset) is not { } read)
            {
                return false;
            }

            body = read;
        }

        try
        {
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(23));
            var at = 27 + headerLength;
            var deflatedLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));
            var bytes = Inflate(body.AsSpan(at + 4, deflatedLength));
            if (!AgcShaderHeader.TryParse(body.AsSpan(27, headerLength), 0, out header) || bytes.Length < program.CodeSize)
            {
                return false;
            }

            code = new ShaderCodeCapture
            {
                Hash = program.Hash,
                CodeSize = program.CodeSize,
                Address = CodeAddress,
                Generation = Generation.Gen5,
                Ranges = [new CodeRange(CodeAddress, bytes)],
            };
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file.Dispose();
        }
    }

    private void Load()
    {
        var length = _file.Length;
        Span<byte> fileHeader = stackalloc byte[FileHeaderBytes];
        if (length < FileHeaderBytes ||
            _file.ReadAtLeast(fileHeader, FileHeaderBytes, throwOnEndOfStream: false) < FileHeaderBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(fileHeader) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(fileHeader[4..]) != FormatVersion)
        {
            _file.SetLength(0);
            fileHeader.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(fileHeader, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(fileHeader[4..], FormatVersion);
            _file.Position = 0;
            _file.Write(fileHeader);
            _file.Flush();
            return;
        }

        var position = (long)FileHeaderBytes;
        Span<byte> recordHeader = stackalloc byte[RecordHeaderBytes];
        var buffer = new byte[4096];
        while (position + RecordHeaderBytes <= length)
        {
            _file.Position = position;
            if (_file.ReadAtLeast(recordHeader, RecordHeaderBytes, throwOnEndOfStream: false) < RecordHeaderBytes)
            {
                break;
            }

            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(recordHeader);
            var checksum = BinaryPrimitives.ReadUInt64LittleEndian(recordHeader[sizeof(int)..]);
            if (bodyLength <= 0 || bodyLength > MaxBodyBytes || position + RecordHeaderBytes + bodyLength > length)
            {
                break;
            }

            if (buffer.Length < bodyLength)
            {
                buffer = new byte[Math.Max(bodyLength, buffer.Length * 2)];
            }

            var body = buffer.AsSpan(0, bodyLength);
            if (_file.ReadAtLeast(body, bodyLength, throwOnEndOfStream: false) < bodyLength || XxHash3.HashToUInt64(body) != checksum ||
                !Index(body, position))
            {
                break;
            }

            position += RecordHeaderBytes + bodyLength;
        }

        if (position < length)
        {
            Console.Error.WriteLine($"[SHADER CACHE][WARN] Shader inventory: cut a damaged tail of {length - position} bytes.");
            _file.SetLength(position);
        }
    }

    private bool Index(ReadOnlySpan<byte> body, long offset)
    {
        switch (body[0])
        {
            case ProgramKind when body.Length >= 27:
            {
                var identity = BinaryPrimitives.ReadUInt64LittleEndian(body[1..]);
                if (_programs.TryAdd(identity, new InventoryProgram(
                        identity,
                        BinaryPrimitives.ReadUInt64LittleEndian(body[9..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(body[17..]),
                        (AgcShaderType)body[21],
                        (InventorySource)body[22],
                        offset)))
                {
                    _programOrder.Add(identity);
                }

                return true;
            }

            case PairKind when body.Length >= 17:
            {
                var pair = (BinaryPrimitives.ReadUInt64LittleEndian(body[1..]), BinaryPrimitives.ReadUInt64LittleEndian(body[9..]));
                if (_pairs.Add(pair))
                {
                    _pairOrder.Add(pair);
                    IndexPair(pair);
                }

                return true;
            }

            case CreatedKind when body.Length >= 9:
            {
                var identity = BinaryPrimitives.ReadUInt64LittleEndian(body[1..]);
                if (_programs.TryGetValue(identity, out var known))
                {
                    _programs[identity] = known with { Source = InventorySource.Created };
                }

                return true;
            }

            case ScannedFileKind when body.Length >= 25:
            {
                var path = Encoding.UTF8.GetString(body[25..]);
                _scanned[path] = new ScannedFile(
                    path,
                    BinaryPrimitives.ReadInt64LittleEndian(body[1..]),
                    BinaryPrimitives.ReadInt64LittleEndian(body[9..]),
                    BinaryPrimitives.ReadInt32LittleEndian(body[17..]),
                    BinaryPrimitives.ReadInt32LittleEndian(body[21..]));
                return true;
            }

            default:
                return false;
        }
    }

    // Under _gate. Returns where the record starts, or null once writing has failed.
    private void IndexPair((ulong Vertex, ulong Pixel) pair)
    {
        if (!_verticesOf.TryGetValue(pair.Pixel, out var vertices))
        {
            _verticesOf[pair.Pixel] = vertices = [];
        }

        vertices.Add(pair.Vertex);
    }

    private long? Append(byte[] body)
    {
        if (_failed)
        {
            return null;
        }

        try
        {
            var offset = _file.Length;
            Span<byte> recordHeader = stackalloc byte[RecordHeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(recordHeader, body.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(recordHeader[sizeof(int)..], XxHash3.HashToUInt64(body));
            _file.Position = offset;
            _file.Write(recordHeader);
            _file.Write(body);
            _file.Flush();
            return offset;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _failed = true;
            Console.Error.WriteLine($"[SHADER CACHE][WARN] Shader inventory stopped recording: {exception.Message}");
            return null;
        }
    }

    // Under _gate.
    private byte[]? ReadBody(long offset)
    {
        try
        {
            Span<byte> recordHeader = stackalloc byte[RecordHeaderBytes];
            _file.Position = offset;
            if (_file.ReadAtLeast(recordHeader, RecordHeaderBytes, throwOnEndOfStream: false) < RecordHeaderBytes)
            {
                return null;
            }

            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(recordHeader);
            if (bodyLength <= 0 || bodyLength > MaxBodyBytes)
            {
                return null;
            }

            var body = new byte[bodyLength];
            return _file.ReadAtLeast(body, bodyLength, throwOnEndOfStream: false) == bodyLength ? body : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }
}
