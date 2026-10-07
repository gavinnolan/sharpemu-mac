// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.MacOS;

/// <summary>
/// One speaker position in an interleaved PCM frame.
/// </summary>
internal enum MacAudioChannelRole
{
    FrontLeft,
    FrontRight,
    FrontCenter,
    LowFrequency,
    BackLeft,
    BackRight,
    SideLeft,
    SideRight,
    BackCenter,
}

/// <summary>
/// kAudioChannelLabel_* values. The WAVE equivalents in the comments are the
/// ones CoreAudioBaseTypes.h documents ("the integer values for the labels
/// match the bit position of the label in the WAVE file bitmap"), and they are
/// what makes the guest's WAV-order interleave expressible: a guest 7.1
/// submission is exactly Left, Right, Center, LfeScreen, LeftSurround,
/// RightSurround, LeftSurroundDirect, RightSurroundDirect. The
/// kAudioChannelLayoutTag_AudioUnit_7_1 / MPEG_7_1_C tags are deliberately not
/// used: their order is L R C LFE Ls Rs Rls Rrs, which is not WAV order.
/// </summary>
internal enum MacAudioChannelLabel : uint
{
    Unused = 0,
    Left = 1,
    Right = 2,
    Center = 3,
    LfeScreen = 4,
    LeftSurround = 5,         // WAVE: back left
    RightSurround = 6,        // WAVE: back right
    CenterSurround = 9,       // WAVE: back center
    LeftSurroundDirect = 10,  // WAVE: side left
    RightSurroundDirect = 11, // WAVE: side right
}

/// <summary>
/// Maps the guest's interleaved channel order onto the host device's.
///
/// The guest hands AudioOut submissions over in WAV order — 7.1 is
/// FL FR FC LFE BL BR SL SR — and the SDL backend passes that straight to
/// SDL_AudioStream, whose channel order is the same WAV order. This backend
/// therefore reads submissions as WAV order too, and the only question left is
/// what the device wants.
///
/// CoreAudio documents no downmix for a client format with more channels than
/// the output device, so the fold is done here instead: a gain matrix from guest
/// channels to device channels, built once per stream, with each output row
/// normalised so its coefficients sum to at most one. Normalising is what keeps
/// a 5.1 fold onto a stereo Mac from clipping, and it is what SDL's conversion
/// matrices do as well, so the two backends land at a comparable level.
/// </summary>
internal static class MacAudioChannelMap
{
    /// <summary>Mix level for a channel folded into a neighbouring speaker (-3 dB).</summary>
    private const float FoldGain = 0.70710678f;

    /// <summary>The widest interleave the host seam accepts.</summary>
    public const int MaxChannels = 8;

    /// <summary>
    /// WAV channel order per channel count. Only 1, 2, 6 and 8 occur in practice
    /// (AudioOut formats are mono, stereo and 7.1); the rest follow the same
    /// WAVE_FORMAT_EXTENSIBLE convention so an odd count cannot land on garbage.
    /// </summary>
    public static MacAudioChannelRole[] Layout(int channels) => channels switch
    {
        < 1 or > MaxChannels => throw new ArgumentOutOfRangeException(nameof(channels)),
        1 => [MacAudioChannelRole.FrontCenter],
        2 => [MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight],
        3 =>
        [
            MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight,
            MacAudioChannelRole.FrontCenter,
        ],
        4 =>
        [
            MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight,
            MacAudioChannelRole.BackLeft, MacAudioChannelRole.BackRight,
        ],
        5 =>
        [
            MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight,
            MacAudioChannelRole.FrontCenter,
            MacAudioChannelRole.BackLeft, MacAudioChannelRole.BackRight,
        ],
        6 =>
        [
            MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight,
            MacAudioChannelRole.FrontCenter, MacAudioChannelRole.LowFrequency,
            MacAudioChannelRole.BackLeft, MacAudioChannelRole.BackRight,
        ],
        7 =>
        [
            MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight,
            MacAudioChannelRole.FrontCenter, MacAudioChannelRole.LowFrequency,
            MacAudioChannelRole.BackCenter,
            MacAudioChannelRole.SideLeft, MacAudioChannelRole.SideRight,
        ],
        _ =>
        [
            MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight,
            MacAudioChannelRole.FrontCenter, MacAudioChannelRole.LowFrequency,
            MacAudioChannelRole.BackLeft, MacAudioChannelRole.BackRight,
            MacAudioChannelRole.SideLeft, MacAudioChannelRole.SideRight,
        ],
    };

    /// <summary>
    /// kAudioChannelLabel_* values describing <paramref name="channels"/> in the
    /// WAV order this backend renders, for the stream's AudioChannelLayout.
    /// Outputs past 7.1 on a wider device are labelled unused.
    /// </summary>
    public static uint[] ChannelLabels(int channels)
    {
        if (channels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        var roles = Layout(Math.Min(channels, MaxChannels));
        var labels = new uint[channels];
        for (var index = 0; index < channels; index++)
        {
            labels[index] = index < roles.Length
                ? (uint)LabelOf(roles[index])
                : (uint)MacAudioChannelLabel.Unused;
        }

        return labels;
    }

    /// <summary>
    /// Gain matrix from <paramref name="sourceChannels"/> guest channels to
    /// <paramref name="destinationChannels"/> device channels, row-major as
    /// [source * destinationChannels + destination]. Null means a plain copy:
    /// the channel counts match and every channel passes through at unity, which
    /// is the stereo-device case every title hits.
    /// </summary>
    public static float[]? Build(int sourceChannels, int destinationChannels) =>
        sourceChannels == destinationChannels
            ? Validate(sourceChannels, destinationChannels)
            : BuildMatrix(sourceChannels, destinationChannels);

    /// <summary>
    /// The matrix <see cref="Build"/> elides for matching channel counts. Kept
    /// reachable so tests can assert the identity case is in fact identity.
    /// </summary>
    public static float[] BuildMatrix(int sourceChannels, int destinationChannels)
    {
        _ = Validate(sourceChannels, destinationChannels);
        var matrix = new float[sourceChannels * destinationChannels];

        // A mono guest port is duplicated at unity rather than folded at -3 dB:
        // SDL's mono upmix copies the sample to both fronts, and a mono AudioOut
        // port must not get quieter for moving to this backend.
        if (sourceChannels == 1)
        {
            foreach (var role in (MacAudioChannelRole[])
                     [MacAudioChannelRole.FrontLeft, MacAudioChannelRole.FrontRight])
            {
                var destination = Array.IndexOf(
                    Layout(Math.Min(destinationChannels, MaxChannels)), role);
                if (destination >= 0)
                {
                    matrix[destination] = 1.0f;
                }
            }

            if (Array.TrueForAll(matrix, gain => gain == 0.0f))
            {
                matrix[0] = 1.0f;
            }

            return matrix;
        }

        var sourceRoles = Layout(sourceChannels);
        // A device wider than 7.1 (an aggregate or a pro interface) gets the 7.1
        // positions filled and the remaining outputs left silent rather than fed
        // a channel the guest never meant for them.
        var destinationRoles = Layout(Math.Min(destinationChannels, MaxChannels));
        for (var source = 0; source < sourceChannels; source++)
        {
            foreach (var (role, gain) in Targets(sourceRoles[source], destinationRoles))
            {
                var destination = Array.IndexOf(destinationRoles, role);
                if (destination >= 0)
                {
                    matrix[(source * destinationChannels) + destination] += gain;
                }
            }
        }

        Normalize(matrix, sourceChannels, destinationChannels);
        return matrix;
    }

    /// <summary>True when the matrix is a channel-for-channel unity copy.</summary>
    public static bool IsIdentity(float[] matrix, int sourceChannels, int destinationChannels)
    {
        if (sourceChannels != destinationChannels)
        {
            return false;
        }

        for (var source = 0; source < sourceChannels; source++)
        {
            for (var destination = 0; destination < destinationChannels; destination++)
            {
                var expected = source == destination ? 1.0f : 0.0f;
                if (matrix[(source * destinationChannels) + destination] != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Where one guest channel goes. A role the device has takes the signal at
    /// unity; otherwise it folds towards the nearest speakers the device does
    /// have. LFE is dropped when the device has no subwoofer channel — folding
    /// bass into the fronts is what makes a downmix boom.
    /// </summary>
    private static (MacAudioChannelRole Role, float Gain)[] Targets(
        MacAudioChannelRole role,
        MacAudioChannelRole[] destinationRoles)
    {
        if (Array.IndexOf(destinationRoles, role) >= 0)
        {
            return [(role, 1.0f)];
        }

        var hasFronts = Array.IndexOf(destinationRoles, MacAudioChannelRole.FrontLeft) >= 0 &&
                        Array.IndexOf(destinationRoles, MacAudioChannelRole.FrontRight) >= 0;

        // A single-speaker device: everything sums into it, and the row
        // normalisation below turns that sum into an average.
        if (destinationRoles is [MacAudioChannelRole.FrontCenter])
        {
            return role == MacAudioChannelRole.LowFrequency
                ? []
                : [(MacAudioChannelRole.FrontCenter, 1.0f)];
        }

        return role switch
        {
            MacAudioChannelRole.LowFrequency => [],
            MacAudioChannelRole.FrontCenter when hasFronts =>
            [
                (MacAudioChannelRole.FrontLeft, FoldGain),
                (MacAudioChannelRole.FrontRight, FoldGain),
            ],
            MacAudioChannelRole.BackLeft or MacAudioChannelRole.SideLeft =>
                FoldSide(destinationRoles, left: true, hasFronts),
            MacAudioChannelRole.BackRight or MacAudioChannelRole.SideRight =>
                FoldSide(destinationRoles, left: false, hasFronts),
            MacAudioChannelRole.BackCenter =>
            [
                .. FoldSide(destinationRoles, left: true, hasFronts),
                .. FoldSide(destinationRoles, left: false, hasFronts),
            ],
            _ => [],
        };
    }

    private static (MacAudioChannelRole Role, float Gain)[] FoldSide(
        MacAudioChannelRole[] destinationRoles,
        bool left,
        bool hasFronts)
    {
        var surround = left ? MacAudioChannelRole.BackLeft : MacAudioChannelRole.BackRight;
        var side = left ? MacAudioChannelRole.SideLeft : MacAudioChannelRole.SideRight;
        var front = left ? MacAudioChannelRole.FrontLeft : MacAudioChannelRole.FrontRight;

        if (Array.IndexOf(destinationRoles, surround) >= 0)
        {
            return [(surround, FoldGain)];
        }

        if (Array.IndexOf(destinationRoles, side) >= 0)
        {
            return [(side, FoldGain)];
        }

        return hasFronts ? [(front, FoldGain)] : [];
    }

    /// <summary>
    /// Scales back any output channel whose contributions sum above unity, so a
    /// fold cannot drive full-scale inputs past full scale.
    /// </summary>
    private static void Normalize(float[] matrix, int sourceChannels, int destinationChannels)
    {
        for (var destination = 0; destination < destinationChannels; destination++)
        {
            var sum = 0.0f;
            for (var source = 0; source < sourceChannels; source++)
            {
                sum += Math.Abs(matrix[(source * destinationChannels) + destination]);
            }

            if (sum <= 1.0f)
            {
                continue;
            }

            for (var source = 0; source < sourceChannels; source++)
            {
                matrix[(source * destinationChannels) + destination] /= sum;
            }
        }
    }

    private static float[]? Validate(int sourceChannels, int destinationChannels)
    {
        if (sourceChannels is < 1 or > MaxChannels)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        }

        if (destinationChannels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationChannels));
        }

        return null;
    }

    private static MacAudioChannelLabel LabelOf(MacAudioChannelRole role) => role switch
    {
        MacAudioChannelRole.FrontLeft => MacAudioChannelLabel.Left,
        MacAudioChannelRole.FrontRight => MacAudioChannelLabel.Right,
        MacAudioChannelRole.FrontCenter => MacAudioChannelLabel.Center,
        MacAudioChannelRole.LowFrequency => MacAudioChannelLabel.LfeScreen,
        MacAudioChannelRole.BackLeft => MacAudioChannelLabel.LeftSurround,
        MacAudioChannelRole.BackRight => MacAudioChannelLabel.RightSurround,
        MacAudioChannelRole.SideLeft => MacAudioChannelLabel.LeftSurroundDirect,
        MacAudioChannelRole.SideRight => MacAudioChannelLabel.RightSurroundDirect,
        _ => MacAudioChannelLabel.CenterSurround,
    };
}
