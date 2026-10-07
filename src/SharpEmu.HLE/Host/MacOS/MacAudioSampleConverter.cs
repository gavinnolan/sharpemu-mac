// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.HLE.Host.MacOS;

/// <summary>
/// Turns one guest submission — interleaved signed-16 or float32, 1 to 8
/// channels, WAV order — into the interleaved float32 frames the CoreAudio
/// render callback copies out verbatim.
///
/// This runs on the guest's submitting thread, never on the render thread: the
/// render callback has to stay a memcpy, so every conversion and every channel
/// fold happens here first.
/// </summary>
internal static class MacAudioSampleConverter
{
    /// <summary>
    /// Scale for signed-16 to float. 1/32768 keeps the mapping symmetric and
    /// makes -32768 exactly -1.0, so no input can overshoot full scale.
    /// </summary>
    public const float Int16Scale = 1.0f / 32768.0f;

    public static int BytesPerSample(HostPcmFormat format) =>
        format == HostPcmFormat.Float32 ? sizeof(float) : sizeof(short);

    public static int SourceFrameSize(int channels, HostPcmFormat format) =>
        checked(channels * BytesPerSample(format));

    /// <summary>
    /// Float samples needed to hold <paramref name="frames"/> frames at
    /// <paramref name="destinationChannels"/> channels per frame.
    /// </summary>
    public static int RequiredSamples(int frames, int destinationChannels) =>
        checked(frames * destinationChannels);

    /// <summary>
    /// Converts whole frames from <paramref name="source"/> into
    /// <paramref name="destination"/> and returns the frame count. A null
    /// <paramref name="matrix"/> means channel-for-channel pass-through, which
    /// requires matching channel counts. A trailing partial frame is ignored —
    /// the device can only play whole frames.
    /// </summary>
    public static int Convert(
        ReadOnlySpan<byte> source,
        Span<float> destination,
        int sourceChannels,
        int destinationChannels,
        HostPcmFormat format,
        float[]? matrix)
    {
        if (sourceChannels < 1 || destinationChannels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        }

        if (matrix is null && sourceChannels != destinationChannels)
        {
            throw new ArgumentException(
                "Pass-through conversion needs matching channel counts.",
                nameof(matrix));
        }

        if (matrix is not null && matrix.Length < sourceChannels * destinationChannels)
        {
            throw new ArgumentException("The gain matrix is too small.", nameof(matrix));
        }

        var sourceFrameSize = SourceFrameSize(sourceChannels, format);
        var frames = Math.Min(
            source.Length / sourceFrameSize,
            destination.Length / destinationChannels);
        if (frames <= 0)
        {
            return 0;
        }

        var bytesPerSample = BytesPerSample(format);
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceFrame = source.Slice(frame * sourceFrameSize, sourceFrameSize);
            var destinationFrame = destination.Slice(frame * destinationChannels, destinationChannels);
            if (matrix is null)
            {
                for (var channel = 0; channel < sourceChannels; channel++)
                {
                    destinationFrame[channel] = ReadSample(sourceFrame, channel, bytesPerSample, format);
                }

                continue;
            }

            destinationFrame.Clear();
            for (var channel = 0; channel < sourceChannels; channel++)
            {
                var sample = ReadSample(sourceFrame, channel, bytesPerSample, format);
                if (sample == 0.0f)
                {
                    continue;
                }

                var row = channel * destinationChannels;
                for (var output = 0; output < destinationChannels; output++)
                {
                    var gain = matrix[row + output];
                    if (gain != 0.0f)
                    {
                        destinationFrame[output] += sample * gain;
                    }
                }
            }
        }

        return frames;
    }

    private static float ReadSample(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        HostPcmFormat format)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (format != HostPcmFormat.Float32)
        {
            return BinaryPrimitives.ReadInt16LittleEndian(sample) * Int16Scale;
        }

        // A guest NaN would propagate into the device buffer and can make a
        // CoreAudio mixer emit a click, so it is flattened to silence here.
        var value = BinaryPrimitives.ReadSingleLittleEndian(sample);
        return float.IsNaN(value) ? 0.0f : value;
    }
}
