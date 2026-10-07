// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Platform.MacOS;
using Xunit;

namespace SharpEmu.Libs.Tests.Platform;

/// <summary>
/// The adaptive-trigger payload the HLE pad exports build is the DualSense HID
/// blob the SDL backend forwarded verbatim; on macOS it has to be re-expressed as
/// GCDualSenseAdaptiveTrigger calls without losing the zone layout.
/// </summary>
public sealed class MacAdaptiveTriggerMappingTests
{
    /// <summary>Builds a 0x21/0x26-style zoned payload the way PadExports does.</summary>
    private static byte[] ZonedPayload(byte mode, ReadOnlySpan<byte> strengths, byte frequency)
    {
        var payload = new byte[11];
        ushort zones = 0;
        uint packed = 0;
        for (var index = 0; index < strengths.Length; index++)
        {
            if (strengths[index] is 0 or > 8)
            {
                continue;
            }

            zones |= (ushort)(1 << index);
            packed |= (uint)(strengths[index] - 1) << (index * 3);
        }

        payload[0] = mode;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), zones);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(3), packed);
        payload[9] = frequency;
        return payload;
    }

    [Theory]
    [InlineData(0x05)] // what PadExports emits for "no effect" and for invalid input
    [InlineData(0x00)]
    [InlineData(0xFC)]
    public void UnknownAndOffModesTurnTheTriggerOff(byte mode)
    {
        var payload = new byte[11];
        payload[0] = mode;
        Assert.Equal(MacAdaptiveTriggerMode.Off, MacAdaptiveTriggerMapping.Map(payload).Mode);
    }

    [Fact]
    public void ZonedFeedbackKeepsPerZoneResistance()
    {
        // Strength 8 from zone 3 onward: what EncodeFeedback(position: 3, strength: 8) produces.
        byte[] strengths = [0, 0, 0, 8, 8, 8, 8, 8, 8, 8];
        var command = MacAdaptiveTriggerMapping.Map(ZonedPayload(0x21, strengths, 0));

        Assert.Equal(MacAdaptiveTriggerMode.Feedback, command.Mode);
        Assert.True(command.HasPositions);
        Assert.Equal(3f / 9f, command.StartPosition, 5);
        Assert.Equal(1f, command.Strength, 5);
        Assert.Equal([0f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f, 1f], command.Positions);
    }

    [Fact]
    public void ZonedFeedbackNormalizesTheOneToEightStrengthScale()
    {
        byte[] strengths = [0, 2, 4, 0, 0, 0, 0, 0, 0, 0];
        var command = MacAdaptiveTriggerMapping.Map(ZonedPayload(0x21, strengths, 0));

        Assert.Equal(1f / 9f, command.StartPosition, 5);
        Assert.Equal(0.25f, command.Positions[1], 5);
        Assert.Equal(0.5f, command.Positions[2], 5);
        // The single-boundary GC setter gets the peak of the curve.
        Assert.Equal(0.5f, command.Strength, 5);
    }

    [Fact]
    public void ZonedVibrationCarriesAmplitudesAndFrequency()
    {
        byte[] amplitudes = [0, 0, 6, 6, 0, 0, 0, 0, 0, 0];
        var command = MacAdaptiveTriggerMapping.Map(ZonedPayload(0x26, amplitudes, 255));

        Assert.Equal(MacAdaptiveTriggerMode.Vibration, command.Mode);
        Assert.True(command.HasPositions);
        Assert.Equal(2f / 9f, command.StartPosition, 5);
        Assert.Equal(0.75f, command.Strength, 5);
        Assert.Equal(1f, command.Frequency, 5);
        Assert.Equal(0.75f, command.Positions[3], 5);
    }

    /// <summary>
    /// PadExports refuses to build a 0x26 with frequency 0, and so does the
    /// mapper: GameController would otherwise be asked for a 0 Hz vibration.
    /// </summary>
    [Fact]
    public void ZonedVibrationWithoutFrequencyTurnsTheTriggerOff()
    {
        byte[] amplitudes = [0, 0, 6, 6, 0, 0, 0, 0, 0, 0];
        Assert.Equal(
            MacAdaptiveTriggerMode.Off,
            MacAdaptiveTriggerMapping.Map(ZonedPayload(0x26, amplitudes, 0)).Mode);
    }

    [Fact]
    public void WeaponUsesTheStartAndEndZoneBits()
    {
        // EncodeWeapon(start: 2, end: 7, strength: 5) sets bits 2 and 7 and stores strength-1.
        var payload = new byte[11];
        payload[0] = 0x25;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (1 << 2) | (1 << 7));
        payload[3] = 4;

        var command = MacAdaptiveTriggerMapping.Map(payload);
        Assert.Equal(MacAdaptiveTriggerMode.Weapon, command.Mode);
        Assert.Equal(2f / 9f, command.StartPosition, 5);
        Assert.Equal(7f / 9f, command.EndPosition, 5);
        Assert.Equal(5f / 8f, command.Strength, 5);
        Assert.False(command.HasPositions);
    }

    [Fact]
    public void WeaponWithASingleZoneTurnsTheTriggerOff()
    {
        var payload = new byte[11];
        payload[0] = 0x25;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), 1 << 4);
        Assert.Equal(MacAdaptiveTriggerMode.Off, MacAdaptiveTriggerMapping.Map(payload).Mode);
    }

    [Fact]
    public void EmptyZoneMaskTurnsTheTriggerOff()
    {
        Assert.Equal(
            MacAdaptiveTriggerMode.Off,
            MacAdaptiveTriggerMapping.Map(ZonedPayload(0x21, new byte[10], 0)).Mode);
    }

    [Theory]
    [InlineData(0x01, 3, 8, 0, MacAdaptiveTriggerMode.Feedback)]
    [InlineData(0x02, 2, 7, 5, MacAdaptiveTriggerMode.Weapon)]
    [InlineData(0x06, 4, 8, 60, MacAdaptiveTriggerMode.Vibration)]
    internal void SingleBoundaryDualSenseModesAreAlsoAccepted(
        byte mode,
        byte first,
        byte second,
        byte third,
        MacAdaptiveTriggerMode expected)
    {
        var payload = new byte[11];
        payload[0] = mode;
        payload[1] = first;
        payload[2] = second;
        payload[3] = third;

        var command = MacAdaptiveTriggerMapping.Map(payload);
        Assert.Equal(expected, command.Mode);
        // These payloads carry no zone curve, so the positional setters are skipped.
        Assert.False(command.HasPositions);
    }

    [Fact]
    public void MappingAcceptsTheHostEffectRecordItself()
    {
        var effect = HostAdaptiveTriggerEffect.FromBytes(
            ZonedPayload(0x21, [0, 0, 0, 8, 8, 8, 8, 8, 8, 8], 0));
        Assert.Equal(MacAdaptiveTriggerMode.Feedback, MacAdaptiveTriggerMapping.Map(effect).Mode);
    }

    [Fact]
    public void ShortPayloadsAreRejectedRatherThanRead()
    {
        Assert.Equal(MacAdaptiveTriggerMode.Off, MacAdaptiveTriggerMapping.Map(new byte[4]).Mode);
    }
}
