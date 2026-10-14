// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

/// <summary>
/// A host thread created natively with a QoS class in its pthread attributes.
///
/// <c>pthread_set_qos_class_self_np</c> fails with EPERM on every thread CoreCLR
/// creates: the runtime passes an explicit scheduling parameter to
/// <c>pthread_create</c>, and Darwin then refuses later QoS changes on that thread.
/// Setting <c>pthread_attr_set_qos_class_np</c> on a thread we create ourselves is
/// the one route that works. The thread runs managed code through a reverse
/// P/Invoke; the runtime attaches and detaches it automatically.
/// </summary>
public sealed unsafe partial class HostQosThread
{
    private const int PthreadCreateDetached = 2;

    private readonly string _name;
    private readonly Action _body;
    private readonly ManualResetEventSlim _exited = new(false);
    private volatile Thread? _managedThread;

    private HostQosThread(string name, Action body)
    {
        _name = name;
        _body = body;
    }

    /// <summary>The managed identity of the running thread; null until it starts.</summary>
    public Thread? ManagedThread => _managedThread;

    public bool IsCurrentThread => ReferenceEquals(Thread.CurrentThread, _managedThread);

    public bool Join(int milliseconds) => _exited.Wait(milliseconds);

    /// <summary>Starts <paramref name="body"/> on a new native thread at <paramref name="qos"/>.
    /// Returns null (and sets <paramref name="errno"/>) when the host cannot do so.</summary>
    public static HostQosThread? TryStart(string name, HostThreadQos qos, int stackBytes, Action body, out int errno)
    {
        errno = -1;
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var thread = new HostQosThread(name, body);
        var handle = GCHandle.Alloc(thread);
        byte* attr = stackalloc byte[128];
        try
        {
            errno = pthread_attr_init(attr);
            if (errno != 0)
            {
                handle.Free();
                return null;
            }

            _ = pthread_attr_setstacksize(attr, (nuint)stackBytes);
            _ = pthread_attr_setdetachstate(attr, PthreadCreateDetached);
            errno = pthread_attr_set_qos_class_np(attr, HostThreadQualityOfService.ToDarwinClass(qos), 0);
            if (errno == 0)
            {
                errno = pthread_create(out _, attr, &Entry, GCHandle.ToIntPtr(handle));
            }

            _ = pthread_attr_destroy(attr);
        }
        catch (Exception)
        {
            errno = -1;
        }

        if (errno != 0)
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }

            return null;
        }

        return thread;
    }

    [UnmanagedCallersOnly]
    private static nint Entry(nint arg)
    {
        var handle = GCHandle.FromIntPtr(arg);
        var self = (HostQosThread)handle.Target!;
        handle.Free();
        var current = Thread.CurrentThread;
        try
        {
            current.Name = self._name;
        }
        catch (InvalidOperationException)
        {
        }

        self._managedThread = current;
        try
        {
            self._body();
        }
        finally
        {
            self._exited.Set();
        }

        return 0;
    }

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_attr_init(byte* attr);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_attr_destroy(byte* attr);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_attr_setstacksize(byte* attr, nuint size);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_attr_setdetachstate(byte* attr, int state);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_attr_set_qos_class_np(byte* attr, uint qosClass, int relativePriority);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_create(out nint thread, byte* attr, delegate* unmanaged<nint, nint> start, nint arg);
}
