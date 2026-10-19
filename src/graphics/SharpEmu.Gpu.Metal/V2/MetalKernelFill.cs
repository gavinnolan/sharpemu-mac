// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// What a compute fill kernel stores into an image, decided from its repeating byte
/// pattern alone. A fill that starts at the image's first byte and covers all of it
/// writes the same texel into every element, whatever the tiling, as long as the
/// pattern repeats with the texel's period; that texel is then a clear colour when the
/// format can carry it through a clear exactly.
/// </summary>
internal static class MetalKernelFill
{
    /// <summary>The longest fill pattern worth reading at record time.</summary>
    public const int MaxPatternBytes = 64;

    /// <summary>
    /// The texel a pattern repeated from the image's first byte stores in every element of
    /// <paramref name="texelBytes"/> bytes, or false when elements would differ.
    /// </summary>
    public static bool TryResolveTexel(ReadOnlySpan<byte> pattern, int texelBytes, Span<byte> texel)
    {
        if (pattern.IsEmpty || texelBytes <= 0 || texel.Length < texelBytes)
        {
            return false;
        }

        if (texelBytes % pattern.Length == 0)
        {
            for (var offset = 0; offset < texelBytes; offset += pattern.Length)
            {
                pattern.CopyTo(texel[offset..]);
            }

            return true;
        }

        if (pattern.Length % texelBytes != 0)
        {
            return false;
        }

        var first = pattern[..texelBytes];
        for (var offset = texelBytes; offset < pattern.Length; offset += texelBytes)
        {
            if (!pattern.Slice(offset, texelBytes).SequenceEqual(first))
            {
                return false;
            }
        }

        first.CopyTo(texel);
        return true;
    }

    /// <summary>
    /// The clear colour that stores exactly these texel bytes in a colour image of this
    /// format. A clear goes through floating-point clear values, so only values that
    /// survive that are taken: zero in any format, and otherwise normal (or zero)
    /// floats and unsigned-normalized values. Integer formats take zero only, since the
    /// clear path carries no integer values.
    /// </summary>
    public static bool TryDecodeTexel(GpuFormat format, ReadOnlySpan<byte> texel, out GpuClearColor color)
    {
        color = default;
        if (texel.IndexOfAnyExcept((byte)0) < 0)
        {
            return true;
        }

        switch (format)
        {
            case GpuFormat.R32Sfloat when texel.Length == 4:
            case GpuFormat.R32G32Sfloat when texel.Length == 8:
            case GpuFormat.R32G32B32A32Sfloat when texel.Length == 16:
                for (var component = 0; component < texel.Length / 4; component++)
                {
                    var value = BitConverter.ToSingle(texel.Slice(component * 4, 4));
                    if (value != 0 && !float.IsNormal(value))
                    {
                        return false;
                    }

                    SetFloat(ref color, component, value);
                }

                return true;
            case GpuFormat.R16Sfloat when texel.Length == 2:
            case GpuFormat.R16G16Sfloat when texel.Length == 4:
            case GpuFormat.R16G16B16A16Sfloat when texel.Length == 8:
                for (var component = 0; component < texel.Length / 2; component++)
                {
                    var value = BitConverter.ToHalf(texel.Slice(component * 2, 2));
                    if (value != Half.Zero && !Half.IsNormal(value))
                    {
                        return false;
                    }

                    SetFloat(ref color, component, (float)value);
                }

                return true;
            case GpuFormat.R8Unorm when texel.Length == 1:
            case GpuFormat.R8G8Unorm when texel.Length == 2:
            case GpuFormat.R8G8B8A8Unorm when texel.Length == 4:
                for (var component = 0; component < texel.Length; component++)
                {
                    SetFloat(ref color, component, texel[component] / 255.0f);
                }

                return true;
            case GpuFormat.B8G8R8A8Unorm when texel.Length == 4:
                SetFloat(ref color, 0, texel[2] / 255.0f);
                SetFloat(ref color, 1, texel[1] / 255.0f);
                SetFloat(ref color, 2, texel[0] / 255.0f);
                SetFloat(ref color, 3, texel[3] / 255.0f);
                return true;
            case GpuFormat.R16Unorm when texel.Length == 2:
            case GpuFormat.R16G16Unorm when texel.Length == 4:
            case GpuFormat.R16G16B16A16Unorm when texel.Length == 8:
                for (var component = 0; component < texel.Length / 2; component++)
                {
                    SetFloat(ref color, component, BitConverter.ToUInt16(texel.Slice(component * 2, 2)) / 65535.0f);
                }

                return true;
            case GpuFormat.B10G11R11UfloatPack32 when texel.Length == 4:
            {
                var packed = BitConverter.ToUInt32(texel);
                if (!TryDecodeSmallFloat(packed & 0x7FF, 6, out var red) ||
                    !TryDecodeSmallFloat((packed >> 11) & 0x7FF, 6, out var green) ||
                    !TryDecodeSmallFloat(packed >> 22, 5, out var blue))
                {
                    return false;
                }

                color.Float32_0 = red;
                color.Float32_1 = green;
                color.Float32_2 = blue;
                color.Float32_3 = 1.0f;
                return true;
            }
            default:
                return false;
        }
    }

    // An unsigned 11- or 10-bit float (five exponent bits, bias 15): zero or a normal value.
    private static bool TryDecodeSmallFloat(uint bits, int mantissaBits, out float value)
    {
        var exponent = (int)(bits >> mantissaBits) & 0x1F;
        var mantissa = bits & ((1u << mantissaBits) - 1);
        value = 0;
        if (exponent == 0)
        {
            return mantissa == 0;
        }

        if (exponent == 0x1F)
        {
            return false;
        }

        value = MathF.ScaleB(1.0f + mantissa / (float)(1u << mantissaBits), exponent - 15);
        return true;
    }

    private static void SetFloat(ref GpuClearColor color, int component, float value)
    {
        switch (component)
        {
            case 0: color.Float32_0 = value; break;
            case 1: color.Float32_1 = value; break;
            case 2: color.Float32_2 = value; break;
            default: color.Float32_3 = value; break;
        }
    }
}
