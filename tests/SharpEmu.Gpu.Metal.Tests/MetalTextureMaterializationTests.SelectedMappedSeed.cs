// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Fact]
    public void InitialSelectedMappedSeedUnavailablePreservesLegacyReadAndDetachedBytes()
    {
        SelectedMappedSeedAssertClr();
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
        SelectedMappedSeedAssertClr();
        using var state = new SelectedMipQueueScope();
        var (bytes, _) = SelectedMipSeed();
        var memory = new RefusedSelectedMappedMemory(bytes, result);
        var error = Assert.Throws<TargetInvocationException>(() => SelectedMipSnapshots(memory));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(attempts, memory.OptionalCalls); Assert.Equal(0, memory.Reads);
        Assert.Equal(0, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
    }

    [Fact]
    public void ReadonlyLegacyInitialCopyWithGlobalWriterDoesNotAddSelectedImageWriterDebt()
    {
        SelectedMappedSeedAssertClr(); using var state = new SelectedMipQueueScope();
        GuestImageWriteTracker.TrackManagedWriter(SelectedMipAddress, 786432, source: "selected-readonly-managed-control");
        var (bytes, expected) = SelectedMipSeed();
        var memory = new RefusedSelectedMappedMemory(bytes, CpuMappedCopyResult.Unavailable);
        var texture = Assert.Single(SelectedMipSnapshots(memory, access: GuestImageAccess.Read));
        Assert.Equal(expected, texture.RgbaPixels); Assert.Equal(1, memory.OptionalCalls); Assert.Equal(1, memory.Reads);
        var (work, sequence) = SubmitReadonlySelectedWithGlobalWriter(texture, SelectedMipAddress + 524288);
        Assert.True(sequence > 0);
        using ((IDisposable)Presenter.GetMethod("BeginSelectedStorageMipSeedExecution", Methods)!.Invoke(null, [work])!)
            Presenter.GetMethod("ValidateSelectedStorageMipOwner", Methods)!.Invoke(null, [SelectedMipAssociation(texture), false]);
        AssertSelectedWriterMetadataEmpty();
    }

    private static (object Work, long Sequence) SubmitReadonlySelectedWithGlobalWriter(GuestDrawTexture texture, ulong globalAddress)
    {
        Assert.Equal(GuestImageAccess.Read, texture.Access);
        var global = new GuestMemoryBuffer(globalAddress, new byte[4], 4, 4, false, Writable: true);
        var sequence = (long)Presenter.GetMethod("SubmitComputeDispatch", Methods)!.Invoke(null,
            [0UL, null, new[] { texture }, new[] { global }, 1u, 1u, 1u, 0u, 0u, 0u, true, null])!;
        Assert.True(sequence > 0); AssertSelectedWriterMetadataEmpty();
        object?[] taken = [null];
        Assert.True((bool)Presenter.GetMethod("TryTakeGuestWork", Methods)!.Invoke(null, taken)!);
        var pending = taken[0]!;
        Assert.Equal(sequence, pending.GetType().GetProperty("Sequence")!.GetValue(pending));
        var work = pending.GetType().GetProperty("Work")!.GetValue(pending)!;
        var globals = Assert.IsType<GuestMemoryBuffer[]>(work.GetType().GetProperty("GlobalMemoryBuffers")!.GetValue(work));
        Assert.Same(global, Assert.Single(globals)); Assert.True(global.Writable);
        return (work, sequence);
    }

    private static void AssertSelectedWriterMetadataEmpty()
    {
        foreach (var field in new[] { "_selectedStorageMipOwners", "_selectedStorageMipPending" })
            Assert.Empty((System.Collections.IDictionary)Presenter.GetField(field, Methods)!.GetValue(null)!);
    }

    private void SelectedMappedSeedAssertClr()
    {
        var actual = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        var expectedFramework = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, actual);
        if (!string.IsNullOrEmpty(expectedFramework)) Assert.Equal(expectedFramework, framework);
        output.WriteLine($"selected mapped seed actualCLR={actual} framework={framework}; expected={expected ?? "unspecified"}");
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
