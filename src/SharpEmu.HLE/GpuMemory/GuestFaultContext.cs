// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.HLE.GpuMemory;

/// <summary>
/// The guest machine state of the fault currently being resolved, published by the fault
/// bridge for the recovery chain to read. A watch that fires has the faulting address but
/// not the instruction that made it, and the instruction is the answer whenever the writer
/// is native guest code rather than a managed path.
///
/// Thread-static: a fault is resolved on the thread that took it.
/// </summary>
public static class GuestFaultContext
{
    /// <summary>The sixteen general registers in Win64 CONTEXT order.</summary>
    public static readonly string[] RegisterNames =
        ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
         "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"];

    [ThreadStatic]
    private static ulong[]? _registers;

    [ThreadStatic]
    private static ulong _rip;

    [ThreadStatic]
    private static bool _valid;

    /// <summary>Publishes the machine state of the fault about to be resolved.</summary>
    public static void Publish(ulong rip, ReadOnlySpan<ulong> registers)
    {
        var storage = _registers ??= new ulong[RegisterNames.Length];
        registers[..Math.Min(registers.Length, storage.Length)].CopyTo(storage);
        _rip = rip;
        _valid = true;
    }

    /// <summary>Forgets the published state once the fault has been resolved.</summary>
    public static void Clear() => _valid = false;

    /// <summary>A snapshot a diagnostic can format without unsafe code.</summary>
    public readonly struct Snapshot
    {
        internal Snapshot(ulong rip, ulong[]? registers, bool valid)
        {
            Rip = rip;
            Registers = registers;
            IsValid = valid;
        }

        public ulong Rip { get; }

        public bool IsValid { get; }

        internal ulong[]? Registers { get; }

        /// <summary>The first sixteen bytes at the faulting instruction, or a note.</summary>
        public string DescribeCode()
        {
            if (!IsValid || Rip < 0x10000)
            {
                return "<no instruction>";
            }

            if (!GuestCodeReader.TryRead(Rip, 16, out var bytes))
            {
                return "<unreadable>";
            }

            var text = Convert.ToHexString(bytes);
            return GuestCodeReader.Disassemble(Rip, bytes) is { Length: > 0 } disassembly
                ? $"{text} ({disassembly})"
                : text;
        }

        /// <summary>
        /// True when any general register holds this value. A store's operand is in a
        /// register when it faults, so this is how a trap tells the write it is hunting
        /// from every other write to the same address.
        /// </summary>
        public bool CarriesValue(ulong value)
        {
            if (!IsValid || Registers is null)
            {
                return false;
            }

            foreach (var register in Registers)
            {
                if (register == value)
                {
                    return true;
                }
            }

            return false;
        }

        public string DescribeRegisters()
        {
            if (!IsValid || Registers is null)
            {
                return "<unavailable>";
            }

            var text = new StringBuilder();
            for (var index = 0; index < RegisterNames.Length && index < Registers.Length; index++)
            {
                text.Append(index == 0 ? string.Empty : " ")
                    .Append(RegisterNames[index]).Append("=0x").Append(Registers[index].ToString("X16"));
            }

            return text.ToString();
        }
    }

    /// <summary>The state published for the fault being resolved on this thread.</summary>
    public static Snapshot Capture() => new(_rip, _registers, _valid);
}

/// <summary>
/// Reads guest code bytes for a diagnostic. Guest code is identity-mapped and the reader
/// is only ever used from a fault that has already stopped on that very instruction, so a
/// direct read is safe; the indirection exists so the HLE layer need not be unsafe.
/// </summary>
public static class GuestCodeReader
{
    /// <summary>Set by the host once guest code is mapped; null leaves code undumpable.</summary>
    public static Func<ulong, int, byte[]?>? Reader { get; set; }

    /// <summary>Set by the platform that owns a disassembler; null prints bytes only.</summary>
    public static Func<ulong, byte[], string?>? Disassembler { get; set; }

    /// <summary>The first instruction of these bytes, or null when nothing can read it.</summary>
    public static string? Disassemble(ulong address, byte[] bytes)
    {
        if (Disassembler is not { } disassembler)
        {
            return null;
        }

        try
        {
            return disassembler(address, bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryRead(ulong address, int count, out byte[] bytes)
    {
        bytes = [];
        if (Reader is not { } reader)
        {
            return false;
        }

        try
        {
            var read = reader(address, count);
            if (read is null)
            {
                return false;
            }

            bytes = read;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
