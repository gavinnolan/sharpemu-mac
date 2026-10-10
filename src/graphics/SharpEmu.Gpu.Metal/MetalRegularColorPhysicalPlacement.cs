// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

// Complete normalized mapping facts only. No content token, write watch,
// publication ticket, CPU seed, native borrow or successful producer receipt.
internal static class MetalRegularColorPhysicalPlacement
{
    internal const ulong MaximumBytes = 128UL * 1024 * 1024;
    internal readonly record struct PhysicalSpan(long BackingIdentity, ulong Offset, ulong ByteCount);

    // Pure geometry validation: supplied scalar segments alone grant no
    // provider provenance. Capture calls this only on the original real receipt.
    internal static PhysicalSpan[] DescribeSpans(ulong address, ulong bytes, IReadOnlyList<CpuMappingFootprintSegment> segments)
    {
        if (address == 0 || bytes is < 8 or > MaximumBytes || address > ulong.MaxValue - bytes ||
            segments.Count is 0 or > CpuMappedCopyObservation.MaximumSegments)
            throw new NotSupportedException("Regular color physical coverage is empty, oversized or wrapping.");
        var cursor = address; var end = address + bytes;
        var result = new PhysicalSpan[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var s = segments[index];
            if (s.MappingAddress == 0 || s.MappingByteCount == 0 || s.MappingAddress > ulong.MaxValue - s.MappingByteCount ||
                s.CoveredAddress != cursor || s.CoveredByteCount == 0 || s.CoveredAddress < s.MappingAddress ||
                s.CoveredAddress - s.MappingAddress > s.MappingByteCount ||
                s.CoveredByteCount > s.MappingByteCount - (s.CoveredAddress - s.MappingAddress) ||
                s.CoveredByteCount > end - cursor || s.MappingIncarnation <= 0 || s.BackingIdentity <= 0 ||
                s.MappingBackingOffset > s.BackingLogicalByteCount ||
                s.MappingByteCount > s.BackingLogicalByteCount - s.MappingBackingOffset ||
                s.ProviderPageSize == 0 || (s.ProviderPageSize & (s.ProviderPageSize - 1)) != 0 ||
                s.MappingAddress % s.ProviderPageSize != 0 || s.MappingByteCount % s.ProviderPageSize != 0 ||
                s.MappingBackingOffset % s.ProviderPageSize != 0 ||
                (s.GuestAccess & (GuestPageProtection.Read | GuestPageProtection.Write)) !=
                    (GuestPageProtection.Read | GuestPageProtection.Write) ||
                (s.GuestAccess & ~(GuestPageProtection.Read | GuestPageProtection.Write | GuestPageProtection.Execute)) != 0)
                throw new NotSupportedException("Regular color original physical coverage/rights/backing geometry are incomplete.");
            var offset = checked(s.MappingBackingOffset + (s.CoveredAddress - s.MappingAddress));
            if (offset > s.BackingLogicalByteCount || s.CoveredByteCount > s.BackingLogicalByteCount - offset)
                throw new NotSupportedException("Regular color original physical span escapes its backing.");
            result[index] = new(s.BackingIdentity, offset, s.CoveredByteCount);
            for (var previous = 0; previous < index; previous++)
            {
                var p = segments[previous];
                if (p.BackingIdentity != s.BackingIdentity) continue;
                if (p.BackingLogicalByteCount != s.BackingLogicalByteCount || p.ProviderPageSize != s.ProviderPageSize ||
                    Overlap(result[previous], result[index]))
                    throw new NotSupportedException("Regular color original backing facts conflict or internally alias.");
            }
            cursor += s.CoveredByteCount;
        }
        if (cursor != end) throw new NotSupportedException("Regular color original full allocation coverage has a gap.");
        return result;
    }

    internal static bool Overlap(PhysicalSpan a, PhysicalSpan b) => a.BackingIdentity == b.BackingIdentity &&
        (UInt128)a.Offset < (UInt128)b.Offset + b.ByteCount && (UInt128)b.Offset < (UInt128)a.Offset + a.ByteCount;

    internal sealed class Layout
    {
        internal readonly ColorTargetWords Registers;
        internal readonly MtlPixelFormat NativeFormat;
        private readonly ImageDescription description;
        private readonly ImageViewDescription view;
        internal ulong Address => description.Data.Address;
        internal ulong ByteCount => description.Data.Size;
        internal uint Width => description.Extent.Width;
        internal uint Height => description.Extent.Height;
        internal uint Pitch => description.Pitch;
        internal ulong MipOffset => description.MipLayout[0].Offset;
        internal ulong MipBytes => description.MipLayout[0].Size;

        private Layout(ColorTargetWords registers, MtlPixelFormat nativeFormat,
            ImageDescription ownedDescription, ImageViewDescription ownedView)
        { Registers = registers; NativeFormat = nativeFormat; description = ownedDescription; view = ownedView; }

        internal bool Matches(Layout other) => Registers == other.Registers && NativeFormat == other.NativeFormat &&
            SameDescription(description, other.description) && view == other.view;

        internal bool MatchesTarget(GuestRenderTarget target) => target.Address == Address &&
            target.Width == Width && target.Height == Height && target.MipLevels == 1 &&
            target.Registers == Registers && target.Format == (uint)Registers.Layout &&
            target.NumberType == (uint)Registers.NumberType && target.ComponentSwap == (uint)Registers.Order &&
            target.TileMode == (uint)Registers.TileMode && (target.WriteMask & ~0xFu) == 0;

        internal static Layout Describe(ColorTargetResolution supplied, ColorTargetWords words, GuestRenderTarget target)
        {
            // Reject unsupported raw shape before invoking the existing builder.
            // Its unknown-input fatal paths must not become a normalization aid.
            if (words.BaseAddress == 0 || words.MaxMip != 0 || words.MipLevel != 0 || words.Dimension != 1 ||
                words.Depth != 0 || words.SliceStart != 0 || words.SliceMax != 0 ||
                words.SamplesLog2 != 0 || words.FragmentsLog2 != 0 || words.DccEnabled || words.FastClear ||
                words.FmaskCompression || words.CmaskAddress != 0 || words.FmaskAddress != 0 || words.DccAddress != 0 ||
                words.TileMode is not (GuestTileMode.Linear or GuestTileMode.Standard4KB or GuestTileMode.Standard64KB or
                    GuestTileMode.Depth or GuestTileMode.RenderTarget) ||
                !MetalGuestFormats.TryDecodeRenderTargetFormat((uint)words.Layout, (uint)words.NumberType,
                    (uint)words.Order, out var format))
                throw new NotSupportedException("Regular color original requires supported single-mip/layer/sample 2D register metadata.");
            // All descriptions contain only value-owned fields/inline mip slots.
            // Never compare InlineArray with ValueType.Equals or expose a ref to it.
            var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)
                ?? throw new NotSupportedException("Regular color original normalization has no target.");
            var d = supplied.Request.Description;
            var n = normalized.Request.Description;
            var v = supplied.Request.View;
            var mip = d.MipLayout[0];
            if (supplied.Request.Role != ImageRole.ColorTarget || supplied.BaseAddress != words.BaseAddress ||
                supplied.BackingSize != d.Data.Size || supplied.BaseMipLevel != 0 || supplied.BaseArrayLayer != 0 ||
                supplied.Samples != 1 || supplied.Extent.Width != d.Extent.Width || supplied.Extent.Height != d.Extent.Height ||
                !d.Data.IsValid || d.Data.Address != words.BaseAddress || d.Data.Size is < 8 or > MaximumBytes ||
                d.Data.Address > ulong.MaxValue - d.Data.Size || d.Type != GuestImageType.Color2D ||
                d.Extent.Width is 0 or > 16384 || d.Extent.Height is 0 or > 16384 || d.Extent.Depth != 1 ||
                d.Resources.Levels != 1 || d.Resources.Layers != 1 || d.Samples != 1 || d.IsBlock || d.HasMetadata || d.HasStencil ||
                d.BytesPerBlock != MetalRenderTargetFormat.GetBytesPerPixel(format.Format) || d.BytesPerBlock == 0 ||
                mip.Size == 0 || mip.Offset > d.Data.Size || mip.Size > d.Data.Size - mip.Offset ||
                v.Type != GpuImageViewType.Type2D || v.Aspect != GpuImageAspect.ColorBit ||
                v.BaseLevel != 0 || v.LevelCount != 1 || v.BaseLayer != 0 || v.LayerCount != 1 ||
                v.Usage != GpuImageUsage.ColorAttachmentBit || v.MinLod != 0 ||
                !SameDescription(d, n) || v != normalized.Request.View)
                throw new NotSupportedException("Regular color original lacks its complete exact normalized allocation/view.");
            var result = new Layout(words, format.Format, d, v);
            if (!result.MatchesTarget(target))
                throw new NotSupportedException("Regular color original target differs from its raw registers/normalized view.");
            return result;
        }

        private static bool SameDescription(ImageDescription a, ImageDescription b)
        {
            if (a.Data != b.Data || a.Stencil != b.Stencil || a.Metadata.Kind != b.Metadata.Kind ||
                a.Metadata.Range != b.Metadata.Range || a.Metadata.Control != b.Metadata.Control ||
                a.Metadata.Compression != b.Metadata.Compression || a.Metadata.StencilCompressed != b.Metadata.StencilCompressed ||
                a.HtileClearMask != b.HtileClearMask || a.PixelFormat != b.PixelFormat || a.GuestFormat != b.GuestFormat ||
                a.Type != b.Type || a.Extent != b.Extent || a.Resources != b.Resources || a.Pitch != b.Pitch ||
                a.BytesPerBlock != b.BytesPerBlock || a.Samples != b.Samples || a.TileMode != b.TileMode || a.Bgra16 != b.Bgra16)
                return false;
            // All sixteen owned slots are compared explicitly, including unused
            // slots, so supplied extra layout data cannot silently disappear.
            for (var i = 0; i < ImageDescription.MaxLevels; i++)
            {
                var x = a.MipLayout[i]; var y = b.MipLayout[i];
                if (x.Offset != y.Offset || x.Size != y.Size || x.Pitch != y.Pitch || x.Height != y.Height) return false;
            }
            return true;
        }
    }

    internal sealed class Observation
    {
        internal readonly ICpuMemory Memory;
        internal readonly ICpuMappedCopySource Source;
        internal readonly CpuMappedCopyObservation Mapping;
        internal readonly Layout Placement;
        internal readonly long Session;
        private readonly PhysicalSpan[] spans;
        internal ReadOnlySpan<PhysicalSpan> Spans => spans;
        private Observation(ICpuMemory memory, ICpuMappedCopySource source, CpuMappedCopyObservation mapping,
            Layout placement, long session, PhysicalSpan[] ownedSpans)
        { Memory = memory; Source = source; Mapping = mapping; Placement = placement; Session = session; spans = ownedSpans; }

        internal static Observation Capture(ICpuMemory memory, Layout layout, long session, Func<bool> ownerStillCurrent)
        {
            ArgumentNullException.ThrowIfNull(ownerStillCurrent);
            // Explicit reader selection precedes the FIRST provider capture.
            // Captured receipts are never re-resolved or rewritten afterward.
            memory = CpuGpuMemoryView.Resolve(memory);
            if (memory is not ICpuMappedCopySource source || !ownerStillCurrent())
                throw new NotSupportedException("Regular color original has no unchanged selected mapped provider/session.");
            Span<byte> discarded = stackalloc byte[8];
            var result = source.TryReadMappedCopy(layout.Address, layout.ByteCount, layout.Address, discarded, out var mapping);
            if (result != CpuMappedCopyResult.Copied || mapping is null ||
                mapping.FootprintAddress != layout.Address || mapping.FootprintByteCount != layout.ByteCount ||
                mapping.SourceAddress != layout.Address || mapping.SourceByteCount != 8)
                throw new NotSupportedException("Regular color original full allocation mapping geometry differs from its first capture.");
            var spans = DescribeSpans(layout.Address, layout.ByteCount, mapping.Segments);
            if (!source.IsMappedCopyObservationCurrent(mapping) || !ownerStillCurrent() ||
                !source.IsMappedCopyObservationCurrent(mapping))
                throw new NotSupportedException("Regular color original full allocation mapping changed before admission.");
            return new Observation(memory, source, mapping, layout, session, spans);
        }
    }
}
