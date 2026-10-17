// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Logging;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Gpu.Metal.V2;

// Which guest memory windows an encoder that runs a device-address program names resident.
//
// Such a program can dereference any mapped guest page, so the safe answer is every window
// the address table spans - for Demon's Souls ten to twelve 1 GiB windows, named by nearly
// every command buffer. The driver wires every page of a named window, touched or not, and
// that much on top of the image cache did not fit under the machine's wire limit: it paged
// windows in between the encoders of each command buffer (30-40 ms of driver time each),
// and a world frame spent ~415 ms of its ~675 ms of GPU time idle while it did. The
// programs reach far less: six of those windows over a whole session.
//
// So the encoder names the windows the title's programs have been seen to reach, kept per
// title next to the shader cache. Every table entry in a window outside that set carries
// the window in its page count's top byte; a lookup that lands in one reports the window
// in a small feedback buffer, and the host names that window from the next submission on
// and adds the backing it owns to the title's profile. The access itself still goes
// through: the window's pages stay mapped for the life of the buffer, so performing it
// keeps the result the guest expects, where a deterministic zero would return wrong data
// on every first touch. A cold Demon's Souls run missed only while it learned (none after
// ~280 s, no failed submission); with the profile warm it missed none at all. Misses are
// counted in the PERF line (da_misses).
// SHARPEMU_METAL_V2_DA_REACH=0 names every window the table spans instead.
internal sealed partial class MetalRenderHost
{
    private static readonly bool DeviceAddressReachEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DA_REACH") != "0";

    // Words 0..3 hold one bit per window reached outside the named set, word 4 counts
    // such lookups. See sharpemu_resolve_device_address.
    private const int ReachFeedbackWords = 8;
    private const int ReachMissCountWord = 4;
    private const ulong ReachUnnamedFlag = 1UL << 63;
    private const int ReachWindowShift = 56;

    private nint _reachFeedbackBuffer;
    private nint _reachFeedbackContents;
    private ulong _reachFeedbackAddress;
    private MetalDeviceAddressReachProfile? _reachProfile;

    // Windows the title's device-address programs are known to reach, and the set the
    // current submission's table was written against.
    private ulong _reachWindows;
    private ulong _reachWindowsThisSubmission;
    private long _reachMisses;
    private long _reachWindowsLearned;

    /// <summary>The feedback buffer a device-address program reports misses into.</summary>
    private ulong ReachFeedbackAddress()
    {
        EnsureReachFeedback();
        return _reachFeedbackAddress;
    }

    private void EnsureReachFeedback()
    {
        if (_reachFeedbackBuffer != 0)
        {
            return;
        }

        _reachFeedbackBuffer = _guestMemory.CreateResidentSharedBuffer(
            ReachFeedbackWords * sizeof(uint), "device-address.reach", out _reachFeedbackContents, out _reachFeedbackAddress);
        unsafe
        {
            new Span<uint>((void*)_reachFeedbackContents, ReachFeedbackWords).Clear();
        }

        _reachProfile = MetalDeviceAddressReachProfile.Open(
            SharpEmuPaths.UserPath(
                "pipeline_cache",
                MetalShaderPrewarm.SanitizeTitleId(VideoOutBackendServices.GetApplicationTitleId()),
                "metal-v2"));
        if (_reachProfile is not null)
        {
            foreach (var granule in _reachProfile.Granules)
            {
                _reachWindows |= 1UL << _guestMemory.WindowOwningBacking(granule << MetalDeviceAddressReachProfile.GranuleBits);
            }

            Console.Error.WriteLine(
                $"[METAL-V2][INFO] device-address reach seeded: granules={_reachProfile.Granules.Count} " +
                $"windows=0x{_reachWindows:X}");
        }
    }

    /// <summary>
    /// Takes the windows completed or running submissions reported reaching outside their
    /// named set and names them from now on. Called once per submission, as its table is
    /// written, so the table's flags and the windows its encoders name agree.
    /// </summary>
    private unsafe void BeginDeviceAddressReach()
    {
        EnsureReachFeedback();
        var words = (int*)_reachFeedbackContents;
        ulong reported = 0;
        for (var word = 0; word < 2; word++)
        {
            reported |= (ulong)(uint)Interlocked.Exchange(ref words[word], 0) << (word * 32);
        }

        Interlocked.Add(ref _reachMisses, (uint)Interlocked.Exchange(ref words[ReachMissCountWord], 0));
        var learned = reported & ~_reachWindows;
        if (learned != 0)
        {
            _reachWindows |= learned;
            Interlocked.Add(ref _reachWindowsLearned, System.Numerics.BitOperations.PopCount(learned));
            var granules = new List<ulong>();
            for (var bits = learned; bits != 0; bits &= bits - 1)
            {
                var (offset, size) = _guestMemory.WindowOwnedBacking(System.Numerics.BitOperations.TrailingZeroCount(bits));
                for (var granule = offset >> MetalDeviceAddressReachProfile.GranuleBits;
                     granule << MetalDeviceAddressReachProfile.GranuleBits < offset + size;
                     granule++)
                {
                    granules.Add(granule);
                }
            }

            _reachProfile?.Learn(granules);
        }

        _reachWindowsThisSubmission = _reachWindows;
    }

    /// <summary>The page count a table entry carries: flagged when its window is not named.</summary>
    private ulong ReachPageCount(ulong pageCount, int window)
    {
        if (!DeviceAddressReachEnabled || window < 0 || (_reachWindowsThisSubmission & (1UL << window)) != 0)
        {
            return pageCount;
        }

        return pageCount | ReachUnnamedFlag | ((ulong)window << ReachWindowShift);
    }

    /// <summary>The windows an encoder that runs a device-address program names.</summary>
    private ulong DeviceAddressNamedWindows() =>
        DeviceAddressReachEnabled ? _deviceAddressWindows & _reachWindowsThisSubmission : _deviceAddressWindows;

    private string TakeDeviceAddressReachCounters() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $" da_misses={Interlocked.Exchange(ref _reachMisses, 0)} " +
            $"da_learned={Interlocked.Exchange(ref _reachWindowsLearned, 0)} " +
            $"da_named=0x{_reachWindowsThisSubmission:X}");
}

/// <summary>
/// A title's device-address reach: the 64 MiB granules of the guest backing its programs
/// were seen to reach, one line of hexadecimal granule numbers per learn, appended as they
/// are learned and read back as a union. A granule the layout of a later start no longer
/// maps there only names a window more; one it misses is learned again on first touch.
/// </summary>
internal sealed class MetalDeviceAddressReachProfile
{
    internal const int GranuleBits = 26;
    internal const string FileName = "device-address-reach-v2.txt";

    private readonly string _path;

    private MetalDeviceAddressReachProfile(string path, HashSet<ulong> granules)
    {
        _path = path;
        Granules = granules;
    }

    public HashSet<ulong> Granules { get; }

    public static MetalDeviceAddressReachProfile? Open(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            return new MetalDeviceAddressReachProfile(path, Load(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Device-address reach could not be read: {exception.Message}");
            return null;
        }
    }

    internal static HashSet<ulong> Load(string path)
    {
        var granules = new HashSet<ulong>();
        if (!File.Exists(path))
        {
            return granules;
        }

        foreach (var line in File.ReadLines(path))
        {
            foreach (var text in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var granule))
                {
                    granules.Add(granule);
                }
            }
        }

        return granules;
    }

    internal static string Line(IEnumerable<ulong> granules) =>
        string.Join(',', granules.Order().Select(granule => granule.ToString("X", CultureInfo.InvariantCulture)));

    public void Learn(IReadOnlyCollection<ulong> granules)
    {
        var added = granules.Where(Granules.Add).ToList();
        if (added.Count == 0)
        {
            return;
        }

        try
        {
            File.AppendAllText(_path, Line(added) + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Device-address reach could not be written: {exception.Message}");
        }
    }
}
