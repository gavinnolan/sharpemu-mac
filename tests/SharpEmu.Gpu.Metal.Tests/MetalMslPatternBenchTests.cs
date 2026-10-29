// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// Micro-kernels for code shapes the Gen5 MSL translator emits, each compared with a cheaper
/// form of the same work. Standalone compute kernels (their source is in
/// MetalMslPatternBenchTests.Kernels.cs, header comment `bench group= variant= threads= tg= iters=`)
/// run with the compile options translated guest MSL uses; the GPU time is
/// GPUEndTime - GPUStartTime of one command buffer holding one dispatch. A pair marked check=1
/// must produce the same output buffer in every variant of its group. Behind the exclusive
/// device gate, and only when asked: SHARPEMU_MSL_PATTERN_BENCH_OUTPUT=&lt;json path&gt;,
/// optionally SHARPEMU_MSL_PATTERN_BENCH_GROUPS=a,b to run only those groups.
/// </summary>
public sealed unsafe partial class MetalMslPatternBenchTests(ITestOutputHelper output)
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const int DataWords = 1 << 22;
    private const int RangeEntries = 256;
    private const int Warmup = 2;
    private const int Reps = 9;

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);

    private static Dictionary<string, string> ComposeKernels() => RawKernels.ToDictionary(
        pair => pair.Key,
        pair => pair.Value
            .Replace("//@DEV_PRELUDE", DevPrelude, StringComparison.Ordinal)
            .Replace("//@BUF_PRELUDE_CONSTANT", BufPrelude.Replace("device uint* b", "constant uint* b", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("//@BUF_PRELUDE", BufPrelude, StringComparison.Ordinal));

    private sealed record Spec(string Name, string Source, string Group, string Variant, int Threads, int Tg, uint Iters, bool Check);

    [Fact]
    public void MeasureMslPatternsWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_MSL_PATTERN_BENCH_OUTPUT");
        if (path is null || !RequireDevice()) return;

        var only = Environment.GetEnvironmentVariable("SHARPEMU_MSL_PATTERN_BENCH_GROUPS")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        var specs = ComposeKernels().Select(Parse).Where(s => only is null || only.Contains(s.Group)).ToList();

        var device = Device.Value;
        var queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue"));
        var data = NewBuffer(device, (nuint)DataWords * 4);
        var faults = NewBuffer(device, 1 << 16);
        var ranges = NewBuffer(device, RangeEntries * 24);
        var dataWords = (uint*)MetalNative.Send(data, MetalNative.Selector("contents"));
        var state = 0x9E3779B9u;
        for (var i = 0; i < DataWords; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            dataWords[i] = state;
        }

        dataWords[0] = 0xFFFFFFFFu; // every lane's exec bit set
        var dataAddress = MetalNative.SendGpuAddress(data, MetalNative.Selector("gpuAddress"));
        FillRanges((ulong*)MetalNative.Send(ranges, MetalNative.Selector("contents")), dataAddress);

        var results = new List<object>();
        var outputs = new Dictionary<string, uint[]>();
        var failures = new List<string>();
        var selSetBuffer = MetalNative.Selector("setBuffer:offset:atIndex:");
        foreach (var group in specs.GroupBy(s => s.Group))
        {
            // Compile every variant of the group first, then take the repetitions round-robin
            // across them, so a change in what else the GPU is doing lands on all variants alike.
            var pool = MetalNative.objc_autoreleasePoolPush();
            var compiled = new List<(Spec Spec, nint Pso, nint Out, uint MaxThreads, uint StaticTg, List<double> Samples, nint[] Handles)>();
            try
            {
                foreach (var spec in group)
                {
                    var options = MetalNative.Send(MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")), MetalNative.Selector("init"));
                    MetalNative.ApplyMslCompileOptions(options);
                    nint error = 0;
                    var library = MetalNative.Send(device, MetalNative.Selector("newLibraryWithSource:options:error:"), MetalNative.NsString(spec.Source), options, ref error);
                    if (library == 0) { failures.Add($"{spec.Name}: {MetalNative.DescribeError(error)}"); continue; }
                    var function = MetalNative.Send(library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString("bench"));
                    nint pipelineError = 0;
                    var pso = MetalNative.Send(device, MetalNative.Selector("newComputePipelineStateWithFunction:error:"), function, ref pipelineError);
                    if (pso == 0) { failures.Add($"{spec.Name}: pso {MetalNative.DescribeError(pipelineError)}"); continue; }
                    var outBuffer = NewBuffer(device, 1 << 22);
                    var stale = (uint*)MetalNative.Send(outBuffer, MetalNative.Selector("contents"));
                    for (var i = 0; i < spec.Threads; i++) stale[i] = 0xDEADBEEFu;
                    compiled.Add((spec, pso, outBuffer,
                        (uint)MetalNative.SendUInteger(pso, MetalNative.Selector("maxTotalThreadsPerThreadgroup")),
                        (uint)MetalNative.SendUInteger(pso, MetalNative.Selector("staticThreadgroupMemoryLength")),
                        new List<double>(), [pso, function, library, options]));
                }

                var parameters = stackalloc uint[4];
                for (var rep = 0; rep < Warmup + Reps; rep++)
                {
                    foreach (var item in compiled)
                    {
                        var spec = item.Spec;
                        var buffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
                        var encoder = MetalNative.Send(buffer, MetalNative.Selector("computeCommandEncoder"));
                        MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), item.Pso);
                        MetalNative.SendSetBuffer(encoder, selSetBuffer, data, 0, 0);
                        MetalNative.SendSetBuffer(encoder, selSetBuffer, item.Out, 0, 1);
                        MetalNative.SendSetBuffer(encoder, selSetBuffer, ranges, 0, 3);
                        MetalNative.SendSetBuffer(encoder, selSetBuffer, faults, 0, 4);
                        MetalNative.SendSetBuffer(encoder, selSetBuffer, data, 0, 5);
                        // x: iterations. y/z/w: the guest base (low, high) and range count for the
                        // device-address kernels; y is instead the byte length of the bound buffer
                        // for the buffer-load kernels.
                        parameters[0] = spec.Iters;
                        parameters[1] = spec.Group.StartsWith("buf_", StringComparison.Ordinal) ? (uint)(DataWords * 4) : (uint)(GuestBase & 0xFFFFFFFFu);
                        parameters[2] = (uint)(GuestBase >> 32);
                        parameters[3] = RangeEntries;
                        MetalNative.SendSetBytes(encoder, MetalNative.Selector("setBytes:length:atIndex:"), (nint)parameters, 16, 2);
                        MetalNative.SendDispatch(encoder, MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:"),
                            new MtlSize { Width = (nuint)(spec.Threads / spec.Tg), Height = 1, Depth = 1 },
                            new MtlSize { Width = (nuint)spec.Tg, Height = 1, Depth = 1 });
                        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
                        MetalNative.SendVoid(buffer, MetalNative.Selector("commit"));
                        MetalNative.SendVoid(buffer, MetalNative.Selector("waitUntilCompleted"));
                        // MTLCommandBufferStatusCompleted is 4; a kernel that faulted leaves its output stale.
                        var status = MetalNative.SendUInteger(buffer, MetalNative.Selector("status"));
                        if (status != 4)
                        {
                            failures.Add($"{spec.Name}: command buffer status {status}: {MetalNative.DescribeError(MetalNative.Send(buffer, MetalNative.Selector("error")))}");
                            continue;
                        }

                        var start = MetalNative.SendDouble(buffer, MetalNative.Selector("GPUStartTime"));
                        var end = MetalNative.SendDouble(buffer, MetalNative.Selector("GPUEndTime"));
                        if (rep >= Warmup) item.Samples.Add((end - start) * 1e6);
                    }
                }

                foreach (var item in compiled)
                {
                    var spec = item.Spec;
                    var samples = item.Samples;
                    if (samples.Count == 0) continue;
                    samples.Sort();
                    var words = (uint*)MetalNative.Send(item.Out, MetalNative.Selector("contents"));
                    var snapshot = new uint[spec.Threads];
                    for (var i = 0; i < snapshot.Length; i++) snapshot[i] = words[i];
                    outputs[spec.Name] = snapshot;
                    results.Add(new
                    {
                        group = spec.Group,
                        variant = spec.Variant,
                        threads = spec.Threads,
                        threadgroup = spec.Tg,
                        iters = spec.Iters,
                        medianMicroseconds = Math.Round(samples[samples.Count / 2], 1),
                        minMicroseconds = Math.Round(samples[0], 1),
                        maxMicroseconds = Math.Round(samples[^1], 1),
                        maxThreadsPerThreadgroup = item.MaxThreads,
                        staticThreadgroupBytes = item.StaticTg,
                        checksum = Checksum(snapshot),
                        firstWords = snapshot.Take(4).Select(w => w.ToString("X8", CultureInfo.InvariantCulture)).ToArray(),
                    });
                    output.WriteLine($"{spec.Group,-26} {spec.Variant,-24} median {samples[samples.Count / 2],9:F1} us  min {samples[0],9:F1}  maxTG {item.MaxThreads}");
                }
            }
            finally
            {
                foreach (var item in compiled)
                {
                    foreach (var handle in item.Handles) MetalNative.SendVoid(handle, MetalNative.Selector("release"));
                    MetalNative.SendVoid(item.Out, MetalNative.Selector("release"));
                }

                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }


        // Variants of a checked group agree with the group's first variant, element for element.
        var mismatches = new List<string>();
        foreach (var group in specs.Where(s => s.Check && outputs.ContainsKey(s.Name)).GroupBy(s => s.Group))
        {
            var reference = group.First();
            foreach (var other in group.Skip(1))
            {
                var a = outputs[reference.Name];
                var b = outputs[other.Name];
                var differing = 0;
                for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) differing++;
                if (differing != 0) mismatches.Add($"{group.Key}: {other.Variant} differs from {reference.Variant} in {differing} of {a.Length} threads");
            }
        }

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            reps = Reps,
            results,
            mismatches,
            failures,
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var line in mismatches) output.WriteLine("MISMATCH " + line);
        foreach (var line in failures) output.WriteLine("FAILED " + line);
    }

    // 256 ranges sorted by first page, a guest page being 16 KiB: lower one-page ranges that
    // nothing reads, then two halves of the data buffer ("A" the first 8 MiB, "B" the next).
    private static void FillRanges(ulong* table, ulong dataAddress)
    {
        var firstDataPage = GuestBase >> 14;
        for (var i = 0; i < RangeEntries - 2; i++)
        {
            table[i * 3] = firstDataPage - 4096 + (ulong)i * 8;
            table[i * 3 + 1] = 1;
            table[i * 3 + 2] = dataAddress;
        }

        const ulong halfPages = 0x800000 >> 14;
        table[(RangeEntries - 2) * 3] = firstDataPage;
        table[(RangeEntries - 2) * 3 + 1] = halfPages;
        table[(RangeEntries - 2) * 3 + 2] = dataAddress;
        table[(RangeEntries - 1) * 3] = firstDataPage + halfPages;
        table[(RangeEntries - 1) * 3 + 1] = halfPages;
        table[(RangeEntries - 1) * 3 + 2] = dataAddress + 0x800000;
    }

    private static string Checksum(uint[] words)
    {
        ulong hash = 1469598103934665603UL;
        foreach (var word in words) hash = (hash ^ word) * 1099511628211UL;
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static nint NewBuffer(nint device, nuint length) =>
        MetalNative.SendNewBuffer(device, MetalNative.Selector("newBufferWithLength:options:"), length, 0);

    private static Spec Parse(KeyValuePair<string, string> kernel)
    {
        var header = Regex.Match(kernel.Value, @"^// bench (?<fields>.*)$", RegexOptions.Multiline).Groups["fields"].Value;
        var fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Split('=')).ToDictionary(p => p[0], p => p[1]);
        return new Spec(kernel.Key, kernel.Value, fields["group"], fields["variant"], int.Parse(fields["threads"], CultureInfo.InvariantCulture),
            int.Parse(fields["tg"], CultureInfo.InvariantCulture), uint.Parse(fields["iters"], CultureInfo.InvariantCulture),
            fields.TryGetValue("check", out var check) && check == "1");
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("MSL pattern micro-kernels run on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }
}
