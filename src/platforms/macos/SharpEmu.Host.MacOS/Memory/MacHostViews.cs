// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

/// <summary>
/// macOS placeholder/view memory: POSIX shared-memory objects aliased into the
/// guest address space with MAP_FIXED, tracked in the neutral
/// <see cref="HostViewRegions"/> table so VirtualQuery-style lookups see them.
/// </summary>
internal sealed unsafe partial class MacHostViews : IHostViewMemory
{
    internal static bool FailAliasMapForTests;

    private const int PROT_NONE = 0x0;
    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int PROT_EXEC = 0x4;
    private const int MAP_SHARED = 0x01;
    private const int MAP_PRIVATE = 0x02;
    private const int MAP_FIXED = 0x10;
    private const int MAP_ANON = 0x1000;

    private const int O_RDWR = 0x2;
    private const int O_CREAT = 0x200;
    private const int O_EXCL = 0x800;

    private const int KERN_SUCCESS = 0;

    // On Darwin fixed placement is the absence of VM_FLAGS_ANYWHERE. Never add
    // VM_FLAGS_OVERWRITE: an overlapping request must fail, not clobber.
    private const int VM_FLAGS_FIXED = 0;

    // sizeof(vm_region_submap_short_info_data_64_t) / sizeof(natural_t).
    private const int VM_REGION_SUBMAP_SHORT_INFO_COUNT_64 = 12;
    private static readonly nint MAP_FAILED = -1;
    private static int _sharedObjectCounter;

    public ulong PageSize { get; } = (ulong)Environment.SystemPageSize;

    public ulong Granularity => PageSize;

    public bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure)
    {
        backing = null;
        failure = HostViewFailure.BackingUnavailable;
        if (size == 0)
        {
            return false;
        }

        // Darwin has no memfd_create; shm_open plus an immediate shm_unlink
        // gives the same anonymous, descriptor-only backing object.
        var descriptor = OpenSharedObject();
        if (descriptor < 0)
        {
            return false;
        }

        if (ftruncate(descriptor, (long)size) != 0)
        {
            close(descriptor);
            return false;
        }

        var alias = FailAliasMapForTests ? MAP_FAILED : mmap(0, (nuint)size, PROT_READ | PROT_WRITE, MAP_SHARED, descriptor, 0);
        if (alias == MAP_FAILED)
        {
            close(descriptor);
            return false;
        }

        backing = new HostBackingObject((ulong)alias, size, ReleaseBackingObject) { Descriptor = descriptor };
        failure = HostViewFailure.None;
        return true;
    }

    public ulong ReserveHole(ulong address, ulong size)
    {
        lock (HostViewRegions.Gate)
        {
            var result = ReserveHoleCore(address, size);
            if (result != 0)
                HostViewRegions.Replace(address, size, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            return result;
        }
    }

    private ulong ReserveHoleCore(ulong address, ulong size)
    {
        if (!HostViewMemory.IsValidRange(address, size) || address % Granularity != 0)
        {
            return 0;
        }

        // Darwin has no MAP_FIXED_NOREPLACE, and mmap's address argument is only
        // a hint there. On Apple Silicon the hint is ignored outright: every
        // anonymous mapping lands at the kernel's own allocation base no matter
        // what was asked for, so a "did it land where we asked?" test can never
        // succeed and every guest reservation would be reported as occupied.
        // mach_vm_allocate with fixed placement and without VM_FLAGS_OVERWRITE
        // maps exactly the requested range or fails with KERN_NO_SPACE, and
        // unlike MAP_FIXED it cannot clobber CLR, dyld, JIT, or Rosetta mappings
        // that are absent from our shadow table. It is the only reservation path
        // here, so a placement answer costs one syscall instead of a hint
        // attempt that is known to miss. (The Linux backend keeps the mmap path:
        // MAP_FIXED_NOREPLACE gives it the same exact-or-fail guarantee there.)
        return ReserveHoleDarwinFixed(address, size);
    }

    // Reserve exactly [address, address + size) or fail, without overwriting
    // anything already mapped there. mach_vm_allocate hands back pages with
    // VM_PROT_DEFAULT, so drop them to PROT_NONE to match the mmap path.
    private static ulong ReserveHoleDarwinFixed(ulong address, ulong size)
    {
        var allocated = address;
        if (mach_vm_allocate(mach_task_self(), ref allocated, size, VM_FLAGS_FIXED) != KERN_SUCCESS)
        {
            return 0;
        }

        if (allocated != address || mprotect((nint)address, (nuint)size, PROT_NONE) != 0)
        {
            mach_vm_deallocate(mach_task_self(), allocated, size);
            return 0;
        }

        return address;
    }

    // Find the free ranges by reserving halves until each attempt fits or falls
    // below the minimum. Occupied host ranges are few, so this needs few
    // attempts. Portable POSIX has no VirtualQuery for foreign host mappings;
    // where one exists, FindUnmappedRange below answers directly.
    public IReadOnlyList<HostAddressRange> ReserveFreeAddressRanges(ulong start, ulong end, ulong minimumSize)
    {
        var reserved = new List<HostAddressRange>();
        start = AlignUp(start, Granularity);
        end -= end % Granularity;
        if (start >= end || minimumSize == 0)
        {
            return reserved;
        }

        lock (HostViewRegions.Gate)
        {
            ReserveFreeAddressRangesLocked(start, end - start, Math.Max(minimumSize, Granularity), reserved);
        }

        return reserved;
    }

    private void ReserveFreeAddressRangesLocked(ulong address, ulong size, ulong minimumSize, List<HostAddressRange> reserved)
    {
        if (ReserveHoleCore(address, size) == address)
        {
            HostViewRegions.Replace(address, size, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            if (reserved.Count != 0 && reserved[^1].Address + reserved[^1].Size == address)
            {
                reserved[^1] = reserved[^1] with { Size = reserved[^1].Size + size };
            }
            else
            {
                reserved.Add(new HostAddressRange(address, size));
            }
            return;
        }

        var half = size / 2;
        half -= half % Granularity;
        if (half < minimumSize)
        {
            return;
        }

        ReserveFreeAddressRangesLocked(address, half, minimumSize, reserved);
        ReserveFreeAddressRangesLocked(address + half, size - half, minimumSize, reserved);
    }

    // Darwin does have a VirtualQuery equivalent. mach_vm_region_recurse walks
    // the task's own map including mappings this process never made, and asking
    // at nesting depth 0 collapses a whole submap into one entry. That matters
    // on Apple Silicon, where the kernel refuses fixed placement everywhere
    // below its own allocation base: the span from the dyld shared region up to
    // that base is hundreds of gigabytes, and a caller stepping one guest page
    // at a time needs tens of millions of probes to cross it. At depth 0 the
    // same crossing is a couple of dozen calls.
    public ulong FindUnmappedRange(ulong address, ulong size, ulong searchEnd)
    {
        if (size == 0 || address >= searchEnd || size > searchEnd - address)
        {
            return 0;
        }

        var task = mach_task_self();
        var info = stackalloc uint[VM_REGION_SUBMAP_SHORT_INFO_COUNT_64];
        var cursor = address;
        while (cursor < searchEnd && size <= searchEnd - cursor)
        {
            var regionAddress = cursor;
            ulong regionSize = 0;
            uint depth = 0;
            var count = VM_REGION_SUBMAP_SHORT_INFO_COUNT_64;
            if (mach_vm_region_recurse(task, ref regionAddress, ref regionSize, ref depth, info, ref count) != KERN_SUCCESS)
            {
                // Nothing is mapped between the cursor and the end of the map.
                return cursor;
            }

            // A region starting above the cursor leaves a hole in front of it.
            if (regionAddress > cursor && regionAddress - cursor >= size)
            {
                return cursor;
            }

            if (regionSize == 0 || regionSize > ulong.MaxValue - regionAddress)
            {
                return 0;
            }

            var regionEnd = regionAddress + regionSize;
            if (regionEnd <= cursor)
            {
                // The map did not move forward, so stop instead of spinning.
                return 0;
            }

            cursor = regionEnd;
        }

        return 0;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }

    public bool SplitHole(ulong address, ulong size) => size != 0;

    public bool JoinHoles(ulong address, ulong size) => size != 0;

    public bool FreeHole(ulong address, ulong size) => FreeOwnedRange(address, size);

    public bool TryMapView(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure)
    {
        lock (HostViewRegions.Gate)
        {
            if (!TryMapViewCore(backing, address, offset, size, protection, out failure)) return false;
            HostViewRegions.Replace(address, size, HostMemory.MEM_COMMIT, HostViewRegions.RawProtection(protection));
            return true;
        }
    }

    private bool TryMapViewCore(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure)
    {
        if (address == 0 || address % PageSize != 0)
        {
            failure = HostViewFailure.WrongHostAddress;
            return false;
        }

        lock (backing.Gate)
        {
            if (backing.IsDisposed)
            {
                failure = HostViewFailure.BackingUnavailable;
                return false;
            }

            if (!HostViewMemory.IsValidOffset(backing, offset, size, PageSize))
            {
                failure = HostViewFailure.OffsetOutOfBounds;
                return false;
            }

            if (size > ulong.MaxValue - address)
            {
                failure = HostViewFailure.WrongHostAddress;
                return false;
            }

            var ptr = mmap((nint)address, (nuint)size, GetNativeProtection(protection), MAP_SHARED | MAP_FIXED, backing.Descriptor, (long)offset);
            if (ptr == MAP_FAILED)
            {
                failure = HostViewFailure.FixedMapFailed;
                return false;
            }

            if ((ulong)ptr != address)
            {
                munmap(ptr, (nuint)size);
                failure = HostViewFailure.WrongHostAddress;
                return false;
            }

            failure = HostViewFailure.None;
            return true;
        }
    }

    public bool UnmapView(ulong address, ulong size) => RestoreHole(address, size);

    public bool CommitPrivate(ulong address, ulong size, HostPageProtection protection)
    {
        lock (HostViewRegions.Gate)
        {
            if (mprotect((nint)address, (nuint)size, GetNativeProtection(protection)) != 0) return false;
            HostViewRegions.Replace(address, size, HostMemory.MEM_COMMIT, HostViewRegions.RawProtection(protection));
            return true;
        }
    }

    public bool ReleasePrivate(ulong address, ulong size) => RestoreHole(address, size);

    public bool ChangeAccess(ulong address, ulong size, HostPageProtection protection)
    {
        lock (HostViewRegions.Gate)
        {
            if (mprotect((nint)address, (nuint)size, GetNativeProtection(protection)) != 0) return false;
            HostViewRegions.ChangeProtection(address, size, HostViewRegions.RawProtection(protection));
            return true;
        }
    }

    public bool FreeOwnedRange(ulong address, ulong size)
    {
        lock (HostViewRegions.Gate)
        {
            if (munmap((nint)address, (nuint)size) != 0) return false;
            HostViewRegions.Replace(address, size, HostMemory.MEM_FREE_STATE, HostMemory.PAGE_NOACCESS);
            return true;
        }
    }

    private static bool RestoreHole(ulong address, ulong size)
    {
        lock (HostViewRegions.Gate)
        {
            var ptr = mmap((nint)address, (nuint)size, PROT_NONE, MAP_PRIVATE | MAP_ANON | MAP_FIXED, -1, 0);
            if (ptr == MAP_FAILED || (ulong)ptr != address) return false;
            HostViewRegions.Replace(address, size, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            return true;
        }
    }

    private static int OpenSharedObject()
    {
        var name = $"/sharpemu-{Environment.ProcessId}-{Interlocked.Increment(ref _sharedObjectCounter)}";
        var descriptor = shm_open(name, O_RDWR | O_CREAT | O_EXCL, 0x180);
        if (descriptor >= 0)
        {
            shm_unlink(name);
        }

        return descriptor;
    }

    private static void ReleaseBackingObject(HostBackingObject backing)
    {
        munmap((nint)backing.AliasBase, (nuint)backing.Size);
        close(backing.Descriptor);
    }

    private static int GetNativeProtection(HostPageProtection protection) => protection switch
    {
        HostPageProtection.NoAccess => PROT_NONE,
        HostPageProtection.ReadOnly => PROT_READ,
        HostPageProtection.ReadWrite => PROT_READ | PROT_WRITE,
        HostPageProtection.Execute => PROT_EXEC,
        HostPageProtection.ReadExecute => PROT_READ | PROT_EXEC,
        HostPageProtection.ReadWriteExecute => PROT_READ | PROT_WRITE | PROT_EXEC,
        HostPageProtection.ExecuteWriteCopy => PROT_READ | PROT_WRITE | PROT_EXEC,
        _ => throw new ArgumentOutOfRangeException(nameof(protection), protection, null),
    };

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint mmap(nint address, nuint length, int protection, int flags, int descriptor, long offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(nint address, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mprotect(nint address, nuint length, int protection);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ftruncate(int descriptor, long length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int descriptor);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open(string name, int flags, int mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_unlink(string name);

    [LibraryImport("libSystem.B.dylib")]
    private static partial uint mach_task_self();

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_vm_allocate(uint task, ref ulong address, ulong size, int flags);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_vm_deallocate(uint task, ulong address, ulong size);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_vm_region_recurse(
        uint task, ref ulong address, ref ulong size, ref uint nestingDepth, uint* info, ref int infoCount);
}
