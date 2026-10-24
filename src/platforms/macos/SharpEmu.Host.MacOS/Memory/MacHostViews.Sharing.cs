// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

/// <summary>
/// Sharing the guest backing with another process. The two-process split runs the Metal backend in
/// a native arm64 process while this one runs the guest under Rosetta; that process maps the whole
/// backing as an offset mirror, at an address of its own, and finds guest addresses in it through the
/// shared view table (<see cref="SharpEmu.HLE.GuestMemory.GuestViewTable"/>).
///
/// <para>
/// Opt-in: <c>SHARPEMU_GUEST_BACKING_SHAREABLE=1</c>, or <c>SHARPEMU_GPU_PROCESS=1</c>; nothing changes
/// without one. The shm backing keeps nothing extra: its descriptor already lives as long as the
/// backing. The object stays unlinked, so it can only be handed over, never opened by name, and a
/// crash cannot leave a 16 GiB shm object behind until the next boot. The chunked backing normally
/// drops each named entry's port right after mapping it; shareable, it keeps them (one per 2 MiB
/// object, 8416 for the 16.4 GiB backing) and releases them with the backing. Another process
/// receives either kind over Mach (<see cref="MacGuestMemoryRendezvous"/>).
/// </para>
///
/// <para>
/// A mirror maps the same objects: the shm object once, or each named entry at its offset over one
/// 64 KiB-aligned reservation, laid out exactly like the alias. It is read-write with no
/// execute, and coherent with the alias and every guest view in every process, including chunk
/// releases (an emptied purgeable object reads zero through every mapping of it). Each entry must
/// be a whole number of this process's pages: an arm64 process maps 16 KiB pages, and the 2 MiB
/// objects always are.
/// </para>
/// </summary>
internal sealed unsafe partial class MacHostViews : IHostGuestMirrorMemory
{
    /// <summary>Test override of the shareable switch for backings created on this thread; null reads the variables.</summary>
    [ThreadStatic]
    internal static bool? ShareableBackingForTests;

    private static bool UseShareableBacking => ShareableBackingForTests ??
        (IsSwitchOn("SHARPEMU_GUEST_BACKING_SHAREABLE") || IsSwitchOn("SHARPEMU_GPU_PROCESS"));

    private static bool IsSwitchOn(string name) => Environment.GetEnvironmentVariable(name) == "1";

    private GuestBackingExport SharedObjectExport(int descriptor, ulong size) =>
        new(GuestBackingExportKind.SharedMemoryObject, size, AlignUp(size, PageSize), descriptor, [], 0, 0);

    // The ports a shareable chunked backing kept; no mapping depends on them.
    private static void ReleaseEntries(IReadOnlyList<GuestBackingEntry>? entries)
    {
        if (entries is null) return;
        var task = mach_task_self();
        foreach (var entry in entries) mach_port_deallocate(task, entry.Handle);
    }

    public bool TryCreateSharedRegion(ulong size, out HostSharedRegion? region)
    {
        region = null;
        if (size == 0 || size > long.MaxValue - PageSize) return false;
        var length = AlignUp(size, PageSize);
        var descriptor = OpenSharedObject();
        if (descriptor < 0) return false;
        if (ftruncate(descriptor, (long)length) != 0 || !TryMapShared(descriptor, length, PROT_READ | PROT_WRITE, out var address))
        {
            close(descriptor);
            return false;
        }

        region = new HostSharedRegion(address, length, descriptor, writable: true, static owned =>
        {
            munmap((nint)owned.Address, (nuint)owned.Size);
            close(owned.Descriptor);
        });
        return true;
    }

    public bool TryMapSharedRegion(int descriptor, ulong size, bool writable, out HostSharedRegion? region)
    {
        region = null;
        if (descriptor < 0 || size == 0 || size > long.MaxValue - PageSize) return false;
        var length = AlignUp(size, PageSize);
        if (!TryMapShared(descriptor, length, writable ? PROT_READ | PROT_WRITE : PROT_READ, out var address)) return false;
        // The descriptor stays the caller's: only the mapping goes with the region.
        region = new HostSharedRegion(address, length, descriptor, writable, static mapped =>
            munmap((nint)mapped.Address, (nuint)mapped.Size));
        return true;
    }

    public bool TryMapMirror(GuestBackingExport export, out HostBackingObject? mirror)
    {
        ArgumentNullException.ThrowIfNull(export);
        mirror = null;
        if (export.MappedSize > long.MaxValue - PageSize) return false;
        var size = AlignUp(export.MappedSize, PageSize);
        var task = mach_task_self();
        if (!TryReserveAboveGuest(task, size, out var address)) return false;
        var mapped = export.Kind == GuestBackingExportKind.SharedMemoryObject
            ? mmap((nint)address, (nuint)export.Size, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_FIXED, export.Descriptor, 0) == (nint)address
            : TryMapMirrorEntries(task, address, size, export);
        if (!mapped)
        {
            mach_vm_deallocate(task, address, size);
            return false;
        }

        try
        {
            // Never DiscardChunks: only the owner releases backing memory.
            mirror = new HostBackingObject(address, export.Size, PageSize, backing => ReleaseMirror(backing, size))
            {
                ChunkBytes = export.ChunkBytes,
                ObjectBytes = export.ObjectBytes,
                CreateGpuAlias = export.Kind == GuestBackingExportKind.MemoryEntries && export.ChunkBytes == GuestChunkBytes
                    ? ChunkedGpuAlias.TryCreate
                    : null,
            };
        }
        catch
        {
            mach_vm_deallocate(task, address, size);
            throw;
        }

        return true;
    }

    // Replica views live at addresses no guest owns (guest address + the replica's constant), so
    // they bypass the mapping ledger and the view-region table, which describe guest memory only.
    public bool TryReserveReplica(ulong address, ulong size) =>
        HostViewMemory.IsValidRange(address, size) && address % PageSize == 0 && size % PageSize == 0 &&
        ReserveHoleDarwinFixed(address, size) == address;

    public bool TryMapReplicaView(HostBackingObject mirror, ulong address, ulong offset, ulong size)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (address % PageSize != 0 || offset % PageSize != 0 || size == 0 || size % PageSize != 0 ||
            offset >= mirror.NativeAliasSize || size > mirror.NativeAliasSize - offset || size > ulong.MaxValue - address)
            return false;
        var task = mach_task_self();
        var target = address;
        // copy=FALSE: the mirror's own objects, read-write like the mirror, over the reservation.
        if (mach_vm_remap(task, ref target, size, 0, VM_FLAGS_FIXED | VM_FLAGS_OVERWRITE, task, mirror.AliasBase + offset, 0,
                out _, out _, VM_INHERIT_SHARE) != KERN_SUCCESS)
            return false;
        if (target == address) return true;
        mach_vm_deallocate(task, target, size);
        return false;
    }

    public bool TryUnmapReplicaView(ulong address, ulong size) =>
        HostViewMemory.IsValidRange(address, size) &&
        mmap((nint)address, (nuint)size, PROT_NONE, MAP_PRIVATE | MAP_ANON | MAP_FIXED, -1, 0) == (nint)address;

    public bool TryReleaseReplica(ulong address, ulong size) =>
        HostViewMemory.IsValidRange(address, size) && mach_vm_deallocate(mach_task_self(), address, size) == KERN_SUCCESS;

    // Each entry at its offset, end to end from 0, as TryCreateChunkedBackingLocked laid them out.
    private static bool TryMapMirrorEntries(uint task, ulong address, ulong size, GuestBackingExport export)
    {
        ulong expected = 0;
        foreach (var entry in export.Entries)
        {
            if (entry.Offset != expected || entry.Size == 0 || entry.Size > size - entry.Offset) return false;
            var target = address + entry.Offset;
            if (mach_vm_map(task, ref target, entry.Size, 0, VM_FLAGS_FIXED | VM_FLAGS_OVERWRITE | GuestChunkTagFlags, entry.Handle, 0, 0,
                    VM_PROT_READ_WRITE, VM_PROT_READ_WRITE, VM_INHERIT_SHARE) != KERN_SUCCESS || target != address + entry.Offset)
                return false;
            expected += entry.Size;
        }

        return expected >= export.Size;
    }

    private static HostBackingReleaseResult ReleaseMirror(HostBackingObject mirror, ulong size)
    {
        var result = mach_vm_deallocate(mach_task_self(), mirror.AliasBase, size);
        return result == KERN_SUCCESS
            ? new HostBackingReleaseResult(HostBackingReleaseStatus.Released)
            : new HostBackingReleaseResult(HostBackingReleaseStatus.AliasOutcomeUnknown, result);
    }

    // A shared mapping of a descriptor above guest VA: the kernel's own pick could land on a
    // title's fixed arena, as the comment on HostAliasStart explains for the alias.
    private bool TryMapShared(int descriptor, ulong length, int protection, out ulong address)
    {
        var task = mach_task_self();
        if (!TryReserveAboveGuest(task, length, out address)) return false;
        if (mmap((nint)address, (nuint)length, protection, MAP_SHARED | MAP_FIXED, descriptor, 0) == (nint)address) return true;
        mach_vm_deallocate(task, address, length);
        address = 0;
        return false;
    }

    // An inaccessible, 64 KiB-aligned reservation found from HostAliasStart up, for a fixed mapping over it.
    private static bool TryReserveAboveGuest(uint task, ulong size, out ulong address)
    {
        address = HostAliasStart;
        if (mach_vm_map(task, ref address, size, GuestChunkBytes - 1, VM_FLAGS_ANYWHERE, 0, 0, 0,
                VM_PROT_NONE, VM_PROT_READ_WRITE, VM_INHERIT_SHARE) == KERN_SUCCESS)
            return true;
        address = 0;
        return false;
    }
}
