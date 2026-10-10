// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal.V2;

// Per-draw and per-dispatch GPU timing, behind SHARPEMU_METAL_V2_PROFILE_SHADERS=1.
//
// Apple silicon reports no support for MTLCounterSamplingPointAtDrawBoundary or
// AtDispatchBoundary (only AtStageBoundary, which times a whole encoder), so a draw
// cannot be timed inside a shared encoder. The profiler therefore takes the documented
// fallback: it commits the command buffer after every draw or dispatch, waits for it,
// and reads GPUEndTime - GPUStartTime, which is that one item's GPU execution. Only the
// profiling mode pays for it; nothing here runs when the switch is off.
internal sealed partial class MetalRenderHost
{
    private static readonly nint SelCommandBufferStatus = MetalNative.Selector("status");

    // The pipeline entry's profile label, resolved once per pipeline so a draw only
    // concatenates its own shape onto it.
    private string? ProfileLabelFor(MetalShaderModule vertex, MetalShaderModule? pixel, Gen5MslMeshInfo? mesh)
    {
        if (!MetalShaderProfile.Enabled)
        {
            return null;
        }

        var stage = mesh is not null ? "mesh" : vertex.Shader.Stage.ToString().ToLowerInvariant();
        var label = $"stage={stage} vs=0x{vertex.Hash:X16}";
        if (pixel is not null)
        {
            label += $" ps=0x{pixel.Hash:X16}";
        }

        if (mesh is not null)
        {
            label += $" mesh_threads={mesh.ThreadsPerThreadgroup} max_vertices={mesh.MaxVertices}";
        }

        return label;
    }

    /// <summary>
    /// Closes the command buffer this draw or dispatch was encoded into, waits for it,
    /// and charges its GPU execution time to the bound pipeline's shaders. Called at the
    /// end of every encode path; a no-op unless the profiling switch is set.
    /// </summary>
    private void ProfileWork(MetalPipelineEntry entry, string kind, string shape)
    {
        if (!MetalShaderProfile.Enabled)
        {
            return;
        }

        var transfers = Interlocked.Exchange(ref _profileTransfers, 0);
        FlushAndWaitForGpu();
        // FlushCommandBuffer parks the committed buffer in _previousCommandBuffer and
        // the wait above has run it to completion; the next flush reports and releases it.
        var buffer = _previousCommandBuffer;
        if (buffer == 0)
        {
            return;
        }

        var status = MetalV2Native.SendValue(buffer, SelCommandBufferStatus);
        if (status != CommandBufferStatusCompleted)
        {
            MetalShaderProfile.NoteFailure(kind);
            return;
        }

        var start = MetalNative.SendDouble(buffer, SelGpuStartTime);
        var end = MetalNative.SendDouble(buffer, SelGpuEndTime);
        if (end <= start)
        {
            return;
        }

        MetalShaderProfile.Note(
            $"{kind} {entry.Profile ?? "stage=?"}",
            transfers == 0 ? shape : $"{shape} transfers={transfers}",
            end - start);
    }

    private long _profileTransfers;

    /// <summary>Counts image uploads/writebacks encoded into the draw's command buffer,
    /// so a slow sample can be told apart from a slow tiler run sharing the buffer.</summary>
    private void NoteProfileTransfer()
    {
        if (MetalShaderProfile.Enabled)
        {
            Interlocked.Increment(ref _profileTransfers);
        }
    }
}

// The aggregate behind the per-draw samples: totals per (kind, shaders) key, printed
// every two seconds and once more at exit. Keys are bounded by the pipeline count.
internal static class MetalShaderProfile
{
    private sealed class Bucket
    {
        public long Count;
        public double TotalSeconds;
        public double MaxSeconds;
        public string Shape = string.Empty;
        public string WorstShape = string.Empty;
    }

    private static readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> _failures = new(StringComparer.Ordinal);
    private static readonly object _gate = new();
    private static long _reportTimestamp;
    private static bool _exitHooked;

    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_PROFILE_SHADERS"), "1", StringComparison.Ordinal);

    /// <summary>How many entries one report prints (SHARPEMU_METAL_V2_PROFILE_TOP).</summary>
    private static readonly int _topCount =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_PROFILE_TOP"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var top) && top > 0
            ? top
            : 8;

    public static void Note(string key, string shape, double seconds)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                _buckets[key] = bucket;
                if (!_exitHooked)
                {
                    _exitHooked = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Report("exit");
                }
            }

            bucket.Count++;
            bucket.TotalSeconds += seconds;
            bucket.Shape = shape;
            if (seconds > bucket.MaxSeconds)
            {
                bucket.MaxSeconds = seconds;
                bucket.WorstShape = shape;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_reportTimestamp == 0)
            {
                _reportTimestamp = now;
            }
            else if (now - _reportTimestamp >= 2 * System.Diagnostics.Stopwatch.Frequency)
            {
                _reportTimestamp = now;
                ReportLocked("interval");
            }
        }
    }

    public static void NoteFailure(string kind)
    {
        lock (_gate)
        {
            _failures[kind] = _failures.TryGetValue(kind, out var count) ? count + 1 : 1;
        }
    }

    public static void Report(string reason)
    {
        lock (_gate)
        {
            ReportLocked(reason);
        }
    }

    private static void ReportLocked(string reason)
    {
        if (_buckets.Count == 0)
        {
            return;
        }

        var culture = CultureInfo.InvariantCulture;
        var ordered = _buckets.OrderByDescending(pair => pair.Value.TotalSeconds).Take(_topCount);
        var rank = 0;
        var total = 0.0;
        var samples = 0L;
        foreach (var bucket in _buckets.Values)
        {
            total += bucket.TotalSeconds;
            samples += bucket.Count;
        }

        Console.Error.WriteLine(string.Create(
            culture,
            $"[METAL-V2][SHADER-PROF] {reason} keys={_buckets.Count} samples={samples} " +
            $"total_gpu_ms={total * 1000.0:F1}"));
        foreach (var (key, bucket) in ordered)
        {
            rank++;
            Console.Error.WriteLine(string.Create(
                culture,
                $"[METAL-V2][SHADER-PROF] #{rank} {key} n={bucket.Count} " +
                $"total_ms={bucket.TotalSeconds * 1000.0:F1} " +
                $"avg_ms={bucket.TotalSeconds * 1000.0 / bucket.Count:F2} " +
                $"max_ms={bucket.MaxSeconds * 1000.0:F2} worst=[{bucket.WorstShape}] last=[{bucket.Shape}]"));
        }

        foreach (var (kind, count) in _failures)
        {
            Console.Error.WriteLine(string.Create(
                culture, $"[METAL-V2][SHADER-PROF] incomplete kind={kind} n={count}"));
        }
    }
}
