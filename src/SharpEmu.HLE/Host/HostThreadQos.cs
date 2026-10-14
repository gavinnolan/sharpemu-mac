// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

/// <summary>
/// The scheduling class a thread asks the host OS for. Named after Darwin's
/// <c>qos_class_t</c> because macOS is the only host that acts on it today, but
/// the concept — "how much does latency matter on this thread" — is portable and
/// the setter is a no-op anywhere else.
/// </summary>
public enum HostThreadQos
{
    /// <summary>Frame-deadline work: presenter/render, GPU submission, the guest's
    /// own main thread, frame pacing. Gets the shortest scheduling latency and, on
    /// macOS, is the class that exempts a thread from timer coalescing.</summary>
    UserInteractive,

    /// <summary>Work the user is waiting on but that has no frame deadline: image
    /// loading, shader and SRT compilation.</summary>
    UserInitiated,

    /// <summary>Whatever the runtime picked. Used to say "deliberately unset".</summary>
    Default,

    /// <summary>Long-running background work that nobody is waiting on.</summary>
    Utility,
}

/// <summary>
/// Applies a quality-of-service class to the calling thread.
///
/// On macOS this matters more than it would elsewhere. Threads created by the
/// CoreCLR thread pool and by <c>new Thread(...)</c> inherit the creating thread's
/// QoS, which for a process started from Finder or a shell is
/// <c>QOS_CLASS_DEFAULT</c> at best — and anything below
/// <c>QOS_CLASS_USER_INTERACTIVE</c> is subject to timer coalescing and to being
/// descheduled behind unrelated user-interactive work. On Apple silicon the class
/// is also what steers a thread towards the P or E cores, so a presenter thread
/// left at the default can end up pacing frames on an efficiency core.
///
/// <see cref="Thread.Priority"/> is not an alternative: CoreCLR's Unix PAL does not
/// implement thread priorities, so setting it is already a no-op on this host. That
/// also means applying a QoS class here cannot fight an existing priority.
/// </summary>
/// <remarks>
/// <c>pthread_set_qos_class_self_np</c> only ever applies to the calling thread —
/// Darwin has no documented way to set another thread's class — so every call site
/// is inside the thread body it is describing, not next to the
/// <c>new Thread(...)</c> that created it.
/// </remarks>
public static partial class HostThreadQualityOfService
{
    // qos_class_t, from <sys/qos.h>.
    private const uint QosClassUserInteractive = 0x21;
    private const uint QosClassUserInitiated = 0x19;
    private const uint QosClassDefault = 0x15;
    private const uint QosClassUtility = 0x11;

    /// <summary>
    /// Asks the host to schedule the calling thread at <paramref name="qos"/>.
    /// Returns true when the class was applied. False means the host has no such
    /// concept (every non-macOS host) or rejected the request; either way the
    /// thread keeps running at whatever class it inherited, so no caller needs to
    /// handle the failure.
    /// </summary>
    public static bool TryApplyToCurrentThread(HostThreadQos qos)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var qosClass = qos switch
        {
            HostThreadQos.UserInteractive => QosClassUserInteractive,
            HostThreadQos.UserInitiated => QosClassUserInitiated,
            HostThreadQos.Utility => QosClassUtility,
            _ => QosClassDefault,
        };

        try
        {
            // relative_priority is 0: the only legal values are 0 and negative
            // offsets within the class, and nothing here wants to sit below its
            // own class's floor.
            return pthread_set_qos_class_self_np(qosClass, 0) == 0;
        }
        catch (Exception)
        {
            // An older or unusual libSystem without the _np entry point. Scheduling
            // hints are an optimisation; never let one abort a thread's startup.
            return false;
        }
    }

    /// <summary>Raw qos_class_t of the calling thread (diagnostics only); 0 off macOS.</summary>
    public static uint GetCurrentThreadClassForDiagnostics()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return 0;
        }

        try
        {
            return qos_class_self();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

    [LibraryImport("libSystem.B.dylib")]
    private static partial uint qos_class_self();
}
