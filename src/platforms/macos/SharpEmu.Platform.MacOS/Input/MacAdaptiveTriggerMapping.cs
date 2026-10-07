// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.Host;

namespace SharpEmu.Platform.MacOS.Input;

/// <summary>The GCDualSenseAdaptiveTrigger setter a decoded effect maps onto.</summary>
internal enum MacAdaptiveTriggerMode
{
    /// <summary>-setModeOff.</summary>
    Off,

    /// <summary>-setModeFeedbackWithStartPosition:resistiveStrength: or
    /// -setModeFeedbackWithResistiveStrengths:.</summary>
    Feedback,

    /// <summary>-setModeWeaponWithStartPosition:endPosition:resistiveStrength:.</summary>
    Weapon,

    /// <summary>-setModeVibrationWithStartPosition:amplitude:frequency: or
    /// -setModeVibrationWithAmplitudes:frequency:.</summary>
    Vibration,
}

/// <summary>
/// A GameController-shaped adaptive-trigger command. All scalars are normalized
/// 0..1 the way GCDualSenseAdaptiveTrigger expects. <see cref="Positions"/> holds
/// the per-zone strengths (feedback) or amplitudes (vibration) for the positional
/// setters, and is empty when the payload carried only a single zone boundary.
/// </summary>
internal readonly record struct MacAdaptiveTriggerCommand(
    MacAdaptiveTriggerMode Mode,
    float StartPosition,
    float EndPosition,
    float Strength,
    float Frequency,
    float[] Positions)
{
    public static readonly MacAdaptiveTriggerCommand Off = new(
        MacAdaptiveTriggerMode.Off, 0f, 0f, 0f, 0f, []);

    public bool HasPositions => Positions.Length == MacAdaptiveTriggerMapping.ZoneCount;
}

/// <summary>
/// Translates the 11-byte DualSense adaptive-trigger command the HLE pad exports
/// produce (<see cref="HostAdaptiveTriggerEffect"/>, which the SDL backend sent
/// verbatim through SDL_SendGamepadEffect) into GCDualSenseAdaptiveTrigger calls.
/// </summary>
/// <remarks>
/// The payloads <c>PadExports.DecodeTriggerEffect</c> emits are the "zoned"
/// DualSense modes:
/// <list type="bullet">
/// <item>0x21 — multiple-position feedback: u16 active-zone bitmap, then ten
/// 3-bit strengths packed into a u32.</item>
/// <item>0x25 — weapon: a bitmap with exactly the start and end zone bits set,
/// plus strength-1.</item>
/// <item>0x26 — multiple-position vibration: zone bitmap, packed 3-bit
/// amplitudes and a frequency byte.</item>
/// <item>0x05 (and 0x00) — off.</item>
/// </list>
/// The three single-boundary modes from the DualSense HID vocabulary (0x01
/// feedback, 0x02 weapon, 0x06 vibration) are accepted too so the mapper covers
/// payloads that did not come from the HLE encoder.
/// Zone indices are normalized against the nine trigger-travel steps the
/// DualSense reports, strengths against the 1..8 scale, and the vibration
/// frequency byte against its full 0..255 range.
/// Pure logic — no GameController calls — so it is unit tested directly.
/// </remarks>
internal static class MacAdaptiveTriggerMapping
{
    public const int ZoneCount = 10;

    private const int MaxZoneIndex = ZoneCount - 1;
    private const float MaxStrength = 8f;
    private const float MaxFrequency = 255f;

    public static MacAdaptiveTriggerCommand Map(HostAdaptiveTriggerEffect effect)
    {
        Span<byte> payload = stackalloc byte[11];
        effect.CopyTo(payload);
        return Map(payload);
    }

    public static MacAdaptiveTriggerCommand Map(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 11)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        return payload[0] switch
        {
            0x01 => MapSimpleFeedback(payload),
            0x02 => MapSimpleWeapon(payload),
            0x06 => MapSimpleVibration(payload),
            0x21 => MapZonedFeedback(payload),
            0x25 => MapWeapon(payload),
            0x26 => MapZonedVibration(payload),
            _ => MacAdaptiveTriggerCommand.Off,
        };
    }

    private static MacAdaptiveTriggerCommand MapZonedFeedback(ReadOnlySpan<byte> payload)
    {
        var zones = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(payload[3..]);
        if (zones == 0)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        var strengths = UnpackZoneStrengths(zones, packed, out var firstZone, out var peak);
        return new MacAdaptiveTriggerCommand(
            MacAdaptiveTriggerMode.Feedback,
            NormalizeZone(firstZone),
            1f,
            peak,
            0f,
            strengths);
    }

    private static MacAdaptiveTriggerCommand MapZonedVibration(ReadOnlySpan<byte> payload)
    {
        var zones = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(payload[3..]);
        var frequency = payload[9];
        if (zones == 0 || frequency == 0)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        var amplitudes = UnpackZoneStrengths(zones, packed, out var firstZone, out var peak);
        return new MacAdaptiveTriggerCommand(
            MacAdaptiveTriggerMode.Vibration,
            NormalizeZone(firstZone),
            1f,
            peak,
            frequency / MaxFrequency,
            amplitudes);
    }

    private static MacAdaptiveTriggerCommand MapWeapon(ReadOnlySpan<byte> payload)
    {
        var zones = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        if (zones == 0)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        // The encoder sets exactly two bits: the lowest is the start of the
        // resistance band and the highest is where it releases.
        var start = MaxZoneIndex;
        var end = 0;
        for (var zone = 0; zone < ZoneCount; zone++)
        {
            if ((zones & (1 << zone)) == 0)
            {
                continue;
            }

            start = Math.Min(start, zone);
            end = Math.Max(end, zone);
        }

        if (end <= start)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        // Stored as strength-1, matching the 1..8 scale the guest supplies.
        var strength = Math.Min(payload[3] + 1, (int)MaxStrength);
        return new MacAdaptiveTriggerCommand(
            MacAdaptiveTriggerMode.Weapon,
            NormalizeZone(start),
            NormalizeZone(end),
            strength / MaxStrength,
            0f,
            []);
    }

    private static MacAdaptiveTriggerCommand MapSimpleFeedback(ReadOnlySpan<byte> payload)
    {
        if (payload[1] > MaxZoneIndex || payload[2] is 0 or > 8)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        return new MacAdaptiveTriggerCommand(
            MacAdaptiveTriggerMode.Feedback,
            NormalizeZone(payload[1]),
            1f,
            payload[2] / MaxStrength,
            0f,
            []);
    }

    private static MacAdaptiveTriggerCommand MapSimpleWeapon(ReadOnlySpan<byte> payload)
    {
        if (payload[1] > MaxZoneIndex || payload[2] <= payload[1] || payload[2] > MaxZoneIndex ||
            payload[3] is 0 or > 8)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        return new MacAdaptiveTriggerCommand(
            MacAdaptiveTriggerMode.Weapon,
            NormalizeZone(payload[1]),
            NormalizeZone(payload[2]),
            payload[3] / MaxStrength,
            0f,
            []);
    }

    private static MacAdaptiveTriggerCommand MapSimpleVibration(ReadOnlySpan<byte> payload)
    {
        if (payload[1] > MaxZoneIndex || payload[2] is 0 or > 8 || payload[3] == 0)
        {
            return MacAdaptiveTriggerCommand.Off;
        }

        return new MacAdaptiveTriggerCommand(
            MacAdaptiveTriggerMode.Vibration,
            NormalizeZone(payload[1]),
            1f,
            payload[2] / MaxStrength,
            payload[3] / MaxFrequency,
            []);
    }

    /// <summary>
    /// Expands the zone bitmap plus packed 3-bit (strength-1) values into ten
    /// normalized per-zone levels, also reporting the lowest active zone and the
    /// peak level for the single-boundary GC setters.
    /// </summary>
    private static float[] UnpackZoneStrengths(
        ushort zones,
        uint packed,
        out int firstZone,
        out float peak)
    {
        var levels = new float[ZoneCount];
        firstZone = 0;
        peak = 0f;
        var seenFirst = false;
        for (var zone = 0; zone < ZoneCount; zone++)
        {
            if ((zones & (1 << zone)) == 0)
            {
                continue;
            }

            var level = (((packed >> (zone * 3)) & 0x7) + 1) / MaxStrength;
            levels[zone] = level;
            peak = Math.Max(peak, level);
            if (!seenFirst)
            {
                seenFirst = true;
                firstZone = zone;
            }
        }

        return levels;
    }

    private static float NormalizeZone(int zone) =>
        Math.Clamp(zone, 0, MaxZoneIndex) / (float)MaxZoneIndex;
}
