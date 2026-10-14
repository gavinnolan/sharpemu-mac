// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.DsTrace;

/// <summary>Darwin entry points used by DSTRACE (same LibraryImport style as HostThreadQos).</summary>
internal static unsafe partial class DsNative
{
    private const string LibSystem = "libSystem.B.dylib";
    private const string QuartzCore = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";

    internal const int RusageInfoV2 = 2;
    internal const int RusageInfoV6 = 6;
    internal const int ThreadBasicInfo = 3;
    internal const uint ThreadBasicInfoCount = 10;
    internal const int ThreadIdentifierInfo = 4;
    internal const uint ThreadIdentifierInfoCount = 6;
    internal const int ProcPidThreadId64Info = 15;
    internal const int ThStateRunning = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SchedParam
    {
        public int SchedPriority;
        public int Opaque;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MachTimebaseInfo
    {
        public uint Numer;
        public uint Denom;
    }

    /// <summary>struct thread_basic_info from &lt;mach/thread_info.h&gt;.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ThreadBasicInfoData
    {
        public int UserSeconds;
        public int UserMicroseconds;
        public int SystemSeconds;
        public int SystemMicroseconds;
        public int CpuUsage;
        public int Policy;
        public int RunState;
        public int Flags;
        public int SuspendCount;
        public int SleepTime;
    }

    /// <summary>struct thread_identifier_info from &lt;mach/thread_info.h&gt;.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ThreadIdentifierInfoData
    {
        public ulong ThreadId;
        public ulong ThreadHandle;
        public ulong DispatchQaddr;
    }

    /// <summary>struct proc_threadinfo from &lt;sys/proc_info.h&gt;.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcThreadInfo
    {
        public ulong UserTime;
        public ulong SystemTime;
        public int CpuUsage;
        public int Policy;
        public int RunState;
        public int Flags;
        public int SleepTime;
        public int CurPri;
        public int Priority;
        public int MaxPriority;
        public fixed byte Name[64];
    }

    /// <summary>
    /// struct rusage_info_v6, copied field for field from
    /// $(xcrun --show-sdk-path)/usr/include/sys/resource.h (macOS SDK, Darwin 27).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RusageInfoV6Data
    {
        public fixed byte ri_uuid[16];
        public ulong ri_user_time;
        public ulong ri_system_time;
        public ulong ri_pkg_idle_wkups;
        public ulong ri_interrupt_wkups;
        public ulong ri_pageins;
        public ulong ri_wired_size;
        public ulong ri_resident_size;
        public ulong ri_phys_footprint;
        public ulong ri_proc_start_abstime;
        public ulong ri_proc_exit_abstime;
        public ulong ri_child_user_time;
        public ulong ri_child_system_time;
        public ulong ri_child_pkg_idle_wkups;
        public ulong ri_child_interrupt_wkups;
        public ulong ri_child_pageins;
        public ulong ri_child_elapsed_abstime;
        public ulong ri_diskio_bytesread;
        public ulong ri_diskio_byteswritten;
        public ulong ri_cpu_time_qos_default;
        public ulong ri_cpu_time_qos_maintenance;
        public ulong ri_cpu_time_qos_background;
        public ulong ri_cpu_time_qos_utility;
        public ulong ri_cpu_time_qos_legacy;
        public ulong ri_cpu_time_qos_user_initiated;
        public ulong ri_cpu_time_qos_user_interactive;
        public ulong ri_billed_system_time;
        public ulong ri_serviced_system_time;
        public ulong ri_logical_writes;
        public ulong ri_lifetime_max_phys_footprint;
        public ulong ri_instructions;
        public ulong ri_cycles;
        public ulong ri_billed_energy;
        public ulong ri_serviced_energy;
        public ulong ri_interval_max_phys_footprint;
        public ulong ri_runnable_time;
        public ulong ri_flags;
        public ulong ri_user_ptime;
        public ulong ri_system_ptime;
        public ulong ri_pinstructions;
        public ulong ri_pcycles;
        public ulong ri_energy_nj;
        public ulong ri_penergy_nj;
        public ulong ri_secure_time_in_system;
        public ulong ri_secure_ptime_in_system;
        public ulong ri_neural_footprint;
        public ulong ri_lifetime_max_neural_footprint;
        public ulong ri_interval_max_neural_footprint;
        public ulong ri_conclave_footprint;
        public ulong ri_page_wait_time_mach;
        public ulong ri_page_cache_hits;
        public fixed ulong ri_reserved[6];
    }

    [LibraryImport(LibSystem)]
    internal static partial nint pthread_self();

    [LibraryImport(LibSystem)]
    internal static partial uint pthread_mach_thread_np(nint thread);

    [LibraryImport(LibSystem)]
    internal static partial int pthread_threadid_np(nint thread, out ulong threadId);

    [LibraryImport(LibSystem)]
    internal static partial int pthread_get_qos_class_np(nint thread, out uint qosClass, out int relativePriority);

    [LibraryImport(LibSystem)]
    internal static partial int pthread_getschedparam(nint thread, out int policy, out SchedParam param);

    [LibraryImport(LibSystem)]
    internal static partial ulong clock_gettime_nsec_np(int clockId);

    [LibraryImport(LibSystem)]
    internal static partial ulong mach_absolute_time();

    [LibraryImport(LibSystem)]
    internal static partial int mach_timebase_info(out MachTimebaseInfo info);

    [LibraryImport(LibSystem)]
    internal static partial int nanosleep(Timespec* request, Timespec* remaining);

    [LibraryImport(LibSystem)]
    internal static partial int proc_pid_rusage(int pid, int flavor, void* buffer);

    [LibraryImport(LibSystem)]
    internal static partial int proc_listallpids(void* buffer, int bufferSize);

    [LibraryImport(LibSystem)]
    internal static partial int proc_name(int pid, void* buffer, uint bufferSize);

    [LibraryImport(LibSystem)]
    internal static partial int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int bufferSize);

    [LibraryImport(LibSystem)]
    internal static partial int task_threads(uint task, out nint threads, out uint count);

    [LibraryImport(LibSystem)]
    internal static partial int thread_info(uint thread, int flavor, void* info, ref uint count);

    [LibraryImport(LibSystem)]
    internal static partial int mach_port_deallocate(uint task, uint name);

    [LibraryImport(LibSystem)]
    internal static partial int vm_deallocate(uint task, nuint address, nuint size);

    [LibraryImport(LibSystem)]
    internal static partial int getloadavg(double* loads, int count);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sysctlbyname(string name, void* oldValue, ref nuint oldLength, nint newValue, nuint newLength);

    [LibraryImport(QuartzCore)]
    internal static partial double CACurrentMediaTime();

    private static uint _taskSelf;

    /// <summary>mach_task_self() is a macro over the mach_task_self_ variable; read the variable.</summary>
    internal static uint TaskSelf
    {
        get
        {
            if (_taskSelf != 0)
            {
                return _taskSelf;
            }

            var lib = NativeLibrary.Load(LibSystem);
            _taskSelf = *(uint*)NativeLibrary.GetExport(lib, "mach_task_self_");
            return _taskSelf;
        }
    }

    internal static void SleepNs(long ns)
    {
        if (ns <= 0)
        {
            return;
        }

        var request = new Timespec { Seconds = ns / 1_000_000_000L, Nanoseconds = ns % 1_000_000_000L };
        _ = nanosleep(&request, null);
    }

    internal static long SysctlLong(string name)
    {
        long value = 0;
        nuint length = sizeof(long);
        return sysctlbyname(name, &value, ref length, 0, 0) == 0 ? value : 0;
    }
}
