// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Builds one-instruction MIMG programs over a synthetic texture descriptor, so a single
/// image opcode can be translated for any guest image type. This project is deliberately
/// self-contained, so the request plumbing is copied from the shared resource helpers
/// rather than referenced.
/// </summary>
internal static class Gen5ImageFixtures
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const ulong RequestHash = 0x5348_4152_5045_4D55;
    private const uint UserDataCount = 20;
    private const uint SEndpgm = 0xBF810000;

    /// <summary>The Gen5 texture descriptor type field, as decoded by GuestImageFormat.</summary>
    public const uint ImageType1D = 8;
    public const uint ImageType2D = 9;
    public const uint ImageType3D = 10;
    public const uint ImageTypeCube = 11;
    public const uint ImageType1DArray = 12;
    public const uint ImageType2DArray = 13;
    public const uint ImageType2DMsaa = 14;
    public const uint ImageType2DMsaaArray = 15;

    /// <summary>The MIMG DIM field: the instruction's own view of the address layout.</summary>
    public const uint Dim1D = 0;
    public const uint Dim2D = 1;
    public const uint Dim3D = 2;
    public const uint DimCube = 3;
    public const uint Dim1DArray = 4;
    public const uint Dim2DArray = 5;
    public const uint Dim2DMsaa = 6;
    public const uint Dim2DMsaaArray = 7;

    /// <summary>A human-readable name for a descriptor type, for golden and test ids.</summary>
    public static string TypeName(uint descriptorType) => descriptorType switch
    {
        ImageType1D => "1d",
        ImageType2D => "2d",
        ImageType3D => "3d",
        ImageTypeCube => "cube",
        ImageType1DArray => "1d-array",
        ImageType2DArray => "2d-array",
        ImageType2DMsaa => "2d-ms",
        ImageType2DMsaaArray => "2d-ms-array",
        _ => $"type{descriptorType}",
    };

    /// <summary>
    /// Translates one image opcode against a descriptor of the given guest type.
    /// <paramref name="unifiedFormat"/> 71 is a four-channel float format; 22 is 8_8_8_8 unorm.
    /// </summary>
    public static bool TryCompile(
        string opcode,
        uint descriptorType,
        out Gen5MslShader? shader,
        out string error,
        uint? instructionDimension = null,
        bool? arrayed = null,
        uint dmask = 0xF,
        uint dstSelect = Gen5ShaderTranslator.IdentityImageDstSelect,
        uint unifiedFormat = 71u,
        bool a16 = false,
        bool d16 = false,
        ShaderStage stage = ShaderStage.Compute)
    {
        var isArray = arrayed ?? descriptorType is ImageType1DArray or ImageType2DArray or ImageTypeCube or ImageType2DMsaaArray;
        var dimension = instructionDimension ?? descriptorType switch
        {
            ImageType1D => Dim1D,
            ImageType3D => Dim3D,
            ImageTypeCube => DimCube,
            ImageType1DArray => Dim1DArray,
            ImageType2DArray => Dim2DArray,
            ImageType2DMsaa => Dim2DMsaa,
            ImageType2DMsaaArray => Dim2DMsaaArray,
            _ => Dim2D,
        };

        // The address registers must cover the coordinates plus every operand the
        // opcode carries, so the widest case is allocated and the translator takes
        // only the components its dimension needs.
        var addressRegisters = Enumerable.Range(0, 12).Select(static value => (uint)value).ToArray();
        var control = new Gen5ImageControl(
            Dmask: dmask,
            VectorAddress: 0,
            AddressRegisters: addressRegisters,
            VectorData: 16,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: dimension,
            IsArray: isArray,
            Glc: false,
            Slc: false,
            A16: a16,
            D16: d16);
        var imageInstruction = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mimg, opcode, [], [], [], control);
        var end = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [SEndpgm], [], [], null);
        var program = new Gen5ShaderProgram(ShaderAddress, [imageInstruction, end]);

        var plan = ShaderResourcePlan.Extract(program, stage, RequestHash, 0, UserDataCount);
        var userData = new uint[UserDataCount];
        WriteDescriptor(userData, descriptorType, unifiedFormat, dstSelect);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(
            ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData }, ref snapshot, ref specialization, out var failure),
            $"descriptor materialization failed: {failure}");

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, UserDataCount),
            false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : [],
        };

        var compiled = Gen5MslTranslator.TryCompileProgram(request, out var result, out error);
        shader = compiled ? result : null;
        return compiled;
    }

    /// <summary>
    /// Writes an eight-dword image T# into the user-data registers the fixture's MIMG
    /// instruction points at (s[8:15]). Array and multisampled types carry extra fields
    /// that the descriptor validator insists on, so they are filled in here: an arrayed
    /// descriptor needs a slice range, and a multisampled one needs a fragment count
    /// that its max-mip field agrees with.
    /// </summary>
    private static void WriteDescriptor(uint[] userData, uint descriptorType, uint unifiedFormat, uint dstSelect)
    {
        userData[8] = 0x20;
        userData[9] = unifiedFormat << 20;
        userData[11] = (descriptorType << 28) | dstSelect;
        if (descriptorType is ImageTypeCube or ImageType1DArray or ImageType2DArray or ImageType2DMsaaArray)
        {
            // Base slice 0 through last slice 5: one cube's six faces, or a six-layer array.
            userData[12] = 5u;
        }

        if (descriptorType is ImageType2DMsaa or ImageType2DMsaaArray)
        {
            // Four samples per texel: log2(4) = 2 fragments, which the max-mip must match.
            userData[11] |= 2u << 16;
            userData[13] = 2u << 4;
        }
    }

    /// <summary>TEMPORARY diagnostic: reports the resolved image shape.</summary>
    public static string DescribeImage(string opcode, uint descriptorType)
    {
        var isArray = descriptorType is ImageType1DArray or ImageType2DArray or ImageTypeCube or ImageType2DMsaaArray;
        var dimension = descriptorType switch
        {
            ImageType1D => Dim1D,
            ImageType3D => Dim3D,
            ImageTypeCube => DimCube,
            ImageType1DArray => Dim1DArray,
            ImageType2DArray => Dim2DArray,
            ImageType2DMsaa => Dim2DMsaa,
            ImageType2DMsaaArray => Dim2DMsaaArray,
            _ => Dim2D,
        };
        var addressRegisters = Enumerable.Range(0, 12).Select(static value => (uint)value).ToArray();
        var control = new Gen5ImageControl(0xF, 0, addressRegisters, 16, 8, 16, dimension, isArray, false, false, false, false);
        var program = new Gen5ShaderProgram(ShaderAddress, [
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mimg, opcode, [], [], [], control),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [SEndpgm], [], [], null)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, RequestHash, 0, UserDataCount);
        var planned = plan.Info.Images[0];
        var userData = new uint[UserDataCount];
        WriteDescriptor(userData, descriptorType, 71u, Gen5ShaderTranslator.IdentityImageDstSelect);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var materialized = ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData }, ref snapshot, ref specialization, out var failure);
        if (!materialized)
        {
            return $"materialize failed: {failure}";
        }

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var image = resources.Info.Images[0];
        return $"planned={planned.Dimension} resolved={image.Dimension} cube={image.Cube} class={image.ResourceClass} numeric={image.NumericClass} binding={ImageDescriptorBinding.ForImage(image)}";
    }

    /// <summary>Translates one image opcode, failing the test if the translator refuses.</summary>
    public static Gen5MslShader CompileOrThrow(
        string opcode,
        uint descriptorType,
        uint? instructionDimension = null,
        bool? arrayed = null,
        uint dmask = 0xF,
        uint unifiedFormat = 71u,
        bool a16 = false,
        bool d16 = false,
        ShaderStage stage = ShaderStage.Compute)
    {
        Assert.True(
            TryCompile(opcode, descriptorType, out var shader, out var error,
                instructionDimension, arrayed, dmask, Gen5ShaderTranslator.IdentityImageDstSelect,
                unifiedFormat, a16, d16, stage),
            $"[{opcode} on {TypeName(descriptorType)}] MSL emit failed: {error}");
        return shader!;
    }
}
