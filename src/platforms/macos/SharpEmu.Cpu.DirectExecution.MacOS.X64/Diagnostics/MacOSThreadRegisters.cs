// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Cpu.DirectExecution.MacOS.X64;

// A thread's registers through Mach for a stall snapshot: found by the id pthread_threadid_np
// gives (the host thread ids guest threads record), suspended, read with the
// x86_THREAD_STATE64 flavor (Rosetta answers it with the translated thread's registers) and
// resumed. Only the two Mach calls run while it is suspended, both bound before the
// suspension, so a thread holding a runtime lock cannot wedge the snapshot.
internal static unsafe class MacOSThreadRegisters
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int ThreadIdentifierInfo = 4;
    private const uint ThreadIdentifierInfoCount = 6;
    private const int X86ThreadState64 = 4;
    private const uint X86ThreadState64Count = 42;

    private static readonly Lazy<uint> TaskSelf = new(() =>
        NativeLibrary.TryGetExport(NativeLibrary.Load(LibSystem), "mach_task_self_", out var address)
            ? *(uint*)address
            : 0);

    // x86_thread_state64_t: rax rbx rcx rdx rdi rsi rbp rsp r8-r15 rip rflags cs fs gs;
    // the first 17 are the registers the platform interface returns, in that order.
    public static bool TryRead(uint threadId, Span<ulong> registers)
    {
        var task = TaskSelf.Value;
        if (threadId == 0 || task == 0 || registers.Length < 17 ||
            task_threads(task, out var threads, out var threadCount) != 0)
        {
            return false;
        }

        uint port = 0;
        try
        {
            var info = stackalloc ulong[3];
            for (var i = 0; i < threadCount; i++)
            {
                var candidate = ((uint*)threads)[i];
                var infoCount = ThreadIdentifierInfoCount;
                if (port == 0 &&
                    thread_info(candidate, ThreadIdentifierInfo, info, ref infoCount) == 0 &&
                    unchecked((uint)info[0]) == threadId)
                {
                    port = candidate;
                    continue;
                }

                _ = mach_port_deallocate(task, candidate);
            }
        }
        finally
        {
            _ = vm_deallocate(task, threads, (nuint)threadCount * sizeof(uint));
        }

        if (port == 0)
        {
            return false;
        }

        try
        {
            var state = stackalloc ulong[21];
            var stateCount = X86ThreadState64Count;
            // Binds both calls (and checks the flavor) before anything is suspended.
            if (thread_get_state(port, X86ThreadState64, state, ref stateCount) != 0)
            {
                return false;
            }

            _ = thread_resume(0);
            if (thread_suspend(port) != 0)
            {
                return false;
            }

            stateCount = X86ThreadState64Count;
            var result = thread_get_state(port, X86ThreadState64, state, ref stateCount);
            _ = thread_resume(port);
            if (result != 0)
            {
                return false;
            }

            new ReadOnlySpan<ulong>(state, 17).CopyTo(registers);
            return true;
        }
        finally
        {
            _ = mach_port_deallocate(task, port);
        }
    }

    [DllImport(LibSystem)]
    private static extern int task_threads(uint task, out nint threads, out int threadCount);

    [DllImport(LibSystem)]
    private static extern int thread_info(uint thread, int flavor, ulong* info, ref uint infoCount);

    [DllImport(LibSystem)]
    private static extern int thread_get_state(uint thread, int flavor, ulong* state, ref uint stateCount);

    [DllImport(LibSystem)]
    private static extern int thread_suspend(uint thread);

    [DllImport(LibSystem)]
    private static extern int thread_resume(uint thread);

    [DllImport(LibSystem)]
    private static extern int mach_port_deallocate(uint task, uint name);

    [DllImport(LibSystem)]
    private static extern int vm_deallocate(uint task, nint address, nuint size);
}
