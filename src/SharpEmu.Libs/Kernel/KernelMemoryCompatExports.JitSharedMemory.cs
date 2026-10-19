// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Libs.Kernel;

// JIT shared memory: an object of flexible memory reached through descriptors. Each
// descriptor carries a maximum protection (an alias may only narrow it) and every map of a
// descriptor views the whole object, so a writable view and an executable view share bytes.
// The object, not its views, owns the backing: it goes back to the flexible pool once no
// descriptor and no view is left.
public static partial class KernelMemoryCompatExports
{
    private const int JitProtectionMask = OrbisProtCpuRead | OrbisProtCpuWrite | OrbisProtCpuExec;

    private sealed class JitObject(ulong offset, ulong length, string? name)
    {
        public ulong Offset { get; } = offset;
        public ulong Length { get; } = length;
        public string? Name { get; } = name;
        public int Descriptors { get; set; }
    }

    private readonly record struct JitDescriptor(JitObject Object, int MaxProtection);

    private static readonly Dictionary<int, JitDescriptor> _jitDescriptors = new();
    private static readonly List<JitObject> _jitObjects = new();

    private static void ResetJitSharedMemoryLocked()
    {
        _jitDescriptors.Clear();
        _jitObjects.Clear();
    }

    // Write or execute access implies read.
    private static int NormalizeJitProtection(int protection) =>
        (protection & (OrbisProtCpuWrite | OrbisProtCpuExec)) != 0 ? protection | OrbisProtCpuRead : protection;

    private static bool TryCloseJitSharedMemory(int fd)
    {
        lock (_memoryGate)
        {
            if (!_jitDescriptors.Remove(fd, out var descriptor))
                return false;
            descriptor.Object.Descriptors--;
            ReleaseUnreferencedJitObjectsLocked();
            return true;
        }
    }

    private static void ReleaseUnreferencedJitObjectsLocked()
    {
        for (var index = _jitObjects.Count - 1; index >= 0; index--)
        {
            var jit = _jitObjects[index];
            if (jit.Descriptors != 0 || _mappedRegions.Values.Any(region => region.IsJit &&
                    region.BackingOffset >= jit.Offset && region.BackingOffset - jit.Offset < jit.Length))
                continue;
            _flexibleBacking.ReleaseRun(jit.Offset, jit.Length);
            ReleaseFreedBackingLocked(jit.Offset, jit.Length, _flexibleBacking.FreeRangeContaining(jit.Offset));
            _jitObjects.RemoveAt(index);
        }
    }

    [SysAbiExport(
        Nid = "avvJ3J0H0EY",
        ExportName = "sceKernelJitCreateSharedMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelJitCreateSharedMemory(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        var maxProtection = unchecked((int)ctx[CpuRegister.Rdx]);
        var output = ctx[CpuRegister.Rcx];
        if (output == 0 || length == 0 || !IsAligned(length, OrbisPageSize) || (maxProtection & ~JitProtectionMask) != 0)
            return MemoryInvalidArgument;
        string? name = null;
        if (nameAddress != 0)
        {
            var nameResult = TryReadRangeName(ctx, nameAddress, out name);
            if (nameResult != 0)
                return nameResult;
        }
        if (!ctx.TryWriteUInt32(output, unchecked((uint)-1)))
            return MemoryFault;

        lock (_memoryGate)
        {
            var space = ResolveBackingSpace(ctx);
            if (space is null || !_flexibleBacking.TryAllocateRun(space, length, out var offset))
                return MemoryNoSpace;
            var fd = AllocateGuestFileDescriptor();
            if (!ctx.TryWriteUInt32(output, (uint)fd))
            {
                _flexibleBacking.ReleaseRun(offset, length);
                return MemoryFault;
            }
            var jit = new JitObject(offset, length, name) { Descriptors = 1 };
            _jitObjects.Add(jit);
            _jitDescriptors[fd] = new JitDescriptor(jit, NormalizeJitProtection(maxProtection));
            return 0;
        }
    }

    [SysAbiExport(
        Nid = "MR221Mwo0Pc",
        ExportName = "sceKernelJitCreateAliasOfSharedMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelJitCreateAliasOfSharedMemory(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var maxProtection = unchecked((int)ctx[CpuRegister.Rsi]);
        var output = ctx[CpuRegister.Rdx];
        if (output == 0 || (maxProtection & ~JitProtectionMask) != 0)
            return MemoryInvalidArgument;
        if (!ctx.TryWriteUInt32(output, unchecked((uint)-1)))
            return MemoryFault;
        lock (_memoryGate)
        {
            if (!_jitDescriptors.TryGetValue(fd, out var descriptor))
                return MemoryBadDescriptor;
            var normalized = NormalizeJitProtection(maxProtection);
            if ((normalized & ~descriptor.MaxProtection) != 0)
                return MemoryAccessDenied;
            var alias = AllocateGuestFileDescriptor();
            if (!ctx.TryWriteUInt32(output, (uint)alias))
                return MemoryFault;
            descriptor.Object.Descriptors++;
            _jitDescriptors[alias] = new JitDescriptor(descriptor.Object, normalized);
            return 0;
        }
    }

    [SysAbiExport(
        Nid = "YKT49TOLQWs",
        ExportName = "sceKernelJitMapSharedMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelJitMapSharedMemory(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var protection = unchecked((int)ctx[CpuRegister.Rsi]);
        var output = ctx[CpuRegister.Rdx];
        return RunMappingTransaction(() => JitMapSharedMemoryCore(ctx, fd, protection, output));
    }

    private static int JitMapSharedMemoryCore(CpuContext ctx, int fd, int protection, ulong output)
    {
        if (output == 0 || (protection & ~JitProtectionMask) != 0 ||
            !TryDecodeMappedProtection(protection, out var mode))
            return MemoryInvalidArgument;
        if (!ctx.TryReadUInt64(output, out var hint) || !ctx.TryWriteUInt64(output, hint))
            return MemoryFault;
        lock (_memoryGate)
        {
            if (!_jitDescriptors.TryGetValue(fd, out var descriptor))
                return MemoryBadDescriptor;
            if ((NormalizeJitProtection(protection) & ~descriptor.MaxProtection) != 0)
                return MemoryAccessDenied;
            var jit = descriptor.Object;
            var space = ResolveBackingSpace(ctx);
            if (space is null ||
                !TrySelectReplacingBackingAddress(space, IsAligned(hint, OrbisPageSize) ? hint : 0, jit.Length,
                    OrbisPageSize, 0, out var address, out var replaced))
                return MemoryNoSpace;
            if (!space.TryMapBacked(address, jit.Length, jit.Offset, mode, out _))
            {
                RestoreReplacedLocked(space, address, jit.Length, replaced);
                return MemoryNoSpace;
            }
            if (!ctx.TryWriteUInt64(output, address))
            {
                _ = space.TryUnmapBacked(address, jit.Length);
                RestoreReplacedLocked(space, address, jit.Length, replaced);
                return MemoryFault;
            }
            CommitReplacedLocked(replaced);
            ReplaceMappedRegionRangeLocked(new MappedRegion(address, jit.Length, protection, false, false, 0,
                jit.Offset, Name: jit.Name, Traits: MappingTraits.Jit));
            GuestGpuMemoryHook.NoteMapped(address, jit.Length, mode);
            return 0;
        }
    }
}
