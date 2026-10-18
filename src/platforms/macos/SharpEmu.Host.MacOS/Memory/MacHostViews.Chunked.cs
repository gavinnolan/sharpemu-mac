// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

/// <summary>
/// SHARPEMU_GUEST_BACKING=chunked: the guest backing as an array of 64 KiB purgeable named
/// memory entries instead of one POSIX shm object.
///
/// <para>
/// Why: under Rosetta (a 4 KiB VM map) no madvise-style call frees a page of the shm object
/// while the alias and the guest views map it, so memory the guest frees stays resident for
/// the life of the process (artifacts/memopt/single-copy-textures.md §3). A purgeable object
/// can be emptied in place: <c>mach_vm_purgable_control(EMPTY)</c> frees its pages at once,
/// every mapping stays valid and reads zero, and NONVOLATILE makes it ordinary memory again.
/// One object per 64 KiB because that is the granule titles allocate and BatchMap direct
/// memory in (Demon's Souls scatters a texture over 64 KiB physical pages); a larger chunk
/// would mix live and freed bytes and could rarely be emptied. The flexible region uses the
/// same size: it is 448 MiB (7168 chunks) and is freed by unmapping, so one rule serves both.
/// </para>
///
/// <para>
/// Layout: a reservation of the alias size, 64 KiB aligned, with each chunk mapped over it
/// (<c>mach_vm_map</c> FIXED|OVERWRITE, read-write, maximum read-write); the entry ports are
/// dropped at once, the mapping keeps each object alive. A guest view is
/// <c>mach_vm_remap(copy=FALSE)</c> of the alias range: the same objects, so it aliases the
/// alias and every other view exactly as an shm view does, for any page-aligned offset and
/// size, then <c>mprotect</c> to the view's protection. Execute is dropped exactly as the shm
/// view drops it: <c>mmap(MAP_SHARED, PROT_READ|PROT_WRITE|PROT_EXEC)</c> of the shm object
/// succeeds but maps read-write with a read-write maximum, on arm64 and under Rosetta alike
/// (artifacts/memopt/exp/chunked-rwx.c), so an executable guest view has never been executable
/// and a later protection change that asks for execute fails the same way. Holes, protection changes and unmaps
/// are the same calls as for shm. Every chunk mapping carries the <see cref="GuestChunkTag"/>
/// VM tag and a view inherits it from the alias, so scripts/macos/memstat.c classifies the objects as
/// guest memory whether or not a view maps them.
/// </para>
/// </summary>
internal sealed unsafe partial class MacHostViews
{
    internal const ulong GuestChunkBytes = 64 * 1024;

    // VM_MEMORY_APPLICATION_SPECIFIC_11: the user tag of chunk mappings (memstat keys on it).
    internal const int GuestChunkTag = 250;

    private const int VM_FLAGS_ANYWHERE = 0x0001;
    private const int VM_FLAGS_OVERWRITE = 0x4000;
    private const int GuestChunkTagFlags = GuestChunkTag << 24;
    private const int MAP_MEM_NAMED_CREATE = 0x020000;
    private const int MAP_MEM_PURGABLE = 0x040000;
    private const int VM_PROT_NONE = 0;
    private const int VM_PROT_READ_WRITE = 3;
    private const uint VM_INHERIT_SHARE = 0;
    private const int VM_PURGABLE_SET_STATE = 0;
    private const int VM_PURGABLE_NONVOLATILE = 0;
    private const int VM_PURGABLE_EMPTY = 2;

    // Chunks emptied before one residency check: a 4096-entry mincore vector at 4 KiB pages.
    private const ulong DiscardBatchChunks = 256;

    /// <summary>Test override of SHARPEMU_GUEST_BACKING for backings created on this thread; null reads the variable.</summary>
    [ThreadStatic]
    internal static bool? ChunkedBackingForTests;

    // The default stays the shm object until the chunked backing is measured in a game:
    // flipping it is this one comparison.
    private static bool UseChunkedBacking => ChunkedBackingForTests ?? string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GUEST_BACKING"), "chunked", StringComparison.OrdinalIgnoreCase);

    // Called under HostViewRegions.Gate after the ledger preflight, like the shm path.
    private bool TryCreateChunkedBackingLocked(ulong size, out HostBackingObject? backing, out HostViewFailure failure)
    {
        backing = null;
        failure = HostViewFailure.BackingUnavailable;
        if (FailAliasMapForTests)
        {
            return false;
        }

        var aliasSize = AlignUp(size, PageSize);
        var task = mach_task_self();
        ulong alias = 0;
        if (mach_vm_map(task, ref alias, aliasSize, GuestChunkBytes - 1, VM_FLAGS_ANYWHERE | GuestChunkTagFlags, 0, 0, 0,
                VM_PROT_NONE, VM_PROT_READ_WRITE, VM_INHERIT_SHARE) != KERN_SUCCESS)
        {
            return false;
        }

        for (ulong offset = 0; offset < aliasSize; offset += GuestChunkBytes)
        {
            if (!TryMapChunk(task, alias + offset, Math.Min(GuestChunkBytes, aliasSize - offset)))
            {
                // Only this new reservation exists; deallocating it drops every chunk made so far.
                mach_vm_deallocate(task, alias, aliasSize);
                return false;
            }
        }

        try
        {
            backing = new HostBackingObject(alias, size, PageSize, ReleaseOwnedBackingObject)
            {
                ChunkBytes = GuestChunkBytes,
                DiscardChunks = DiscardChunks,
            };
            MacMappedCopyState.Ledger?.RegisterBacking(backing);
        }
        catch (Exception error)
        {
            // No guest view exists yet; the alias is the only owner of the chunks.
            var deallocated = mach_vm_deallocate(task, alias, aliasSize);
            if (deallocated != KERN_SUCCESS)
            {
                MacMappedCopyState.Ledger?.QuarantineUnknownAlias();
                try { error.Data["BackingSetupCleanup"] = $"alias=0x{alias:X};size={aliasSize};mach_vm_deallocate={deallocated}"; }
                catch { } // primary construction/publication failure remains authoritative
            }

            backing = null;
            throw;
        }

        failure = HostViewFailure.None;
        return true;
    }

    private static bool TryMapChunk(uint task, ulong address, ulong length)
    {
        var entrySize = length;
        if (mach_make_memory_entry_64(task, ref entrySize, 0, MAP_MEM_NAMED_CREATE | MAP_MEM_PURGABLE | VM_PROT_READ_WRITE,
                out var entry, 0) != KERN_SUCCESS)
        {
            return false;
        }

        var target = address;
        var mapped = entrySize >= length && mach_vm_map(task, ref target, length, 0,
            VM_FLAGS_FIXED | VM_FLAGS_OVERWRITE | GuestChunkTagFlags, entry, 0, 0, VM_PROT_READ_WRITE, VM_PROT_READ_WRITE,
            VM_INHERIT_SHARE) == KERN_SUCCESS && target == address;
        // The mapping holds the object; the port is not needed again (views remap the alias).
        mach_port_deallocate(task, entry);
        return mapped;
    }

    // Called under backing.Gate with the range validated, in place of the shm MAP_FIXED view.
    private bool TryMapChunkedViewCore(HostBackingObject backing, ulong address, ulong offset, ulong size,
        HostPageProtection protection, out HostViewFailure failure)
    {
        var task = mach_task_self();
        var target = address;
        // No tag in the flags: remap refuses one (KERN_INVALID_ARGUMENT) and copies the alias tag.
        if (mach_vm_remap(task, ref target, size, 0, VM_FLAGS_FIXED | VM_FLAGS_OVERWRITE, task,
                backing.AliasBase + offset, 0, out _, out _, VM_INHERIT_SHARE) != KERN_SUCCESS)
        {
            failure = HostViewFailure.FixedMapFailed;
            return false;
        }

        if (target != address)
        {
            mach_vm_deallocate(task, target, size);
            failure = HostViewFailure.WrongHostAddress;
            return false;
        }

        // The view's maximum is the alias's read-write; execute is dropped as for shm (above).
        if (mprotect((nint)address, (nuint)size, GetNativeProtection(protection) & ~PROT_EXEC) != 0)
        {
            // Put the hole back, as an unmap would, rather than leave a read-write view.
            _ = mmap((nint)address, (nuint)size, PROT_NONE, MAP_PRIVATE | MAP_ANON | MAP_FIXED, -1, 0);
            failure = HostViewFailure.FixedMapFailed;
            return false;
        }

        failure = HostViewFailure.None;
        return true;
    }

    // HostBackingObject.DiscardChunks. EMPTY frees a chunk's pages (resident and compressed)
    // at once, through every mapping; NONVOLATILE right after makes it ordinary memory again
    // that reads zero. A chunk wired by a command buffer keeps its pages and contents through
    // EMPTY, and one touched meanwhile has pages again, so whatever is still resident after a
    // batch is zero-filled instead: the caller is promised zeros either way.
    private static HostBackingDiscard DiscardChunks(HostBackingObject backing, ulong offset, ulong size)
    {
        var chunk = backing.ChunkBytes;
        if (chunk == 0 || size == 0 || offset % chunk != 0 || offset >= backing.Size || size > backing.Size - offset ||
            (size % chunk != 0 && offset + size != backing.Size))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"0x{offset:X}+0x{size:X} is not whole chunks of the backing.");
        }

        var task = mach_task_self();
        var alias = backing.AliasBase;
        // The last chunk reaches over the alias padding past Size.
        var end = offset + size == backing.Size ? backing.NativeAliasSize : offset + size;
        var page = (ulong)Environment.SystemPageSize;
        var vector = stackalloc byte[(int)(DiscardBatchChunks * chunk / page)];
        var wasEmptied = stackalloc bool[(int)DiscardBatchChunks];
        ulong emptied = 0, emptiedChunks = 0, zeroFilled = 0;
        for (var batch = offset; batch < end; batch += DiscardBatchChunks * chunk)
        {
            var batchEnd = Math.Min(end, batch + DiscardBatchChunks * chunk);
            var slot = 0;
            for (var cursor = batch; cursor < batchEnd; cursor += chunk, slot++)
            {
                var state = VM_PURGABLE_EMPTY;
                wasEmptied[slot] = mach_vm_purgable_control(task, alias + cursor, VM_PURGABLE_SET_STATE, ref state) == KERN_SUCCESS;
                if (!wasEmptied[slot])
                {
                    continue;
                }

                state = VM_PURGABLE_NONVOLATILE;
                if (mach_vm_purgable_control(task, alias + cursor, VM_PURGABLE_SET_STATE, ref state) != KERN_SUCCESS)
                {
                    // An object left empty or volatile could lose later writes: never continue.
                    throw new InvalidOperationException(
                        $"The guest backing chunk at offset 0x{cursor:X} could not be made nonvolatile again.");
                }
            }

            // No residency answer means every chunk is treated as still resident.
            var known = mincore((nint)(alias + batch), (nuint)(batchEnd - batch), vector) == 0;
            slot = 0;
            for (var cursor = batch; cursor < batchEnd; cursor += chunk, slot++)
            {
                var length = Math.Min(chunk, batchEnd - cursor);
                var resident = !known || !wasEmptied[slot];
                var first = (cursor - batch) / page;
                for (var index = first; index < first + length / page && !resident; index++)
                {
                    resident = (vector[index] & 1) != 0;
                }

                if (resident)
                {
                    NativeMemory.Clear((void*)(alias + cursor), (nuint)length);
                    zeroFilled += length;
                }
                else
                {
                    emptied += length;
                    emptiedChunks++;
                }
            }
        }

        return new HostBackingDiscard(emptied, emptiedChunks, zeroFilled);
    }

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_vm_map(uint task, ref ulong address, ulong size, ulong mask, int flags,
        uint memoryObject, ulong offset, int copy, int currentProtection, int maximumProtection, uint inheritance);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_vm_remap(uint targetTask, ref ulong targetAddress, ulong size, ulong mask, int flags,
        uint sourceTask, ulong sourceAddress, int copy, out int currentProtection, out int maximumProtection,
        uint inheritance);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_make_memory_entry_64(uint task, ref ulong size, ulong offset, int permission,
        out uint objectHandle, uint parentEntry);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_vm_purgable_control(uint task, ulong address, int control, ref int state);

    [LibraryImport("libSystem.B.dylib")]
    private static partial int mach_port_deallocate(uint task, uint name);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mincore(nint address, nuint length, byte* vector);
}
