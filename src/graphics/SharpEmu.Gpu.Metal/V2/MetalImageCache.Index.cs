// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

// Generational slots for cached images: the shared engine only ever holds
// ResourceSlotIdentifier values, so a slot can be erased while an identifier for it
// is still in flight and the stale identifier simply stops resolving.
internal sealed class MetalImageSlots
{
    private readonly List<MetalCachedImage?> _entries = new();
    private readonly List<uint> _generations = new();
    private readonly Stack<int> _free = new();

    public int Count { get; private set; }

    public MetalCachedImage this[ResourceSlotIdentifier identifier] =>
        TryGet(identifier) ?? throw GpuFatal.Fatal(
            $"The image slot is no longer live: index={identifier.Index} generation={identifier.Generation}.");

    public MetalCachedImage? TryGet(ResourceSlotIdentifier identifier)
    {
        var index = (int)identifier.Index;
        if (identifier.Generation == 0 || index >= _entries.Count || _generations[index] != identifier.Generation)
        {
            return null;
        }

        return _entries[index];
    }

    public ResourceSlotIdentifier Insert(MetalCachedImage image)
    {
        if (_free.TryPop(out var index))
        {
            _entries[index] = image;
            Count++;
            return new ResourceSlotIdentifier((uint)index, _generations[index]);
        }

        index = _entries.Count;
        // Generation 0 never identifies a live slot, so a fresh slot starts at 1.
        _entries.Add(image);
        _generations.Add(1);
        Count++;
        return new ResourceSlotIdentifier((uint)index, 1);
    }

    public void Erase(ResourceSlotIdentifier identifier)
    {
        var index = (int)identifier.Index;
        if (index >= _entries.Count || _generations[index] != identifier.Generation || _entries[index] is null)
        {
            return;
        }

        _entries[index] = null;
        _generations[index] = _generations[index] + 1 == 0 ? 1u : _generations[index] + 1;
        _free.Push(index);
        Count--;
    }

    public void ForEach(Action<ResourceSlotIdentifier, MetalCachedImage> visit)
    {
        for (var index = 0; index < _entries.Count; index++)
        {
            if (_entries[index] is { } image)
            {
                visit(new ResourceSlotIdentifier((uint)index, _generations[index]), image);
            }
        }
    }
}

// The images that own one 1 MiB guest page. Sparse two-level lookup; a query never
// allocates a bucket, and the "ever owned" bitmap lets a CPU write on the faulting
// thread skip the cache lock when no image has ever covered the page.
internal sealed class MetalImagePageOwners
{
    public const int PageBits = 20;
    public const int AddressSpaceBits = 40;
    public const int FirstLevelBits = 10;
    public const int SecondLevelBits = AddressSpaceBits - FirstLevelBits - PageBits;
    public const int BucketEntries = 1 << SecondLevelBits;
    public const ulong PageCount = 1UL << (AddressSpaceBits - PageBits);
    public const ulong AddressSpaceSize = 1UL << AddressSpaceBits;

    private readonly List<ResourceSlotIdentifier>?[]?[] _firstLevel =
        new List<ResourceSlotIdentifier>?[]?[1 << FirstLevelBits];

    private readonly long[] _everOwnedPages = new long[PageCount / 64];

    public static bool IsValidPage(ulong page) => page < PageCount;

    public List<ResourceSlotIdentifier>? Find(ulong page) =>
        IsValidPage(page) && _firstLevel[page >> SecondLevelBits] is { } bucket
            ? bucket[page & (BucketEntries - 1)]
            : null;

    public List<ResourceSlotIdentifier> GetOrCreate(ulong page)
    {
        if (!IsValidPage(page))
        {
            throw GpuFatal.Fatal($"The owner-table page is outside the guest address space: page={page}.");
        }

        ref var bucket = ref _firstLevel[page >> SecondLevelBits];
        bucket ??= new List<ResourceSlotIdentifier>?[BucketEntries];
        var owners = bucket[page & (BucketEntries - 1)] ??= new List<ResourceSlotIdentifier>(4);
        Interlocked.Or(ref _everOwnedPages[page / 64], 1L << (int)(page % 64));
        return owners;
    }

    // Bits are never cleared, so a false answer is safe without the cache lock.
    public bool MayHaveOwners(ulong address, ulong size)
    {
        if (!TryGetPageRange(address, size, out var first, out var lastExclusive))
        {
            return false;
        }

        for (var page = first; page < lastExclusive; page++)
        {
            if ((Volatile.Read(ref _everOwnedPages[page / 64]) & (1L << (int)(page % 64))) != 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetPageRange(ulong address, ulong size, out ulong first, out ulong lastExclusive)
    {
        first = 0;
        lastExclusive = 0;
        if (size == 0 || address >= AddressSpaceSize || size > AddressSpaceSize - address)
        {
            return false;
        }

        first = address >> PageBits;
        lastExclusive = ((address + size - 1) >> PageBits) + 1;
        return true;
    }
}
