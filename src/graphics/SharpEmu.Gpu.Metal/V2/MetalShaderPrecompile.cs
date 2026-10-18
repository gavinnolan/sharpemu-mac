// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>What the shader prewarm does before it lets the guest run.</summary>
internal enum MetalPrecompileMode
{
    /// <summary>Holds the guest for its first frame's programs only; the rest compile behind it.</summary>
    Off,

    /// <summary>Holds the guest until every recorded compute, graphics program and listed library is compiled.</summary>
    Hold,

    /// <summary>As <see cref="Hold"/>, then closes the session instead of letting the guest go.</summary>
    Exit,
}

/// <summary>
/// The hold-until-done and precompile-only modes of the shader prewarm
/// (SHARPEMU_SHADER_PREWARM_HOLD=complete, SHARPEMU_SHADER_PRECOMPILE_ONLY=1; the CLI's
/// --shader-prewarm-hold and --precompile-only set them). The guest is held at its first
/// splash hide, GPU submission or flip while the compute prewarm, the graphics prewarm and
/// then the MSL library list compile to completion; in precompile-only mode the session
/// then closes. Everything is compiled and released, not kept: what makes the next start
/// fast is macOS's per-app Metal shader cache, which the compiles fill, and holding every
/// library would cost about 48 KB each (MetalRenderHost.ShaderCache). The log reports the
/// phases, progress every five seconds, and the footprint the precompile reached.
/// </summary>
internal sealed class MetalShaderPrecompile(
    MetalPrecompileMode mode,
    Action exit,
    Func<(ulong Current, ulong LifetimePeak)> footprint)
{
    internal const string PrecompileOnlyVariable = "SHARPEMU_SHADER_PRECOMPILE_ONLY";
    internal const string HoldVariable = "SHARPEMU_SHADER_PREWARM_HOLD";
    internal const string HoldCompleteValue = "complete";

    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private long _started;
    private long _lastReport;
    private ulong _startFootprint;
    private ulong _peakFootprint;
    private string _phase = string.Empty;
    private int _total;
    private int _done;
    private int _completed;

    /// <summary>The mode the environment asks for.</summary>
    public static MetalPrecompileMode EnvironmentMode { get; } = Parse(
        Environment.GetEnvironmentVariable(PrecompileOnlyVariable),
        Environment.GetEnvironmentVariable(HoldVariable));

    /// <summary>The process's precompile; closing the session is closing the presentation window.</summary>
    public static MetalShaderPrecompile Current { get; } = new(
        EnvironmentMode,
        VideoOutBackendServices.NotifyPresentationWindowClosed,
        () => OperatingSystem.IsMacOS() ? MetalHostMemory.Footprint() : (0, 0));

    public MetalPrecompileMode Mode => mode;

    /// <summary>Whether the guest is held until everything is compiled.</summary>
    public bool HoldsUntilComplete => mode != MetalPrecompileMode.Off;

    /// <summary>Whether <see cref="Complete"/> ran.</summary>
    public bool Completed => Volatile.Read(ref _completed) != 0;

    /// <summary>The highest footprint sampled since the first phase began, in bytes.</summary>
    public ulong PeakFootprint => Interlocked.Read(ref _peakFootprint);

    internal static MetalPrecompileMode Parse(string? precompileOnly, string? hold) =>
        precompileOnly is "1" || string.Equals(precompileOnly, "true", StringComparison.OrdinalIgnoreCase)
            ? MetalPrecompileMode.Exit
            : string.Equals(hold, HoldCompleteValue, StringComparison.OrdinalIgnoreCase)
                ? MetalPrecompileMode.Hold
                : MetalPrecompileMode.Off;

    /// <summary>A phase starts: the recorded programs, then the library list.</summary>
    public void BeginPhase(string phase, int total)
    {
        var (current, _) = footprint();
        lock (_sync)
        {
            if (_started == 0)
            {
                _started = Stopwatch.GetTimestamp();
                _startFootprint = current;
            }

            _phase = phase;
            _total = total;
            _done = 0;
            _lastReport = Stopwatch.GetTimestamp();
        }

        NotePeak(current);
        Console.Error.WriteLine(
            $"[LOADER][INFO] Shader precompile: phase={phase} total={total} footprint_mb={Mb(current)}");
    }

    /// <summary>One item of the current phase is compiled. Any thread.</summary>
    public void Advance()
    {
        var done = Interlocked.Increment(ref _done);
        var (current, _) = footprint();
        NotePeak(current);
        var last = Interlocked.Read(ref _lastReport);
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(last, now) < ReportInterval ||
            Interlocked.CompareExchange(ref _lastReport, now, last) != last)
        {
            return;
        }

        var total = Volatile.Read(ref _total);
        Console.Error.WriteLine(
            $"[LOADER][INFO] Shader precompile: phase={_phase} {done}/{total} " +
            $"({(total == 0 ? 100 : done * 100 / total)}%) footprint_mb={Mb(current)}");
    }

    /// <summary>
    /// Everything is compiled (or there was nothing to compile): logs the summary and, in
    /// precompile-only mode, closes the session. Only the first call does anything; every
    /// call is true when the session is closing, so the caller does not let the guest go.
    /// </summary>
    public bool Complete(string detail)
    {
        if (mode == MetalPrecompileMode.Off)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return mode == MetalPrecompileMode.Exit;
        }

        var (current, lifetimePeak) = footprint();
        NotePeak(current);
        long started;
        ulong startFootprint;
        lock (_sync)
        {
            started = _started == 0 ? Stopwatch.GetTimestamp() : _started;
            startFootprint = _started == 0 ? current : _startFootprint;
        }

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[LOADER][INFO] Shader precompile done: mode={(mode == MetalPrecompileMode.Exit ? "precompile-only" : "hold")} " +
            $"ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} footprint_start_mb={Mb(startFootprint)} " +
            $"footprint_peak_mb={Mb(PeakFootprint)} footprint_end_mb={Mb(current)} " +
            $"process_peak_mb={Mb(lifetimePeak)} {detail}"));
        if (mode != MetalPrecompileMode.Exit)
        {
            return false;
        }

        Console.Error.WriteLine("[LOADER][INFO] Shader precompile only: closing the session.");
        exit();
        return true;
    }

    private void NotePeak(ulong bytes)
    {
        var peak = Interlocked.Read(ref _peakFootprint);
        while (bytes > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peakFootprint, bytes, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }

    private static string Mb(ulong bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture);
}
