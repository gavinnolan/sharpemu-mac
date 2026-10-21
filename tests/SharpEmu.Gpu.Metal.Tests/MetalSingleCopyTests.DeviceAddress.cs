// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// Single-copy textures next to device-address programs:
/// the ranges the host evaluated pin and give back the chunks they cover, a program it cannot
/// bound takes the global fallback, the shader's trap sees a released chunk read through the
/// page table and the host gives it back and pins it, the shadow mode releases nothing, and
/// the kill switch persists.
/// </summary>
public sealed unsafe partial class MetalSingleCopyTests
{
    private const string ProbeKernel = """

        kernel void probe(device const Gen5AddressRange* ranges [[buffer(0)]],
                          constant uint& range_count [[buffer(1)]],
                          device uint* faults [[buffer(2)]],
                          device const ulong* addresses [[buffer(3)]],
                          device uint* results [[buffer(4)]],
                          uint tid [[thread_position_in_grid]])
        {
            Gen5RangeCache cache = { 0ul, 0ul, nullptr };
            results[tid] = sharpemu_load_device_dword(ranges, range_count, faults, 0u, cache, addresses[tid]);
        }
        """;

    private sealed class PinsOn : IDisposable
    {
        private readonly bool _saved = MetalSingleCopy.DeviceAddressPins;

        public PinsOn(bool on = true) => MetalSingleCopy.DeviceAddressPins = on;

        public void Dispose() => MetalSingleCopy.DeviceAddressPins = _saved;
    }

    private static double Value(string describe, string name)
    {
        var token = describe.Split(' ').Single(part => part.StartsWith(name + "=", StringComparison.Ordinal));
        return double.Parse(token[(name.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DeviceAddressRange Range(ulong address, ulong size, bool planned = true, bool written = false) =>
        new(0, address, size, planned, written);

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), $"sharpemu-sc-da-{Guid.NewGuid():N}");

    // Reads one dword per address through the device-address table, as a translated program does.
    private static uint[] Probe(Rig rig, MetalDeviceAddressTable table, MetalDeviceAddressReach reach, ulong[] probes)
    {
        var pipeline = MetalGuestWindowResidencyTests.ComputePipeline(
            "#include <metal_stdlib>\nusing namespace metal;\n" + Gen5MslTranslator.DeviceAddressPrelude() + ProbeKernel, "probe");
        try
        {
            var harness = rig.Harness;
            var tableBuffer = harness.Buffer((ulong)table.ByteSize);
            table.Write(new Span<byte>((void*)MetalNative.Send(tableBuffer, MetalNative.Selector("contents")), table.ByteSize), null);
            var addresses = harness.Buffer(MemoryMarshal.AsBytes(probes.AsSpan()));
            var results = harness.Buffer((ulong)(probes.Length * sizeof(uint)));
            var count = (uint)table.Count | MetalDeviceAddressIndex.IndexedFlag;
            var encoder = MetalNative.Send(harness.CommandBuffer, MetalNative.Selector("computeCommandEncoder"));
            MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), pipeline);
            var setBuffer = MetalNative.Selector("setBuffer:offset:atIndex:");
            MetalNative.SendSetBuffer(encoder, setBuffer, tableBuffer, 0, 0);
            MetalNative.SendSetBytes(encoder, MetalNative.Selector("setBytes:length:atIndex:"), (nint)(&count), sizeof(uint), 1);
            MetalNative.SendSetBuffer(encoder, setBuffer, reach.Buffer, 0, 2);
            MetalNative.SendSetBuffer(encoder, setBuffer, addresses, 0, 3);
            MetalNative.SendSetBuffer(encoder, setBuffer, results, 0, 4);
            var resident = new List<nint>();
            foreach (var id in table.Windows)
            {
                resident.Add(rig.Memory.WindowBuffer(id));
            }

            var residentArray = resident.ToArray();
            fixed (nint* resources = residentArray)
            {
                MetalNative.SendUseResources(encoder, MetalNative.Selector("useResources:count:usage:"), resources,
                    (nuint)residentArray.Length, Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite);
            }

            MetalNative.SendDispatch(encoder, MetalNative.Selector("dispatchThreads:threadsPerThreadgroup:"),
                new MtlSize { Width = (nuint)probes.Length, Height = 1, Depth = 1 },
                new MtlSize { Width = (nuint)probes.Length, Height = 1, Depth = 1 });
            MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
            harness.Submit();
            harness.Begin();
            return MemoryMarshal.Cast<byte, uint>(harness.Read(results, 0, (ulong)(probes.Length * sizeof(uint)))).ToArray();
        }
        finally
        {
            MetalNative.SendVoid(pipeline, MetalNative.Selector("release"));
        }
    }

    private static MetalDeviceAddressTable Table(Rig rig)
    {
        var table = new MetalDeviceAddressTable();
        table.Rebuild(rig.Memory, visit => visit(rig.Address, BackingSize), (int)Gen5MslTranslator.MaxAddressRangeCount);
        return table;
    }

    // An exact range inside a released texture: the chunk it covers is pinned and the record
    // gets its bytes back before the program runs; no window this command buffer used maps the
    // hole, so nothing ends the command buffer. The range again is a lookup. The record's other
    // chunks are armed and released again; the pinned one never is.
    [Fact]
    public void AnEvaluatedRangeOverAReleasedChunkGivesItBackAndPinsIt()
    {
        if (!RequireDevice()) return;
        using var pins = new PinsOn();
        using var rig = new Rig(Device.Value);
        var request = Request(rig.ImageAddress, GuestTileMode.Standard64KB);
        var size = request.Description.Data.Size;
        var bytes = Pattern(size, 81);
        rig.Release(request, bytes, (int)(size / Chunk));
        var state = rig.Views.SingleCopy!;
        var first = rig.ChunkOf(rig.ImageAddress);

        DeviceAddressRange[] ranges = [Range(rig.ImageAddress + Chunk + 0x100, 64)];
        Assert.True(rig.SingleCopy.BeforeDeviceAddressProgram(false, ranges, 1, static (_, length) => length, late: false));
        Assert.False(rig.SingleCopy.DeviceAddressSeen);
        Assert.True(state.IsPinned(first + 1));
        Assert.False(state.IsPinned(first));
        Assert.Equal(0, state.ReleasedChunks);
        Assert.Equal(0, rig.Memory.SingleCopyHoleCount);
        Assert.Equal(bytes, rig.Raw(rig.ImageAddress, size));
        Assert.False(rig.Memory.HasStaleSingleCopyWindows);
        var describe = rig.SingleCopy.Describe();
        output.WriteLine(describe);
        Assert.Equal(1, Field(describe, "restores_da"));
        Assert.Equal(1, Field(describe, "da_ranges_exact"));
        Assert.Equal(0, Field(describe, "da_fallbacks"));
        Assert.True(Value(describe, "da_planned_mb") > 0);
        Assert.True(Value(describe, "da_pin_candidate_mb") > 0);

        Assert.False(rig.SingleCopy.BeforeDeviceAddressProgram(false, ranges, 1, static (_, length) => length, late: true));
        Assert.Equal(0, Field(rig.SingleCopy.Describe(), "da_late_pins"));

        for (var tick = 0; tick < 4; tick++)
        {
            rig.Submit();
            rig.Cache.Collect();
        }

        Assert.Equal((ulong)(size / Chunk - 1) * Chunk, rig.SingleCopy.Counts.EmptiedBytes);
        Assert.True(state.IsReleased(first));
        Assert.False(state.IsReleased(first + 1));
        Assert.Equal(bytes, rig.Read(rig.ImageAddress, size));
    }

    // The same pin while a window this command buffer used lies over the run: its buffer still
    // maps the hole, so the host must end the command buffer before the program (the windows say
    // so, and the trap map keeps the chunks flagged until then); the next one reads in place.
    [Fact]
    public void APinUnderAWindowInUseNeedsTheCommandBufferSplit()
    {
        if (!RequireDevice()) return;
        using var pins = new PinsOn();
        using var rig = new Rig(Device.Value);
        var request = Request(rig.ImageAddress, GuestTileMode.Standard64KB);
        var size = request.Description.Data.Size;
        var bytes = Pattern(size, 83);
        rig.Release(request, bytes, (int)(size / Chunk));
        var near = rig.Memory.Resolve(rig.ImageAddress + size, size);
        Assert.True(rig.Memory.IsInPlace(near));

        var version = rig.Memory.TrapVersion;
        Assert.True(rig.SingleCopy.BeforeDeviceAddressProgram(
            false, [Range(rig.ImageAddress + 0x40, 16)], 1, static (_, length) => length, late: false));
        Assert.True(rig.Memory.HasStaleSingleCopyWindows);
        Assert.NotEqual(version, rig.Memory.TrapVersion);
        var map = new byte[rig.Memory.TrapChunkCount];
        Assert.True(rig.Memory.CopyTrapChunks(map) > 0);
        Assert.NotEqual(0, map[rig.ChunkOf(rig.ImageAddress)]);

        rig.Submit();
        Assert.False(rig.Memory.HasStaleSingleCopyWindows);
        Assert.Equal(0, rig.Memory.CopyTrapChunks(map));
        var range = rig.Memory.Resolve(rig.ImageAddress, size);
        Assert.True(rig.Memory.IsInPlace(range));
        var readback = rig.Harness.Buffer(size);
        Copy(rig.Harness, range.Buffer, range.Offset, readback, size);
        rig.Harness.Submit();
        rig.Harness.Begin();
        Assert.Equal(bytes, rig.Harness.Read(readback, 0, size));
    }

    // A handle nothing evaluated, a store no evaluated range bounds, the kill switch, or the
    // evaluated ranges switched off: the global fallback, everything back, nothing released again.
    [Theory]
    [InlineData("unplanned")]
    [InlineData("hidden-store")]
    [InlineData("kill-switch")]
    [InlineData("pins-off")]
    public void AnUnboundedProgramTakesTheGlobalFallback(string cause)
    {
        if (!RequireDevice()) return;
        using var pins = new PinsOn(cause != "pins-off");
        using var rig = new Rig(Device.Value);
        var request = Request(rig.ImageAddress, GuestTileMode.Standard64KB);
        var size = request.Description.Data.Size;
        var bytes = Pattern(size, 85);
        rig.Release(request, bytes, (int)(size / Chunk));
        var directory = TempDirectory();
        try
        {
            if (cause == "kill-switch")
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, MetalSingleCopyKillSwitch.FileName), "set\n");
                rig.SingleCopy.KillSwitch = MetalSingleCopyKillSwitch.Open(directory);
                Assert.True(rig.SingleCopy.KillSwitch.IsSet);
            }

            // Elsewhere in the backing: only the fallback gives the texture back.
            var elsewhere = rig.Address + 4 * Mib;
            DeviceAddressRange[] ranges = cause == "unplanned" ? [Range(0, 0, planned: false)] : [Range(elsewhere, 64)];
            Assert.True(rig.SingleCopy.BeforeDeviceAddressProgram(
                cause == "hidden-store", ranges, 1, static (_, length) => length, late: false));
            Assert.True(rig.SingleCopy.DeviceAddressSeen);
            Assert.Equal(0, rig.Views.SingleCopy!.ReleasedChunks);
            Assert.Equal(bytes, rig.Raw(rig.ImageAddress, size));
            var describe = rig.SingleCopy.Describe();
            Assert.Equal(1, Field(describe, "da_fallbacks"));
            Assert.Equal(cause == "unplanned" ? 1 : 0, Field(describe, "da_unplanned_handles"));
            Assert.Equal(cause == "hidden-store" ? 1 : 0, Field(describe, "da_hidden_writes"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // A released chunk read through the page table outside every evaluated range: the table
    // flags it, the shader reads zero there (not the dummy's sentinel) and reports the page, and
    // the host gives the chunk back, pins it and sets the title's kill switch. The next table
    // flags nothing and the program reads the guest's bytes.
    [Fact]
    public void AReleasedChunkReadThroughTheTableIsTrappedGivenBackAndPinned()
    {
        if (!RequireDevice()) return;
        using var pins = new PinsOn();
        using var rig = new Rig(Device.Value, sentinel: true);
        var request = Request(rig.ImageAddress, GuestTileMode.Standard64KB);
        var size = request.Description.Data.Size;
        var bytes = Pattern(size, 87);
        var beside = Pattern(size, 88);
        Assert.True(rig.Views.TryWriteBacking(rig.ImageAddress + size, beside));
        rig.Release(request, bytes, (int)(size / Chunk));

        var table = Table(rig);
        Assert.Equal(1, table.TrapEntryCount);
        Assert.Equal(size, table.TrapBytes);
        var reach = new MetalDeviceAddressReach(rig.Memory, null);
        var inside = rig.ImageAddress + Chunk + 0x40;
        var outside = rig.ImageAddress + size + 0x80;
        var results = Probe(rig, table, reach, [inside, outside]);
        Assert.Equal(0u, results[0]);
        Assert.Equal(BitConverter.ToUInt32(beside, 0x80), results[1]);

        var pages = new List<ulong>();
        var hits = reach.TakeTrapHits(pages);
        Assert.Equal(1L, hits);
        Assert.Equal([(inside - rig.Address) >> DeviceAddressPaging.PageBits], pages.ToArray());

        var directory = TempDirectory();
        try
        {
            rig.SingleCopy.KillSwitch = MetalSingleCopyKillSwitch.Open(directory);
            Assert.False(rig.SingleCopy.KillSwitch.IsSet);
            rig.SingleCopy.OnTrapHits(hits, pages);
            var state = rig.Views.SingleCopy!;
            Assert.True(state.IsPinned(rig.ChunkOf(inside)));
            Assert.Equal(0, state.ReleasedChunks);
            Assert.Equal(bytes, rig.Raw(rig.ImageAddress, size));
            var describe = rig.SingleCopy.Describe();
            output.WriteLine(describe);
            Assert.Equal(1, Field(describe, "da_released_hits"));
            Assert.Equal(1, Field(describe, "da_released_hit_chunks"));
            Assert.Equal(0, Field(describe, "da_shadow_hits"));
            Assert.True(rig.SingleCopy.KillSwitch.Tripped);
            Assert.True(MetalSingleCopyKillSwitch.Open(directory).IsSet);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        rig.Submit();
        table.Recut(rig.Memory);
        Assert.Equal(0, table.TrapEntryCount);
        results = Probe(rig, table, reach, [inside, outside]);
        Assert.Equal(BitConverter.ToUInt32(bytes, (int)(Chunk + 0x40)), results[0]);
        Assert.Equal(BitConverter.ToUInt32(beside, 0x80), results[1]);
        pages.Clear();
        Assert.Equal(0L, reach.TakeTrapHits(pages));
        Assert.Empty(pages);
    }

    // The shadow mode runs the protocol to the end but punches and empties nothing: the guest
    // copy keeps its bytes, the GPU reads them through the windows, the table still flags the
    // chunks, and the trap counts the read as a shadow hit, lets it through, and sets no switch.
    [Fact]
    public void TheShadowModeReleasesNothingAndCountsWhatWouldHaveBeenRead()
    {
        if (!RequireDevice()) return;
        using var pins = new PinsOn(false);
        using var rig = new Rig(Device.Value, sentinel: true, shadow: true);
        Assert.True(rig.SingleCopy.IsShadow);
        var request = Request(rig.ImageAddress, GuestTileMode.Standard64KB);
        var size = request.Description.Data.Size;
        var bytes = Pattern(size, 89);
        rig.Release(request, bytes, (int)(size / Chunk));
        var first = rig.ChunkOf(rig.ImageAddress);
        Assert.Equal(bytes, rig.Raw(rig.ImageAddress, size));
        Assert.True(rig.Memory.IsSingleCopyHole(first));
        var describe = rig.SingleCopy.Describe();
        Assert.Equal(0, Value(describe, "single_copy_released_mb"));
        Assert.True(Value(describe, "sc_shadow_released_mb") > 0);
        Assert.Equal(1, Field(describe, "sc_shadow"));

        var table = Table(rig);
        Assert.Equal(1, table.TrapEntryCount);
        var reach = new MetalDeviceAddressReach(rig.Memory, null);
        var inside = rig.ImageAddress + 2 * Chunk + 0x10;
        var results = Probe(rig, table, reach, [inside]);
        Assert.Equal(BitConverter.ToUInt32(bytes, (int)(2 * Chunk + 0x10)), results[0]);
        var pages = new List<ulong>();
        var hits = reach.TakeTrapHits(pages);
        Assert.Equal(1L, hits);

        var directory = TempDirectory();
        try
        {
            rig.SingleCopy.KillSwitch = MetalSingleCopyKillSwitch.Open(directory);
            rig.SingleCopy.OnTrapHits(hits, pages);
            describe = rig.SingleCopy.Describe();
            output.WriteLine(describe);
            Assert.Equal(1, Field(describe, "da_shadow_hits"));
            Assert.Equal(1, Field(describe, "da_shadow_hit_chunks"));
            Assert.Equal(0, Field(describe, "da_released_hits"));
            Assert.True(rig.Views.SingleCopy!.IsPinned(rig.ChunkOf(inside)));
            Assert.False(rig.SingleCopy.KillSwitch.Tripped);
            Assert.False(File.Exists(Path.Combine(directory, MetalSingleCopyKillSwitch.FileName)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // Evaluated ranges are on in the shadow mode: a pin is not the fallback.
        Assert.False(rig.SingleCopy.DeviceAddressSeen);
        Assert.Equal(bytes, rig.Read(rig.ImageAddress, size));
    }

    // The kill switch: off without its file, set by a trip (once per session), and found set
    // by the next start.
    [Fact]
    public void TheKillSwitchPersistsAcrossStarts()
    {
        var directory = TempDirectory();
        try
        {
            var killSwitch = MetalSingleCopyKillSwitch.Open(directory);
            Assert.False(killSwitch.IsSet);
            Assert.False(killSwitch.Tripped);
            killSwitch.Trip("first");
            killSwitch.Trip("second");
            Assert.True(killSwitch.Tripped);
            Assert.False(killSwitch.IsSet);
            var path = Path.Combine(directory, MetalSingleCopyKillSwitch.FileName);
            Assert.Single(File.ReadAllLines(path));
            Assert.Contains("first", File.ReadAllText(path));
            Assert.True(MetalSingleCopyKillSwitch.Open(directory).IsSet);
            File.Delete(path);
            Assert.False(MetalSingleCopyKillSwitch.Open(directory).IsSet);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
