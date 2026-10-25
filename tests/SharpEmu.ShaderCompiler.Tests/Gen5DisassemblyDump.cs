// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// SHARPEMU_DISASM_BIN=<shader.bin> [SHARPEMU_DISASM_OUT=<file>]: prints a raw shader dump (a skipped-graphics
// capture, say) as the translator decodes it, one instruction per line. Does nothing without the variable.
public sealed class Gen5DisassemblyDump
{
    [Fact]
    public void DumpsTheNamedBinary()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_DISASM_BIN");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var context = new CpuContext(new BinaryMemory(bytes), Generation.Gen5);
        var text = new StringBuilder();
        if (!Gen5ShaderTranslator.TryDecodeFunction(context, 0x1000, (uint)bytes.Length & ~3u, out var program, out var error))
        {
            text.AppendLine($"; decode stopped: {error}");
        }

        foreach (var instruction in program.Instructions)
        {
            text.Append($"{instruction.Pc - 0x1000:X5}  {instruction.Opcode,-28}");
            text.Append(string.Join(", ", instruction.Destinations));
            if (instruction.Sources.Count > 0)
            {
                text.Append(instruction.Destinations.Count > 0 ? " <- " : "");
                text.Append(string.Join(", ", instruction.Sources));
            }

            if (instruction.Control is not null)
            {
                text.Append($"  {instruction.Control}");
            }

            text.AppendLine();
        }

        var output = Environment.GetEnvironmentVariable("SHARPEMU_DISASM_OUT");
        if (string.IsNullOrEmpty(output))
        {
            Console.WriteLine(text);
        }
        else
        {
            File.WriteAllText(output, text.ToString());
        }
    }

    private sealed class BinaryMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
