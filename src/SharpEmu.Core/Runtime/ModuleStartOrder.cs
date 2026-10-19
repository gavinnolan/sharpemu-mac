// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.IO;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// The order preloaded modules start in (SPEC §5.1): every module after the
/// modules it names in DT_SCE_NEEDED_MODULE, as rtld initializes a DAG from
/// the leaves up. Independent modules, and modules on a dependency cycle,
/// keep their discovery order, so the result is deterministic.
/// </summary>
internal static class ModuleStartOrder
{
    /// <summary>
    /// Indices into <paramref name="modulePaths"/> in start order.
    /// <paramref name="neededModuleNames"/>[i] lists the names module i
    /// needs; a name matches a module by file name with or without extension
    /// (".prx"/".sprx"), ignoring case. Names that match no listed module
    /// (HLE libraries) are ignored.
    /// </summary>
    public static int[] Compute(IReadOnlyList<string> modulePaths, IReadOnlyList<IReadOnlyList<string>> neededModuleNames)
    {
        ArgumentNullException.ThrowIfNull(modulePaths);
        ArgumentNullException.ThrowIfNull(neededModuleNames);
        if (modulePaths.Count != neededModuleNames.Count)
        {
            throw new ArgumentException("Each module needs its dependency list.", nameof(neededModuleNames));
        }

        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < modulePaths.Count; i++)
        {
            indexByName.TryAdd(Path.GetFileName(modulePaths[i]), i);
            indexByName.TryAdd(Path.GetFileNameWithoutExtension(modulePaths[i]), i);
        }

        var order = new List<int>(modulePaths.Count);
        var state = new byte[modulePaths.Count]; // 0 new, 1 visiting, 2 done
        for (var i = 0; i < modulePaths.Count; i++)
        {
            Visit(i);
        }

        return order.ToArray();

        void Visit(int index)
        {
            if (state[index] != 0)
            {
                // Done, or on the current path (a cycle: start in discovery order).
                return;
            }

            state[index] = 1;
            var dependencies = new SortedSet<int>();
            foreach (var name in neededModuleNames[index])
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    (indexByName.TryGetValue(name, out var dependency) ||
                     indexByName.TryGetValue(Path.GetFileNameWithoutExtension(name), out dependency)) &&
                    dependency != index)
                {
                    dependencies.Add(dependency);
                }
            }

            foreach (var dependency in dependencies)
            {
                Visit(dependency);
            }

            state[index] = 2;
            order.Add(index);
        }
    }
}
