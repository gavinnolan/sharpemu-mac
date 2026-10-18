// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using SharpEmu.HLE.Host;
#if SHARPEMU_HOST_MACOS
using SharpEmu.Host.MacOS;
#endif
using SharpEmu.Libs.Tests.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

// The macOS host package is only referenced when the tests are built on macOS
// (see SharpEmu.Libs.Tests.csproj), so the whole file is macOS-only.
#if SHARPEMU_HOST_MACOS

/// <summary>
/// The device-independent half of the macOS CoreAudio backend: the channel fold,
/// the sample conversion and the ring buffer that hands frames to the render
/// callback. These run on every host because none of them touch CoreAudio.
/// </summary>
public class MacAudioChannelMapTests
{
    private const float Fold = 0.70710678f;

    [Fact]
    public void MatchingChannelCountsSkipTheMatrix()
    {
        Assert.Null(MacAudioChannelMap.Build(2, 2));
        Assert.Null(MacAudioChannelMap.Build(8, 8));
        Assert.True(MacAudioChannelMap.IsIdentity(MacAudioChannelMap.BuildMatrix(6, 6), 6, 6));
    }

    [Fact]
    public void MonoPlaysAtUnityInBothFronts()
    {
        Assert.Equal(new[] { 1.0f, 1.0f }, MacAudioChannelMap.Build(1, 2));
    }

    [Fact]
    public void MonoOnAMonoDeviceIsPassThrough()
    {
        Assert.Null(MacAudioChannelMap.Build(1, 1));
        Assert.Equal(new[] { 1.0f }, MacAudioChannelMap.BuildMatrix(1, 1));
    }

    [Fact]
    public void StereoFoldsToMonoAsAnAverage()
    {
        Assert.Equal(new[] { 0.5f, 0.5f }, MacAudioChannelMap.Build(2, 1));
    }

    [Fact]
    public void FiveOneFoldsToStereoWithNormalisedFronts()
    {
        var matrix = MacAudioChannelMap.Build(6, 2)!;

        // Front left, centre and back left all land in the left output; the row
        // is scaled by the sum of its gains so full-scale input cannot clip.
        var scale = 1.0f / (1.0f + Fold + Fold);
        Assert.Equal(scale, Gain(matrix, 0, 0, 2), 5);          // FL -> L
        Assert.Equal(0.0f, Gain(matrix, 0, 1, 2), 5);           // FL -> R
        Assert.Equal(scale, Gain(matrix, 1, 1, 2), 5);          // FR -> R
        Assert.Equal(Fold * scale, Gain(matrix, 2, 0, 2), 5);   // FC -> L
        Assert.Equal(Fold * scale, Gain(matrix, 2, 1, 2), 5);   // FC -> R
        Assert.Equal(0.0f, Gain(matrix, 3, 0, 2), 5);           // LFE dropped
        Assert.Equal(0.0f, Gain(matrix, 3, 1, 2), 5);
        Assert.Equal(Fold * scale, Gain(matrix, 4, 0, 2), 5);   // BL -> L
        Assert.Equal(Fold * scale, Gain(matrix, 5, 1, 2), 5);   // BR -> R
    }

    [Fact]
    public void SevenOneFoldsSidesIntoTheSurroundsForFiveOne()
    {
        var matrix = MacAudioChannelMap.Build(8, 6)!;

        // FL FR FC LFE pass through untouched.
        for (var channel = 0; channel < 4; channel++)
        {
            Assert.Equal(1.0f, Gain(matrix, channel, channel, 6), 5);
        }

        var scale = 1.0f / (1.0f + Fold);
        Assert.Equal(scale, Gain(matrix, 4, 4, 6), 5);          // BL -> BL
        Assert.Equal(Fold * scale, Gain(matrix, 6, 4, 6), 5);   // SL -> BL
        Assert.Equal(scale, Gain(matrix, 5, 5, 6), 5);          // BR -> BR
        Assert.Equal(Fold * scale, Gain(matrix, 7, 5, 6), 5);   // SR -> BR
    }

    [Fact]
    public void StereoOnAWiderDeviceLeavesTheOtherOutputsSilent()
    {
        var matrix = MacAudioChannelMap.Build(2, 6)!;
        Assert.Equal(1.0f, Gain(matrix, 0, 0, 6), 5);
        Assert.Equal(1.0f, Gain(matrix, 1, 1, 6), 5);
        for (var destination = 2; destination < 6; destination++)
        {
            Assert.Equal(0.0f, Gain(matrix, 0, destination, 6), 5);
            Assert.Equal(0.0f, Gain(matrix, 1, destination, 6), 5);
        }
    }

    [Fact]
    public void NoFoldCanEverClip()
    {
        for (var source = 1; source <= MacAudioChannelMap.MaxChannels; source++)
        {
            for (var destination = 1; destination <= 16; destination++)
            {
                var matrix = MacAudioChannelMap.BuildMatrix(source, destination);
                for (var output = 0; output < destination; output++)
                {
                    var sum = 0.0f;
                    for (var input = 0; input < source; input++)
                    {
                        sum += Math.Abs(Gain(matrix, input, output, destination));
                    }

                    Assert.True(
                        sum <= 1.0001f,
                        $"{source}ch -> {destination}ch output {output} sums to {sum}");
                }
            }
        }
    }

    [Fact]
    public void ChannelLabelsAreWavOrder()
    {
        // kAudioChannelLabel_ Left, Right, Center, LFEScreen, LeftSurround,
        // RightSurround, LeftSurroundDirect, RightSurroundDirect — the WAV 7.1
        // order the guest submits in.
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 10, 11 }, MacAudioChannelMap.ChannelLabels(8));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, MacAudioChannelMap.ChannelLabels(6));
        Assert.Equal(new uint[] { 1, 2 }, MacAudioChannelMap.ChannelLabels(2));
        Assert.Equal(new uint[] { 3 }, MacAudioChannelMap.ChannelLabels(1));

        // A device wider than 7.1 gets its extra outputs marked unused.
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 10, 11, 0, 0 }, MacAudioChannelMap.ChannelLabels(10));
    }

    [Fact]
    public void ChannelCountsOutsideTheSeamAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MacAudioChannelMap.Build(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MacAudioChannelMap.Build(9, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MacAudioChannelMap.Build(2, 0));
    }

    private static float Gain(float[] matrix, int source, int destination, int destinationChannels) =>
        matrix[(source * destinationChannels) + destination];
}

public class MacAudioSampleConverterTests
{
    [Fact]
    public void Signed16ScalesSymmetricallyIntoFullScale()
    {
        var source = new byte[4 * sizeof(short)];
        WriteInt16(source, 0, short.MinValue);
        WriteInt16(source, 1, 0);
        WriteInt16(source, 2, short.MaxValue);
        WriteInt16(source, 3, 16384);

        var destination = new float[4];
        var frames = MacAudioSampleConverter.Convert(
            source, destination, sourceChannels: 2, destinationChannels: 2,
            HostPcmFormat.Signed16, matrix: null);

        Assert.Equal(2, frames);
        Assert.Equal(-1.0f, destination[0], 6);
        Assert.Equal(0.0f, destination[1], 6);
        Assert.Equal(0.99997f, destination[2], 4);
        Assert.Equal(0.5f, destination[3], 6);
    }

    [Fact]
    public void Float32PassesThroughAndNaNBecomesSilence()
    {
        var source = new byte[2 * sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(source, 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(sizeof(float)), float.NaN);

        var destination = new float[2];
        var frames = MacAudioSampleConverter.Convert(
            source, destination, sourceChannels: 2, destinationChannels: 2,
            HostPcmFormat.Float32, matrix: null);

        Assert.Equal(1, frames);
        Assert.Equal(0.25f, destination[0], 6);
        Assert.Equal(0.0f, destination[1], 6);
    }

    [Fact]
    public void ConversionAppliesTheChannelMatrix()
    {
        // 5.1 float, one frame: only the centre channel carries signal, so both
        // stereo outputs get it at the matrix's normalised centre gain.
        var source = new byte[6 * sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(2 * sizeof(float)), 1.0f);

        var matrix = MacAudioChannelMap.Build(6, 2)!;
        var destination = new float[2];
        var frames = MacAudioSampleConverter.Convert(
            source, destination, sourceChannels: 6, destinationChannels: 2,
            HostPcmFormat.Float32, matrix);

        Assert.Equal(1, frames);
        Assert.Equal(matrix[(2 * 2) + 0], destination[0], 6);
        Assert.Equal(matrix[(2 * 2) + 1], destination[1], 6);
        Assert.True(destination[0] > 0.0f);
    }

    [Fact]
    public void TrailingPartialFramesAndShortDestinationsAreClamped()
    {
        // Five shorts is two and a half stereo frames; only whole frames convert.
        var source = new byte[5 * sizeof(short)];
        var destination = new float[8];
        Assert.Equal(
            2,
            MacAudioSampleConverter.Convert(
                source, destination, 2, 2, HostPcmFormat.Signed16, matrix: null));

        // A destination too small for the source caps the frame count instead of
        // overrunning.
        Assert.Equal(
            1,
            MacAudioSampleConverter.Convert(
                source, destination.AsSpan(0, 2), 2, 2, HostPcmFormat.Signed16, matrix: null));
    }

    [Fact]
    public void FrameSizesFollowTheGuestFormat()
    {
        Assert.Equal(4, MacAudioSampleConverter.SourceFrameSize(2, HostPcmFormat.Signed16));
        Assert.Equal(32, MacAudioSampleConverter.SourceFrameSize(8, HostPcmFormat.Float32));
    }

    [Fact]
    public void PassThroughRequiresMatchingChannelCounts()
    {
        Assert.Throws<ArgumentException>(() => MacAudioSampleConverter.Convert(
            new byte[8], new float[8], 2, 4, HostPcmFormat.Signed16, matrix: null));
    }

    private static void WriteInt16(byte[] destination, int index, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(index * sizeof(short)), value);
}

public class MacAudioRingBufferTests
{
    [Fact]
    public void QueueDepthTracksWritesAndReads()
    {
        using var ring = new MacAudioRingBuffer(channels: 2, capacityFrames: 8);
        Assert.Equal(0L, ring.QueuedFrames);
        Assert.Equal(8L, ring.FreeFrames);

        Assert.Equal(3, ring.Write(new float[6]));
        Assert.Equal(3L, ring.QueuedFrames);
        Assert.Equal(5L, ring.FreeFrames);

        var destination = new float[4];
        Assert.Equal(2, ring.Read(destination));
        Assert.Equal(1L, ring.QueuedFrames);
    }

    [Fact]
    public void FramesComeBackInOrderAcrossTheWrap()
    {
        using var ring = new MacAudioRingBuffer(channels: 1, capacityFrames: 4);
        Assert.Equal(3, ring.Write(new[] { 1f, 2f, 3f }));

        var first = new float[2];
        Assert.Equal(2, ring.Read(first));
        Assert.Equal(new[] { 1f, 2f }, first);

        // Writing four more wraps past the end of the storage.
        Assert.Equal(3, ring.Write(new[] { 4f, 5f, 6f, 7f }));
        var rest = new float[4];
        Assert.Equal(4, ring.Read(rest));
        Assert.Equal(new[] { 3f, 4f, 5f, 6f }, rest);
        Assert.Equal(0L, ring.QueuedFrames);
    }

    [Fact]
    public void AFullRingReportsAShortWrite()
    {
        using var ring = new MacAudioRingBuffer(channels: 2, capacityFrames: 4);
        Assert.Equal(4, ring.Write(new float[12]));
        Assert.Equal(0, ring.Write(new float[4]));
        Assert.Equal(4L, ring.QueuedFrames);
    }

    [Fact]
    public void AnEmptyRingReportsAShortRead()
    {
        using var ring = new MacAudioRingBuffer(channels: 2, capacityFrames: 4);
        Assert.Equal(0, ring.Read(new float[8]));

        ring.Write(new float[2]);
        Assert.Equal(1, ring.Read(new float[8]));
    }

    [Fact]
    public void WritingAfterDisposeIsHarmless()
    {
        var ring = new MacAudioRingBuffer(channels: 2, capacityFrames: 4);
        ring.Dispose();
        ring.Dispose();
        Assert.Equal(0, ring.Write(new float[4]));
        Assert.Equal(0, ring.Read(new float[4]));
    }
}

/// <summary>
/// The CoreAudio device tests. They skip when the host has no usable output
/// device; SHARPEMU_TEST_REQUIRE_DEVICE=1 turns that skip into a failure, the
/// same switch the GPU device tests use. Tones are 0.05 full scale and a few
/// hundred milliseconds long, so running the suite does not blast the speakers.
/// </summary>
[SupportedOSPlatform("macos")]
public class MacCoreAudioDeviceTests
{
    private const uint SampleRate = 48_000;
    private const int ToneMilliseconds = 300;
    private const float Amplitude = 0.05f;

    [Fact]
    public void BackendNameIdentifiesCoreAudio()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal("coreaudio", new MacCoreAudioOutput().BackendName);
    }

    [Fact]
    public void UnsupportedRatesAndChannelCountsAreRejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var audio = new MacCoreAudioOutput();
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.OpenStereoPcm16Stream(1_000));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => audio.OpenPcmStream(SampleRate, 9, HostPcmFormat.Signed16));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => audio.OpenPcmStream(SampleRate, 0, HostPcmFormat.Float32));
    }

    [Fact]
    public void LatencyCapCoversTheTargetQueueDepth()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var expected = (int)(SampleRate * 4 * MacCoreAudioOutput.TargetQueuedMilliseconds / 1_000);
        Assert.Equal(expected, MacCoreAudioOutput.LatencyCapBytes(SampleRate, 2, HostPcmFormat.Signed16));
    }

    [Fact]
    public void StereoSigned16TonePlaysAndDrains()
    {
        if (!TryOpen(audio => audio.OpenStereoPcm16Stream(SampleRate), out var stream))
        {
            return;
        }

        using (stream)
        {
            PlayTone(stream, channels: 2, HostPcmFormat.Signed16);
            AssertQueueDrains(stream);
        }

        Assert.False(stream.Submit(new byte[4]));
        Assert.Equal(-1, stream.QueuedMilliseconds);
    }

    [Fact]
    public void FiveOneFloat32TonePlaysAndDrains()
    {
        if (!TryOpen(
                audio => audio.OpenPcmStream(SampleRate, 6, HostPcmFormat.Float32),
                out var stream))
        {
            return;
        }

        using (stream)
        {
            PlayTone(stream, channels: 6, HostPcmFormat.Float32);
            AssertQueueDrains(stream);
        }
    }

    [Fact]
    public void MonoStreamOpensAndAcceptsAudio()
    {
        if (!TryOpen(
                audio => audio.OpenPcmStream(SampleRate, 1, HostPcmFormat.Signed16),
                out var stream))
        {
            return;
        }

        using (stream)
        {
            PlayTone(stream, channels: 1, HostPcmFormat.Signed16, milliseconds: 100);
            Assert.True(stream.QueuedMilliseconds >= 0);
        }
    }

    /// <summary>
    /// Opens a stream, or reports that this host cannot: no output device is a
    /// skip unless the required switch is set.
    /// </summary>
    private static bool TryOpen(
        Func<MacCoreAudioOutput, IHostAudioStream> open,
        out IHostAudioStream stream)
    {
        stream = null!;
        if (!OperatingSystem.IsMacOS())
        {
            Assert.False(
                GatePrerequisites.DeviceRequired,
                "The required gate cannot run without macOS.");
            return false;
        }

        try
        {
            stream = open(new MacCoreAudioOutput());
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
        {
            Assert.False(
                GatePrerequisites.DeviceRequired,
                $"The required gate cannot run without a CoreAudio output device: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Feeds a quiet sine in 10 ms grains, the way AudioOut does. Submit blocks
    /// on the device's queue, so this call takes roughly as long as the tone.
    /// </summary>
    private static void PlayTone(
        IHostAudioStream stream,
        int channels,
        HostPcmFormat format,
        int milliseconds = ToneMilliseconds)
    {
        const int grainFrames = (int)SampleRate / 100;
        var frameSize = MacAudioSampleConverter.SourceFrameSize(channels, format);
        var grain = new byte[grainFrames * frameSize];
        var phase = 0.0;
        var step = 2.0 * Math.PI * 440.0 / SampleRate;

        for (var grainIndex = 0; grainIndex < milliseconds / 10; grainIndex++)
        {
            for (var frame = 0; frame < grainFrames; frame++)
            {
                var sample = (float)(Math.Sin(phase) * Amplitude);
                phase += step;
                for (var channel = 0; channel < channels; channel++)
                {
                    var offset = (frame * frameSize) +
                                 (channel * MacAudioSampleConverter.BytesPerSample(format));
                    if (format == HostPcmFormat.Float32)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(grain.AsSpan(offset), sample);
                    }
                    else
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(
                            grain.AsSpan(offset),
                            (short)(sample * short.MaxValue));
                    }
                }
            }

            Assert.True(stream.Submit(grain), $"grain {grainIndex} was rejected");
        }
    }

    /// <summary>
    /// The queue has to be both non-empty right after the tone (the device is
    /// holding a cushion) and shallower a moment later (the device is consuming
    /// it). A stream that never drains would hold the first value forever.
    /// </summary>
    private static void AssertQueueDrains(IHostAudioStream stream)
    {
        var queued = stream.QueuedMilliseconds;
        Assert.True(queued > 0, $"the device queue should hold a cushion, saw {queued} ms");

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        var drained = false;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(20);
            if (stream.QueuedMilliseconds < queued)
            {
                drained = true;
                break;
            }
        }

        Assert.True(drained, $"the device never consumed the queued {queued} ms");
    }
}
#endif
