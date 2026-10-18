// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.ShaderCache;

/// <summary>
/// The shaders the game creates (sceAgcCreateShader), captured before the call relocates the
/// header so it is the form the game files store, and recorded in the title's inventory on
/// a background thread. A geometry program followed by a pixel program on one thread is a
/// pair. A backend that attaches a callback hears of every new program once it is recorded,
/// so it can compile it ahead of its first draw or dispatch.
/// </summary>
internal static class ShaderInventory
{
    // What waits for the inventory to open (the presenter starts after the game created
    // its first shaders) is bounded by bytes, not by count; a backend that never attaches
    // (not Metal V2) holds at most this much.
    private const long MaxPendingBytes = 32L << 20;

    private sealed class Pending
    {
        public required byte[] Header { get; init; }
        public required byte[] Code { get; init; }
        public required ulong CodeAddress { get; init; }
        public required ulong HeaderAddress { get; init; }
        public required uint CodeSize { get; init; }
        public Pending? Vertex { get; set; }
        public ulong Identity { get; set; }
    }

    private static readonly object _gate = new();
    private static readonly Queue<Pending> _pending = new();
    private static ShaderInventoryFile? _file;
    private static Action<InventoryProgram, bool>? _recorded;
    private static Thread? _thread;
    private static long _pendingBytes;
    private static long _dropped;
    private static long _captured;
    private static volatile bool _disabled = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_INVENTORY") == "0" ||
        Environment.GetEnvironmentVariable("SHARPEMU_SHADER_CACHE")?.Trim().ToLowerInvariant() is "0" or "off" or "false" or "no" or "disabled";

    [ThreadStatic]
    private static Pending? _lastVertex;

    public static long Captured => Interlocked.Read(ref _captured);

    public static long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Stops capturing (a backend that has no use for it, or the user's choice).</summary>
    public static void Disable()
    {
        _disabled = true;
        lock (_gate)
        {
            _pending.Clear();
            _pendingBytes = 0;
        }
    }

    /// <summary>
    /// Records what was captured so far and everything after into <paramref name="file"/>;
    /// <paramref name="recorded"/> hears of each program (true when it was new to the file).
    /// </summary>
    public static void Attach(ShaderInventoryFile file, Action<InventoryProgram, bool>? recorded)
    {
        lock (_gate)
        {
            _file = file;
            _recorded = recorded;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "SharpEmu shader inventory", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }

            Monitor.PulseAll(_gate);
        }
    }

    public static void Detach(ShaderInventoryFile file)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_file, file))
            {
                _file = null;
                _recorded = null;
            }
        }
    }

    public static void Capture(CpuContext context, ulong headerAddress, ulong codeAddress)
    {
        if (_disabled)
        {
            return;
        }

        Span<byte> head = stackalloc byte[AgcShaderHeader.FixedBytes];
        if (!context.Memory.TryRead(headerAddress, head) ||
            !AgcShaderHeader.TryPeekSizes(head, out var headerBytes, out var codeBytes, out var type) ||
            !AgcShaderHeader.IsSupported(type))
        {
            return;
        }

        var header = new byte[headerBytes];
        var code = new byte[codeBytes + ShaderInventoryFile.CodeSlackBytes];
        if (!context.Memory.TryRead(headerAddress, header) ||
            (!context.Memory.TryRead(codeAddress, code) && !context.Memory.TryRead(codeAddress, code.AsSpan(0, codeBytes))))
        {
            return;
        }

        var pending = new Pending
        {
            Header = header,
            Code = code,
            CodeAddress = codeAddress,
            HeaderAddress = headerAddress,
            CodeSize = (uint)codeBytes,
        };
        switch ((AgcShaderType)type)
        {
            case AgcShaderType.Geometry:
                _lastVertex = pending;
                break;
            case AgcShaderType.Pixel:
                pending.Vertex = _lastVertex;
                _lastVertex = null;
                break;
        }

        Interlocked.Increment(ref _captured);
        lock (_gate)
        {
            var bytes = header.Length + code.Length;
            if (_pendingBytes + bytes > MaxPendingBytes)
            {
                _dropped++;
                return;
            }

            _pendingBytes += bytes;
            _pending.Enqueue(pending);
            Monitor.PulseAll(_gate);
        }
    }

    public static bool TryHash(ReadOnlySpan<byte> code, uint codeSize, ulong codeAddress, out ulong hash)
    {
        hash = 0;
        if (code.Length < codeSize)
        {
            return false;
        }

        var replay = new ReplayCpuMemory([new CodeRange(codeAddress, code.ToArray())]);
        if (!ShaderIdentity.TryReadDeclaredHash(replay, codeAddress, out var declared))
        {
            return false;
        }

        hash = declared != 0 ? declared : XxHash3.HashToUInt64(code[..(int)codeSize]);
        return true;
    }

    /// <summary>The code decodes as a program at <paramref name="codeAddress"/>.</summary>
    public static bool Decodes(ReadOnlySpan<byte> code, ulong codeAddress)
    {
        var range = new CodeRange(codeAddress, code.ToArray());
        return Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new ReplayCpuMemory([range]), Generation.Gen5), codeAddress, out _, out _);
    }

    private static void Run()
    {
        while (true)
        {
            Pending pending;
            ShaderInventoryFile file;
            Action<InventoryProgram, bool>? recorded;
            lock (_gate)
            {
                while (_file is null || _pending.Count == 0)
                {
                    Monitor.Wait(_gate);
                }

                pending = _pending.Dequeue();
                _pendingBytes -= pending.Header.Length + pending.Code.Length;
                file = _file;
                recorded = _recorded;
            }

            try
            {
                Process(pending, file, recorded);
            }
            catch (Exception exception)
            {
                // The capture thread must outlive any one program (and any listener).
                Console.Error.WriteLine($"[SHADER CACHE][WARN] A created shader could not be recorded: {exception.Message}");
            }
        }
    }

    private static void Process(Pending pending, ShaderInventoryFile file, Action<InventoryProgram, bool>? recorded)
    {
        if (!AgcShaderHeader.TryParse(pending.Header, pending.HeaderAddress, out var header) ||
            !TryHash(pending.Code, pending.CodeSize, pending.CodeAddress, out var hash))
        {
            return;
        }

        var identity = ShaderInventoryFile.ProgramIdentity(hash, pending.CodeSize, header.Bytes);
        var known = file.Contains(identity);
        if (!known && !Decodes(pending.Code, pending.CodeAddress))
        {
            return;
        }

        pending.Identity = file.RecordProgram(header, hash, pending.CodeSize, pending.Code, InventorySource.Created);
        if (header.Type == AgcShaderType.Pixel && pending.Vertex is { Identity: not 0 } vertex)
        {
            file.RecordPair(vertex.Identity, pending.Identity);
        }

        if (recorded is not null && file.TryGetProgram(pending.Identity, out var program))
        {
            recorded(program, !known);
        }
    }
}
