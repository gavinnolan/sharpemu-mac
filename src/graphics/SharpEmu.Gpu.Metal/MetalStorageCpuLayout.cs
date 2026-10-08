// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;

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
}
