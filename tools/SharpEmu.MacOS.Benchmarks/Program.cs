// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static partial class Program
{
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Register(string name);

    private static int Main()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Real macOS Objective-C runtime required.");
        var assembly = Assembly.Load("SharpEmu.Gpu.Metal");
        var selector = assembly.GetType("SharpEmu.Gpu.Metal.MetalNative", true)!
            .GetMethod("Selector", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Func<string, nint>>();
        string[] names = ["commandBuffer", "commit", "endEncoding", "texture", "nextDrawable", "setVertexBuffer:offset:atIndex:",
            "setFragmentTexture:atIndex:", "setFragmentSamplerState:atIndex:", "setRenderPipelineState:", "retain", "release",
            "renderCommandEncoderWithDescriptor:", "presentDrawable:", "setViewport:", "setScissorRect:", "status"];
        var expected = names.Select(Register).ToArray();
        if (expected.Any(value => value == 0)) throw new InvalidOperationException("Native selector registration failed.");
        Parallel.For(0, 4096, i =>
        {
            if (selector(names[i & 15]) != expected[i & 15]) throw new InvalidOperationException("Selector identity changed under concurrent first use.");
        });
        // Both comparisons use a managed delegate and the same real generated
        // UTF-8 native import. Warm both before alternating sample order.
        // Give the default runtime's background tiered compilation time to
        // settle; never infer steady-state cost from a short Tier0 startup run.
        var warmupStarted = Stopwatch.GetTimestamp();
        do
        {
            for (var i = 0; i < 100_000; i++) { Register(names[i & 15]); selector(names[i & 15]); }
        } while (Stopwatch.GetElapsedTime(warmupStarted) < TimeSpan.FromSeconds(2));
        var samples = new List<object>();
        for (var sample = 0; sample < 10; sample++)
        {
            if ((sample & 1) == 0) { Measure("native_registration", Register); Measure("production_selector", selector); }
            else { Measure("production_selector", selector); Measure("native_registration", Register); }
            void Measure(string kind, Func<string, nint> operation)
            {
                const int iterations = 1_000_000;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                nint checksum = 0;
                for (var i = 0; i < iterations; i++) checksum ^= operation(names[i & 15]);
                var ticks = Stopwatch.GetTimestamp() - started;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                samples.Add(new { sample, kind, iterations, ticks, nanoseconds_per_call = ticks * 1e9 / Stopwatch.Frequency / iterations,
                    allocated_bytes = allocated, checksum = (long)checksum });
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { scope = "Real selector identity/cache microbenchmark; no GPU, window, guest or end-to-end FPS proof.",
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
            warmup_seconds = 2, selector_names = names.Length, concurrent_identity_checks = 4096,
            stopwatch_frequency = Stopwatch.Frequency, assembly_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            sse2 = System.Runtime.Intrinsics.X86.Sse2.IsSupported, avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported,
            advsimd = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported, samples }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
