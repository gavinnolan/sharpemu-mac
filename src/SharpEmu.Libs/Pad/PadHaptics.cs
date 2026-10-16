// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// DualSense voice-coil haptics. On PS5 a title drives the two actuators by
/// writing a haptic waveform to a vibration port (sceAudioOut port type
/// VIBRATION, sceAudioOut2 port type 0x6): channel 0 is the left actuator,
/// channel 1 the right one, and a mono port drives both. The host pad only
/// exposes two rumble motors through <see cref="IHostInput.SetRumble"/>, so
/// each output chunk is reduced to one intensity per actuator and the left
/// actuator drives the large motor, the right one the small motor. The audio
/// itself never reaches the speakers.
/// </summary>
/// <remarks>
/// scePadSetVibration keeps working alongside: the host motors run at the
/// stronger of the compat vibration and the haptic envelope, so a title that
/// clears the compat vibration every frame does not cut its haptics.
/// </remarks>
public static class PadHaptics
{
    // Below this RMS a chunk is treated as silence (the actuators are idle),
    // so dither and DC-free noise floors do not keep the motors spinning.
    internal const double SilenceRms = 0.01;

    private static readonly object Gate = new();
    private static byte _vibrationLarge;
    private static byte _vibrationSmall;
    private static byte _hapticLeft;
    private static byte _hapticRight;
    private static byte _appliedLarge;
    private static byte _appliedSmall;
    private static IHostInput? _hostInputForTests;

    /// <summary>
    /// Reduces one chunk of interleaved haptic PCM to an intensity per
    /// actuator: the RMS of the actuator's channel, doubled and clamped to
    /// full scale, then scaled to 0..255. A chunk whose RMS is below
    /// <see cref="SilenceRms"/> yields 0 for that actuator.
    /// </summary>
    /// <param name="pcm">Interleaved samples, <paramref name="frames"/> × <paramref name="channels"/>.</param>
    /// <param name="frames">Frame count of the chunk.</param>
    /// <param name="channels">Interleaved channel count (1 drives both actuators).</param>
    /// <param name="bytesPerSample">2 (signed 16-bit) or 4 (float32).</param>
    /// <param name="isFloat">Whether samples are float32.</param>
    /// <param name="gain">Linear gain applied before the envelope (port volume).</param>
    public static (byte Left, byte Right) IntensityFromPcm(
        ReadOnlySpan<byte> pcm,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float gain)
    {
        if (frames <= 0 || channels <= 0 || bytesPerSample is not (2 or 4) ||
            (isFloat && bytesPerSample != 4) ||
            pcm.Length < (long)frames * channels * bytesPerSample ||
            !float.IsFinite(gain) || gain <= 0f)
        {
            return (0, 0);
        }

        var left = ChannelIntensity(pcm, frames, channels, bytesPerSample, isFloat, 0, gain);
        var right = channels == 1
            ? left
            : ChannelIntensity(pcm, frames, channels, bytesPerSample, isFloat, 1, gain);
        return (left, right);
    }

    private static byte ChannelIntensity(
        ReadOnlySpan<byte> pcm,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        int channel,
        float gain)
    {
        var energy = 0.0;
        var stride = channels * bytesPerSample;
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = pcm.Slice(frame * stride + channel * bytesPerSample, bytesPerSample);
            double value = isFloat
                ? BinaryPrimitives.ReadSingleLittleEndian(sample)
                : BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0;
            if (double.IsFinite(value))
            {
                value *= gain;
                energy += value * value;
            }
        }

        var rms = Math.Sqrt(energy / frames);
        return rms < SilenceRms
            ? (byte)0
            : (byte)Math.Round(Math.Min(1.0, rms * 2.0) * 255.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Sets the actuator intensities derived from the latest haptic chunk.
    /// The host motors are only updated when the combined level changes.
    /// </summary>
    public static void SubmitAudioHaptics(byte left, byte right)
    {
        lock (Gate)
        {
            _hapticLeft = left;
            _hapticRight = right;
            ApplyLocked(force: false);
        }
    }

    /// <summary>Stops the haptic contribution (vibration port closed).</summary>
    public static void ReleaseAudioHaptics() => SubmitAudioHaptics(0, 0);

    /// <summary>scePadSetVibration: always forwarded to the host.</summary>
    internal static void SetVibration(byte largeMotor, byte smallMotor)
    {
        lock (Gate)
        {
            _vibrationLarge = largeMotor;
            _vibrationSmall = smallMotor;
            ApplyLocked(force: true);
        }
    }

    private static void ApplyLocked(bool force)
    {
        var large = Math.Max(_vibrationLarge, _hapticLeft);
        var small = Math.Max(_vibrationSmall, _hapticRight);
        if (!force && large == _appliedLarge && small == _appliedSmall)
        {
            return;
        }

        _appliedLarge = large;
        _appliedSmall = small;
        (Volatile.Read(ref _hostInputForTests) ?? HostPlatform.Current.Input).SetRumble(large, small);
    }

    internal static void SetHostInputForTests(IHostInput? input) =>
        Volatile.Write(ref _hostInputForTests, input);

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _vibrationLarge = _vibrationSmall = 0;
            _hapticLeft = _hapticRight = 0;
            _appliedLarge = _appliedSmall = 0;
        }

        Volatile.Write(ref _hostInputForTests, null);
    }
}
