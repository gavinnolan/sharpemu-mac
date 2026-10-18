// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// A title's single-copy kill switch (artifacts/memopt/single-copy-device-address.md §3 (d) 5),
/// kept next to its device-address reach profile: written the first time a device-address
/// program of the title read a released chunk (the shader's trap saw it, so that access read
/// zeros once), and from the next start on the title releases nothing once a device-address
/// program has run, as before the evaluated ranges. Deleting the file clears it.
/// </summary>
internal sealed class MetalSingleCopyKillSwitch
{
    internal const string FileName = "single-copy-da-v1.txt";

    private readonly string? _path;
    private int _written;

    private MetalSingleCopyKillSwitch(string? path, bool set)
    {
        _path = path;
        IsSet = set;
    }

    /// <summary>Whether the switch was found set when the session started.</summary>
    public bool IsSet { get; }

    /// <summary>Whether this session has set it.</summary>
    public bool Tripped => Volatile.Read(ref _written) != 0;

    public string? Path => _path;

    /// <summary>The switch in <paramref name="directory"/>; unreadable, it is off and never written.</summary>
    public static MetalSingleCopyKillSwitch Open(string directory)
    {
        try
        {
            var path = System.IO.Path.Combine(directory, FileName);
            return new MetalSingleCopyKillSwitch(path, File.Exists(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] The single-copy kill switch could not be read: {exception.Message}");
            return new MetalSingleCopyKillSwitch(null, false);
        }
    }

    /// <summary>Sets the switch for the next start, once per session, with what tripped it.</summary>
    public void Trip(string reason)
    {
        if (_path is null || Interlocked.Exchange(ref _written, 1) != 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:O} {reason}\n"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] The single-copy kill switch could not be written: {exception.Message}");
        }
    }
}
