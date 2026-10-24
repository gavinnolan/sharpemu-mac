// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GpuRemote;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Host.MacOS;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.VideoOut;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.GpuSplit.Tests;

/// <summary>
/// The real native GPU host, started by this test as process A starts it, with the probe backend
/// instead of Metal (<c>SHARPEMU_GPU_HOST_BACKEND=probe</c>, SharpEmu.GpuHost's SplitProbeBackend):
/// every split message kind crosses between two processes, over a guest memory the host maps
/// through Mach and its own replica.
/// </summary>
[Collection(SplitCollection.Name)]
public sealed unsafe class SplitCrossProcessTests(ITestOutputHelper output)
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private const uint ProbeQueue = 0xEE;
    private const ulong Marker = 0x5052_4F42_4553_504C;

    [MacOSFact]
    public void EveryMessageKindCrossesToTheNativeGpuHostAndBack()
    {
        using var guest = new SharedGuestMemory(chunked: true);
        var server = MacOSHost.ServeGuestMemory(guest.Export, guest.Table.Region, out var service);
        var space = new RecordingGuestSpace();
        var side = new RecordingGuestSide();
        using var memory = new GuestGpuMemory(space);
        GpuProcessHost process;
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_GPU_HOST_BACKEND");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GPU_HOST_BACKEND", "probe");
            process = GpuProcessHost.Start(new GpuProcessHostOptions { Layout = SplitSharedState.Layout });
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GPU_HOST_BACKEND", previous);
        }

        var session = new SplitGpuSession(process.Channel, process.SyncRoot, memory, guest.Table, side, process, server);
        try
        {
            session.Begin(service, new HostVideoOptions(), "PPSA09999", installGlobals: false);
            output.WriteLine($"GPU host {process.Peer} up in {process.StartupTime.TotalMilliseconds:F0} ms");

            var address = guest.Address;
            memory.Register(address, SharedGuestMemory.ViewBytes, ReadWrite);
            *guest.Pointer(0) = Marker;
            Assert.True(session.Writer.Write(GpuRemoteMessage.ShaderHeader, new ShaderHeaderMessage { CodeAddress = 0x100, HeaderAddress = 0x200 }));
            Assert.True(session.Writer.Write(GpuRemoteMessage.FusedProgram, new FusedProgramMessage
            {
                Entry = 0x300, EntryHeader = 0x400, Continuation = 0x500, ContinuationHeader = 0x600,
            }));

            // The probe: a watch, every video-out question, a flip, an interrupt, a key, a read.
            var backend = session.Backend;
            backend.SubmitCommandStream(null!, ProbeQueue, address, dwordCount: 9, submissionId: 11, null);
            Wait.Until(() => side.Notices.Any(notice => notice.Kind == GpuSplitNotice.SelectedGpuName), "the probe's report", 30_000);
            var report = side.Notices.First(notice => notice.Kind == GpuSplitNotice.SelectedGpuName).Text;
            output.WriteLine(report);
            Assert.Contains("result=0 request=77 done=True present=True next=105 buffer=True:0x5000 events=2", report);
            Assert.Contains("title=SharpEmu · test window", report);
            Assert.Contains($"read=0x{Marker:X}", report);
            Assert.Contains("reserve 1 0 1 11", side.Calls);
            Assert.Contains((GpuSplitFlipEvent.Presented, 77UL), side.FlipEvents);
            Assert.Contains((9, 3u), side.Interrupts);
            Wait.Until(() => side.Inputs.Any(keys => keys.Contains(42)), "the probe's key");

            // The probe's watch: applied here, and in the summary, so managed writes cross.
            Assert.Equal(GuestPageProtection.Read, space.Last(address).Access);
            Assert.True(memory.MarkCpuWrite(address, 8));
            memory.MarkCpuWriteLanded(address, 8);
            Assert.False(session.ImageStore.MarkCpuWrite(address, 8));

            // Export flips: an odd request id is refused, an even one taken.
            Assert.True(backend.TrySubmitGuestImage(1, 0, address, 64, 64, 64, flipRequestId: 13));
            Assert.True(backend.TrySubmitGuestImage(1, 0, address, 64, 64, 64, flipRequestId: 14));
            Wait.Until(() => side.FlipEvents.Contains((GpuSplitFlipEvent.Refused, 13UL)), "the refused flip");

            // Control calls, a host frame in pieces, window options, gamepad output.
            backend.EnsureStarted(1920, 1080);
            backend.HideSplashScreen();
            backend.CountShaderCompilation();
            backend.Submit(new byte[640 * 480 * 4], 640, 480);
            session.SendVideoOptions(new HostVideoOptions { Width = 1280, Height = 720 });
            new SplitGamepadOutput(session.Writer).SetRumble(1, 2);
            backend.SubmitCommandStream(null!, 0, address, 4, 12, null);

            // The frame boundary waits for all of it; the counters say what arrived.
            Assert.Equal(IdleOutcome.Completed, backend.SubmitDone(null!));
            Assert.Equal((2L, 3.0, 2L, 1L), backend.ReadAndResetPerfCounters());

            // A mapping change parks the host's worker, and an unregistration crosses inside it.
            memory.RunMappingChange(() => memory.Unregister(address + SharedGuestMemory.Chunk, SharedGuestMemory.Chunk));
            Assert.False(memory.Covers(address + SharedGuestMemory.Chunk, 8));
            var ran = false;
            backend.RunAfterPendingCommandStreams(() => ran = true);
            Assert.True(ran);
            Assert.True(memory.NoteProtected(address, 0x1000, GuestPageProtection.Read));
            Assert.False(backend.QueryHdrOutputSupport());
            Assert.Equal(0, session.ProtectCounts.Skipped);
        }
        finally
        {
            session.Dispose();
        }

        Assert.True(process.HasExited);
        Assert.Equal(GpuProcessClient.ExitClean, process.ExitCode);
    }
}
