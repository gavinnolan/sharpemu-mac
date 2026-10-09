// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Kernel;
using CpuTextureSnapshotObservation = SharpEmu.Gpu.Metal.MetalVideoPresenter.CpuTextureSnapshotObservation;

namespace SharpEmu.Gpu.Metal;

// Original normalized DB placement. This is neither a backing lease nor a
// successful GPU completion receipt. The 8-byte probe observes the COMPLETE
// original backing, and is discarded; it never supplies sampled depth bytes.
internal static class MetalDepthFacePlacement
{
    internal static bool Enabled = string.Equals(Environment.GetEnvironmentVariable(
        "SHARPEMU_METAL_NATIVE_DEPTH_CUBE"), "1", StringComparison.Ordinal);
    internal const ulong FaceBytes = 0x200000;
    internal sealed record Observation(ICpuMemory Memory, ICpuMappedCopySource Source,
        CpuMappedCopyObservation Mapping, CpuTextureSnapshotObservation Content,
        long Session, DepthTargetWords Registers, ulong BackingAddress,
        ulong BackingBytes, ulong Address, uint Layer);
    private static readonly ConditionalWeakTable<GuestDepthTarget, Observation> Targets = new();
    internal sealed record DepthSpan(ICpuMemory Memory, ulong Address, ulong Bytes);
    private static readonly ConditionalWeakTable<GuestDepthTarget, DepthSpan> Depths = new();
    internal static bool TryGetDepthSpan(GuestDepthTarget target, out DepthSpan span)
    {
        span = null!; return Enabled && Depths.TryGetValue(target, out span!);
    }
    internal sealed record ColorSpan(ICpuMemory Memory, ulong Address, ulong Bytes);
    private static readonly ConditionalWeakTable<GuestRenderTarget, ColorSpan> Colors = new();
    internal static bool TryGet(GuestDepthTarget target, out Observation observation)
    {
        observation = null!; return Enabled && Targets.TryGetValue(target, out observation!);
    }
    internal static bool TryGetColorSpan(GuestRenderTarget target, out ColorSpan span)
    {
        span = null!; return Enabled && Colors.TryGetValue(target, out span!);
    }
    internal static GuestRenderTarget RecordColorSpan(ICpuMemory memory, ColorTargetResolution resolution, GuestRenderTarget target)
    {
        if (Enabled)
        {
            memory = CpuGpuMemoryView.Resolve(memory);
            var span = resolution.Request.Description.Data;
            if (!span.IsValid || span.Size == 0)
                throw new NotSupportedException("Native depth cube sibling color writer lacks its complete normalized span.");
            Colors.Add(target, new(memory, span.Address, span.Size));
        }
        return target;
    }

    internal static ulong SelectedAddress(DepthTargetResolution resolution, DepthTargetWords words)
    {
        var d = resolution.Request.Description;
        var view = resolution.Request.View;
        if (resolution.Width != 1024 || resolution.Height != 1024 || resolution.Samples != 1 ||
            resolution.HasStencil || words.DepthFormat != GuestDepthFormat.Z16 ||
            ((words.ZInfo >> 4) & 31) != 24 || words.MaxMip != 0 || words.ViewMipLevel != 0 ||
            words.SliceStart != words.SliceMax || words.SliceStart >= 6 ||
            words.ZReadBase == 0 || words.ZReadBase != words.ZWriteBase || (words.ZReadBase & 0xFFFF) != 0 ||
            d.Data.Address != words.ZReadBase || d.Data.Size != FaceBytes * (words.SliceMax + 1UL) ||
            resolution.DepthAddress != d.Data.Address || resolution.DepthSize != d.Data.Size ||
            d.BytesPerBlock != 2 || d.Pitch != 1024 || d.Resources.Levels != 1 || d.Resources.Layers != words.SliceMax + 1 ||
            d.PixelFormat != GpuFormat.D16Unorm || d.Extent.Width != 1024 || d.Extent.Height != 1024 || d.Extent.Depth != 1 ||
            d.MipLayout[0].Offset != 0 || d.MipLayout[0].Size != d.Data.Size ||
            view.BaseLayer != words.SliceStart || view.LayerCount != 1 || view.BaseLevel != 0 || view.LevelCount != 1 ||
            words.ZReadBase > ulong.MaxValue - d.Data.Size)
            throw new NotSupportedException("Metal native depth cube face requires the original normalized 1024-square Z16 mode24 single selected DB layer and complete backing footprint.");
        return checked(d.Data.Address + words.SliceStart * FaceBytes);
    }

    internal static GuestDepthTarget Project(ICpuMemory memory, DepthTargetResolution resolution,
        DepthTargetWords words, GuestDepthTarget target)
    {
        if (!Enabled) return target;
        memory = CpuGpuMemoryView.Resolve(memory);
        if (resolution.Width != 1024 || resolution.Height != 1024 || words.DepthFormat != GuestDepthFormat.Z16 || ((words.ZInfo >> 4) & 31) != 24)
        {
            if (resolution.DepthAddress == 0 || resolution.DepthSize == 0 || resolution.DepthAddress > ulong.MaxValue - resolution.DepthSize)
                throw new NotSupportedException("Native cube sibling depth writer lacks its original normalized full span.");
            Depths.Add(target, new(memory, resolution.DepthAddress, resolution.DepthSize));
            return target;
        }
        var address = SelectedAddress(resolution, words);
        if (memory is not ICpuMappedCopySource source)
            throw new NotSupportedException("Metal native depth cube producer requires an original full-footprint mapping observation.");
        var originalSession = MetalVideoPresenter.CurrentNativeDepthFaceSession;
        // Mapping lifetime covers the complete original resource; content belongs
        // to the exact selected physical layer. Tracking a growing prefix at
        // the same base would replace earlier immutable protected owners.
        var epoch = HostMemory.MappingGeneration;
        var probe = new byte[8];
        var result = source.TryReadMappedCopy(resolution.DepthAddress, resolution.DepthSize, address, probe, out var mapping);
        // Choose the original complete mapping BEFORE arming observation or
        // capturing the first token. Remapping during either operation revokes
        // this exact mapping; a younger mapping is never captured in its place.
        // The discarded probe never supplies native depth contents.
        var read = GuestImageWriteTracker.BeginReadSnapshot(address, FaceBytes, source: "metal.depth-face.original");
        var content = MetalVideoPresenter.ObserveCpuTextureSnapshot(address, FaceBytes, epoch);
        long? sessionNow = null, epochNow = null;
        bool? mappingCurrent = null, tokenPresent = null;
        GuestImageWriteTracker.ContentToken tokenNow = default;
        string? reason = null;
        // Scope lifetime validation to the original provider mapping. The
        // process-wide epoch includes unrelated mappings/protection changes;
        // retain it only as a diagnostic, never restamp either observation.
        if (result != CpuMappedCopyResult.Copied) reason = "mapped-copy-result";
        else if (mapping is null) reason = "missing-mapping";
        else if (mapping.FootprintAddress != resolution.DepthAddress || mapping.FootprintByteCount != resolution.DepthSize ||
            mapping.SourceAddress != address || mapping.SourceByteCount != 8) reason = "mapping-geometry";
        else if (!content.Observed) reason = "original-content-unobserved";
        else if (!read.Active || !GuestImageWriteTracker.IsReadSnapshotStable(read)) reason = "original-read-generation";
        else if ((sessionNow = MetalVideoPresenter.CurrentNativeDepthFaceSession) != originalSession) reason = "presenter-session";
        else if (!(mappingCurrent = source.IsMappedCopyObservationCurrent(mapping)).Value) reason = "original-mapping-currentness";
        else if (!(tokenPresent = GuestImageWriteTracker.TryGetObservedContentToken(address, content.ByteCount, out tokenNow)).Value)
            reason = "current-content-unobserved";
        else if (tokenNow != content.Token) reason = "original-content-token";
        else if (!(mappingCurrent = source.IsMappedCopyObservationCurrent(mapping)).Value) reason = "original-mapping-currentness-final";
        else if (!GuestImageWriteTracker.IsReadSnapshotStable(read)) reason = "original-read-generation-final";
        else if ((sessionNow = MetalVideoPresenter.CurrentNativeDepthFaceSession) != originalSession) reason = "presenter-session-final";
        if (reason is not null)
        {
            epochNow = HostMemory.MappingGeneration; // failure diagnostic only
            throw OriginalAdmissionFailure(reason, result, address, resolution.DepthAddress, resolution.DepthSize,
                words, read, content, mapping, originalSession, sessionNow, mappingCurrent, epochNow, tokenPresent, tokenNow);
        }
        var projected = target with { ReadAddress = address, WriteAddress = address };
        Targets.Add(projected, new(memory, source, mapping, content, originalSession, words,
            resolution.DepthAddress, resolution.DepthSize, address, words.SliceStart));
        Depths.Add(projected, new(memory, address, FaceBytes));
        return projected;
    }

    // Failure-only scalar formatting; no extra tracker/provider/native queries.
    private static NotSupportedException OriginalAdmissionFailure(string reason, CpuMappedCopyResult result,
        ulong address, ulong backingAddress, ulong backingBytes, DepthTargetWords words,
        GuestImageWriteTracker.ReadSnapshot read, CpuTextureSnapshotObservation content,
        CpuMappedCopyObservation? mapping, long session, long? sessionNow, bool? mappingCurrent,
        long? epochNow, bool? tokenPresent, GuestImageWriteTracker.ContentToken tokenNow)
    {
        const string primary = "Metal native depth face original mapping/session/content changed before admission.";
        try
        {
            return new NotSupportedException($"{primary} reason={reason} stage=project " +
                $"selected_addr=0x{address:X} selected_bytes={FaceBytes} layer={words.SliceStart} " +
                $"backing_addr=0x{backingAddress:X} backing_bytes={backingBytes} " +
                $"mapped_result={result} mapping_present={mapping is not null} mapping_current={mappingCurrent} " +
                $"session_before={session} session_now={sessionNow} mapping_before={content.MappingEpoch} mapping_now={epochNow} " +
                $"snapshot_active={read.Active} snapshot_generation={read.Generation} observed_before={content.Observed} observed_now={tokenPresent} " +
                $"token_before=[{content.Token}] token_now=[{tokenNow}] " +
                $"zinfo=0x{words.ZInfo:X8} slice_max={words.SliceMax}");
        }
        catch (Exception) { return new NotSupportedException(primary); }
    }

    internal static void RequireOriginalCurrent(Observation original,
        [CallerMemberName] string invocation = "")
    {
        long? sessionNow = null, epochNow = null;
        bool? mappingCurrent = null, tokenPresent = null;
        GuestImageWriteTracker.ContentToken tokenNow = default;
        string? reason = null;
        // This route retains the original complete provider mapping, rather
        // than relying on a process-wide epoch shared by unrelated mappings.
        // Keep the original content token and recheck the exact mapping after
        // querying it. Neither observation is replaced or restamped.
        if (!original.Content.Observed) reason = "original-content-unobserved";
        else if (original.Content.ByteCount != FaceBytes) reason = "original-content-size";
        else if (original.Mapping.FootprintAddress != original.BackingAddress ||
            original.Mapping.FootprintByteCount != original.BackingBytes ||
            original.Mapping.SourceAddress != original.Address || original.Mapping.SourceByteCount != 8 ||
            original.Address < original.BackingAddress ||
            original.Address - original.BackingAddress > original.BackingBytes ||
            FaceBytes > original.BackingBytes - (original.Address - original.BackingAddress)) reason = "original-mapping-geometry";
        else if ((sessionNow = MetalVideoPresenter.CurrentNativeDepthFaceSession) != original.Session) reason = "presenter-session";
        else if (!(mappingCurrent = original.Source.IsMappedCopyObservationCurrent(original.Mapping)).Value) reason = "original-mapping-currentness";
        else if (!(tokenPresent = GuestImageWriteTracker.TryGetObservedContentToken(
            original.Address, original.Content.ByteCount, out tokenNow)).Value) reason = "current-content-unobserved";
        else if (tokenNow != original.Content.Token) reason = "original-content-token";
        else if (!(mappingCurrent = original.Source.IsMappedCopyObservationCurrent(original.Mapping)).Value) reason = "original-mapping-currentness-final";
        if (reason is not null)
        {
            epochNow = HostMemory.MappingGeneration; // failure diagnostic only
            throw OriginalCurrentFailure(original, invocation, reason, sessionNow, mappingCurrent, epochNow, tokenPresent, tokenNow);
        }
    }

    private static NotSupportedException OriginalCurrentFailure(Observation original, string invocation, string reason,
        long? sessionNow, bool? mappingCurrent, long? epochNow, bool? tokenPresent,
        GuestImageWriteTracker.ContentToken tokenNow)
    {
        const string primary = "Metal native depth face lost its ORIGINAL mapping/session/content observation.";
        try
        {
            return new NotSupportedException($"{primary} reason={reason} stage=require-original invocation={invocation} " +
                $"selected_addr=0x{original.Address:X} selected_bytes={original.Content.ByteCount} layer={original.Layer} " +
                $"backing_addr=0x{original.BackingAddress:X} backing_bytes={original.BackingBytes} " +
                $"session_before={original.Session} session_now={sessionNow} mapping_current={mappingCurrent} " +
                $"mapping_before={original.Content.MappingEpoch} mapping_now={epochNow} " +
                $"observed_before={original.Content.Observed} observed_now={tokenPresent} " +
                $"token_before=[{original.Content.Token}] token_now=[{tokenNow}]");
        }
        catch (Exception) { return new NotSupportedException(primary); }
    }
}
