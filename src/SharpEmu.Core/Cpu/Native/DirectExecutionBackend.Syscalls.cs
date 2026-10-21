// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

// Raw guest `syscall` instructions. Two traps lead here: the ud2
// the loader put over a recognised syscall site, and the SIGSYS XNU/Rosetta
// raise for a classless syscall the scanner did not recognise. Both only
// redirect the guest into GuestSyscallTrap's thunk; the syscall itself runs
// after the signal handler has returned, through the trap export.
public sealed partial class DirectExecutionBackend
{
    private static int _unpatchedSyscallAnnounced;

    /// <summary>
    /// #UD at a loader-patched syscall site: resume in the trap thunk with the
    /// syscall's return RIP just past the 2-byte instruction.
    /// </summary>
    private unsafe bool TryRecoverPatchedSyscall(void* contextRecord, ulong rip)
    {
        if (!GuestSyscallTrap.IsRegisteredSite(rip))
        {
            return false;
        }

        return TryRedirectToSyscallTrap(contextRecord, rip + 2);
    }

    /// <summary>
    /// Host SIGSYS for a raw guest syscall the loader did not patch. The
    /// kernel reports RIP after the instruction and leaves rax = the number.
    /// Only a <c>0F 05</c> right before RIP qualifies; host code always
    /// passes a syscall class, so a classless syscall here is guest code.
    /// </summary>
    private unsafe bool TryRecoverUnpatchedSyscall(void* contextRecord, ulong rip)
    {
        var opcode = new byte[2];
        if (rip < 2 || !TryReadExecutableBytes(rip - 2, opcode) || opcode[0] != 0x0F || opcode[1] != 0x05)
        {
            return false;
        }

        if (!TryRedirectToSyscallTrap(contextRecord, rip))
        {
            return false;
        }

        if (Interlocked.Exchange(ref _unpatchedSyscallAnnounced, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Raw guest syscall 0x{ReadCtxU64(contextRecord, CTX_RAX):X} at 0x{rip - 2:X16} " +
                "was not patched at load; servicing it through the host SIGSYS fallback.");
        }

        return true;
    }

    private unsafe bool TryRedirectToSyscallTrap(void* contextRecord, ulong returnRip)
    {
        // The thunk calls the trap export through an import stub, which needs
        // a guest thread with a live CPU context and a bound stub.
        if (ActiveCpuContext is null || !GuestSyscallTrap.IsStubBound)
        {
            return false;
        }

        var redirect = GuestSyscallTrap.ComputeRedirect(
            GuestSyscallTrap.ThunkAddress,
            ReadCtxU64(contextRecord, CTX_RSP),
            returnRip);
        WriteCtxU64(contextRecord, CTX_RCX, redirect.Rcx);
        WriteCtxU64(contextRecord, CTX_R11, redirect.R11);
        WriteCtxU64(contextRecord, CTX_RSP, redirect.Rsp);
        WriteCtxU64(contextRecord, CTX_RIP, redirect.Rip);
        return true;
    }
}
