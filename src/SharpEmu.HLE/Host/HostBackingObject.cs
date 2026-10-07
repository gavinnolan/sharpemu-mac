// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

public sealed class HostBackingObject : IDisposable
{
    private readonly Action<HostBackingObject> _release;

    /// <summary>
    /// The lock the owning backend holds while it maps, unmaps or disposes this
    /// object, so a view operation cannot race the release callback.
    /// </summary>
    /// <remarks>Host-package contract.</remarks>
    public object Gate { get; } = new();

    /// <summary>
    /// The OS handle for the backing storage: a Mach memory entry on macOS, a
    /// section handle on Windows. Zero when the backend identifies the storage
    /// by <see cref="Descriptor"/> instead.
    /// </summary>
    /// <remarks>Host-package contract; written by the owning backend.</remarks>
    public nint Handle { get; init; }

    /// <summary>
    /// The file descriptor for the backing storage on the POSIX backends
    /// (memfd/shm), or -1 when the backend identifies it by
    /// <see cref="Handle"/> instead.
    /// </summary>
    /// <remarks>Host-package contract; written by the owning backend.</remarks>
    public int Descriptor { get; init; } = -1;

    /// <summary>
    /// Creates a backing object for storage the caller has already reserved at
    /// <paramref name="aliasBase"/>.
    /// </summary>
    /// <param name="aliasBase">Host address of the reserved alias window.</param>
    /// <param name="size">Size of the backing storage in bytes.</param>
    /// <param name="release">
    /// Invoked once, under <see cref="Gate"/>, when the object is disposed; the
    /// owning backend releases the OS storage there.
    /// </param>
    /// <remarks>
    /// Host-package contract: the per-OS packages are the only code that creates
    /// backing storage, so they are the only callers.
    /// </remarks>
    public HostBackingObject(ulong aliasBase, ulong size, Action<HostBackingObject> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        AliasBase = aliasBase;
        Size = size;
        _release = release;
    }

    public ulong AliasBase { get; }

    public ulong Size { get; }

    /// <summary>Whether <see cref="Dispose"/> has already released the storage.</summary>
    /// <remarks>Host-package contract; read under <see cref="Gate"/>.</remarks>
    public bool IsDisposed { get; private set; }

    // Wait for an active mapping operation. The owner must remove guest views before disposal.
    public void Dispose()
    {
        lock (Gate)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _release(this);
        }
    }
}
