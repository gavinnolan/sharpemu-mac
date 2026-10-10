// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Runs work other threads post to the GPU worker before the next submission; the
/// twin of the Vulkan <c>GpuWorkerRelay</c>. The relay never waits for the GPU — it
/// only orders managed work against the recording thread.
/// </summary>
internal sealed class MetalGpuWorkerRelay : IGpuQueueRelay
{
    [ThreadStatic]
    private static MetalGpuWorkerRelay? _boundWorker;

    private readonly object _gate = new();
    private readonly Queue<Action> _commands = new();
    private readonly Action _wake;
    private readonly Func<Action, Action, bool>? _tryEnqueueAfterPendingWork;
    private int _pendingCount;
    private bool _accepting = true;

    public MetalGpuWorkerRelay(Action wake, Func<Action, Action, bool>? tryEnqueueAfterPendingWork = null)
    {
        _wake = wake;
        _tryEnqueueAfterPendingWork = tryEnqueueAfterPendingWork;
    }

    public bool IsGpuQueueThread => _boundWorker == this;

    public bool HasPendingCommands => Volatile.Read(ref _pendingCount) != 0;

    public void BindCurrentThread() => _boundWorker = this;

    public bool TryRunAfterPendingWork(Action work) => TryRunOnGpuQueue(work);

    // An ordered video-out change must land behind the command streams already accepted.
    public bool TryRunAfterAcceptedCommandStreams(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsGpuQueueThread || _tryEnqueueAfterPendingWork is null)
        {
            return TryRunOnGpuQueue(work);
        }

        using var done = new SemaphoreSlim(0);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        if (!_tryEnqueueAfterPendingWork(
                () =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception exception)
                    {
                        failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                    }
                    finally
                    {
                        done.Release();
                    }
                },
                () =>
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                        new OperationCanceledException(
                            "The command stream stopped before the ordered GPU worker action could run."));
                    done.Release();
                }))
        {
            return false;
        }

        done.Wait();
        failure?.Throw();
        return true;
    }

    public void Post(Action work)
    {
        if (!TryPost(work))
        {
            throw GpuFatal.Fatal("The Metal GPU worker relay is closed.");
        }
    }

    public bool TryPost(Action work)
    {
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            _commands.Enqueue(work);
            Interlocked.Increment(ref _pendingCount);
        }

        _wake();
        return true;
    }

    public void RunPendingCommands()
    {
        if (!IsGpuQueueThread)
        {
            throw GpuFatal.Fatal("Only the Metal GPU worker can run relay commands.");
        }

        while (HasPendingCommands)
        {
            Action command;
            lock (_gate)
            {
                command = _commands.Dequeue();
                Interlocked.Decrement(ref _pendingCount);
            }

            command();
        }
    }

    public void RunOnGpuQueue(Action work)
    {
        if (!TryRunOnGpuQueue(work))
        {
            throw GpuFatal.Fatal("The Metal GPU worker relay is closed.");
        }
    }

    public bool TryRunOnGpuQueue(Action work)
    {
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        using var done = new SemaphoreSlim(0);
        if (!TryPost(() =>
            {
                work();
                done.Release();
            }))
        {
            return false;
        }

        done.Wait();
        return true;
    }

    // Reject new work from other threads; the worker still runs what it accepted.
    public void StopAcceptingWork()
    {
        lock (_gate)
        {
            _accepting = false;
        }
    }
}

/// <summary>
/// Completion of submitted command buffers, through one MTLSharedEvent with a
/// monotonically increasing value per submission. A single thread waits with
/// <c>waitUntilSignaledValue:timeoutMS:</c> and runs the end-of-pipe work of each
/// submission in order — no Objective-C blocks and no per-buffer callback object.
/// </summary>
internal sealed class MetalCompletionQueue : IDisposable
{
    private const ulong WaitTimeoutMilliseconds = 50;

    private static readonly nint SelSignaledValue = MetalNative.Selector("signaledValue");
    private static readonly nint SelWaitUntilSignaledValue = MetalNative.Selector("waitUntilSignaledValue:timeoutMS:");

    private readonly nint _sharedEvent;
    private readonly object _gate = new();
    private readonly List<(ulong Serial, Action Work)> _pending = new();
    private readonly Thread _thread;
    private ulong _publishedSerial;
    private ulong _drainedSerial;
    private bool _stopping;

    public MetalCompletionQueue(nint sharedEvent)
    {
        _sharedEvent = sharedEvent;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu Metal completion",
        };
        _thread.Start();
    }

    public ulong SignaledValue => MetalV2Native.SendValue64(_sharedEvent, SelSignaledValue);

    /// <summary>Runs the work once the submission with this serial completes.</summary>
    public void Enqueue(ulong serial, Action work)
    {
        lock (_gate)
        {
            _pending.Add((serial, work));
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>A command buffer was committed; its completion work may now run.</summary>
    public void Publish(ulong serial)
    {
        lock (_gate)
        {
            if (serial > _publishedSerial)
            {
                _publishedSerial = serial;
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Blocks until the GPU finished the serial and its completions ran.</summary>
    public void WaitFor(ulong serial)
    {
        if (serial == 0)
        {
            return;
        }

        while (SignaledValue < serial)
        {
            if (!MetalV2Native.SendWaitUntilSignaledValue(
                    _sharedEvent, SelWaitUntilSignaledValue, serial, WaitTimeoutMilliseconds) &&
                SignaledValue < serial)
            {
                continue;
            }

            break;
        }

        lock (_gate)
        {
            while (_drainedSerial < serial && !_stopping && HasWorkAtOrBefore(serial))
            {
                Monitor.Wait(_gate, 2);
            }
        }
    }

    private bool HasWorkAtOrBefore(ulong serial)
    {
        foreach (var (pendingSerial, _) in _pending)
        {
            if (pendingSerial <= serial)
            {
                return true;
            }
        }

        return false;
    }

    private void Run()
    {
        while (true)
        {
            ulong target;
            lock (_gate)
            {
                while (!_stopping && (_pending.Count == 0 || MinimumPendingSerial() > _publishedSerial))
                {
                    Monitor.Wait(_gate, 20);
                }

                if (_stopping && _pending.Count == 0)
                {
                    return;
                }

                target = MinimumPendingSerial();
                if (target == ulong.MaxValue || target > _publishedSerial)
                {
                    continue;
                }
            }

            if (SignaledValue < target)
            {
                _ = MetalV2Native.SendWaitUntilSignaledValue(
                    _sharedEvent, SelWaitUntilSignaledValue, target, WaitTimeoutMilliseconds);
            }

            Drain(SignaledValue);
        }
    }

    private ulong MinimumPendingSerial()
    {
        var minimum = ulong.MaxValue;
        foreach (var (serial, _) in _pending)
        {
            if (serial < minimum)
            {
                minimum = serial;
            }
        }

        return minimum;
    }

    // Runs completed work in submission order; a failure never stops the queue.
    private void Drain(ulong signaled)
    {
        while (true)
        {
            Action? work = null;
            ulong serial = 0;
            lock (_gate)
            {
                var index = -1;
                var best = ulong.MaxValue;
                for (var candidate = 0; candidate < _pending.Count; candidate++)
                {
                    if (_pending[candidate].Serial <= signaled && _pending[candidate].Serial < best)
                    {
                        best = _pending[candidate].Serial;
                        index = candidate;
                    }
                }

                if (index >= 0)
                {
                    (serial, work) = _pending[index];
                    _pending.RemoveAt(index);
                }
                else
                {
                    if (signaled > _drainedSerial)
                    {
                        _drainedSerial = signaled;
                    }

                    Monitor.PulseAll(_gate);
                    return;
                }
            }

            try
            {
                work();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[METAL-V2][ERROR] An end-of-pipe completion failed: serial={serial} {exception}");
            }

            lock (_gate)
            {
                if (serial > _drainedSerial)
                {
                    _drainedSerial = serial;
                }

                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
