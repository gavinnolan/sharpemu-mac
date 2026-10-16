// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using Iced.Intel;
using SharpEmu.HLE;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>Register changes that send a trapped guest syscall through the trap thunk.</summary>
internal readonly record struct GuestSyscallRedirect(ulong Rip, ulong Rsp, ulong Rcx, ulong R11);

/// <summary>
/// Registry of patched raw <c>syscall</c> sites, and the code a trapped site
/// is resumed into.
/// </summary>
/// <remarks>
/// The fault handler does not run the syscall: an HLE export running inside a
/// signal handler cannot take the lazy-commit or GPU-tracking faults its guest
/// memory accesses may raise, and blocks with the guest's signal frame live.
/// Instead the handler changes four registers and returns. The guest resumes
/// in the thunk with rcx = the RIP after the syscall and r11 = its RSP (the two
/// registers <c>syscall</c> itself clobbers) and RSP below its red zone. The
/// thunk pushes every register (a <see cref="GuestSyscallFrame"/>) and the
/// SSE state, calls the trap export through an ordinary import stub with rdi
/// = the frame, then pops the frame (now holding the results), restores RSP
/// and jumps to the return RIP. All guest registers survive except those the
/// FreeBSD return path defines: rax, rdx, rcx, r11 and RFLAGS.
/// </remarks>
internal static unsafe class GuestSyscallTrap
{
    /// <summary>SysV red zone below the guest RSP that the redirect must not touch.</summary>
    public const ulong RedZoneBytes = 128;

    /// <summary>Bytes the backend patches at the stub (a 16-byte import stub slot).</summary>
    public const int StubBytes = 16;

    /// <summary>Offset of the thunk in the code page, after the import stub slot.</summary>
    public const int ThunkOffset = 64;

    private const int CodePageBytes = 4096;

    private static readonly ConcurrentDictionary<ulong, int> s_sites = new();
    private static readonly object s_gate = new();
    private static nint s_codePage;

    public static int SiteCount => s_sites.Count;

    public static void RegisterSite(ulong address, int number) => s_sites[address] = number;

    public static bool IsRegisteredSite(ulong address) => s_sites.ContainsKey(address);

    /// <summary>The import stub the thunk calls; the loader publishes it under the trap NID.</summary>
    public static ulong StubAddress => (ulong)EnsureCodePage();

    /// <summary>
    /// Whether the backend has bound the stub to the trap export (it writes
    /// <c>mov rax, imm64; jmp rax</c> over the slot). Until then a redirect
    /// would call an unbound slot, so the handler declines.
    /// </summary>
    public static bool IsStubBound
    {
        get
        {
            var page = Volatile.Read(ref s_codePage);
            return page != 0 && ((byte*)page)[0] == 0x48 && ((byte*)page)[1] == 0xB8;
        }
    }

    /// <summary>Entry of the trap thunk, or 0 before the loader created it.</summary>
    public static ulong ThunkAddress
    {
        get
        {
            var page = Volatile.Read(ref s_codePage);
            return page == 0 ? 0 : (ulong)page + ThunkOffset;
        }
    }

    /// <summary>
    /// The registers that resume the guest in the thunk for a syscall whose
    /// next instruction is <paramref name="returnRip"/>.
    /// </summary>
    public static GuestSyscallRedirect ComputeRedirect(ulong thunk, ulong guestRsp, ulong returnRip) =>
        new(thunk, (guestRsp - RedZoneBytes) & ~0xFUL, returnRip, guestRsp);

    private static nint EnsureCodePage()
    {
        var page = Volatile.Read(ref s_codePage);
        if (page != 0)
        {
            return page;
        }

        lock (s_gate)
        {
            if (s_codePage != 0)
            {
                return s_codePage;
            }

            var memory = (byte*)HostMemory.Alloc(
                null, CodePageBytes, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_READWRITE);
            if (memory == null)
            {
                throw new OutOfMemoryException("Failed to allocate the raw syscall trap page");
            }

            // Until the backend binds the stub, calling it traps (int3) rather than running off.
            new Span<byte>(memory, CodePageBytes).Fill(0xCC);
            var thunk = AssembleThunk((ulong)memory + ThunkOffset, (ulong)memory);
            if (thunk.Length > CodePageBytes - ThunkOffset)
            {
                throw new InvalidOperationException("The raw syscall trap thunk does not fit its page.");
            }

            thunk.CopyTo(new Span<byte>(memory + ThunkOffset, thunk.Length));
            // Read-execute like the import stub region; the backend unprotects
            // the stub slot itself while it binds it.
            if (!HostMemory.Protect(memory, CodePageBytes, HostMemory.PAGE_EXECUTE_READ, out _))
            {
                throw new InvalidOperationException("Failed to protect the raw syscall trap page");
            }

            Volatile.Write(ref s_codePage, (nint)memory);
            return s_codePage;
        }
    }

    /// <summary>
    /// The thunk, assembled at <paramref name="thunkAddress"/>, calling
    /// <paramref name="stubAddress"/>. Entry state: RSP 16-byte aligned below
    /// the red zone, rcx = return RIP, r11 = guest RSP, everything else as the
    /// guest left it.
    /// </summary>
    internal static byte[] AssembleThunk(ulong thunkAddress, ulong stubAddress)
    {
        var a = new Assembler(64);
        a.push(r11);   // GuestSyscallFrame.GuestRsp
        a.push(rcx);   // GuestSyscallFrame.ReturnRip
        a.pushfq();    // GuestSyscallFrame.Rflags
        a.push(rax);
        a.push(rbx);
        a.push(rcx);
        a.push(rdx);
        a.push(rsi);
        a.push(rdi);
        a.push(rbp);
        a.push(r8);
        a.push(r9);
        a.push(r10);
        a.push(r11);
        a.push(r12);
        a.push(r13);
        a.push(r14);
        a.push(r15);   // 18 pushes: RSP is 16-byte aligned again
        a.cld();
        a.sub(rsp, 512);
        a.fxsave64(__[rsp]);
        a.lea(rdi, __[rsp + 512]);
        a.mov(rax, stubAddress);
        a.call(rax);
        a.fxrstor64(__[rsp]);
        a.add(rsp, 512);
        a.pop(r15);
        a.pop(r14);
        a.pop(r13);
        a.pop(r12);
        a.pop(r11);
        a.pop(r10);
        a.pop(r9);
        a.pop(r8);
        a.pop(rbp);
        a.pop(rdi);
        a.pop(rsi);
        a.pop(rdx);
        a.pop(rcx);
        a.pop(rbx);
        a.pop(rax);
        a.popfq();
        a.pop(rcx);    // return RIP, as sysret leaves it
        a.pop(rsp);    // guest RSP
        a.jmp(rcx);

        var writer = new ListCodeWriter();
        a.Assemble(writer, thunkAddress);
        return writer.Bytes.ToArray();
    }

    private sealed class ListCodeWriter : CodeWriter
    {
        public List<byte> Bytes { get; } = new();

        public override void WriteByte(byte value) => Bytes.Add(value);
    }
}
