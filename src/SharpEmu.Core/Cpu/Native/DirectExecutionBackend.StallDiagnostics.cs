// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using Microsoft.Win32.SafeHandles;

using SharpEmu.Core.Cpu.Native.Platform;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
    private SafeWaitHandle? _mainExecutionThreadHandle;
    private int _mainExecutionThreadId;

    // Suspending another thread is an explicit platform capability. Native
    // declarations and export resolution belong to the execution package.
    private static bool SupportsThreadContextCapture =>
        (DirectExecutionPlatform.Current.Capabilities & DirectExecutionCapabilities.ThreadContextCapture) != 0;
    // One immutable code page serves all diagnostic samples for the process lifetime.
    private static readonly Lazy<ThreadContextCaptureCode> ThreadCaptureCode = new(() => new());

    private sealed class ThreadContextCaptureCode : SafeHandle
    {
        public override bool IsInvalid => handle == 0;

        public ThreadContextCaptureCode() : base(0, ownsHandle: true)
        {
            var suspendAddress = ExecutionPlatform.GetRuntimeRoutine(HostRuntimeFunction.SuspendThread);
            var contextAddress = ExecutionPlatform.GetRuntimeRoutine(HostRuntimeFunction.GetThreadContext);
            var resumeAddress = ExecutionPlatform.GetRuntimeRoutine(HostRuntimeFunction.ResumeThread);
            if (suspendAddress == 0 || contextAddress == 0 || resumeAddress == 0) return;
            var instructions = BuildThreadContextCaptureCode(suspendAddress, contextAddress, resumeAddress);
            SetHandle((nint)VirtualAlloc(null, (nuint)instructions.Length, 0x3000, 0x04));
            if (IsInvalid) return;
            instructions.CopyTo(new Span<byte>((void*)handle, instructions.Length));
            uint previousProtection = 0;
            if (!VirtualProtect((void*)handle, (nuint)instructions.Length, 0x20, &previousProtection))
            {
                Dispose();
                return;
            }
            FlushInstructionCache(GetCurrentProcess(), (void*)handle, (nuint)instructions.Length);
        }

        protected override bool ReleaseHandle() => VirtualFree((void*)handle, 0, 0x8000);
    }

    private static SafeWaitHandle? OpenThreadForContext(uint threadId)
    {
        if (!SupportsThreadContextCapture || RuntimeInformation.ProcessArchitecture != Architecture.X64 || threadId == 0)
            return null;

        var threadHandle = ExecutionPlatform.OpenThreadForContext(threadId);
        return threadHandle == 0 ? null : new SafeWaitHandle(threadHandle, ownsHandle: true);
    }

    private static bool TryCaptureHostThreadContext(int threadId, out HostThreadContextSnapshot snapshot)
    {
        if (!SupportsThreadContextCapture)
            return TryReadPlatformThreadRegisters(threadId, out snapshot);

        using var threadHandle = OpenThreadForContext(unchecked((uint)threadId));
        return TryCaptureThreadContext(threadHandle, out snapshot);
    }

    // A host without the context-capture stub (macOS reads them through Mach) hands the
    // registers over directly.
    private static bool TryReadPlatformThreadRegisters(int threadId, out HostThreadContextSnapshot snapshot)
    {
        snapshot = default;
        Span<ulong> registers = stackalloc ulong[17];
        if (threadId == 0 || !ExecutionPlatform.TryReadThreadRegisters(unchecked((uint)threadId), registers))
            return false;

        // rax rbx rcx rdx rdi rsi rbp rsp r8-r15 rip
        snapshot = new HostThreadContextSnapshot(
            unchecked((uint)threadId),
            Rip: registers[16], Rsp: registers[7], Rbp: registers[6],
            Rax: registers[0], Rbx: registers[1], Rcx: registers[2], Rdx: registers[3],
            Rsi: registers[5], Rdi: registers[4],
            R8: registers[8], R9: registers[9], R10: registers[10], R11: registers[11],
            R12: registers[12], R13: registers[13], R14: registers[14], R15: registers[15]);
        return true;
    }

    private static bool TryCaptureThreadContext(SafeWaitHandle? threadHandle, out HostThreadContextSnapshot snapshot)
    {
        snapshot = default;
        if (threadHandle is null || !SupportsThreadContextCapture || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return false;

        var handleReferenced = false;
        try
        {
            threadHandle.DangerousAddRef(ref handleReferenced);
            var nativeHandle = threadHandle.DangerousGetHandle();
            var threadId = ExecutionPlatform.GetThreadId(nativeHandle);
            if (threadId == 0 || threadId == GetCurrentThreadId()) return false;

            var captureCode = ThreadCaptureCode.Value;
            if (captureCode.IsInvalid || captureCode.IsClosed) return false;

            byte* contextStorage = stackalloc byte[Win64ContextSize + 15];
            var contextRecord = (void*)(((nuint)contextStorage + 15) & ~(nuint)15);
            new Span<byte>(contextRecord, Win64ContextSize).Clear();
            WriteCtxU32(contextRecord, Win64ContextFlagsOffset, ContextAmd64ControlInteger);

            // One native transition keeps managed work outside the suspended interval.
            var captured = ((delegate* unmanaged<nint, void*, int>)captureCode.DangerousGetHandle())(nativeHandle, contextRecord);
            if (captured == 0) return false;
            snapshot = DecodeHostThreadContext(threadId, contextRecord);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (handleReferenced) threadHandle!.DangerousRelease();
        }
    }

    private static HostThreadContextSnapshot DecodeHostThreadContext(uint threadId, void* contextRecord) => new(
        threadId,
        ReadCtxU64(contextRecord, 248), ReadCtxU64(contextRecord, 152), ReadCtxU64(contextRecord, 160),
        ReadCtxU64(contextRecord, 120), ReadCtxU64(contextRecord, 144), ReadCtxU64(contextRecord, 128),
        ReadCtxU64(contextRecord, 136), ReadCtxU64(contextRecord, 168), ReadCtxU64(contextRecord, 176),
        ReadCtxU64(contextRecord, 184), ReadCtxU64(contextRecord, 192), ReadCtxU64(contextRecord, 200),
        ReadCtxU64(contextRecord, 208), ReadCtxU64(contextRecord, 216), ReadCtxU64(contextRecord, 224),
        ReadCtxU64(contextRecord, 232), ReadCtxU64(contextRecord, 240));

    internal static byte[] BuildThreadContextCaptureCode(nint suspendAddress, nint contextAddress, nint resumeAddress)
    {
        var instructions = new List<byte>();
        void CallAddress(nint address)
        {
            instructions.AddRange([0x48, 0xB8]);
            instructions.AddRange(BitConverter.GetBytes((long)address));
            instructions.AddRange([0xFF, 0xD0]);
        }

        // Preserve nonvolatile registers and reserve aligned argument space.
        instructions.AddRange([0x53, 0x56, 0x57, 0x48, 0x83, 0xEC, 0x20,
            0x48, 0x89, 0xCB, 0x48, 0x89, 0xD6, 0x31, 0xFF]);
        CallAddress(suspendAddress);
        instructions.AddRange([0x83, 0xF8, 0xFF, 0x74, 0]);
        var failedSuspendJump = instructions.Count - 1;
        instructions.AddRange([0x48, 0x89, 0xD9, 0x48, 0x89, 0xF2]);
        CallAddress(contextAddress);
        instructions.AddRange([0x89, 0xC7, 0x48, 0x89, 0xD9]);
        CallAddress(resumeAddress);
        instructions.AddRange([0x83, 0xF8, 0xFF, 0x75, 0x02, 0x31, 0xFF]);
        var returnOffset = instructions.Count;
        instructions.AddRange([0x89, 0xF8, 0x48, 0x83, 0xC4, 0x20, 0x5F, 0x5E, 0x5B, 0xC3]);
        instructions[failedSuspendJump] = checked((byte)(returnOffset - failedSuspendJump - 1));
        return instructions.ToArray();
    }

    private void LogMainThreadStallContext()
    {
        if (!(SupportsThreadContextCapture
                ? TryCaptureThreadContext(Volatile.Read(ref _mainExecutionThreadHandle), out var context)
                : TryReadPlatformThreadRegisters(Volatile.Read(ref _mainExecutionThreadId), out context))) return;

        Console.Error.WriteLine($"[LOADER][ERROR] Live hardware context (main thread {context.ThreadId}):");
        Console.Error.WriteLine($"[LOADER][ERROR]   RIP=0x{context.Rip:X16} RSP=0x{context.Rsp:X16} RBP=0x{context.Rbp:X16}");
        Console.Error.WriteLine($"[LOADER][ERROR]   RAX=0x{context.Rax:X16} RBX=0x{context.Rbx:X16} RCX=0x{context.Rcx:X16} RDX=0x{context.Rdx:X16}");
        Console.Error.WriteLine($"[LOADER][ERROR]   RSI=0x{context.Rsi:X16} RDI=0x{context.Rdi:X16} R8=0x{context.R8:X16} R9=0x{context.R9:X16}");
        Console.Error.WriteLine($"[LOADER][ERROR]   R10=0x{context.R10:X16} R11=0x{context.R11:X16} R12=0x{context.R12:X16} R13=0x{context.R13:X16} R14=0x{context.R14:X16} R15=0x{context.R15:X16}");

        var modules = Array.Empty<(ulong Start, ulong Size, string Name)>();
        try
        {
            using var process = Process.GetCurrentProcess();
            modules = process.Modules.Cast<ProcessModule>().Select(module =>
                (Start: unchecked((ulong)module.BaseAddress), Size: (ulong)module.ModuleMemorySize, Name: module.ModuleName)).ToArray();
        }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
        string DescribeAddress(ulong address)
        {
            foreach (var module in modules)
                if (address >= module.Start && address - module.Start < module.Size)
                    return $"{module.Name}+0x{address - module.Start:X}";
            return $"0x{address:X16}";
        }

        Console.Error.WriteLine($"[LOADER][ERROR]   RIP location: {DescribeAddress(context.Rip)}");
        // The thread has resumed. These bytes are best-effort observations, not an atomic snapshot.
        var instructionBytes = new byte[48];
        if (context.Rip >= 32 && TryReadHostBytes(context.Rip - 32, instructionBytes))
            Console.Error.WriteLine($"[LOADER][ERROR]   Live bytes @[RIP-32..RIP+16]: {Convert.ToHexString(instructionBytes)}");
        var currentInstruction = new byte[16];
        if (TryReadHostBytes(context.Rip, currentInstruction))
            Console.Error.WriteLine($"[LOADER][ERROR]   Live bytes @RIP: {Convert.ToHexString(currentInstruction)}");

        Console.Error.WriteLine("[LOADER][ERROR]   Live stack pointer candidates:");
        for (var offset = 0; offset < 256; offset += sizeof(ulong))
        {
            var address = context.Rsp + (ulong)offset;
            if (address < context.Rsp || !TryReadDiagnosticHostQword(address, out var value)) break;
            if (value != 0)
                Console.Error.WriteLine($"[LOADER][ERROR]     [rsp+0x{offset:X2}] = 0x{value:X16} ({DescribeAddress(value)})");
        }
    }

    // SHARPEMU_SNAPSHOT_PEEK=<base>[+<offset>]:<length>[,...]: guest memory each stall snapshot
    // prints as qwords, the base a hex address or, where the host can capture it, one of the main
    // thread's live registers (r14+0x20:0x60), so a structure the stuck code holds in a register can
    // be read without knowing where it lives. "ersp" is the stack pointer the main thread last
    // entered an import with, which every host has; "ersp-0x2000:0x1000" names a range below it.
    // A leading '*' reads the pointer stored at the base first: "*0x8031E7FC8+0x40:0x20".
    private static readonly string? SnapshotPeeks = Environment.GetEnvironmentVariable("SHARPEMU_SNAPSHOT_PEEK");

    private void LogSnapshotPeeks()
    {
        if (string.IsNullOrWhiteSpace(SnapshotPeeks) || _cpuContext is not { } cpuContext) return;
        var live = TryCaptureHostThreadContext(Volatile.Read(ref _mainExecutionThreadId), out var context);
        if (live)
            Console.Error.WriteLine(
                $"[LOADER][ERROR]   peek registers: rip=0x{context.Rip:X16} rsp=0x{context.Rsp:X16} rbp=0x{context.Rbp:X16} " +
                $"rax=0x{context.Rax:X16} rbx=0x{context.Rbx:X16} rcx=0x{context.Rcx:X16} rdx=0x{context.Rdx:X16} " +
                $"rsi=0x{context.Rsi:X16} rdi=0x{context.Rdi:X16} r12=0x{context.R12:X16} r13=0x{context.R13:X16} " +
                $"r14=0x{context.R14:X16} r15=0x{context.R15:X16}");

        foreach (var token in SnapshotPeeks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = token.LastIndexOf(':');
            if (colon <= 0 || !TryParseHex(token[(colon + 1)..], out var length)) continue;
            var location = token[..colon];
            var dereference = location.StartsWith('*');
            if (dereference) location = location[1..];
            var sign = location.IndexOfAny(['+', '-']);
            var baseText = sign < 0 ? location : location[..sign];
            ulong offset = 0;
            if (sign >= 0 && !TryParseHex(location[(sign + 1)..], out offset)) continue;
            if (sign >= 0 && location[sign] == '-') offset = unchecked(0UL - offset);
            ulong address;
            switch (baseText.ToLowerInvariant())
            {
                case "ersp": address = cpuContext[SharpEmu.HLE.CpuRegister.Rsp]; break;
                case "rax" or "rbx" or "rcx" or "rdx" or "rsi" or "rdi" or "rbp" or "rsp" or "r12" or "r13" or "r14" or "r15" when !live:
                    continue;
                case "rax": address = context.Rax; break;
                case "rbx": address = context.Rbx; break;
                case "rcx": address = context.Rcx; break;
                case "rdx": address = context.Rdx; break;
                case "rsi": address = context.Rsi; break;
                case "rdi": address = context.Rdi; break;
                case "rbp": address = context.Rbp; break;
                case "rsp": address = context.Rsp; break;
                case "r12": address = context.R12; break;
                case "r13": address = context.R13; break;
                case "r14": address = context.R14; break;
                case "r15": address = context.R15; break;
                default:
                    if (!TryParseHex(baseText, out address)) continue;
                    break;
            }

            if (dereference && !cpuContext.TryReadUInt64(address, out address)) continue;
            address = unchecked(address + offset);
            for (ulong at = 0; at < Math.Min(length, 0x1000UL); at += 32)
            {
                var line = new System.Text.StringBuilder($"[LOADER][ERROR]   peek {token} 0x{address + at:X16}:");
                for (ulong word = 0; word < 32 && at + word < length; word += 8)
                {
                    line.Append(cpuContext.TryReadUInt64(address + at + word, out var value) ? $" {value:X16}" : " ????????????????");
                }

                Console.Error.WriteLine(line.ToString());
            }
        }

        static bool TryParseHex(string text, out ulong value) => ulong.TryParse(
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
            System.Globalization.NumberStyles.HexNumber, null, out value);
    }

}
