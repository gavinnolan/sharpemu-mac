// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using System.Net.Sockets;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Descriptor filters: EVFILT_READ / EVFILT_WRITE are level triggered and are
/// re-evaluated when a wait starts, when they are registered and when the
/// readiness monitor reports a socket change. EVFILT_VNODE follows host file
/// system notifications.
/// </summary>
public static partial class KernelEventQueueCompatExports
{
    private const int SceEbadf = unchecked((int)0x80020009);

    public const uint NoteDelete = 0x01;
    public const uint NoteWrite = 0x02;
    public const uint NoteExtend = 0x04;
    public const uint NoteAttrib = 0x08;
    public const uint NoteLink = 0x10;
    public const uint NoteRename = 0x20;
    public const uint NoteRevoke = 0x40;
    private const uint VnodeNoteMask = NoteDelete | NoteWrite | NoteExtend | NoteAttrib | NoteLink | NoteRename | NoteRevoke;

    /// <summary>Wake key of poll/select waiters; any descriptor change re-evaluates them.</summary>
    internal const string DescriptorReadinessWakeKey = "sceKernelPoll:readiness";

    private static readonly object _descriptorIndexGate = new();
    private static readonly Dictionary<int, List<Knote>> _descriptorKnotes = new();

    static KernelEventQueueCompatExports()
    {
        DescriptorReadinessMonitor.DescriptorReady += NotifyDescriptorChanged;
    }

    /// <summary>
    /// A descriptor's readiness may have changed (socket data arrived, file
    /// written, descriptor closed): re-evaluate its knotes and poll waiters.
    /// </summary>
    internal static void NotifyDescriptorChanged(int fd)
    {
        Knote[] knotes;
        lock (_descriptorIndexGate)
        {
            knotes = _descriptorKnotes.TryGetValue(fd, out var list) ? [.. list] : [];
        }

        foreach (var knote in knotes)
        {
            if (knote.Filter is KernelEventFilterRead or KernelEventFilterWrite)
            {
                RefreshDescriptorKnote(knote);
            }
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(DescriptorReadinessWakeKey);
        PulseHostPollWaiters();
    }

    /// <summary>
    /// knote_fdclose: closing a descriptor deletes every knote attached to it.
    /// </summary>
    internal static void OnDescriptorClosed(int fd)
    {
        List<Knote>? knotes;
        lock (_descriptorIndexGate)
        {
            _descriptorKnotes.Remove(fd, out knotes);
        }

        DescriptorReadinessMonitor.Disarm(fd);
        if (knotes is not null)
        {
            foreach (var knote in knotes)
            {
                var state = knote.Owner;
                lock (state.Gate)
                {
                    DropKnoteLocked(state, knote);
                }

                Interlocked.Exchange(ref knote.Watch, null)?.Dispose();
            }
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(DescriptorReadinessWakeKey);
        PulseHostPollWaiters();
    }

    private static readonly object _hostPollGate = new();
    private static long _descriptorChangeGeneration;

    /// <summary>Counts descriptor changes; host poll waiters sleep until it moves.</summary>
    internal static long DescriptorChangeGeneration => Interlocked.Read(ref _descriptorChangeGeneration);

    private static void PulseHostPollWaiters()
    {
        lock (_hostPollGate)
        {
            _descriptorChangeGeneration++;
            Monitor.PulseAll(_hostPollGate);
        }
    }

    /// <summary>
    /// Parks a host thread until a descriptor changes after
    /// <paramref name="observedGeneration"/> or the deadline (0 = none) passes.
    /// </summary>
    internal static void WaitForDescriptorChange(long observedGeneration, long deadlineTimestamp)
    {
        lock (_hostPollGate)
        {
            while (_descriptorChangeGeneration == observedGeneration)
            {
                if (deadlineTimestamp == 0)
                {
                    Monitor.Wait(_hostPollGate);
                    continue;
                }

                var remaining = deadlineTimestamp - System.Diagnostics.Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    return;
                }

                var milliseconds = Math.Max(1, (long)Math.Ceiling(
                    remaining * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
                Monitor.Wait(_hostPollGate, (int)Math.Min(milliseconds, int.MaxValue));
            }
        }
    }

    private static void IndexDescriptorKnote(Knote knote)
    {
        var fd = unchecked((int)knote.Ident);
        lock (_descriptorIndexGate)
        {
            if (!_descriptorKnotes.TryGetValue(fd, out var list))
            {
                list = [];
                _descriptorKnotes[fd] = list;
            }

            if (!list.Contains(knote))
            {
                list.Add(knote);
            }
        }
    }

    private static void UnindexDescriptorKnote(Knote knote)
    {
        var fd = unchecked((int)knote.Ident);
        lock (_descriptorIndexGate)
        {
            if (_descriptorKnotes.TryGetValue(fd, out var list) &&
                list.Remove(knote) &&
                list.Count == 0)
            {
                _descriptorKnotes.Remove(fd);
            }
        }
    }

    private static void RefreshDescriptorKnotes(Kqueue state)
    {
        List<Knote>? knotes = null;
        lock (state.Gate)
        {
            foreach (var knote in state.Knotes.Values)
            {
                if (knote.Filter is KernelEventFilterRead or KernelEventFilterWrite)
                {
                    (knotes ??= []).Add(knote);
                }
            }
        }

        if (knotes is null)
        {
            return;
        }

        foreach (var knote in knotes)
        {
            RefreshDescriptorKnote(knote);
        }
    }

    /// <summary>
    /// Re-evaluates a level-triggered descriptor knote: activates it with the
    /// readable byte count (or EV_EOF), deactivates it when no longer ready
    /// and arms the readiness monitor, or drops it when the descriptor is gone.
    /// </summary>
    private static void RefreshDescriptorKnote(Knote knote)
    {
        var fd = unchecked((int)knote.Ident);
        var exists = TryGetDescriptorState(
            fd,
            knote.Filter,
            Math.Max(1UL, knote.SData),
            out var ready,
            out var data,
            out var statusFlags);
        var state = knote.Owner;
        var signal = false;
        lock (state.Gate)
        {
            if (knote.Dropped || state.Deleted)
            {
                return;
            }

            if (!exists)
            {
                DropKnoteLocked(state, knote);
            }
            else if (ready)
            {
                knote.Data = data;
                knote.StatusFlags = statusFlags;
                ActivateLocked(state, knote);
                signal = true;
            }
            else
            {
                DeactivateLocked(state, knote);
                knote.Data = 0;
                knote.StatusFlags = 0;
            }
        }

        if (!exists)
        {
            UnindexDescriptorKnote(knote);
            return;
        }

        if (signal)
        {
            Signal(state);
            return;
        }

        ArmDescriptorMonitor(fd, knote.Filter == KernelEventFilterWrite);
    }

    /// <summary>
    /// Arms the readiness monitor for a socket descriptor. Files are always
    /// ready and need no monitoring.
    /// </summary>
    internal static void ArmDescriptorMonitor(int fd, bool write)
    {
        if (TryGetHostSocket(fd, out var socket) && socket is not null)
        {
            DescriptorReadinessMonitor.Arm(fd, write, socket);
        }
    }

    private static bool TryGetHostSocket(int fd, out Socket? socket)
    {
        if (KernelSocketCompatExports.TryGetHostSocket(fd, out socket))
        {
            return true;
        }

        return NetExports.TryGetHostSocket(fd, out socket);
    }

    /// <summary>
    /// The filter state of a descriptor, as filt_soread / filt_sowrite /
    /// filt_vfsread / filt_vfswrite compute it. Returns false for an unknown
    /// descriptor.
    /// </summary>
    internal static bool TryGetDescriptorState(
        int fd,
        short filter,
        ulong lowWater,
        out bool ready,
        out ulong data,
        out ushort statusFlags)
    {
        if (filter == KernelEventFilterWrite)
        {
            statusFlags = 0;
            data = 0;
            if (fd is 1 or 2)
            {
                ready = true;
                return true;
            }

            if (fd == 0)
            {
                ready = false;
                return true;
            }

            if (TryGetHostSocket(fd, out var socket))
            {
                ready = false;
                if (socket is null)
                {
                    return true;
                }

                try
                {
                    var connectionless = socket.SocketType != SocketType.Stream;
                    if ((connectionless || socket.Connected) && socket.Poll(0, SelectMode.SelectWrite))
                    {
                        data = unchecked((ulong)Math.Max(0, socket.SendBufferSize));
                        ready = data >= lowWater;
                    }
                }
                catch (SocketException)
                {
                    ready = true;
                    statusFlags = KernelEventFlagEof;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                return true;
            }

            return KernelMemoryCompatExports.TryGetFileWriteEventState(fd, out ready);
        }

        if (fd is 0 or 1 or 2)
        {
            // The console has no input.
            ready = false;
            data = 0;
            statusFlags = 0;
            return true;
        }

        if (KernelSocketCompatExports.TryGetReadEventState(fd, lowWater, out ready, out data, out statusFlags) ||
            KernelMemoryCompatExports.TryGetFileReadEventState(fd, lowWater, out ready, out data, out statusFlags) ||
            NetExports.TryGetReadEventState(fd, lowWater, out ready, out data, out statusFlags))
        {
            return true;
        }

        data = 0;
        statusFlags = 0;
        ready = KernelMemoryCompatExports.IsAlwaysReadyDescriptor(fd);
        return ready;
    }

    [SysAbiExport(
        Nid = "cG3t15OK4Ow",
        ExportName = "sceKernelAddFileEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddFileEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var fd = unchecked((int)ctx[CpuRegister.Rsi]);
        var watch = unchecked((uint)ctx[CpuRegister.Rdx]);
        var userData = ctx[CpuRegister.Rcx];
        var result = AddVnodeKnote(handle, fd, watch, userData, KernelEventFlagClear);
        TraceEventQueue(ctx, "add_file", handle, $"fd={fd} watch=0x{watch:X} result=0x{unchecked((uint)result):X8}");
        return result;
    }

    [SysAbiExport(
        Nid = "UmXngHKB6is",
        ExportName = "sceKernelDeleteFileEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteFileEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var fd = unchecked((int)ctx[CpuRegister.Rsi]);
        var deleted = DeleteRegisteredEvent(handle, unchecked((uint)fd), KernelEventFilterVnode);
        TraceEventQueue(ctx, "delete_file", handle, $"fd={fd}");
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    private static int AddVnodeKnote(ulong handle, int fd, uint watch, ulong userData, ushort flags)
    {
        if (!TryGetLiveEventQueue(handle, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!KernelMemoryCompatExports.TryGetOpenFileHostPath(fd, out var path))
        {
            return SceEbadf;
        }

        Knote knote;
        bool created;
        lock (state.Gate)
        {
            if (state.Deleted)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            knote = AttachOrUpdateLocked(
                state,
                unchecked((uint)fd),
                KernelEventFilterVnode,
                userData,
                (ushort)(flags | KernelEventFlagClear),
                watch & VnodeNoteMask,
                0,
                out created);
        }

        if (created)
        {
            IndexDescriptorKnote(knote);
            var watcher = VnodeWatch.TryCreate(knote, path);
            if (watcher is not null)
            {
                Interlocked.Exchange(ref knote.Watch, watcher)?.Dispose();
                if (Volatile.Read(ref knote.Dropped))
                {
                    Interlocked.Exchange(ref knote.Watch, null)?.Dispose();
                }
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TriggerVnodeKnote(Knote knote, uint notes)
    {
        var state = knote.Owner;
        lock (state.Gate)
        {
            notes &= knote.SFflags;
            if (notes == 0 || knote.Dropped || state.Deleted)
            {
                return;
            }

            knote.Fflags |= notes;
            if ((notes & (NoteDelete | NoteRevoke)) != 0)
            {
                knote.StatusFlags |= KernelEventFlagEof;
            }

            ActivateLocked(state, knote);
        }

        Signal(state, _logEqueue ? $"source=vnode ident=0x{knote.Ident:X16} notes=0x{notes:X}" : null);
    }

    // Host file system notifications for one watched file.
    private sealed class VnodeWatch : IDisposable
    {
        private readonly Knote _knote;
        private readonly FileSystemWatcher _watcher;
        private readonly string _path;
        private long _lastLength;

        private VnodeWatch(Knote knote, FileSystemWatcher watcher, string path)
        {
            _knote = knote;
            _watcher = watcher;
            _path = path;
            _lastLength = LengthOf(path);
        }

        public static VnodeWatch? TryCreate(Knote knote, string path)
        {
            var fullPath = Path.GetFullPath(path);
            var isDirectory = Directory.Exists(fullPath);
            var directory = isDirectory ? fullPath : Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    Filter = isDirectory ? "*" : Path.GetFileName(fullPath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                                   NotifyFilters.Attributes | NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                };
                var watch = new VnodeWatch(knote, watcher, fullPath);
                watcher.Changed += watch.OnChanged;
                watcher.Created += watch.OnChanged;
                watcher.Deleted += watch.OnDeleted;
                watcher.Renamed += watch.OnRenamed;
                watcher.EnableRaisingEvents = true;
                return watch;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        private static long LengthOf(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs args)
        {
            var length = LengthOf(_path);
            var previous = Interlocked.Exchange(ref _lastLength, length);
            // A change that kept the length may be a write or an attribute
            // change; the host notification does not say which.
            var notes = length == previous ? NoteWrite | NoteAttrib : NoteWrite;
            if (length > previous)
            {
                notes |= NoteExtend;
            }

            if (Directory.Exists(_path))
            {
                // A directory changes when an entry is added or removed.
                notes = NoteWrite | (args.ChangeType == WatcherChangeTypes.Created ? NoteLink : 0);
            }

            TriggerVnodeKnote(_knote, notes);
        }

        private void OnDeleted(object sender, FileSystemEventArgs args) =>
            TriggerVnodeKnote(
                _knote,
                string.Equals(args.FullPath, _path, StringComparison.Ordinal) ? NoteDelete : NoteWrite);

        private void OnRenamed(object sender, RenamedEventArgs args) =>
            TriggerVnodeKnote(
                _knote,
                string.Equals(args.OldFullPath, _path, StringComparison.Ordinal) ? NoteRename : NoteWrite);

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
