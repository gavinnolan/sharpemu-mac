// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A V#-described image table read per lane through a device table of texture handles, on
/// the GPU: thousands of entries over hundreds of textures of three classes (float 2D, float
/// 2D array, uint 2D), entries the access cannot read (a 3D image, a sint format that needs
/// another sampler), null entries and keys past the table, which all read zero; and the
/// table rewritten between dispatches.
/// </summary>
public sealed class MetalBindlessImageTests(ITestOutputHelper output)
{
    // GTA V's waterfall (0x9FBCE50E7D68468F) in miniature, assembled with llvm-mc for gfx1030:
    //   s_load_dwordx4 s[8:11], s[0:1], 0x10       ; the table's V#
    //   s_load_dwordx4 s[24:27], s[0:1], 0x20      ; the data V#: outputs at 0, keys at 0x14000
    //   s_load_dwordx4 s[28:31], s[0:1], 0x30      ; the S#
    //   v1 = 0x14000 + lane * 4; v3 = lane * 16
    //   buffer_load_dword v2, v1, s[24:27], 0 offen ; v11 = v2 >> 4
    //   v[4:7] = (0.5, 0.5, 1.0, 0)                ; x, y, slice 1, lod 0
    //   loop: v_readfirstlane s100, v11; v_cmpx_eq_u32 s100, v11; s_lshl_b32 s100, s100, 5
    //         s_buffer_load_dwordx8 s[12:19], s[8:11], s100
    //         image_sample_l v[20:23], v[4:7], s[12:19], s[28:31] dmask:0xf dim:2D_ARRAY
    //   buffer_store_dwordx4 v[20:23], v3, s[24:27], 0 offen
    private static readonly uint[] Words =
    [
        0xF4080200, 0xFA000010, 0xF4080600, 0xFA000020, 0xF4080700, 0xFA000030, 0xBF8CC07F, 0x34020082,
        0x4A0202FF, 0x00014000, 0x34060084, 0xE0301000, 0x80060201, 0xBF8C3F70, 0x2C160484, 0x7E0802F0,
        0x7E0A02F0, 0x7E0C02F2, 0x7E0E0280, 0xBE86047E, 0x7E260281, 0x7EC8050B, 0xBE94047E, 0x7DA41664,
        0x8F648564, 0x7E260280, 0xF42C0304, 0xC8000000, 0xBF8CC07F, 0xF0900F28, 0x00E31404, 0xBEFE0414,
        0x7DAA2680, 0xBF89FFF3, 0xBEFE0406, 0xBF8C3F70, 0xE0781000, 0x80061403, 0xBF810000,
    ];

    private const ulong ResourceBlock = 0x1000;
    private const ulong TableAddress = 0x10000;
    private const int Entries = 4096;
    private const int Lanes = 1024;
    private const int KeyOffset = 0x14000;
    private const uint UserDataCount = 16;
    private const ulong Hash = 0x4249_4E44_4C45_5353;

    private const uint FloatFormat = 71;
    private const uint UintFormat = 72;
    private const uint SintFormat = 73;
    private const uint Type2D = 9;
    private const uint Type3D = 10;
    private const uint Type2DArray = 13;

    private enum Kind { Float2D, Float2DArray, Uint2D, Sint2D, Float3D }

    // Three hundred textures: 150 float 2D, 80 float 2D arrays, 50 uint 2D, and ten each the
    // access cannot read. The descriptor's first word names it.
    private static Kind KindOf(int texture) => texture switch
    {
        < 150 => Kind.Float2D,
        < 230 => Kind.Float2DArray,
        < 280 => Kind.Uint2D,
        < 290 => Kind.Sint2D,
        _ => Kind.Float3D,
    };

    private const int Textures = 300;

    private static uint[] Descriptor(int texture) => KindOf(texture) switch
    {
        Kind.Float2D => Words8(texture, FloatFormat, Type2D, 0),
        Kind.Float2DArray => Words8(texture, FloatFormat, Type2DArray, 1),
        Kind.Uint2D => Words8(texture, UintFormat, Type2D, 0),
        Kind.Sint2D => Words8(texture, SintFormat, Type2D, 0),
        _ => Words8(texture, FloatFormat, Type3D, 0),
    };

    private static uint[] Words8(int texture, uint format, uint type, uint lastSlice) =>
        [0x100u + (uint)texture, format << 20, 0, (type << 28) | DescriptorConstants.IdentityImageSwizzle, lastSlice, 0, 0, 0];

    // The value texel c of a texture holds where the access samples it (layer 1 of an array).
    private static uint Texel(int texture, int component) => KindOf(texture) switch
    {
        Kind.Uint2D => 0x10000u + (uint)(texture * 4 + component),
        _ => BitConverter.SingleToUInt32Bits(texture * 4 + component + 0.25f),
    };

    // Entry e names texture (e * 37) % 300, except every seventh, which is null.
    private static int TextureOfEntry(int entry, int generation) =>
        entry % 7 == 3 ? -1 : (entry * 37 + generation * 11) % Textures;

    private sealed class Guest
    {
        public readonly byte[] Memory = new byte[0x40000];

        public Guest(int generation)
        {
            Write(ResourceBlock + 0x10, [(uint)TableAddress, 0, Entries * 32, DescriptorConstants.IdentityDestinationSelect]);
            Write(ResourceBlock + 0x20, [0x30000, 0, KeyOffset + Lanes * 4, 0x00038FAC]);
            Write(ResourceBlock + 0x30, [0, 0, 0, 0]);
            for (var entry = 0; entry < Entries; entry++)
            {
                var texture = TextureOfEntry(entry, generation);
                Write(TableAddress + (ulong)entry * 32, texture < 0 ? new uint[8] : Descriptor(texture));
            }
        }

        public void Write(ulong address, uint[] words)
        {
            for (var index = 0; index < words.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(Memory.AsSpan((int)address + index * 4), words[index]);
        }

        public bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address + 4 > (ulong)Memory.Length) return false;
            word = BinaryPrimitives.ReadUInt32LittleEndian(Memory.AsSpan((int)address));
            return true;
        }
    }

    private static (ShaderResourcePlan Plan, ResourceSnapshot Snapshot, ShaderCompileRequest Request) Materialize(Guest guest)
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture("bindless-waterfall", Words, 0, 0));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, UserDataCount);
        var userData = new uint[UserDataCount];
        userData[0] = (uint)ResourceBlock;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var inputs = new ResourceRuntimeInputs { UserData = userData, ReadMemory = guest.Read, ReadCleanMemory = guest.Read };
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out var failure),
            $"materialize: {failure}");
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, UserDataCount),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { WaveSize = 32, LocalSizeX = Lanes };
        return (plan, snapshot, request);
    }

    [Fact]
    public void ATableOfManyTexturesSortsIntoTheClassesItsAccessReads()
    {
        var (_, snapshot, request) = Materialize(new Guest(0));
        var table = Assert.Single(snapshot.BindlessImages);
        Assert.Equal(Entries, table.Entries.Length);
        // The 3D and sint textures are null entries; the rest are three variants, the most
        // used (float 2D) first, so a 2D-array access reads 2D and 2D-array views and a uint one.
        Assert.Equal(280, table.Descriptors.Length);
        Assert.Equal(3, table.VariantImages.Length);
        var info = request.Resources.Info;
        var variants = table.VariantImages.Select(image => info.Images[(int)image]).ToArray();
        Assert.Equal((ImageNumericClass.Float, ImageDimension.Dim2D), (variants[0].NumericClass, variants[0].Dimension));
        Assert.Contains(variants, image => image.NumericClass == ImageNumericClass.Float && image.Dimension == ImageDimension.Dim2DArray);
        Assert.Contains(variants, image => image.NumericClass == ImageNumericClass.Uint && image.Dimension == ImageDimension.Dim2D);
        for (var entry = 0; entry < Entries; entry++)
        {
            var texture = TextureOfEntry(entry, 0);
            var readable = texture >= 0 && KindOf(texture) is Kind.Float2D or Kind.Float2DArray or Kind.Uint2D;
            Assert.Equal(readable, table.Entries[entry] >= 0);
            if (readable)
                Assert.Equal(0x100u + (uint)texture, table.Descriptors[table.Entries[entry]][0]);
        }

        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    [Fact]
    public void EveryLaneSamplesTheTextureItsEntryNamesAndZeroWhereNone()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Bindless execution requires the exclusive Metal device gate.");
            return;
        }

        Assert.True(MetalNative.IsAvailable);
        var textures = new nint[Textures];
        for (var texture = 0; texture < Textures; texture++)
        {
            var texel = Enumerable.Range(0, 4).Select(component => Texel(texture, component)).ToArray();
            textures[texture] = KindOf(texture) switch
            {
                Kind.Uint2D => MetalNative.CreateTexelTexture(MetalNative.PixelFormatRgba32Uint, MetalNative.TextureType2D, 1, texel),
                // Layer 0 holds something else, so reading the wrong layer shows.
                Kind.Float2DArray => MetalNative.CreateTexelTexture(MetalNative.PixelFormatRgba32Float, MetalNative.TextureType2DArray, 2,
                    [.. Enumerable.Repeat(BitConverter.SingleToUInt32Bits(-7f), 4), .. texel]),
                _ => MetalNative.CreateTexelTexture(MetalNative.PixelFormatRgba32Float, MetalNative.TextureType2D, 1, texel),
            };
        }

        var nullFloat2D = MetalNative.CreateTexelTexture(MetalNative.PixelFormatRgba32Float, MetalNative.TextureType2D, 1, new uint[4]);
        var nullFloat2DArray = MetalNative.CreateTexelTexture(MetalNative.PixelFormatRgba32Float, MetalNative.TextureType2DArray, 1, new uint[4]);
        var nullUint2D = MetalNative.CreateTexelTexture(MetalNative.PixelFormatRgba32Uint, MetalNative.TextureType2D, 1, new uint[4]);

        for (var generation = 0; generation < 2; generation++)
        {
            // The second generation rewrites the guest table, as a title streaming textures does.
            var (_, snapshot, request) = Materialize(new Guest(generation));
            Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
            Assert.True(MetalNative.TryCompileLibrary(shader!.Source, out var library, out error), error);
            var table = Assert.Single(snapshot.BindlessImages);
            var entries = new byte[(table.Entries.Length + 1) * 16];
            var nullId = MetalNative.ResourceId(nullFloat2D);
            for (var entry = 0; entry <= table.Entries.Length; entry++)
            {
                var descriptor = entry < table.Entries.Length ? table.Entries[entry] : -1;
                var span = entries.AsSpan(entry * 16, 16);
                if (descriptor < 0)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(span, nullId);
                    continue;
                }

                var texture = (int)(table.Descriptors[descriptor][0] - 0x100);
                BinaryPrimitives.WriteUInt64LittleEndian(span, MetalNative.ResourceId(textures[texture]));
                BinaryPrimitives.WriteUInt32LittleEndian(span[8..], table.DescriptorVariants[descriptor]);
            }

            var bindless = MetalNative.CreateBindlessTable(entries, textures.Append(nullFloat2D).ToArray());
            var flattened = MetalNative.CreateWordTable(DescriptorBindingKind.FlattenedResourceTable, snapshot.FlattenedResourceTable);
            var layout = request.Bindings;
            var shaderData = new uint[Math.Max(layout.ShaderDataDwordCount, 1)];
            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
                shaderData[index] = snapshot.UserData[(int)layout.UserDataRegisters[index]];
            var pushData = new byte[PushData.ByteSize];
            if (layout.UsesPushData)
            {
                for (var index = 0; index < layout.ShaderDataDwordCount; index++)
                    BinaryPrimitives.WriteUInt32LittleEndian(pushData.AsSpan((int)(layout.PushDataStartDword + index) * 4), shaderData[index]);
            }

            var tables = new List<DeviceTable> { flattened, MetalNative.CreateWordTable(DescriptorBindingKind.ShaderData, shaderData) };
            // Four dispatches of a thousand lanes cover every entry and keys past the table.
            for (var pass = 0; pass < 5; pass++)
            {
                var keys = Enumerable.Range(0, Lanes)
                    .Select(lane => pass < 4 ? (pass * Lanes + lane) * 1237 % Entries : Entries + lane * 13)
                    .ToArray();
                var data = new byte[KeyOffset + Lanes * 4];
                for (var lane = 0; lane < Lanes; lane++)
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(KeyOffset + lane * 4), (uint)keys[lane] << 4);
                Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, data, pushData,
                    shader.ArgumentLayout!, Lanes, out var result, out _, out error,
                    tables: tables, bindlessTables: [bindless],
                    textureOfClass: kind => ImageDescriptorBinding.Describe(kind) switch
                    {
                        (_, ImageNumericClass.Uint, _, _) => nullUint2D,
                        (_, _, ImageDimension.Dim2DArray, _) => nullFloat2DArray,
                        _ => nullFloat2D,
                    }), error);
                for (var lane = 0; lane < Lanes; lane++)
                {
                    var entry = keys[lane];
                    var texture = entry < Entries ? TextureOfEntry(entry, generation) : -1;
                    var readable = texture >= 0 && KindOf(texture) is Kind.Float2D or Kind.Float2DArray or Kind.Uint2D;
                    for (var component = 0; component < 4; component++)
                    {
                        var actual = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(lane * 16 + component * 4));
                        var expected = readable ? Texel(texture, component) : 0u;
                        Assert.True(expected == actual,
                            $"generation {generation} pass {pass} lane {lane} entry {entry} texture {texture} component {component}: " +
                            $"expected 0x{expected:X8}, read 0x{actual:X8}");
                    }
                }
            }

        }
    }
}
