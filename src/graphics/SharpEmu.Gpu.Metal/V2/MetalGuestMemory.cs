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
    private const ulong MaxUploadBytes = 64UL * 1024 * 1024;
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
    private static readonly nint SelRecommendedWorkingSet =
        MetalNative.Selector("recommendedMaxWorkingSetSize");
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
        var budget = WindowBudget(device);
        var (windowStride, windowCount, mappedBytes) = ChooseWindowLayout(aliasSize, windowBytes, budget);
        if (mappedBytes > budget)
        {
            MetalV2Diagnostics.WarnOnce(
                "window-budget",
                "The guest backing does not fit what the device can hold resident; submissions may fail with " +
                $"Insufficient Memory: mapped=0x{mappedBytes:X} budget=0x{budget:X}.");
        }

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
            if (_residencySet != 0)
            {
                AddResident(buffer);
            }
        }

        _windowCount = windowCount;
        _windowStride = windowStride;
        _gdsBuffer = CreateSharedBuffer(GdsBytes, "guest.gds", out _, out _);
        _nullBuffer = CreateSharedBuffer(256, "guest.null", out _nullContents, out _);
        // Both are reached through an address in an argument buffer, so the encoder
        // has to be told about them; both are small enough to always name.
        AddResident(_gdsBuffer);
        AddResident(_nullBuffer);
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
            $"stride=0x{windowStride:X} mapped_mb={mappedBytes / (1024 * 1024)} " +
            $"budget_mb={budget / (1024 * 1024)} " +
            $"alias=0x{aliasBase:X16}+0x{aliasSize:X} map_seconds={mapSeconds} residency_set={(_residencySet != 0 ? 1 : 0)}");
    }

    /// <summary>
    /// Picks the window stride for a backing alias.
    ///
    /// Windows may overlap by half: window k starts at k * stride, so any range of up to
    /// one stride lies wholly inside the window its first byte selects, and a range
    /// crossing a window edge never falls back to a copy.
    ///
    /// That doubles how many window bytes every encoder names with <c>useResource</c>,
    /// and a command buffer whose named resources exceed what the device can hold
    /// resident fails outright — "Insufficient Memory"
    /// (<c>kIOGPUCommandBufferCallbackErrorOutOfMemory</c>) — with no draw ever reaching
    /// the framebuffer. On a 24 GB M4 a 16.4 GiB backing in half-overlapping 4 GiB
    /// windows asks for 30.4 GiB and every single submission failed. The overlap is
    /// therefore taken only while the mapped total fits the device's working set;
    /// otherwise the windows tile the alias exactly and a straddling range takes the ring
    /// (reads) or splits into runs (which the device-address table already does).
    /// </summary>
    internal static (ulong Stride, int Count, ulong MappedBytes) ChooseWindowLayout(
        ulong aliasSize,
        ulong windowBytes,
        ulong budget)
    {
        var overlapped = windowBytes / 2;
        if (overlapped != 0 && MappedBytes(aliasSize, windowBytes, overlapped) <= budget)
        {
            return (overlapped, WindowCount(aliasSize, windowBytes, overlapped),
                MappedBytes(aliasSize, windowBytes, overlapped));
        }

        return (windowBytes, WindowCount(aliasSize, windowBytes, windowBytes),
            MappedBytes(aliasSize, windowBytes, windowBytes));
    }

    // How many windows of windowBytes at this stride cover the alias.
    private static int WindowCount(ulong aliasSize, ulong windowBytes, ulong windowStride) =>
        aliasSize <= windowBytes
            ? 1
            : (int)((aliasSize - windowBytes + windowStride - 1) / windowStride) + 1;

    // The bytes every encoder names with useResource, which is what the device has to
    // hold resident: overlapping windows map the same guest bytes more than once.
    private static ulong MappedBytes(ulong aliasSize, ulong windowBytes, ulong windowStride)
    {
        var total = 0UL;
        var count = WindowCount(aliasSize, windowBytes, windowStride);
        for (var index = 0; index < count; index++)
        {
            var offset = (ulong)index * windowStride;
            total += Math.Min(windowBytes, aliasSize - offset);
        }

        return total;
    }

    // What the device says it can keep resident. A command buffer that names more than
    // this is refused by the driver rather than throttled, so the window layout is
    // chosen against it instead of against the alias size.
    private static ulong WindowBudget(nint device)
    {
        var recommended = MetalV2Native.SendValue64(device, SelRecommendedWorkingSet);
        return recommended != 0 ? recommended : 4UL * 1024 * 1024 * 1024;
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
        // Residency is per submission: naming every window on every encoder asks the
        // kernel to make the whole 16 GiB backing writable-resident for a draw that
        // touches one window, which measured as tens of milliseconds between -commit
        // and the driver scheduling the submission.
        _touchedWindows = 0;
        _residentDirty = true;
    }

    private ulong _touchedWindows;
    private bool _residentDirty = true;

    /// <summary>The windows resolved since this submission began, one bit each.</summary>
    public ulong TouchedWindows => _touchedWindows;

    /// <summary>Notes that this submission reaches into these windows.</summary>
    public void MarkWindowsTouched(ulong windows)
    {
        if ((windows & ~_touchedWindows) == 0)
        {
            return;
        }

        _touchedWindows |= windows;
        _residentDirty = true;
    }

    /// <summary>Forgets which windows this submission has reached, for a re-scan.</summary>
    public void ClearTouchedWindows()
    {
        _touchedWindows = 0;
        _residentDirty = true;
    }

    /// <summary>Every window, for a submission that cannot name the ones it needs.</summary>
    public ulong AllWindows => _windowCount >= 64 ? ulong.MaxValue : (1UL << _windowCount) - 1;

    public void NoteCompleted(ulong completedSerial)
    {
        _completedSerial = Math.Max(_completedSerial, completedSerial);
        TrimIdleRings();
    }

    // A ring is created when a range straddles a window or is not backed, which a
    // title can do in bursts: a 4 GiB span of unbacked descriptors once cost tens of
    // rings. Nothing freed them again, so the pool was the process's high-water mark
    // for the rest of the run. Rings beyond the few that keep a steady submission fed
    // are released once the GPU has been done with them for this many serials.
    private const int KeptRings = 4;
    private const ulong IdleRingSerials = 64;

    private void TrimIdleRings()
    {
        if (_rings.Count <= KeptRings || _completedSerial < IdleRingSerials)
        {
            return;
        }

        var retireBefore = _completedSerial - IdleRingSerials;
        for (var index = _rings.Count - 1; index >= KeptRings; index--)
        {
            var ring = _rings[index];
            if (ring.LastUsedSerial > retireBefore)
            {
                continue;
            }

            RemoveResident(ring.Buffer);
            MetalNative.SendVoid(ring.Buffer, SelRelease);
            _rings.RemoveAt(index);
            _ringsReleased++;
        }
    }

    private long _ringsReleased;

    /// <summary>How many idle upload rings have been handed back since startup.</summary>
    public long RingsReleased => Volatile.Read(ref _ringsReleased);

    /// <summary>The upload rings alive right now.</summary>
    public int RingCount => _rings.Count;

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

        var bit = 1UL << index;
        if ((_touchedWindows & bit) == 0)
        {
            _touchedWindows |= bit;
            _residentDirty = true;
        }

        resolved = new MetalGuestRange(window.Buffer, offset, size, window.GpuAddress + offset);
        return true;
    }

    /// <summary>Copies guest bytes into the ring; used for ranges no window covers.</summary>
    private MetalGuestRange Upload(ulong gpuVa, ulong size)
    {
        if (size > MaxUploadBytes)
        {
            // An unbacked range this large is not data a draw reads whole; bind its
            // prefix rather than staging gigabytes per submission.
            MetalV2Diagnostics.WarnOnce(
                "oversized-upload",
                $"An unbacked guest range is too large to stage; binding its first 0x{MaxUploadBytes:X} bytes: " +
                $"address=0x{gpuVa:X16} size=0x{size:X}.");
            size = MaxUploadBytes;
        }

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

    /// <summary>
    /// Reads guest bytes on the host, for diagnostics that want to say what a shader is
    /// about to read. Not a transfer path: the GPU reaches the same bytes through a
    /// window or the upload ring.
    /// </summary>
    public bool TryPeek(ulong gpuVa, Span<byte> destination) => _memory.TryRead(gpuVa, destination);

    /// <summary>
    /// A small shared buffer outside the per-submission ring, for words the GPU writes
    /// and the host reads back after a submission. The caller owns its lifetime and
    /// must name it with <c>useResource</c> wherever a shader reaches it.
    /// </summary>
    public nint CreateReportBuffer(ulong size, string label, out nint contents, out ulong gpuAddress) =>
        CreateSharedBuffer(size, label, out contents, out gpuAddress);

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

    /// <summary>Drops a released allocation from whatever keeps it resident.</summary>
    public void RemoveResident(nint allocation)
    {
        if (allocation == 0)
        {
            return;
        }

        if (_residencySet != 0)
        {
            var selector = MetalNative.Selector("removeAllocation:");
            if (MetalNative.SendBool(_residencySet, MetalNative.Selector("respondsToSelector:"), selector))
            {
                MetalNative.SendVoid(_residencySet, selector, allocation);
                CommitResidency();
            }

            return;
        }

        for (var index = 0; index < _residentCount; index++)
        {
            if (_residentBuffers[index] != allocation)
            {
                continue;
            }

            _residentBuffers[index] = _residentBuffers[--_residentCount];
            _residentBuffers[_residentCount] = 0;
            _residentDirty = true;
            return;
        }
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
    public ReadOnlySpan<nint> ResidentBuffers
    {
        get
        {
            if (_residencySet != 0)
            {
                return default;
            }

            if (_residentDirty)
            {
                _residentDirty = false;
                _submissionResidentCount = 0;
                for (var index = 0; index < _windowCount; index++)
                {
                    if ((_touchedWindows & (1UL << index)) != 0)
                    {
                        Append(ref _submissionResidentBuffers, ref _submissionResidentCount, _windows[index].Buffer);
                    }
                }

                for (var index = 0; index < _residentCount; index++)
                {
                    Append(ref _submissionResidentBuffers, ref _submissionResidentCount, _residentBuffers[index]);
                }
            }

            return _submissionResidentBuffers.AsSpan(0, _submissionResidentCount);
        }
    }

    private static void Append(ref nint[] buffers, ref int count, nint buffer)
    {
        if (count == buffers.Length)
        {
            Array.Resize(ref buffers, buffers.Length * 2);
        }

        buffers[count++] = buffer;
    }

    private nint[] _residentBuffers = new nint[16];
    private int _residentCount;
    private nint[] _submissionResidentBuffers = new nint[16];
    private int _submissionResidentCount;

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
        _residentDirty = true;
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
