// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio3d;

/// <summary>
/// Pure mixing helpers for libSceAudio3d. Everything is accumulated as
/// interleaved stereo float (L, R) and converted to PCM16 for the host.
/// </summary>
internal static class Audio3dMixer
{
    private const float HalfPower = 0.70710677f;

    /// <summary>
    /// Adds a bed buffer of <paramref name="samples"/> frames to
    /// <paramref name="stereo"/>, downmixing 2/6/8 channel layouts.
    /// 6ch order: FL FR C LFE SL SR. 8ch order (Sony): FL FR C LFE BL BR SL SR.
    /// The LFE channel is dropped, as in a conventional stereo downmix.
    /// </summary>
    public static void AccumulateBed(
        Span<float> stereo, ReadOnlySpan<byte> source, int channels, bool isFloat, int samples)
    {
        var bytesPerSample = isFloat ? 4 : 2;
        Span<float> frame = stackalloc float[8];
        for (var i = 0; i < samples; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                frame[c] = ReadSample(source, (i * channels + c) * bytesPerSample, isFloat);
            }

            float left;
            float right;
            switch (channels)
            {
                case 2:
                    left = frame[0];
                    right = frame[1];
                    break;
                case 6:
                    left = frame[0] + (HalfPower * frame[2]) + (HalfPower * frame[4]);
                    right = frame[1] + (HalfPower * frame[2]) + (HalfPower * frame[5]);
                    break;
                default:
                    left = frame[0] + (HalfPower * frame[2]) + (0.5f * frame[4]) + (HalfPower * frame[6]);
                    right = frame[1] + (HalfPower * frame[2]) + (0.5f * frame[5]) + (HalfPower * frame[7]);
                    break;
            }

            stereo[i * 2] += left;
            stereo[(i * 2) + 1] += right;
        }
    }

    /// <summary>
    /// Adds a mono object signal centred with equal power. Object positions are
    /// not rendered: positional (HRTF/spatial) rendering is out of scope, so
    /// every object is folded to the bed layout at the centre.
    /// </summary>
    public static void AccumulateObject(Span<float> stereo, ReadOnlySpan<float> mono)
    {
        for (var i = 0; i < mono.Length; i++)
        {
            var sample = mono[i] * HalfPower;
            stereo[i * 2] += sample;
            stereo[(i * 2) + 1] += sample;
        }
    }

    public static void ReadMono(
        ReadOnlySpan<byte> source, bool isFloat, int samples, Span<float> destination)
    {
        var bytesPerSample = isFloat ? 4 : 2;
        for (var i = 0; i < samples; i++)
        {
            destination[i] = ReadSample(source, i * bytesPerSample, isFloat);
        }
    }

    public static void ConvertToPcm16(ReadOnlySpan<float> stereo, Span<byte> destination)
    {
        for (var i = 0; i < stereo.Length; i++)
        {
            var clamped = Math.Clamp(stereo[i], -1f, 1f);
            var value = (short)Math.Clamp(
                (int)MathF.Round(clamped * 32767f), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], value);
        }
    }

    private static float ReadSample(ReadOnlySpan<byte> source, int offset, bool isFloat)
    {
        if (isFloat)
        {
            var value = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(source[offset..]));
            return float.IsFinite(value) ? value : 0f;
        }

        return BinaryPrimitives.ReadInt16LittleEndian(source[offset..]) / 32768f;
    }
}
