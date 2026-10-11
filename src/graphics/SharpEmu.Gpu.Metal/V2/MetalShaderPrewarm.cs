// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Logging;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Holds the guest at its first splash hide, GPU submission or flip until the shader
/// prewarm has been decided and has finished, so logos, movies and the first scenes do
/// not race thousands of background pipeline compiles. The window keeps presenting the
/// splash meanwhile. SHARPEMU_SHADER_PREWARM_WAIT=0 opts out. The v2 twin of the
/// Vulkan presenter's WaitForShaderPrewarm.
/// </summary>
internal sealed class MetalShaderPrewarmGate(bool disabled)
{
    private readonly object _sync = new();
    private bool _started;
    private bool _decided;
    private bool _running;
    private bool _cancelled;
    private volatile bool _released;
    private int _holdReported;
    private int _progress;
    private int _total;

    public int Progress => Volatile.Read(ref _progress);

    /// <summary>The presenter started: a holder now waits for the prewarm to be decided.</summary>
    public void Begin()
    {
        lock (_sync)
        {
            _started = true;
        }
    }

    /// <summary>The prewarm is decided; running holds the guest until it is set false.</summary>
    public void SetRunning(bool running)
    {
        lock (_sync)
        {
            _decided = true;
            _running = running;
            _released = !running;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>The session is closing: nothing is held any longer.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _cancelled = true;
            _released = true;
            Monitor.PulseAll(_sync);
        }
    }

    public void SetTotal(int total)
    {
        Volatile.Write(ref _total, total);
        Volatile.Write(ref _progress, 0);
    }

    public int Advance() => Interlocked.Increment(ref _progress);

    /// <summary>Whether a holder would wait right now.</summary>
    public bool IsHolding
    {
        get
        {
            lock (_sync)
            {
                return HoldingLocked(static () => false);
            }
        }
    }

    public void Wait(string holder) => Wait(holder, static () => HostSessionControl.IsShutdownRequested);

    public void Wait(string holder, Func<bool> shutdownRequested)
    {
        if (disabled || _released)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var reported = started;
        var reporter = false;
        lock (_sync)
        {
            while (HoldingLocked(shutdownRequested))
            {
                if (!reporter && _running && Interlocked.Exchange(ref _holdReported, 1) == 0)
                {
                    reporter = true;
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Shader prewarm: holding the game at its {holder} until it finishes.");
                }

                Monitor.Wait(_sync, 1000);
                if (reporter && Stopwatch.GetElapsedTime(reported) >= TimeSpan.FromSeconds(5))
                {
                    reported = Stopwatch.GetTimestamp();
                    var total = Volatile.Read(ref _total);
                    var done = Volatile.Read(ref _progress);
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Shader prewarm: {done}/{total} ({(total == 0 ? 0 : done * 100 / total)}%)");
                }
            }
        }

        if (reporter)
        {
            Console.Error.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"[LOADER][INFO] Shader prewarm: game released after {Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s."));
        }
    }

    private bool HoldingLocked(Func<bool> shutdownRequested) =>
        _started && (!_decided || _running) && !_cancelled && !shutdownRequested();
}

/// <summary>
/// Compiles the compute pipelines a previous run recorded (shader-prewarm.bin) on
/// background threads at startup: the MSL library, its function and the compute
/// pipeline state, which fills the driver's shader cache, so the first dispatch of
/// each shader no longer waits for the driver. Uses the shared list and record
/// infrastructure and the shared resource plan; the stamp names the translators, the
/// compiler type and the device and OS, and an interrupted prewarm resumes with the
/// pipelines it had not finished. SHARPEMU_SHADER_PREWARM=0 turns it off. The v2 twin
/// of the Vulkan presenter's ShaderPrewarm partial.
/// </summary>
internal static class MetalShaderPrewarm
{
    private const int MaxLoggedPrewarmFailures = 8;
    private const int PrewarmProgressBatch = 256;

    private static readonly nint SelNewFunctionWithName = MetalNative.Selector("newFunctionWithName:");
    private static readonly nint SelNewComputePipelineState =
        MetalNative.Selector("newComputePipelineStateWithFunction:error:");
    private static readonly nint SelRelease = MetalNative.Selector("release");
    private static readonly nint SelName = MetalNative.Selector("name");

    public static readonly MetalShaderPrewarmGate Gate =
        new(Environment.GetEnvironmentVariable("SHARPEMU_SHADER_PREWARM_WAIT") == "0");

    private static readonly object _startGate = new();
    private static bool _startRequested;
    private static ShaderPrewarmList? _list;
    private static ConcurrentDictionary<string, byte>? _prewarmedShaderIdentities;
    private static ConcurrentDictionary<string, byte>? _runtimeComputeIdentities;
    private static Thread[] _threads = [];
    private static volatile bool _stopping;
    private static volatile bool _finished;
    private static int _compiled;
    private static int _failed;
    private static int _runtimeHits;
    private static readonly ConcurrentQueue<ulong> _completed = new();

    /// <summary>The list the shader cache records into; null while prewarm is off.</summary>
    public static ShaderPrewarmList? List => Volatile.Read(ref _list);

    /// <summary>
    /// Decides the prewarm once per session, off the caller's thread: loading the list
    /// reads a file that can run to megabytes. The gate holds the guest until then.
    /// </summary>
    public static void Start(nint device)
    {
        lock (_startGate)
        {
            if (_startRequested)
            {
                return;
            }

            _startRequested = true;
        }

        Gate.Begin();
        new Thread(() =>
        {
            try
            {
                if (!StartCore(device))
                {
                    Gate.SetRunning(false);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm could not start: {exception.Message}");
                Gate.SetRunning(false);
            }
        })
        {
            IsBackground = true,
            Name = "SharpEmu Metal v2 shader prewarm",
        }.Start();
    }

    private static bool StartCore(nint device)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_SHADER_PREWARM") == "0")
        {
            return false;
        }

        var directory = SharpEmuPaths.UserPath(
            "pipeline_cache", SanitizeTitleId(VideoOutBackendServices.GetApplicationTitleId()), "metal-v2");
        if (ShaderPrewarmList.Open(directory) is not { } list)
        {
            return false;
        }

        _prewarmedShaderIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        _runtimeComputeIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        Volatile.Write(ref _list, list);
        var compiler = GuestGpu.Current;
        var stamp = Stamp(compiler, device);
        if (list.LoadedComputeCount == 0)
        {
            list.WriteStamp(stamp);
            _finished = true;
            return false;
        }

        if (list.IsStampCurrent(stamp))
        {
            _finished = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm skipped: compute={list.LoadedComputeCount} reason=same-build-and-driver");
            return false;
        }

        var completed = list.ReadProgress(stamp);
        var pending = list.LoadedComputes()
            .Where(item => !completed.Contains(ShaderPrewarmList.Identity(item.Record)))
            .ToList();
        var started = Stopwatch.GetTimestamp();
        if (pending.Count == 0)
        {
            Finish(list, stamp, started);
            return false;
        }

        var work = new ConcurrentQueue<(ComputePrewarmRecord Record, ShaderCodeCapture Code)>(pending);
        var workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        var remaining = workerCount;
        Gate.SetTotal(pending.Count);
        Console.Error.WriteLine(
            $"[LOADER][INFO] Shader prewarm started: compute={pending.Count} resumed={completed.Count} workers={workerCount}");
        Gate.SetRunning(true);
        var threads = new Thread[workerCount];
        for (var index = 0; index < workerCount; index++)
        {
            threads[index] = new Thread(() =>
            {
                while (!_stopping && work.TryDequeue(out var item))
                {
                    PrewarmComputePipeline(item.Record, item.Code, compiler, device);
                    _completed.Enqueue(ShaderPrewarmList.Identity(item.Record));
                    if (Gate.Advance() % PrewarmProgressBatch == 0)
                    {
                        FlushProgress(list, stamp);
                    }
                }

                FlushProgress(list, stamp);
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    Finish(list, stamp, started);
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"SharpEmu shader prewarm {index}",
            };
        }

        Volatile.Write(ref _threads, threads);
        foreach (var thread in threads)
        {
            thread.Start();
        }

        return true;
    }

    private static void FlushProgress(ShaderPrewarmList list, string stamp)
    {
        var batch = new List<ulong>();
        while (_completed.TryDequeue(out var identity))
        {
            batch.Add(identity);
        }

        list.AppendProgress(stamp, batch);
    }

    private static string StampPart(Type type) => type.Assembly.ManifestModule.ModuleVersionId.ToString("N");

    // The translators decide the MSL; the device and the OS decide what the driver's
    // compiler makes of it.
    private static string Stamp(IGuestGpuBackend compiler, nint device) =>
        string.Join(
            '|',
            StampPart(typeof(Gen5ShaderTranslator)),
            StampPart(typeof(Gen5MslTranslator)),
            compiler.GetType().FullName,
            MetalNative.ReadNsString(MetalNative.Send(device, SelName)),
            RuntimeInformation.OSDescription);

    /// <summary>The identity of a compiled module, for the runtime match count.</summary>
    public static string Identity(byte[] payload) =>
        XxHash3.HashToUInt64(payload).ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

    private static void PrewarmComputePipeline(
        ComputePrewarmRecord record,
        ShaderCodeCapture code,
        IGuestGpuBackend compiler,
        nint device)
    {
        if (!ShaderPrewarmCompiler.TryCompilePrewarm(
                record,
                code,
                compiler,
                MetalRenderHost.PrewarmSharedInt64Atomics,
                MetalRenderHost.PrewarmExecGuardElision,
                MetalRenderHost.PrewarmSignedZeroInfNanPreserve,
                out var compiled,
                out _,
                out var error))
        {
            NoteFailure(record, error);
            return;
        }

        if (compiled is not MetalCompiledGuestShader metal)
        {
            NoteFailure(record, $"the compiler produced {compiled?.GetType().Name ?? "nothing"}, not MSL");
            return;
        }

        _prewarmedShaderIdentities![Identity(metal.Payload)] = 0;
        var pool = MetalNative.objc_autoreleasePoolPush();
        nint library = 0;
        nint function = 0;
        nint state = 0;
        try
        {
            library = MetalRenderHost.TryCompileLibrary(
                device, metal.Shader.Source, metal.Shader.UsesPerVertexInputs, out error);
            if (library == 0)
            {
                NoteFailure(record, error);
                return;
            }

            function = MetalNative.Send(library, SelNewFunctionWithName, MetalNative.NsString(metal.Shader.EntryPoint));
            if (function == 0)
            {
                NoteFailure(record, $"the library has no entry point {metal.Shader.EntryPoint}");
                return;
            }

            nint pipelineError = 0;
            state = MetalNative.Send(device, SelNewComputePipelineState, function, ref pipelineError);
            if (state == 0)
            {
                NoteFailure(record, MetalNative.DescribeError(pipelineError));
                return;
            }

            Interlocked.Increment(ref _compiled);
        }
        catch (Exception exception)
        {
            NoteFailure(record, exception.Message);
        }
        finally
        {
            foreach (var handle in (ReadOnlySpan<nint>)[state, function, library])
            {
                if (handle != 0)
                {
                    MetalNative.SendVoid(handle, SelRelease);
                }
            }

            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    private static void NoteFailure(ComputePrewarmRecord record, string error)
    {
        if (Interlocked.Increment(ref _failed) <= MaxLoggedPrewarmFailures)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Shader prewarm failed: cs=0x{record.Hash:X16} shader=0x{record.Address:X16} error={error}");
        }
    }

    private static void Finish(ShaderPrewarmList list, string stamp, long started)
    {
        var runtime = _runtimeComputeIdentities!;
        var prewarmed = _prewarmedShaderIdentities!;
        var matched = runtime.Keys.Count(prewarmed.ContainsKey);
        if (_stopping)
        {
            Gate.SetRunning(false);
            return;
        }

        list.WriteStamp(stamp);
        list.ClearProgress();
        _finished = true;
        Gate.SetRunning(false);
        Console.Error.WriteLine(
            $"[LOADER][INFO] Shader prewarm done: compiled={Volatile.Read(ref _compiled)} " +
            $"failed={Volatile.Read(ref _failed)} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} " +
            $"runtime_compute_modules={runtime.Count} matched={matched}");
    }

    /// <summary>A compute module the running title created, counted against the prewarmed ones.</summary>
    public static void NoteRuntimeComputeModule(MetalCompiledGuestShader shader)
    {
        if (_runtimeComputeIdentities is not { } runtime || _prewarmedShaderIdentities is not { } prewarmed)
        {
            return;
        }

        var identity = Identity(shader.Payload);
        runtime[identity] = 0;
        if (!_finished || !prewarmed.ContainsKey(identity))
        {
            return;
        }

        var hits = Interlocked.Increment(ref _runtimeHits);
        if (hits is 1 or 10 or 100 or 1000)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm hits: {hits} of runtime_compute_modules={runtime.Count}");
        }
    }

    /// <summary>Stops the workers and closes the list; the gate holds nobody afterwards.</summary>
    public static void Stop()
    {
        _stopping = true;
        Gate.Cancel();
        foreach (var thread in Volatile.Read(ref _threads))
        {
            if (thread != Thread.CurrentThread)
            {
                thread.Join(TimeSpan.FromSeconds(10));
            }
        }

        Volatile.Write(ref _threads, []);
        if (Interlocked.Exchange(ref _list, null) is { } list)
        {
            list.Dispose();
        }
    }

    // The same per-title directory naming as the Vulkan pipeline cache.
    private static string SanitizeTitleId(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return "UNKNOWN";
        }

        var source = titleId.Trim();
        var sanitized = new char[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            sanitized[index] = char.IsAsciiLetterOrDigit(value) || value is '-' or '_'
                ? char.ToUpperInvariant(value)
                : '_';
        }

        return new string(sanitized);
    }
}
