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
/// Adjacent windows overlap by at least <see cref="SeamOverlapBytes"/>, so a binding up
/// to that size never straddles a window edge. A range that is not backed, or a larger
/// one that straddles two windows, falls back to a per-command-buffer upload ring. The windows and the live ring buffers sit in an
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

    /// <summary>
    /// How far adjacent windows overlap when the device cannot hold the half overlap.
    /// A range staged into the ring is copied on the CPU at record time, so it misses
    /// GPU writes still in flight to those bytes, and a shader's stores into it land in
    /// the ring instead of guest memory. With this overlap every range up to its size
    /// lies wholly inside the window its first byte selects and binds the guest bytes
    /// directly, ordered like any other window access. It matches the ring's own cap,
    /// so no range the ring could stage whole still needs it for straddling an edge,
    /// and it costs one overlap of resident bytes per window edge.
    /// </summary>
    internal const ulong SeamOverlapBytes = MaxUploadBytes;
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
    private readonly ulong _windowOverlap;
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
    private long _ringBytes;
    private long _ringFallbacks;
    private bool _disposed;

    public MetalGuestMemory(nint device, IGuestBackedSpace backing, ICpuMemory memory)
        : this(device, backing, memory, PreferredWindowBytes, SeamOverlapBytes, budgetOverride: 0)
    {
    }

    /// <summary>A layout with explicit window, overlap and budget sizes, for tests on a small backing.</summary>
    internal MetalGuestMemory(
        nint device,
        IGuestBackedSpace backing,
        ICpuMemory memory,
        ulong preferredWindowBytes,
        ulong seamBytes,
        ulong budgetOverride)
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
        var windowBytes = Math.Min(preferredWindowBytes, maxBufferLength == 0 ? preferredWindowBytes : maxBufferLength);
        var budget = budgetOverride != 0 ? budgetOverride : WindowBudget(device);
        var (windowStride, windowCount, mappedBytes) = ChooseWindowLayout(aliasSize, windowBytes, budget, seamBytes);
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
        _windowOverlap = windowCount > 1 ? windowBytes - windowStride : 0;
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
            $"stride=0x{windowStride:X} overlap=0x{_windowOverlap:X} mapped_mb={mappedBytes / (1024 * 1024)} " +
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
    /// therefore taken only while the mapped total fits the device's working set.
    ///
    /// Short of that, the windows overlap by the seam alone, which adds one seam of
    /// resident bytes per window edge and still keeps every range up to the seam inside
    /// one window. Only when even that does not fit do the windows tile the alias exactly,
    /// and a straddling range takes the ring (reads) or splits into runs (which the
    /// device-address table already does).
    /// </summary>
    internal static (ulong Stride, int Count, ulong MappedBytes) ChooseWindowLayout(
        ulong aliasSize,
        ulong windowBytes,
        ulong budget,
        ulong seamBytes = SeamOverlapBytes)
    {
        var overlapped = windowBytes / 2;
        if (overlapped != 0 && MappedBytes(aliasSize, windowBytes, overlapped) <= budget)
        {
            return (overlapped, WindowCount(aliasSize, windowBytes, overlapped),
                MappedBytes(aliasSize, windowBytes, overlapped));
        }

        if (seamBytes != 0 && seamBytes < overlapped)
        {
            var seamed = windowBytes - seamBytes;
            if (MappedBytes(aliasSize, windowBytes, seamed) <= budget)
            {
                return (seamed, WindowCount(aliasSize, windowBytes, seamed),
                    MappedBytes(aliasSize, windowBytes, seamed));
            }
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

    /// <summary>
    /// Records every window resolution while the slot trap is armed. The tiler and the
    /// transfer paths bind their buffers straight to an encoder rather than through a
    /// descriptor, so a submission that writes guest memory can resolve a window without
    /// any binding the descriptor path ever sees.
    /// </summary>
    internal MetalSlotTrap? Trap { get; set; }

    public nint Device => _device;

    public nint GdsBuffer => _gdsBuffer;

    public nint NullBuffer => _nullBuffer;

    public nint ResidencySet => _residencySet;

    public ulong RingFallbackBytes => (ulong)Volatile.Read(ref _ringFallbackBytes);

    public ulong RingFallbacks => (ulong)Volatile.Read(ref _ringFallbacks);

    // Shared memory the upload rings hold now, for the periodic counters line.
    public ulong RingBytes => (ulong)Volatile.Read(ref _ringBytes);

    /// <summary>
    /// Shared memory the rings handed to the submission being recorded hold. A ring
    /// returns to the pool only when the submission that last used it completes, so a
    /// submission that keeps allocating holds every ring it touched and grows the pool
    /// by a ring at a time; the host commits it at a budget.
    /// </summary>
    public ulong RecordingBytes { get; private set; }

    /// <summary>The serial a resource allocated now is retired at.</summary>
    public void BeginSubmission(ulong submissionSerial, ulong completedSerial)
    {
        _submissionSerial = submissionSerial;
        _completedSerial = completedSerial;
        RecordingBytes = 0;
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
            Interlocked.Add(ref _ringBytes, -(long)ring.Size);
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

    /// <summary>
    /// The window a binding of the range reads and writes in place; false when no one
    /// window covers it and it has to be staged (<see cref="Stage"/>).
    /// </summary>
    public bool TryResolveInPlace(ulong gpuVa, ulong size, out MetalGuestRange range)
    {
        range = default;
        return !StageEverything && TryResolveWindow(gpuVa, size, out range);
    }

    /// <summary>A ring copy of the guest bytes the CPU sees now.</summary>
    public MetalGuestRange Stage(ulong gpuVa, ulong size) =>
        size == 0 || gpuVa == 0 || size > ulong.MaxValue - gpuVa ? default : Upload(gpuVa, size);

    /// <summary>
    /// True when the whole range lives in the backing. A managed write to such a range
    /// lands through the backing alias, never through the guest mapping a page watch
    /// protects.
    /// </summary>
    public bool IsBacked(ulong gpuVa, ulong size) =>
        size != 0 && size <= ulong.MaxValue - gpuVa && _backing.TryResolveBackingAliasSpan(gpuVa, size, out _);

    /// <summary>The window range of an address that must be written by the GPU; zero when it is not backed.</summary>
    public MetalGuestRange ResolveWritable(ulong gpuVa, ulong size) =>
        TryResolveWindow(gpuVa, size, out var resolved) ? resolved : default;

    /// <summary>
    /// The longest prefix of a range that one window covers, or an invalid range when
    /// not even the first byte is in one. Whether a range resolves is monotone in its
    /// length - a window covers a prefix of whatever it covers - so the longest one is
    /// a binary search, which is how an image too scattered to map in one piece is cut
    /// into the pieces that do map.
    /// </summary>
    public MetalGuestRange ResolveWritablePrefix(ulong gpuVa, ulong size)
    {
        if (size == 0)
        {
            return default;
        }

        var whole = ResolveWritable(gpuVa, size);
        if (whole.IsValid)
        {
            return whole;
        }

        var low = 0UL;
        var high = size;
        var found = default(MetalGuestRange);
        while (high - low > 1)
        {
            var probe = low + ((high - low) / 2);
            var resolved = ResolveWritable(gpuVa, probe);
            if (resolved.IsValid)
            {
                found = resolved;
                low = probe;
            }
            else
            {
                high = probe;
            }
        }

        return found;
    }

    /// <summary>
    /// Why a range has no window, for diagnostics. A caller that has to fall back wants to
    /// say whether the guest bytes are not one backed span at all or merely straddle the
    /// boundary between two windows, because the two have different answers.
    /// </summary>
    public string DescribeResolve(ulong gpuVa, ulong size)
    {
        if (size == 0 || gpuVa == 0 || size > ulong.MaxValue - gpuVa)
        {
            return "degenerate range";
        }

        if (!_backing.TryResolveBackingAlias(gpuVa, size, out var alias))
        {
            // Narrow down to the largest prefix that is one backed span, which says
            // whether the range is unbacked outright or merely not contiguous.
            var contiguous = 0UL;
            for (var probe = size / 2; probe >= 4096; probe /= 2)
            {
                if (_backing.TryResolveBackingAlias(gpuVa, contiguous + probe, out _))
                {
                    contiguous += probe;
                }
            }

            return $"not one backed span (contiguous prefix about 0x{contiguous:X} of 0x{size:X})";
        }

        if (alias < _aliasBase || alias - _aliasBase >= _aliasSize)
        {
            return $"backed at alias 0x{alias:X16}, outside the mapped windows " +
                $"0x{_aliasBase:X16}+0x{_aliasSize:X}";
        }

        var index = (int)Math.Min((alias - _aliasBase) / _windowStride, (ulong)(_windowCount - 1));
        ref readonly var window = ref _windows[index];
        var offset = alias - window.AliasBase;
        if (alias < window.AliasBase || offset >= window.Size)
        {
            return $"alias 0x{alias:X16} is outside window {index} (0x{window.AliasBase:X16}+0x{window.Size:X})";
        }

        if (size > window.Size - offset)
        {
            return $"straddles the end of window {index}: offset 0x{offset:X} + size 0x{size:X} " +
                $"exceeds 0x{window.Size:X} by 0x{size - (window.Size - offset):X}";
        }

        return "resolvable";
    }

    // Why a range had to be staged into the ring instead of binding its window: the
    // start, the end, or the span between them. A sample per size class is enough to
    // name the allocation, and the ring bytes in the counters say how much it costs.
    private void ReportUnresolved(ulong gpuVa, ulong size)
    {
        if (!MetalV2Diagnostics.ReportUnbackedRanges)
        {
            return;
        }

        var startBacked = _backing.TryResolveBackingAlias(gpuVa, 1, out var startAlias);
        var endBacked = _backing.TryResolveBackingAlias(gpuVa + size - 1, 1, out var endAlias);
        var contiguous = startBacked && endBacked && endAlias - startAlias == size - 1;
        MetalV2Diagnostics.WarnOnce(
            $"unbacked-range:{size:X}:{(startBacked ? 1 : 0)}{(endBacked ? 1 : 0)}{(contiguous ? 1 : 0)}",
            $"A guest range does not resolve to a window and is staged into the ring: " +
            $"address=0x{gpuVa:X16} size=0x{size:X} start_backed={startBacked} end_backed={endBacked} " +
            $"alias_contiguous={contiguous} start_alias=0x{startAlias:X16} end_alias=0x{endAlias:X16}.");
    }

    private bool TryResolveWindow(ulong gpuVa, ulong size, out MetalGuestRange resolved)
    {
        resolved = default;
        if (size == 0 || gpuVa == 0 || size > ulong.MaxValue - gpuVa)
        {
            return false;
        }

        // A range that crosses the edge between two mappings of one allocation is
        // still contiguous in the backing, so it still has one window address; the
        // single-mapping test alone sent those to the staging ring.
        if (!_backing.TryResolveBackingAliasSpan(gpuVa, size, out var alias) ||
            alias < _aliasBase || alias - _aliasBase >= _aliasSize)
        {
            ReportUnresolved(gpuVa, size);
            return false;
        }

        var index = (int)Math.Min((alias - _aliasBase) / _windowStride, (ulong)(_windowCount - 1));
        ref readonly var window = ref _windows[index];
        var offset = alias - window.AliasBase;
        if (alias < window.AliasBase || offset >= window.Size || size > window.Size - offset)
        {
            MetalV2Diagnostics.WarnOnce(
                $"window-straddle:{size:X}",
                $"A backed guest range straddles the window edge, so no single window covers it: " +
                $"address=0x{gpuVa:X16} size=0x{size:X} alias=0x{alias:X16} window={index} " +
                $"window_offset=0x{offset:X} window_size=0x{window.Size:X} " +
                $"overlap=0x{_windowOverlap:X}.");
            return false;
        }

        var bit = 1UL << index;
        if ((_touchedWindows & bit) == 0)
        {
            _touchedWindows |= bit;
            _residentDirty = true;
        }

        resolved = new MetalGuestRange(window.Buffer, offset, size, window.GpuAddress + offset);
        // Mappings are page-granular on both sides, so a page-aligned guest address has to
        // resolve to a page-aligned window offset. When it does not, some view record's
        // guest address and backing offset disagree below the page, and every range it
        // resolves lands that far from where the guest put it.
        if ((gpuVa & 0xFFF) != (offset & 0xFFF))
        {
            MetalV2Diagnostics.WarnOnce(
                "unaligned-window-offset",
                $"A guest address resolved to a window offset that disagrees with it below the page: " +
                $"address=0x{gpuVa:X16} offset=0x{offset:X} window={index} delta=0x{(gpuVa ^ offset) & 0xFFF:X}.");
        }

        Trap?.NoteBinding($"resolve[{index}]", gpuVa, size, (ulong)window.Buffer, offset, resolved.GpuAddress);
        return true;
    }

    /// <summary>Copies guest bytes into the ring; used for ranges no window covers.</summary>
    private MetalGuestRange Upload(ulong gpuVa, ulong size)
    {
        if (size > MaxUploadBytes)
        {
            // A range this large that no window covers is not data a draw reads whole;
            // bind its prefix rather than staging gigabytes per submission. It may well
            // be backed and merely cross a window edge, so the warning says which.
            MetalV2Diagnostics.WarnOnce(
                "oversized-upload",
                $"A guest range no single window covers is too large to stage; binding its first " +
                $"0x{MaxUploadBytes:X} bytes: address=0x{gpuVa:X16} size=0x{size:X} " +
                $"({DescribeResolve(gpuVa, size)}).");
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

    /// <summary>A second ring copy of bytes already staged in the ring; invalid when the range is not a ring range.</summary>
    public unsafe MetalGuestRange Duplicate(in MetalGuestRange staged)
    {
        nint source = 0;
        foreach (var ring in _rings)
        {
            if (ring.Buffer == staged.Buffer && staged.Offset + staged.Size <= ring.Size)
            {
                source = ring.Contents + (nint)staged.Offset;
                break;
            }
        }

        if (source == 0 || staged.Size == 0)
        {
            return default;
        }

        var copy = Allocate(staged.Size, RingAlignment, out var span);
        new ReadOnlySpan<byte>((void*)source, checked((int)staged.Size)).CopyTo(span);
        return copy;
    }

    /// <summary>Reserves ring bytes and hands back the host span to fill.</summary>
    public MetalGuestRange Allocate(ulong size, ulong alignment, out Span<byte> span)
    {
        if (size == 0)
        {
            throw GpuFatal.Fatal("A guest memory ring allocation needs a nonzero size.");
        }

        var ring = Reserve(size, Math.Max(alignment, 16), out var offset);
        if (ring.LastUsedSerial != _submissionSerial)
        {
            // The whole ring is this submission's until it completes, however little
            // of it the reservation took.
            RecordingBytes += ring.Size;
        }

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
        Interlocked.Add(ref _ringBytes, (long)capacity);
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

    /// <summary>A resident shared buffer for one cached meshlet list; see
    /// <see cref="MetalMeshletCache"/>.</summary>
    public nint CreateMeshletBuffer(ulong size, out nint contents)
    {
        var buffer = CreateSharedBuffer(size, "mesh.meshlets", out contents, out _);
        AddResident(buffer);
        CommitResidency();
        return buffer;
    }

    public void ReleaseMeshletBuffer(nint buffer)
    {
        RemoveResident(buffer);
        MetalNative.SendVoid(buffer, MetalNative.Selector("release"));
    }

    private nint _meshLdsPool;
    private readonly object _meshLdsPoolLock = new();

    /// <summary>The device-memory LDS pool of mesh programs whose guest LDS is too large for
    /// threadgroup memory (see <c>Gen5MslMeshInfo.DeviceLds</c>): a zeroed header of a probe
    /// counter and one busy flag per slot, then one slot of data per resident threadgroup.
    /// Created on first use and never reset: a threadgroup frees its slot when it ends.</summary>
    public nint MeshLdsPool
    {
        get
        {
            lock (_meshLdsPoolLock)
            {
                if (_meshLdsPool == 0)
                {
                    var buffer = CreateSharedBuffer(
                        SharpEmu.ShaderCompiler.Metal.Gen5MslMeshInfo.LdsPoolByteSize, "mesh.lds-pool", out var contents, out _);
                    unsafe
                    {
                        new Span<byte>(
                            (void*)contents,
                            (int)(SharpEmu.ShaderCompiler.Metal.Gen5MslMeshInfo.LdsPoolHeaderDwords * sizeof(uint))).Clear();
                    }

                    AddResident(buffer);
                    CommitResidency();
                    _meshLdsPool = buffer;
                }

                return _meshLdsPool;
            }
        }
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
