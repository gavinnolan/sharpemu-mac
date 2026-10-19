// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// A Stream flush (the end of a guest submission) leaves the command buffer open while the
/// worker has more to record, and commits once the stream is idle or the buffer passes a
/// submission, encoder or time limit. With coalescing off every Stream flush commits.
/// </summary>
public sealed class MetalFlushCoalesceTests
{
    private const int MaxSubmissions = 8;
    private const int MaxEncoders = 64;
    private const long MaxTicks = 2000;

    [Theory]
    // Coalescing off: always commits.
    [InlineData(false, false, 1, 0, 0L, true)]
    // More work is runnable and the buffer is small and young: deferred.
    [InlineData(true, false, 1, 0, 0L, false)]
    [InlineData(true, false, 7, 63, 1999L, false)]
    // Nothing else to record: commit now, it costs nothing.
    [InlineData(true, true, 1, 0, 0L, true)]
    // Each limit on its own commits.
    [InlineData(true, false, 8, 0, 0L, true)]
    [InlineData(true, false, 1, 64, 0L, true)]
    [InlineData(true, false, 1, 0, 2000L, true)]
    public void AStreamFlushCommitsOnlyWhenIdleOrOverALimit(
        bool coalesce, bool streamIdle, int submissions, int encoders, long wantedTicks, bool expected) =>
        Assert.Equal(expected, MetalRenderHost.ShouldCommitStreamFlush(
            coalesce, streamIdle, submissions, encoders, wantedTicks, MaxSubmissions, MaxEncoders, MaxTicks));
}
