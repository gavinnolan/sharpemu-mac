// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.GpuSplit;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Pad;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.GpuHost;

/// <summary>
/// The GPU host's backend for the cross-process tests (<c>SHARPEMU_GPU_HOST_BACKEND=probe</c>):
/// no Metal and no window, only counts of what arrived, so a test in process A can see every
/// message kind cross both ways.
/// </summary>
/// <remarks>
/// A command stream on <see cref="ProbeQueue"/> starts a probe on a thread of its own (the ring
/// thread must never call into the guest process): it watches the stream's first page, asks the
/// guest's video-out state every question the Metal host asks, presents the flip it reserved,
/// raises an interrupt numbered by the stream's dword count, presses a key in the window input,
/// reads the stream's first word through guest memory, and reports what it learned as the GPU
/// name. The counters come back through the backend's performance counters: draws = command
/// streams, pipelines = flips, shader compilations = host frames, draw milliseconds = control calls.
/// A flip whose request id is odd is refused.
/// </remarks>
internal sealed class SplitProbeBackend : IGuestGpuBackend
{
    public const uint ProbeQueue = 0xEE;
    public const int ProbeKey = 42;

    private long _submissions;
    private long _controls;
    private long _flips;
    private long _frames;

    public static IGuestGpuBackend Compose(SplitGpuEnvironment environment)
    {
        HostWindowInput.Connect();
        return new SplitProbeBackend();
    }

    public string BackendName => "Probe";

    public void EnsureStarted(uint width, uint height) => Interlocked.Increment(ref _controls);

    public bool TryCompileProgram(ShaderCompileRequest request, out IGuestCompiledShader? shader, out string error)
    {
        shader = null;
        error = "The probe backend compiles nothing.";
        return false;
    }

    public IGuestCompiledShader GetDepthOnlyFragmentShader() => throw new NotSupportedException("The probe backend has no shaders.");

    public void HideSplashScreen() => Interlocked.Increment(ref _controls);

    public void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length == (long)width * height * 4)
        {
            Interlocked.Increment(ref _frames);
        }
    }

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
    }

    public bool TrySubmitGuestImage(int videoOutHandle, int displayBufferIndex, ulong address, uint width, uint height,
        uint pitchInPixel, ulong flipRequestId)
    {
        Interlocked.Increment(ref _flips);
        return flipRequestId % 2 == 0;
    }

    public void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        Interlocked.Increment(ref _submissions);
        if (queue == ProbeQueue)
        {
            new Thread(() => RunProbe(memory, address, dwordCount, submissionId)) { IsBackground = true, Name = "SharpEmu GPU probe" }.Start();
        }
    }

    public IdleOutcome SubmitDone(ICpuMemory memory) => IdleOutcome.Completed;

    public void RunAfterPendingCommandStreams(Action work) => work();

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
    }

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) => false;

    public void CountShaderCompilation() => Interlocked.Increment(ref _controls);

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() =>
        (Interlocked.Read(ref _submissions), Interlocked.Read(ref _controls), Interlocked.Read(ref _flips), Interlocked.Read(ref _frames));

    public void RequestClose() => Interlocked.Increment(ref _controls);

    private static void RunProbe(ICpuMemory memory, ulong address, uint dwordCount, ulong submissionId)
    {
        try
        {
            GuestGpuMemoryHook.Current?.Pages.AddWatch(address, TrackerLayout.PageBytes, blockReads: false);
            var result = VideoOutBackendServices.TryReserveFlipRequest(1, 0, 1, (long)submissionId, gpuQueued: true, out var request);
            var done = VideoOutBackendServices.IsFlipDone(1, 0);
            var present = VideoOutBackendServices.CanPresentFlip(request, 5);
            var next = VideoOutBackendServices.NextFlipPresentTimestamp(request, 5);
            var buffer = VideoOutBackendServices.TryGetDisplayBufferInfo(1, 0, out var info);
            var events = VideoOutBackendServices.GetFlipEventCount(request);
            var title = VideoOutBackendServices.GetWindowTitle();
            VideoOutBackendServices.MarkFlipPresented(request);
            new EndOfPipeEvents().TriggerInterrupt((int)dwordCount, 3);
            HostWindowInput.SetKey(ProbeKey, down: true);
            Span<byte> word = stackalloc byte[8];
            var read = memory.TryRead(address, word) ? BitConverter.ToUInt64(word) : 0;
            VideoOutBackendServices.SetSelectedGpuName(string.Create(CultureInfo.InvariantCulture,
                $"probe result={result} request={request} done={done} present={present} next={next} " +
                $"buffer={buffer}:0x{info.Address:X} events={events} title={title} read=0x{read:X}"));
        }
        catch (Exception exception)
        {
            VideoOutBackendServices.SetSelectedGpuName($"probe failed: {exception.Message}");
        }
    }
}
