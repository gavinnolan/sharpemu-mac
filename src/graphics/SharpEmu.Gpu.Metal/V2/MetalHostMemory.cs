// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Reads the host memory state the image budget is sized against: physical memory
/// (hw.memsize), what the kernel can hand out without compressing (host_statistics64
/// HOST_VM_INFO64: free + pageable file-backed pages, in kernel pages), this process's
/// phys_footprint (proc_pid_rusage RUSAGE_INFO_V2) and the pressure level
/// (kern.memorystatus_vm_pressure_level). A DISPATCH_SOURCE_TYPE_MEMORYPRESSURE source bumps
/// <see cref="PressureEvents"/> on every level change, so the cache re-reads at once instead
/// of at its next periodic evaluation.
/// </summary>
internal static unsafe partial class MetalHostMemory
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int HostVmInfo64 = 4;
    private const int HostVmInfo64Count = 38;
    private const int RusageInfoV2 = 2;
    private const ulong DispatchMemoryPressureNormal = 0x01;
    private const ulong DispatchMemoryPressureWarn = 0x02;
    private const ulong DispatchMemoryPressureCritical = 0x04;
    private const long QosClassUtility = 0x11;

    // struct vm_statistics64 (mach/vm_statistics.h), 152 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
        public uint FreeCount;
        public uint ActiveCount;
        public uint InactiveCount;
        public uint WireCount;
        public ulong ZeroFillCount;
        public ulong Reactivations;
        public ulong Pageins;
        public ulong Pageouts;
        public ulong Faults;
        public ulong CowFaults;
        public ulong Lookups;
        public ulong Hits;
        public ulong Purges;
        public uint PurgeableCount;
        public uint SpeculativeCount;
        public ulong Decompressions;
        public ulong Compressions;
        public ulong Swapins;
        public ulong Swapouts;
        public uint CompressorPageCount;
        public uint ThrottledCount;
        public uint ExternalPageCount;
        public uint InternalPageCount;
        public ulong TotalUncompressedPagesInCompressor;
    }

    private static readonly ulong PhysicalBytes = ReadSysctlUInt64("hw.memsize");
    private static readonly ulong KernelPageSize = ReadKernelPageSize();
    private static readonly nint PressureSource = StartPressureSource();
    private static long _pressureEvents;
    private static int _lastEventLevel;

    /// <summary>Physical memory in bytes; zero when it could not be read.</summary>
    public static ulong Physical => PhysicalBytes;

    /// <summary>Level changes the dispatch source has delivered; a new value means re-read now.</summary>
    public static long PressureEvents => Volatile.Read(ref _pressureEvents);

    /// <summary>The level the dispatch source last delivered.</summary>
    public static MetalMemoryPressure LastEventLevel => (MetalMemoryPressure)Volatile.Read(ref _lastEventLevel);

    /// <summary>Whether the dispatch source is installed.</summary>
    public static bool PressureSourceInstalled => PressureSource != 0;

    public static MetalHostMemorySample Sample()
    {
        var available = 0UL;
        var count = HostVmInfo64Count;
        VmStatistics64 statistics = default;
        if (host_statistics64(mach_host_self(), HostVmInfo64, &statistics, ref count) == 0)
        {
            // free_count already includes the speculative pages.
            available = ((ulong)statistics.FreeCount + statistics.ExternalPageCount) * KernelPageSize;
        }

        var footprint = 0UL;
        var usage = stackalloc ulong[32];
        if (proc_pid_rusage(getpid(), RusageInfoV2, usage) == 0)
        {
            // rusage_info_v2: uuid[16], then user_time, system_time, pkg_idle_wkups,
            // interrupt_wkups, pageins, wired_size, resident_size, phys_footprint.
            footprint = usage[2 + 7];
        }

        return new MetalHostMemorySample(PhysicalBytes, available, footprint, ReadPressureLevel());
    }

    private static MetalMemoryPressure ReadPressureLevel()
    {
        var level = 0;
        var size = (nuint)sizeof(int);
        if (sysctlbyname("kern.memorystatus_vm_pressure_level", &level, &size, null, 0) != 0)
        {
            return LastEventLevel;
        }

        return level switch
        {
            4 => MetalMemoryPressure.Critical,
            2 => MetalMemoryPressure.Warn,
            1 => MetalMemoryPressure.Normal,
            _ => MetalMemoryPressure.Unknown,
        };
    }

    private static ulong ReadSysctlUInt64(string name)
    {
        ulong value = 0;
        var size = (nuint)sizeof(ulong);
        return sysctlbyname(name, &value, &size, null, 0) == 0 ? value : 0;
    }

    // vm_statistics64 counts kernel pages (16 KiB on Apple Silicon); vm_kernel_page_size is
    // the libSystem global behind vm_stat's "page size of N bytes".
    private static ulong ReadKernelPageSize()
    {
        if (NativeLibrary.TryLoad(LibSystem, out var library) &&
            NativeLibrary.TryGetExport(library, "vm_kernel_page_size", out var symbol))
        {
            var size = *(nuint*)symbol;
            if (size != 0)
            {
                return size;
            }
        }

        return (ulong)Environment.SystemPageSize;
    }

    private static nint StartPressureSource()
    {
        if (!OperatingSystem.IsMacOS() ||
            !NativeLibrary.TryLoad(LibSystem, out var library) ||
            !NativeLibrary.TryGetExport(library, "_dispatch_source_type_memorypressure", out var type))
        {
            return 0;
        }

        var queue = dispatch_get_global_queue(QosClassUtility, 0);
        var source = dispatch_source_create(
            type, 0, (nuint)(DispatchMemoryPressureNormal | DispatchMemoryPressureWarn | DispatchMemoryPressureCritical), queue);
        if (source == 0)
        {
            return 0;
        }

        dispatch_set_context(source, source);
        dispatch_source_set_event_handler_f(source, &OnPressureEvent);
        dispatch_resume(source);
        return source;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPressureEvent(nint context)
    {
        var data = dispatch_source_get_data(context);
        var level = (data & DispatchMemoryPressureCritical) != 0 ? MetalMemoryPressure.Critical
            : (data & DispatchMemoryPressureWarn) != 0 ? MetalMemoryPressure.Warn
            : MetalMemoryPressure.Normal;
        Volatile.Write(ref _lastEventLevel, (int)level);
        Interlocked.Increment(ref _pressureEvents);
    }

    [LibraryImport(LibSystem)]
    private static partial uint mach_host_self();

    [LibraryImport(LibSystem)]
    private static partial int host_statistics64(uint host, int flavor, VmStatistics64* info, ref int count);

    [LibraryImport(LibSystem)]
    private static partial int getpid();

    [LibraryImport(LibSystem)]
    private static partial int proc_pid_rusage(int pid, int flavor, void* buffer);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, void* oldValue, nuint* oldSize, void* newValue, nuint newSize);

    [LibraryImport(LibSystem)]
    private static partial nint dispatch_get_global_queue(long identifier, nuint flags);

    [LibraryImport(LibSystem)]
    private static partial nint dispatch_source_create(nint type, nuint handle, nuint mask, nint queue);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_set_context(nint obj, nint context);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_source_set_event_handler_f(nint source, delegate* unmanaged[Cdecl]<nint, void> handler);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_resume(nint obj);

    [LibraryImport(LibSystem)]
    private static partial nuint dispatch_source_get_data(nint source);
}
