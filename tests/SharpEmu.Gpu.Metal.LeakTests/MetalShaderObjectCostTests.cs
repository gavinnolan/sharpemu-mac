// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Gpu.Metal.V2;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.LeakTests;

/// <summary>
/// What one MTLLibrary, its MTLFunction and a pipeline state made from it cost the process
/// (malloc bytes and blocks, phys_footprint), measured by building a batch of distinct ones,
/// and that releasing them gives it back: the render host's shader cache bound
/// (MetalRenderHost.ShaderCache) evicts exactly these objects, and an eviction that freed
/// nothing would bound nothing.
/// </summary>
/// <remarks>
/// SHARPEMU_TEST_MSL_LIST=path/to/msl-libraries-v1-*.bin also measures the first libraries of
/// a title's recorded list (the library prewarm's file), which are the sizes a game makes.
/// </remarks>
public sealed class MetalShaderObjectCostTests(ITestOutputHelper output)
{
    private const int Count = 128;
    private static readonly nint SelRelease = MetalNative.Selector("release");
    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);

    [Fact]
    public void ReleasingLibrariesFunctionsAndComputePipelinesGivesTheirMemoryBack()
    {
        if (!RequireDevice()) return;
        var device = Device.Value;
        // One of each first: the driver's own first-use allocations are not a per-entry cost.
        ReleaseAll(BuildCompute(device, 1, salt: 0xFFFF));

        var baseline = Sample();
        var objects = BuildLibraries(device, Count, salt: 0, Kernel);
        var libraries = Sample();
        BuildFunctions(objects, "k");
        var functions = Sample();
        BuildComputePipelines(device, objects);
        var pipelines = Sample();
        ReleasePipelines(objects);
        var pipelinesReleased = Sample();
        ReleaseAll(objects);
        var released = Sample();

        Report("compute", baseline, libraries, functions, pipelines, pipelinesReleased, released);
        var grown = (long)pipelines.Blocks - baseline.Blocks;
        var left = (long)released.Blocks - baseline.Blocks;
        Assert.True(grown > Count, $"Building {Count} libraries, functions and pipelines grew malloc by only {grown} blocks.");
        // About a quarter stays (~12 blocks a shader): the driver's bookkeeping of what it compiled.
        Assert.True(left < grown / 2, $"Releasing them left {left} of the {grown} malloc blocks they took.");
    }

    [Fact]
    public void ReleasingModulesBeforeTheirPipelinesGivesBackEachShare()
    {
        if (!RequireDevice()) return;
        var device = Device.Value;
        ReleaseAll(BuildCompute(device, Count, salt: 0x7000));

        var baseline = Sample();
        var objects = BuildCompute(device, Count, salt: 0x3000);
        var built = Sample();
        foreach (var item in objects)
        {
            MetalNative.SendVoid(item.Function, SelRelease);
            MetalNative.SendVoid(item.Library, SelRelease);
            item.Function = item.Library = 0;
        }

        var modulesReleased = Sample();
        ReleasePipelines(objects);
        var released = Sample();
        Output(
            "modules-first", Count,
            ("built", baseline, built),
            ("modules released", built, modulesReleased),
            ("pipelines released", modulesReleased, released));
        // A state lives on without its library and function, which give back their share at
        // once (the bound releases a module once no pipeline is being made from it); the
        // state's own binary goes with the state.
        Assert.True(modulesReleased.Blocks < built.Blocks, "Releasing the modules of live pipeline states freed nothing.");
        Assert.True(released.Blocks < modulesReleased.Blocks, "Releasing the pipeline states freed nothing.");
    }

    [Fact]
    public void ReleasingRenderPipelinesGivesTheirMemoryBack()
    {
        if (!RequireDevice()) return;
        var device = Device.Value;
        ReleaseAll(BuildRender(device, 1, salt: 0xFFFF));

        var baseline = Sample();
        var objects = BuildRender(device, Count, salt: 0);
        var built = Sample();
        ReleasePipelines(objects);
        var pipelinesReleased = Sample();
        ReleaseAll(objects);
        var released = Sample();

        Output(
            "render", Count,
            ("libraries+functions+pipelines", baseline, built),
            ("pipelines released", built, pipelinesReleased),
            ("all released", built, released));
        var grown = (long)built.Blocks - baseline.Blocks;
        var left = (long)released.Blocks - baseline.Blocks;
        Assert.True(left < grown / 4, $"Releasing them left {left} of the {grown} malloc blocks they took.");
    }

    [Fact]
    public void ALibraryOfARecordedTitleIsMeasured()
    {
        if (!RequireDevice()) return;
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_MSL_LIST") is not { Length: > 0 } path || !File.Exists(path))
        {
            output.WriteLine("SHARPEMU_TEST_MSL_LIST names no recorded library list; skipped.");
            return;
        }

        var device = Device.Value;
        var (entries, _) = MetalLibraryPrewarm.Load(path, out _);
        var sources = entries.Take(Count).Select(entry => (MetalLibraryPrewarm.Decompress(entry.Compressed), entry.UsesPerVertexInputs)).ToList();
        var objects = new List<Objects>(sources.Count);
        var baseline = Sample();
        long sourceBytes = 0;
        foreach (var (source, perVertex) in sources)
        {
            sourceBytes += source.Length;
            var library = MetalRenderHost.TryCompileLibrary(device, source, perVertex, out var error);
            Assert.True(library != 0, error);
            objects.Add(new Objects { Library = library });
        }

        var libraries = Sample();
        foreach (var item in objects)
        {
            // The first function of the library: a recorded source has one entry point.
            var names = MetalNative.Send(item.Library, MetalNative.Selector("functionNames"));
            var name = MetalNative.ReadNsString(MetalNative.SendAtIndex(names, MetalNative.Selector("objectAtIndex:"), 0))!;
            item.Function = MetalNative.Send(item.Library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString(name));
        }

        var functions = Sample();
        ReleaseAll(objects);
        var released = Sample();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"msl_kb_avg={sourceBytes / 1024.0 / Math.Max(objects.Count, 1):F1}"));
        Output(
            "recorded", objects.Count,
            ("libraries", baseline, libraries),
            ("functions", libraries, functions),
            ("all released", functions, released));
    }

    private sealed class Objects
    {
        public nint Library;
        public nint Function;
        public nint VertexFunction;
        public nint Pipeline;
    }

    private static string Kernel(int index, int salt) =>
        $$"""
        #include <metal_stdlib>
        using namespace metal;
        kernel void k(device uint* data [[buffer(0)]], uint id [[thread_position_in_grid]])
        {
            uint value = data[id] ^ {{index * 7919 + salt}}u;
            for (uint step = 0; step < 16; step++)
            {
                value = value * 1664525u + {{index + 1013904223 + salt}}u;
                data[(id + step) & 1023u] += value >> {{index % 13 + 1}}u;
            }
            data[id] = value;
        }
        """;

    private static string RenderSource(int index, int salt) =>
        $$"""
        #include <metal_stdlib>
        using namespace metal;
        struct Out { float4 position [[position]]; float4 color; };
        vertex Out v(uint id [[vertex_id]])
        {
            Out o;
            o.position = float4(float(id & 1u) * 2.0 - 1.0, float(id >> 1u) * 2.0 - 1.0, 0.0, 1.0);
            o.color = float4({{index}}.0 / 255.0, {{salt % 255}}.0 / 255.0, 0.25, 1.0);
            return o;
        }
        fragment float4 f(Out in [[stage_in]]) { return in.color * {{index + 1}}.0; }
        """;

    private static List<Objects> BuildLibraries(nint device, int count, int salt, Func<int, int, string> source)
    {
        var objects = new List<Objects>(count);
        for (var index = 0; index < count; index++)
        {
            var library = MetalRenderHost.TryCompileLibrary(device, source(index, salt), usesPerVertexInputs: false, out var error);
            Assert.True(library != 0, error);
            objects.Add(new Objects { Library = library });
        }

        return objects;
    }

    private static void BuildFunctions(List<Objects> objects, string name)
    {
        foreach (var item in objects)
        {
            item.Function = MetalNative.Send(item.Library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString(name));
            Assert.NotEqual(0, item.Function);
        }
    }

    private static void BuildComputePipelines(nint device, List<Objects> objects)
    {
        foreach (var item in objects)
        {
            nint error = 0;
            item.Pipeline = MetalNative.Send(device, MetalNative.Selector("newComputePipelineStateWithFunction:error:"), item.Function, ref error);
            Assert.NotEqual(0, item.Pipeline);
        }
    }

    private static List<Objects> BuildCompute(nint device, int count, int salt)
    {
        var objects = BuildLibraries(device, count, salt, Kernel);
        BuildFunctions(objects, "k");
        BuildComputePipelines(device, objects);
        return objects;
    }

    private static List<Objects> BuildRender(nint device, int count, int salt)
    {
        var objects = BuildLibraries(device, count, salt, RenderSource);
        foreach (var item in objects)
        {
            item.VertexFunction = MetalNative.Send(item.Library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString("v"));
            item.Function = MetalNative.Send(item.Library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString("f"));
            var pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                var descriptor = MetalNative.Send(
                    MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Selector("alloc")),
                    MetalNative.Selector("init"));
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), item.VertexFunction);
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), item.Function);
                var attachment = MetalNative.SendAtIndex(
                    MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments")), MetalNative.Selector("objectAtIndexedSubscript:"), 0);
                MetalNative.SendVoidNUInt(attachment, MetalNative.Selector("setPixelFormat:"), 80);
                nint error = 0;
                item.Pipeline = MetalNative.Send(device, MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref error);
                MetalNative.SendVoid(descriptor, SelRelease);
                Assert.True(item.Pipeline != 0, MetalNative.DescribeError(error));
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        return objects;
    }

    private static void ReleasePipelines(List<Objects> objects)
    {
        foreach (var item in objects)
        {
            if (item.Pipeline != 0)
            {
                MetalNative.SendVoid(item.Pipeline, SelRelease);
                item.Pipeline = 0;
            }
        }
    }

    private static void ReleaseAll(List<Objects> objects)
    {
        ReleasePipelines(objects);
        foreach (var item in objects)
        {
            foreach (var handle in new[] { item.Function, item.VertexFunction, item.Library })
            {
                if (handle != 0)
                {
                    MetalNative.SendVoid(handle, SelRelease);
                }
            }

            item.Function = item.VertexFunction = item.Library = 0;
        }
    }

    private void Report(
        string label,
        (uint Blocks, ulong Bytes, ulong Footprint) baseline,
        (uint Blocks, ulong Bytes, ulong Footprint) libraries,
        (uint Blocks, ulong Bytes, ulong Footprint) functions,
        (uint Blocks, ulong Bytes, ulong Footprint) pipelines,
        (uint Blocks, ulong Bytes, ulong Footprint) pipelinesReleased,
        (uint Blocks, ulong Bytes, ulong Footprint) released) =>
        Output(
            label, Count,
            ("libraries", baseline, libraries),
            ("functions", libraries, functions),
            ("pipelines", functions, pipelines),
            ("pipelines released", pipelines, pipelinesReleased),
            ("all released", pipelines, released));

    private void Output(string label, int count, params (string Phase, (uint Blocks, ulong Bytes, ulong Footprint) From, (uint Blocks, ulong Bytes, ulong Footprint) To)[] phases)
    {
        var text = new StringBuilder(label);
        foreach (var (phase, from, to) in phases)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" | {phase}: blocks {((long)to.Blocks - from.Blocks) / (double)count:F1}/entry " +
                $"malloc {((long)to.Bytes - (long)from.Bytes) / 1024.0 / count:F1} KB/entry " +
                $"footprint {((long)to.Footprint - (long)from.Footprint) / 1024.0 / count:F1} KB/entry");
        }

        output.WriteLine(text.ToString());
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Device objects are built only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static (uint Blocks, ulong Bytes, ulong Footprint) Sample()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        malloc_zone_statistics(0, out var statistics);
        return (statistics.BlocksInUse, statistics.SizeInUse, PhysFootprint());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MallocStatistics
    {
        public uint BlocksInUse;
        public nuint SizeInUse;
        public nuint MaxSizeInUse;
        public nuint SizeAllocated;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern void malloc_zone_statistics(nint zone, out MallocStatistics statistics);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_task_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern unsafe int task_info(uint task, uint flavor, void* info, ref uint count);

    private static unsafe ulong PhysFootprint()
    {
        var info = stackalloc byte[512];
        var count = 512u / 4;
        return task_info(mach_task_self(), 22, info, ref count) == 0 ? *(ulong*)(info + 144) : 0;
    }
}
