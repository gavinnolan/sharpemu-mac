// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using System.Buffers.Binary;

namespace SharpEmu.Gpu.Metal;

// Immutable creation layout, independent of opt-in diagnostic metadata.
// This describes byte placement only. It grants no mapping/content authority.
internal sealed class MetalStorageCpuLayout
{
    private readonly GuestDrawTexture _template;
    private readonly uint[] _raw;
    internal ulong Address => _template.Address;
    internal uint Width => _template.Width;
    internal uint Height => _template.Height;
    internal uint Pitch => _template.Pitch;
    internal ulong PhysicalBytes { get; }
    internal int LinearBytes { get; }
    internal MetalTextureFormat NativeFormat { get; }

    private MetalStorageCpuLayout(GuestDrawTexture texture, MetalTextureFormat format)
    {
        _raw = texture.Descriptor!.ToArray();
        _template = texture with { Descriptor = null, RgbaPixels = [], Sampler = default };
        NativeFormat = format;
        PhysicalBytes = texture.SourceByteCount;
        LinearBytes = checked((int)((ulong)texture.Width * texture.Height * format.BytesPerPixel));
    }

    internal static MetalStorageCpuLayout? TryCreate(GuestDrawTexture texture)
    {
        if (!MetalSingletonArrayStorageAdmission.IsQualified(texture) &&
            !MetalBgra1024ArrayStorageAdmission.IsQualified(texture) &&
            !MetalR8FourKArrayStorageAdmission.IsQualified(texture)) return null;
        var format = MetalVideoPresenter.StorageTextureFormat(texture);
        var physical = MetalR8FourKArrayStorageAdmission.IsQualified(texture) ? 8847360UL :
            MetalBgra1024ArrayStorageAdmission.IsQualified(texture) ? 4194304UL : 65536UL;
        if (texture.SourceByteCount != physical || texture.Address > ulong.MaxValue - physical ||
            !texture.CpuSnapshotStable) return null;
        return new(texture, format);
    }

    internal bool Matches(GuestDrawTexture texture) =>
        texture.Descriptor is { Length: 8 } raw && raw.AsSpan().SequenceEqual(_raw) &&
        (texture with { Descriptor = null, RgbaPixels = [], Sampler = default,
            WriteGeneration = _template.WriteGeneration, CpuSnapshotStable = _template.CpuSnapshotStable }) == _template;

    internal byte[] Decode(ReadOnlySpan<byte> physical)
    {
        if ((ulong)physical.Length != PhysicalBytes)
            throw new InvalidOperationException("Typed CPU image copy does not contain its exact physical footprint.");
        var linear = new byte[LinearBytes];
        if (!GnmTiling.TryDetile(physical, linear, _template.TileMode,
                checked((int)Pitch), checked((int)Height), checked((int)NativeFormat.BytesPerPixel)))
            throw new NotSupportedException("Typed CPU image copy failed its qualified physical layout; raw/display fallback is forbidden.");
        return linear;
    }

    internal GuestDrawTexture Upload(byte[] ownedLinear)
    {
        if (ownedLinear.Length != LinearBytes)
            throw new InvalidOperationException("Typed CPU image upload requires every native texel.");
        return _template with { Descriptor = _raw.ToArray(), RgbaPixels = ownedLinear, CpuSnapshotStable = true };
    }

    // Ordinary finite writeback only; initialized on first Encode, never from a
    // fault route. Invert the already-qualified production detiler, rather than
    // importing a second address equation or old CPU seed/padding bytes.
    private static readonly Lazy<int[]> _rgba1024PhysicalOffsets = new(CreateRgba1024PhysicalOffsets);

    internal byte[] Encode(ReadOnlySpan<byte> nativeLinear)
    {
        if (!MetalBgra1024ArrayStorageAdmission.IsQualified(_template with { Descriptor = _raw }) ||
            NativeFormat.Format != MtlPixelFormat.Rgba8Unorm || NativeFormat.BytesPerPixel != 4 ||
            PhysicalBytes != 4194304 || LinearBytes != 4194304 || nativeLinear.Length != LinearBytes)
            throw new NotSupportedException("Retiling publication requires the exact complete RGBA8 1024-square native layout.");
        var offsets = _rgba1024PhysicalOffsets.Value;
        var physical = new byte[LinearBytes];
        for (var pixel = 0; pixel < offsets.Length; pixel++)
            nativeLinear.Slice(pixel * 4, 4).CopyTo(physical.AsSpan(offsets[pixel], 4));
        return physical;
    }

    private static int[] CreateRgba1024PhysicalOffsets()
    {
        const int bytes = 4194304;
        var tagged = new byte[bytes];
        for (var offset = 0; offset < bytes; offset += 4)
            BinaryPrimitives.WriteInt32LittleEndian(tagged.AsSpan(offset, 4), offset);
        var linear = new byte[bytes];
        if (!GnmTiling.TryDetile(tagged, linear, 27, 1024, 1024, 4))
            throw new InvalidOperationException("Qualified RGBA8 publication inverse layout is unavailable.");
        var offsets = new int[bytes / 4];
        var seen = new uint[offsets.Length / 32];
        for (var pixel = 0; pixel < offsets.Length; pixel++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(linear.AsSpan(pixel * 4, 4));
            if (offset < 0 || offset > bytes - 4 || (offset & 3) != 0)
                throw new InvalidOperationException("Qualified RGBA8 publication inverse layout exceeds its physical footprint.");
            var index = offset / 4;
            var bit = 1u << (index & 31);
            if ((seen[index / 32] & bit) != 0)
                throw new InvalidOperationException("Qualified RGBA8 publication inverse layout aliases physical texels.");
            seen[index / 32] |= bit;
            offsets[pixel] = offset;
        }
        return offsets;
    }
}
