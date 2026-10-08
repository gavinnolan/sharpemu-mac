// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal;

// What one argument-buffer field holds: an array of buffer pointers or lengths, textures,
// samplers, or one pointer with its length.
public enum MslArgumentFieldKind
{
    BufferPointers,
    BufferByteCounts,
    Textures,
    Samplers,
    Pointer,
    Word,
}

// One field of the stage argument buffer: its binding kind, id range and byte range.
public sealed record MslArgumentField(
    DescriptorBindingKind Kind,
    MslArgumentFieldKind FieldKind,
    string Name,
    uint Count,
    uint FirstId,
    uint ByteOffset,
    uint ByteSize);

// The argument buffer of one stage mirrors the binding layout in descriptor order. Pointers,
// textures and samplers take eight bytes each, counts take four; the host fills it directly.
public sealed class Gen5MslArgumentLayout
{
    public const int ResourcesBufferIndex = 0;
    public const int PushDataBufferIndex = 1;
    public const string StructName = "Gen5StageResources";
    private const uint ResourceBytes = 8;
    private const uint WordBytes = 4;

    private Gen5MslArgumentLayout(IReadOnlyList<MslArgumentField> fields, uint byteSize)
    {
        Fields = fields;
        ByteSize = byteSize;
    }

    public IReadOnlyList<MslArgumentField> Fields { get; }

    public uint ByteSize { get; }

    public MslArgumentField? Find(DescriptorBindingKind kind, MslArgumentFieldKind fieldKind) =>
        Fields.FirstOrDefault(field => field.Kind == kind && field.FieldKind == fieldKind);

    public static Gen5MslArgumentLayout Build(BindingLayout layout)
    {
        var fields = new List<MslArgumentField>();
        uint id = 0;
        uint offset = 0;
        foreach (var binding in layout.Descriptors)
        {
            var count = (uint)binding.Resources.Count;
            switch (binding.Kind)
            {
                case DescriptorBindingKind.Buffers:
                    Add(binding.Kind, MslArgumentFieldKind.BufferPointers, "buffers", count, ResourceBytes);
                    Add(binding.Kind, MslArgumentFieldKind.BufferByteCounts, "buffer_bytes", count, WordBytes);
                    break;
                case DescriptorBindingKind.Samplers:
                    Add(binding.Kind, MslArgumentFieldKind.Samplers, "samplers", count, ResourceBytes);
                    break;
                case DescriptorBindingKind.GlobalDataShare:
                    Add(binding.Kind, MslArgumentFieldKind.Pointer, "global_data_share", 1, ResourceBytes);
                    Add(binding.Kind, MslArgumentFieldKind.Word, "global_data_share_bytes", 1, WordBytes);
                    break;
                case DescriptorBindingKind.DeviceAddressPageTable:
                    Add(binding.Kind, MslArgumentFieldKind.Pointer, "address_ranges", 1, ResourceBytes);
                    Add(binding.Kind, MslArgumentFieldKind.Word, "address_range_count", 1, WordBytes);
                    break;
                case DescriptorBindingKind.FaultBuffer:
                    Add(binding.Kind, MslArgumentFieldKind.Pointer, "fault_bits", 1, ResourceBytes);
                    Add(binding.Kind, MslArgumentFieldKind.Word, "fault_word_count", 1, WordBytes);
                    break;
                case DescriptorBindingKind.FlattenedResourceTable:
                    Add(binding.Kind, MslArgumentFieldKind.Pointer, "flattened_table", 1, ResourceBytes);
                    Add(binding.Kind, MslArgumentFieldKind.Word, "flattened_table_words", 1, WordBytes);
                    break;
                case DescriptorBindingKind.ShaderData:
                    Add(binding.Kind, MslArgumentFieldKind.Pointer, "shader_data", 1, ResourceBytes);
                    Add(binding.Kind, MslArgumentFieldKind.Word, "shader_data_words", 1, WordBytes);
                    break;
                default:
                    Add(binding.Kind, MslArgumentFieldKind.Textures, ImageClassName(binding.Kind), count, ResourceBytes);
                    break;
            }
        }

        return new Gen5MslArgumentLayout(fields, Align(offset, ResourceBytes));

        void Add(DescriptorBindingKind kind, MslArgumentFieldKind fieldKind, string name, uint count, uint elementBytes)
        {
            offset = Align(offset, elementBytes);
            var byteSize = count * elementBytes;
            fields.Add(new MslArgumentField(kind, fieldKind, name, count, id, offset, byteSize));
            id += count;
            offset += byteSize;
        }
    }

    private static uint Align(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    // The field name of an image class: its resource class, numeric class and dimension.
    public static string ImageClassName(DescriptorBindingKind kind)
    {
        var (resourceClass, numericClass, dimension, atomic) = ImageDescriptorBinding.Describe(kind);
        if (resourceClass == ImageResourceClass.None)
        {
            throw new InvalidOperationException($"binding kind {kind} is not an image class");
        }

        var classPart = atomic ? "atomic" : resourceClass == ImageResourceClass.Storage ? "storage" :
            ImageDescriptorBinding.IsDepthCompare(kind) ? "sampled_compare" : "sampled";
        var numericPart = numericClass switch
        {
            ImageNumericClass.Uint => "uint",
            ImageNumericClass.Sint => "sint",
            _ => "float",
        };
        var dimensionPart = ImageDescriptorBinding.IsCube(kind) ? "cube" : DimensionName(dimension);
        return $"{classPart}_{numericPart}_{dimensionPart}";
    }

    public static string DimensionName(ImageDimension dimension) => dimension switch
    {
        ImageDimension.Dim1D => "1d",
        ImageDimension.Dim1DArray => "1d_array",
        ImageDimension.Dim2DArray => "2d_array",
        ImageDimension.Dim2DMsaa => "2d_ms",
        ImageDimension.Dim2DMsaaArray => "2d_ms_array",
        ImageDimension.Dim3D => "3d",
        _ => "2d",
    };

    // The Metal texture type of an image class with the given component and access.
    //
    // Guest cube maps are not declared as texturecube. Gen5 shaders resolve the cube
    // face themselves (V_CUBEID/V_CUBESC/V_CUBETC/V_CUBEMA) and then address the image
    // as a 2D array with the face in the layer coordinate, so ImageDescriptorBinding
    // describes every cube binding as Dim2DArray and the host binds a 2D-array view.
    // A texturecube declaration would instead demand a float3 direction vector. This
    // mirrors the SPIR-V backend, which likewise declares cube bindings Dim2D+Arrayed.
    public static string TextureType(ImageDimension dimension, string componentType, string? access, bool depthCompare = false)
    {
        if (depthCompare)
        {
            if (componentType != "float" || access is not null ||
                dimension is not (ImageDimension.Dim2D or ImageDimension.Dim2DArray))
                throw new NotSupportedException($"Metal depth comparison does not support {dimension}/{componentType}/{access}.");
            return dimension == ImageDimension.Dim2D ? "depth2d<float>" : "depth2d_array<float>";
        }
        var typeName = dimension switch
        {
            ImageDimension.Dim1D => "texture2d",
            ImageDimension.Dim1DArray => "texture2d_array",
            ImageDimension.Dim2DArray => "texture2d_array",
            ImageDimension.Dim2DMsaa => "texture2d_ms",
            ImageDimension.Dim2DMsaaArray => "texture2d_ms_array",
            ImageDimension.Dim3D => "texture3d",
            _ => "texture2d",
        };
        return access is null ? $"{typeName}<{componentType}>" : $"{typeName}<{componentType}, access::{access}>";
    }
}
