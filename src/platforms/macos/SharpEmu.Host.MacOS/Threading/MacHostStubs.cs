// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SharpEmu.Host.Posix;
using static SharpEmu.Host.Posix.PosixStubEmitter;

namespace SharpEmu.Host.MacOS;

/// <summary>
/// macOS replacements for the kernel32 helpers the native backend embeds in
/// emitted x86-64 code. Every stub exposed here follows the Win64 calling
/// convention the emitted call sites were written for (first argument in
/// ECX, result in RAX, Win64 non-volatile registers preserved), so the
/// emission code stays identical across platforms.
/// </summary>
/// <remarks>
/// Darwin-specific and deliberately not shared with the Linux package: pthread
/// keys are <c>nuint</c>-wide here and 32-bit on Linux, unnamed
/// <c>sem_init</c> is unsupported so worker events are dispatch semaphores,
/// and x86-64 pthread TLS reads collapse to a single gs-relative load.
/// </remarks>
internal static unsafe class MacHostStubs
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static nint _tlsGetValueStub;
    private static nint _queryPerformanceCounterStub;
    private static nint _switchToThreadStub;
    private static nint _sleepStub;
    private static nint _waitForSingleObjectStub;
    private static nint _setEventStub;
    private static nint _exitThreadStub;

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

    /// <summary>
    /// Win64-convention replacements for the kernel32 event/thread helpers the
    /// native guest worker loop embeds. The "handle" they take is a worker
    /// event created by <see cref="CreateWorkerEvent"/>: a dispatch semaphore.
    /// The wait stub always waits forever (the worker loop passes INFINITE)
    /// and retries EINTR.
    /// </summary>
    public static nint WaitForSingleObjectStubAddress
    {
        get { EnsureInitialized(); return _waitForSingleObjectStub; }
    }

    public static nint SetEventStubAddress
    {
        get { EnsureInitialized(); return _setEventStub; }
    }

    public static nint ExitThreadStubAddress
    {
        get { EnsureInitialized(); return _exitThreadStub; }
    }

    /// <summary>
    /// Creates a binary-semaphore worker event signalable/waitable both from
    /// managed code and from emitted native code (via the stub addresses
    /// above). Returns 0 on failure.
    /// </summary>
    public static nint CreateWorkerEvent() => dispatch_semaphore_create(0);

    public static bool SignalWorkerEvent(nint handle)
    {
        _ = dispatch_semaphore_signal(handle);
        return true;
    }

    /// <summary>Waits for a worker event; a negative timeout waits forever.</summary>
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

    /// <summary>
    /// Starts a raw pthread at a native entry point (pthread entries take their
    /// argument in RDI; the worker loop stub ignores it). Returns an opaque
    /// handle for <see cref="WaitForWorkerThreadExit"/>/<see cref="CloseWorkerThreadHandle"/>,
    /// or 0 on failure.
    /// </summary>
    public static nint CreateWorkerThread(nint entry, nint parameter, nuint stackReserveBytes, out uint threadId)
    {
        threadId = 0;
        byte* attr = stackalloc byte[512];
        if (pthread_attr_init(attr) != 0)
        {
            return 0;
        }

        try
        {
            if (stackReserveBytes != 0)
            {
                _ = pthread_attr_setstacksize(attr, nuint.Max(stackReserveBytes, 512 * 1024));
            }

            nint thread;
            if (pthread_create(&thread, attr, entry, parameter) != 0)
            {
                return 0;
            }

            ulong numericId;
            if (pthread_threadid_np(thread, &numericId) == 0)
            {
                threadId = unchecked((uint)numericId);
            }

            var holder = (nint*)Marshal.AllocHGlobal(sizeof(nint) * 2);
            holder[0] = thread;
            holder[1] = 0; // joined flag
            return (nint)holder;
        }
        finally
        {
            _ = pthread_attr_destroy(attr);
        }
    }

    /// <summary>
    /// Waits for a worker thread to exit. Liveness is probed with
    /// pthread_kill(thread, 0) (ESRCH once the thread has terminated) because
    /// Darwin offers no timed join; the exited thread is then joined so its
    /// resources are reclaimed.
    /// </summary>
    public static bool WaitForWorkerThreadExit(nint threadHandle, uint timeoutMilliseconds)
    {
        var holder = (nint*)threadHandle;
        if (holder == null)
        {
            return false;
        }

        if (holder[1] != 0)
        {
            return true;
        }

        var thread = holder[0];
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (pthread_kill(thread, 0) == 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            System.Threading.Thread.Sleep(1);
        }

        _ = pthread_join(thread, null);
        holder[1] = 1;
        return true;
    }

    public static void CloseWorkerThreadHandle(nint threadHandle)
    {
        var holder = (nint*)threadHandle;
        if (holder == null)
        {
            return;
        }

        if (holder[1] == 0)
        {
            // Never observed exiting: detach so the thread does not leak a
            // zombie join target when it eventually terminates.
            _ = pthread_detach(holder[0]);
        }

        Marshal.FreeHGlobal(threadHandle);
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

    /// <inheritdoc cref="PosixStubEmitter.CreateWin64ToSysVThunk"/>
    public static nint CreateWin64ToSysVThunk(nint sysvTarget) =>
        PosixStubEmitter.CreateWin64ToSysVThunk(HostPlatform.Current.Memory, sysvTarget);

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
        var memory = HostPlatform.Current.Memory;
        var page = (byte*)memory.Allocate(
            0,
            4096,
            HostPageProtection.ReadWriteExecute);
        if (page == null)
        {
            throw new OutOfMemoryException("Failed to allocate macOS host helper stub page");
        }

        var offset = 0;
        _tlsGetValueStub = EmitTlsGetValue(page, ref offset);
        _queryPerformanceCounterStub = EmitQueryPerformanceCounter(page, ref offset);
        _switchToThreadStub = EmitSwitchToThread(page, ref offset);
        _sleepStub = EmitSleep(page, ref offset);
        _waitForSingleObjectStub = EmitWaitForSingleObject(page, ref offset);
        _setEventStub = EmitSetEvent(page, ref offset);
        _exitThreadStub = EmitExitThread(page, ref offset);

        if (!memory.Protect((ulong)page, 4096, HostPageProtection.ReadExecute, out _))
        {
            throw new InvalidOperationException("Failed to protect macOS host helper stub page");
        }

        memory.FlushInstructionCache((ulong)page, (ulong)offset);
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

    private static nint EmitWaitForSingleObject(byte* page, ref int offset)
    {
        // DWORD WaitForSingleObject(worker event in rcx, timeout in edx): the
        // worker loop only ever waits forever, so the timeout is ignored.
        // The wait is on a dispatch semaphore, which needs
        // DISPATCH_TIME_FOREVER in rsi, and retries until it succeeds (EINTR).
        var wait = ResolveLibcExport("dispatch_semaphore_wait");
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x56);                                           // push rsi
        Emit(page, ref offset, 0x57);                                           // push rdi
        Emit(page, ref offset, 0x53);                                           // push rbx
        Emit(page, ref offset, 0x48, 0x89, 0xCB);                               // mov rbx, rcx
        var retry = offset;
        Emit(page, ref offset, 0x48, 0x89, 0xDF);                               // mov rdi, rbx
        Emit(page, ref offset, 0x48, 0xC7, 0xC6, 0xFF, 0xFF, 0xFF, 0xFF);       // mov rsi, DISPATCH_TIME_FOREVER
        EmitMovRaxImm64(page, ref offset, wait);                                // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                     // call rax
        Emit(page, ref offset, 0x85, 0xC0);                                     // test eax, eax
        Emit(page, ref offset, 0x75, unchecked((byte)(retry - (offset + 2))));  // jnz retry
        Emit(page, ref offset, 0x31, 0xC0);                                     // xor eax, eax (WAIT_OBJECT_0)
        Emit(page, ref offset, 0x5B);                                           // pop rbx
        Emit(page, ref offset, 0x5F);                                           // pop rdi
        Emit(page, ref offset, 0x5E);                                           // pop rsi
        Emit(page, ref offset, 0xC3);                                           // ret
        return start;
    }

    private static nint EmitSetEvent(byte* page, ref int offset)
    {
        // BOOL SetEvent(worker event in rcx) -> dispatch_semaphore_signal.
        var signal = ResolveLibcExport("dispatch_semaphore_signal");
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x56);                                           // push rsi
        Emit(page, ref offset, 0x57);                                           // push rdi
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08);                         // sub rsp, 8
        Emit(page, ref offset, 0x48, 0x89, 0xCF);                               // mov rdi, rcx
        EmitMovRaxImm64(page, ref offset, signal);                              // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                     // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08);                         // add rsp, 8
        Emit(page, ref offset, 0x5F);                                           // pop rdi
        Emit(page, ref offset, 0x5E);                                           // pop rsi
        Emit(page, ref offset, 0xB8, 0x01, 0x00, 0x00, 0x00);                   // mov eax, 1
        Emit(page, ref offset, 0xC3);                                           // ret
        return start;
    }

    private static nint EmitExitThread(byte* page, ref int offset)
    {
        // void ExitThread(code in ecx) -> pthread_exit(NULL); never returns,
        // so no registers need preserving. pthread_exit runs the thread's TSD
        // destructors, which detaches the CLR if the thread lazily attached.
        var pthreadExit = ResolveLibcExport("pthread_exit");
        var start = (nint)(page + offset);
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08);                         // sub rsp, 8
        Emit(page, ref offset, 0x31, 0xFF);                                     // xor edi, edi
        EmitMovRaxImm64(page, ref offset, pthreadExit);                         // mov rax, imm64
        Emit(page, ref offset, 0xFF, 0xD0);                                     // call rax
        Emit(page, ref offset, 0xCC);                                           // int3 (never returns)
        return start;
    }

    private static nint ResolveLibcExport(string name)
    {
        var libc = NativeLibrary.Load("libSystem.dylib");
        return NativeLibrary.GetExport(libc, name);
    }

    // "libc" resolves to libSystem on Darwin, which also reexports libdispatch,
    // so the pthread and dispatch imports below share one library name.
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
    private static extern int pthread_attr_init(byte* attr);

    [DllImport("libc")]
    private static extern int pthread_attr_destroy(byte* attr);

    [DllImport("libc")]
    private static extern int pthread_attr_setstacksize(byte* attr, nuint stackSize);

    [DllImport("libc")]
    private static extern int pthread_create(nint* thread, byte* attr, nint startRoutine, nint arg);

    [DllImport("libc")]
    private static extern int pthread_join(nint thread, nint* returnValue);

    [DllImport("libc")]
    private static extern int pthread_detach(nint thread);

    [DllImport("libc")]
    private static extern int pthread_kill(nint thread, int signal);

    // Dispatch semaphores back the worker events: unnamed sem_init is
    // unsupported on Darwin.
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
