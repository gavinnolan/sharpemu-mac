// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using BindingLayout = SharpEmu.ShaderCompiler.Resources.BindingLayout;
using DescriptorBinding = SharpEmu.ShaderCompiler.Resources.DescriptorBinding;
using DescriptorBindingKind = SharpEmu.ShaderCompiler.Resources.DescriptorBindingKind;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// Which draws are left out while their pipeline compiles (RenderExecutor.MayLeaveOutWhileCompiling),
// and the dispatch that suspends on its pipeline running again on the program it prepared.
[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorCompilingDrawTests : IDisposable
{
    private readonly RecordingRenderHost _host = new() { LeavesOutDrawsWhileCompiling = true };
    private readonly FakePipelineProvider _pipelines = new() { PendingGraphicsCompiles = 1 };
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorCompilingDrawTests()
    {
        _executor = new RenderExecutor(_host, _pipelines);
    }

    public void Dispose() => _fatal.Dispose();

    private bool Drawn => _host.Calls.Any(call => call.StartsWith("draw ", StringComparison.Ordinal));

    private bool AskedWithoutWaiting => _pipelines.Calls.Contains("try_create_graphics_pipeline");

    [Fact]
    public void ADrawIntoRebuiltTargetsIsLeftOutWhileItsPipelineCompiles()
    {
        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.True(AskedWithoutWaiting);
        Assert.False(Drawn);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("begin_rendering", StringComparison.Ordinal));
        // Nothing of it stays bound for the next draw.
        Assert.Equal("reset_bindings", _host.Calls[^1]);

        _executor.DrawAuto(2, Banks(), Auto(3));
        Assert.True(Drawn);
    }

    [Fact]
    public void AHostThatLeavesNothingOutWaits()
    {
        _host.LeavesOutDrawsWhileCompiling = false;
        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.False(AskedWithoutWaiting);
        Assert.True(Drawn);
    }

    [Fact]
    public void TargetsTheHostDoesNotKnowAreRebuiltEveryFrameWait()
    {
        _host.TargetsRebuiltEveryFrame = false;
        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.Contains("may_leave_out", _host.Calls);
        Assert.False(AskedWithoutWaiting);
        Assert.True(Drawn);
    }

    [Fact]
    public void APredicatedDrawOrOneInsideAnOcclusionQueryWaits()
    {
        _executor.DrawAuto(1, Banks(), Auto(3) with { MustExecute = true });

        Assert.False(AskedWithoutWaiting);
        Assert.True(Drawn);
    }

    [Fact]
    public void ADrawWhoseTargetCarriesAClearWaits()
    {
        _host.ColorMetadataClears[ColorBase] = default;
        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.False(AskedWithoutWaiting);
        Assert.True(Drawn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADrawThatWritesDepthWaits(bool writes)
    {
        var banks = Banks(withDepth: true);
        // Test on, LESS; the write bit is what differs.
        banks.Context.DepthTarget = banks.Context.DepthTarget with { DepthControl = (1u << 1) | (1u << 4) | (writes ? 1u << 2 : 0) };
        _executor.DrawAuto(1, banks, Auto(3));

        Assert.Equal(!writes, AskedWithoutWaiting);
        Assert.Equal(writes, Drawn);
    }

    [Fact]
    public void ADrawWhoseProgramWritesMemoryWaits()
    {
        var pixel = Program(ShaderStageKind.Pixel, buffers: [new BufferResourceInfo(true, true, false, false, false, 16, 0)]);
        _pipelines.Graphics = Programs(pixelStage: Stage(pixel, buffers: [BufferDescriptor(VertexBase, 16, 1)]));
        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.False(AskedWithoutWaiting);
        Assert.True(Drawn);
    }

    public static TheoryData<string, ShaderProgramInfo, bool> Programs_() => new()
    {
        { "read-only buffer and texture", Program(ShaderStageKind.Pixel,
            buffers: [new BufferResourceInfo(true, false, false, false, false, 16, 0)],
            images: [new ImageResourceInfo(ImageResourceClass.Sampled, false)]), false },
        { "written buffer", Program(ShaderStageKind.Pixel, buffers: [new BufferResourceInfo(false, true, false, false, false, 16, 0)]), true },
        { "atomic buffer", Program(ShaderStageKind.Pixel, buffers: [new BufferResourceInfo(true, false, true, false, false, 16, 0)]), true },
        { "storage image", Program(ShaderStageKind.Pixel, images: [new ImageResourceInfo(ImageResourceClass.Storage, false)]), true },
        { "written image", Program(ShaderStageKind.Pixel, images: [new ImageResourceInfo(ImageResourceClass.Sampled, true)]), true },
        { "device-address stores", Program(ShaderStageKind.Vertex, usesDeviceAddresses: true, writesDeviceAddresses: true), true },
        { "device-address loads", Program(ShaderStageKind.Vertex, usesDeviceAddresses: true), false },
        { "global data share", WithBindings(Program(ShaderStageKind.Pixel), new BindingLayout
            {
                Descriptors = [new DescriptorBinding(DescriptorBindingKind.GlobalDataShare, [])],
            }), true },
        { "mip statistics", WithBindings(Program(ShaderStageKind.Pixel), new BindingLayout { MipStatisticsCount = 1 }), true },
    };

    [Theory]
    [MemberData(nameof(Programs_))]
    public void WritesOutsideTargets_ReadsTheProgramConservatively(string what, ShaderProgramInfo program, bool writes)
    {
        _ = what;
        Assert.Equal(writes, RenderExecutor.WritesOutsideTargets(Stage(program)));
    }

    private static ShaderProgramInfo WithBindings(ShaderProgramInfo program, BindingLayout bindings) => new()
    {
        Stage = program.Stage,
        Hash = program.Hash,
        Buffers = program.Buffers,
        Images = program.Images,
        Bindings = bindings,
    };

    [Fact]
    public void ASuspendedDispatchRunsAgainOnTheProgramItPrepared()
    {
        var completion = new TaskCompletionSource();
        _host.SupportsPipelineSuspend = true;
        _pipelines.ReuseRetries = true;
        _pipelines.PendingComputeCompletion = completion.Task;

        _executor.Dispatch(5, Banks(), 4, 1, 1, 0x41);
        Assert.Contains("pipeline_not_ready", _host.Calls);
        // Still compiling at the retry: the program is not prepared again.
        _executor.Dispatch(5, Banks(), 4, 1, 1, 0x41);
        completion.SetResult();
        _executor.Dispatch(5, Banks(), 4, 1, 1, 0x41);

        Assert.Contains("dispatch 4 1 1", _host.Calls);
        Assert.Single(_pipelines.Calls, call => call.StartsWith("get_compute_program", StringComparison.Ordinal));
        Assert.Equal(3, _pipelines.ComputeRetries.Count);
        Assert.Same(_pipelines.ComputeRetries[0], _pipelines.ComputeRetries[2]);
        Assert.Equal(2, _executor.ComputeRetriesReused);
    }

    [Fact]
    public void AnotherSubmissionDoesNotTakeASuspendedDispatchsProgram()
    {
        _host.SupportsPipelineSuspend = true;
        _pipelines.ReuseRetries = true;
        _pipelines.PendingComputeCompletion = new TaskCompletionSource().Task;

        _executor.Dispatch(5, Banks(), 4, 1, 1, 0x41);
        _executor.Dispatch(6, Banks(), 4, 1, 1, 0x41);

        Assert.Equal(2, _pipelines.Calls.Count(call => call.StartsWith("get_compute_program", StringComparison.Ordinal)));
        Assert.NotSame(_pipelines.ComputeRetries[0], _pipelines.ComputeRetries[1]);
        Assert.Equal(0, _executor.ComputeRetriesReused);
    }
}
