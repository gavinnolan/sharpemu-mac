// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Cpu.DirectExecution.MacOS.X64;

/// <summary>
/// Darwin replacements for the kernel32 helpers the direct-execution backend
/// embeds in emitted x86-64 code, plus the Darwin pthread TLS keys and worker
/// events it uses from managed code.
/// </summary>
/// <remarks>
/// Every stub exposed here follows the Win64 calling convention the emitted
/// call sites were written for (first argument in ECX, result in RAX, Win64
/// non-volatile registers preserved), so the emission code in SharpEmu.Core
/// stays identical across platforms.
///
/// <para>Moved verbatim out of <c>SharpEmu.Core/Cpu/Native/PosixHostStubs.cs</c>
/// by W10, keeping only the Darwin arm of each branch: pthread keys are
/// <c>nuint</c>-wide here and 32-bit on Linux, unnamed <c>sem_init</c> is
/// unsupported so worker events are dispatch semaphores, and x86-64 pthread TLS
/// reads collapse to a single gs-relative load.</para>
///
/// <para>This overlaps <c>SharpEmu.Host.MacOS</c>'s <c>MacHostStubs</c>, which
/// emits the same four stub bodies byte for byte (and three more for the host
/// contract's worker loop) behind <c>IHostSymbolResolver</c>. They are kept
/// separate deliberately: W10 was a mechanical move of the execution backend's
/// own code, and collapsing the two would change which implementation the
/// emitted guest stubs call. Retiring this copy in favour of
/// <c>HostPlatform.Current.Symbols</c> is tracked as W10 follow-up work and
/// needs its own launch/stop evidence, not a drive-by edit to a page the guest
/// executes.</para>
/// </remarks>
internal static unsafe class MacOSExecutionStubs
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static nint _tlsGetValueStub;
    private static nint _queryPerformanceCounterStub;
    private static nint _switchToThreadStub;
    private static nint _sleepStub;

    public static nint TlsGetValueStubAddress
    {
        get { EnsureInitialized(); return _tlsGetValueStub; }
    }

    public static nint QueryPerformanceCounterStubAddress
    {
        get { EnsureInitialized(); return _queryPerformanceCounterStub; }
    }

    public static nint SwitchToThreadStubAddress
    {
        get { EnsureInitialized(); return _switchToThreadStub; }
    }

    public static nint SleepStubAddress
    {
        get { EnsureInitialized(); return _sleepStub; }
    }

    public static nint CreateWorkerEvent() => dispatch_semaphore_create(0);

    public static bool SignalWorkerEvent(nint handle)
    {
        _ = dispatch_semaphore_signal(handle);
        return true;
    }

    public static bool WaitWorkerEvent(nint handle, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0)
        {
            return dispatch_semaphore_wait(handle, ulong.MaxValue) == 0;
        }

        var deadline = dispatch_time(0, timeoutMilliseconds * 1_000_000L);
        return dispatch_semaphore_wait(handle, deadline) == 0;
    }

    public static void DestroyWorkerEvent(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        dispatch_release(handle);
    }

    /// <summary>Allocates a pthread TLS key, mirroring kernel32!TlsAlloc.</summary>
    public static uint TlsAlloc()
    {
        nuint key;
        return pthread_key_create(&key, 0) == 0 ? (uint)key : uint.MaxValue;
    }

    public static bool TlsFree(uint key) => pthread_key_delete((nuint)key) == 0;

    public static bool TlsSetValue(uint key, nint value) => pthread_setspecific((nuint)key, value) == 0;

    public static nint TlsGetValue(uint key) => pthread_getspecific((nuint)key);

    /// <summary>Stable numeric id of the calling thread (kernel32!GetCurrentThreadId).</summary>
    public static uint GetCurrentThreadId()
    {
        ulong tid;
        return pthread_threadid_np(0, &tid) == 0 ? unchecked((uint)tid) : 0u;
    }

    /// <summary>
    /// Wraps a managed callback (compiled for the SysV ABI on POSIX .NET) in a
    /// thunk that accepts up to four integer arguments in the Win64 ABI the
    /// emitted x86-64 call sites use. Win64 passes args in rcx/rdx/r8/r9 and
    /// treats rdi/rsi as non-volatile; SysV expects rdi/rsi/rdx/rcx and
    /// clobbers them, so the thunk saves rdi/rsi, shuffles the registers, keeps
    /// the stack 16-byte aligned for the call, and forwards the rax result.
    /// </summary>
    public static nint CreateWin64ToSysVThunk(nint sysvTarget)
    {
        var page = (byte*)HostMemory.Alloc(
            null,
            4096,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
            HostMemory.PAGE_EXECUTE_READWRITE);
        if (page == null)
        {
            throw new OutOfMemoryException("Failed to allocate Win64->SysV thunk page");
        }

        var offset = 0;
        Emit(page, ref offset, 0x57);                   // push rdi
        Emit(page, ref offset, 0x56);                   // push rsi
        Emit(page, ref offset, 0x48, 0x89, 0xCF);       // mov rdi, rcx
        Emit(page, ref offset, 0x48, 0x89, 0xD6);       // mov rsi, rdx
        Emit(page, ref offset, 0x4C, 0x89, 0xC2);       // mov rdx, r8
        Emit(page, ref offset, 0x4C, 0x89, 0xC9);       // mov rcx, r9
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08); // sub rsp, 8 (realign to 16)
        EmitMovRaxImm64(page, ref offset, sysvTarget);  // mov rax, target
        Emit(page, ref offset, 0xFF, 0xD0);             // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08); // add rsp, 8
        Emit(page, ref offset, 0x5E);                   // pop rsi
        Emit(page, ref offset, 0x5F);                   // pop rdi
        Emit(page, ref offset, 0xC3);                   // ret

        if (!HostMemory.Protect(page, 4096, HostMemory.PAGE_EXECUTE_READ, out _))
        {
            throw new InvalidOperationException("Failed to protect Win64->SysV thunk page");
        }

        HostMemory.FlushInstructionCache(page, (nuint)offset);
        return (nint)page;
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            BuildStubs();
            _initialized = true;
        }
    }

    private static void BuildStubs()
    {
        var page = (byte*)HostMemory.Alloc(
            null,
            4096,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
            HostMemory.PAGE_EXECUTE_READWRITE);
        if (page == null)
        {
            throw new OutOfMemoryException("Failed to allocate macOS execution helper stub page");
        }

        var offset = 0;
        _tlsGetValueStub = EmitTlsGetValue(page, ref offset);
        _queryPerformanceCounterStub = EmitQueryPerformanceCounter(page, ref offset);
        _switchToThreadStub = EmitSwitchToThread(page, ref offset);
        _sleepStub = EmitSleep(page, ref offset);

        if (!HostMemory.Protect(page, 4096, HostMemory.PAGE_EXECUTE_READ, out _))
        {
            throw new InvalidOperationException("Failed to protect macOS execution helper stub page");
        }

        HostMemory.FlushInstructionCache(page, (nuint)offset);
    }

    private static nint EmitTlsGetValue(byte* page, ref int offset)
    {
        // On macOS x86-64 pthread keys index the gs-based thread specific
        // data array directly, so TlsGetValue(index in ecx) collapses to a
        // single load that clobbers nothing but RAX.
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x89, 0xC8);                                     // mov eax, ecx
        Emit(page, ref offset, 0x65, 0x48, 0x8B, 0x04, 0xC5, 0, 0, 0, 0);       // mov rax, gs:[rax*8]
        Emit(page, ref offset, 0xC3);                                           // ret
        return start;
    }

    private static nint EmitQueryPerformanceCounter(byte* page, ref int offset)
    {
        // BOOL QueryPerformanceCounter(LARGE_INTEGER* out in rcx): the emitted
        // consumers only need a monotonically increasing counter, which rdtsc
        // provides without leaving Win64-safe registers.
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x0F, 0x31);                                         // rdtsc
        Emit(page, ref offset, 0x48, 0xC1, 0xE2, 0x20);                             // shl rdx, 32
        Emit(page, ref offset, 0x48, 0x09, 0xD0);                                   // or rax, rdx
        Emit(page, ref offset, 0x48, 0x89, 0x01);                                   // mov [rcx], rax
        Emit(page, ref offset, 0xB8, 0x01, 0x00, 0x00, 0x00);                       // mov eax, 1
        Emit(page, ref offset, 0xC3);                                               // ret
        return start;
    }

    private static nint EmitSwitchToThread(byte* page, ref int offset)
    {
        var schedYield = ResolveLibcExport("sched_yield");
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x56);                                               // push rsi
        Emit(page, ref offset, 0x57);                                               // push rdi
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08);                             // sub rsp, 8
        EmitMovRaxImm64(page, ref offset, schedYield);                              // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                         // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08);                             // add rsp, 8
        Emit(page, ref offset, 0x5F);                                               // pop rdi
        Emit(page, ref offset, 0x5E);                                               // pop rsi
        Emit(page, ref offset, 0xB8, 0x01, 0x00, 0x00, 0x00);                       // mov eax, 1
        Emit(page, ref offset, 0xC3);                                               // ret
        return start;
    }

    private static nint EmitSleep(byte* page, ref int offset)
    {
        // void Sleep(DWORD milliseconds in ecx) -> usleep(microseconds in edi).
        var usleep = ResolveLibcExport("usleep");
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x56);                                               // push rsi
        Emit(page, ref offset, 0x57);                                               // push rdi
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08);                             // sub rsp, 8
        Emit(page, ref offset, 0x89, 0xCF);                                         // mov edi, ecx
        Emit(page, ref offset, 0x81, 0xFF, 0xFF, 0x0F, 0x00, 0x00);                 // cmp edi, 0xFFF
        Emit(page, ref offset, 0x76, 0x05);                                         // jbe +5
        Emit(page, ref offset, 0xBF, 0xFF, 0x0F, 0x00, 0x00);                       // mov edi, 0xFFF (cap at ~4s)
        Emit(page, ref offset, 0x69, 0xFF, 0xE8, 0x03, 0x00, 0x00);                 // imul edi, edi, 1000
        EmitMovRaxImm64(page, ref offset, usleep);                                  // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                         // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08);                             // add rsp, 8
        Emit(page, ref offset, 0x5F);                                               // pop rdi
        Emit(page, ref offset, 0x5E);                                               // pop rsi
        Emit(page, ref offset, 0xC3);                                               // ret
        return start;
    }

    private static nint ResolveLibcExport(string name) =>
        NativeLibrary.GetExport(NativeLibrary.Load("libSystem.dylib"), name);

    private static void Emit(byte* page, ref int offset, params byte[] bytes)
    {
        foreach (var value in bytes)
        {
            page[offset++] = value;
        }
    }

    private static void EmitMovRaxImm64(byte* page, ref int offset, nint value)
    {
        Emit(page, ref offset, 0x48, 0xB8);
        *(long*)(page + offset) = value;
        offset += sizeof(long);
    }

    [DllImport("libc", EntryPoint = "pthread_key_create", SetLastError = true)]
    private static extern int pthread_key_create(nuint* key, nint destructor);

    [DllImport("libc", EntryPoint = "pthread_key_delete")]
    private static extern int pthread_key_delete(nuint key);

    [DllImport("libc", EntryPoint = "pthread_setspecific")]
    private static extern int pthread_setspecific(nuint key, nint value);

    [DllImport("libc", EntryPoint = "pthread_getspecific")]
    private static extern nint pthread_getspecific(nuint key);

    [DllImport("libc")]
    private static extern int pthread_threadid_np(nint thread, ulong* threadId);

    [DllImport("libc")]
    private static extern int getloadavg(double* loadAverage, int count);

    /// <summary>The one-minute load average, or -1 when it cannot be read.</summary>
    internal static double ReadLoadAverage()
    {
        double load;
        return getloadavg(&load, 1) == 1 ? load : -1.0;
    }

    [DllImport("libc")]
    private static extern nint dispatch_semaphore_create(long value);

    [DllImport("libc")]
    private static extern nint dispatch_semaphore_signal(nint semaphore);

    [DllImport("libc")]
    private static extern nint dispatch_semaphore_wait(nint semaphore, ulong timeout);

    [DllImport("libc")]
    private static extern ulong dispatch_time(ulong when, long deltaNanoseconds);

    [DllImport("libc")]
    private static extern void dispatch_release(nint handle);
}
