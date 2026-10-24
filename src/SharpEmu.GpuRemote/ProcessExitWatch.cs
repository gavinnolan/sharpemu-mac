// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>
/// Reports the exit of another process once, from a background thread, with kqueue's
/// EVFILT_PROC/NOTE_EXIT - the same mechanism SharpEmu.CLI's OwnerProcessWatch uses for the GUI.
/// Each side of the split watches the other: A its GPU host child (and reaps it), B its parent.
/// </summary>
internal static class ProcessExitWatch
{
    private const int FallbackPollMilliseconds = 100;

    /// <summary>
    /// Starts watching <paramref name="pid"/>. <paramref name="onExit"/> runs once, with the
    /// wait status when <paramref name="reapChild"/> is set (the pid must be this process's
    /// child), otherwise with -1. When <paramref name="expectedParentOfSelf"/> is set, the watch
    /// also treats "this process was re-parented away from pid" as the exit, which covers a
    /// parent that died before the watch could register.
    /// </summary>
    public static void Start(int pid, string threadName, Action<int> onExit, bool reapChild, bool expectedParentOfSelf = false)
    {
        new Thread(() => Watch(pid, onExit, reapChild, expectedParentOfSelf), 256 * 1024)
        {
            IsBackground = true,
            Name = threadName,
        }.Start();
    }

    private static unsafe void Watch(int pid, Action<int> onExit, bool reapChild, bool expectedParentOfSelf)
    {
        var queue = RemoteNative.kqueue();
        if (queue < 0)
        {
            Poll(pid, onExit, reapChild, expectedParentOfSelf);
            return;
        }

        try
        {
            var change = new RemoteNative.KEvent
            {
                Ident = (nuint)pid,
                Filter = RemoteNative.EVFILT_PROC,
                Flags = RemoteNative.EV_ADD | RemoteNative.EV_ONESHOT,
                FilterFlags = RemoteNative.NOTE_EXIT,
            };
            if (RemoteNative.kevent(queue, &change, 1, null, 0, null) != 0)
            {
                // ESRCH: already gone (a zombie child included, which the reap below collects).
                if (Marshal.GetLastPInvokeError() != RemoteNative.ESRCH)
                {
                    Poll(pid, onExit, reapChild, expectedParentOfSelf);
                    return;
                }
            }
            else if (!expectedParentOfSelf || RemoteNative.getppid() == pid)
            {
                RemoteNative.KEvent received;
                while (true)
                {
                    var count = RemoteNative.kevent(queue, null, 0, &received, 1, null);
                    if (count > 0)
                    {
                        break;
                    }

                    if (count < 0 && Marshal.GetLastPInvokeError() != RemoteNative.EINTR)
                    {
                        Poll(pid, onExit, reapChild, expectedParentOfSelf);
                        return;
                    }
                }
            }
        }
        finally
        {
            _ = RemoteNative.close(queue);
        }

        onExit(reapChild ? Reap(pid) : -1);
    }

    // Only when kqueue itself fails.
    private static void Poll(int pid, Action<int> onExit, bool reapChild, bool expectedParentOfSelf)
    {
        while (true)
        {
            if (reapChild)
            {
                var status = TryReap(pid, out var exited);
                if (exited)
                {
                    onExit(status);
                    return;
                }
            }
            else if (expectedParentOfSelf ? RemoteNative.getppid() != pid : RemoteNative.kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == RemoteNative.ESRCH)
            {
                onExit(-1);
                return;
            }

            Thread.Sleep(FallbackPollMilliseconds);
        }
    }

    private static unsafe int Reap(int pid)
    {
        int status;
        while (RemoteNative.waitpid(pid, &status, 0) < 0)
        {
            if (Marshal.GetLastPInvokeError() != RemoteNative.EINTR)
            {
                return -1;
            }
        }

        return status;
    }

    private static unsafe int TryReap(int pid, out bool exited)
    {
        int status;
        var result = RemoteNative.waitpid(pid, &status, RemoteNative.WNOHANG);
        exited = result == pid || (result < 0 && Marshal.GetLastPInvokeError() == RemoteNative.ECHILD);
        return result == pid ? status : -1;
    }
}
