// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.HLE;

/// <summary>
/// Legacy raw host-memory entrypoints. All operations route through the same
/// process-selected provider as IHostPlatform.Memory; no independent region
/// table or signal-side provider initialization is present here.
/// </summary>
public static unsafe class HostMemory
{
    private static long _mappingGeneration;

    // Changes whenever a POSIX mapping or its protection changes, so callers can cache a
    // query result until the address space changes.
    public static long MappingGeneration => Volatile.Read(ref _mappingGeneration);

    /// <summary>
    /// Invalidates every cached <see cref="MappingGeneration"/> observation.
    /// </summary>
    /// <remarks>
    /// Host-package contract: the per-OS <c>SharpEmu.Host.*</c> backends own the
    /// syscalls that create, resize, protect and release mappings, so they are
    /// the only callers that know when the address space changed.
    /// </remarks>
    public static void OnMappingChanged() => Interlocked.Increment(ref _mappingGeneration);

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint MEM_FREE_STATE = 0x10000;
    public const uint MEM_PRIVATE = 0x20000;

    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_EXECUTE = 0x10;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    /// <summary>Win32 MEMORY_BASIC_INFORMATION (64-bit) layout.</summary>
    public struct BasicInfo
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    public static void* Alloc(void* address, nuint size, uint allocationType, uint protect) =>
        AllocCore(Host.HostMemoryAuthority.Current, address, size, allocationType, protect);

    public static bool Free(void* address, nuint size, uint freeType) =>
        FreeCore(Host.HostMemoryAuthority.Current, address, size, freeType);

    public static bool Protect(void* address, nuint size, uint newProtect, out uint oldProtect) =>
        ProtectCore(Host.HostMemoryAuthority.Current, address, size, newProtect, out oldProtect);

    public static nuint Query(void* address, out BasicInfo info) =>
        QueryCore(Host.HostMemoryAuthority.Current, address, out info);

    public static void FlushInstructionCache(void* address, nuint size) =>
        FlushCore(Host.HostMemoryAuthority.Current, address, size);

    // Startup and public calls share these exact interface-dispatch cells.
    // Do not inline them into a cold callback-side dispatch site.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void* AllocCore(Host.IHostMemoryRaw provider, void* address, nuint size, uint allocationType, uint protect) =>
        (void*)provider.AllocateRaw((ulong)address, (ulong)size, allocationType, protect);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FreeCore(Host.IHostMemoryRaw provider, void* address, nuint size, uint freeType) =>
        provider.FreeRaw((ulong)address, (ulong)size, freeType);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProtectCore(Host.IHostMemoryRaw provider, void* address, nuint size, uint protect, out uint old) =>
        provider.ProtectRaw((ulong)address, (ulong)size, protect, out old);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nuint QueryCore(Host.IHostMemoryRaw provider, void* address, out BasicInfo info) =>
        provider.QueryRaw((ulong)address, out info);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FlushCore(Host.IHostMemoryRaw provider, void* address, nuint size) =>
        provider.FlushInstructionCache((ulong)address, (ulong)size);

    /// <summary>Exercises migrated entries on the startup stack before publication.</summary>
    /// <remarks>Owns one private native page. This is dispatch warming, not a signal-safety or lifetime receipt.</remarks>
    internal static void WarmUpProvider(Host.IHostMemoryRaw provider)
    {
        var generation = MappingGeneration;
        if (AllocCore(provider, null, 0, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE) != null ||
            FreeCore(provider, null, 0, MEM_RELEASE) ||
            ProtectCore(provider, null, 0, PAGE_NOACCESS, out _) ||
            QueryCore(provider, null, out _) != (nuint)sizeof(BasicInfo) ||
            MappingGeneration != generation)
            throw new InvalidOperationException("Host-memory startup guard probes failed; provider was not published.");

        var pageSize = (nuint)Environment.SystemPageSize;
        var page = AllocCore(provider, null, pageSize, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (page == null)
            throw new InvalidOperationException("Could not allocate the private host-memory startup page; provider was not published.");
        Exception? probeFailure = null;
        try
        {
            if (QueryCore(provider, page, out var before) != (nuint)sizeof(BasicInfo) ||
                before.AllocationBase != (ulong)page || before.RegionSize < (ulong)pageSize ||
                before.State != MEM_COMMIT || before.Protect != PAGE_READWRITE ||
                !ProtectCore(provider, page, pageSize, PAGE_READONLY, out var old) || old != PAGE_READWRITE ||
                !ProtectCore(provider, page, pageSize, PAGE_READWRITE, out old) || old != PAGE_READONLY ||
                QueryCore(provider, page, out var after) != (nuint)sizeof(BasicInfo) || after.Protect != PAGE_READWRITE)
                throw new InvalidOperationException("Host-memory positive startup probes failed; provider was not published.");
            FlushCore(provider, page, pageSize);

        }
        catch (Exception failure) { probeFailure = failure; throw; }
        finally
        {
            // This exact unpublished allocation remains exclusively owned here.
            // Failed cleanup prevents publication; no inferred success is accepted.
            var released = false;
            var cleanupThrew = false;
            try { released = FreeCore(provider, page, 0, MEM_RELEASE); }
            catch (Exception cleanupFailure)
            {
                cleanupThrew = true;
                Host.HostMemoryAuthority.RecordStartupCleanupFailure((ulong)page, threw: true);
                // Keep an ordinary secondary cleanup error observable without
                // replacing the original probe failure. Fatal cleanup still propagates.
                if (probeFailure is null || cleanupFailure is OutOfMemoryException || cleanupFailure is AccessViolationException) throw;
            }
            if (!cleanupThrew && !released)
            {
                Host.HostMemoryAuthority.RecordStartupCleanupFailure((ulong)page, threw: false);
                if (probeFailure is null)
                    throw new InvalidOperationException("Could not release the private host-memory startup page; provider was not published.");
            }
        }
    }
}
