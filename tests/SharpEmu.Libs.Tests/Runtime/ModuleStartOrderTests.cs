// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Runtime;
using Xunit;

namespace SharpEmu.Libs.Tests.Runtime;

public sealed class ModuleStartOrderTests
{
    [Fact]
    public void DependenciesStartBeforeTheirDependents()
    {
        // File-name order would start libA first although it needs libZ.
        string[] paths = ["/m/libA.prx", "/m/libB.sprx", "/m/libZ.prx"];
        IReadOnlyList<string>[] needed =
        [
            ["libZ", "libc", "libkernel"],
            [],
            ["libB.sprx"],
        ];

        Assert.Equal([1, 2, 0], ModuleStartOrder.Compute(paths, needed));
    }

    [Fact]
    public void IndependentModulesKeepDiscoveryOrder()
    {
        string[] paths = ["/m/b.prx", "/m/a.prx", "/m/c.prx"];
        IReadOnlyList<string>[] needed = [[], ["libSceHleOnly"], []];

        Assert.Equal([0, 1, 2], ModuleStartOrder.Compute(paths, needed));
    }

    [Fact]
    public void CyclesStartEveryModuleExactlyOnce()
    {
        string[] paths = ["/m/x.prx", "/m/y.prx", "/m/z.prx"];
        IReadOnlyList<string>[] needed = [["y"], ["x"], ["z", "x"]];

        var order = ModuleStartOrder.Compute(paths, needed);
        Assert.Equal([1, 0, 2], order);
        Assert.Equal(3, order.Distinct().Count());
    }
}
