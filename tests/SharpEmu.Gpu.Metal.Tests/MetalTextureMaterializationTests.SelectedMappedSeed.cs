// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Fact]
    public void InitialSelectedMappedSeedUnavailablePreservesLegacyReadAndDetachedBytes()
    {
        var (bytes, expected) = SelectedMipSeed();
        var memory = new RefusedSelectedMappedMemory(bytes, CpuMappedCopyResult.Unavailable);
        var texture = Assert.Single(SelectedMipSnapshots(memory));
        Assert.Equal(1, memory.OptionalCalls); Assert.Equal(1, memory.Reads);
        Assert.Equal(expected, texture.RgbaPixels);
        Array.Fill(bytes, (byte)0xA5); Assert.Equal(expected, texture.RgbaPixels);
        GuestImageWriteTracker.Untrack(SelectedMipAddress);
    }

    [Theory]
    [InlineData(CpuMappedCopyResult.Rejected, 3)]
    [InlineData(CpuMappedCopyResult.Copied, 1)] // null original observation is never a valid positive
    [InlineData((CpuMappedCopyResult)17, 1)]
    public void InitialSelectedMappedSeedRefusalNeverFallsBackOrPublishes(CpuMappedCopyResult result, int attempts)
    {
        using var state = new SelectedMipQueueScope();
        var (bytes, _) = SelectedMipSeed();
        var memory = new RefusedSelectedMappedMemory(bytes, result);
        var error = Assert.Throws<TargetInvocationException>(() => SelectedMipSnapshots(memory));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(attempts, memory.OptionalCalls); Assert.Equal(0, memory.Reads);
        Assert.Equal(0, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
    }

    private sealed class RefusedSelectedMappedMemory(byte[] bytes, CpuMappedCopyResult result) : ICpuMemory, ICpuMappedCopySource
    {
        internal int OptionalCalls, Reads;
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong footprint, ulong source,
            Span<byte> destination, out CpuMappedCopyObservation? observation)
        {
            OptionalCalls++; observation = null;
            destination.Fill(0xA5); // partial/undefined optional storage must be discarded or overwritten
            return result;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => false;
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads++;
            if (address < SelectedMipAddress || address - SelectedMipAddress > (ulong)bytes.Length ||
                (ulong)destination.Length > (ulong)bytes.Length - (address - SelectedMipAddress)) return false;
            bytes.AsSpan(checked((int)(address - SelectedMipAddress)), destination.Length).CopyTo(destination);
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
