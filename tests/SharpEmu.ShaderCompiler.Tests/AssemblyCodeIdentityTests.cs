// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class AssemblyCodeIdentityTests
{
    private static byte[] TranslatorImage() => File.ReadAllBytes(typeof(Gen5ShaderTranslator).Assembly.Location);

    private static string Identity(byte[] image) => AssemblyCodeIdentity.Compute(new MemoryStream(image));

    [Fact]
    public void TheIdentityOfALoadedAssemblyIsThatOfItsImage()
    {
        Assert.Equal(
            Identity(TranslatorImage()),
            AssemblyCodeIdentity.Of(typeof(Gen5ShaderTranslator).Assembly));
    }

    [Fact]
    public void ANewModuleVersionIdKeepsTheIdentity()
    {
        var image = TranslatorImage();
        var original = Identity(image);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var reader = pe.GetMetadataReader();
            var guids = pe.PEHeaders.MetadataStartOffset + reader.GetHeapMetadataOffset(HeapIndex.Guid);
            var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToByteArray();
            Assert.Equal(mvid, image.AsSpan(guids, 16).ToArray());
            Guid.NewGuid().ToByteArray().CopyTo(image, guids);
        }

        Assert.Equal(original, Identity(image));
    }

    [Fact]
    public void NewVersionAttributesKeepTheIdentity()
    {
        var image = TranslatorImage();
        var original = Identity(image);
        var patched = 0;
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var reader = pe.GetMetadataReader();
            var blobs = pe.PEHeaders.MetadataStartOffset + reader.GetHeapMetadataOffset(HeapIndex.Blob);
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                // The informational version ends in "+<commit>" and changes with every
                // commit; any value of an assembly attribute may.
                var value = reader.GetCustomAttribute(handle).Value;
                var length = reader.GetBlobReader(value).Length;
                var prefix = length < 0x80 ? 1 : length < 0x4000 ? 2 : 4;
                if (length > 2)
                {
                    image[blobs + MetadataTokens.GetHeapOffset(value) + prefix + length - 1] ^= 0x01;
                    patched++;
                }
            }
        }

        Assert.True(patched > 0);
        Assert.Equal(original, Identity(image));
    }

    [Fact]
    public void AChangedMethodBodyChangesTheIdentity()
    {
        var image = TranslatorImage();
        var original = Identity(image);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var reader = pe.GetMetadataReader();
            var rva = reader.MethodDefinitions
                .Select(handle => reader.GetMethodDefinition(handle).RelativeVirtualAddress)
                .First(value => value != 0 && pe.GetMethodBody(value).Size > 8);
            var section = pe.PEHeaders.SectionHeaders.First(header =>
                rva >= header.VirtualAddress && rva < header.VirtualAddress + header.VirtualSize);
            var offset = rva - section.VirtualAddress + section.PointerToRawData;
            // The last byte of the body: IL, past the header.
            image[offset + pe.GetMethodBody(rva).Size - 1] ^= 0x5A;
        }

        Assert.NotEqual(original, Identity(image));
    }
}
