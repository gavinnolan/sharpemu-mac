// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

[CollectionDefinition("GuestImageWriteTracker", DisableParallelization = true)]
public sealed class GuestImageWriteTrackerCollection;

[Collection("GuestImageWriteTracker")]
public sealed unsafe class GuestImageWriteFaultTests
{
    private const nuint AllocationBytes = 16384;
    public GuestImageWriteFaultTests()
    {
        if (!OperatingSystem.IsWindows()) GuestImageWriteTracker.Configure(true);
    }

    [TrackedWriteFaultFact]
    public void FaultsDelayedBehindAnotherThreadsRestoreStillHaveNoOwnershipProof()
    {
        using var memory = new Mapping();
        GuestImageWriteTracker.Track(memory.Address, 3 * 4096);
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        Task<(bool SawArmed, bool Recovered)> Pending(ulong fault) => Task.Run(() =>
        {
            // Scheduling barriers live outside the production signal path.
            var found = GuestImageWriteTracker.TryGetProtectionState(memory.Address, out _, out var armed);
            ready.Signal();
            return (found && armed, release.Wait(TimeSpan.FromSeconds(10)) &&
                GuestImageWriteTracker.TryHandleWriteFault(fault));
        });
        var first = Pending(memory.Address + 128);
        var second = Pending(memory.Address + 2 * 4096 + 256);
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(memory.Address + 4096 + 16));
            release.Set();
            Assert.True(Task.WaitAll([first, second], TimeSpan.FromSeconds(10)));
            Assert.True(first.Result.SawArmed);
            Assert.True(second.Result.SawArmed);
            // Address-only lookup cannot identify either pending invocation
            // after the third writer has removed the protection owner.
            Assert.False(first.Result.Recovered);
            Assert.False(second.Result.Recovered);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(memory.Address, out var generation));
            Assert.Equal(1, generation);
            Assert.True(GuestImageWriteTracker.PeekDirty(memory.Address));
            for (ulong offset = 0; offset < 3 * 4096; offset += 4096)
                Assert.Equal(HostMemory.PAGE_READWRITE, NativePageProtection.Query(memory.Address + offset));
            Assert.False(GuestImageWriteTracker.TryHandleWriteFault(memory.Address));
        }
        finally
        {
            release.Set();
            _ = Task.WaitAll([first, second], TimeSpan.FromSeconds(10));
        }
    }

    [TrackedWriteFaultTheory]
    [InlineData(10, 2u, false, true)] // SIGBUS write, fallback address
    [InlineData(11, 2u, false, true)] // SIGSEGV write, fallback address
    [InlineData(10, 2u, true, true)] // raw si_addr remains preferred
    [InlineData(10, 0u, false, false)] // unclassified/read
    [InlineData(10, 1u, false, false)] // read protection fault
    [InlineData(10, 16u, false, false)] // instruction fetch
    [InlineData(10, 18u, false, false)] // instruction fetch takes precedence
    [InlineData(11, 0u, true, false)]
    [InlineData(4, 2u, true, false)] // SIGILL
    [InlineData(8, 2u, true, false)] // SIGFPE
    [InlineData(6, 2u, true, false)] // SIGABRT
    public void DarwinEarlyPathUsesNormalizedAddressAndOnlyHandlesMemoryWrites(
        int signalNumber, uint error, bool rawAddress, bool expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var memory = new Mapping();
        using var fallback = new Mapping();
        GuestImageWriteTracker.Track(memory.Address, 4096);
        GuestImageWriteTracker.Track(fallback.Address, 4096);
        var assembly = Assembly.Load("SharpEmu.Cpu.DirectExecution.MacOS.X64");
        var layout = assembly.GetTypes().Single(static type => type.Name == "PosixSignalLayout");
        var bridge = assembly.GetTypes().Single(static type => type.Name == "PosixFaultBridge");
        int Offset(string name) => (int)layout.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        byte* user = stackalloc byte[512];
        byte* machine = stackalloc byte[1024];
        byte* signal = stackalloc byte[128];
        new Span<byte>(user, 512).Clear();
        new Span<byte>(machine, 1024).Clear();
        new Span<byte>(signal, 128).Clear();
        *(byte**)(user + Offset("UcontextMcontextOffset")) = machine;
        *(ulong*)(user + Offset("UserContextMachineContextSizeOffset")) = 1024;
        *(ulong*)(machine + Offset("McontextFaultAddressOffset")) = rawAddress ? fallback.Address : memory.Address;
        *(uint*)(machine + Offset("McontextErrOffset")) = error;
        *(ulong*)(machine + 144) = 0x1234; // Darwin x64 RIP offset
        if (rawAddress) *(ulong*)(signal + Offset("SigInfoAddressOffset")) = memory.Address;
        var method = bridge.GetMethod("TryHandleTrackedImageWriteFault", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (bool)method.Invoke(null, [signalNumber, (nint)signal, (nint)user])!);
        Assert.Equal(expected, GuestImageWriteTracker.PeekDirty(memory.Address));
        Assert.Equal(expected ? HostMemory.PAGE_READWRITE : HostMemory.PAGE_READONLY,
            NativePageProtection.Query(memory.Address));
        Assert.False(GuestImageWriteTracker.PeekDirty(fallback.Address));
        Assert.Equal(HostMemory.PAGE_READONLY, NativePageProtection.Query(fallback.Address));
    }

    [TrackedWriteFaultFact]
    public void MissingDarwinMachineContextDeclinesWithoutChangingProtection()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var memory = new Mapping();
        GuestImageWriteTracker.Track(memory.Address, 4096);
        var bridge = Assembly.Load("SharpEmu.Cpu.DirectExecution.MacOS.X64").GetTypes()
            .Single(static type => type.Name == "PosixFaultBridge");
        var method = bridge.GetMethod("TryHandleTrackedImageWriteFault", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.False((bool)method.Invoke(null, [10, (nint)0, (nint)0])!);
        Assert.Equal(HostMemory.PAGE_READONLY, NativePageProtection.Query(memory.Address));
        Assert.False(GuestImageWriteTracker.PeekDirty(memory.Address));
    }

    private sealed class Mapping : IDisposable
    {
        private readonly void* _pointer;
        public ulong Address => (ulong)_pointer;
        public Mapping()
        {
            _pointer = HostMemory.Alloc(null, AllocationBytes, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_READWRITE);
            Assert.NotEqual(0, (nint)_pointer);
        }
        public void Dispose()
        {
            GuestImageWriteTracker.Untrack(Address);
            Assert.True(HostMemory.Free(_pointer, 0, HostMemory.MEM_RELEASE));
        }
    }
}

public sealed class TrackedWriteFaultFactAttribute : FactAttribute
{
    public TrackedWriteFaultFactAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.SystemPageSize != 4096)
            Skip = "The tracker fixture requires POSIX native 4 KiB protection.";
    }
}
public sealed class TrackedWriteFaultTheoryAttribute : TheoryAttribute
{
    public TrackedWriteFaultTheoryAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.SystemPageSize != 4096)
            Skip = "The tracker fixture requires POSIX native 4 KiB protection.";
    }
}
