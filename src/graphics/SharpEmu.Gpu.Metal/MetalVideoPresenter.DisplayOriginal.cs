// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Display placement is separate from typed storage layout/publication. This
    // original full copy can prove physical disjointness, never GPU completion,
    // permissions, dirty acknowledgment or authority to publish display bytes.
    private sealed record DisplayOriginalCopy(ICpuMemory Memory, ICpuMappedCopySource Source,
        CpuMappedCopyObservation Mapping, CpuTextureSnapshotObservation Content,
        GuestImageWriteTracker.ReadSnapshot Read, long Session, ulong Address,
        uint Width, uint Height, uint Pitch, uint BytesPerPixel, MtlPixelFormat Format);
    private sealed record DisplayOriginalOwner(DisplayOriginalCopy Copy, nint Texture, int InitialVersion);
    private static readonly ConditionalWeakTable<GuestImage, DisplayOriginalOwner> _displayOriginalOwners = new();
    private static DisplayOriginalCopy?[]? _finiteRetiredDisplayOrigins;
    private static int _finiteRetiredDisplayOriginCount;

    private static bool ReadDisplayOriginalSource(ICpuMemory memory, ulong address, uint width,
        uint height, uint pitch, uint bytesPerPixel, MtlPixelFormat format, long originalSession,
        byte[] bytes, int byteCount, out DisplayOriginalCopy? original)
    {
        original = null;
        if (!_rgbaStorageRuntimePublicationRequested)
            return memory.TryRead(address, bytes.AsSpan(0, byteCount));
        return ReadDisplayOriginalSourceCore(memory, address, width, height, pitch, bytesPerPixel,
            format, originalSession, bytes, byteCount, out original);
    }

    private static bool ReadDisplayOriginalSourceCore(ICpuMemory memory, ulong address, uint width,
        uint height, uint pitch, uint bytesPerPixel, MtlPixelFormat format, long originalSession,
        byte[] bytes, int byteCount, out DisplayOriginalCopy? original)
    {
        original = null;
        if (memory is not ICpuMappedCopySource source ||
            byteCount <= 0 || byteCount > 128 * 1024 * 1024 || address == 0 ||
            address > ulong.MaxValue - (ulong)byteCount)
            return memory.TryRead(address, bytes.AsSpan(0, byteCount));
        var read = GuestImageWriteTracker.BeginReadSnapshot(address, (ulong)byteCount,
            source: "metal.display.original-copy");
        var observation = ObserveCpuTextureSnapshot(address, (ulong)byteCount, HostMemory.MappingGeneration);
        var result = source.TryReadMappedCopy(address, (ulong)byteCount, address,
            bytes.AsSpan(0, byteCount), out var mapping);
        if (result == CpuMappedCopyResult.Unavailable)
            return memory.TryRead(address, bytes.AsSpan(0, byteCount)); // overwrite all partly changed bytes
        if (result != CpuMappedCopyResult.Copied || mapping is null ||
            mapping.FootprintAddress != address || mapping.FootprintByteCount != (ulong)byteCount ||
            mapping.SourceAddress != address || mapping.SourceByteCount != (ulong)byteCount ||
            !ReferenceEquals(memory, source) || originalSession != CurrentStorageCpuSeedSession ||
            !source.IsMappedCopyObservationCurrent(mapping) || !observation.Observed ||
            !GuestImageWriteTracker.IsReadSnapshotStable(read) ||
            !IsCpuTextureSnapshotContentObservationStable(address, observation))
            throw new NotSupportedException("Metal display initial copy lost its original complete mapping/content/session observation.");
        original = new(memory, source, mapping, observation, read, originalSession,
            address, width, height, pitch, bytesPerPixel, format);
        return true;
    }

    // Tracker sampling is outside the presenter gate. The later under-gate
    // check is scalar mapping/owner identity only; it never queries a new view.
    private static void ValidateDisplayOriginalCopy(DisplayOriginalCopy original)
    {
        if (original.Session != CurrentStorageCpuSeedSession ||
            !original.Source.IsMappedCopyObservationCurrent(original.Mapping) ||
            !original.Content.Observed || !GuestImageWriteTracker.IsReadSnapshotStable(original.Read) ||
            !IsCpuTextureSnapshotContentObservationStable(original.Address, original.Content))
            throw new NotSupportedException("Metal display original copy changed before native publication.");
    }

    private static bool IsDisplayOriginalOwnerCurrentLocked(GuestImage image, DisplayOriginalOwner owner,
        ICpuMemory memory, long session)
    {
        var original = owner.Copy;
        // Native mutations may advance the same owner's version, but never
        // rebase its mapping. The pitched span covers every native XY texel.
        return ReferenceEquals(original.Memory, memory) && original.Session == session &&
            ReferenceEquals(original.Memory, original.Source) && image.Texture != 0 && image.Texture == owner.Texture &&
            image.Initialized && image.ContentVersion >= owner.InitialVersion &&
            image.Width == original.Width && image.Height == original.Height && image.Depth == 1 &&
            image.ArrayLength == 1 && image.TextureType == MtlTextureType2D && image.Format == original.Format &&
            original.Pitch >= original.Width && original.BytesPerPixel == MetalRenderTargetFormat.GetBytesPerPixel(original.Format) &&
            original.Mapping.FootprintAddress == original.Address && original.Mapping.SourceAddress == original.Address &&
            original.Mapping.FootprintByteCount == checked((ulong)original.Pitch * original.Height * original.BytesPerPixel) &&
            original.Mapping.SourceByteCount == original.Mapping.FootprintByteCount &&
            original.Source.IsMappedCopyObservationCurrent(original.Mapping);
    }

    private static bool RecordRetiredDisplayOriginalLocked(GuestImage retired)
    {
        if (!_displayOriginalOwners.TryGetValue(retired, out var owner) ||
            _guestMemory is not { } memory || !IsDisplayOriginalOwnerCurrentLocked(retired, owner, memory, CurrentStorageCpuSeedSession))
            return false;
        var original = owner.Copy;
        for (var index = 0; index < _finiteRetiredDisplayOriginCount; index++)
            if (ReferenceEquals(_finiteRetiredDisplayOrigins![index], original)) return true;
        if (_finiteRetiredDisplayOriginCount + _finiteStorageRetiredOriginCount >= MaximumFiniteRetiredOrigins)
            return false;
        try { _finiteRetiredDisplayOrigins ??= new DisplayOriginalCopy?[MaximumFiniteRetiredOrigins]; }
        catch { return false; }
        _finiteRetiredDisplayOrigins[_finiteRetiredDisplayOriginCount++] = original;
        return true;
    }
}
