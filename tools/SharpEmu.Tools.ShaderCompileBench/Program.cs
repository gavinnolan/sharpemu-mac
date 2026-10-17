// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Gpu.Metal;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Pipelines;

// Times the Metal v2 shader compile path on a title's recorded lists, per shader:
//   translate (GCN -> MSL, C#), library (newLibraryWithSource), function, pipeline state.
// Arguments are key=value:
//   dir=<copy of pipeline_cache/<title>/metal-v2>  (required; the list loader prunes files)
//   mode=translate|compute|libraries               (default compute)
//   workers=4 tworkers=4 priority=normal|below limit=0 order=list|bigfirst
//   salt=<text>  prefixes every source with a comment, so the front end misses its cache
//   opt=default|size  lang=3.2|4.0  pipeline=1|0  csv=<file>  dumpmsl=<dir>
//   libfile=<msl-libraries-*.bin name>  (mode=libraries; default the largest)
internal static class Program
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private sealed class Row
    {
        public int Index;
        public string Name = "";
        public string Source = "";
        public string Entry = "";
        public bool PerVertex;
        public double TranslateMs;
        public double LibraryMs;
        public double FunctionMs;
        public double PipelineMs;
        public long StartTicks;
        public long EndTicks;
        public string? Error;
    }

    private static Dictionary<string, string> _args = new();

    private static string Arg(string key, string fallback) => _args.TryGetValue(key, out var value) ? value : fallback;

    private static int Main(string[] argv)
    {
        foreach (var arg in argv)
        {
            var split = arg.IndexOf('=');
            if (split > 0)
            {
                _args[arg[..split]] = arg[(split + 1)..];
            }
        }

        if (Arg("mode", "") == "identity")
        {
            // identity=<dll>[,<dll>...]: the code identity and the module version id of each.
            foreach (var path in Arg("identity", "").Split(','))
            {
                using var stream = File.OpenRead(path);
                var identity = SharpEmu.ShaderCompiler.AssemblyCodeIdentity.Compute(stream);
                var mvid = System.Reflection.AssemblyName.GetAssemblyName(path).Name;
                using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
                var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                Console.WriteLine($"{identity} mvid={reader.GetGuid(reader.GetModuleDefinition().Mvid):N} {mvid} {path}");
            }

            return 0;
        }

        var dir = Arg("dir", "");
        if (dir.Length == 0)
        {
            Console.Error.WriteLine("dir= is required");
            return 2;
        }

        MetalNative.EnsureFrameworksLoaded();
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        var mode = Arg("mode", "compute");
        var limit = int.Parse(Arg("limit", "0"), CultureInfo.InvariantCulture);
        var rows = new List<Row>();
        var translateWall = 0.0;
        if (mode is "translate" or "compute")
        {
            var list = ShaderPrewarmList.Open(dir) ?? throw new InvalidOperationException("no list");
            var items = list.LoadedComputes();
            Console.Error.WriteLine($"computes={items.Count}");
            MetalGpuBackend.Register();
            var compiler = SharpEmu.Libs.Gpu.GuestGpu.Current;
            // The learned device data loads change the MSL; seed them as a start of the title would.
            if (Arg("loads", "") is { Length: > 0 } loads)
            {
                var learned = MetalDeviceDataLoadsStore.Load(Path.Combine(dir, loads), out _);
                foreach (var (hash, indices) in learned)
                {
                    SharpEmu.ShaderCompiler.Resources.DeviceDataLoads.Seed(hash, indices);
                }

                Console.Error.WriteLine($"seeded device data loads: shaders={learned.Count}");
            }
            var count = limit > 0 ? Math.Min(limit, items.Count) : items.Count;
            for (var index = 0; index < count; index++)
            {
                rows.Add(new Row { Index = index, Name = $"cs=0x{items[index].Record.Hash:X16}" });
            }

            var tworkers = int.Parse(Arg("tworkers", "4"), CultureInfo.InvariantCulture);
            var next = -1;
            var started = Stopwatch.GetTimestamp();
            RunThreads(tworkers, ThreadPriority.Normal, () =>
            {
                int index;
                while ((index = Interlocked.Increment(ref next)) < rows.Count)
                {
                    var row = rows[index];
                    var item = items[index];
                    var t0 = Stopwatch.GetTimestamp();
                    if (ShaderPrewarmCompiler.TryCompilePrewarm(item.Record, item.Code, compiler,
                            MetalRenderHost.PrewarmSharedInt64Atomics, MetalRenderHost.PrewarmExecGuardElision,
                            MetalRenderHost.PrewarmSignedZeroInfNanPreserve, out var compiled, out _, out var error) &&
                        compiled is MetalCompiledGuestShader metal)
                    {
                        row.Source = metal.Shader.Source;
                        row.Entry = metal.Shader.EntryPoint;
                        row.PerVertex = metal.Shader.UsesPerVertexInputs;
                    }
                    else
                    {
                        row.Error = error;
                    }

                    row.TranslateMs = (Stopwatch.GetTimestamp() - t0) * TicksToMs;
                }
            });
            translateWall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            list.Dispose();
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"translate: wall_ms={translateWall:F0} sum_ms={rows.Sum(r => r.TranslateMs):F0} tworkers={tworkers} " +
                $"failed={rows.Count(r => r.Error is not null)} msl_mb={rows.Sum(r => (long)r.Source.Length) / 1048576.0:F1}"));
        }
        else if (mode == "libraries")
        {
            var file = Arg("libfile", "");
            var path = file.Length > 0
                ? Path.Combine(dir, file)
                : new DirectoryInfo(dir).EnumerateFiles(MetalLibraryPrewarm.FilePrefix + "*.bin").OrderByDescending(f => f.Length).First().FullName;
            var (entries, sessions) = MetalLibraryPrewarm.Load(path, out _);
            var kept = MetalLibraryPrewarm.Select(entries, sessions);
            Console.Error.WriteLine($"libraries={kept.Count} file={Path.GetFileName(path)}");
            var count = limit > 0 ? Math.Min(limit, kept.Count) : kept.Count;
            for (var index = 0; index < count; index++)
            {
                rows.Add(new Row
                {
                    Index = index,
                    Name = $"lib=0x{kept[index].Key:X16}",
                    Source = MetalLibraryPrewarm.Decompress(kept[index].Compressed),
                    PerVertex = kept[index].UsesPerVertexInputs,
                });
            }
        }

        if (Arg("dumpmsl", "") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            foreach (var row in rows.Where(r => r.Error is null))
            {
                File.WriteAllText(Path.Combine(dump, $"{row.Index:D4}-{row.Name.Replace('=', '_')}.metal"), row.Source);
            }
        }

        if (mode != "translate")
        {
            Compile(device, rows.Where(r => r.Error is null && r.Source.Length > 0).ToList(), mode == "compute");
        }

        if (Arg("csv", "") is { Length: > 0 } csv)
        {
            var text = new StringBuilder("index,name,msl_hash,msl_bytes,lines,translate_ms,library_ms,function_ms,pipeline_ms,start_ms,end_ms,error\n");
            var origin = rows.Where(r => r.StartTicks != 0).Select(r => r.StartTicks).DefaultIfEmpty(0).Min();
            foreach (var row in rows)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"{row.Index},{row.Name},{MetalLibraryPrewarm.Key(row.Source, row.PerVertex):X16},{row.Source.Length},{row.Source.Count(c => c == '\n')},{row.TranslateMs:F2},{row.LibraryMs:F2},{row.FunctionMs:F2},{row.PipelineMs:F2}," +
                    $"{(row.StartTicks - origin) * TicksToMs:F1},{(row.EndTicks - origin) * TicksToMs:F1},\"{row.Error?.Replace('"', '\'')}\"\n");
            }

            File.WriteAllText(csv, text.ToString());
        }

        return 0;
    }

    private static void Compile(nint device, List<Row> rows, bool pipeline)
    {
        var workers = int.Parse(Arg("workers", "4"), CultureInfo.InvariantCulture);
        var priority = Arg("priority", "normal") == "below" ? ThreadPriority.BelowNormal : ThreadPriority.Normal;
        var salt = Arg("salt", "");
        var opt = Arg("opt", "default");
        var lang = Arg("lang", "3.2");
        var makePipeline = pipeline && Arg("pipeline", "1") == "1";
        var order = Arg("order", "list") == "bigfirst" ? rows.OrderByDescending(r => r.Source.Length).ToList() : rows;
        var queue = new ConcurrentQueue<Row>(order);
        var selNewLibrary = MetalNative.Selector("newLibraryWithSource:options:error:");
        var selNewFunction = MetalNative.Selector("newFunctionWithName:");
        var selNewPipeline = MetalNative.Selector("newComputePipelineStateWithFunction:error:");
        var selRelease = MetalNative.Selector("release");
        var started = Stopwatch.GetTimestamp();
        RunThreads(workers, priority, () =>
        {
            while (queue.TryDequeue(out var row))
            {
                var pool = MetalNative.objc_autoreleasePoolPush();
                nint options = 0, library = 0, function = 0, state = 0;
                try
                {
                    options = MetalNative.Send(MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
                        MetalNative.Selector("init"));
                    MetalNative.ApplyMslCompileOptions(options);
                    if (row.PerVertex)
                    {
                        MetalNative.SetMslLanguageVersion(options, SharpEmu.ShaderCompiler.Metal.Gen5MslShader.PerVertexInputsLanguageVersion);
                    }
                    else if (lang == "4.0")
                    {
                        MetalNative.SetMslLanguageVersion(options, (nuint)(4 << 16));
                    }

                    if (opt == "size")
                    {
                        MetalNative.SendVoidNUInt(options, MetalNative.Selector("setOptimizationLevel:"), 1);
                    }

                    var source = salt.Length > 0 ? $"// bench salt {salt} {row.Index}\n{row.Source}" : row.Source;
                    var t0 = Stopwatch.GetTimestamp();
                    row.StartTicks = t0;
                    nint error = 0;
                    library = MetalNative.Send(device, selNewLibrary, MetalNative.NsString(source), options, ref error);
                    var t1 = Stopwatch.GetTimestamp();
                    row.LibraryMs = (t1 - t0) * TicksToMs;
                    if (library == 0)
                    {
                        row.Error = MetalNative.DescribeError(error);
                        continue;
                    }

                    if (makePipeline)
                    {
                        function = MetalNative.Send(library, selNewFunction, MetalNative.NsString(row.Entry));
                        var t2 = Stopwatch.GetTimestamp();
                        row.FunctionMs = (t2 - t1) * TicksToMs;
                        nint pipelineError = 0;
                        state = MetalNative.Send(device, selNewPipeline, function, ref pipelineError);
                        row.PipelineMs = (Stopwatch.GetTimestamp() - t2) * TicksToMs;
                        if (state == 0)
                        {
                            row.Error = MetalNative.DescribeError(pipelineError);
                        }
                    }
                }
                finally
                {
                    row.EndTicks = Stopwatch.GetTimestamp();
                    foreach (var handle in (ReadOnlySpan<nint>)[state, function, library, options])
                    {
                        if (handle != 0)
                        {
                            MetalNative.SendVoid(handle, selRelease);
                        }
                    }

                    MetalNative.objc_autoreleasePoolPop(pool);
                }
            }
        });
        var wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        static double P(List<double> sorted, double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
        var lib = rows.Select(r => r.LibraryMs).Order().ToList();
        var pso = rows.Select(r => r.PipelineMs).Order().ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"compile: n={rows.Count} workers={workers} priority={priority} salt={(salt.Length > 0 ? salt : "-")} opt={opt} lang={lang} " +
            $"wall_ms={wall:F0} per_s={rows.Count / (wall / 1000):F1} lib_sum={lib.Sum():F0} lib_p50={P(lib, .5):F1} lib_p90={P(lib, .9):F1} lib_max={P(lib, 1):F0} " +
            $"fn_sum={rows.Sum(r => r.FunctionMs):F0} pso_sum={pso.Sum():F0} pso_p50={P(pso, .5):F1} pso_p90={P(pso, .9):F1} pso_max={P(pso, 1):F0} " +
            $"failed={rows.Count(r => r.Error is not null)}"));
        foreach (var row in rows.OrderByDescending(r => r.LibraryMs + r.PipelineMs).Take(int.Parse(Arg("top", "10"), CultureInfo.InvariantCulture)))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  slow #{row.Index} {row.Name} msl_kb={row.Source.Length / 1024.0:F0} lines={row.Source.Count(c => c == '\n')} " +
                $"tr={row.TranslateMs:F0} lib={row.LibraryMs:F0} fn={row.FunctionMs:F0} pso={row.PipelineMs:F0} {row.Error}"));
        }
    }

    private static void RunThreads(int count, ThreadPriority priority, Action body)
    {
        var threads = Enumerable.Range(0, count).Select(index => new Thread(() => body())
        {
            IsBackground = true,
            Priority = priority,
            Name = $"bench {index}",
        }).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
    }
}
