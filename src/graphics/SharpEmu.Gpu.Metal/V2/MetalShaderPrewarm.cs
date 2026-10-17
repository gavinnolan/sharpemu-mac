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
/// prewarm has been decided and has compiled what the guest needs first (every recorded
/// compute, or those of its first frame once a session has recorded them; see
/// MetalShaderPrewarm.HoldFirst), so the first frames do not wait on cold compiles. The
/// window keeps presenting the splash meanwhile. SHARPEMU_SHADER_PREWARM_WAIT=0 opts out.
/// The v2 twin of the Vulkan presenter's WaitForShaderPrewarm.
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
/// Compiles the compute pipelines a previous run recorded (shader-prewarm-*.bin) on
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
    private static int _deduplicated;
    private static readonly ConcurrentQueue<ulong> _completed = new();
    private static readonly ConcurrentDictionary<string, ManualResetEventSlim> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Compiles in flight at once. The MSL front end runs in the driver's compiler service
    /// and is CPU bound there: cold compiles of Demon's Souls' computes finished twice as
    /// fast with four requests in flight as with two, and no faster with more.
    /// </summary>
    internal static int WorkerCount { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

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
        var compiler = GuestGpu.Current;
        var stamp = Stamp(compiler, device);
        // Seeded before the compute prewarm or the game plans a shader.
        MetalDeviceDataLoadsStore.Open(directory, stamp);
        // Loaded before the gate lets the game go, so every module the game creates
        // finds the graphics libraries queued; they compile once the computes are done.
        MetalLibraryPrewarm.Open(directory, device, stamp);
        if (ShaderPrewarmList.Open(directory) is not { } list)
        {
            MetalLibraryPrewarm.Begin();
            return false;
        }

        _prewarmedShaderIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        _runtimeComputeIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        Volatile.Write(ref _list, list);
        if (list.LoadedComputeCount == 0)
        {
            list.WriteStamp(stamp);
            _finished = true;
            MetalLibraryPrewarm.Begin();
            return false;
        }

        var pending = PendingComputes(list, stamp, out var resumed);
        var started = Stopwatch.GetTimestamp();
        if (pending.Count == 0)
        {
            Finish(list, stamp, started);
            return false;
        }

        var ordered = HoldFirst(pending, HoldsForEveryCompute ? null : list.ReadFirstFrameComputes(), out var held);
        var work = new ConcurrentQueue<(ComputePrewarmRecord Record, ShaderCodeCapture Code, bool Held)>(ordered);
        var workerCount = WorkerCount;
        var remaining = workerCount;
        var done = 0;
        Gate.SetTotal(held);
        Console.Error.WriteLine(
            $"[LOADER][INFO] Shader prewarm started: compute={pending.Count} held={held} resumed={resumed} workers={workerCount}");
        Gate.SetRunning(held > 0);
        var threads = new Thread[workerCount];
        for (var index = 0; index < workerCount; index++)
        {
            threads[index] = new Thread(() =>
            {
                while (!_stopping && work.TryDequeue(out var item))
                {
                    PrewarmComputePipeline(item.Record, item.Code, compiler, device);
                    _completed.Enqueue(ShaderPrewarmList.Identity(item.Record));
                    if (item.Held && Gate.Advance() == held && held < pending.Count)
                    {
                        Gate.SetRunning(false);
                        Console.Error.WriteLine(string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"[LOADER][INFO] Shader prewarm: the {held} computes of the first frame are ready after " +
                            $"{Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s; the other {pending.Count - held} compile while the game runs."));
                    }

                    if (Interlocked.Increment(ref done) % PrewarmProgressBatch == 0)
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
                // Normal priority: the driver's compiler service runs a request at the
                // requesting thread's priority, and from below-normal threads the same 300
                // cold Demon's Souls computes took 88 s instead of 34 s (ShaderCompileBench).
                IsBackground = true,
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

    /// <summary>
    /// The recorded computes this start compiles: every one, but those an interrupted
    /// prewarm under the same stamp already finished. A current stamp does not skip the
    /// rest. The libraries land in macOS's per-app Metal shader cache, which the OS caps
    /// and evicts by itself while other titles and builds under the same bundle fill it,
    /// so a finished prewarm says nothing about what the cache still holds. Skipping on
    /// the stamp left Demon's Souls, after an eviction, compiling every compute library
    /// cold on the GPU worker at its first dispatch, drawing nothing for about 100 s.
    /// A library the cache still holds compiles in milliseconds, and a warm prewarm
    /// finishes before the game reaches its splash hold.
    /// </summary>
    internal static List<(ComputePrewarmRecord Record, ShaderCodeCapture Code)> PendingComputes(
        ShaderPrewarmList list,
        string stamp,
        out int resumed)
    {
        var completed = list.ReadProgress(stamp);
        resumed = completed.Count;
        return list.LoadedComputes()
            .Where(item => !completed.Contains(ShaderPrewarmList.Identity(item.Record)))
            .ToList();
    }

    /// <summary>
    /// SHARPEMU_SHADER_PREWARM_HOLD=all holds the guest for every recorded compute, as
    /// before there was a first-frame list.
    /// </summary>
    private static readonly bool HoldsForEveryCompute =
        Environment.GetEnvironmentVariable("SHARPEMU_SHADER_PREWARM_HOLD") == "all";

    /// <summary>
    /// The pending computes in the order the prewarm compiles them, and how many from the
    /// front the guest is held for: those the last session created before its first frame
    /// (in list order, which is the order a session first used them), then the rest. With no
    /// first-frame list yet, every one is held for. Demon's Souls records some 1400 computes
    /// across its scenes and creates about a hundred before its first frame; a cold start
    /// held it for all of them, 126 s at the splash screen (run 20261017-075343-ds-base-boot),
    /// while the rest can compile behind the logos and the intro.
    /// </summary>
    internal static List<(ComputePrewarmRecord Record, ShaderCodeCapture Code, bool Held)> HoldFirst(
        IReadOnlyList<(ComputePrewarmRecord Record, ShaderCodeCapture Code)> pending,
        IReadOnlySet<ulong>? firstFrame,
        out int held)
    {
        if (firstFrame is null)
        {
            held = pending.Count;
            return pending.Select(item => (item.Record, item.Code, true)).ToList();
        }

        var ordered = new List<(ComputePrewarmRecord, ShaderCodeCapture, bool)>(pending.Count);
        ordered.AddRange(pending
            .Where(item => firstFrame.Contains(ShaderPrewarmList.Identity(item.Record)))
            .Select(item => (item.Record, item.Code, true)));
        held = ordered.Count;
        ordered.AddRange(pending
            .Where(item => !firstFrame.Contains(ShaderPrewarmList.Identity(item.Record)))
            .Select(item => (item.Record, item.Code, false)));
        return ordered;
    }

    /// <summary>The guest presented its first frame: what it created until now is what the next start holds it for.</summary>
    public static void NoteFirstGuestFrame() => List?.WriteFirstFrameComputes();

    /// <summary>
    /// Waits while the prewarm compiles this module's source, so a dispatch that comes for
    /// a compute the background prewarm has in hand takes its cached library instead of
    /// starting the same front-end compile a second time.
    /// </summary>
    public static void WaitWhileCompiling(MetalCompiledGuestShader shader)
    {
        if (_finished || _inFlight.IsEmpty)
        {
            return;
        }

        if (_inFlight.TryGetValue(Identity(shader.Payload), out var compiling))
        {
            compiling.Wait();
        }
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

    // The translators' code, not their build: every commit gives every assembly a new
    // module version id, and keying on it threw away the library list and the learned
    // device data loads at the first start after any commit (AssemblyCodeIdentity).
    private static string StampPart(Type type) => AssemblyCodeIdentity.Of(type.Assembly);

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

        // Records that differ only in what the plan never reads translate to the same
        // MSL; about half of Demon's Souls' 1389 recorded computes repeat another one's
        // source, and a cold start compiled each copy again, side by side with the first.
        var identity = Identity(metal.Payload);
        if (!_prewarmedShaderIdentities!.TryAdd(identity, 0))
        {
            Interlocked.Increment(ref _deduplicated);
            return;
        }

        // Not disposed: a dispatch may still be waking from it.
        var compiling = new ManualResetEventSlim(false);
        _inFlight[identity] = compiling;
        try
        {
            CompileComputePipeline(record, metal, device);
        }
        finally
        {
            _inFlight.TryRemove(identity, out _);
            compiling.Set();
        }
    }

    private static void CompileComputePipeline(ComputePrewarmRecord record, MetalCompiledGuestShader metal, nint device)
    {
        string error;
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
        MetalLibraryPrewarm.Begin();
        Console.Error.WriteLine(
            $"[LOADER][INFO] Shader prewarm done: compiled={Volatile.Read(ref _compiled)} " +
            $"failed={Volatile.Read(ref _failed)} same_source={Volatile.Read(ref _deduplicated)} " +
            $"ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} " +
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
        MetalLibraryPrewarm.Stop();
        MetalDeviceDataLoadsStore.Stop();
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
