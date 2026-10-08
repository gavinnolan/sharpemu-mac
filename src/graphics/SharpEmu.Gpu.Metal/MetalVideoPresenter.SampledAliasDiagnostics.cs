// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Cached ordinary failure diagnostics only. No fault-path consumer or
    // new eligibility/content/native/physical ownership decision.
    private static bool _sampledAliasDiagnosticsEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_SAMPLED_ALIAS_DIAGNOSTICS") == "1";
    private static int _sampledAliasDiagnosticCount;
    private readonly record struct SampledAliasFirstRefusal(string Reason, bool Active,
        uint NativeType, uint Depth, uint Layers, uint Width, uint Height,
        MtlPixelFormat Format, bool Initialized, bool GpuWritten, int Version,
        ImageDiagnosticOrigin Creation, ImageDiagnosticWork Mutation,
        bool OriginalPresent, long OriginalSession, ulong OriginalFootprint,
        ulong OriginalFootprintBytes, ulong OriginalSource, ulong OriginalSourceBytes,
        bool OriginalWatchPresent);

    private static void CaptureSampledAliasFirstRefusalLocked(GuestImage image, bool active,
        string reason, ref SampledAliasFirstRefusal? first)
    {
        // Called under the existing matcher gate, before its first rejected
        // candidate disappears. Default off exits before optional metadata.
        if (!_sampledAliasDiagnosticsEnabled || first.HasValue || _sampledAliasDiagnosticCount >= 64) return;
        _sampledAliasDiagnosticCount++;
        var original = image.StorageCpuOrigin;
        first = new(reason, active, image.TextureType, image.Depth, image.ArrayLength,
            image.Width, image.Height, image.Format, image.Initialized, image.GpuWritten,
            image.ContentVersion, image.DiagnosticOrigin, image.DiagnosticMutation,
            original is not null, original?.Session ?? 0, original?.Mapping.FootprintAddress ?? 0,
            original?.Mapping.FootprintByteCount ?? 0, original?.Mapping.SourceAddress ?? 0,
            original?.Mapping.SourceByteCount ?? 0, original?.VolumeWatch is not null);
    }

    private static string AppendSampledAliasFirstRefusal(string primary, SampledAliasFirstRefusal? first)
    {
        if (!first.HasValue) return primary;
        try
        {
            var d = first.Value; var c = d.Creation; var m = d.Mutation;
            return primary + $" sampled_alias_first_refusal={d.Reason} candidate_active={d.Active}" +
                $" candidate_native_type={d.NativeType} candidate_depth={d.Depth} candidate_layers={d.Layers}" +
                $" candidate_size={d.Width}x{d.Height} candidate_format={d.Format}/{(uint)d.Format}" +
                $" candidate_initialized={d.Initialized} candidate_gpu_written={d.GpuWritten} candidate_version={d.Version}" +
                $" candidate_creation_kind={c.Work.Kind ?? "unrecorded"} candidate_creation_sequence={c.Work.Sequence}" +
                $" candidate_creation_queue='{c.Work.Queue.Name}' candidate_creation_submission={c.Work.Queue.SubmissionId}" +
                $" candidate_creation_address=0x{c.Address:X} candidate_creation_size={c.Width}x{c.Height}" +
                $" candidate_creation_pitch={c.Pitch} candidate_creation_tile={c.Tile} candidate_creation_format={c.Format}/{c.Number}" +
                $" candidate_creation_type={c.Type} candidate_creation_base_mip={c.BaseMip}" +
                $" candidate_creation_mip={c.SelectedMip} candidate_creation_resource_mips={c.ResourceMips}" +
                $" candidate_creation_source_bytes={c.SourceBytes} candidate_creation_raw={c.Raw?.ToString() ?? "unavailable"}" +
                $" candidate_mutation_kind={m.Kind ?? "unrecorded"} candidate_mutation_sequence={m.Sequence}" +
                $" candidate_mutation_queue='{m.Queue.Name}' candidate_mutation_submission={m.Queue.SubmissionId}" +
                $" candidate_original_present={d.OriginalPresent} candidate_original_session={d.OriginalSession}" +
                $" candidate_original_footprint=0x{d.OriginalFootprint:X} candidate_original_footprint_bytes={d.OriginalFootprintBytes}" +
                $" candidate_original_source=0x{d.OriginalSource:X} candidate_original_source_bytes={d.OriginalSourceBytes}" +
                $" candidate_original_watch_present={d.OriginalWatchPresent}" +
                " candidate_metadata_observation_only=True captured_at_first_matcher_refusal=True.";
        }
        catch { return primary; }
    }
}
