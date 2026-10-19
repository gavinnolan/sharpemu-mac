// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Failure-only cached observations. Never query a provider, native handle,
    // content token or replacement mapping to explain this original veto.
    private static InvalidOperationException R32MipZeroInventoryRefusedLocked(long originalSession)
    {
        var reason = $"session, cleanup or unqualified native/pending writer inventory images={_guestImages.Count} extents={_guestImageExtents.Count} depth={_guestDepthImages.Count}/{_guestDepthReadAliases.Count} sources={_latestTextureSources.Count} retired={_finiteStorageRetiredOriginCount}/{_finiteRetiredDisplayOriginCount}";
        var primary = R32MipZeroRefused(reason);
        if (!_selectedCopyDiagnostics) return primary;
        try
        {
            var rows = new List<string>(Math.Min(_guestImages.Count, 8));
            foreach (var pair in _guestImages)
            {
                if (rows.Count == 8) break;
                var image = pair.Value;
                var origin = image.DiagnosticOrigin;
                var storage = image.StorageCpuOrigin;
                _guestImageExtents.TryGetValue(pair.Key, out var extent);
                rows.Add($"addr=0x{pair.Key:X} size={image.Width}x{image.Height}x{image.Depth} " +
                    $"native_type={image.TextureType} format={image.Format} initialized={image.Initialized} gpu_written={image.GpuWritten} version={image.ContentVersion} extent_bytes={extent.ByteCount} " +
                    $"creation_kind={origin.Work.Kind ?? "unrecorded"} creation_sequence={origin.Work.Sequence} creation_queue='{origin.Work.Queue.Name}' creation_submission={origin.Work.Queue.SubmissionId} " +
                    $"creation_guest_type={origin.Type} creation_guest_format={origin.Format}/{origin.Number} creation_tile={origin.Tile} creation_mip={origin.SelectedMip} creation_resource_mips={origin.ResourceMips} creation_raw={origin.Raw?.ToString() ?? "unavailable"} " +
                    $"storage_original_present={storage is not null} storage_original_session={storage?.Session} storage_original_footprint=0x{storage?.Mapping.FootprintAddress:X}/{storage?.Mapping.FootprintByteCount} " +
                    $"storage_original_source=0x{storage?.Mapping.SourceAddress:X}/{storage?.Mapping.SourceByteCount} " +
                    $"storage_original_segments={storage?.Mapping.Segments.Count} color_original_present={image.ColorPhysicalView is not null}");
            }
            return R32MipZeroRefused(reason + $" original_session={originalSession} current_session={CurrentSelectedStorageMipSeedSession} " +
                $"inventory_truncated={_guestImages.Count > rows.Count} original_inventory=[{string.Join("; ", rows)}]");
        }
        catch { return primary; } // Preserve the original veto if optional formatting fails.
    }
}
