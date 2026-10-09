// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

/// <summary>Explicit opt-in for a CPU decorator whose GPU-used reads, writes and
/// mapped capabilities are exactly those of one immutable inner memory. This
/// excludes thread-local CPU failure bookkeeping only; it grants no mapping,
/// byte, lifetime or native authority. Generic ICpuMemoryWrapper is not enough.</summary>
public interface ICpuGpuMemoryView
{
    // A scalar, stable, non-null link with no callback, allocation or provider
    // effect. Decorators with GPU-visible behavior must not implement this.
    ICpuMemory GpuMemoryView { get; }
}

public static class CpuGpuMemoryView
{
    private const int MaximumLinks = 16;

    /// <summary>Resolves only explicitly transparent GPU links, preserving the
    /// chosen exact reader/provider. Malformed or changing links refuse; they
    /// never fall back to a generic wrapper's inner memory.</summary>
    public static ICpuMemory Resolve(ICpuMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return Resolve(memory, 0);
    }

    private static ICpuMemory Resolve(ICpuMemory memory, int depth)
    {
        if (memory is not ICpuGpuMemoryView view) return memory;
        if (depth >= MaximumLinks) throw new InvalidOperationException("GPU memory view chain is cyclic or exceeds its bounded link count.");
        var inner = view.GpuMemoryView ?? throw new InvalidOperationException("GPU memory view link is null.");
        if (ReferenceEquals(inner, memory)) throw new InvalidOperationException("GPU memory view link is cyclic.");
        if (!ReferenceEquals(inner, view.GpuMemoryView)) throw new InvalidOperationException("GPU memory view link changed during resolution.");
        var resolved = Resolve(inner, depth + 1);
        if (!ReferenceEquals(inner, view.GpuMemoryView)) throw new InvalidOperationException("GPU memory view link changed during resolution.");
        return resolved;
    }
}
