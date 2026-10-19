// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryConcurrencyTests
{
    private sealed class Caller(BackedKernelMemory test)
    {
        public CpuContext Context { get; } = new(test.Memory, Generation.Gen5);
        public ulong Output { get; } = test.Memory.AllocateAt(0, 0x4000, false);

        public int MapDirect(ulong offset, ulong size, ulong address, ulong flags)
        {
            Assert.True(Context.TryWriteUInt64(Output, address));
            Context[CpuRegister.Rdi] = Output;
            Context[CpuRegister.Rsi] = size;
            Context[CpuRegister.Rdx] = 0x33;
            Context[CpuRegister.Rcx] = flags;
            Context[CpuRegister.R8] = offset;
            Context[CpuRegister.R9] = 0;
            return KernelMemoryCompatExports.KernelMapDirectMemory(Context);
        }

        public int Reserve(ulong size, ulong address)
        {
            Assert.True(Context.TryWriteUInt64(Output, address));
            Context[CpuRegister.Rdi] = Output;
            Context[CpuRegister.Rsi] = size;
            Context[CpuRegister.Rdx] = address == 0 ? 0UL : 0x10UL;
            Context[CpuRegister.Rcx] = 0;
            return KernelRuntimeCompatExports.KernelReserveVirtualRange(Context);
        }

        public int Unmap(ulong address, ulong size)
        {
            Context[CpuRegister.Rdi] = address;
            Context[CpuRegister.Rsi] = size;
            return KernelMemoryCompatExports.KernelMunmap(Context);
        }

        public int Query(ulong address, bool findNext)
        {
            Context[CpuRegister.Rdi] = address;
            Context[CpuRegister.Rsi] = findNext ? 1UL : 0UL;
            Context[CpuRegister.Rdx] = Output + 0x100;
            Context[CpuRegister.Rcx] = 0x48;
            return KernelMemoryCompatExports.KernelVirtualQuery(Context);
        }
    }

    private static GuestGpuMemory? AttachGpuWhenSupported(BackedKernelMemory test)
    {
        // GPU page tracking needs native 4 KiB pages; elsewhere the table races still run.
        if (Environment.SystemPageSize != 4096)
            return null;
        var gpu = new GuestGpuMemory(test.Memory);
        GuestGpuMemoryHook.Attach(gpu);
        return gpu;
    }

    [Fact]
    public void ConcurrentMapsUnmapsQueriesAndShaderReadsStayConsistent()
    {
        using var test = new BackedKernelMemory();
        using var gpu = AttachGpuWhenSupported(test);
        try
        {
            const int workers = 4;
            const int iterations = 150;
            test.Allocate(0, workers * 0x8000UL);
            var live = new ConcurrentDictionary<ulong, byte>();
            var failures = new ConcurrentQueue<string>();
            using var stop = new CancellationTokenSource();

            var mappers = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
            {
                var caller = new Caller(test);
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    if (caller.MapDirect((ulong)worker * 0x8000, 0x8000, 0, 0) != 0 ||
                        !caller.Context.TryReadUInt64(caller.Output, out var address))
                    {
                        failures.Enqueue($"map {worker}/{iteration}");
                        return;
                    }
                    live[address] = 0;
                    if (!caller.Context.TryWriteUInt64(address + 0x4000, (ulong)iteration))
                        failures.Enqueue($"write {worker}/{iteration}");
                    if (caller.Query(address + 0x4000, false) != 0)
                        failures.Enqueue($"query {worker}/{iteration}");
                    live.TryRemove(address, out _);
                    if (caller.Unmap(address, 0x8000) != 0)
                        failures.Enqueue($"unmap {worker}/{iteration}");
                }
            })).ToArray();

            // Readers race the mappers over addresses that come and go.
            var readers = Enumerable.Range(0, 2).Select(reader => Task.Run(() =>
            {
                var caller = new Caller(test);
                var buffer = new byte[64];
                while (!stop.IsCancellationRequested)
                {
                    foreach (var address in live.Keys)
                    {
                        _ = KernelMemoryCompatExports.TryReadShaderGuestMemory(address + 0x4000, buffer);
                        _ = caller.Query(address, true);
                    }
                    var cursor = 0UL;
                    for (var step = 0; step < 32 && caller.Query(cursor, true) == 0; step++)
                    {
                        if (!caller.Context.TryReadUInt64(caller.Output + 0x108, out var end) || end <= cursor)
                            break;
                        cursor = end;
                    }
                }
            })).ToArray();

            Assert.True(Task.WaitAll(mappers, TimeSpan.FromSeconds(60)));
            stop.Cancel();
            Assert.True(Task.WaitAll(readers, TimeSpan.FromSeconds(10)));
            Assert.Empty(failures);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }

    // A fixed reservation is checked against GPU-registered memory without the lock and may then
    // run off the GPU worker; a mapping of the same range on another thread used to slip in
    // between and make the transaction throw. Both calls must keep succeeding.
    [Fact]
    public void FixedReservationsRacingMappingsOfTheSameRangeNeverThrow()
    {
        using var test = new BackedKernelMemory();
        using var gpu = AttachGpuWhenSupported(test);
        try
        {
            test.Allocate(0, 0x4000);
            var address = test.Reserve(0x10000);
            var failures = new ConcurrentQueue<string>();
            var reserver = Task.Run(() =>
            {
                var caller = new Caller(test);
                for (var iteration = 0; iteration < 300; iteration++)
                {
                    var result = caller.Reserve(0x4000, address);
                    if (result != 0)
                        failures.Enqueue($"reserve {iteration}: 0x{result:X8}");
                }
            });
            var mapper = Task.Run(() =>
            {
                var caller = new Caller(test);
                for (var iteration = 0; iteration < 300; iteration++)
                {
                    var result = caller.MapDirect(0, 0x4000, address, 0x10);
                    if (result != 0)
                        failures.Enqueue($"map {iteration}: 0x{result:X8}");
                }
            });

            Assert.True(Task.WaitAll([reserver, mapper], TimeSpan.FromSeconds(60)));
            Assert.Empty(failures);
            var info = KernelGuestVaMapTests.Query(test, address);
            Assert.Equal(address, info.Start);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }
}
