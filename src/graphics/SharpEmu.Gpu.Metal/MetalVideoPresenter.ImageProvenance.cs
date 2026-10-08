// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Cached opt-in observational metadata. No backing ownership or coherence receipt.
    private static bool _imageProvenanceEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_IMAGE_PROVENANCE") == "1";
    [ThreadStatic]
    private static ImageDiagnosticWork _imageDiagnosticExecutingWork;
    private readonly record struct ImageDiagnosticWork(long Sequence, GuestQueueIdentity Queue, string? Kind);
    private readonly record struct ImageDiagnosticWords(uint W0, uint W1, uint W2, uint W3, uint W4, uint W5, uint W6, uint W7)
    {
        public override string ToString() => $"[{W0:X8},{W1:X8},{W2:X8},{W3:X8},{W4:X8},{W5:X8},{W6:X8},{W7:X8}]";
    }
    private readonly record struct ImageDiagnosticOrigin(ImageDiagnosticWork Work, ulong Address,
        uint Width, uint Height, uint Pitch, uint Tile, uint Format, uint Number,
        uint Type, uint BaseMip, uint SelectedMip, uint ResourceMips, ulong SourceBytes,
        ImageDiagnosticWords? Raw, ColorTargetWords? Color);

    private static ImageDiagnosticWork BeginImageDiagnosticWork(long sequence)
    {
        if (!_imageProvenanceEnabled) return default;
        var previous = _imageDiagnosticExecutingWork;
        _imageDiagnosticExecutingWork = new(sequence, _submittingGuestQueue ?? GuestQueueIdentity.Default, null);
        return previous;
    }
    private static void EndImageDiagnosticWork(ImageDiagnosticWork previous)
    {
        if (_imageProvenanceEnabled) _imageDiagnosticExecutingWork = previous;
    }
    private static ImageDiagnosticWork ImageDiagnosticContext(string kind) =>
        _imageDiagnosticExecutingWork with { Kind = kind };
    private static ImageDiagnosticWords? ImageDiagnosticRaw(uint[]? raw) => raw is { Length: 8 }
        ? new(raw[0], raw[1], raw[2], raw[3], raw[4], raw[5], raw[6], raw[7]) : null;
    private static ImageDiagnosticOrigin CaptureStorageImageOrigin(GuestDrawTexture texture) => !_imageProvenanceEnabled ? default :
        new(ImageDiagnosticContext("storage"), texture.Address, texture.Width, texture.Height,
            texture.Pitch, texture.TileMode, texture.Format, texture.NumberType, texture.Type,
            texture.BaseMipLevel, texture.MipLevel, texture.ResourceMipLevels, texture.SourceByteCount,
            ImageDiagnosticRaw(texture.Descriptor), null);
    private static ImageDiagnosticOrigin CaptureColorImageOrigin(GuestRenderTarget target) => !_imageProvenanceEnabled ? default :
        new(ImageDiagnosticContext("color-target"), target.Address, target.Width, target.Height,
            0, target.TileMode, target.Format, target.NumberType, 0, 0, 0, target.MipLevels, 0,
            null, target.Registers);
    private static ImageDiagnosticOrigin CaptureDisplayImageOrigin(ulong address, uint width, uint height, uint pitch, uint formatTag) =>
        !_imageProvenanceEnabled ? default : new(ImageDiagnosticContext("display"), address, width, height,
            pitch, 0, formatTag, 0, 0, 0, 0, 0, 0, null, null);

    private static NotSupportedException CpuDirtyImageShapeMismatch(
        GuestImage image, ulong address, uint width, uint height, ulong byteCount)
    {
        // Exception-path observations only: none of these fields authorizes a
        // CPU upload over GPU-written content or proves mapping/write stability.
        var message = $"Metal CPU dirty image synchronization requires a 2D guest image;" +
            $" native_type={image.TextureType} depth={image.Depth}." +
            $" address=0x{address:X} registered_size={width}x{height} registered_bytes={byteCount}" +
            $" live_size={image.Width}x{image.Height} array_length={image.ArrayLength}" +
            $" native_format={image.Format}/{(uint)image.Format}" +
            $" live_initialized={image.Initialized} live_gpu_written={image.GpuWritten}" +
            $" live_content_version={image.ContentVersion}";
        var primary = new NotSupportedException(message);
        if (!_imageProvenanceEnabled) return primary;
        try
        {
            var origin = image.DiagnosticOrigin;
            var mutation = image.DiagnosticMutation;
            long advertisedSequence;
            lock (_gate) _guestImageWorkSequences.TryGetValue(address, out advertisedSequence);
            return new NotSupportedException(message +
                $" diagnostic_creation_kind={origin.Work.Kind ?? "unrecorded"} creation_sequence={origin.Work.Sequence}" +
                $" creation_queue='{origin.Work.Queue.Name}' creation_submission={origin.Work.Queue.SubmissionId}" +
                $" creation_address=0x{origin.Address:X} creation_size={origin.Width}x{origin.Height}" +
                $" creation_pitch={origin.Pitch} creation_tile={origin.Tile} creation_guest_format={origin.Format}/{origin.Number}" +
                $" creation_guest_type={origin.Type} creation_base_mip={origin.BaseMip} creation_selected_mip={origin.SelectedMip}" +
                $" creation_resource_mips={origin.ResourceMips} creation_source_bytes={origin.SourceBytes}" +
                $" creation_raw={origin.Raw?.ToString() ?? "unavailable"}" +
                $" creation_color_registers=[{origin.Color?.ToString() ?? "unavailable"}]" +
                $" mutation_kind={mutation.Kind ?? "unrecorded"} mutation_sequence={mutation.Sequence}" +
                $" mutation_queue='{mutation.Queue.Name}' mutation_submission={mutation.Queue.SubmissionId}" +
                $" advertised_sequence={advertisedSequence}" +
                $" executing_sequence={Volatile.Read(ref _executingGuestWorkSequence)}.");
        }
        catch { return primary; } // Preserve the strict veto if optional diagnostics fail.
    }

    private static NotSupportedException StorageImageMismatch(string message, GuestImage image, GuestDrawTexture requested)
    {
        var primary = new NotSupportedException(message);
        if (!_imageProvenanceEnabled) return primary;
        try
        {
            var origin = image.DiagnosticOrigin;
            var mutation = image.DiagnosticMutation;
            return new NotSupportedException(message +
                $" diagnostic_creation_kind={origin.Work.Kind ?? "unrecorded"} creation_sequence={origin.Work.Sequence}" +
                $" creation_queue='{origin.Work.Queue.Name}' creation_submission={origin.Work.Queue.SubmissionId}" +
                $" creation_address=0x{origin.Address:X} creation_size={origin.Width}x{origin.Height}" +
                $" creation_pitch={origin.Pitch} creation_tile={origin.Tile} creation_guest_format={origin.Format}/{origin.Number}" +
                $" creation_guest_type={origin.Type} creation_base_mip={origin.BaseMip} creation_selected_mip={origin.SelectedMip}" +
                $" creation_resource_mips={origin.ResourceMips} creation_source_bytes={origin.SourceBytes}" +
                $" creation_raw={origin.Raw?.ToString() ?? "unavailable"} creation_color_registers=[{origin.Color?.ToString() ?? "unavailable"}]" +
                $" mutation_kind={mutation.Kind ?? "unrecorded"} mutation_sequence={mutation.Sequence}" +
                $" mutation_queue='{mutation.Queue.Name}' mutation_submission={mutation.Queue.SubmissionId}" +
                $" live_initialized={image.Initialized} live_gpu_written={image.GpuWritten} live_content_version={image.ContentVersion}" +
                $" incoming_pitch={requested.Pitch} incoming_tile={requested.TileMode} incoming_resource_mips={requested.ResourceMipLevels}" +
                $" incoming_source_bytes={requested.SourceByteCount} incoming_generation={requested.WriteGeneration}" +
                $" incoming_cpu_stable={requested.CpuSnapshotStable} incoming_access={requested.Access}" +
                $" incoming_raw={ImageDiagnosticRaw(requested.Descriptor)?.ToString() ?? "unavailable"}.");
        }
        catch { return primary; } // Optional diagnostic failure must preserve the original strict veto.
    }
}
