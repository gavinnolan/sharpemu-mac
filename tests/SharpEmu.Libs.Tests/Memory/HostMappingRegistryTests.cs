// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// Ordinary-thread model controls. No native mapping/protection call or callback runs here.
public sealed class HostMappingRegistryTests
{
    private const ulong Base = 0x5000_0000_0000;
    private const ulong Page = 4096;
    private static HostMappingSpec Spec(ulong address, ulong size, object backing,
        ulong backingSize = Page * 3, ulong offset = 0) =>
        new(address, size, backing, backingSize, offset, HostPageProtection.ReadOnly);
    private static HostMappingRecord Find(HostMappingRegistry registry, ulong address)
    { Assert.True(registry.TryFind(address, out var found)); return Assert.IsType<HostMappingRecord>(found); }
    private static void Finish(HostMappingMutation mutation, HostMappingKernelOutcome outcome,
        bool expectedCommit, int error = 0)
    {
        Assert.True(mutation.TryDrain(TimeSpan.Zero));
        Assert.True(mutation.TryStartKernelOperation(out var operation));
        Assert.True(mutation.RecordKernelOutcome(operation!, operation!.Ranges, outcome, error));
        Assert.Equal(expectedCommit, mutation.CompleteRecordedOperation(operation));
        Assert.False(mutation.CompleteRecordedOperation(operation));
    }

    [Fact]
    public void InitialRegistrationRejectsOverlapInvalidBackingAndProtection()
    {
        var registry = new HostMappingRegistry(Page);
        var backing = new object();
        var valid = Spec(Base, Page, backing);
        Assert.True(registry.TryPublishInitial(valid));
        Assert.False(registry.TryPublishInitial(valid));
        Assert.False(registry.TryPublishInitial(valid with { Address = Base + 1 }));
        Assert.False(registry.TryPublishInitial(valid with { Address = Base + 8 * Page, BackingOffset = 3 * Page }));
        Assert.False(registry.TryPublishInitial(valid with { Address = Base + 8 * Page, BackingSize = 4 * Page }));
        Assert.False(registry.TryPublishInitial(valid with { Address = Base + 8 * Page, Protection = (HostPageProtection)99 }));
        Assert.Single(registry.Snapshot());
    }

    [Theory]
    [InlineData(4096UL)]
    [InlineData(16384UL)]
    public void ReplacementRequiresWholeActualProviderPages(ulong page)
    {
        var registry = new HostMappingRegistry(page);
        var initial = Spec(Base, page * 3, new object(), page * 3);
        Assert.True(registry.TryPublishInitial(initial));
        Assert.False(registry.TryBeginChange(new(Base + 1, page), [], out _));
        Assert.False(registry.TryBeginChange(new(Base, page - 1), [], out _));
        Assert.False(registry.TryBeginChange(new(ulong.MaxValue - page + 1, page), [], out _));
        Assert.True(registry.TryBeginChange(new(Base + page, page), [], out var unmap));
        Finish(unmap!, HostMappingKernelOutcome.AppliedWholeScope, true);
        Assert.False(registry.TryFind(Base + page, out _));
        Assert.Equal(2, registry.Snapshot().Count);
    }

    [Fact]
    public void MiddleReplacementSplitsOffsetsAndKeepsUntouchedAliasIdentity()
    {
        var registry = new HostMappingRegistry(Page);
        var backing = new object();
        Assert.True(registry.TryPublishInitial(Spec(Base, Page * 3, backing)));
        var aliasAddress = Base + 0x10_0000;
        Assert.True(registry.TryPublishInitial(Spec(aliasAddress, Page, backing, offset: Page)));
        var old = Find(registry, Base);
        var alias = Find(registry, aliasAddress);
        var replacement = Spec(Base + Page, Page, new object(), Page);
        Assert.True(registry.TryBeginChange(new(Base + Page, Page), [replacement], out var mutation));
        Assert.True(old.Domain.IsClosed);
        Assert.False(alias.Domain.IsClosed);
        Finish(mutation!, HostMappingKernelOutcome.AppliedWholeScope, true);
        var left = Find(registry, Base);
        var center = Find(registry, Base + Page);
        var right = Find(registry, Base + Page * 2);
        Assert.Equal(Page, left.Mapping.Size);
        Assert.Equal(0UL, left.Mapping.BackingOffset);
        Assert.Equal(Page * 2, right.Mapping.BackingOffset);
        Assert.Same(backing, left.Mapping.BackingIdentity);
        Assert.Same(backing, right.Mapping.BackingIdentity);
        Assert.Same(replacement.BackingIdentity, center.Mapping.BackingIdentity);
        Assert.NotEqual(old.Domain.Identity, left.Domain.Identity);
        Assert.NotEqual(old.Domain.Identity, right.Domain.Identity);
        Assert.NotEqual(left.Domain.DomainEpoch, right.Domain.DomainEpoch);
        Assert.Same(alias, Find(registry, aliasAddress));
    }

    [Theory]
    [InlineData(HostMappingKernelOutcome.FailedUnchanged)]
    [InlineData(HostMappingKernelOutcome.Partial)]
    [InlineData(HostMappingKernelOutcome.Unknown)]
    public void UnsuccessfulOutcomeCannotRepublishOldOrPlannedIdentity(HostMappingKernelOutcome outcome)
    {
        var registry = new HostMappingRegistry(Page);
        var mapping = Spec(Base, Page * 3, new object());
        Assert.True(registry.TryPublishInitial(mapping));
        var old = Find(registry, Base);
        Assert.True(registry.TryBeginChange(new(Base + Page, Page), [], out var mutation));
        Finish(mutation!, outcome, false, 12);
        Assert.True(old.Domain.IsClosed);
        Assert.False(registry.TryFind(Base, out _)); // entire old domain remains quarantined
        Assert.False(registry.TryPublishInitial(mapping));
        Assert.Same(old, Assert.Single(registry.Snapshot()));
        Assert.False(registry.TryBeginChange(new(Base + Page, Page), [], out _)); // no partial repair of unknown coverage
        Assert.True(registry.TryBeginChange(new(Base, Page * 3), [mapping], out var repair));
        Finish(repair!, HostMappingKernelOutcome.AppliedWholeScope, true);
        Assert.NotEqual(old.Domain.Identity, Find(registry, Base).Domain.Identity);
    }

    [Fact]
    public void TimeoutKeepsIdentityClosedAndBlocksOperationUntilPinsRelease()
    {
        var registry = new HostMappingRegistry(Page);
        Assert.True(registry.TryPublishInitial(Spec(Base, Page, new object())));
        var old = Find(registry, Base);
        Assert.True(old.Domain.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1,
            old.Domain.DomainEpoch, out var pin));
        Assert.True(registry.TryBeginChange(new(Base, Page), [], out var mutation));
        Assert.False(mutation!.TryStartKernelOperation(out _));
        Assert.False(mutation.TryDrain(TimeSpan.Zero));
        Assert.True(old.Domain.IsClosed);
        Assert.False(registry.TryFind(Base, out _));
        Assert.False(registry.TryBeginChange(new(Base + 8 * Page, Page), [], out _)); // global serialization
        Assert.True(old.Domain.Release(pin));
        Finish(mutation, HostMappingKernelOutcome.AppliedWholeScope, true);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void AliasRetirementClosesEveryRegisteredBackingViewBeforeDrain()
    {
        var registry = new HostMappingRegistry(Page);
        var backing = new object();
        Assert.True(registry.TryPublishInitial(Spec(Base, Page, backing)));
        Assert.True(registry.TryPublishInitial(Spec(Base + 0x10_0000, Page, backing, offset: Page * 2)));
        var other = Spec(Base + 0x20_0000, Page, new object());
        Assert.True(registry.TryPublishInitial(other));
        var first = Find(registry, Base);
        var second = Find(registry, Base + 0x10_0000);
        var untouched = Find(registry, other.Address);
        Assert.True(second.Domain.TryAcquireWritePin(second.Mapping.Address, HostRecoveryAccess.Write,
            1, second.Domain.DomainEpoch, out var pin));
        Assert.True(registry.TryBeginBackingRetirement(backing, out var retirement));
        Assert.True(first.Domain.IsClosed);
        Assert.True(second.Domain.IsClosed);
        Assert.False(untouched.Domain.IsClosed);
        Assert.False(retirement!.TryDrain(TimeSpan.Zero));
        Assert.True(second.Domain.Release(pin));
        Assert.True(retirement.TryDrain(TimeSpan.Zero));
        Assert.True(retirement.TryStartKernelOperation(out var operation));
        Assert.Equal(new HostAddressRange[] { new(Base, Page), new(Base + 0x10_0000, Page) }, operation!.Ranges);
        Assert.False(retirement.RecordKernelOutcome(operation, [operation.Ranges[0]],
            HostMappingKernelOutcome.AppliedWholeScope, 0)); // incomplete alias receipt
        Assert.False(retirement.TryAbandonBeforeKernel());
        Assert.True(retirement.RecordKernelOutcome(operation, operation.Ranges, HostMappingKernelOutcome.AppliedWholeScope, 0));
        Assert.True(retirement.CompleteRecordedOperation(operation));
        Assert.Same(untouched, Assert.Single(registry.Snapshot()));
    }

    [Fact]
    public void InFlightOperationCannotBeAbandonedOrReplacedAndMustRecordExactScopeOnce()
    {
        var registry = new HostMappingRegistry(Page);
        Assert.True(registry.TryPublishInitial(Spec(Base, Page, new object())));
        Assert.True(registry.TryBeginChange(new(Base, Page), [], out var mutation));
        Assert.True(mutation!.TryDrain(TimeSpan.Zero));
        Assert.True(mutation.TryStartKernelOperation(out var operation));
        Assert.False(mutation.TryStartKernelOperation(out _));
        Assert.False(mutation.TryAbandonBeforeKernel());
        Assert.False(registry.TryBeginChange(new(Base, Page), [], out _));
        Assert.False(mutation.RecordKernelOutcome(operation!, [new(Base + Page, Page)], HostMappingKernelOutcome.AppliedWholeScope, 0));
        Assert.False(mutation.RecordKernelOutcome(operation!, operation!.Ranges, HostMappingKernelOutcome.AppliedWholeScope, 12));
        Assert.True(mutation.RecordKernelOutcome(operation!, operation!.Ranges, HostMappingKernelOutcome.AppliedWholeScope, 0));
        Assert.False(mutation.RecordKernelOutcome(operation!, operation!.Ranges, HostMappingKernelOutcome.AppliedWholeScope, 0));
        Assert.False(mutation.TryAbandonBeforeKernel());
        Assert.True(mutation.CompleteRecordedOperation(operation));
        Assert.False(mutation.CompleteRecordedOperation(operation));
    }

    [Fact]
    public void AbandonBeforeKernelLeavesClosedCoverageAndDoesNotOpenPlannedReplacement()
    {
        var registry = new HostMappingRegistry(Page);
        var oldSpec = Spec(Base, Page, new object());
        Assert.True(registry.TryPublishInitial(oldSpec));
        var old = Find(registry, Base);
        Assert.True(registry.TryBeginChange(new(Base, Page), [oldSpec], out var mutation));
        Assert.True(mutation!.TryAbandonBeforeKernel());
        Assert.False(mutation.TryAbandonBeforeKernel());
        Assert.False(mutation.TryDrain(TimeSpan.Zero));
        Assert.False(registry.TryFind(Base, out _));
        Assert.Same(old, Assert.Single(registry.Snapshot()));
    }

    [Fact]
    public void OperationFromAnotherRegistryCannotRecordDespiteEqualScalarIdentity()
    {
        var first = new HostMappingRegistry(Page);
        var second = new HostMappingRegistry(Page);
        Assert.True(first.TryBeginChange(new(Base, Page), [], out var one));
        Assert.True(second.TryBeginChange(new(Base, Page), [], out var two));
        Assert.True(one!.TryDrain(TimeSpan.Zero));
        Assert.True(two!.TryDrain(TimeSpan.Zero));
        Assert.True(one.TryStartKernelOperation(out var firstOp));
        Assert.True(two.TryStartKernelOperation(out var secondOp));
        Assert.Equal(firstOp!.Identity, secondOp!.Identity);
        Assert.False(one.RecordKernelOutcome(secondOp, secondOp.Ranges, HostMappingKernelOutcome.AppliedWholeScope, 0));
        Assert.False(one.CompleteRecordedOperation(secondOp));
        Assert.True(one.RecordKernelOutcome(firstOp, firstOp.Ranges, HostMappingKernelOutcome.AppliedWholeScope, 0));
        Assert.True(one.CompleteRecordedOperation(firstOp));
        Assert.True(two.RecordKernelOutcome(secondOp, secondOp.Ranges, HostMappingKernelOutcome.Unknown, 0));
        Assert.False(two.CompleteRecordedOperation(secondOp));
    }

    [Fact]
    public async Task RegistryDrainReleasesGateBeforeWaitingForRecoveryCompletion()
    {
        var registry = new HostMappingRegistry(Page);
        Assert.True(registry.TryPublishInitial(Spec(Base, Page, new object())));
        var old = Find(registry, Base);
        Assert.True(old.Domain.TryAcquireWritePin(Base, HostRecoveryAccess.Write, 1,
            old.Domain.DomainEpoch, out var pin));
        Assert.True(registry.TryBeginChange(new(Base, Page), [], out var mutation));
        using var started = new ManualResetEventSlim();
        // Dedicated threads: under a loaded parallel run the thread pool could take
        // longer than a short drain budget to schedule the releasing task. A drain
        // that held the registry gate would block the release until its own 30 s
        // budget ran out, so the release bound below still catches that.
        var drain = Task.Factory.StartNew(() => { started.Set(); return mutation!.TryDrain(TimeSpan.FromSeconds(30)); },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var release = Task.Factory.StartNew(() =>
        {
            Assert.Single(registry.Snapshot()); // requires registry gate while drain is in progress
            return old.Domain.Release(pin);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(await release.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(mutation!.TryStartKernelOperation(out var operation));
        Assert.True(mutation.RecordKernelOutcome(operation!, operation!.Ranges, HostMappingKernelOutcome.AppliedWholeScope, 0));
        Assert.True(mutation.CompleteRecordedOperation(operation));
    }
}
