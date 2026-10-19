// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.ShaderCompiler.Resources;

// Offline runs of the static shader precompile over a copy of a title's lists.
internal static class Derivation
{
    private static string Trim(string text) => text.Length > 90 ? text[..90] : text;

    // The static precompile end to end over a copy of a title's lists: plans the units,
    // translates the first limit= of them and stores what is new into a scratch archive,
    // with the time, the failures, the library sizes and the process footprint.
    public static int Static(string dir, Func<string, string, string> arg)
    {
        MetalGpuBackend.Register();
        var compiler = GuestGpu.Current;
        MetalRenderHost.ApplyTranslationSwitches();
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        var limit = int.Parse(arg("limit", "300"));
        var threads = int.Parse(arg("threads", "2"));
        var (startFootprint, _) = MetalHostMemory.Footprint();
        List<ShaderCorpusRecord> records;
        using (var list = GraphicsPrewarmList.Open(dir) ?? throw new InvalidOperationException("no graphics list"))
        {
            records = list.Loaded().Select(item => GraphicsPrewarmList.Decode(item)).ToList();
        }

        var opened = System.Diagnostics.Stopwatch.GetTimestamp();
        using var planner = StaticShaderPlanner.Open(dir, records, arg("seed", "") is { Length: > 0 } seed ? seed : null)
            ?? throw new InvalidOperationException("no inventory");
        var plan = planner.Plan(static _ => false);
        Console.WriteLine($"planner: programs={planner.Programs} pairs={planner.Pairs} signatures={planner.Signatures} " +
            $"states={planner.States} votes={planner.FirstVariantVotes}/{planner.SecondVariantVotes} jobs={plan.Count} " +
            $"(compute={plan.Count(j => j.Kind == StaticShaderKind.Compute)} graphics={plan.Count(j => j.Kind == StaticShaderKind.Graphics)} " +
            $"created={plan.Count(j => j.Created)}) plan_s={System.Diagnostics.Stopwatch.GetElapsedTime(opened).TotalSeconds:F1}");
        if (arg("learn", "") is { Length: > 0 } learn)
        {
            Console.WriteLine($"seed written={planner.TrySaveSeed(learn, "BENCH", "1.0")} bytes={new FileInfo(learn).Length}");
        }

        var root = Path.Combine(Path.GetTempPath(), $"sharpemu-static-bench-{Guid.NewGuid():N}");
        using var archive = MetalBinaryArchive.Open(root, device, MetalBinaryArchive.TranslatorStamp(), MetalBinaryArchive.DiskBudgetBytes);
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        void Count(string key) => counts.AddOrUpdate(key, 1, static (_, value) => value + 1);
        var sizes = new System.Collections.Concurrent.ConcurrentBag<(string Entry, long Bytes)>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ulong peak = startFootprint;
        var next = -1;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var translateTicks = 0L;
        var storeTicks = 0L;
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < Math.Min(limit, plan.Count))
            {
                var job = plan[index];
                if (!planner.TryBuild(job, out var build, out var reason))
                {
                    Count($"{job.Kind} underivable: {Trim(reason)}");
                    continue;
                }

                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var shaders = MetalStaticPrecompile.Translate(build, compiler);
                Interlocked.Add(ref translateTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                Count($"{job.Kind} built: pixel={(build.Pixel is null ? 0 : 1)} vertex={(build.Vertex is null ? 0 : 1)} translated={shaders.Count}");
                foreach (var shader in shaders)
                {
                    var key = MetalBinaryArchive.LibraryKey(shader.Source, shader.UsesPerVertexInputs);
                    if (archive.HasLibrary(key))
                    {
                        Count($"{shader.EntryPoint} present");
                        continue;
                    }

                    var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var bytes = MetalStaticPrecompile.Store(archive, device, shader, key, out var error);
                    Interlocked.Add(ref storeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t1);
                    if (bytes > 0)
                    {
                        sizes.Add((shader.EntryPoint, bytes));
                        Count($"{shader.EntryPoint} stored");
                    }
                    else
                    {
                        Count($"{shader.EntryPoint} {(error is null ? "skipped" : "failed")}");
                        if (error is not null && errors.Count < 6)
                        {
                            errors.Enqueue($"{shader.EntryPoint}: {Trim(error)}");
                        }
                    }

                    var (current, _) = MetalHostMemory.Footprint();
                    InterlockedMax(ref peak, current);
                }
            }
        })).ToList();
        workers.ForEach(static thread => thread.Start());
        workers.ForEach(static thread => thread.Join());
        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        foreach (var (key, value) in counts.OrderBy(pair => pair.Key))
        {
            Console.WriteLine($"{value,6} {key}");
        }

        foreach (var error in errors)
        {
            Console.WriteLine($"  error {error}");
        }

        foreach (var group in sizes.GroupBy(size => size.Entry))
        {
            Console.WriteLine($"library {group.Key}: n={group.Count()} mean_kb={group.Average(size => size.Bytes) / 1024.0:F1} " +
                $"max_kb={group.Max(size => size.Bytes) / 1024.0:F1}");
        }

        var (endFootprint, _) = MetalHostMemory.Footprint();
        Console.WriteLine($"jobs={Math.Min(limit, plan.Count)} threads={threads} s={seconds:F1} translate_s={translateTicks / (double)System.Diagnostics.Stopwatch.Frequency:F1} " +
            $"store_s={storeTicks / (double)System.Diagnostics.Stopwatch.Frequency:F1} archive_mb={archive.DiskBytes() / 1048576.0:F1} " +
            $"footprint_mb start={startFootprint / 1048576.0:F0} peak={peak / 1048576.0:F0} end={endFootprint / 1048576.0:F0}");
        archive.Dispose();
        Directory.Delete(root, recursive: true);
        return 0;
    }

    private static void InterlockedMax(ref ulong target, ulong value)
    {
        ulong current;
        while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    // Each recorded compute derived again from its AGC header (the inventory's) alone, and
    // compared by MSL with the recorded one.
    public static int ComputeMatch(string dir, Func<string, string, string> arg)
    {
        MetalGpuBackend.Register();
        var compiler = GuestGpu.Current;
        MetalRenderHost.ApplyTranslationSwitches();
        using var list = ShaderPrewarmList.Open(dir) ?? throw new InvalidOperationException("no compute list");
        var records = list.LoadedComputes();
        var inventoryRecords = new List<ShaderCorpusRecord>();
        using var planner = StaticShaderPlanner.Open(dir, inventoryRecords, null) ?? throw new InvalidOperationException("no inventory");
        var byCode = planner.Plan(static _ => false).Where(job => job.Kind == StaticShaderKind.Compute).ToList();
        var recordedCodes = records.Select(static item => (item.Record.Hash, item.Record.CodeSize)).ToHashSet();
        var derived = new Dictionary<(ulong Hash, uint Size), List<string>>();
        foreach (var job in byCode)
        {
            if (planner.TryBuild(job, out var build, out _) && build.Compute is { } compute &&
                recordedCodes.Contains((compute.Hash, compute.CodeSize)) &&
                MetalStaticPrecompile.Translate(build, compiler) is [var shader])
            {
                var key = (compute.Hash, compute.CodeSize);
                (derived.TryGetValue(key, out var sources) ? sources : derived[key] = []).Add(shader.Source);
            }
        }

        var counts = new Dictionary<string, int>();
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        var examples = int.Parse(arg("examples", "3"));
        foreach (var (record, code) in records)
        {
            if (!derived.TryGetValue((record.Hash, record.CodeSize), out var sources))
            {
                Count("not derived (not in inventory or underivable)");
                continue;
            }

            var recorded = MetalStaticPrecompile.Translate(new StaticShaderBuild(record, code, null, null), compiler);
            if (recorded is not [var shader])
            {
                Count("recorded does not translate");
                continue;
            }

            if (sources.Contains(shader.Source))
            {
                Count("same");
                continue;
            }

            Count("differs");
            if (examples-- > 0)
            {
                var mine = sources[0].Split('\n');
                var theirs = shader.Source.Split('\n');
                var line = Enumerable.Range(0, Math.Min(mine.Length, theirs.Length)).FirstOrDefault(index => mine[index] != theirs[index]);
                Console.WriteLine($"--- 0x{record.Hash:X16} line {line}: recorded `{Trim(theirs.ElementAtOrDefault(line) ?? "")}` derived `{Trim(mine.ElementAtOrDefault(line) ?? "")}`");
            }
        }

        foreach (var (key, value) in counts.OrderBy(pair => pair.Key))
        {
            Console.WriteLine($"{value,6} {key}");
        }

        Console.WriteLine($"recorded computes={records.Count} derived compute programs={derived.Count}");
        return 0;
    }

}
