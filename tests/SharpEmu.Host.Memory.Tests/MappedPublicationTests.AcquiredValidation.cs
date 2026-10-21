// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed partial class MappedPublicationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void AcquiredOwnValidationCannotCertifyDifferentBackingBusyOrItsLaterHistory(bool overlap, bool unknown)
    {
        using var own = new State(); using var other = new State();
        var original = own.Capture();
        using var publication = own.Prepare(original, State.Address, [17]);
        Assert.Equal(CpuMappedPublicationResult.Applied, publication.TryCommit());
        var applied = Assert.IsType<CpuMappedCopyObservation>(publication.SuccessfulObservation);
        Assert.Equal(CpuMappedCopyResult.Copied, other.Host.Ledger.TryReadMappedCopyWithWriteWatch(
            other.Backing, State.Address, State.Page, State.Address, new byte[8], out var captured));
        var watched = Assert.IsType<CpuMappedCopyObservation>(captured);
        try
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                own.Views.TryPrepareMappedPublicationValidation(applied, out var validation));
            var exact = Assert.IsType<CpuMappedPublicationValidation>(validation);
            Assert.True(exact.IsCurrent);
            var operation = BeginValidationWriterOperation(other.Backing); Assert.True(ValidationWriterIdentity(operation) > 0);
            try
            {
                Assert.Equal(CpuMappedReadWatchStatus.Busy, other.Host.Ledger.GetMappedCopyWriteWatchStatus(other.Backing, watched));
                Assert.False(exact.IsOriginalDisjointReadWatchCurrent(watched)); // foreign registry, even identical VA
                exact.Dispose(); // known cancel before waiting on the OTHER backing
                Assert.False(exact.IsCurrent);
                Assert.Equal(CpuMappedReadWatchStatus.Busy, other.Host.Ledger.GetMappedCopyWriteWatchStatus(other.Backing, watched));
                Assert.True(other.Views.TryWriteBacking(State.Address + (overlap ? 0ul : 32ul), new byte[] { 93 }));
            }
            finally { EndValidationWriter(operation, !unknown); }
            Assert.Equal(overlap || unknown ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Current,
                other.Host.Ledger.GetMappedCopyWriteWatchStatus(other.Backing, watched));
            // SAME Applied observation can reprepare its own exclusion, but
            // cannot repair the different original watch after overlap/unknown.
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                own.Views.TryPrepareMappedPublicationValidation(applied, out var retry));
            using (retry) { Assert.True(retry!.IsCurrent); Assert.False(retry.IsOriginalDisjointReadWatchCurrent(watched)); }
            Assert.Same(applied, publication.SuccessfulObservation);
            Assert.Equal(17, own.Bytes()[0]); Assert.Equal(overlap ? 93 : 0, other.Bytes()[0]);
        }
        finally { other.Host.Ledger.ReleaseMappedCopyWriteWatch(watched); }
    }
}
