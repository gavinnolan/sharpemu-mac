// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

// A guest byte range as seen by the GPU: an MTLBuffer (a no-copy guest window or an
// upload ring slice) and the byte offset of the range's first byte inside it.
internal readonly record struct MetalGuestRange(nint Buffer, ulong Offset, ulong Size, ulong GpuAddress)
{
    public bool IsValid => Buffer != 0;
}

// The command buffer currently being recorded and the completion serials of the queue.
// Resources a transfer allocates for this command buffer may be reused once
// CompletedSerial >= SubmissionSerial. Callers end any open encoder before passing it.
internal interface IMetalSubmissionContext
{
    nint Device { get; }

    nint CommandBuffer { get; }

    // The serial the shared event reaches when CommandBuffer completes.
    ulong SubmissionSerial { get; }

    // The last serial the shared event has reached.
    ulong CompletedSerial { get; }

    // Profiling only: everything encoded into CommandBuffer up to here belongs to this
    // label. The host closes and times the command buffer under
    // SHARPEMU_METAL_V2_PROFILE_SHADERS=1 and does nothing otherwise, which is why this
    // may be called from any encode path without a cost.
    void ProfileEncoded(string label)
    {
        _ = label;
    }
}

// Moves image contents between guest memory and Metal textures entirely on the GPU.
// Implemented by MetalImageTransfer (tiler workstream); used by MetalImageCache.
// Why an encoder was torn down to record a transfer. Every one of these ends the open
// render pass, and a pass restart costs the attachment a store and a reload, so which
// cause dominates decides what is worth avoiding.
internal enum MetalTransferCause
{
    Upload,
    Writeback,
    ColorClear,
    DepthClear,
    GuestFill,
    GuestCopy,
    ImageCopy,
    EndOfPipeLabel,
}

internal interface IMetalImageTransfer
{
    // The Metal texture shape the cache must allocate for a description (format,
    // type, extent, mips, layers, usage). The transfer and the cache agree on it.
    MetalTextureShape ShapeFor(in ImageDescription description);

    // Detiles/copies every subresource in range from guest memory into the texture.
    //
    // source covers [guestBase, guestBase + pieceSize) of description.Data — the piece of
    // the image the caller could map — and range names the subresources inside it.
    // Passing (0, description.Data.Size) with the whole range is the ordinary whole-image
    // transfer; a caller that cannot map a very large image whole splits it into pieces so
    // that each one lands in a guest-memory window and the staging stays piece-sized.
    // Stencil, when present, is passed separately and is cut the same way.
    void EncodeGuestToTexture(IMetalSubmissionContext context, in ImageDescription description,
        SubresourceRange range, MetalGuestRange source, MetalGuestRange stencilSource, nint texture,
        ulong guestBase, ulong pieceSize);

    // Retiles/copies every subresource in range from the texture back into guest memory,
    // with the same piece contract as the upload.
    void EncodeTextureToGuest(IMetalSubmissionContext context, in ImageDescription description,
        SubresourceRange range, nint texture, MetalGuestRange destination, MetalGuestRange stencilDestination,
        ulong guestBase, ulong pieceSize);

    // Where a run of an image's guest bytes may end without cutting one of its transfers
    // in half: a caller that found a window covering [start, end) asks for the end it
    // should actually use, so every subresource is either wholly inside a run or wholly
    // inside another one.
    ulong AlignRunEnd(in ImageDescription description, ulong start, ulong end);

    // Where the next run may begin, for a caller that had to step over guest bytes no
    // window covers: a transfer the step landed inside carries nothing, so the run after
    // it starts past that transfer rather than in the middle of it.
    ulong AlignRunStart(in ImageDescription description, ulong offset);
}

// What MTLTextureDescriptor fields an image needs.
internal readonly record struct MetalTextureShape(
    ulong PixelFormat,      // MTLPixelFormat
    ulong TextureType,      // MTLTextureType
    uint Width,
    uint Height,
    uint Depth,
    uint MipLevels,
    uint ArrayLength,
    uint SampleCount,
    ulong Usage);           // MTLTextureUsage
