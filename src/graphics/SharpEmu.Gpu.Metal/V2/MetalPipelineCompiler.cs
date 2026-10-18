// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Pipeline states built off the command-stream thread.
///
/// The driver turns a function into a GPU binary when a pipeline state is created,
/// and it does that on whichever thread asks. Asking from the GPU worker made the
/// guest's dispatch packet wait for it: measured on Demon's Souls' load phase,
/// DISPATCH_DIRECT was ninety-five per cent of the worker's packet time at eighty-
/// eight to four hundred and ninety-seven milliseconds each.
///
/// A request now starts a compile on a bounded pool and the caller is told the
/// pipeline is not ready; the packet suspends, the command stream serves its other
/// queues, and the compile's completion wakes it. Two requests for the same key share
/// one compile.
/// </summary>
internal sealed class MetalPipelineCompiler : IDisposable
{
    private readonly ConcurrentDictionary<ulong, Pending> _pending = new();
    private readonly SemaphoreSlim _slots;
    private readonly Action _onCompleted;
    private long _started;
    private long _finished;
    private long _compileTicks;
    private long _deduped;
    private bool _disposed;

    internal sealed class Pending
    {
        public Task<nint> Work = null!;
        public long StartedTicks;
        public long CompileTicks;
    }

    public MetalPipelineCompiler(Action onCompleted, int parallelism)
    {
        _onCompleted = onCompleted;
        _slots = new SemaphoreSlim(Math.Max(1, parallelism));
    }

    /// <summary>How many compiles have been started, finished, and shared.</summary>
    public (long Started, long Finished, long Deduped, double Milliseconds) TakeCounters() =>
        (Interlocked.Exchange(ref _started, 0), Interlocked.Exchange(ref _finished, 0),
            Interlocked.Exchange(ref _deduped, 0),
            Interlocked.Exchange(ref _compileTicks, 0) * 1000.0 / Stopwatch.Frequency);

    /// <summary>Compiles in flight right now.</summary>
    public int InFlight => _pending.Count;

    /// <summary>
    /// The finished pipeline state for this key, or zero while it is still being
    /// built. <paramref name="build"/> runs on the pool and must be safe to call from
    /// any thread; creating a pipeline state from a device and a function is.
    /// </summary>
    public bool TryTake(ulong key, Func<nint> build, out nint state)
    {
        state = 0;
        if (_disposed)
        {
            state = build();
            return true;
        }

        if (_pending.TryGetValue(key, out var pending))
        {
            if (!pending.Work.IsCompleted)
            {
                Interlocked.Increment(ref _deduped);
                return false;
            }

            _pending.TryRemove(key, out _);
            Interlocked.Increment(ref _finished);
            Interlocked.Add(ref _compileTicks, pending.CompileTicks);
            // A failed compile is reported by the caller, which knows the shader.
            state = pending.Work.GetAwaiter().GetResult();
            return true;
        }

        var started = new Pending { StartedTicks = Stopwatch.GetTimestamp() };
        if (!_pending.TryAdd(key, started))
        {
            return false;
        }

        Interlocked.Increment(ref _started);
        started.Work = Task.Run(() =>
        {
            _slots.Wait();
            try
            {
                var compileStarted = Stopwatch.GetTimestamp();
                // A pool thread has no autorelease pool, and building a pipeline autoreleases
                // strings and descriptors: the build frees its own. The state it returns is
                // owned (new*), not autoreleased, so it outlives the pool.
                nint result;
                using (MetalAutoreleasePool.Enter())
                {
                    result = build();
                }

                started.CompileTicks = Stopwatch.GetTimestamp() - compileStarted;
                return result;
            }
            finally
            {
                _slots.Release();
                // The command stream may be parked on this pipeline; wake it.
                try
                {
                    _onCompleted();
                }
                catch
                {
                    // Waking is best effort: the blocked-retry fallback still runs.
                }
            }
        });

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var pending in _pending.Values)
        {
            try
            {
                pending.Work.GetAwaiter().GetResult();
            }
            catch
            {
                // A compile that failed during shutdown has nothing left to tell us.
            }
        }

        _pending.Clear();
        _slots.Dispose();
    }
}
