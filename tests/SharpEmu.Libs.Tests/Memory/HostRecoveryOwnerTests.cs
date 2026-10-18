// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

public sealed class HostRecoveryOwnerTests
{
    private const ulong Base = 0x5000_0000_0000;
    private static HostRecoveryOwner Create(int capacity = 1, ulong page = 4096) =>
        new(new object(), Base, page * 2, page, 7, capacity);

    [Theory]
    [InlineData(4096UL)]
    [InlineData(16384UL)]
    public void WholeNativePageCoverageAndFrameIdentityAreRetained(ulong nativePage)
    {
        var owner = Create(page: nativePage);
        Assert.True(owner.TryAcquireWritePin(Base + nativePage + 17, HostRecoveryAccess.Write, 23, 7, out var pin));
        Assert.Equal(Base + nativePage + 17, pin.FaultAddress);
        Assert.Equal(Base + nativePage, pin.PageAddress);
        Assert.Equal(23, pin.Invocation);
        Assert.Equal(7UL, pin.DomainEpoch);
        Assert.Equal(owner.Identity, pin.OwnerIdentity);
        Assert.True(owner.Release(pin));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostRecoveryOwner(new object(), Base + 4096,
            32768, 16384, 7));
    }

    [Theory]
    [InlineData(HostRecoveryAccess.Unknown, 1, 7UL, 0UL)]
    [InlineData(HostRecoveryAccess.Read, 1, 7UL, 0UL)]
    [InlineData(HostRecoveryAccess.Execute, 1, 7UL, 0UL)]
    [InlineData(HostRecoveryAccess.Write, 0, 7UL, 0UL)]
    [InlineData(HostRecoveryAccess.Write, -1, 7UL, 0UL)]
    [InlineData(HostRecoveryAccess.Write, 1, 8UL, 0UL)]
    [InlineData(HostRecoveryAccess.Write, 1, 7UL, 8192UL)]
    public void WrongFrameEpochAccessAndOutOfCoverageDecline(HostRecoveryAccess access,
        long invocation, ulong epoch, ulong offset)
    {
        var owner = Create();
        Assert.False(owner.TryAcquireWritePin(Base + offset, access, invocation, epoch, out _));
        Assert.Equal(0, owner.ActivePins);
        Assert.False(owner.TryAcquireWritePin(Base - 1, HostRecoveryAccess.Write, 1, 7, out _));
    }

    [Fact]
    public async Task CloseRejectsNewPinsAndDrainRunsOutsideRecoveryGate()
    {
        var owner = Create();
        Assert.True(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 7, out var held));
        var recoveryGate = new object();
        using var closed = new ManualResetEventSlim();
        var mutationStarted = 0;
        var mutation = Task.Run(() =>
        {
            lock (recoveryGate) { owner.Close(); closed.Set(); }
            // The kernel-mutation stand-in is deliberately after the gate/drain.
            if (!owner.TryDrain(TimeSpan.FromSeconds(10))) return false;
            Interlocked.Exchange(ref mutationStarted, 1);
            return true;
        });
        Assert.True(closed.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(owner.IsClosed);
        Assert.False(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 2, 7, out _));
        Assert.Equal(0, Volatile.Read(ref mutationStarted));
        // A recovery needing this gate can finish; the draining thread cannot hold it.
        var released = await Task.Run(() => { lock (recoveryGate) return owner.Release(held); })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(released);
        Assert.True(await mutation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Volatile.Read(ref mutationStarted));
        Assert.Equal(0, owner.ActivePins);
    }

    [Fact]
    public void TimeoutRetainsClosedOwnerAndCannotAuthorizeMutation()
    {
        var backing = new object();
        var owner = new HostRecoveryOwner(backing, Base, 4096, 4096, 1);
        Assert.False(owner.TryDrain(TimeSpan.Zero)); // open identity is not drained authority
        Assert.True(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 1, out var pin));
        owner.Close();
        owner.Close();
        Assert.False(owner.TryDrain(TimeSpan.FromMilliseconds(2)));
        Assert.Same(backing, owner.RetainedOwner);
        Assert.Equal(1, owner.ActivePins);
        Assert.True(owner.IsClosed);
        Assert.True(owner.Release(pin));
        Assert.True(owner.TryDrain(TimeSpan.Zero));
        Assert.False(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 2, 1, out _));
    }

    [Fact]
    public void CopiedStaleAndForeignPinsCannotReleaseNewOperation()
    {
        var owner = Create();
        Assert.False(owner.Release(default));
        Assert.True(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 7, out var first));
        Assert.False(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 2, 7, out _)); // capacity
        Assert.True(owner.Release(first));
        Assert.True(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 3, 7, out var later));
        Assert.False(owner.Release(first));
        Assert.Equal(1, owner.ActivePins);
        var foreign = Create();
        Assert.False(foreign.Release(later));
        Assert.True(owner.Release(later));
        owner.Close();
        Assert.True(owner.TryDrain(TimeSpan.Zero));
        // Replacement is published only after old drain in this modeled safe caller.
        var replacement = Create();
        Assert.NotEqual(owner.Identity, replacement.Identity);
        Assert.True(replacement.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 4, 7, out var newer));
        Assert.False(replacement.Release(first));
        Assert.Equal(1, replacement.ActivePins);
        Assert.True(replacement.Release(newer));
    }

    [Fact]
    public async Task AcquiringRacesClosureWithoutPostCloseCountIncrease()
    {
        // The acquirer runs on its own thread and the waits below only guard against a hang: a
        // pool task can start seconds late on a loaded two-core runner, and the barrier would
        // then time out although the race under test never happened.
        var guard = TimeSpan.FromSeconds(30);
        for (var i = 0; i < 64; i++)
        {
            var owner = Create(8);
            using var ready = new Barrier(2);
            var acquire = Task.Factory.StartNew(() =>
            {
                Assert.True(ready.SignalAndWait(guard));
                return owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 7, out var pin)
                    ? pin : default;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(ready.SignalAndWait(guard));
            owner.Close();
            var result = await acquire.WaitAsync(guard);
            Assert.False(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 2, 7, out _));
            if (result.OwnerIdentity != 0)
            {
                Assert.Equal(1, owner.ActivePins);
                Assert.False(owner.TryDrain(TimeSpan.Zero));
                Assert.True(owner.Release(result));
            }
            Assert.True(owner.TryDrain(TimeSpan.Zero));
            Assert.Equal(0, owner.ActivePins);
        }
    }

    [Fact]
    public void SignalFacingScalarMethodsAllocateNothingOnWarmedOrdinaryThread()
    {
        var owner = Create();
        var closed = Create();
        closed.Close();
        Assert.False(closed.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 7, out _));
        Assert.True(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 7, out var warm));
        Assert.True(owner.Release(warm));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var acquired = owner.TryAcquireWritePin(Base + 1, HostRecoveryAccess.Write, 2, 7, out var pin);
        var released = owner.Release(pin);
        var declined = closed.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 2, 7, out _);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(acquired);
        Assert.True(released);
        Assert.False(declined);
        Assert.Equal(before, after);
    }

    [Fact]
    public void PreemptedReservedAcquisitionLosesAfterCloseAndDrain()
    {
        var owner = Create();
        // Invoke the actual private scalar phases, no delegate/pause hook in production.
        var arguments = new object?[] { 0, 0L };
        Assert.True((bool)Private("TryReserveSlot").Invoke(owner, arguments)!);
        owner.Close();
        Assert.True(owner.TryDrain(TimeSpan.Zero)); // reserved slot has no native authority
        Assert.False((bool)Private("TryCountReservedSlot").Invoke(owner, arguments)!);
        Assert.Equal(0, owner.ActivePins);
        Assert.False(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 7, out _));
    }

    [Fact]
    public void PreemptedCountedAcquisitionBlocksDrainBeforePinPublication()
    {
        var owner = Create();
        var arguments = new object?[] { 0, 0L };
        Assert.True((bool)Private("TryReserveSlot").Invoke(owner, arguments)!);
        Assert.True((bool)Private("TryCountReservedSlot").Invoke(owner, arguments)!);
        owner.Close();
        Assert.Equal(1, owner.ActivePins);
        Assert.False(owner.TryDrain(TimeSpan.Zero));
        var pin = (HostRecoveryPin)Private("PublishPin").Invoke(owner,
            [arguments[0], arguments[1], Base, Base, 1L])!;
        Assert.True(owner.Release(pin));
        Assert.True(owner.TryDrain(TimeSpan.Zero));
    }

    private static MethodInfo Private(string name) => typeof(HostRecoveryOwner)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void SlotCursorOverflowIsBoundedAndExhaustedGenerationCannotWrap()
    {
        var owner = Create();
        typeof(HostRecoveryOwner).GetField("_nextSlot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, int.MaxValue);
        Assert.True(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1, 7, out var pin));
        Assert.True(owner.Release(pin));
        // Safe test setup only, no friend assembly or new production injection hook.
        var slots = (Array)typeof(HostRecoveryOwner).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        var boxed = slots.GetValue(0)!;
        boxed.GetType().GetField("Token")!.SetValue(boxed, (long.MaxValue >> 2) << 2);
        slots.SetValue(boxed, 0);
        Assert.False(owner.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 2, 7, out _));
        Assert.Equal(0, owner.ActivePins);
        owner.Close();
        Assert.True(owner.TryDrain(TimeSpan.Zero));
    }
}
