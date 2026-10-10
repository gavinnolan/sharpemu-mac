// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Guest memory as the GPU sees it. The guest's direct-memory backing is one
/// contiguous host alias, so a handful of <c>newBufferWithBytesNoCopy</c> windows
/// cover all of it: every vertex, index, constant, storage and indirect binding is
/// then a (window, offset) pair and no guest byte is ever copied.
///
/// A range that is not backed, or that straddles two windows, falls back to a
/// per-command-buffer upload ring. The windows and the live ring buffers sit in an
/// <c>MTLResidencySet</c> attached to the queue, so no encoder needs
/// <c>useResource:</c> for them.
/// </summary>
internal sealed class MetalGuestMemory : IDisposable
{
    // Four 4 GiB windows cover the 16 GiB of direct memory; the flexible tail adds a
    // fifth. maxBufferLength caps a single window at about 13.6 GB on this hardware.
    private const ulong PreferredWindowBytes = 4UL * 1024 * 1024 * 1024;
    private const int MaxWindows = 16;
    private const ulong RingBufferBytes = 32UL * 1024 * 1024;
    private const ulong RingAlignment = 256;
    public const ulong GdsBytes = ManagedCommandStreamHost.GdsBytes;

    // Hazard tracking on a 4 GiB window makes Metal order and fence every command
    // buffer that touches it, which measured at tens of milliseconds per submission
    // because every submission touches a window. Untracked is the design's choice:
    // the guest's own barriers end the encoder, and encoders on one queue run in
    // order. SHARPEMU_METAL_V2_TRACKED=1 restores automatic tracking for comparison.
    // An MTLResidencySet would keep the windows mapped for the queue without any
    // per-encoder call, which is what the design asked for — but a set holding the
    // whole 16 GiB backing measured about seventy milliseconds of scheduling
    // latency on every submission, with the GPU itself idle: Dreaming Sarah ran at
    // five frames a second with it and at a hundred and twenty without. Naming the
    // windows per encoder costs five useResource calls instead.
    // SHARPEMU_METAL_V2_RESIDENCY_SET=1 restores the set for comparison.
    private static readonly bool UseResidencySet = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_RESIDENCY_SET"), "1", StringComparison.Ordinal);

    private static readonly bool StageEverything = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_STAGE_ALL"), "1", StringComparison.Ordinal);

    private static readonly nuint HazardTrackingMode =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_TRACKED"), "1", StringComparison.Ordinal)
            ? 0
            : Mtl.ResourceHazardTrackingModeUntracked;

    private static readonly nint SelNewBufferNoCopy =
        MetalNative.Selector("newBufferWithBytesNoCopy:length:options:deallocator:");
    private static readonly nint SelNewBuffer = MetalNative.Selector("newBufferWithLength:options:");
    private static readonly nint SelGpuAddress = MetalNative.Selector("gpuAddress");
    private static readonly nint SelContents = MetalNative.Selector("contents");
    private static readonly nint SelMaxBufferLength = MetalNative.Selector("maxBufferLength");
    private static readonly nint SelSetLabel = MetalNative.Selector("setLabel:");
    private static readonly nint SelRelease = MetalNative.Selector("release");
    private static readonly nint SelAddAllocation = MetalNative.Selector("addAllocation:");
    private static readonly nint SelCommit = MetalNative.Selector("commit");

    // One no-copy MTLBuffer over a slice of the backing alias.
    private readonly struct Window
    {
        public Window(nint buffer, ulong aliasBase, ulong size, ulong gpuAddress)
        {
            Buffer = buffer;
            AliasBase = aliasBase;
            Size = size;
            GpuAddress = gpuAddress;
        }

        public nint Buffer { get; }

        // Host pointer of the window's first byte.
        public ulong AliasBase { get; }

        public ulong Size { get; }

        public ulong GpuAddress { get; }
    }

    // A ring buffer of host-written bytes: valid until the submission that used it completes.
    private sealed class RingBuffer
    {
        public nint Buffer;
        public ulong GpuAddress;
        public nint Contents;
        public ulong Size;
        public ulong Cursor;
        public ulong LastUsedSerial;
    }

    private readonly nint _device;
    private readonly IGuestBackedSpace _backing;
    private readonly ICpuMemory _memory;
    private readonly Window[] _windows;
    private readonly ulong _windowStride;
    private readonly int _windowCount;
    private readonly ulong _aliasBase;
    private readonly ulong _aliasSize;
    private readonly List<RingBuffer> _rings = new();
    private readonly nint _residencySet;
    private readonly nint _gdsBuffer;
    private readonly nint _nullBuffer;
    private readonly nint _nullContents;
    private ulong _submissionSerial;
    private ulong _completedSerial;
    private long _ringFallbackBytes;
    private long _ringFallbacks;
    private bool _disposed;

    public MetalGuestMemory(nint device, IGuestBackedSpace backing, ICpuMemory memory)
    {
        _device = device;
        _backing = backing;
        _memory = memory;
        var (aliasBase, aliasSize) = backing.BackingAliasRange;
        if (aliasBase == 0 || aliasSize == 0)
        {
            throw GpuFatal.Fatal("Metal v2 needs the guest backing alias; the address space reports none.");
        }

        _aliasBase = aliasBase;
        _aliasSize = aliasSize;
        var maxBufferLength = (ulong)MetalV2Native.SendValue(device, SelMaxBufferLength);
        var windowBytes = Math.Min(PreferredWindowBytes, maxBufferLength == 0 ? PreferredWindowBytes : maxBufferLength);
        // Windows overlap by half: window k starts at k * stride, so any range of up to
        // one stride lies wholly inside the window its first byte selects, and a range
        // crossing a window edge never falls back to a copy.
        var windowStride = windowBytes / 2;
        var windowCount = aliasSize <= windowBytes
            ? 1
            : (int)((aliasSize - windowBytes + windowStride - 1) / windowStride) + 1;
        if (windowCount > MaxWindows)
        {
            throw GpuFatal.Fatal(
                $"The guest backing needs more Metal windows than v2 maps: windows={windowCount} max={MaxWindows} " +
                $"size=0x{aliasSize:X} windowBytes=0x{windowBytes:X}.");
        }

        _residencySet = UseResidencySet ? CreateResidencySet(device) : 0;
        _windows = new Window[windowCount];
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var index = 0; index < windowCount; index++)
        {
            var offset = (ulong)index * windowStride;
            var size = Math.Min(windowBytes, aliasSize - offset);
            var pointer = aliasBase + offset;
            var buffer = MetalV2Native.SendNewBufferNoCopy(
                device,
                SelNewBufferNoCopy,
                (nint)pointer,
                (nuint)size,
                Mtl.ResourceStorageModeShared | HazardTrackingMode,
                0);
            if (buffer == 0)
            {
                throw GpuFatal.Fatal(
                    $"A guest memory window could not be mapped: window={index} pointer=0x{pointer:X16} size=0x{size:X}.");
            }

            SetLabel(buffer, $"guest.window[{index}]");
            _windows[index] = new Window(buffer, pointer, size, MetalNative.SendGpuAddress(buffer, SelGpuAddress));
            AddResident(buffer);
        }

        _windowCount = windowCount;
        _windowStride = windowStride;
        _gdsBuffer = CreateSharedBuffer(GdsBytes, "guest.gds", out _, out _);
        _nullBuffer = CreateSharedBuffer(256, "guest.null", out _nullContents, out _);
        unsafe
        {
            new Span<byte>((void*)_nullContents, 256).Clear();
        }

        CommitResidency();
        var seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) /
            (double)System.Diagnostics.Stopwatch.Frequency;
        var mapSeconds = seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        Console.Error.WriteLine(
            $"[METAL-V2][INFO] guest memory mapped windows={windowCount} window_bytes=0x{windowBytes:X} " +
            $"alias=0x{aliasBase:X16}+0x{aliasSize:X} map_seconds={mapSeconds} residency_set={(_residencySet != 0 ? 1 : 0)}");
    }

    public nint Device => _device;

    public nint GdsBuffer => _gdsBuffer;

    public nint NullBuffer => _nullBuffer;

    public nint ResidencySet => _residencySet;

    public ulong RingFallbackBytes => (ulong)Volatile.Read(ref _ringFallbackBytes);

    public ulong RingFallbacks => (ulong)Volatile.Read(ref _ringFallbacks);

    /// <summary>The serial a resource allocated now is retired at.</summary>
    public void BeginSubmission(ulong submissionSerial, ulong completedSerial)
    {
        _submissionSerial = submissionSerial;
        _completedSerial = completedSerial;
    }

    public void NoteCompleted(ulong completedSerial) => _completedSerial = Math.Max(_completedSerial, completedSerial);

    /// <summary>
    /// The GPU view of a guest byte range. A backed range inside one window needs no
    /// copy; anything else is staged into the upload ring, so the bytes the GPU reads
    /// are the bytes the CPU can see right now.
    /// </summary>
    public MetalGuestRange Resolve(ulong gpuVa, ulong size)
    {
        if (size == 0 || gpuVa == 0 || size > ulong.MaxValue - gpuVa)
        {
            return default;
        }

        if (!StageEverything && TryResolveWindow(gpuVa, size, out var resolved))
        {
            return resolved;
        }

        return Upload(gpuVa, size);
    }

    /// <summary>The window range of an address that must be written by the GPU; zero when it is not backed.</summary>
    public MetalGuestRange ResolveWritable(ulong gpuVa, ulong size) =>
        TryResolveWindow(gpuVa, size, out var resolved) ? resolved : default;

    private bool TryResolveWindow(ulong gpuVa, ulong size, out MetalGuestRange resolved)
    {
        resolved = default;
        if (size == 0 || gpuVa == 0 || size > ulong.MaxValue - gpuVa ||
            !_backing.TryResolveBackingAlias(gpuVa, size, out var alias) ||
            alias < _aliasBase || alias - _aliasBase >= _aliasSize)
        {
            return false;
        }

        var index = (int)Math.Min((alias - _aliasBase) / _windowStride, (ulong)(_windowCount - 1));
        ref readonly var window = ref _windows[index];
        var offset = alias - window.AliasBase;
        if (alias < window.AliasBase || offset >= window.Size || size > window.Size - offset)
        {
            return false;
        }

        resolved = new MetalGuestRange(window.Buffer, offset, size, window.GpuAddress + offset);
        return true;
    }

    /// <summary>Copies guest bytes into the ring; used for ranges no window covers.</summary>
    private MetalGuestRange Upload(ulong gpuVa, ulong size)
    {
        var range = Allocate(size, RingAlignment, out var span);
        span.Clear();
        if (!_memory.TryRead(gpuVa, span))
        {
            // A partially mapped range still binds: the readable prefix is what the
            // guest shader would have seen, and the rest reads as zero.
            for (var offset = 0UL; offset < size; offset += 4096)
            {
                var chunk = (int)Math.Min(4096, size - offset);
                if (!_memory.TryRead(gpuVa + offset, span.Slice((int)offset, chunk)))
                {
                    break;
                }
            }
        }

        Interlocked.Add(ref _ringFallbackBytes, (long)size);
        Interlocked.Increment(ref _ringFallbacks);
        return range;
    }

    /// <summary>Reserves ring bytes and hands back the host span to fill.</summary>
    public MetalGuestRange Allocate(ulong size, ulong alignment, out Span<byte> span)
    {
        if (size == 0)
        {
            throw GpuFatal.Fatal("A guest memory ring allocation needs a nonzero size.");
        }

        var ring = Reserve(size, Math.Max(alignment, 16), out var offset);
        ring.LastUsedSerial = _submissionSerial;
        unsafe
        {
            span = new Span<byte>((void*)(ring.Contents + (nint)offset), checked((int)size));
        }

        return new MetalGuestRange(ring.Buffer, offset, size, ring.GpuAddress + offset);
    }

    private RingBuffer Reserve(ulong size, ulong alignment, out ulong offset)
    {
        var capacity = Math.Max(RingBufferBytes, (size + alignment) * 2);
        for (var index = 0; index < _rings.Count; index++)
        {
            var candidate = _rings[index];
            var aligned = (candidate.Cursor + alignment - 1) & ~(alignment - 1);
            if (aligned <= candidate.Size && size <= candidate.Size - aligned)
            {
                candidate.Cursor = aligned + size;
                offset = aligned;
                return candidate;
            }
        }

        // Recycle a ring the GPU is done with before growing the pool.
        for (var index = 0; index < _rings.Count; index++)
        {
            var candidate = _rings[index];
            if (candidate.LastUsedSerial > _completedSerial || size > candidate.Size)
            {
                continue;
            }

            candidate.Cursor = size;
            offset = 0;
            return candidate;
        }

        var buffer = CreateSharedBuffer(capacity, $"guest.ring[{_rings.Count}]", out var contents, out var gpuAddress);
        var ring = new RingBuffer
        {
            Buffer = buffer,
            Contents = contents,
            GpuAddress = gpuAddress,
            Size = capacity,
            Cursor = size,
        };
        _rings.Add(ring);
        AddResident(buffer);
        CommitResidency();
        offset = 0;
        return ring;
    }

    /// <summary>The host span of a ring range this submission allocated.</summary>
    public unsafe Span<byte> RingSpan(in MetalGuestRange range)
    {
        foreach (var ring in _rings)
        {
            if (ring.Buffer == range.Buffer)
            {
                return new Span<byte>((void*)(ring.Contents + (nint)range.Offset), checked((int)range.Size));
            }
        }

        return default;
    }

    public unsafe Span<byte> GdsWords()
    {
        var contents = MetalNative.Send(_gdsBuffer, SelContents);
        return new Span<byte>((void*)contents, (int)GdsBytes);
    }

    private nint CreateSharedBuffer(ulong size, string label, out nint contents, out ulong gpuAddress)
    {
        var buffer = MetalNative.SendNewBuffer(
            _device,
            SelNewBuffer,
            (nuint)size,
            Mtl.ResourceStorageModeShared | HazardTrackingMode);
        if (buffer == 0)
        {
            throw GpuFatal.Fatal($"A Metal buffer could not be created: label={label} size=0x{size:X}.");
        }

        SetLabel(buffer, label);
        contents = MetalNative.Send(buffer, SelContents);
        gpuAddress = MetalNative.SendGpuAddress(buffer, SelGpuAddress);
        return buffer;
    }

    private static void SetLabel(nint resource, string label)
    {
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            MetalNative.SendVoid(resource, SelSetLabel, MetalNative.NsString(label));
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    // MTLResidencySet keeps the windows mapped for the queue without per-encoder calls.
    // macOS before 15 has no residency set; the fallback is useResource on each encoder.
    private static nint CreateResidencySet(nint device)
    {
        var selector = MetalNative.Selector("newResidencySetWithDescriptor:error:");
        if (!MetalNative.SendBool(device, MetalNative.Selector("respondsToSelector:"), selector))
        {
            Console.Error.WriteLine("[METAL-V2][WARN] MTLResidencySet is unavailable; windows bind per encoder.");
            return 0;
        }

        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            var descriptorClass = MetalNative.Class("MTLResidencySetDescriptor");
            var descriptor = MetalNative.Send(
                MetalNative.Send(descriptorClass, MetalNative.Selector("alloc")),
                MetalNative.Selector("init"));
            nint error = 0;
            var set = MetalNative.Send(device, selector, descriptor, ref error);
            MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
            if (set == 0)
            {
                Console.Error.WriteLine(
                    $"[METAL-V2][WARN] MTLResidencySet creation failed: {MetalNative.DescribeError(error)}");
            }

            return set;
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    public void AddResident(nint allocation)
    {
        if (_residencySet != 0 && allocation != 0)
        {
            MetalNative.SendVoid(_residencySet, SelAddAllocation, allocation);
            return;
        }

        NoteResident(allocation);
    }

    public void CommitResidency()
    {
        if (_residencySet == 0)
        {
            return;
        }

        // Commit publishes the allocation list; requesting residency once keeps the
        // windows mapped for the queue instead of letting Metal establish residency
        // around every command buffer. The pages stay lazily backed either way.
        MetalNative.SendVoid(_residencySet, SelCommit);
        MetalNative.SendVoid(_residencySet, MetalNative.Selector("requestResidency"));
    }

    /// <summary>Attaches the residency set to a queue, once per queue.</summary>
    public void AttachToQueue(nint queue)
    {
        if (_residencySet == 0 || queue == 0)
        {
            return;
        }

        var selector = MetalNative.Selector("addResidencySet:");
        if (MetalNative.SendBool(queue, MetalNative.Selector("respondsToSelector:"), selector))
        {
            MetalNative.SendVoid(queue, selector, _residencySet);
        }
    }

    /// <summary>
    /// The window and ring buffers an encoder has to be told about when there is no
    /// residency set: an argument buffer holds their addresses, not references, so
    /// Metal cannot see the dependency on its own.
    /// </summary>
    public ReadOnlySpan<nint> ResidentBuffers => _residentBuffers.AsSpan(0, _residentCount);

    private nint[] _residentBuffers = new nint[16];
    private int _residentCount;

    private void NoteResident(nint buffer)
    {
        if (_residencySet != 0 || buffer == 0)
        {
            return;
        }

        for (var index = 0; index < _residentCount; index++)
        {
            if (_residentBuffers[index] == buffer)
            {
                return;
            }
        }

        if (_residentCount == _residentBuffers.Length)
        {
            Array.Resize(ref _residentBuffers, _residentBuffers.Length * 2);
        }

        _residentBuffers[_residentCount++] = buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var ring in _rings)
        {
            MetalNative.SendVoid(ring.Buffer, SelRelease);
        }

        _rings.Clear();
        for (var index = 0; index < _windowCount; index++)
        {
            MetalNative.SendVoid(_windows[index].Buffer, SelRelease);
        }

        if (_gdsBuffer != 0)
        {
            MetalNative.SendVoid(_gdsBuffer, SelRelease);
        }

        if (_nullBuffer != 0)
        {
            MetalNative.SendVoid(_nullBuffer, SelRelease);
        }

        if (_residencySet != 0)
        {
            MetalNative.SendVoid(_residencySet, SelRelease);
        }
    }
}
