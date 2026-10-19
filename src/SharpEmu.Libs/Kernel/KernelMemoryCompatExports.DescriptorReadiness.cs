// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    internal static bool TryGetFileReadEventState(
        int fileDescriptor,
        ulong lowWater,
        out bool ready,
        out ulong availableBytes,
        out ushort eventFlags)
    {
        ready = false;
        availableBytes = 0;
        eventFlags = 0;

        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(fileDescriptor, out var stream) ||
                !stream.CanRead)
            {
                return false;
            }

            try
            {
                var remaining = Math.Max(0L, stream.Length - stream.Position);
                availableBytes = unchecked((ulong)remaining);
                if (remaining == 0)
                {
                    ready = true;
                    eventFlags = KernelEventQueueCompatExports.KernelEventFlagEof;
                }
                else
                {
                    ready = availableBytes >= Math.Max(1UL, lowWater);
                }
            }
            catch (IOException)
            {
                // The descriptor is still registered. A later poll can observe
                // it again after the host finishes the concurrent operation.
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A descriptor that is not a byte stream but is always ready (the random
    /// device and directories), as FreeBSD's vnode filters report them.
    /// </summary>
    internal static bool IsAlwaysReadyDescriptor(int fileDescriptor)
    {
        lock (_fdGate)
        {
            return _randomDeviceDescriptors.Contains(fileDescriptor) ||
                   _openDirectories.ContainsKey(fileDescriptor);
        }
    }

    /// <summary>Write readiness of a file: a vnode is always writable (data 0).</summary>
    internal static bool TryGetFileWriteEventState(int fileDescriptor, out bool ready)
    {
        lock (_fdGate)
        {
            ready = _openFiles.ContainsKey(fileDescriptor) ||
                    _randomDeviceDescriptors.Contains(fileDescriptor) ||
                    _openDirectories.ContainsKey(fileDescriptor);
            return ready;
        }
    }

    internal static bool TryGetOpenFileHostPath(int fileDescriptor, out string path)
    {
        lock (_fdGate)
        {
            if (_openFiles.TryGetValue(fileDescriptor, out var stream) &&
                stream is FileStream fileStream &&
                !string.IsNullOrEmpty(fileStream.Name))
            {
                path = fileStream.Name;
                return true;
            }

            if (_openDirectories.TryGetValue(fileDescriptor, out var directory))
            {
                path = directory.Path;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }
}
