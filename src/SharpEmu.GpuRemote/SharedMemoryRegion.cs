// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>
/// A named POSIX shared-memory object (<c>shm_open</c>) mapped read-write and shared into this
/// process. The creator picks the name and hands it to the other process on its command line.
/// </summary>
/// <remarks>
/// The mapping, not the name, keeps the object alive: once both sides have mapped it the
/// creator unlinks the name (<see cref="Unlink"/>), so nothing is left in the namespace if
/// either process then crashes. The descriptor is closed right after mapping for the same
/// reason. Each process maps the object at an address of the kernel's choosing; nothing in the
/// transport stores a pointer in shared memory.
/// </remarks>
public sealed unsafe class SharedMemoryRegion : IDisposable
{
    /// <summary>PSHMNAMLEN: Darwin rejects longer names (the leading slash counts).</summary>
    public const int MaxNameLength = 31;

    /// <summary>The size every region is rounded to: the arm64 page (Rosetta's 4 KiB divides it).</summary>
    public const int Granularity = 16 * 1024;

    private static int _nameCounter;

    private nint _address;
    private int _unlinked;

    private SharedMemoryRegion(string name, nint address, nuint size, bool owner)
    {
        Name = name;
        _address = address;
        Size = size;
        IsOwner = owner;
        _unlinked = owner ? 0 : 1;
    }

    public string Name { get; }

    public nuint Size { get; }

    /// <summary>True for the process that created the object (and is responsible for unlinking it).</summary>
    public bool IsOwner { get; }

    public byte* Pointer => _address != 0 ? (byte*)_address : throw new ObjectDisposedException(nameof(SharedMemoryRegion));

    /// <summary>
    /// A fresh name for <see cref="Create"/>: the prefix, this process id and a counter, which
    /// stays well inside <see cref="MaxNameLength"/> for a short prefix.
    /// </summary>
    public static string CreateUniqueName(string prefix)
    {
        var counter = Interlocked.Increment(ref _nameCounter);
        var name = $"/{prefix}-{Environment.ProcessId:x}-{counter:x}";
        ValidateName(name);
        return name;
    }

    /// <summary>
    /// Creates a new object (failing if the name exists), sizes it to <paramref name="size"/>
    /// rounded up to <see cref="Granularity"/>, owner read-write only, and maps it. The memory
    /// starts zero-filled.
    /// </summary>
    public static SharedMemoryRegion Create(string name, nuint size)
    {
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfZero(size);
        size = (size + Granularity - 1) & ~(nuint)(Granularity - 1);

        var descriptor = RemoteNative.ShmOpen(name, RemoteNative.O_RDWR | RemoteNative.O_CREAT | RemoteNative.O_EXCL, 0x180);
        if (descriptor < 0)
        {
            throw Failure("shm_open(create)", name);
        }

        try
        {
            if (RemoteNative.ftruncate(descriptor, (long)size) != 0)
            {
                throw Failure("ftruncate", name);
            }

            return new SharedMemoryRegion(name, Map(descriptor, size, name), size, owner: true);
        }
        catch
        {
            _ = RemoteNative.ShmUnlink(name);
            throw;
        }
        finally
        {
            _ = RemoteNative.close(descriptor);
        }
    }

    /// <summary>Opens and maps an existing object at its full size.</summary>
    public static SharedMemoryRegion Open(string name)
    {
        ValidateName(name);
        // The mode argument is only read with O_CREAT.
        var descriptor = RemoteNative.ShmOpen(name, RemoteNative.O_RDWR, 0);
        if (descriptor < 0)
        {
            throw Failure("shm_open(open)", name);
        }

        try
        {
            if (!RemoteNative.TryGetFileStatus(descriptor, out _, out var size))
            {
                throw Failure("fstat", name);
            }

            if (size <= 0)
            {
                throw new IOException($"Shared memory object {name} has no size yet.");
            }

            return new SharedMemoryRegion(name, Map(descriptor, (nuint)size, name), (nuint)size, owner: false);
        }
        finally
        {
            _ = RemoteNative.close(descriptor);
        }
    }

    /// <summary>Removes the name; existing mappings stay valid. Idempotent, and a no-op for a non-owner.</summary>
    public void Unlink()
    {
        if (Interlocked.Exchange(ref _unlinked, 1) == 0)
        {
            _ = RemoteNative.ShmUnlink(Name);
        }
    }

    public void Dispose()
    {
        Unlink();
        var address = Interlocked.Exchange(ref _address, 0);
        if (address != 0)
        {
            _ = RemoteNative.munmap(address, Size);
        }
    }

    private static nint Map(int descriptor, nuint size, string name)
    {
        var address = RemoteNative.mmap(0, size, RemoteNative.PROT_READ | RemoteNative.PROT_WRITE, RemoteNative.MAP_SHARED, descriptor, 0);
        if (address == RemoteNative.MapFailed)
        {
            throw Failure("mmap", name);
        }

        return address;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name[0] != '/' || name.IndexOf('/', 1) >= 0 || name.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"A shared memory name is one '/' followed by at most {MaxNameLength - 1} characters without another '/': {name}",
                nameof(name));
        }
    }

    private static IOException Failure(string call, string name) =>
        new($"{call} failed for shared memory {name}: errno {Marshal.GetLastPInvokeError()}");
}
