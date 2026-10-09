// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using static SharpEmu.Libs.Agc.AgcBackendServices;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalTextureSnapshots
{
    // Capture the ORIGINAL mapping incarnation alongside the original bytes,
    // not by rereading/reconstructing a mapping at native creation or dirty sync.
    private static bool TryCreateStorageCpuCopy(CpuContext context, Binding binding, out GuestDrawTexture texture)
    {
        texture = null!;
        if (!binding.IsStorage || MetalVideoPresenter.TextureSourceGraphEnabled ||
            context.Memory is not ICpuMappedCopySource source) return false;
        var d = binding.Descriptor;
        var linearVolume = MetalVideoPresenter.SingletonVolumeOriginalCaptureEnabled && d.Type == 10 && d.TileMode == 0;
        var tiledSingleton = MetalVideoPresenter.SingletonVolumeOriginalCaptureEnabled &&
            MetalSingletonVolumeStorageAdmission.IsCandidate(d);
        var volume = linearVolume || tiledSingleton;
        if (tiledSingleton && !MetalSingletonVolumeStorageAdmission.IsQualified(d, binding.ResourceDescriptor,
                binding.IsArrayed, binding.MipLevel, binding.Shape, binding.Access))
            throw new NotSupportedException("Original singleton volume capture requires the exact captured RGBA8Uint mode27/raw8 origin class.");
        if ((linearVolume || MetalSingletonVolumeStorageAdmission.IsCandidate(d)) &&
            (!volume || source is not ICpuMappedReadWatchSource)) return false; // legacy bytes; no positive cohort proof
        // Preserve native-owner/queued upload selection. Do not capture stale
        // guest bytes over a previously admitted storage producer.
        if (GuestGpu.Current is IGuestImageSnapshotBackend snapshots &&
            snapshots.IsGuestImageUploadKnown(d.Address, d.Format, d.NumberType)) return false;
        var physical = d.Width == 3840 && d.Height == 2160 ? 8847360UL :
            d.Width == 1024 && d.Height == 1024 ? 4194304UL : 65536UL;
        var pitch = linearVolume ? GetLinearTexturePitch(Math.Max(d.Width, d.Pitch), d.Height, d.Format) : d.Pitch;
        var template = new GuestDrawTexture(d.Address, d.Width, d.Height, d.Format, d.NumberType, [],
            IsFallback: false, IsStorage: true, Pitch: pitch, TileMode: d.TileMode, DstSelect: d.DstSelect,
            ArrayedView: binding.IsArrayed, Type: d.Type, Depth: volume ? d.Depth : 1, SourceByteCount: physical,
            Descriptor: binding.ResourceDescriptor?.ToArray(), Shape: binding.Shape, Access: binding.Access,
            MipLevel: binding.MipLevel, BaseMipLevel: d.BaseLevel, ResourceMipLevels: d.MaxMip + 1);
        if (linearVolume)
        {
            var nativeFormat = MetalVideoPresenter.StorageTextureFormat(template);
            physical = MetalVideoPresenter.StorageTextureSeedLayout(template, nativeFormat).RequiredBytes;
            if (physical == 0 || physical > MaximumSnapshotBytes)
                throw SnapshotBudgetExceeded(d, physical, "linear storage-volume original", MaximumSnapshotBytes);
            template = template with { SourceByteCount = physical };
        }
        var layout = MetalStorageCpuLayout.TryCreate(template);
        if (layout is null) return false;
        var withWriteWatch = volume || MetalVideoPresenter.RgbaStorageRuntimePublicationRequested &&
            MetalBgra1024ArrayStorageAdmission.IsQualified(template) && source is ICpuMappedReadWatchSource;
        // The original copy belongs to this session even if a reset races a
        // later capture attempt. Never stamp old bytes with a newer session.
        var originalSession = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        var lastResult = CpuMappedCopyResult.Unavailable;
        var firstRejectedPredicate = "copy-not-attempted";
        ulong lastFootprintAddress = 0, lastFootprintBytes = 0, lastSourceAddress = 0, lastSourceBytes = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (originalSession != MetalVideoPresenter.CurrentStorageCpuSeedSession)
                throw new InvalidOperationException("Metal typed storage session changed before the original copy.");
            var read = GuestImageWriteTracker.BeginReadSnapshot(d.Address, physical, source: "agc.typed-storage-mapped-copy");
            // Initial tracking may establish native page protection and publish
            // that setup effect as a mapping change. Retain the original epoch
            // after setup, BEFORE token observation and copied bytes; never
            // refresh it after the copy or a later publication.
            var epoch = HostMemory.MappingGeneration;
            var observation = MetalVideoPresenter.ObserveCpuTextureSnapshot(d.Address, physical, epoch);
            var bytes = new byte[checked((int)physical)];
            CpuMappedCopyObservation? mapping = null;
            var registered = false;
            Exception? primary = null;
            try
            {
                var result = withWriteWatch
                    ? ((ICpuMappedReadWatchSource)source).TryReadMappedCopyWithWriteWatch(d.Address, physical, d.Address, bytes, out mapping)
                    : source.TryReadMappedCopy(d.Address, physical, d.Address, bytes, out mapping);
                lastResult = result;
                lastFootprintAddress = mapping?.FootprintAddress ?? 0;
                lastFootprintBytes = mapping?.FootprintByteCount ?? 0;
                lastSourceAddress = mapping?.SourceAddress ?? 0;
                lastSourceBytes = mapping?.SourceByteCount ?? 0;
                if (originalSession != MetalVideoPresenter.CurrentStorageCpuSeedSession)
                    throw new InvalidOperationException("Metal typed storage session changed during the original copy.");
                if (result == CpuMappedCopyResult.Unavailable) return false; // unchanged legacy owned-copy path; no dirty authority
                if (result != CpuMappedCopyResult.Copied || mapping is null)
                { firstRejectedPredicate = "mapped-copy-refused-or-missing-original"; continue; }
                if (withWriteWatch && (mapping.FootprintAddress != d.Address || mapping.FootprintByteCount != physical ||
                    mapping.SourceAddress != d.Address || mapping.SourceByteCount != physical))
                    throw new InvalidOperationException("Original storage volume copy returned inconsistent full pitched/block metadata.");
                var linear = layout.Decode(bytes);
                // Preserve the exact prior short-circuit predicate order and
                // call count. Failure attribution never obtains a new probe,
                // token, watch or mapping to replace the original admission.
                if (!source.IsMappedCopyObservationCurrent(mapping))
                { firstRejectedPredicate = "original-mapping-currentness"; continue; }
                if (withWriteWatch && !((ICpuMappedReadWatchSource)source).IsMappedCopyWriteWatchCurrent(mapping))
                { firstRejectedPredicate = "original-physical-write-watch"; continue; }
                if (!GuestImageWriteTracker.IsReadSnapshotStable(read))
                { firstRejectedPredicate = "original-read-generation"; continue; }
                if (!MetalVideoPresenter.IsCpuTextureSnapshotAdmissionStable(d.Address, observation))
                { firstRejectedPredicate = "original-content-or-epoch-admission"; continue; }
                var captured = layout.Upload(linear) with
                {
                    Sampler = ToGuestSampler(binding.SamplerDescriptor),
                    WriteGeneration = read.Active ? read.Generation : -1,
                };
                MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(captured, observation);
                if (volume) MetalVideoPresenter.RegisterSingletonVolumeStorageCpuSeedOrigin(captured, layout, context.Memory,
                    (ICpuMappedReadWatchSource)source, mapping, observation, originalSession, read);
                else MetalVideoPresenter.RegisterStorageCpuSeedOrigin(captured, layout, context.Memory, source, mapping, observation, originalSession);
                if (originalSession != MetalVideoPresenter.CurrentStorageCpuSeedSession)
                    throw new InvalidOperationException("Metal typed storage session changed after original-copy registration.");
                texture = captured;
                registered = true;
                return true;
            }
            catch (Exception failure) { primary = failure; throw; }
            finally
            {
                if (withWriteWatch && !registered) MetalVideoPresenter.ReleaseUnregisteredSingletonVolumeWatch(source, mapping, primary);
            }
        }
        throw new InvalidOperationException($"Metal typed storage mapping/content changed across three original-copy attempts at 0x{d.Address:X}. " +
            $"last_result={lastResult} first_rejected_predicate={firstRejectedPredicate} requested_bytes={physical} original_session={originalSession} " +
            $"write_watch_requested={withWriteWatch} last_original_footprint=0x{lastFootprintAddress:X}/{lastFootprintBytes} " +
            $"last_original_source=0x{lastSourceAddress:X}/{lastSourceBytes}.");
    }
}
