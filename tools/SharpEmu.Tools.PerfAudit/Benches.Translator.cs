// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;

namespace SharpEmu.PerfAudit;

/// <summary>k17: GuestMirrorTranslator hit path, miss rate and miss allocation over view-table layouts.</summary>
internal static unsafe class TranslatorBenches
{
    private const ulong Base = 0x20_0000_0000;
    private const ulong Block = 0x10000;
    private const ulong MirrorBase = 0x7100_0000_0000;
    private const ulong MirrorBytes = 1UL << 36;
    private const int Ops = 4096;

    private static GuestViewTable NewTable(int capacity)
    {
        var bytes = GuestViewTable.RegionBytes(capacity, 256);
        var address = (ulong)NativeMemory.AllocZeroed((nuint)bytes);
        var region = new HostSharedRegion(address, bytes, -1, writable: true, static r => NativeMemory.Free((void*)r.Address));
        return new GuestViewTable(region, MirrorBytes, 256);
    }

    public static void Run()
    {
        Console.WriteLine("--- 10. Mirror translator (k17) ---");
        var random = new Random(17);

        // a: one 1 GiB view, random addresses.
        {
            using var table = NewTable(64);
            table.Map(Base, 1UL << 30, 0, HostPageProtection.ReadWrite);
            var addresses = Enumerable.Range(0, Ops).Select(_ => Base + (ulong)random.NextInt64(1L << 30)).ToArray();
            Case("10a", "1 GiB single view, random addresses", table, addresses);
        }

        // b: 512 separate 1 MiB views (offsets do not continue), random.
        {
            using var table = NewTable(1024);
            for (var i = 0; i < 512; i++)
                table.Map(Base + (ulong)i * 2 * (1UL << 20), 1UL << 20, (ulong)i * 3 * (1UL << 20), HostPageProtection.ReadWrite);
            var addresses = Enumerable.Range(0, Ops).Select(_ => Base + (ulong)random.Next(512) * 2 * (1UL << 20) + (ulong)random.Next(1 << 20)).ToArray();
            Case("10b", "512 separate 1 MiB views, random addresses", table, addresses);
        }

        // c: 48 buffers of 64 KiB at a 4 MiB stride: every block index is a multiple of 64.
        {
            using var table = NewTable(128);
            for (var i = 0; i < 48; i++)
                table.Map(Base + (ulong)i * (4UL << 20), Block, (ulong)i * 2 * Block, HostPageProtection.ReadWrite);
            var addresses = Enumerable.Range(0, Ops).Select(_ => Base + (ulong)random.Next(48) * (4UL << 20) + (ulong)random.Next((int)Block)).ToArray();
            Case("10c", "48 x 64 KiB views at a 4 MiB stride, random addresses", table, addresses);
        }

        // d: a 64 MiB heap split by watches into alternating 16 KiB protections (4096 entries, runs cut at 129).
        {
            using var table = NewTable(8192);
            table.Map(Base, 64UL << 20, 0, HostPageProtection.ReadWrite);
            for (ulong at = 0; at < (64UL << 20); at += 0x8000)
                table.Protect(Base + at, 0x4000, HostPageProtection.ReadOnly);
            var addresses = Enumerable.Range(0, Ops).Select(_ => Base + (ulong)random.NextInt64(64L << 20)).ToArray();
            Case("10d", "64 MiB heap split into 4096 watch entries, random addresses", table, addresses);
            var walk = Enumerable.Range(0, Ops).Select(i => Base + (ulong)i * 0x4000 % (64UL << 20)).ToArray();
            Case("10e", "same heap, sequential 16 KiB walk", table, walk);
        }
    }

    private static void Case(string id, string name, GuestViewTable table, ulong[] addresses)
    {
        var translator = new GuestMirrorTranslator(table.Region.Address, table.Region.Size, MirrorBase, MirrorBytes) { CountCacheAccesses = false };
        ulong sink = 0;
        Harness.Measure(id, name, Ops, 1, _ =>
        {
            for (var i = 0; i < addresses.Length; i++)
                if (translator.TryTranslate(addresses[i], 8, out var mirror)) sink += mirror;
        }, unit: "xlate");
        // Miss rate over warm passes.
        var counting = new GuestMirrorTranslator(table.Region.Address, table.Region.Size, MirrorBase, MirrorBytes) { CountCacheAccesses = true };
        for (var pass = 0; pass < 3; pass++)
            foreach (var address in addresses) counting.TryTranslate(address, 8, out _);
        var hits0 = counting.CacheHits; var misses0 = counting.CacheMisses;
        for (var pass = 0; pass < 10; pass++)
            foreach (var address in addresses) counting.TryTranslate(address, 8, out _);
        var hits = counting.CacheHits - hits0; var misses = counting.CacheMisses - misses0;
        Console.WriteLine($"  {id} warm miss rate {100.0 * misses / Math.Max(1, hits + misses):F2}% ({misses} misses / {hits + misses})");
        GC.KeepAlive(sink);
    }
}
