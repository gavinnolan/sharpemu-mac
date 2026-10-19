// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Core.Cpu;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    [Fact]
    public void BufferOriginalRequiresTheHostSelectedReaderAndNeverRecapturesOnRefusal()
    {
        ActualArchitecture(); using var state = new State(); using var owners = new ColdOwnerState();
        using var source = new DisplayLedgerMemory(); using var other = new DisplayLedgerMemory();
        var tracked = new TrackedCpuMemory(source); var nested = new TrackedCpuMemory(tracked);
        var backend = new MetalGuestGpuBackend(); owners.Backend(backend);
        var host = new MetalCommandStreamHost(nested, backend, backend);
        MetalVideoPresenter.AttachGuestMemory(nested);
        Assert.Same(source, host.Memory); Assert.Same(source, Field("_guestMemory").GetValue(null));

        GuestMemoryBuffer Capture(ICpuMemory reader, out CpuMappedCopyObservation mapping)
        {
            var bytes = new byte[DisplayLedgerMemory.ByteCount];
            Assert.Equal(CpuMappedCopyResult.Copied,
                MetalVideoPresenter.TryReadFiniteStorageBuffer(reader, Address, bytes, out var observed, out var session));
            mapping = observed!; Assert.NotNull(mapping); Assert.Equal(source.Expected, bytes);
            var buffer = new GuestMemoryBuffer(Address, bytes, bytes.Length, (ulong)bytes.Length, false, true);
            MetalVideoPresenter.RegisterFiniteStorageBufferOrigin(buffer, reader, mapping, session);
            return buffer;
        }

        // The old manual fixture captures through the tracker and persists that
        // exact identity. A real ledger receipt cannot repair an identity mismatch.
        var raw = Capture(nested, out var rawMapping);
        Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() =>
            InvokeVolume("RequireFiniteStorageBufferOrigin", raw)).InnerException);
        Assert.True(source.IsMappedCopyObservationCurrent(rawMapping)); Assert.Equal(1, source.Copies);

        // Production Rendering uses host.Memory for both the FIRST copy and its
        // origin registration. No receipt is rewritten or captured a second time.
        var selected = Capture(host.Memory, out var selectedMapping);
        var original = InvokeVolume("RequireFiniteStorageBufferOrigin", selected)!;
        Assert.Same(source, Property(original, "Memory")); Assert.Same(source, Property(original, "Source"));
        Assert.Same(selectedMapping, Property(original, "Mapping")); Assert.Equal(source.Expected, selected.Data);
        Assert.Same(original, InvokeVolume("RequireFiniteStorageBufferOrigin", selected));
        Assert.Equal(2, source.Copies); Assert.Equal(0, source.Reads);

        MetalVideoPresenter.AttachGuestMemory(new TrackedCpuMemory(other));
        Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() =>
            InvokeVolume("RequireFiniteStorageBufferOrigin", selected)).InnerException);
        Assert.Same(source, Property(original, "Memory")); Assert.Same(source, Property(original, "Source"));
        Assert.Same(selectedMapping, Property(original, "Mapping"));
        Assert.Equal(2, source.Copies); Assert.Equal(0, other.Copies);
        Assert.Equal(0, source.Reads); Assert.Equal(0, other.Reads);
        Assert.Null(tracked.LastFailure); Assert.Null(nested.LastFailure); AssertNoGpuViewWorker(backend);
        // Actual managed ledger copies only: no watch/publication, native handle,
        // GPU command, status-4 completion, worker or shader is acquired.
    }
}
