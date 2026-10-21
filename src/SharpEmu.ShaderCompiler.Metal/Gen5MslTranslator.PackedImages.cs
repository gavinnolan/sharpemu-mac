// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        // One component of a _PCK texel: its first bit in the texel, its width, and the
        // number format the host view converts it with.
        private readonly record struct PackedComponent(int Bit, int Bits, uint NumberFormat);

        // The memory layout of a texel of the guest format, as the host view reads it (an
        // 8 USCALED view is 8 UNORM and 11_11_10 UINT one 32-bit UINT, with the same bits).
        // _PCK accesses move a texel's bits without format conversion (RDNA2 ISA, MIMG
        // opcodes 2-5, 10, 11), which the view's conversion can only be undone for when it
        // is exact: integer components, UNORM up to 16 bits and 16- or 32-bit floats.
        private static bool TryGetPackedLayout(
            uint unifiedFormat,
            out PackedComponent[] components,
            out int texelBits,
            out string error)
        {
            components = [];
            texelBits = 0;
            error = string.Empty;
            var viewFormat = GuestImageFormat.Remap(unifiedFormat);
            if (!Gfx10UnifiedFormat.TryDecode(viewFormat, out var dataFormat, out var numberFormat) ||
                Gfx10UnifiedFormat.ComponentCount(dataFormat) == 0)
            {
                error = $"packed image access to format {unifiedFormat}, which has no texel layout";
                return false;
            }

            var count = Gfx10UnifiedFormat.ComponentCount(dataFormat);
            components = new PackedComponent[count];
            for (uint component = 0; component < count; component++)
            {
                if (!Gfx10UnifiedFormat.TryGetComponentLayout(dataFormat, component, out var byteOffset, out var bitOffset, out var bits))
                {
                    error = $"packed image access to format {unifiedFormat}, which has no layout for component {component}";
                    return false;
                }

                var exact = numberFormat switch
                {
                    0 => bits <= 16,
                    4 or 5 => true,
                    7 => bits is 16 or 32,
                    _ => false,
                };
                if (!exact)
                {
                    error = $"packed image access to format {unifiedFormat}: its view's conversion of {bits}-bit number format {numberFormat} components cannot be undone exactly";
                    return false;
                }

                components[component] = new PackedComponent((int)(byteOffset * 8 + bitOffset), (int)bits, numberFormat);
                texelBits = Math.Max(texelBits, components[component].Bit + (int)bits);
            }

            return true;
        }

        // IMAGE_LOAD(_MIP)_PCK(_SGN): the texel's components back to their raw bits, packed into
        // dwords in memory order. _SGN sign-extends a texel narrower than a dword from its top
        // bit (the ISA: "8- and 16-bit elements are sign-extended"; AnyPS5 reads it the same way).
        private bool TryPackImageTexel(string texel, string kind, uint unifiedFormat, bool signed, out string packed, out string error)
        {
            packed = string.Empty;
            error = string.Empty;
            if (unifiedFormat == DescriptorConstants.InvalidFormat)
            {
                // A null T#: the read returns zero.
                packed = "uint4(0u)";
                return true;
            }

            if (!TryGetPackedLayout(unifiedFormat, out var components, out var texelBits, out error))
            {
                return false;
            }

            var words = new[] { "0u", "0u", "0u", "0u" };
            for (var index = 0; index < components.Length; index++)
            {
                var (bit, bits, numberFormat) = components[index];
                var scalar = $"{texel}[{index}]";
                var raw = numberFormat switch
                {
                    0 => $"(uint)({scalar} * {(1u << bits) - 1u}.0f + 0.5f)",
                    7 when bits == 16 => $"(uint)as_type<ushort>(half({scalar}))",
                    _ => kind == "uint" ? scalar : $"as_type<uint>({scalar})",
                };
                if (bits < 32)
                {
                    raw = $"(({raw}) & 0x{(1u << bits) - 1u:X}u)";
                }

                var shift = bit % 32;
                words[bit / 32] = $"{words[bit / 32]} | ({raw}{(shift == 0 ? string.Empty : $" << {shift}u")})";
            }

            if (signed && texelBits < 32)
            {
                words[0] = $"(uint)extract_bits(as_type<int>({words[0]}), 0u, {texelBits}u)";
            }

            packed = Temp("uint4", $"uint4({string.Join(", ", words)})");
            return true;
        }

        // IMAGE_STORE(_MIP)_PCK: DMASK names the texel dwords the data registers carry, in
        // order; each component is cut from its bits and converted for the view's write.
        // The bits are the memory's, so the T# DST_SEL does not apply.
        private bool TryUnpackImageTexel(Gen5ImageControl image, string kind, uint unifiedFormat, out string[] values, out string error)
        {
            values = [];
            error = string.Empty;
            var zero = kind switch { "float" => "0.0f", "int" => "0", _ => "0u" };
            if (unifiedFormat == DescriptorConstants.InvalidFormat)
            {
                // A null T#: the write lands on the host's placeholder and is never read back.
                values = [zero, zero, zero, zero];
                return true;
            }

            if (!TryGetPackedLayout(unifiedFormat, out var components, out _, out error))
            {
                return false;
            }

            var words = new string[4];
            uint register = 0;
            for (var word = 0; word < 4; word++)
            {
                words[word] = (image.Dmask & (1u << word)) != 0 ? $"v[{image.VectorData + register++}]" : "0u";
            }

            values = [zero, zero, zero, zero];
            for (var index = 0; index < components.Length && index < 4; index++)
            {
                var (bit, bits, numberFormat) = components[index];
                var word = words[bit / 32];
                var field = bits == 32 ? word : $"extract_bits({word}, {bit % 32}u, {bits}u)";
                values[index] = (numberFormat, kind) switch
                {
                    (0, _) => $"(float)({field}) / {(1u << bits) - 1u}.0f",
                    (7, _) when bits == 16 => $"(float)as_type<half>((ushort)({field}))",
                    (7, _) => $"as_type<float>({field})",
                    (5, "int") => bits == 32 ? $"as_type<int>({word})" : $"extract_bits(as_type<int>({word}), {bit % 32}u, {bits}u)",
                    (_, "int") => $"as_type<int>({field})",
                    (_, "float") => $"as_type<float>({field})",
                    _ => field,
                };
            }

            return true;
        }
    }
}
