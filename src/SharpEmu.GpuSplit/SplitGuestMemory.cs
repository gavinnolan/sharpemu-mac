// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.Versioning;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Host.MacOS;

namespace SharpEmu.GpuSplit;

/// <summary>
/// The guest process's backing as the GPU process maps it (docs/macos/split-shared-memory.md,
/// "What B does"): the offset mirror, the view table (read-only), the replica of every view at
/// guest address + K, and a translator over the table. Owns all four mappings.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class SplitGuestMemory : IDisposable
{
    private int _disposed;

    private SplitGuestMemory(HostBackingObject mirror, HostSharedRegion table, GuestReplica replica, GuestBackingExport export)
    {
        Mirror = mirror;
        Table = table;
        Replica = replica;
        ChunkBytes = export.ChunkBytes;
        ObjectBytes = export.ObjectBytes;
        Translator = new GuestMirrorTranslator(table, mirror);
    }

    public HostBackingObject Mirror { get; }

    public HostSharedRegion Table { get; }

    public GuestReplica Replica { get; }

    public GuestMirrorTranslator Translator { get; }

    public ulong ChunkBytes { get; }

    public ulong ObjectBytes { get; }

    /// <summary>
    /// Receives the guest memory the guest process serves under <paramref name="serviceName"/>
    /// (<see cref="MacOSHost.ServeGuestMemory"/>) and maps it, the replica at
    /// <paramref name="replicaBase"/> (K).
    /// </summary>
    public static SplitGuestMemory Connect(string serviceName, ulong replicaBase = GuestReplica.DefaultBase)
    {
        var clock = Stopwatch.StartNew();
        var host = MacOSHost.CreateGuestMirrorMemory();
        HostBackingObject? mirror = null;
        HostSharedRegion? table = null;
        GuestBackingExport export;
        using (var share = MacOSHost.ConnectGuestMemory(serviceName, TimeSpan.FromSeconds(20)))
        {
            export = share.Backing;
            if (!host.TryMapMirror(share.Backing, out mirror) || mirror is null)
            {
                throw new InvalidOperationException($"The guest backing ({share.Backing.Kind}, 0x{share.Backing.Size:X} bytes) could not be mapped.");
            }

            if (!host.TryMapSharedRegion(share.ViewTableDescriptor, share.ViewTableBytes, writable: false, out table) || table is null)
            {
                mirror.Dispose();
                throw new InvalidOperationException("The guest view table could not be mapped.");
            }
        }

        // The descriptor and ports are released here: the mappings keep the objects.
        try
        {
            var replica = GuestReplica.TryAttach(host, mirror, table, replicaBase)
                ?? throw new InvalidOperationException(
                    $"The guest replica's window at 0x{replicaBase:X} is taken; the GPU process cannot mirror the guest views there.");
            Console.Error.WriteLine(
                $"[GPU-SPLIT][INFO] Guest memory connected in {clock.Elapsed.TotalMilliseconds:F1} ms: {export.Kind} backing " +
                $"0x{export.Size:X}, mirror 0x{mirror.AliasBase:X}, replica +0x{replica.Base:X} at log record {replica.ReplayedSequence}.");
            return new SplitGuestMemory(mirror, table, replica, export);
        }
        catch
        {
            table.Dispose();
            mirror.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Replica.Dispose();
        Table.Dispose();
        Mirror.Dispose();
    }
}
