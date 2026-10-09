// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private static object[]? r32DisjointFixtureQuarantine;

    [R32DisjointNativeFact]
    public void ActualR32OriginalMipZeroSampleAndDisjointMipOneWriterPreserveFullChain()
    {
        SelectedMappedSeedAssertClr(); Assert.True(RequireDevice());
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_R32_DISJOINT_NATIVE"));
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        Assert.Equal(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"));
        Assert.Null(r32DisjointFixtureQuarantine);
        var pending = new SelectedMipQueueScope();
        var host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
        var views = new SharedBackingViews(host, 0xC0000);
        SelectedMappedSeedHole? hole = null; SelectedNativeExecutionState? native = null;
        R32DisjointMemory? memory = null;
        IDisposable? logical = null;
        var oldTracker = GuestImageWriteTracker.Enabled;
        Exception? primary = null; ulong address = 0;
        try
        {
            Assert.True(views.IsAvailable); hole = new(host); address = hole.Address;
            Assert.True(address < 1UL << 40 && (address & 65535) == 0);
            Assert.True(views.TryMapReservedRange(address, 0xC0000, 0, HostPageProtection.ReadWrite, out _));
            var (guest, mip0, mip1) = R32DisjointIndependentSeed();
            Assert.True(views.TryWriteBacking(address, guest));
            Assert.False(GuestImageWriteTracker.TryGetContentToken(address, out _));
            Assert.False(GuestImageWriteTracker.TryGetContentToken(address + 0x40000, out _));
            GuestImageWriteTracker.Configure(true); // actual production BeginReadSnapshot; no test prearm
            memory = new(views); native = new(memory);
            uint[] readRaw = [(uint)(address >> 8), 0xC1600000, 0x003FC07F, 0x90900924, 0, 0x00700080, 0, 0];
            uint[] writeRaw = [(uint)(address >> 8), 0xC1600000, 0x003FC07F, 0x90911FAC, 0, 0x00700080, 0, 0];
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(readRaw, out var readDescriptor));
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(writeRaw, out var writeDescriptor));
            var read = new AgcExports.TranslatedImageBinding(readDescriptor, false, 0, [], ResourceDescriptor: readRaw,
                Shape: new(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
            var write = new AgcExports.TranslatedImageBinding(writeDescriptor, true, 1, [], ResourceDescriptor: writeRaw,
                Shape: new(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
            var source = Presenter.Assembly.GetTypes().Single(type => type.Name == "MetalTextureSnapshots");
            var textures = (IReadOnlyList<GuestDrawTexture>)source.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
                [new CpuContext(memory, Generation.Gen5), new[] { read, write }, 0])!;
            Assert.Equal(2, textures.Count);
            Assert.Equal((512u, 256u, 512u), (textures[0].Width, textures[0].Height, textures[0].Pitch));
            Assert.Equal((256u, 128u, 256u), (textures[1].Width, textures[1].Height, textures[1].Pitch));
            Assert.Equal(mip0, textures[0].RgbaPixels); Assert.Equal(mip1, textures[1].RgbaPixels);
            Assert.Equal(0, memory.OrdinaryReads); Assert.Equal(2, memory.Copies.Count);
            Assert.Contains(memory.Copies, copy => copy.SourceAddress == address + 0x40000 && copy.SourceByteCount == 0x80000);
            Assert.Contains(memory.Copies, copy => copy.SourceAddress == address + 0x20000 && copy.SourceByteCount == 0x20000);
            Assert.All(memory.Copies, copy => Assert.All(copy.Segments, segment =>
            {
                Assert.True(segment.BackingIdentity > 0 && segment.MappingIncarnation > 0);
                Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var expectedPage));
                Assert.Equal(expectedPage, segment.ProviderPageSize);
            }));
            logical = (IDisposable)Presenter.GetMethod("EnterGuestQueue", Methods)!.Invoke(null, ["dcb.graphics", 3UL])!;
            native.DispatchR32Disjoint(textures);
            var image = Assert.Single(((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!).Values.Cast<object>());
            var texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.NotEqual((nint)0, texture);
            Assert.Equal(2UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(256UL, ReadInteger(texture, Selector("width"))); Assert.Equal(128UL, ReadInteger(texture, Selector("height")));
            var expected = new byte[0x20000];
            for (var y = 0; y < 128; y++) for (var x = 0; x < 256; x++)
                mip0.AsSpan(((2 * y) * 512 + 2 * x) * 4, 4).CopyTo(expected.AsSpan((y * 256 + x) * 4, 4));
            Assert.Equal(expected, ReadNativeRows(texture, 256, 128, 4));
            var actualGuest = new byte[guest.Length]; Assert.True(views.TryReadBacking(address, actualGuest));
            Assert.Equal(guest, actualGuest); // complete original mip0/mip1/mip2/tail/padding, no publication claim
            Assert.Equal(0, memory.OrdinaryReads);
            output.WriteLine($"r32-disjoint-native actualCLR={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; " +
                $"provider_page={host.PageSize}; sampled_raw=90900924/RRRR; mip0=+40000/80000; mip1=+20000/20000; " +
                "publicSubmit/Drain; CBstatus=4; all32768-native-R32-values; all786432-original-guest-bytes; independent-mode9-equation; noCPUfallback");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null;
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
            Attempt(() => logical?.Dispose());
            Attempt(() => native?.Dispose());
            if (native?.Quarantined == true || cleanup is not null)
                r32DisjointFixtureQuarantine = [pending, views, hole!, native!, memory!];
            else
            {
                Attempt(pending.Dispose);
                Attempt(() => { if (address != 0) GuestImageWriteTracker.Untrack(address); });
                Attempt(() => { if (address != 0) GuestImageWriteTracker.Untrack(address + 0x40000); });
                Attempt(() =>
                {
                    try { views.Dispose(); if (!views.CleanupComplete) throw new IOException("R32 disjoint views cleanup incomplete."); }
                    catch { hole?.Quarantine(); throw; }
                });
                if (cleanup is null) Attempt(() => hole?.Dispose());
                else r32DisjointFixtureQuarantine = [pending, views, hole!, native!, memory!];
            }
            Attempt(() => GuestImageWriteTracker.Configure(oldTracker));
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["R32DisjointNativeCleanup"] = cleanup; } catch { }
            }
        }
    }

    private static (byte[] Guest, byte[] Mip0, byte[] Mip1) R32DisjointIndependentSeed()
    {
        var guest = Enumerable.Range(0, 0xC0000).Select(index => (byte)(index * 13 + 29)).ToArray();
        var mip0 = new byte[512 * 256 * 4]; var mip1 = new byte[256 * 128 * 4];
        Fill(512, 256, 0x40000, mip0, positive: true); Fill(256, 128, 0x20000, mip1, positive: false);
        return (guest, mip0, mip1);
        void Fill(int width, int height, int placement, byte[] linear, bool positive)
        {
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                // Independent standard64K mode9 32bpp equation (XXYYYXYXYXYXYX).
                // Expected bytes never call GnmTiling placement or detiling.
                var xx = x & 127; var yy = y & 127;
                var offset = (((y >> 7) * (width >> 7) + (x >> 7)) * 65536) +
                    ((xx & 3) << 2) + ((yy & 7) << 4) + ((xx & 4) << 5) +
                    ((yy & 8) << 5) + ((xx & 8) << 6) + ((yy & 16) << 6) +
                    ((xx & 16) << 7) + ((yy & 32) << 7) + ((xx & 32) << 8) +
                    ((yy & 64) << 8) + ((xx & 64) << 9);
                var value = (positive ? 1 : -1) * (y * width + x + .25f);
                var bits = BitConverter.SingleToUInt32Bits(value);
                BinaryPrimitives.WriteUInt32LittleEndian(guest.AsSpan(placement + offset), bits);
                BinaryPrimitives.WriteUInt32LittleEndian(linear.AsSpan((y * width + x) * 4), bits);
            }
        }
    }

    private sealed class R32DisjointMemory(SharedBackingViews views) : ICpuMemory, ICpuMappedReadWatchSource
    {
        internal readonly List<CpuMappedCopyObservation> Copies = [];
        internal int OrdinaryReads;
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { var result = views.TryReadMappedCopy(address, count, source, bytes, out observation); if (result == CpuMappedCopyResult.Copied) Copies.Add(observation!); return result; }
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { var result = views.TryReadMappedCopyWithWriteWatch(address, count, source, bytes, out observation); if (result == CpuMappedCopyResult.Copied) Copies.Add(observation!); return result; }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => views.IsMappedCopyObservationCurrent(original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => views.IsMappedCopyWriteWatchCurrent(original);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => views.ReleaseMappedCopyWriteWatch(original);
        public bool TryRead(ulong address, Span<byte> bytes) { OrdinaryReads++; return views.TryReadBacking(address, bytes); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("R32 disjoint fixture never writes back a CPU fallback.");
    }

    private sealed partial class SelectedNativeExecutionState
    {
        internal void DispatchR32Disjoint(IReadOnlyList<GuestDrawTexture> textures)
        {
            var sampled = ImageDescriptorBinding.ForImage(new ImageResource { ResourceClass = ImageResourceClass.Sampled,
                NumericClass = ImageNumericClass.Float, Dimension = ImageDimension.Dim2D })!.Value;
            var stored = ImageDescriptorBinding.ForImage(new ImageResource { ResourceClass = ImageResourceClass.Storage,
                NumericClass = ImageNumericClass.Float, Dimension = ImageDimension.Dim2D, Written = true })!.Value;
            var layout = Gen5MslArgumentLayout.Build(new BindingLayout { Descriptors = [new(sampled, [0]), new(stored, [1])] });
            var fields = layout.Fields.Where(field => field.FieldKind == MslArgumentFieldKind.Textures).ToArray();
            Assert.Equal(2, fields.Length);
            var input = fields.Single(field => field.Kind == sampled);
            var target = fields.Single(field => field.Kind == stored);
            var msl = $$"""
                #include <metal_stdlib>
                using namespace metal;
                struct Gen5StageResources {
                    array<texture2d<float>,1> {{input.Name}} [[id({{input.FirstId}})]];
                    array<texture2d<float,access::read_write>,1> {{target.Name}} [[id({{target.FirstId}})]];
                };
                kernel void original_r32_mip_downsample(constant Gen5StageResources& resources [[buffer(0)]], uint2 position [[thread_position_in_grid]]) {
                    if (position.x >= 256u || position.y >= 128u) return;
                    constexpr sampler point(coord::normalized,address::clamp_to_edge,filter::nearest);
                    float2 center=(float2(position*2u)+0.5f)/float2(512.0f,256.0f);
                    float4 value=resources.{{input.Name}}[0].sample(point,center,metal::level(0.0f));
                    float result=all(value==float4(value.x)) ? value.x : -999.0f;
                    resources.{{target.Name}}[0].write(float4(result),position);
                }
                """;
            var shader = new Gen5MslShader(msl, "original_r32_mip_downsample", Gen5MslStage.Compute, 0,
                ThreadgroupSizeX: 16, ThreadgroupSizeY: 16, ArgumentLayout: layout);
            var compiled = Activator.CreateInstance(Presenter.Assembly.GetTypes().Single(type => type.Name == "MetalCompiledGuestShader"), [shader])!;
            shaders.Add(compiled);
            var bindings = new GuestStageBindings(GuestStageKind.Compute, [], [0, 1], [], [], null, [], [], false, false, 0);
            var sequence = (long)Presenter.GetMethod("SubmitComputeDispatch", Methods)!.Invoke(null,
                [0UL, compiled, textures.ToArray(), Array.Empty<GuestMemoryBuffer>(), 16u, 8u, 1u, 0u, 0u, 0u, false, bindings])!;
            Assert.True(sequence > 0);
            Presenter.GetMethod("ReleasePreparedTextureSources", Methods)!.Invoke(null, [textures]);
            try
            {
                Presenter.GetMethod("DrainGuestWorkCore", Methods)!.Invoke(null, [Device.Value, queue, 5L * System.Diagnostics.Stopwatch.Frequency]);
                var command = (nint)Presenter.GetField("_lastFlushedCommandBuffer", Methods)!.GetValue(null)!;
                Assert.NotEqual((nint)0, command); SendVoid(command, Selector("waitUntilCompleted"));
                Assert.Equal(4UL, ReadInteger(command, Selector("status")));
                Assert.True((bool)Presenter.GetMethod("IsGuestWorkCompletedLocked", Methods)!.Invoke(null, [sequence])!);
            }
            catch { unknown = true; throw; }
        }
    }
}

internal sealed class R32DisjointNativeFactAttribute : FactAttribute
{
    public R32DisjointNativeFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_R32_DISJOINT_NATIVE") != "1")
            Skip = "R32 original disjoint mip native fixture requires a fresh granted native worker.";
    }
}
