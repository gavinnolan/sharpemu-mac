// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpEmu.Gpu.Metal.Tests.Scenes;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe class MetalPairLockTableTests(ITestOutputHelper output)
{
    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);

    [Fact]
    public void ConcurrentFirstUsePublishesOneZeroedResidentTableAndPreservesItsWords()
    {
        if (!RequireDevice()) return;
        using var backing = new SceneMemory();
        using var memory = new MetalGuestMemory(Device.Value, backing, backing);
        var initialBuffers = memory.ResidentBuffers.ToArray();
        var initialResidencyCount = memory.ResidencySet == 0 ? 0 :
            MetalNative.Send(memory.ResidencySet, MetalNative.Selector("allocationCount"));
        var addresses = new ulong[8];
        using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, addresses.Length).Select(index => Task.Factory.StartNew(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            addresses[index] = memory.PairLockTableGpuAddress;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        start.Set();
        Assert.True(Task.WaitAll(readers, TimeSpan.FromSeconds(10)));
        Assert.NotEqual(0UL, addresses[0]);
        Assert.All(addresses, address => Assert.Equal(addresses[0], address));
        var buffer = (nint)typeof(MetalGuestMemory).GetField("_pairLockTable",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
        Assert.NotEqual(0, buffer);
        AssertResident();
        Assert.Equal(addresses[0], (ulong)MetalNative.Send(buffer, MetalNative.Selector("gpuAddress")));
        var words = new Span<uint>((void*)MetalNative.Send(buffer, MetalNative.Selector("contents")),
            Gen5MslArgumentLayout.PairLockTableWords);
        Assert.True(words.IndexOfAnyExcept(0u) < 0, "The complete pair-lock table must start unlocked.");

        // Sequence words belong to the shaders after publication. Retrieving the address,
        // including at a new submission, must neither allocate another table nor reset them.
        words[0] = 2;
        words[^1] = 0x12345678;
        memory.BeginSubmission(1, 0);
        Parallel.For(0, addresses.Length, index =>
        {
            for (var read = 0; read < 1000; read++)
                Assert.Equal(addresses[index], memory.PairLockTableGpuAddress);
        });
        Assert.Equal(2u, words[0]);
        Assert.Equal(0x12345678u, words[^1]);
        AssertResident();

        void AssertResident()
        {
            if (memory.ResidencySet == 0)
                Assert.Equal(buffer, Assert.Single(memory.ResidentBuffers.ToArray().Except(initialBuffers)));
            else
            {
                Assert.True(MetalNative.SendBool(memory.ResidencySet, MetalNative.Selector("containsAllocation:"), buffer));
                Assert.Equal(initialResidencyCount + 1, MetalNative.Send(memory.ResidencySet, MetalNative.Selector("allocationCount")));
            }
        }
    }

    [Fact]
    public void MeasurePublishedTableLookupWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_PAIR_LOCK_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(path) || !RequireDevice()) return;
        using var backing = new SceneMemory();
        using var memory = new MetalGuestMemory(Device.Value, backing, backing);
        const int iterations = 1_000_000;
        var address = memory.PairLockTableGpuAddress;
        ReadBatch(memory, 20_000);
        var batches = new List<object>();
        for (var batch = 0; batch < 9; batch++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var sum = ReadBatch(memory, iterations);
            var elapsed = Stopwatch.GetTimestamp() - start;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(unchecked(address * iterations), sum);
            batches.Add(new
            {
                batch,
                nsPerLookup = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations,
                bytesPerLookup = (double)allocated / iterations,
            });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            iterations,
            batches,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        output.WriteLine(path);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ReadBatch(MetalGuestMemory memory, int iterations)
    {
        ulong sum = 0;
        for (var index = 0; index < iterations; index++)
            sum = unchecked(sum + memory.PairLockTableGpuAddress);
        return sum;
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Pair-lock table tests need the exclusive device gate.");
            return false;
        }
        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }
}
