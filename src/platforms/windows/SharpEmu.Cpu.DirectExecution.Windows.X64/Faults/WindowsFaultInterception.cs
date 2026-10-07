// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native.Platform;
using SharpEmu.HLE;

namespace SharpEmu.Cpu.DirectExecution.Windows.X64;

/// <summary>
/// Registers the process-wide vectored exception handlers and the last-resort
/// filter in front of the shared recovery chain.
/// </summary>
/// <remarks>
/// OS registration and thunk lifetime belong here; guest recovery stays in Core.
/// The original delegate-marshalling reverse-P/Invoke transition is preserved.
/// Delegates are rooted for the process lifetime and their callables are built
/// during installation, never from a fault frame.
/// </remarks>
internal static unsafe class WindowsFaultInterception
{
    private delegate int RecoveryDelegate(void* exceptionInfo);
    private static readonly RecoveryDelegate RawDelegate = RunRawRecovery;
    private static readonly RecoveryDelegate RecoveryDelegateInstance = RunRecovery;
    private static readonly RecoveryDelegate ReportDelegate = RunUnhandledReport;
    private static delegate* managed<void*, int> _rawRecovery;
    private static delegate* managed<void*, int> _recovery;
    private static delegate* managed<void*, int> _report;
    private static int RunRawRecovery(void* info) => _rawRecovery(info);
    private static int RunRecovery(void* info) => _recovery(info);
    private static int RunUnhandledReport(void* info) => _report(info);

    private static DirectExecutionFaultHandlers _installedParameters;
    private static nint _rawHandler;
    private static nint _rawHandlerThunk;
    private static nint _handler;
    private static nint _handlerThunk;
    private static nint _unhandledFilterThunk;

    internal static void Install(in DirectExecutionFaultHandlers handlers)
    {
        if (_handler != 0 &&
            (_installedParameters.HostRspSlotTlsIndex != handlers.HostRspSlotTlsIndex ||
             _installedParameters.TlsGetValueAddress != handlers.TlsGetValueAddress ||
             _installedParameters.WorkerAbortStub != handlers.WorkerAbortStub ||
             _installedParameters.WorkerAbortEligibleTlsIndex != handlers.WorkerAbortEligibleTlsIndex))
            throw new InvalidOperationException("The previous direct-execution session still owns Windows fault interception.");
        WindowsStackBounds.SetRecovery(handlers);
        _rawRecovery = handlers.RawRecovery;
        _recovery = handlers.Recovery;
        _report = handlers.UnhandledReport;
        if (_handler != 0) return;
        _installedParameters = handlers;
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RAW_HANDLER"), "1", StringComparison.Ordinal))
        {
            _rawHandlerThunk = WindowsFaultTrampoline.Create(
                Marshal.GetFunctionPointerForDelegate(RawDelegate), handlers.HostRspSlotTlsIndex, handlers.TlsGetValueAddress,
                handlers.WorkerAbortStub, handlers.WorkerAbortEligibleTlsIndex);
            if (_rawHandlerThunk == 0)
            {
                throw new InvalidOperationException("Failed to create raw exception handler trampoline");
            }
            _rawHandler = (nint)AddVectoredExceptionHandler(1u, _rawHandlerThunk);
            Console.Error.WriteLine($"[LOADER][INFO] Raw exception handler installed: 0x{_rawHandler:X16}");
        }
        else
        {
            Console.Error.WriteLine("[LOADER][INFO] Raw exception handler disabled by SHARPEMU_DISABLE_RAW_HANDLER=1");
        }

        _handlerThunk = WindowsFaultTrampoline.Create(
            Marshal.GetFunctionPointerForDelegate(RecoveryDelegateInstance), handlers.HostRspSlotTlsIndex, handlers.TlsGetValueAddress,
            handlers.WorkerAbortStub, handlers.WorkerAbortEligibleTlsIndex);
        if (_handlerThunk == 0)
        {
            throw new InvalidOperationException("Failed to create exception handler trampoline");
        }
        _handler = (nint)AddVectoredExceptionHandler(1u, _handlerThunk);
        Console.Error.WriteLine($"[LOADER][INFO] Exception handler installed: 0x{_handler:X16}");

        _unhandledFilterThunk = WindowsFaultTrampoline.Create(
            Marshal.GetFunctionPointerForDelegate(ReportDelegate), handlers.HostRspSlotTlsIndex, handlers.TlsGetValueAddress,
            handlers.WorkerAbortStub, handlers.WorkerAbortEligibleTlsIndex);
        if (_unhandledFilterThunk == 0)
        {
            throw new InvalidOperationException("Failed to create unhandled exception filter trampoline");
        }
        SetUnhandledExceptionFilter(_unhandledFilterThunk);
    }

    internal static void Remove()
    {
        if (_handler != 0)
        {
            RemoveVectoredExceptionHandler((void*)_handler);
            _handler = 0;
        }
        if (_rawHandler != 0)
        {
            RemoveVectoredExceptionHandler((void*)_rawHandler);
            _rawHandler = 0;
        }
        if (_rawHandlerThunk != 0)
        {
            HostMemory.Free((void*)_rawHandlerThunk, 0u, 32768u);
            _rawHandlerThunk = 0;
        }
        if (_handlerThunk != 0)
        {
            HostMemory.Free((void*)_handlerThunk, 0u, 32768u);
            _handlerThunk = 0;
        }
        if (_unhandledFilterThunk != 0)
        {
            SetUnhandledExceptionFilter(0);
            HostMemory.Free((void*)_unhandledFilterThunk, 0u, 32768u);
            _unhandledFilterThunk = 0;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "AddVectoredExceptionHandler")]
    private static extern void* AddVectoredExceptionHandler(uint first, nint handler);

    [DllImport("kernel32.dll", EntryPoint = "RemoveVectoredExceptionHandler")]
    private static extern uint RemoveVectoredExceptionHandler(void* handle);

    [DllImport("kernel32.dll", EntryPoint = "SetUnhandledExceptionFilter")]
    private static extern nint SetUnhandledExceptionFilter(nint topLevelExceptionFilter);
}
