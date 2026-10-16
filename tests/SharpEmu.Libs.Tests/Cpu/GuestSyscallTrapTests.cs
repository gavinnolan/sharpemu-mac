// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestSyscallTrapTests
{
    private const ulong ThunkAddress = 0x7000_0000_1040;
    private const ulong StubAddress = 0x7000_0000_1000;

    [Theory]
    [InlineData(0x7FFF_0000_1000UL)]
    [InlineData(0x7FFF_0000_1008UL)]
    [InlineData(0x7FFF_0000_10F7UL)]
    public void RedirectKeepsTheRedZoneAndPassesRipAndRspInTheSyscallScratchRegisters(ulong guestRsp)
    {
        var redirect = GuestSyscallTrap.ComputeRedirect(ThunkAddress, guestRsp, returnRip: 0x1234);

        Assert.Equal(ThunkAddress, redirect.Rip);
        Assert.Equal(0UL, redirect.Rsp % 16);
        Assert.True(redirect.Rsp <= guestRsp - GuestSyscallTrap.RedZoneBytes);
        Assert.True(redirect.Rsp > guestRsp - GuestSyscallTrap.RedZoneBytes - 16);
        // syscall itself clobbers rcx (return RIP) and r11 (RFLAGS); the trap borrows them.
        Assert.Equal(0x1234UL, redirect.Rcx);
        Assert.Equal(guestRsp, redirect.R11);
    }

    [Fact]
    public void ThunkFrameMatchesTheExportsView()
    {
        var instructions = Decode(GuestSyscallTrap.AssembleThunk(ThunkAddress, StubAddress));

        // Simulate the stack from entry (rsp = 0, 16-byte aligned) up to the call.
        long rsp = 0;
        var slots = new Dictionary<long, string>();
        long frameBase = long.MinValue;
        var index = 0;
        for (; instructions[index].Mnemonic != Mnemonic.Call; index++)
        {
            var instruction = instructions[index];
            switch (instruction.Mnemonic)
            {
                case Mnemonic.Push:
                    rsp -= 8;
                    slots[rsp] = instruction.Op0Register.ToString().ToLowerInvariant();
                    break;
                case Mnemonic.Pushfq:
                    rsp -= 8;
                    slots[rsp] = "rflags";
                    break;
                case Mnemonic.Sub when instruction.Op0Register == Register.RSP:
                    rsp -= (long)instruction.GetImmediate(1);
                    break;
                case Mnemonic.Lea when instruction.Op0Register == Register.RDI:
                    Assert.Equal(Register.RSP, instruction.MemoryBase);
                    frameBase = rsp + (long)instruction.MemoryDisplacement64;
                    break;
            }
        }

        Assert.Equal(0, rsp % 16);
        Assert.Equal(-GuestSyscallFrame.Size, frameBase);
        Assert.Equal("r11", slots[frameBase + GuestSyscallFrame.GuestRsp]);  // r11 = guest RSP at entry
        Assert.Equal("rcx", slots[frameBase + GuestSyscallFrame.ReturnRip]); // rcx = return RIP at entry
        Assert.Equal("rflags", slots[frameBase + GuestSyscallFrame.Rflags]);
        (string Name, int Offset)[] registers =
        [
            ("rax", GuestSyscallFrame.Rax), ("rbx", GuestSyscallFrame.Rbx), ("rcx", GuestSyscallFrame.Rcx),
            ("rdx", GuestSyscallFrame.Rdx), ("rsi", GuestSyscallFrame.Rsi), ("rdi", GuestSyscallFrame.Rdi),
            ("rbp", GuestSyscallFrame.Rbp), ("r8", GuestSyscallFrame.R8), ("r9", GuestSyscallFrame.R9),
            ("r10", GuestSyscallFrame.R10), ("r11", GuestSyscallFrame.R11), ("r12", GuestSyscallFrame.R12),
            ("r13", GuestSyscallFrame.R13), ("r14", GuestSyscallFrame.R14), ("r15", GuestSyscallFrame.R15),
        ];
        foreach (var (name, offset) in registers)
        {
            Assert.Equal(name, slots[frameBase + offset]);
        }

        // The call goes to the stub.
        Assert.Equal(Mnemonic.Mov, instructions[index - 1].Mnemonic);
        Assert.Equal(StubAddress, instructions[index - 1].GetImmediate(1));

        // After the call every pop reads back the slot its register was pushed to, the
        // last two load the return RIP into rcx and the guest RSP, and the thunk jumps.
        var popped = new List<(string Register, long Slot)>();
        for (index++; instructions[index].Mnemonic != Mnemonic.Jmp; index++)
        {
            var instruction = instructions[index];
            switch (instruction.Mnemonic)
            {
                case Mnemonic.Add when instruction.Op0Register == Register.RSP:
                    rsp += (long)instruction.GetImmediate(1);
                    break;
                case Mnemonic.Pop:
                    popped.Add((instruction.Op0Register.ToString().ToLowerInvariant(), rsp));
                    rsp += 8;
                    break;
                case Mnemonic.Popfq:
                    popped.Add(("rflags", rsp));
                    rsp += 8;
                    break;
            }
        }

        Assert.Equal(18, popped.Count);
        foreach (var (register, slot) in popped[..^2])
        {
            Assert.Equal(slots[slot], register);
        }

        Assert.Equal(("rcx", frameBase + GuestSyscallFrame.ReturnRip), popped[^2]);
        Assert.Equal(("rsp", frameBase + GuestSyscallFrame.GuestRsp), popped[^1]);
        Assert.Equal(Register.RCX, instructions[index].Op0Register);
    }

    [Fact]
    public void ThunkPreservesTheSseStateAroundTheCall()
    {
        var mnemonics = Decode(GuestSyscallTrap.AssembleThunk(ThunkAddress, StubAddress))
            .Select(i => i.Mnemonic)
            .ToList();
        var call = mnemonics.IndexOf(Mnemonic.Call);

        Assert.InRange(mnemonics.IndexOf(Mnemonic.Fxsave64), 0, call);
        Assert.True(mnemonics.IndexOf(Mnemonic.Fxrstor64) > call);
        Assert.InRange(mnemonics.IndexOf(Mnemonic.Cld), 0, call);
    }

    private static List<Instruction> Decode(byte[] code)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
        decoder.IP = ThunkAddress;
        var instructions = new List<Instruction>();
        while (decoder.IP < ThunkAddress + (ulong)code.Length)
        {
            decoder.Decode(out var instruction);
            Assert.NotEqual(Code.INVALID, instruction.Code);
            instructions.Add(instruction);
        }

        return instructions;
    }
}
