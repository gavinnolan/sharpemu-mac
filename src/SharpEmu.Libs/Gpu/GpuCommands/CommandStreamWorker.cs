// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The dedicated thread form of the submission loop for hosts without their own render loop.
public sealed class CommandStreamWorker
{
    private readonly CommandStreamQueue _queue;
    private readonly ICommandStreamHost _host;
    private readonly Func<bool> _hasPendingCommands;
    private readonly bool _cancelBlockedAtStop;
    private readonly string _threadName;
    private Thread? _thread;
    private SharpEmu.HLE.Host.HostQosThread? _qosThread;
    private readonly Action<Exception>? _onTerminalFailure;
    private Exception? _failure;

    public CommandStreamWorker(CommandStreamQueue queue, ICommandStreamHost host, Func<bool> hasPendingCommands, bool cancelBlockedAtStop, string threadName, Action<Exception>? onTerminalFailure = null)
    {
        _queue = queue;
        _host = host;
        _hasPendingCommands = hasPendingCommands;
        _cancelBlockedAtStop = cancelBlockedAtStop;
        _onTerminalFailure = onTerminalFailure;
        _threadName = threadName;
    }

    /// <summary>The host QoS class the worker thread runs at (Darwin qos_class_t; 0x21 is user-interactive), read back from the thread itself; 0 until it starts or off macOS.</summary>
    public uint WorkerQosClass => Volatile.Read(ref _workerQosClass);

    private uint _workerQosClass;

    // Recorded by Run itself so the answer is right from the worker's first instruction,
    // before Start has stored the native thread handle.
    private volatile Thread? _runThread;

    public bool IsWorkerThread => ReferenceEquals(Thread.CurrentThread, _runThread);

    public void Start()
    {
        // The GPU submission thread: every guest draw and every flip passes through here on its way
        // to the backend, so it sits on the frame deadline just as much as the presenter does.
        // pthread_set_qos_class_self_np fails with EPERM on a CoreCLR-created thread, so the class
        // goes into the pthread attributes of a thread created natively; the managed thread below
        // is the fallback for hosts without that (every non-macOS host).
        _qosThread = SharpEmu.HLE.Host.HostQosThread.TryStart(
            _threadName, SharpEmu.HLE.Host.HostThreadQos.UserInteractive, 16 * 1024 * 1024, Run, out _);
        if (_qosThread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = _threadName };
            _thread.Start();
        }
    }

    // Stops accepting work, drains the queue and joins; a worker failure is rethrown here.
    public IdleOutcome Stop()
    {
        _queue.StopAccepting();
        _queue.Wake();
        if (!IsWorkerThread)
        {
            if (_qosThread is { } qosThread)
            {
                qosThread.Join(Timeout.Infinite);
            }
            else if (_thread is { IsAlive: true } thread)
            {
                thread.Join();
            }
        }

        if (_failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        }

        return _queue.Outcome;
    }

    private void Run()
    {
        _runThread = Thread.CurrentThread;
        Volatile.Write(ref _workerQosClass, SharpEmu.HLE.Host.HostThreadQualityOfService.GetCurrentThreadClassForDiagnostics());
        try
        {
            for (;;)
            {
                _host.RunPendingCommands();
                if (_queue.IsStopping)
                {
                    _queue.DrainForShutdown(_cancelBlockedAtStop);
                    _host.RunPendingCommands();
                    _host.SynchronizeGpu();
                    return;
                }

                switch (_queue.ProcessOne())
                {
                    case SliceResult.NoWork:
                        if (!_hasPendingCommands())
                        {
                            _ = _queue.WaitForWork(AllBlockedWait);
                        }

                        break;
                    case SliceResult.AllBlocked:
                        if (!_queue.WaitForRetryInterval(CommandStreamQueue.AllBlockedRetryMilliseconds))
                        {
                            _queue.RetryBlocked();
                        }

                        break;
                }
            }
        }
        catch (Exception exception)
        {
            _failure = exception;
            try { _queue.Fail(exception); }
            catch (Exception cleanup) { AttachFailureCleanup(exception, cleanup); }
            // Queue failure owns its own gate. Host notification runs only after
            // that attempt returns, including when cancellation cleanup throws.
            try { _onTerminalFailure?.Invoke(exception); }
            catch (Exception cleanup) { AttachFailureCleanup(exception, cleanup); }
        }
    }

    private static void AttachFailureCleanup(Exception original, Exception cleanup)
    {
        try
        {
            if (original.Data["CommandStreamWorkerCleanup"] is Exception previous)
                original.Data["CommandStreamWorkerCleanup"] = new AggregateException(previous, cleanup);
            else original.Data["CommandStreamWorkerCleanup"] = cleanup;
        }
        catch { /* Optional diagnostics cannot replace the worker's first cause. */ }
    }

    private const int AllBlockedWait = 1000;
}
