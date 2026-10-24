// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Memory;

// The two-process split's layout (SplitBackingLayout, docs/macos/split-shared-memory.md): the GPU
// process sees guest memory only through the shared backing, so guest memory the GPU reads that
// is private here otherwise is mapped from a part of the backing past direct and flexible memory.
// The guest allocator arena is one view of it. A module image is mapped page by page: every guest
// page no executable segment touches is a view of a slice handed out in load order, and the rest
// (code, and the partial pages at either end) is private as before, because a shared view of the
// backing is never executable on macOS. Map then fills and protects a segment whatever mix of
// regions it lies over. Nothing here runs without a split layout.
public sealed unsafe partial class PhysicalVirtualMemory
{
    // Null unless asked for and the backing is shareable.
    private readonly SplitBackingLayout? _splitLayout;
    // Arena bytes handed out since the backing was made: a later arena over the same slice
    // (after Clear) zeroes them, as fresh private memory would be.
    private ulong _splitArenaUsedBytes;
    // The image slice: the next free byte, and the bytes ever handed out (zeroed when reused).
    private ulong _splitImageNext;
    private ulong _splitImageUsedBytes;
    private int _splitImageWarnings;
    private int _splitProtectionWarnings;

    /// <summary>The two-process split's layout in use; null when this space keeps that memory private.</summary>
    public SplitBackingLayout? SplitLayout => _splitLayout;

    /// <summary>
    /// True when module images are mapped with their non-executable pages as views of the backing
    /// (<see cref="TryAllocateModuleImage"/>).
    /// </summary>
    public bool SplitsModuleImages => _splitLayout is { ImageBytes: > 0 } && HasBackingOwner();

    /// <summary>Bytes of the image slice the loaded modules hold.</summary>
    public ulong SplitImageBytesInUse
    {
        get
        {
            lock (_guestAllocationGate)
            {
                return _splitImageNext;
            }
        }
    }

    /// <summary>True while the guest allocator arena is a view of the backing (split layout).</summary>
    public bool AllocatesFromBacking
    {
        get
        {
            lock (_guestAllocationGate)
            {
                // Before the arena exists, the layout says what it will be.
                return _guestAllocationArenaBase == 0 ? _splitLayout is not null && HasBackingOwner() : _guestAllocationArenaBacked;
            }
        }
    }

    private SplitBackingLayout? SelectSplitLayout(SplitBackingLayout? requested)
    {
        if (requested is null)
        {
            return null;
        }

        if (_backedSpace?.ViewTable is null || _backedSpace.BackingSize < requested.End)
        {
            Log.Warn($"Split layout off: the guest backing is not shareable or smaller than 0x{requested.End:X} bytes; " +
                "module images, the guest allocator arena and HLE data stay private to this process.");
            return null;
        }

        Log.Info($"Split layout: the guest allocator arena (backing 0x{requested.ArenaOffset:X}+0x{requested.ArenaBytes:X}) " +
            $"and module data pages (backing 0x{requested.ImageOffset:X}+0x{requested.ImageBytes:X}) are views of the backing.");
        return requested;
    }

    // The split layout's arena: one view of the backing at the layout's address. Caller holds
    // _guestAllocationGate. False (nothing mapped) leaves the arena to the private path.
    private bool TryMapSplitArena(out ulong arenaBase, ref ulong arenaSize)
    {
        arenaBase = 0;
        if (_splitLayout is not { } layout || !HasBackingOwner())
        {
            return false;
        }

        if (_splitArenaUsedBytes != 0)
        {
            if (!TryClearBacking(layout.ArenaOffset, AlignUp(_splitArenaUsedBytes, GuestMemoryLayout.GuestPage)))
            {
                Log.Warn("Split layout: the guest allocator arena's slice could not be zeroed for reuse; the arena stays private.");
                return false;
            }

            _splitArenaUsedBytes = 0;
        }

        if (!TryMapBacked(layout.ArenaAddress, layout.ArenaBytes, layout.ArenaOffset,
                GuestPageProtection.Read | GuestPageProtection.Write, out var failure))
        {
            Log.Warn($"Split layout: the guest allocator arena could not be mapped from the backing at 0x{layout.ArenaAddress:X16} " +
                $"({failure}); it stays private, and the GPU process cannot see the VideoOut labels or HLE data in it.");
            return false;
        }

        arenaBase = layout.ArenaAddress;
        arenaSize = layout.ArenaBytes;
        TraceVmem($"Mapped the guest allocator arena from the backing: 0x{arenaBase:X16}+0x{arenaSize:X} " +
            $"(backing 0x{layout.ArenaOffset:X})");
        return true;
    }

    // Caller holds _guestAllocationGate.
    private void NoteSplitArenaUse(ulong allocationEnd) =>
        _splitArenaUsedBytes = Math.Max(_splitArenaUsedBytes, allocationEnd);

    /// <summary>
    /// Allocates a module image at exactly <paramref name="imageBase"/>, as
    /// <see cref="TryAllocateAtExact"/> does for an executable image, except that in the split layout
    /// every guest page no executable span (image-relative) touches is a view of a new slice of the
    /// backing; code and the partial pages at either end stay private. Without a split layout, with
    /// nothing to back, or with the image slice full, it is one private allocation. False when the
    /// range is not free; nothing stays allocated then.
    /// </summary>
    public bool TryAllocateModuleImage(ulong imageBase, ulong size, IReadOnlyList<(ulong Offset, ulong Size)> executableSpans,
        out ulong actualAddress)
    {
        ArgumentNullException.ThrowIfNull(executableSpans);
        actualAddress = 0;
        if (!SplitsModuleImages || size == 0 || imageBase % PageSize != 0 || imageBase > ulong.MaxValue - PageSize ||
            size > ulong.MaxValue - PageSize - imageBase)
        {
            return TryAllocateAtExact(imageBase, size, executable: true, out actualAddress);
        }

        var end = imageBase + AlignUp(size, PageSize);
        var pieces = PlanModuleImage(imageBase, end, executableSpans);
        ulong backedBytes = 0;
        foreach (var piece in pieces)
        {
            backedBytes += piece.Backed ? piece.Size : 0;
        }

        if (backedBytes == 0)
        {
            return TryAllocateAtExact(imageBase, size, executable: true, out actualAddress);
        }

        // Probe first: a view placed and given back again leaves its range reserved (the backing's
        // owner keeps its placeholders), where a private piece of a later attempt could not go.
        // Zero means the host cannot say.
        var unmapped = _backedSpace!.FindUnmappedHostRange(imageBase, end - imageBase, ProbeSearchEnd);
        if (unmapped != 0 && unmapped != imageBase)
        {
            return false;
        }

        if (!TryTakeSplitImageSlice(backedBytes, out var sliceOffset))
        {
            if (Interlocked.Increment(ref _splitImageWarnings) <= 8)
            {
                Log.Warn($"Split layout: no room for 0x{backedBytes:X} more bytes in the image slice " +
                    $"(0x{_splitLayout!.ImageBytes:X}); the module at 0x{imageBase:X16} stays private and the GPU process cannot see it.");
            }

            return TryAllocateAtExact(imageBase, size, executable: true, out actualAddress);
        }

        var placed = new List<ModuleImagePiece>(pieces.Count);
        var offset = sliceOffset;
        foreach (var piece in pieces)
        {
            var mapped = piece.Backed
                ? TryMapBacked(piece.Address, piece.Size, offset, GuestPageProtection.Read | GuestPageProtection.Write, out _)
                : TryAllocateAtExact(piece.Address, piece.Size, executable: true, out _);
            if (!mapped)
            {
                ReleaseModuleImagePieces(placed);
                ReturnSplitImageSlice(sliceOffset, backedBytes);
                TraceVmem($"Module image at 0x{imageBase:X16} refused: {(piece.Backed ? "view" : "private piece")} " +
                    $"0x{piece.Address:X16}+0x{piece.Size:X} could not be placed");
                return false;
            }

            placed.Add(piece);
            offset += piece.Backed ? piece.Size : 0;
        }

        actualAddress = imageBase;
        TraceVmem($"Allocated module image: 0x{imageBase:X16} - 0x{end:X16} backed=0x{backedBytes:X} " +
            $"private=0x{end - imageBase - backedBytes:X} slice=0x{sliceOffset:X} pieces={pieces.Count}");
        return true;
    }

    // Probed host ranges end below the 47-bit limit; an occupied range reports a later address.
    private const ulong ProbeSearchEnd = 0x8000_0000_0000;

    internal readonly record struct ModuleImagePiece(ulong Address, ulong Size, bool Backed)
    {
        public ulong End => Address + Size;
    }

    /// <summary>
    /// The pieces a module image over [imageBase, end) is mapped as, in address order: runs of whole
    /// guest pages no executable span touches are backed; the rest is private (and executable).
    /// </summary>
    internal static List<ModuleImagePiece> PlanModuleImage(ulong imageBase, ulong end,
        IReadOnlyList<(ulong Offset, ulong Size)> executableSpans)
    {
        const ulong page = GuestMemoryLayout.GuestPage;
        var pieces = new List<ModuleImagePiece>();
        void Add(ulong address, ulong stop, bool backed)
        {
            if (stop <= address)
            {
                return;
            }

            if (pieces.Count != 0 && pieces[^1].Backed == backed && pieces[^1].End == address)
            {
                pieces[^1] = pieces[^1] with { Size = stop - pieces[^1].Address };
                return;
            }

            pieces.Add(new ModuleImagePiece(address, stop - address, backed));
        }

        var first = AlignUp(imageBase, page);
        var last = AlignDown(end, page);
        if (first >= last)
        {
            Add(imageBase, end, backed: false);
            return pieces;
        }

        var code = new List<(ulong Start, ulong End)>(executableSpans.Count);
        foreach (var (spanOffset, spanSize) in executableSpans)
        {
            if (spanSize == 0 || spanOffset >= end - imageBase)
            {
                continue;
            }

            var spanEnd = imageBase + spanOffset + Math.Min(spanSize, end - imageBase - spanOffset);
            code.Add((AlignDown(imageBase + spanOffset, page), Math.Min(AlignUp(spanEnd, page), last)));
        }

        code.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        Add(imageBase, first, backed: false);
        var cursor = first;
        foreach (var (start, stop) in code)
        {
            var clippedStart = Math.Max(start, first);
            if (stop <= clippedStart || stop <= cursor)
            {
                continue;
            }

            Add(cursor, clippedStart, backed: true);
            Add(Math.Max(cursor, clippedStart), stop, backed: false);
            cursor = stop;
        }

        Add(cursor, last, backed: true);
        Add(last, end, backed: false);
        return pieces;
    }

    private bool TryTakeSplitImageSlice(ulong size, out ulong offset)
    {
        var layout = _splitLayout!;
        ulong reused;
        lock (_guestAllocationGate)
        {
            offset = 0;
            if (size > layout.ImageBytes - _splitImageNext)
            {
                return false;
            }

            offset = layout.ImageOffset + _splitImageNext;
            reused = _splitImageUsedBytes > _splitImageNext ? Math.Min(size, _splitImageUsedBytes - _splitImageNext) : 0;
            _splitImageNext += size;
            _splitImageUsedBytes = Math.Max(_splitImageUsedBytes, _splitImageNext);
        }

        // Bytes a module held before the last Clear read zero again, as fresh private memory does.
        return reused == 0 || TryClearBacking(offset, reused);
    }

    // Only the last slice handed out goes back; an earlier one waits for Clear.
    private void ReturnSplitImageSlice(ulong offset, ulong size)
    {
        lock (_guestAllocationGate)
        {
            if (_splitLayout!.ImageOffset + _splitImageNext == offset + size)
            {
                _splitImageNext -= size;
            }
        }
    }

    private void ReleaseModuleImagePieces(List<ModuleImagePiece> placed)
    {
        foreach (var piece in placed)
        {
            if (piece.Backed)
            {
                TryUnmapBacked(piece.Address, piece.Size);
            }
            else
            {
                ReleaseUntrackedAllocation(piece.Address);
            }
        }
    }

    // Caller holds _gate. Whether [address, address + size) lies over regions side by side, at
    // least one a view of the backing: a module image in the split layout, which Map fills and
    // protects as it is instead of allocating.
    private bool IsCoveredBySplitImage(ulong address, ulong size)
    {
        if (_splitLayout is null || size == 0 || size > ulong.MaxValue - address)
        {
            return false;
        }

        var end = address + size;
        var cursor = address;
        var backed = false;
        for (var index = Math.Max(0, FindRegionIndexAtOrBelow(address)); index < _regions.Count && cursor < end; index++)
        {
            var region = _regions[index];
            var regionEnd = region.VirtualAddress + region.Size;
            if (regionEnd <= cursor)
            {
                continue;
            }

            if (region.VirtualAddress > cursor)
            {
                return false;
            }

            backed |= region.IsBackedView;
            cursor = regionEnd;
        }

        return cursor >= end && backed;
    }

    // Caller holds _gate. Map's protection over a range that crosses views of the backing: each
    // view takes it from the backing's owner (without execute, which a view cannot have; a split
    // image keeps every executable page private), each private region from the host. False when
    // the range holds no view, so the caller protects it as one private range.
    private bool TrySetSplitImageProtection(ulong address, ulong size, HostPageProtection protection, ProgramHeaderFlags flags)
    {
        if (_splitLayout is null || _backedSpace is null || size == 0 || size > ulong.MaxValue - address)
        {
            return false;
        }

        var end = address + size;
        var pieces = new List<(ulong Start, ulong End, bool Backed)>();
        var anyBacked = false;
        for (var index = Math.Max(0, FindRegionIndexAtOrBelow(address)); index < _regions.Count; index++)
        {
            var region = _regions[index];
            if (region.VirtualAddress >= end)
            {
                break;
            }

            var start = Math.Max(address, region.VirtualAddress);
            var stop = Math.Min(end, region.VirtualAddress + region.Size);
            if (stop <= start)
            {
                continue;
            }

            pieces.Add((start, stop, region.IsBackedView));
            anyBacked |= region.IsBackedView;
        }

        if (!anyBacked)
        {
            return false;
        }

        var viewProtection = protection switch
        {
            HostPageProtection.ReadWriteExecute => HostPageProtection.ReadWrite,
            HostPageProtection.ReadExecute or HostPageProtection.Execute => HostPageProtection.ReadOnly,
            _ => protection,
        };
        foreach (var (start, stop, backed) in pieces)
        {
            if (backed)
            {
                // A protection the owner cannot apply (a run smaller than a host page) leaves the
                // view as it was: protection only decides which guest accesses fault, never the bytes.
                if (!_backedSpace.SetTransientAccess(start, stop - start, viewProtection) &&
                    Interlocked.Increment(ref _splitProtectionWarnings) <= 8)
                {
                    Log.Warn($"Split layout: module data at 0x{start:X16}+0x{stop - start:X} keeps its protection " +
                        $"(wanted {viewProtection}).");
                }

                continue;
            }

            if (!_hostMemory.Protect(start, stop - start, protection, out _))
            {
                throw new InvalidOperationException($"Failed to set memory protection at 0x{start:X16}");
            }

            if ((flags & ProgramHeaderFlags.Execute) != 0)
            {
                _hostMemory.FlushInstructionCache(start, stop - start);
            }
        }

        return true;
    }

    // Caller holds _gate. The last region starting at or below the address, or -1.
    private int FindRegionIndexAtOrBelow(ulong address)
    {
        var low = 0;
        var high = _regions.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            if (_regions[middle].VirtualAddress <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}
