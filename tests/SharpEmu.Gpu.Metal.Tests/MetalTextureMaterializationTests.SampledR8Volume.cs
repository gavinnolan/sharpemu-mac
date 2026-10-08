// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Metal.Tests;
using SharpEmu.ShaderCompiler.Resources;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private const ulong R8VolumeAddress = 0x701529A000;
    private static uint[] R8VolumeWords() => [0x701529A0, 0xC0100000, 0x0000C000, 0xA0500800, 15, 0x00700000, 0, 0];
    private static AgcExports.TranslatedImageBinding R8VolumeBinding(uint[]? raw = null)
    {
        raw ??= R8VolumeWords();
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, false, 0, [], ResourceDescriptor: raw,
            Shape: new ShaderImageShape(true, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
    }
    private static (byte[] Physical, byte[] Linear) R8VolumeSeed()
    {
        using var reader = new StreamReader(typeof(MetalTextureMaterializationTests).Assembly.GetManifestResourceStream("R8SampledVolume.offsets.csv")!);
        var physical = Enumerable.Repeat((byte)0xCD, 4096).ToArray(); var linear = new byte[256]; var count = 0;
        while (reader.ReadLine() is { } row)
        {
            var fields = row.Split(',').Select(int.Parse).ToArray(); var index = (fields[2] * 4 + fields[1]) * 4 + fields[0];
            var value = (byte)index; physical[fields[3]] = value; linear[index] = value; count++;
        }
        Assert.Equal(256, count); return (physical, linear);
    }
    private sealed class R8VolumeMemory(byte[] physical) : ICpuMemory
    {
        internal readonly List<(ulong Address, int Bytes)> Reads = [];
        public bool TryRead(ulong address, Span<byte> bytes)
        {
            Reads.Add((address, bytes.Length));
            if (address != R8VolumeAddress || bytes.Length != 4096 || physical.Length != 4096) return false;
            physical.CopyTo(bytes); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("Read-only volume may not change guest memory.");
    }
    private static GuestDrawTexture R8VolumeSnapshot(R8VolumeMemory memory, AgcExports.TranslatedImageBinding? binding = null)
    {
        var snapshots = Presenter.Assembly.GetTypes().Single(static t => t.Name == "MetalTextureSnapshots");
        try { return Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
            [new CpuContext(memory, Generation.Gen5), new[] { binding ?? R8VolumeBinding() }, 0])!); }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    [Fact]
    public void SampledR8VolumeOwnedFullBlockDecodesAll256PrimaryOracleVoxelsAndMetadata()
    {
        var (physical, expected) = R8VolumeSeed(); var original = physical.ToArray();
        var raw = R8VolumeWords(); var binding = R8VolumeBinding(raw); var memory = new R8VolumeMemory(physical);
        try
        {
            var texture = R8VolumeSnapshot(memory, binding);
            Assert.Equal(expected, texture.RgbaPixels); Assert.Equal(original, physical);
            Assert.Equal(new[] { (R8VolumeAddress, 4096) }, memory.Reads);
            Assert.Equal((7u, 1u, 16u), Presenter.GetMethod("GuestTextureShape", Methods)!.Invoke(null, [texture]));
            Assert.Equal(4096UL, texture.SourceByteCount); Assert.Equal(4u, texture.Pitch);
            Assert.Equal(raw, texture.Descriptor); Assert.NotSame(raw, texture.Descriptor);
            Assert.Null(texture.TiledSource); Assert.Null(texture.Detile); Assert.Null(texture.MipUploads);
            Presenter.Assembly.GetTypes().Single(static t => t.Name == "MetalTextureSnapshots")
                .GetMethod("ValidateSampledSingletonVolumeUpload", Methods)!.Invoke(null, [texture]);
            Assert.Same(texture, Presenter.GetMethod("PrepareDrawTextureUpload", Methods)!.Invoke(null, [texture]));
            physical.AsSpan().Fill(0); raw[4] = 0;
            Assert.Equal(expected, texture.RgbaPixels); Assert.Equal(R8VolumeWords(), texture.Descriptor);
        }
        finally { GuestImageWriteTracker.Untrack(R8VolumeAddress); }
    }

    [Theory]
    [InlineData(0)] // metadata
    [InlineData(1)] // raw type/shape mismatch
    [InlineData(2)] // pitch/raw mismatch
    [InlineData(3)] // compact descriptor
    [InlineData(4)] // dynamic mip
    [InlineData(5)] // write access
    [InlineData(6)] // Uint reinterpretation
    [InlineData(7)] // non3D compiled image
    [InlineData(8)] // different PERF_MOD hint
    [InlineData(9)] // adjacent flag
    [InlineData(10)] // nonzero view mip
    [InlineData(11)] // alternate selector
    [InlineData(12)] // misaligned address
    public void SampledR8VolumeUnqualifiedRawShapeOrAccessRejectsBeforeRead(int kind)
    {
        var binding = R8VolumeBinding(); var raw = R8VolumeWords();
        binding = kind switch
        {
            0 => binding with { ResourceDescriptor = raw.Select((v, i) => i == 7 ? 1u : v).ToArray() },
            1 => binding with { Descriptor = binding.Descriptor with { Depth = 1 } },
            2 => binding with { Descriptor = binding.Descriptor with { Pitch = 8 } },
            3 => binding with { ResourceDescriptor = raw[..4] },
            4 => binding with { DynamicMip = true },
            5 => binding with { Access = GuestImageAccess.Write },
            6 => binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Uint } },
            7 => binding with { Shape = binding.Shape with { Volume = false } },
            8 => R8VolumeBinding(raw.Select((v, i) => i == 5 ? 0x00600000u : v).ToArray()),
            9 => R8VolumeBinding(raw.Select((v, i) => i == 5 ? v | 0x80000000u : v).ToArray()),
            10 => binding with { MipLevel = 1 },
            11 => R8VolumeBinding(raw.Select((v, i) => i == 3 ? 0xA0500FACu : v).ToArray()),
            12 => R8VolumeBinding(raw.Select((v, i) => i == 0 ? v + 1u : v).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var memory = new R8VolumeMemory(new byte[4096]);
        Assert.Throws<NotSupportedException>(() => R8VolumeSnapshot(memory, binding)); Assert.Empty(memory.Reads);
    }

    [Fact]
    public void SampledR8VolumeShortPhysicalBlockCannotBecomeRawLinearOrZeroSeed()
    {
        var memory = new R8VolumeMemory(new byte[4095]);
        try { Assert.Throws<InvalidOperationException>(() => R8VolumeSnapshot(memory)); Assert.Single(memory.Reads); }
        finally { GuestImageWriteTracker.Untrack(R8VolumeAddress); }
    }

    [Fact]
    public void SampledR8VolumeKnownGpuWriterBlockVetoRunsBeforeGuestRead()
    {
        var registry = (IDictionary)Presenter.GetField("_availableGuestImages", Methods)!.GetValue(null)!;
        var gate = Presenter.GetField("_gate", Methods)!.GetValue(null)!;
        lock (gate) registry.Add(R8VolumeAddress + 2048, 1u);
        var memory = new R8VolumeMemory(new byte[4096]);
        try { Assert.Throws<NotSupportedException>(() => R8VolumeSnapshot(memory)); Assert.Empty(memory.Reads); }
        finally { lock (gate) registry.Remove(R8VolumeAddress + 2048); }
    }

    [Fact]
    public void SampledR8VolumeEmptyPartialOrDeferredSeedRejectsBeforeNativeDeviceUse()
    {
        var texture = R8VolumeSnapshot(new R8VolumeMemory(R8VolumeSeed().Physical));
        try
        {
            foreach (var invalid in new[] { texture with { RgbaPixels = [] }, texture with { RgbaPixels = new byte[255] },
                texture with { TiledSource = new byte[4096] } })
            {
                var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                    Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, [nint.Zero, nint.Zero, invalid, true]));
                Assert.IsType<NotSupportedException>(failure.InnerException);
            }
        }
        finally { GuestImageWriteTracker.Untrack(R8VolumeAddress); }
    }

    [Theory]
    [InlineData(0)] // point native consumer, all256 load/sample voxels
    [InlineData(1)] // linear native consumer, all256 weighted/clamped voxel neighborhoods
    [InlineData(2)] // translated signed-negative integer load
    [InlineData(3)] // translated z16 load outside native depth
    public void SampledR8VolumeNativeAllVoxelUpload000RLoadSampleFilterAndSignedBounds(int kind)
    {
        var sampling = kind < 2;
        uint[] coords = sampling ? [0x3E000000, 0x3E000000, 0x3D000000] : kind == 2 ? [uint.MaxValue, 0, 0] : [0, 0, 16];
        Assert.True(Gen5ImageFixtures.TryCompile(sampling ? "ImageSampleLz" : "ImageLoad", 10,
            out var shader, out var error, unifiedFormat: 1, dstSelect: 0x800, addressValues: coords, storeResults: true), error);
        Assert.Contains("texture3d<float", shader!.Source);
        var textureName = shader.ArgumentLayout!.Fields.First(static f => f.FieldKind == MslArgumentFieldKind.Textures).Name;
        var bufferName = shader.ArgumentLayout.Fields.First(static f => f.FieldKind == MslArgumentFieldKind.BufferPointers).Name;
        var source = shader.Source;
        if (sampling)
        {
            var samplerName = shader.ArgumentLayout.Fields.First(static f => f.FieldKind == MslArgumentFieldKind.Samplers).Name;
            // Independent controlled consumer appended to the actual layout;
            // all-voxel filter proof is native MSL, not a claim of ISA lowering
            // for256 coordinates. The separate entry executes translated MIMG.
            source += $$"""

                kernel void r8_all_voxels(constant Gen5StageResources& r [[buffer(0)]]) {
                    for (uint i=0;i<256;i++) {
                        uint3 c=uint3(i%4,(i/4)%4,i/16);
                        float3 size=float3(4,4,16);
                        float4 a=r.{{textureName}}[0].read(c);
                        float4 b=r.{{textureName}}[0].sample(r.{{samplerName}}[0],(float3(c)+0.5f)/size,level(0.0f));
                        float4 d=r.{{textureName}}[0].sample(r.{{samplerName}}[0],(float3(c)+0.75f)/size,level(0.0f));
                        uint4 av=as_type<uint4>(a),bv=as_type<uint4>(b),dv=as_type<uint4>(d);
                        for (uint k=0;k<4;k++) { r.{{bufferName}}[0][i*12+k]=av[k]; r.{{bufferName}}[0][i*12+4+k]=bv[k]; r.{{bufferName}}[0][i*12+8+k]=dv[k]; }
                    }
                }
                """;
        }
        if (!RequireDevice()) return; // Source/codegen only, never device coverage while disabled.
        var (physical, expected) = R8VolumeSeed(); var memory = new R8VolumeMemory(physical);
        nint texture = 0, library = 0, sampler = 0;
        var samplerKey = new GuestSampler(2u | 2u << 3 | 2u << 6, 0, kind == 1 ? (1u << 20 | 1u << 22) : 0, 0x52583000u + (uint)kind);
        var cache = (IDictionary)Presenter.GetField("_samplerCache", Methods)!.GetValue(null)!;
        try
        {
            var snapshot = R8VolumeSnapshot(memory); physical.AsSpan().Fill(0);
            var args = new object?[] { Device.Value, nint.Zero, snapshot, true };
            texture = (nint)Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, args)!;
            Assert.True((bool)args[3]!); Assert.NotEqual((nint)0, texture);
            Assert.Equal(7UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(10UL, ReadInteger(texture, Selector("pixelFormat"))); // actual R8Unorm
            Assert.Equal(4UL, ReadInteger(texture, Selector("width"))); Assert.Equal(4UL, ReadInteger(texture, Selector("height")));
            Assert.Equal(16UL, ReadInteger(texture, Selector("depth")));
            Assert.Equal(expected, ReadNativeVolume(texture, 4, 4, 16, 1));
            if (sampling)
            {
                lock (cache) Assert.False(cache.Contains(samplerKey));
                sampler = (nint)Presenter.GetMethod("GetOrCreateSampler", Methods)!.Invoke(null, [Device.Value, samplerKey])!;
                Assert.NotEqual((nint)0, sampler);
            }
            Assert.True(DeviceShader.TryCompileLibrary(source, out library, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, Enumerable.Repeat((byte)0xA7, 16).ToArray(),
                new byte[PushData.ByteSize], shader.ArgumentLayout, 1, out var translated, out error, texture, sampler), error);
            for (var c = 0; c < 4; c++)
            {
                var value = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(translated.AsSpan(c * 4)));
                Assert.Equal(0f, value); // origin value0 or rejected signed/outside load
            }
            if (sampling)
            {
                Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(library, "r8_all_voxels", Enumerable.Repeat((byte)0xA7, 256 * 12 * 4).ToArray(),
                    new byte[PushData.ByteSize], shader.ArgumentLayout, 1, out var all, out error, texture, sampler), error);
                for (var i = 0; i < 256; i++) for (var operation = 0; operation < 3; operation++) for (var c = 0; c < 4; c++)
                {
                    var want = c != 3 ? 0 : operation == 2 && kind == 1 ? R8LinearNeighborhood(expected, i) : expected[i] / 255f;
                    var actual = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((i * 12 + operation * 4 + c) * 4)));
                    Assert.InRange(Math.Abs(actual - want), 0, 1e-5f);
                }
            }
            Assert.Equal(new[] { (R8VolumeAddress, 4096) }, memory.Reads);
            Assert.Equal(-1, physical.AsSpan().IndexOfAnyExcept((byte)0)); // native read-only work left every guest byte unchanged after caller mutation
            output.WriteLine($"Actual native3D R8Unorm4x4x16 upload all256 primary-oracle voxels, 000R view and translated MIMG kind={kind}; controlled point/linear filter oracle={sampling}, signed load bounds; exact one owned4KiB read/no late guest reads.");
        }
        finally
        {
            if (sampler != 0)
            {
                lock (cache) if (cache.Contains(samplerKey) && (nint)cache[samplerKey]! == sampler) cache.Remove(samplerKey);
                SendVoid(sampler, Selector("release"));
            }
            if (library != 0) SendVoid(library, Selector("release"));
            if (texture != 0) SendVoid(texture, Selector("release"));
            GuestImageWriteTracker.Untrack(R8VolumeAddress);
        }
    }
    private static float R8LinearNeighborhood(byte[] values, int index)
    {
        var x = index % 4; var y = index / 4 % 4; var z = index / 16; var sum = 0f;
        for (var dz = 0; dz < 2; dz++) for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++)
        {
            var weight = (dx == 0 ? .75f : .25f) * (dy == 0 ? .75f : .25f) * (dz == 0 ? .75f : .25f);
            sum += weight * values[(Math.Min(z + dz, 15) * 4 + Math.Min(y + dy, 3)) * 4 + Math.Min(x + dx, 3)] / 255f;
        }
        return sum;
    }
}
