// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Gpu.Metal.Tests.Scenes;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.PerfAudit;

/// <summary>
/// W8: a 64 MB staged-range-cache entry, one 4-byte guest store into it, then the rebind that has to make the
/// entry current again. The bind is what MetalRenderHost.BindStagedRange does minus the GPU gather: Acquire, then
/// either the partial refill or a whole read of the entry from guest memory. Watches go to a real PageGuard over a
/// no-op address space, so the watch bookkeeping is counted too (the syscall is not: 3.8-7.8 us per page in 6q).
/// </summary>
internal static unsafe class W8Benches
{
    private const ulong GuestBase = 0x4_0000_0000;
    private const ulong EntryBytes = 64UL * 1024 * 1024;

    private sealed class Backend : IStagedRangeBackend, IDisposable
    {
        private readonly PageGuard _guard = new(new SceneAddressSpace());
        private readonly List<nint> _buffers = [];
        public long AddCalls, RemoveCalls, AddPages, RemovePages;

        public (nint Handle, nint Contents, ulong GpuAddress) CreateBuffer(ulong capacity)
        {
            var memory = (nint)NativeMemory.AlignedAlloc((nuint)capacity, 16384);
            _buffers.Add(memory);
            return (memory, memory, (ulong)memory);
        }

        public void ReleaseBuffer(nint handle) { }

        public void AddWatch(ulong address, ulong size)
        {
            AddCalls++;
            AddPages += (long)((size + 4095) / 4096);
            _guard.AddWatch(address, size, false);
        }

        public void RemoveWatch(ulong address, ulong size)
        {
            RemoveCalls++;
            RemovePages += (long)((size + 4095) / 4096);
            _guard.RemoveWatch(address, size, false);
        }

        public void Dispose()
        {
            foreach (var buffer in _buffers)
            {
                NativeMemory.AlignedFree((void*)buffer);
            }
        }
    }

    public static void Run()
    {
        Console.WriteLine("--- W8. staged entry: one 4-byte store, rebind ---");
        var guest = new byte[EntryBytes];
        new Random(1).NextBytes(guest);
        Scenario("w8a", partial: false, written: false, inFlight: false);
        Scenario("w8b", partial: true, written: false, inFlight: false);
        Scenario("w8c", partial: false, written: false, inFlight: true);
        Scenario("w8d", partial: true, written: false, inFlight: true);
        Scenario("w8e", partial: false, written: true, inFlight: false);
        Scenario("w8f", partial: true, written: true, inFlight: false);

        void Scenario(string id, bool partial, bool written, bool inFlight)
        {
            using var backend = new Backend();
            var cache = new MetalStagedRangeCache(backend, 512UL * 1024 * 1024, partial);
            long guestBytes = 0;
            void Read(ulong address, Span<byte> destination)
            {
                guest.AsSpan((int)(address - GuestBase), destination.Length).CopyTo(destination);
                guestBytes += destination.Length;
            }

            ulong serial = 3;
            void Bind()
            {
                // In flight: the previous submission has not completed when the next one binds.
                var completed = inFlight ? serial - 2 : serial - 1;
                var hit = cache.Acquire(GuestBase, EntryBytes, written, serial, completed, out var entry);
                if (!hit)
                {
                    if (!cache.RefillDirty(entry!, Read))
                    {
                        Read(GuestBase, new Span<byte>((void*)entry!.StagingBuffer!.Contents, (int)EntryBytes));
                        if (entry.Pristine.IsValid)
                        {
                            new ReadOnlySpan<byte>((void*)entry.StagingBuffer!.Contents, (int)EntryBytes)
                                .CopyTo(new Span<byte>((void*)entry.PristineBuffer!.Contents, (int)EntryBytes));
                        }
                    }
                }

                serial++;
            }

            Bind();
            var iteration = 0;
            guestBytes = 0;
            backend.AddCalls = backend.RemoveCalls = backend.AddPages = backend.RemovePages = 0;
            var result = Harness.Measure(id,
                $"64 MB staged entry, one 4-byte store, rebind; partial={partial} written={written} in_flight={inFlight}",
                1, 1, i =>
                {
                    iteration++;
                    var offset = (ulong)((i * 7919L) % (long)(EntryBytes / 4096)) * 4096 + 64;
                    guest[(int)offset] ^= 1;
                    cache.OnCpuWrite(GuestBase + offset, 4);
                    Bind();
                }, unit: "rebind", maxInvokesPerRun: 4000);
            var counters = cache.PartialCounters;
            Console.WriteLine(
                $"{id} per rebind: guest bytes copied={guestBytes / (double)iteration:N0} " +
                $"watch add calls={backend.AddCalls / (double)iteration:N2} pages={backend.AddPages / (double)iteration:N0} " +
                $"remove calls={backend.RemoveCalls / (double)iteration:N2} pages={backend.RemovePages / (double)iteration:N0} " +
                $"partial refills={counters.Refills} " +
                $"median={result.MedianNs / 1000:N1} us");
        }
    }
}
