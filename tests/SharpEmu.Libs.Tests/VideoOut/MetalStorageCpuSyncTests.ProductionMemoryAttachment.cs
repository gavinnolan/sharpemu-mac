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
    public void ProductionAttachmentAndPreparationContextKeepSameOpaqueColdOwnerIdentity()
    {
        ActualArchitecture(); using var state = new State(); using var owners = new ColdOwnerState();
        var physical = new Memory([1]); var source = new OpaqueGpuViewDecorator(physical);
        var tracked = new TrackedCpuMemory(source); var nested = new TrackedCpuMemory(tracked);
        var backend = new MetalGuestGpuBackend(); owners.Backend(backend);
        var host = new MetalCommandStreamHost(nested, backend, backend);
        var context = (CpuContext)typeof(MetalCommandStreamHost).GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
        Assert.Same(source, host.Memory); Assert.Same(source, context.Memory);
        var session = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        // Reproduce the old fixture bypass with metadata only. The strict owner
        // guard must reject it; correcting setup must not unwrap inside the guard.
        Field("_guestMemory").SetValue(null, nested);
        Assert.False(MetalVideoPresenter.IsStorageOriginalEnrollmentOwnerCurrent(context.Memory, backend, Address, session));
        MetalVideoPresenter.AttachGuestMemory(nested);
        Assert.Same(source, Field("_guestMemory").GetValue(null));
        Assert.True(MetalVideoPresenter.IsStorageOriginalEnrollmentOwnerCurrent(context.Memory, backend, Address, session));
        Assert.False(MetalVideoPresenter.IsStorageOriginalEnrollmentOwnerCurrent(nested, backend, Address, session));
        MetalVideoPresenter.AttachGuestMemory(new TrackedCpuMemory(new OpaqueGpuViewDecorator(physical)));
        Assert.False(MetalVideoPresenter.IsStorageOriginalEnrollmentOwnerCurrent(context.Memory, backend, Address, session));
        Assert.Same(source, context.Memory); Assert.Equal(0, source.InnerQueries); Assert.Equal(0, source.Reads);
        Assert.Equal(0, physical.Reads); Assert.Null(tracked.LastFailure); Assert.Null(nested.LastFailure);
        AssertNoGpuViewWorker(backend);
        // No mapping, copy, watch, receipt, native worker or shader is acquired.
    }
}
