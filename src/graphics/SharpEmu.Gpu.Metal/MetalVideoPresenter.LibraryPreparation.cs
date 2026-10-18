// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private sealed class LibraryDeviceOwner(nint device)
    {
        internal readonly nint Device = device;
        internal int References = 1;
        internal bool Retired;
    }
    private sealed class LibraryDeviceLease(LibraryDeviceOwner owner) : IDisposable
    {
        internal readonly LibraryDeviceOwner Owner = owner;
        private bool _released;
        public void Dispose()
        {
            nint release = 0;
            lock (_gate)
            {
                if (_released) return;
                _released = true;
                if (--Owner.References == 0) release = Owner.Device;
            }
            if (release != 0) MetalNative.SendVoid(release, MetalNative.Selector("release"));
        }
    }
    private static LibraryDeviceOwner? _libraryDeviceOwner;
    // Managed-only controls may replace this callback; native default remains null.
    private static Action<MetalCompiledGuestShader, uint?>? _prepareLibraryOverride = null;

    private static readonly object _libraryDeviceInitializationGate = new();
    private static bool _libraryDeviceAdmissionClosed;

    // Window-independent initialization. Native creation/compilation never holds
    // the presenter gate, and rendering leases this exact shared device owner.
    private static void EnsureLibraryPreparationDevice()
    {
        lock (_libraryDeviceInitializationGate)
        {
            lock (_gate)
            {
                RequireLibraryDeviceAdmissionLocked();
                if (_libraryDeviceOwner is not null) return;
            }
            nint device = 0, pool = 0;
            Exception? first = null;
            try
            {
                MetalNative.EnsureFrameworksLoaded();
                pool = MetalNative.objc_autoreleasePoolPush();
                device = MetalNative.MTLCreateSystemDefaultDevice(); // SDK: NS_RETURNS_RETAINED.
                if (device == 0) throw new InvalidOperationException("Metal library preparation has no available native device.");
                var owner = new LibraryDeviceOwner(device);
                lock (_gate)
                {
                    RequireLibraryDeviceAdmissionLocked();
                    _libraryDeviceOwner = owner;
                    device = 0; // Exact owned retain transferred only after successful publication.
                    Monitor.PulseAll(_gate);
                }
            }
            catch (Exception failure) { first = failure; }
            finally
            {
                if (device != 0)
                    try { MetalNative.SendVoid(device, MetalNative.Selector("release")); }
                    catch (Exception cleanup) { RecordCleanup(ref first, cleanup); }
                if (pool != 0)
                    try { MetalNative.objc_autoreleasePoolPop(pool); }
                    catch (Exception cleanup) { RecordCleanup(ref first, cleanup); }
            }
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    private static void RequireLibraryDeviceAdmissionLocked()
    {
        if (_guestWorkFailure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        if (_closed || _libraryDeviceAdmissionClosed)
            throw new OperationCanceledException("Metal shader preparation device admission is closed.");
    }

    private static void RetireLibraryPreparationDevice()
    {
        nint release = 0;
        lock (_gate)
        {
            _libraryDeviceAdmissionClosed = true;
            if (_libraryDeviceOwner is { } owner)
            {
                _libraryDeviceOwner = null; owner.Retired = true;
                if (--owner.References == 0) release = owner.Device;
            }
            Monitor.PulseAll(_gate);
        }
        if (release != 0) MetalNative.SendVoid(release, MetalNative.Selector("release"));
    }

    private static LibraryDeviceLease AcquireLibraryPreparationDevice()
    {
        EnsureLibraryPreparationDevice();
        lock (_gate)
        {
            RequireLibraryDeviceAdmissionLocked();
            if (_libraryDeviceOwner is not { Retired: false } owner)
                throw new OperationCanceledException("Metal shader preparation has no live initialized device owner.");
            var lease = new LibraryDeviceLease(owner);
            owner.References = checked(owner.References + 1);
            return lease;
        }
    }

    private static void RecordCleanup(ref Exception? first, Exception cleanup)
    {
        if (first is null) first = cleanup;
        else AddLibraryPreparationCleanup(first, cleanup);
    }

    private static void RequireLibraryPreparationCurrentLocked(LibraryDeviceLease lease)
    {
        if (_guestWorkFailure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        if (_closed || _libraryDeviceAdmissionClosed || lease.Owner.Retired || !ReferenceEquals(_libraryDeviceOwner, lease.Owner))
            throw new OperationCanceledException("Metal shader preparation lost its exact initialized device owner.");
    }

    internal static void PrepareGuestShaderLibrary(MetalCompiledGuestShader shader, uint? fixedVertexAttributes = null)
    {
        if (_prepareLibraryOverride is { } prepare) { prepare(shader, fixedVertexAttributes); return; }
        TraceLibraryPreparation("begin", shader);
        var lease = AcquireLibraryPreparationDevice();
        nint pool = 0;
        Exception? first = null;
        try
        {
            pool = MetalNative.objc_autoreleasePoolPush();
            if (GetOrCreateShaderLibrary(lease.Owner.Device, shader, lease) == 0)
                throw new InvalidOperationException($"Metal cold library preparation failed: entry={shader.Shader.EntryPoint}.");
            if (fixedVertexAttributes is { } attributes)
            {
                var function = GetFixedFullscreenVertexFunction(lease.Owner.Device, attributes, lease);
                if (function == 0) throw new InvalidOperationException("Metal fixed vertex library preparation failed.");
                MetalNative.SendVoid(function, MetalNative.Selector("release"));
            }
            lock (_gate) RequireLibraryPreparationCurrentLocked(lease);
        }
        catch (Exception error) { first = error; }
        finally
        {
            if (pool != 0)
                try { MetalNative.objc_autoreleasePoolPop(pool); } catch (Exception error) { if (first is null) first = error; else AddLibraryPreparationCleanup(first, error); }
            try { lease.Dispose(); } catch (Exception error) { if (first is null) first = error; else AddLibraryPreparationCleanup(first, error); }
        }
        if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        TraceLibraryPreparation("finished", shader);
    }

    private static int _libraryPreparationTraceLines;
    private static void TraceLibraryPreparation(string phase, MetalCompiledGuestShader shader)
    {
        if (!_commandMemoryTraceEnabled || Interlocked.Increment(ref _libraryPreparationTraceLines) > 128) return;
        try
        {
            var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
            Console.Error.WriteLine($"[METAL][LIBRARY_PREPARATION] monotonic_ticks={System.Diagnostics.Stopwatch.GetTimestamp()} tick_frequency={System.Diagnostics.Stopwatch.Frequency} phase={phase} queue='{queue.Name}' submission={queue.SubmissionId} shader_stage={shader.Shader.Stage} entry={shader.Shader.EntryPoint}");
        }
        catch { }
    }

    private static void AddLibraryPreparationCleanup(Exception primary, Exception cleanup)
    {
        try
        {
            if (primary.Data["Metal.LibraryPreparationCleanup"] is Exception prior)
                primary.Data["Metal.LibraryPreparationCleanup"] = new AggregateException(prior, cleanup);
            else primary.Data["Metal.LibraryPreparationCleanup"] = cleanup;
        }
        catch { }
    }

    private static nint GetOrCreateShaderLibrary(nint device, MetalCompiledGuestShader shader, LibraryDeviceLease? preparation = null)
    {
        lock (shader.LibraryGate)
        {
            if (shader.CachedLibrary != 0)
            {
                if (shader.LibraryDevice != device)
                    throw new InvalidOperationException("Metal shader library belongs to a different initialized device.");
                if (preparation is not null)
                {
                    lock (_gate) RequireLibraryPreparationCurrentLocked(preparation);
                    TraceLibraryPreparation("cache_hit", shader);
                }
                return shader.CachedLibrary;
            }
            if (preparation is not null) TraceLibraryPreparation("compile_begin", shader);
            if (!TryCompileLibrary(device, shader.AcquireShader().Source, out var library, out var error, traceGuestWork: preparation is null))
            {
                Console.Error.WriteLine($"[LOADER][WARN] Metal shader compile failed: {error}");
                return 0;
            }
            if (preparation is not null) TraceLibraryPreparation("compile_end", shader);
            Exception? first = null;
            var transferred = false;
            try
            {
                if (preparation is null)
                { shader.LibraryDevice = device; shader.CachedLibrary = library; transferred = true; }
                else lock (_gate)
                {
                    RequireLibraryPreparationCurrentLocked(preparation);
                    shader.LibraryDevice = device; shader.CachedLibrary = library; transferred = true;
                }
                return library;
            }
            catch (Exception failure) { first = failure; throw; }
            finally
            {
                if (!transferred)
                    try { MetalNative.SendVoid(library, MetalNative.Selector("release")); }
                    catch (Exception cleanup)
                    { if (first is null) throw; AddLibraryPreparationCleanup(first, cleanup); }
            }
        }
    }
}
