// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

// mode=gpureport: ranks the shaders of one or more mode=gpu runs and tabulates each variant against
// its baseline. Arguments: csv=<gpu-*.csv[,...]> static=<static-*.csv> out=<dir> top=20 floor=20
//   hot=<label|vs-prefix|ps-prefix|share;...>  production pairs whose synthetic cost is reported on their own,
//   share being their percentage of profiled GPU time (an empty prefix leaves that half out).
// floor is the smallest baseline time (microseconds) a shader needs to count in a ratio statistic: below it
// the fixed cost of a command buffer is most of the number.
internal static class GpuBenchReport
{
    private sealed record Sample(string Phase, int Index, string Id, string Kind, string Hash, string Variant, string Status, double Median);

    public static int Run(Func<string, string, string> arg)
    {
        var inputs = arg("csv", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var outDir = arg("out", ".");
        var top = int.Parse(arg("top", "20"), CultureInfo.InvariantCulture);
        var floor = double.Parse(arg("floor", "20"), CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        var samples = new List<Sample>();
        foreach (var path in inputs)
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var f = SplitCsv(line);
                if (f.Count < 13)
                {
                    continue;
                }

                samples.Add(new Sample(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), f[2], f[3], f[4], f[5], f[12],
                    double.Parse(f[7], CultureInfo.InvariantCulture)));
            }
        }

        var statics = new Dictionary<int, string[]>();
        string staticHeader = "";
        if (arg("static", "") is { Length: > 0 } staticPath)
        {
            var lines = File.ReadAllLines(staticPath);
            staticHeader = lines[0];
            foreach (var line in lines.Skip(1))
            {
                var f = line.Split(',');
                statics[int.Parse(f[0], CultureInfo.InvariantCulture)] = f;
            }
        }

        // The deep (more repetitions) number replaces the full one where there is one.
        double? Value(int index, string variant, string phase) =>
            samples.Where(s => s.Index == index && s.Variant == variant && s.Phase == phase && s.Status == "ok").Select(s => (double?)s.Median).FirstOrDefault();
        var byIndex = samples.GroupBy(s => s.Index).ToDictionary(g => g.Key, g => g.First());
        double? Best(int index, string variant) => Value(index, variant, "deep") ?? Value(index, variant, "full");
        var ranked = byIndex.Keys.Select(i => (Index: i, Us: Best(i, "base"))).Where(x => x.Us.HasValue).OrderByDescending(x => x.Us!.Value).ToList();
        var variants = samples.Select(s => s.Variant).Distinct().ToList();

        var md = new StringBuilder();
        md.AppendLine("> Synthetic GPU time: every shader runs on zero (or patterned) buffers, blank textures and a fixed workload. Zero or patterned inputs change control flow");
        md.AppendLine("> (trip counts, early-outs, touched memory), so these numbers are a cost proxy for ranking and for A/B of one shader against its own variant in the same");
        md.AppendLine("> process; they are not production times. Mesh programs run with a per-program vertex count (grid <= 30000 groups), so they do not compare across programs at equal vertices.");
        md.AppendLine();
        md.AppendLine("## Synthetic GPU time by shader kind (base variant)");
        md.AppendLine();
        md.AppendLine("| kind | shaders | ran | failed | n/a | sum us | median us | p90 us | max us |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var kind in new[] { "cs", "vs", "ms", "ps" })
        {
            var all = byIndex.Values.Where(s => s.Kind == kind).Select(s => s.Index).ToList();
            var ok = all.Select(i => Best(i, "base")).Where(v => v.HasValue).Select(v => v!.Value).Order().ToList();
            if (all.Count == 0)
            {
                continue;
            }

            var failed = all.Count(i => !Best(i, "base").HasValue);
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {kind} | {all.Count} | {ok.Count} | {failed} | 0 | {ok.Sum():F0} | {(ok.Count > 0 ? ok[ok.Count / 2] : 0):F0} | {(ok.Count > 0 ? ok[Math.Min(ok.Count - 1, (int)(ok.Count * 0.9))] : 0):F0} | {(ok.Count > 0 ? ok[^1] : 0):F0} |"));
        }

        md.AppendLine();
        var failures = samples.Where(s => s.Phase == "full" && s.Variant == "base" && s.Status != "ok" && s.Status != "n/a").ToList();
        if (failures.Count > 0)
        {
            md.AppendLine($"Failed to run (base variant): {failures.Count}. By status: " + string.Join(", ", failures.GroupBy(s => s.Status).Select(g => $"{g.Key} x{g.Count()}")));
            md.AppendLine();
        }

        md.AppendLine($"## Top {top} by synthetic GPU time (base)");
        md.AppendLine();
        md.AppendLine("| # | shader | kind | us | msl kB | tex | loads | loops | branches | barriers | tg | notes |");
        md.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|");
        var rank = 0;
        foreach (var (index, us) in ranked.Take(top))
        {
            var st = statics.GetValueOrDefault(index);
            string S(int column) => st is null ? "" : st[column];
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {++rank} | {byIndex[index].Id} | {byIndex[index].Kind} | {us!.Value:F0} | {(st is null ? 0 : double.Parse(S(4), CultureInfo.InvariantCulture) / 1024):F0} | {S(6)} | {S(9)} | {S(11)} | {S(12)} | {S(13)} | {S(18)} | {Note(st)} |"));
        }

        md.AppendLine();
        var csv = new StringBuilder("rank,idx,id,kind,hash,base_us");
        foreach (var variant in variants.Where(v => v != "base"))
        {
            csv.Append(',').Append(variant).Append("_us");
        }

        csv.AppendLine(staticHeader.Length > 0 ? "," + string.Join(',', staticHeader.Split(',').Skip(4)) : "");
        rank = 0;
        foreach (var (index, us) in ranked)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{++rank},{index},{byIndex[index].Id},{byIndex[index].Kind},{byIndex[index].Hash},{us!.Value:F1}");
            foreach (var variant in variants.Where(v => v != "base"))
            {
                csv.Append(',').Append(Best(index, variant) is { } v ? v.ToString("F1", CultureInfo.InvariantCulture) : "");
            }

            csv.AppendLine(statics.TryGetValue(index, out var st) ? "," + string.Join(',', st.Skip(4)) : "");
        }

        File.WriteAllText(Path.Combine(outDir, "ranking.csv"), csv.ToString());

        // ---- variant tables -----------------------------------------------------------------
        md.AppendLine("## Variant A/B");
        md.AppendLine();
        md.AppendLine($"Ratios use shaders whose baseline is at least {floor:F0} us. `all` is every such shader (full phase, 5 reps); `top30` is the 30 slowest base shaders (deep phase, more reps). Delta is the change of the summed time, negative is faster; `geo` is the geometric-mean ratio per shader; `better/worse` count shaders more than 3% faster/slower. `early`/`earlyd` are compared with `basez`/`basezd` (same depth-rejecting scenario), every other variant with `base`.");
        md.AppendLine();
        md.AppendLine("| variant | scope | n | sum base us | sum variant us | delta | geo ratio | better | worse |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        var top30 = ranked.Take(30).Select(x => x.Index).ToHashSet();
        var variantCsv = new StringBuilder("variant,scope,n,sum_base_us,sum_variant_us,delta_pct,geo_ratio,better,worse\n");
        foreach (var variant in variants.Where(v => v is not ("base" or "basez" or "basezd")))
        {
            var reference = variant == "early" ? "basez" : variant == "earlyd" ? "basezd" : "base";
            foreach (var (scope, filter) in new (string, Func<int, bool>)[]
            {
                ("all", _ => true), ("top30", i => top30.Contains(i)), ("vs", i => byIndex[i].Kind == "vs"), ("ms", i => byIndex[i].Kind == "ms"),
                ("ps", i => byIndex[i].Kind == "ps"), ("cs", i => byIndex[i].Kind == "cs"),
            })
            {
                var pairs = new List<(double Base, double Value)>();
                foreach (var index in byIndex.Keys.Where(filter))
                {
                    // The top-30 scope compares deep with deep where both exist, the others full with full.
                    var phase = scope == "top30" ? "deep" : "full";
                    var baseline0 = scope == "top30" ? Best(index, reference) : Value(index, reference, phase);
                    var value0 = scope == "top30" ? Best(index, variant) : Value(index, variant, phase);
                    if (baseline0 is { } baseline && value0 is { } value && baseline >= floor)
                    {
                        pairs.Add((baseline, value));
                    }
                }

                if (pairs.Count == 0)
                {
                    continue;
                }

                var sumBase = pairs.Sum(p => p.Base);
                var sumValue = pairs.Sum(p => p.Value);
                var geo = Math.Exp(pairs.Average(p => Math.Log(p.Value / p.Base)));
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"| {variant} | {scope} | {pairs.Count} | {sumBase:F0} | {sumValue:F0} | {(sumValue / sumBase - 1) * 100:+0.0;-0.0}% | {geo:F3} | {pairs.Count(p => p.Value < p.Base * 0.97)} | {pairs.Count(p => p.Value > p.Base * 1.03)} |");
                md.AppendLine(line);
                variantCsv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{variant},{scope},{pairs.Count},{sumBase:F1},{sumValue:F1},{(sumValue / sumBase - 1) * 100:F2},{geo:F4},{pairs.Count(p => p.Value < p.Base * 0.97)},{pairs.Count(p => p.Value > p.Base * 1.03)}"));
            }
        }

        File.WriteAllText(Path.Combine(outDir, "variants.csv"), variantCsv.ToString());

        // ---- hot pairs ----------------------------------------------------------------------
        if (arg("hot", "") is { Length: > 0 } hot)
        {
            md.AppendLine();
            md.AppendLine("## Production hot list (synthetic cost of each half, variant deltas of vs+ps summed)");
            md.AppendLine();
            var shown = variants.Where(v => v is not ("base" or "basez" or "basezd" or "early" or "earlyd")).ToList();
            md.AppendLine("| pair | prod share | vs us | ps us | " + string.Join(" | ", shown) + " |");
            md.AppendLine("|---|---:|---:|---:|" + string.Concat(shown.Select(_ => "---:|")));
            var weighted = shown.ToDictionary(v => v, _ => 0.0);
            foreach (var entry in hot.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                var share = double.Parse(parts[3], CultureInfo.InvariantCulture);
                var members = byIndex.Values.Where(s =>
                    (parts[1].Length > 0 && s.Kind is "vs" or "ms" && s.Hash.StartsWith("0x" + parts[1], StringComparison.OrdinalIgnoreCase)) ||
                    (parts[2].Length > 0 && s.Kind is "ps" or "cs" && s.Hash.StartsWith("0x" + parts[2], StringComparison.OrdinalIgnoreCase))).Select(s => s.Index).ToList();
                double Sum(string variant, params string[] kinds) =>
                    members.Where(i => kinds.Contains(byIndex[i].Kind) && Best(i, variant).HasValue && Best(i, "base").HasValue).Sum(i => Best(i, variant)!.Value);
                var vs = Sum("base", "vs", "ms", "cs");
                var ps = Sum("base", "ps");
                var cells = new List<string>();
                foreach (var variant in shown)
                {
                    var matched = members.Where(i => Best(i, variant).HasValue && Best(i, "base").HasValue).ToList();
                    var baseline = matched.Sum(i => Best(i, "base")!.Value);
                    var value = matched.Sum(i => Best(i, variant)!.Value);
                    var delta = baseline > 0 ? (value / baseline - 1) * 100 : 0;
                    weighted[variant] += delta * share / 100;
                    cells.Add(string.Create(CultureInfo.InvariantCulture, $"{delta:+0.0;-0.0}%"));
                }

                md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {parts[0]} ({members.Count} items) | {share:F1}% | {vs:F0} | {ps:F0} | {string.Join(" | ", cells)} |"));
            }

            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| share-weighted change of profiled GPU time (these entries only) | | | | {string.Join(" | ", shown.Select(v => $"{weighted[v]:+0.00;-0.00}%"))} |"));
        }

        File.WriteAllText(Path.Combine(outDir, "tables.md"), md.ToString());
        Console.WriteLine(md.ToString());
        return 0;
    }

    private static string Note(string[]? st)
    {
        if (st is null)
        {
            return "";
        }

        var notes = new List<string>();
        if (st[15] == "1")
        {
            notes.Add("discard");
        }

        if (st[16] == "1")
        {
            notes.Add("depth-out");
        }

        if (st[17] == "1")
        {
            notes.Add("early-z");
        }

        if (st[10] != "0")
        {
            notes.Add("stores/atomics " + st[10]);
        }

        return string.Join(", ", notes);
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var text = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(text.ToString());
                text.Clear();
            }
            else
            {
                text.Append(c);
            }
        }

        fields.Add(text.ToString());
        return fields;
    }
}
