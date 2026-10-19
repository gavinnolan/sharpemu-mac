// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Media;
using Xunit;

namespace SharpEmu.Libs.Tests.Media;

public sealed class MediaFramePlaybackTests
{
    [Fact]
    public void FramesAdvanceAccordingToMovieClock()
    {
        using var playback = new MediaFramePlayback(new SequenceDecoder(1, 2, 3));

        Assert.Equal(1, WaitForAdvancedFrame(playback)[0]);
        Assert.True(playback.TryGetFrame(true, out var heldFrame, out var advanced));
        var current = heldFrame[0];
        Assert.True(advanced ? current > 1 : current == 1);

        // A loaded machine can poll after the clock has passed a frame, which playback then drops;
        // what must hold is that frames only move forward and the last one is reached.
        while (current < 3)
        {
            var next = WaitForAdvancedFrame(playback)[0];
            Assert.True(next > current);
            current = next;
        }

        Assert.Equal(3, current);
    }

    private static byte[] WaitForAdvancedFrame(MediaFramePlayback playback)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (playback.TryGetFrame(true, out var frame, out var advanced) && advanced)
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The decoder did not produce a frame.");
    }

    [Fact]
    public void FirstFrameWaitsUntilPresentationStarts()
    {
        using var playback = new MediaFramePlayback(new SequenceDecoder(1, 2));

        var first = WaitForFrame(playback, advanceClock: false);
        Assert.Equal(1, first[0]);
        Thread.Sleep(100);

        Assert.True(playback.TryGetFrame(false, out var held, out var advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);

        Assert.True(playback.TryGetFrame(true, out held, out advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);
        Assert.Equal(2, WaitForAdvancedFrame(playback)[0]);
    }

    [Fact]
    public void PauseHoldsTheCurrentFrameWithoutClockCatchUp()
    {
        // Frames past 2 so a loaded runner whose clock skips one still sees playback resume.
        using var playback = new MediaFramePlayback(new SequenceDecoder(1, 2, 3, 4));

        Assert.Equal(1, WaitForAdvancedFrame(playback)[0]);
        playback.Pause();
        Thread.Sleep(700);

        Assert.False(playback.TryGetFrame(true, out _, out _));

        playback.Resume();
        Assert.True(playback.TryGetFrame(true, out var held, out var advanced));
        Assert.False(advanced);
        Assert.Equal(1, held[0]);
        Assert.True(WaitForAdvancedFrame(playback)[0] >= 2);
    }

    [Fact]
    public void GuestFrameIndexLimitsClientControlledPresentation()
    {
        using var playback = new MediaFramePlayback(new SequenceDecoder(1, 2, 3));

        Assert.Equal(1, WaitForGuestFrame(playback, 0)[0]);
        Thread.Sleep(700);

        Assert.True(playback.TryGetFrameAtOrBeforeIndex(
            0,
            out var held,
            out var heldIndex,
            out var advanced));
        Assert.False(advanced);
        Assert.Equal(0, heldIndex);
        Assert.Equal(1, held[0]);

        Assert.Equal(2, WaitForGuestFrame(playback, 1)[0]);
        Assert.Equal(3, WaitForGuestFrame(playback, 2)[0]);
    }

    private static byte[] WaitForFrame(
        MediaFramePlayback playback,
        bool advanceClock)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (playback.TryGetFrame(advanceClock, out var frame, out _))
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The decoder did not produce a frame.");
    }

    private static byte[] WaitForGuestFrame(
        MediaFramePlayback playback,
        long targetFrameIndex)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (playback.TryGetFrameAtOrBeforeIndex(
                    targetFrameIndex,
                    out var frame,
                    out var frameIndex,
                    out _) &&
                frameIndex == targetFrameIndex)
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The decoder did not produce the requested guest frame.");
    }

    private sealed class SequenceDecoder(params byte[] values) : IMediaFrameDecoder
    {
        private int _index;

        public uint Width => 1;

        public uint Height => 1;

        // Keep frame boundaries far enough apart that a loaded CI runner cannot
        // skip an expected frame between polling iterations.
        public uint FramesPerSecondNumerator => 2;

        public uint FramesPerSecondDenominator => 1;

        public bool TryDecodeNextFrame(Span<byte> destination)
        {
            if (_index >= values.Length)
            {
                return false;
            }

            destination.Fill(values[_index++]);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
