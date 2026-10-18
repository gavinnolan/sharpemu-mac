// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.CLI;

/// <summary>
/// The macOS half of the GUI's session ownership. Windows ties a GUI-launched emulator to the
/// GUI with a kill-on-close job object; macOS has no equivalent and .NET cannot run code between
/// fork and exec, so the emulator does it itself when the GUI names itself in
/// <see cref="OwnerPidEnvironment"/>: it leads a process group of its own, which the GUI's Stop
/// signals as a whole, and it watches the GUI with kqueue so that losing the GUI — a crash or a
/// SIGKILL included — takes the emulator and everything it started down with it.
/// A command-line run without the variable is left exactly as it was.
/// </summary>
internal static class OwnerProcessWatch
{
    public const string OwnerPidEnvironment = "SHARPEMU_OWNER_PID";

    // The emulator's own cooperative shutdown exits within four seconds of a SIGTERM
    // (VideoOutExports.ShutdownGraceMilliseconds); the group is killed outright a second after.
    private const int ShutdownDeadlineMilliseconds = 5000;
    private const int FallbackPollMilliseconds = 250;

    private const short EVFILT_PROC = -5;
    private const ushort EV_ADD = 0x0001;
    private const ushort EV_ONESHOT = 0x0010;
    private const uint NOTE_EXIT = 0x80000000;
    private const int EINTR = 4;
    private const int ESRCH = 3;
    private const int SIGKILL = 9;
    private const int SIGTERM = 15;
    private const uint PROC_PGRP_ONLY = 2;

    private static int _ownerPid;
    private static bool _isGroupLeader;
    private static int _ownerLost;

    /// <summary>
    /// Called first thing in CLI startup. Does nothing unless this is macOS and the GUI set
    /// <see cref="OwnerPidEnvironment"/>; the variable is cleared so nothing this process starts
    /// inherits it.
    /// </summary>
    public static void StartIfRequested()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var value = Environment.GetEnvironmentVariable(OwnerPidEnvironment);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Environment.SetEnvironmentVariable(OwnerPidEnvironment, null);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid) || ownerPid <= 1)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Ignoring {OwnerPidEnvironment}={value}: not a process id.");
            return;
        }

        // Before anything can start a thread or a child: everything this process starts from
        // here on is in the group the GUI signals.
        if (setpgid(0, 0) != 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Could not lead a process group (errno {Marshal.GetLastPInvokeError()}); " +
                "only this process is stopped with the GUI.");
        }

        _isGroupLeader = getpgrp() == Environment.ProcessId;
        _ownerPid = ownerPid;
        new Thread(WatchOwner, 256 * 1024)
        {
            IsBackground = true,
            Name = "SharpEmu owner watch",
        }.Start();
    }

    private static unsafe void WatchOwner()
    {
        var queue = kqueue();
        if (queue < 0)
        {
            PollOwner();
            return;
        }

        try
        {
            var change = new KEvent
            {
                Ident = (nuint)_ownerPid,
                Filter = EVFILT_PROC,
                Flags = EV_ADD | EV_ONESHOT,
                FilterFlags = NOTE_EXIT,
            };
            if (kevent(queue, &change, 1, null, 0, null) != 0)
            {
                if (Marshal.GetLastPInvokeError() == ESRCH)
                {
                    OnOwnerLost();
                }
                else
                {
                    PollOwner();
                }

                return;
            }

            // Registered, so an exit from here on is reported. One that came first shows as
            // having been re-parented away from the owner (a stale or reused pid included).
            if (getppid() != _ownerPid)
            {
                OnOwnerLost();
                return;
            }

            KEvent received;
            while (true)
            {
                var count = kevent(queue, null, 0, &received, 1, null);
                if (count > 0)
                {
                    break;
                }

                if (count < 0 && Marshal.GetLastPInvokeError() != EINTR)
                {
                    PollOwner();
                    return;
                }
            }
        }
        finally
        {
            _ = close(queue);
        }

        OnOwnerLost();
    }

    // Only when kqueue itself is unavailable: the owner is the parent, so losing it
    // re-parents this process.
    private static void PollOwner()
    {
        while (getppid() == _ownerPid)
        {
            Thread.Sleep(FallbackPollMilliseconds);
        }

        OnOwnerLost();
    }

    private static void OnOwnerLost()
    {
        if (Interlocked.Exchange(ref _ownerLost, 1) != 0)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][INFO] Owner process {_ownerPid} is gone; shutting down.");
        if (_isGroupLeader)
        {
            // A cooperative exit that beats the deadline would otherwise leave behind
            // whatever in the group ignored the SIGTERM.
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => KillOtherGroupMembers();
        }

        // The same SIGTERM the GUI's Stop sends, so this process takes its usual cooperative
        // shutdown and anything it started gets the same request.
        Signal(SIGTERM);
        Thread.Sleep(ShutdownDeadlineMilliseconds);
        Signal(SIGKILL);
    }

    private static void Signal(int signal)
    {
        _ = _isGroupLeader
            ? kill(-Environment.ProcessId, signal)
            : kill(Environment.ProcessId, signal);
    }

    private static unsafe void KillOtherGroupMembers()
    {
        var self = Environment.ProcessId;
        var pids = new int[256];
        int bytes;
        fixed (int* buffer = pids)
        {
            bytes = proc_listpids(PROC_PGRP_ONLY, (uint)self, buffer, pids.Length * sizeof(int));
        }

        for (var i = 0; i < bytes / sizeof(int); i++)
        {
            if (pids[i] > 0 && pids[i] != self)
            {
                _ = kill(pids[i], SIGKILL);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEvent
    {
        public nuint Ident;
        public short Filter;
        public ushort Flags;
        public uint FilterFlags;
        public nint Data;
        public nint UserData;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setpgid(int pid, int pgid);

    [DllImport("libc")]
    private static extern int getpgrp();

    [DllImport("libc")]
    private static extern int getppid();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int kqueue();

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int kevent(int kq, KEvent* changes, int changeCount, KEvent* events, int eventCount, void* timeout);

    [DllImport("libc")]
    private static extern int close(int fd);

    [DllImport("libc")]
    private static extern unsafe int proc_listpids(uint type, uint typeInfo, int* buffer, int bufferSize);
}
