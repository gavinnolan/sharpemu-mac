// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Gpu.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class MetalGpuCompletionProfileTests
{
    [Fact]
    public void OverlappingDuplicateAndNestedBuffersCannotDoubleCountGpuExecution()
    {
        var actual = MetalGpuCompletionProfile.CalculateUnion([(3, 6), (1, 4), (2, 3), (1, 4), (8, 9), (6, 7)]);
        Assert.Equal(7, actual.Union); Assert.Equal(8, actual.Span); Assert.Equal(6, actual.Valid);
    }

    [Fact]
    public void InvalidOrMissingNativeTimestampsCannotCreateBusyTime()
    {
        var actual = MetalGpuCompletionProfile.CalculateUnion([(0, 5), (3, 3), (9, 2), (double.NaN, 8), (2, double.PositiveInfinity)]);
        Assert.Equal((0d, 0d, 0), actual);
        Assert.Equal((0d, 0d, 0), MetalGpuCompletionProfile.CalculateUnion([]));
    }

    [Fact]
    public void GapsAreExcludedFromGpuUnionAndNeverConfusedWithCallerWaitTime()
    {
        var actual = MetalGpuCompletionProfile.CalculateUnion([(100.25, 100.5), (102, 102.125), (104, 104.5)]);
        Assert.Equal(.875, actual.Union); Assert.Equal(4.25, actual.Span); Assert.Equal(3, actual.Valid);
    }
}
