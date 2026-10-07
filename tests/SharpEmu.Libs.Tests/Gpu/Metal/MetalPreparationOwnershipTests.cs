// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.VideoOut;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    // The registry foundation does not yet capture in production snapshots or
    // retain at admission. Transfer/retain calls below are explicit; disposal
    // uses the real frontend scope and registry reference counters, without GPU.
    [Fact]
    public void CanceledPreparationReleasesItsSourceOnceAndCanOpenAnotherScope()
    {
        using var state = new MetalTextureSourceLeaseTests.State();
        var source = MetalTextureSourceLeaseTests.PublishAndCapture();
        var host = (IRenderHost)_host;
        var preparation = host.BeginPreparation();
        OwnSources(preparation, source);
        preparation.Dispose();
        preparation.Dispose();
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(source).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(source).ReservedBytes);
        using var next = host.BeginPreparation();
    }

    [Fact]
    public void TransferToDisposedPreparationRollsBackNewOwnerWithoutClosingCurrentScope()
    {
        using var state = new MetalTextureSourceLeaseTests.State();
        var host = (IRenderHost)_host;
        var disposed = host.BeginPreparation();
        disposed.Dispose();
        using var current = host.BeginPreparation();
        var source = MetalTextureSourceLeaseTests.PublishAndCapture();
        var error = Assert.Throws<TargetInvocationException>(() => OwnSources(disposed, source));
        Assert.IsType<ObjectDisposedException>(error.InnerException);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(source).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(source).ReservedBytes);
        host.PrepareDeviceAddresses();
    }

    [Fact]
    public void MultipleSubmittedOwnersSurvivePreparationDisposalIndependently()
    {
        using var state = new MetalTextureSourceLeaseTests.State();
        var source = MetalTextureSourceLeaseTests.PublishAndCapture();
        var preparation = ((IRenderHost)_host).BeginPreparation();
        OwnSources(preparation, source);
        var shader = Shader("preparation_owner", Gen5MslStage.Compute);
        var first = SubmitAndRetain(shader, source);
        var second = SubmitAndRetain(shader, source);
        Assert.Equal(3, MetalVideoPresenter.TextureSourceState(source).References);
        preparation.Dispose();
        Assert.Equal(2, MetalVideoPresenter.TextureSourceState(source).References);
        SourceMethod("ReleaseQueuedTextureSourcesLocked", first);
        SourceMethod("ReleaseQueuedTextureSourcesLocked", first);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(source).References);
        SourceMethod("ReleaseQueuedTextureSourcesLocked", second);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(source).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(source).ReservedBytes);
    }

    [Fact]
    public void PreparedStageCanBeReusedWithinScopeButNotInNewOrForeignScope()
    {
        var host = (IRenderHost)_host;
        var preparation = host.BeginPreparation();
        var program = Program(ShaderStageKind.Compute, 99, new ShaderResourceInfo());
        var prepared = host.PrepareBindings(new ShaderStageResources(program, new ResourceSnapshot()));
        host.BindResources(prepared);
        host.BindResources(prepared);
        host.CommitBindings(GpuBindPoint.Compute, default, [prepared]);
        // Another host has a live scope, but must not accept this host's owner.
        var other = (IRenderHost)new MetalCommandStreamHost(_memory, _backend, _backend);
        using (other.BeginPreparation())
        {
            Assert.Throws<SchedulerFatalException>(() => other.BindResources(prepared));
        }
        preparation.Dispose();
        using var next = host.BeginPreparation();
        Assert.Throws<SchedulerFatalException>(() => host.BindResources(prepared));
        Assert.Throws<SchedulerFatalException>(() => host.CommitBindings(GpuBindPoint.Compute, default, [prepared]));
    }

    [Fact]
    public void FatalPreparationFailureUnwindsCapturedOwner()
    {
        using var state = new MetalTextureSourceLeaseTests.State();
        var source = MetalTextureSourceLeaseTests.PublishAndCapture();
        var host = (IRenderHost)_host;
        var program = Program(ShaderStageKind.Compute, 100, new ShaderResourceInfo { Samplers = [new SamplerResource()] });
        Assert.Throws<SchedulerFatalException>(() =>
        {
            using var preparation = host.BeginPreparation();
            OwnSources(preparation, source);
            host.PrepareBindings(new ShaderStageResources(program, new ResourceSnapshot { Samplers = [[]] }));
        });
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(source).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(source).ReservedBytes);
        using var next = host.BeginPreparation();
    }

    private static void OwnSources(IResourcePreparation preparation, params GuestDrawTexture[] sources) =>
        preparation.GetType().GetMethod("OwnTextureSources")!.Invoke(preparation, [sources]);

    private static object SubmitAndRetain(MetalCompiledGuestShader shader, GuestDrawTexture source)
    {
        Assert.True(MetalVideoPresenter.SubmitComputeDispatch(0, shader, [source], [], 1, 1, 1, 0, 0, 0, true, null) > 0);
        var field = typeof(MetalVideoPresenter).GetField("_pendingGuestWorkByQueue", BindingFlags.NonPublic | BindingFlags.Static)!;
        var queue = ((IDictionary)field.GetValue(null)!).Values.Cast<object>().Single();
        var last = queue.GetType().GetProperty("Last")!.GetValue(queue)!;
        var pending = last.GetType().GetProperty("Value")!.GetValue(last)!;
        var work = pending.GetType().GetProperty("Work")!.GetValue(pending)!;
        SourceMethod("RetainQueuedTextureSourcesLocked", work);
        return work;
    }

    private static void SourceMethod(string name, object work) => typeof(MetalVideoPresenter)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [work]);
}
