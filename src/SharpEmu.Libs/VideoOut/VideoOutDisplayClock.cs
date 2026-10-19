// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// Callers hold the port-state lock when they advance or read the clock.
// A refresh rate below 1000 is in hertz; from 1000 up it is in millihertz, so
// 59940 and 119880 express the 59.94 Hz and 119.88 Hz display modes.
internal sealed class VideoOutDisplayClock(long openedAt, ulong processCounter, ulong timestampCounter, ulong timestampFrequency)
{
    private const int FlipModeAsap = 2;
    private const int FlipModeWindow = 3;
    internal const int FlipModeVsyncMultiple = 4;
    private const uint MillihertzThreshold = 1000;

    // Vblanks are counted from the anchor: the open, or the last vblank before
    // the refresh rate changed. Re-anchoring keeps Count monotonic and the next
    // vblank one new interval after the last one.
    private long _anchorTimestamp = openedAt;
    private ulong _anchorCount;
    private uint _anchorRate;

    public ulong Count { get; private set; }
    public long LastTimestamp { get; private set; }
    public ulong ProcessCounter { get; private set; }
    public ulong TimestampCounter { get; private set; }
    // The process-time counter runs at the fixed libkernel frequency, not at
    // the host Stopwatch rate.
    public ulong ProcessMicroseconds => (ulong)((UInt128)ProcessCounter * 1_000_000 /
        Kernel.KernelRuntimeCompatExports.ProcessTimeCounterFrequency);

    /// <summary>The origin of the current vblank grid, for flip pacing.</summary>
    public long AnchorTimestamp => _anchorTimestamp;

    public void Advance(long timestamp, uint refreshRate)
    {
        Reanchor(refreshRate);
        var interval = RefreshInterval(refreshRate);
        var count = _anchorCount + (ulong)(Math.Max(0, timestamp - _anchorTimestamp) / interval);
        if (count <= Count) return;
        Count = count;
        LastTimestamp = _anchorTimestamp + checked((long)(count - _anchorCount) * interval);
        var elapsed = (ulong)Math.Max(0, LastTimestamp - openedAt);
        ProcessCounter = processCounter + (ulong)((UInt128)elapsed *
            Kernel.KernelRuntimeCompatExports.ProcessTimeCounterFrequency / (ulong)Stopwatch.Frequency);
        TimestampCounter = timestampCounter + (ulong)((UInt128)elapsed * timestampFrequency / (ulong)Stopwatch.Frequency);
    }

    public long NextTimestamp(uint refreshRate)
    {
        Reanchor(refreshRate);
        return _anchorTimestamp + checked((long)(Count - _anchorCount + 1) * RefreshInterval(refreshRate));
    }

    private void Reanchor(uint refreshRate)
    {
        if (_anchorRate == refreshRate)
        {
            return;
        }

        if (_anchorRate != 0 && Count > _anchorCount)
        {
            _anchorTimestamp = LastTimestamp;
            _anchorCount = Count;
        }

        _anchorRate = refreshRate;
    }

    internal static long RefreshInterval(uint refreshRate) =>
        refreshRate >= MillihertzThreshold
            ? Math.Max(1, (long)((UInt128)(ulong)Stopwatch.Frequency * 1000 / refreshRate))
            : Math.Max(1, Stopwatch.Frequency / Math.Max(1L, refreshRate));

    /// <summary>The refresh rate rounded to whole hertz (59.94 Hz is 60).</summary>
    internal static uint NominalHz(uint refreshRate) =>
        refreshRate >= MillihertzThreshold ? (refreshRate + 500) / 1000 : refreshRate;

    /// <summary>
    /// Parses a refresh rate setting: whole hertz ("60", "120") or a fractional
    /// rate ("59.94", "119.88"), returned in the clock's units.
    /// </summary>
    internal static bool TryParseRefreshRate(string? text, out uint refreshRate)
    {
        refreshRate = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (uint.TryParse(text, out var hertz))
        {
            refreshRate = hertz;
            return hertz is >= 30 and <= 999;
        }

        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fractional) &&
            fractional is >= 30 and < 1000)
        {
            refreshRate = (uint)Math.Round(fractional * 1000);
            return true;
        }

        return false;
    }

    internal static long NextFlipTimestamp(long openedAt, long lastPresentedAt, long timestamp,
        uint refreshRate, int flipRate, int flipMode, uint height, int windowTop, int windowBottom, long readyAt)
    {
        if (flipMode == FlipModeAsap) return timestamp;
        if (flipMode == FlipModeVsyncMultiple)
        {
            // All flips ready in one interval share its next boundary.
            // Keep readiness fixed so polling cannot move the deadline.
            var refreshInterval = RefreshInterval(refreshRate);
            var readyInterval = Math.Max(0, readyAt - openedAt) / refreshInterval;
            return checked(openedAt + (readyInterval + 1) * refreshInterval);
        }
        if (lastPresentedAt < 0 && flipMode != FlipModeWindow) return timestamp;
        var interval = RefreshInterval(refreshRate);
        var flipInterval = interval * Math.Max(1L, (long)flipRate + 1);
        var previousInterval = Math.Max(0, lastPresentedAt - openedAt) / flipInterval;
        var next = openedAt + (previousInterval + 1) * flipInterval;
        if (flipMode != FlipModeWindow || height == 0) return Math.Max(timestamp, next);

        // Window margins are scanline positions at the top and bottom of the image.
        var topEnd = interval * Math.Clamp((long)windowTop, 0, height) / height;
        var bottomStart = interval * Math.Clamp((long)windowBottom, 0, height) / height;
        if (topEnd == 0 && bottomStart == interval)
            return lastPresentedAt < 0 ? timestamp : Math.Max(timestamp, next);
        var windowLead = interval - bottomStart;
        var previousWindow = lastPresentedAt < 0
            ? -1
            : (Math.Max(0, lastPresentedAt - openedAt) + windowLead) / flipInterval;
        var windowStart = openedAt + (previousWindow + 1) * flipInterval - windowLead;
        if (timestamp < windowStart) return windowStart;
        var phase = Math.Max(0, timestamp - openedAt) % interval;
        return phase <= topEnd || phase >= bottomStart
            ? timestamp
            : timestamp - phase + bottomStart;
    }
}
