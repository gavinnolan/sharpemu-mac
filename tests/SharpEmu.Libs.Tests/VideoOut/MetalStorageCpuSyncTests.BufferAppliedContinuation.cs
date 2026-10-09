// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Real provider publication over owned pinned bytes, ordinary thread only.
    // No native handle/status/encoder or manufactured positive authority.
    [Fact]
    public void CompletedBufferUsesItsOwnAppliedContinuationAfterInputConsumption()
    {
        ActualArchitecture(); using var fixture = new BufferContinuationFixture();
        fixture.Apply();
        Assert.Same(fixture.Successor, InvokeVolume("ValidateFiniteStorageCompletedBuffer", fixture.Slot));
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("RequireFiniteStorageBufferOrigin", fixture.Buffer));
        Assert.Equal(fixture.Expected, fixture.Memory.Expected);
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads);
        Assert.NotSame(fixture.Mapping, fixture.Successor);
        Assert.Equal(fixture.Mapping.Segments, fixture.Successor!.Segments);
    }

    [Theory]
    [InlineData("session")] [InlineData("mapping")] [InlineData("input")]
    [InlineData("unknown")] [InlineData("not-completed")]
    public void CompletedBufferCannotAdoptChangedOriginalOrUncertainCompletion(string change)
    {
        ActualArchitecture(); using var fixture = new BufferContinuationFixture(); fixture.Apply();
        switch (change)
        {
            case "session": Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1); break;
            case "mapping": fixture.Memory.Map(); break; // actual same-address replacement/ABA
            case "input": fixture.Buffer.Data[13] ^= 1; break;
            case "unknown": PrecommitSet(fixture.Slot, "Unknown", true); break;
            case "not-completed": PrecommitSet(fixture.Slot, "Complete", false); break;
        }
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStorageCompletedBuffer", fixture.Slot));
        Assert.Equal(fixture.Expected, fixture.Memory.Expected); // no second publication
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads);
    }

    [Fact]
    public void ConsumedBufferRejectsDuplicateCommitEnrollmentAndDuplicateCompletion()
    {
        ActualArchitecture(); using var fixture = new BufferContinuationFixture(); fixture.Apply();
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("BeginFiniteStorageBufferPublication", fixture.Slot, fixture.Transaction));
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("RecordFiniteStorageBufferApplied", fixture.Slot, fixture.Transaction, fixture.Successor));
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("CompleteFiniteStorageBufferPublication", fixture.Slot));
        Assert.Same(fixture.Successor, InvokeVolume("ValidateFiniteStorageCompletedBuffer", fixture.Slot));
        Assert.Equal(fixture.Expected, fixture.Memory.Expected);
    }

    [Fact]
    public void AppliedBufferCancellationErrorRetainsExactTransactionAndCannotComplete()
    {
        ActualArchitecture(); using var fixture = new BufferContinuationFixture();
        fixture.Apply(throwAfterRealCancellation: true);
        Assert.Same(fixture.Transaction, PrecommitGet(fixture.Slot, "Transaction"));
        Assert.Same(fixture.Successor, PrecommitGet(fixture.Slot, "Applied"));
        Assert.True((bool)PrecommitGet(fixture.Slot, "DisposalAttempted")!);
        Assert.False((bool)PrecommitGet(fixture.Slot, "Complete")!);
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStorageCompletedBuffer", fixture.Slot));
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("CompleteFiniteStorageBufferPublication", fixture.Slot));
        Assert.Equal(1, fixture.Cancellation!.Attempts);
        Assert.Equal(fixture.Expected, fixture.Memory.Expected);
    }

    private sealed class BufferContinuationFixture : IDisposable
    {
        private readonly State state = new();
        internal readonly DisplayLedgerMemory Memory = new();
        internal readonly GuestMemoryBuffer Buffer;
        internal readonly CpuMappedCopyObservation Mapping;
        internal readonly object Slot;
        internal readonly byte[] Expected;
        internal CpuMappedPublication? Transaction;
        internal CpuMappedCopyObservation? Successor;
        internal BufferCancellationError? Cancellation;
        internal BufferContinuationFixture()
        {
            Memory.Backing.EnrollManagedWrites(); // real writer history BEFORE the original copy
            MetalVideoPresenter.AttachGuestMemory(Memory);
            var bytes = new byte[DisplayLedgerMemory.ByteCount];
            Assert.Equal(CpuMappedCopyResult.Copied, MetalVideoPresenter.TryReadFiniteStorageBuffer(Memory, Address, bytes, out var mapping, out var session));
            Mapping = mapping!;
            Buffer = new(Address, bytes, bytes.Length, (ulong)bytes.Length, false, true, true);
            MetalVideoPresenter.RegisterFiniteStorageBufferOrigin(Buffer, Memory, Mapping, session);
            var original = InvokeVolume("RequireFiniteStorageBufferOrigin", Buffer)!;
            var originals = Array.CreateInstance(original.GetType(), 1); originals.SetValue(original, 0);
            Slot = ((Array)InvokeVolume("CaptureFiniteStorageBufferPublications", new[] { Buffer }, originals)!).GetValue(0)!;
            Expected = bytes.ToArray(); Expected.AsSpan(0, 4).Fill(0xD9);
        }
        internal void Apply(bool throwAfterRealCancellation = false)
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                Memory.Ledger.TryPrepareMappedPublication(Memory.Backing, Mapping, [new(Address, Expected.AsMemory(0, 4))], out var actual));
            Assert.NotNull(actual);
            Transaction = actual;
            if (throwAfterRealCancellation)
            {
                Cancellation = new(actual!);
                Transaction = new CpuMappedPublication(Cancellation);
            }
            PrecommitSet(Slot, "Transaction", Transaction);
            InvokeVolume("BeginFiniteStorageBufferPublication", Slot, Transaction);
            Assert.Equal(CpuMappedPublicationResult.Applied, Transaction!.TryCommit());
            Successor = Transaction.SuccessfulObservation; Assert.NotNull(Successor);
            PrecommitSet(Slot, "Applied", Successor);
            InvokeVolume("RecordFiniteStorageBufferApplied", Slot, Transaction, Successor);
            if (throwAfterRealCancellation)
            {
                var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("CompleteFiniteStorageBufferPublication", Slot));
                Assert.Same(Cancellation!.Failure, failure.InnerException);
            }
            else InvokeVolume("CompleteFiniteStorageBufferPublication", Slot);
        }
        public void Dispose()
        {
            // Cancellation-error injection runs AFTER the exact real release;
            // the fixture knows this boundary, production must not infer it.
            if (Transaction is not null && Cancellation is null) Transaction.Dispose();
            Memory.Dispose(); state.Dispose();
        }
    }

    private sealed class BufferCancellationError(CpuMappedPublication actual) : CpuMappedPublicationTransaction
    {
        internal readonly IOException Failure = new("simulated cancellation error AFTER actual provider release");
        internal int Attempts;
        internal override CpuMappedPublicationResult TryCommit() => actual.TryCommit();
        internal override CpuMappedCopyObservation? SuccessfulObservation => actual.SuccessfulObservation;
        internal override void Cancel() { Attempts++; actual.Dispose(); throw Failure; }
    }
}
