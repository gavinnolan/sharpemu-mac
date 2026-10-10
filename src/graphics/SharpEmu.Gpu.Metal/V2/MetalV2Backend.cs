// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The v2 backend at the guest-GPU seam. Deliberately minimal, like
/// <c>VulkanGuestGpuBackend</c>: MSL codegen plus the command stream, with no
/// snapshot or translated-draw surface — the render host serves the shared
/// RenderExecutor directly.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalV2Backend : IGuestGpuBackend
{
    private static readonly IGuestCompiledShader DepthOnlyFragmentShader =
        new MetalCompiledGuestShader(new Gen5MslShader(
            MslFixedShaders.CreateDepthOnlyFragment(),
            "depth_only_fs",
            Gen5MslStage.Pixel,
            AttributeCount: 0));

    public string BackendName => "Metal v2";

    public bool TryCompileProgram(ShaderCompileRequest request, out IGuestCompiledShader? shader, out string error)
    {
        shader = null;
        if (!Gen5MslTranslator.TryCompileProgram(request, out var compiled, out error))
        {
            return false;
        }

        shader = new MetalCompiledGuestShader(compiled);
        return true;
    }

    public IGuestCompiledShader GetDepthOnlyFragmentShader() => DepthOnlyFragmentShader;

    public void EnsureStarted(uint width, uint height) => MetalV2Presenter.EnsureStarted(width, height);

    public void HideSplashScreen() => MetalV2Presenter.HideSplashScreen();

    public void Submit(byte[] bgraFrame, uint width, uint height) =>
        MetalV2Presenter.Submit(bgraFrame, width, height);

    // v2 has no fixed-function guest-draw path; the executor renders every draw.
    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        _ = drawKind;
        _ = width;
        _ = height;
    }

    public bool TrySubmitGuestImage(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel,
        ulong flipRequestId)
    {
        _ = address;
        _ = width;
        _ = height;
        _ = pitchInPixel;
        if (!MetalV2Presenter.IsStarted)
        {
            return false;
        }

        // The flip queues behind the graphics submissions before it.
        return MetalV2Presenter.Host.CommandStream.TryEnqueueFlipPreparation(
            videoOutHandle, displayBufferIndex, flipRequestId);
    }

    public void SubmitCommandStream(
        ICpuMemory memory,
        uint queue,
        ulong address,
        uint dwordCount,
        ulong submissionId,
        object? geometrySnapshots)
    {
        MetalV2Presenter.EnsureStarted(1280, 720);
        var host = MetalV2Presenter.EnsureHost();
        if (queue == 0)
        {
            host.CommandStream.EnqueueGraphics(address, dwordCount, submissionId, geometrySnapshots);
        }
        else
        {
            host.CommandStream.EnqueueCompute(queue, address, dwordCount, submissionId, geometrySnapshots);
        }

        host.CommandStream.Wake();
        _ = memory;
    }

    public IdleOutcome SubmitDone(ICpuMemory memory)
    {
        _ = memory;
        if (HostSessionControl.IsShutdownRequested)
        {
            return IdleOutcome.Cancelled;
        }

        return MetalV2Presenter.IsStarted ? MetalV2Presenter.Host.CommandStream.Done() : IdleOutcome.Completed;
    }

    public void RunAfterPendingCommandStreams(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (MetalV2Presenter.IsStarted &&
            MetalV2Presenter.Host.Relay.TryRunAfterAcceptedCommandStreams(work))
        {
            return;
        }

        work();
    }

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        // The image cache finds a display surface by its guest range like any other
        // image, so no address needs registering up front.
        _ = address;
        _ = guestFormat;
    }

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType)
    {
        _ = address;
        _ = format;
        _ = numberType;
        return false;
    }

    public void CountShaderCompilation()
    {
        if (MetalV2Presenter.IsStarted)
        {
            MetalV2Presenter.Host.CountShaderCompilation();
        }
    }

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() =>
        MetalV2Presenter.IsStarted ? MetalV2Presenter.Host.ReadAndResetPerfCounters() : (0, 0, 0, 0);

    public void RequestClose() => MetalV2Presenter.RequestClose();
}
