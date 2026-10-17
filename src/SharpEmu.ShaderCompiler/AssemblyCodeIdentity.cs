// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// An identity of an assembly's code that changes when its code does and not otherwise,
/// for keying what a translator build produces (prewarm lists, learned loads, analysis
/// graphs). The module version id is no such identity: the build stamps every assembly
/// with the commit it was built from (the informational version's "+sha", and the PDB
/// the debug directory names), so every commit gave every assembly a new one, and every
/// start after any commit threw away the shader lists and learned loads of the build
/// before it, compiling Demon's Souls' libraries cold again. This hashes the metadata
/// with the module id and the assembly-level attribute values blanked, every method
/// body and every field's initial data; type, member and string-literal changes all
/// reach the metadata, and code changes reach a body.
/// </summary>
public static class AssemblyCodeIdentity
{
    private static readonly ConcurrentDictionary<Assembly, string> Identities = new();

    /// <summary>The code identity of <paramref name="assembly"/>, hex; computed once per process.</summary>
    public static string Of(Assembly assembly) => Identities.GetOrAdd(assembly, Compute);

    private static string Compute(Assembly assembly)
    {
        try
        {
            if (!string.IsNullOrEmpty(assembly.Location))
            {
                using var stream = File.OpenRead(assembly.Location);
                return Compute(stream);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
        }

        // No readable image (single-file or in-memory): fall back to the build's own id,
        // which is never stale, only too fresh.
        return "mvid-" + assembly.ManifestModule.ModuleVersionId.ToString("N");
    }

    /// <summary>The code identity of the PE image in <paramref name="image"/>.</summary>
    public static string Compute(Stream image)
    {
        using var pe = new PEReader(image);
        var reader = pe.GetMetadataReader();
        var bytes = pe.GetMetadata().GetContent().ToArray();

        // The module id (and any edit-and-continue ids) live in the #GUID heap.
        var guids = reader.GetHeapMetadataOffset(HeapIndex.Guid);
        Array.Clear(bytes, guids, reader.GetHeapSize(HeapIndex.Guid));

        // Version, commit and build attributes on the assembly and the module: each value
        // blob with its length prefix.
        var blobs = reader.GetHeapMetadataOffset(HeapIndex.Blob);
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes()
                     .Concat(reader.GetModuleDefinition().GetCustomAttributes()))
        {
            var value = reader.GetCustomAttribute(handle).Value;
            var length = reader.GetBlobReader(value).Length;
            var prefix = length < 0x80 ? 1 : length < 0x4000 ? 2 : 4;
            Array.Clear(bytes, blobs + MetadataTokens.GetHeapOffset(value), prefix + length);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes);
        Span<byte> number = stackalloc byte[sizeof(int)];
        foreach (var handle in reader.MethodDefinitions)
        {
            var rva = reader.GetMethodDefinition(handle).RelativeVirtualAddress;
            if (rva == 0)
            {
                continue;
            }

            // The header, the IL and the exception regions.
            var section = pe.GetSectionData(rva);
            var size = Math.Min(pe.GetMethodBody(rva).Size, section.Length);
            hash.AppendData(section.GetContent(0, size).AsSpan());
        }

        foreach (var handle in reader.FieldDefinitions)
        {
            var field = reader.GetFieldDefinition(handle);
            var rva = field.GetRelativeVirtualAddress();
            if (rva == 0)
            {
                continue;
            }

            var section = pe.GetSectionData(rva);
            var size = Math.Min(FieldDataSize(reader, field), section.Length);
            BitConverter.TryWriteBytes(number, size);
            hash.AppendData(number);
            hash.AppendData(section.GetContent(0, size).AsSpan());
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // The bytes a field's initial data spans: a primitive's size, or the layout size of the
    // value type the compiler made for an array initializer.
    private static int FieldDataSize(MetadataReader reader, FieldDefinition field)
    {
        var signature = reader.GetBlobReader(field.Signature);
        signature.ReadSignatureHeader();
        var code = signature.ReadByte();
        switch ((SignatureTypeCode)code)
        {
            case SignatureTypeCode.Boolean or SignatureTypeCode.SByte or SignatureTypeCode.Byte:
                return 1;
            case SignatureTypeCode.Char or SignatureTypeCode.Int16 or SignatureTypeCode.UInt16:
                return 2;
            case SignatureTypeCode.Int32 or SignatureTypeCode.UInt32 or SignatureTypeCode.Single:
                return 4;
            case SignatureTypeCode.Int64 or SignatureTypeCode.UInt64 or SignatureTypeCode.Double:
                return 8;
            case (SignatureTypeCode)0x11: // ELEMENT_TYPE_VALUETYPE
                var type = signature.ReadTypeHandle();
                return type.Kind == HandleKind.TypeDefinition
                    ? reader.GetTypeDefinition((TypeDefinitionHandle)type).GetLayout().Size
                    : 0;
            default:
                return 0;
        }
    }
}
