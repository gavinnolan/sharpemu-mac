// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Explicit read-ahead for host files the guest streams sequentially.
/// </summary>
/// <remarks>
/// A guest that reads a large archive in small positional chunks issues one
/// syscall per chunk and waits for the device on each. macOS's own read-ahead
/// (F_RDAHEAD, on by default) keys off the file offset, which a positional
/// <c>pread</c> never advances, so a <c>RandomAccess.Read</c> stream looks
/// random to the kernel however sequential it really is. F_RDADVISE states the
/// intent directly: it queues the read asynchronously with no copy to
/// userspace, so the next chunk can come out of the buffer cache.
///
/// .NET exposes no equivalent. FileOptions.RandomAccess and SequentialScan map
/// to posix_fadvise, which macOS does not implement, so they are inert here.
/// </remarks>
internal static partial class HostReadAhead
{
    // <sys/fcntl.h>: "Issue an advisory read async with no copy to user".
    private const int FRdAdvise = 44;

    /// <summary>
    /// How far ahead of a continuing read to prefetch. Large enough to cover
    /// several of Unreal's 64 KiB pak reads per hint, small enough that a
    /// stream which turns out not to continue has not displaced much.
    /// </summary>
    internal const int WindowBytes = 1 << 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RaAdvisory
    {
        public long Offset;
        public int Count;
    }

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int descriptor, int command, ref RaAdvisory advisory);

    // F_RDADVISE is advisory, and a refusal can be about one call (an offset
    // the filesystem will not prefetch) rather than about support. Giving up on
    // the first failure would silently drop read-ahead for the whole process,
    // so support is only written off after a run of failures.
    private const int FailuresBeforeGivingUp = 32;
    private static int _consecutiveFailures;
    private static int _unavailable;

    internal static bool Enabled { get; } =
        OperatingSystem.IsMacOS() &&
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_READ_AHEAD"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Hints that <paramref name="count"/> bytes at <paramref name="offset"/>
    /// will be read next. Best-effort: a kernel or filesystem that refuses the
    /// hint just leaves the following read to fetch its own bytes.
    /// </summary>
    internal static void Advise(SafeFileHandle handle, long offset, int count)
    {
        if (!Enabled || count <= 0 || offset < 0 ||
            Volatile.Read(ref _unavailable) != 0 ||
            handle.IsInvalid || handle.IsClosed)
        {
            return;
        }

        var advisory = new RaAdvisory { Offset = offset, Count = count };
        try
        {
            // DangerousGetHandle is correct here: the call does not outlive this
            // frame, and the caller's CachedHostFile owns the handle for the
            // lifetime of its entry in the open-file cache.
            if (Fcntl((int)handle.DangerousGetHandle(), FRdAdvise, ref advisory) == 0)
            {
                Volatile.Write(ref _consecutiveFailures, 0);
            }
            else if (Interlocked.Increment(ref _consecutiveFailures) >= FailuresBeforeGivingUp)
            {
                Volatile.Write(ref _unavailable, 1);
            }
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or DllNotFoundException)
        {
            Volatile.Write(ref _unavailable, 1);
        }
        catch (ObjectDisposedException)
        {
            // The handle was closed between the check and the call; the read
            // that follows will fail on its own terms.
        }
    }
}
