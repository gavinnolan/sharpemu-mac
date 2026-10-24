// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GpuSplit;

/// <summary>
/// The GPU worker of the GPU process, parked while the guest process changes something the
/// worker must not see half done: a guest mapping change, or an ordered video-out change behind
/// the accepted command streams (<see cref="GpuRemote.GpuSplitBracket"/>).
/// </summary>
/// <remarks>
/// In one process the change runs on the worker itself. Across the split it runs in the guest
/// process, so the worker runs <see cref="Park"/> instead - handed to it the way the change would
/// have been (<c>GuestGpuMemory.RunMappingChange</c>, <c>RunAfterPendingCommandStreams</c>) by a
/// thread of its own, which blocks in that call - and stays there until the change is over. What
/// the change needs from the worker meanwhile (an image unregistration) runs inside the park, in
/// order (<see cref="TryRun"/>); the end runs its last action (the replica sync) there too.
/// Every wait is bounded and gives up once the guest process is gone.
/// </remarks>
internal sealed class WorkerBracket
{
    private readonly object _gate = new();
    private readonly Queue<Action> _work = new();
    private readonly ManualResetEventSlim _parked = new(false);
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly Func<bool> _alive;

    // A park nobody ends within this (the guest process gave up on its call) lets the worker go.
    private static readonly long ParkLimitMilliseconds = (long)TimeSpan.FromMinutes(11).TotalMilliseconds;
    private Action? _atEnd;
    private bool _ending;

    public WorkerBracket(ulong id, Func<bool> alive)
    {
        Id = id;
        _alive = alive;
    }

    public ulong Id { get; }

    /// <summary>
    /// Hands <see cref="Park"/> to the worker: <paramref name="runParked"/> gets it and must run it
    /// there, blocking until it returns.
    /// </summary>
    public void Start(Action<Action> runParked)
    {
        new Thread(() =>
        {
            try
            {
                runParked(Park);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[GPU-SPLIT][ERROR] GPU worker bracket {Id} failed: {exception}");
            }
            finally
            {
                lock (_gate)
                {
                    _ending = true;
                    Monitor.PulseAll(_gate);
                }

                _parked.Set();
                _finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = $"SharpEmu GPU split bracket {Id}",
        }.Start();
    }

    /// <summary>Waits until the worker is parked (or the bracket ended without it).</summary>
    public bool WaitParked(TimeSpan timeout) => WaitWhileAlive(_parked, timeout);

    /// <summary>
    /// Runs <paramref name="work"/> on the parked worker and waits for it. False, with nothing
    /// handed over, when the bracket is already over (the caller runs the work some other way);
    /// true once it is handed over, even when the wait gives up (reported: the work still runs
    /// there, and must not run twice).
    /// </summary>
    public bool TryRun(Action work, TimeSpan timeout)
    {
        // Not disposed: work that outlives a given-up wait still sets it.
        var done = new ManualResetEventSlim(false);
        lock (_gate)
        {
            if (_ending)
            {
                return false;
            }

            _work.Enqueue(() =>
            {
                try
                {
                    work();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[GPU-SPLIT][ERROR] Work in GPU worker bracket {Id} failed: {exception}");
                }
                finally
                {
                    done.Set();
                }
            });
            Monitor.PulseAll(_gate);
        }

        if (!WaitWhileAlive(done, timeout))
        {
            Console.Error.WriteLine($"[GPU-SPLIT][WARN] Work in GPU worker bracket {Id} has not finished in {timeout.TotalSeconds:F0} s.");
        }

        return true;
    }

    /// <summary>Ends the bracket: the worker runs <paramref name="atEnd"/> and goes back to its work.</summary>
    public bool End(Action atEnd, TimeSpan timeout)
    {
        lock (_gate)
        {
            _atEnd = atEnd;
            _ending = true;
            Monitor.PulseAll(_gate);
        }

        return WaitWhileAlive(_finished, timeout);
    }

    // On the GPU worker.
    private void Park()
    {
        _parked.Set();
        var parkedAt = Environment.TickCount64;
        while (true)
        {
            Action? next = null;
            Action? atEnd = null;
            var leave = false;
            lock (_gate)
            {
                while (_work.Count == 0 && !_ending)
                {
                    if (!_alive())
                    {
                        _ending = true;
                        break;
                    }

                    if (Environment.TickCount64 - parkedAt > ParkLimitMilliseconds)
                    {
                        Console.Error.WriteLine($"[GPU-SPLIT][ERROR] GPU worker bracket {Id} was never ended; the worker goes on.");
                        _ending = true;
                        break;
                    }

                    Monitor.Wait(_gate, 50);
                }

                if (_work.Count != 0)
                {
                    next = _work.Dequeue();
                }
                else
                {
                    atEnd = _atEnd;
                    leave = true;
                }
            }

            if (next is not null)
            {
                next();
                continue;
            }

            try
            {
                atEnd?.Invoke();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[GPU-SPLIT][ERROR] The end of GPU worker bracket {Id} failed: {exception}");
            }

            if (leave)
            {
                return;
            }
        }
    }

    private bool WaitWhileAlive(ManualResetEventSlim signal, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!signal.Wait(50))
        {
            if (!_alive() || Environment.TickCount64 >= deadline)
            {
                return signal.IsSet;
            }
        }

        return true;
    }
}
