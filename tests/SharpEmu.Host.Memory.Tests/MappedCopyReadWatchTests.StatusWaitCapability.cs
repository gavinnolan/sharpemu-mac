// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    [Fact]
    public void ExistingOriginalStatusWaitCapabilityExcludesProviderGateWithoutRecapture()
    {
        using var state = new State(); var original = state.Capture();
        try
        {
            Assert.True(state.Ledger.CanWaitForMappedReadWatchStatus);
            lock (state.Gate) Assert.False(state.Ledger.CanWaitForMappedReadWatchStatus);
            Assert.True(state.Ledger.CanWaitForMappedReadWatchStatus);
            Assert.True(state.Current(original));
            Assert.Equal(CpuMappedReadWatchStatus.Current, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, original));
        }
        finally { state.Ledger.ReleaseMappedCopyWriteWatch(original); }
        Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Fact]
    public void UnknownBooleanOnlySourceCannotOptIntoExistingWatchStatusWaiting()
    {
        using var state = new State(); var original = state.Capture();
        try
        {
            ICpuMappedReadWatchSource source = new LegacyBooleanWatch();
            Assert.False(source.CanWaitForMappedReadWatchStatus);
            Assert.True(source.IsMappedCopyWriteWatchCurrent(original));
            Assert.Equal(CpuMappedReadWatchStatus.Stale, source.GetMappedCopyWriteWatchStatus(original));
        }
        finally { state.Ledger.ReleaseMappedCopyWriteWatch(original); }
    }
}
