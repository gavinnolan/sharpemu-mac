// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Linked into SharpEmu.Tools.GpuProcessSmoke as well, which runs it under Rosetta.
#if GPU_PROCESS_SMOKE
namespace SharpEmu.Tools.GpuProcessSmoke;
#else
namespace SharpEmu.GpuRemote.Tests;
#endif

using SharpEmu.GpuRemote;

/// <summary>
/// Makes synchronous calls from inside a real POSIX signal handler, the way the emulator's
/// write-fault handler will: a <c>sigaction</c> handler (an UnmanagedCallersOnly method, as the
/// fault bridge's trampoline calls) raised on the calling thread with <c>raise</c>, so the
/// handler interrupts a thread that is outside managed code - like a guest thread at a fault.
/// </summary>
internal static unsafe class SignalContextProbe
{
    private const int SIGUSR2 = 31;
    private const long CallTimeoutNanoseconds = 5_000_000_000;

    private static SyncCallTable? _table;
    private static byte* _request;
    private static byte* _response;
    private static long _spinNanoseconds;
    private static int _status;
    private static int _handled;

    /// <summary>
    /// Runs <paramref name="iterations"/> calls (opcode 1, an echo) from signal context and
    /// returns how many completed and the round trips, signal delivery included, in nanoseconds.
    /// </summary>
    public static (int Completed, double[] RoundTrips) Run(SyncCallTable table, int iterations, long spinNanoseconds)
    {
        SyncCallTable.PrepareForSignalContext();
        RuntimeHelpers.PrepareMethod(typeof(SignalContextProbe).GetMethod(nameof(OnSignal), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle);
        _table = table;
        _spinNanoseconds = spinNanoseconds;
        _request = (byte*)NativeMemory.AllocZeroed(16);
        _response = (byte*)NativeMemory.AllocZeroed(16);
        var roundTrips = new double[iterations];
        var completed = 0;

        SigAction action = default;
        action.Handler = (nint)(delegate* unmanaged<int, void>)&OnSignal;
        SigAction previous;
        if (sigaction(SIGUSR2, &action, &previous) != 0)
        {
            throw new InvalidOperationException($"sigaction failed: errno {Marshal.GetLastPInvokeError()}");
        }

        try
        {
            for (var i = 0; i < iterations; i++)
            {
                *(long*)_request = i;
                _status = -1;
                _handled = 0;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = raise(SIGUSR2);
                roundTrips[i] = System.Diagnostics.Stopwatch.GetElapsedTime(start).Ticks * 100.0;
                if (_handled == 1 && _status == (int)SyncCallStatus.Completed && *(long*)_response == i)
                {
                    completed++;
                }
            }
        }
        finally
        {
            _ = sigaction(SIGUSR2, &previous, null);
            NativeMemory.Free(_request);
            NativeMemory.Free(_response);
            _table = null;
        }

        return (completed, roundTrips);
    }

    // Signal context: shared-memory atomics and the call's own system calls only.
    [UnmanagedCallersOnly]
    private static void OnSignal(int signal)
    {
        _status = (int)_table!.Call(1, _request, 16, _response, 16, out _, out _, CallTimeoutNanoseconds, _spinNanoseconds);
        _handled = 1;
    }

    // Darwin's user-side struct sigaction: handler, sa_mask (sigset_t, 32 bits), sa_flags.
    [StructLayout(LayoutKind.Sequential)]
    private struct SigAction
    {
        public nint Handler;
        public uint Mask;
        public int Flags;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sigaction(int signal, SigAction* action, SigAction* previous);

    [DllImport("libc")]
    private static extern int raise(int signal);
}
