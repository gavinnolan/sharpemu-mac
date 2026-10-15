// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Gpu.Metal.V2;

// One-shot warnings and the environment switches of the v2 backend. Anything v2
// cannot do exactly is reported once by key, never per draw and never fatally.
internal static class MetalV2Diagnostics
{
    private static readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    public static readonly bool Verbose =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_TRACE"), "1", StringComparison.Ordinal);

    // Naming a range that has to be staged costs two extra backing lookups, so it is
    // on only while someone is hunting ring fallbacks. Defaults on under the trace
    // switch; SHARPEMU_METAL_V2_REPORT_UNBACKED=1 turns it on by itself.
    public static readonly bool ReportUnbackedRanges = Verbose ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_REPORT_UNBACKED"), "1", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_IGNORE_TESTS=1 drops face culling, the depth compare and the
    // stencil test from every draw. A pass whose target never changes either had its
    // fragments thrown away by one of those three or never had a fragment to throw
    // away, and nothing in a target dump tells the two apart.
    public static readonly bool IgnoreFragmentTests =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_IGNORE_TESTS"), "1", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_REPORT_VERTEX_LAYOUT=1 keeps the vertex descriptor each
    // pipeline was built with, for the per-draw report: a stride or an attribute format
    // that does not match what the program fetches is invisible in everything else.
    public static readonly bool ReportVertexLayout =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_REPORT_VERTEX_LAYOUT"), "1", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_DEFER_STORE=1 lets a pass wait until it knows what follows it
    // before naming its store actions, so a store the next operation clears outright is
    // discarded. Off by default on the measurement: across Octopath and Sarah not one
    // store was ever proven dead — no pass in either ends because a clear arrived, and
    // the clears that do happen are materialising a surface about to be sampled rather
    // than one just rendered into. Asking Metal for MTLStoreActionUnknown changes what
    // the driver can decide before the pass runs, and that is not worth paying for a
    // saving neither title has.
    public static readonly bool DeferStoreActions =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DEFER_STORE"), "1", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_DEFER_LABELS=0 makes an end-of-pipe label's bytes end the open
    // render pass to be copied, as they did before. A label is four or eight bytes and
    // a pass end on a tile-based GPU costs the whole framebuffer twice, so the copy
    // waits for a boundary that was going to happen anyway — but it also means the
    // guest sees the label slightly later, so the old behaviour stays reachable.
    public static readonly bool DeferLabelWrites =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DEFER_LABELS"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_DEFER_COMPUTE_COPIES=0 makes an end-of-pipe label or a
    // command-processor write recorded while a compute encoder is open end that encoder to
    // be copied, as it did before. Games put a label or a WRITE_DATA after nearly every
    // dispatch, and ending the encoder for each one split a frame into a hundred compute
    // encoders whose boundaries cost more GPU time than the dispatches they held.
    public static readonly bool DeferComputeCopies =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DEFER_COMPUTE_COPIES"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_KERNEL_FILLS=0 runs a title's own compute memset or memcpy over the
    // exact bytes of a cached image as the dispatch it is, with the writeback before it
    // and the upload after it, instead of as a clear or texture copy of the image
    // (MetalImageCache.KernelFills).
    public static readonly bool KernelFills =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_KERNEL_FILLS"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_FOLD_CLEARS=0 encodes every clear as its own render pass the
    // moment it is asked for, the way it did before a clear could become the load action
    // of the pass that follows it. A fast-clear that is materialised and then rendered
    // into costs a 4K store and a 4K load for a result the load action gives away, which
    // is what the switch exists to be able to measure.
    // SHARPEMU_METAL_V2_COPY_READ_THROUGH=0 makes a host read of bytes a title's compute
    // memcpy is still writing wait for that submission, as before. Otherwise the read is
    // answered from the copy's source words when nothing later writes the range and the
    // source itself is not pending (MetalRenderHost.CopyReads).
    public static readonly bool CopyReadThrough =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_COPY_READ_THROUGH"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_DATA_LOADS_DEVICE=0 keeps every data-only scalar load flattened into
    // the host-evaluated table, as before. Otherwise a load whose dword the GPU is still
    // writing when the draw is bound (a constant table copied on the GPU) makes that shader
    // be planned again with the load read on the device, instead of the host waiting for the
    // writer's submission on every bind.
    public static readonly bool DataLoadsOnDevice =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DATA_LOADS_DEVICE"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_WAIT_TRACE=1 (or SHARPEMU_METAL_V2_EOP_TRACE=1) names, on each
    // SLOW-WAIT line, the shader whose resources were being materialized when the host waited.
    public static readonly bool WaitTrace =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_WAIT_TRACE"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_EOP_TRACE"), "1", StringComparison.Ordinal);

    public static readonly bool FoldClearsIntoPasses =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_FOLD_CLEARS"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_FLUSH_COALESCE=0 commits the command buffer at the end of every
    // guest submission that made progress, as it did before. Each commit leaves the GPU
    // idle until the next buffer is scheduled; on Demon's Souls' title screen ~17 commits
    // a frame were made only because a submission ended. The three limits below bound
    // how long a coalesced buffer stays open (submissions, encoders, microseconds).
    // SHARPEMU_METAL_V2_ELIDE_LOADS=0 loads every colour attachment a pass does not clear,
    // as before. Otherwise a pass begun for a draw that provably overwrites every pixel of
    // an attachment does not read it into tile memory first (MetalPassLoads).
    public static readonly bool ElideLoads =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_ELIDE_LOADS"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_SKIP_OVERWRITTEN_REFRESH=0 uploads a CPU-dirty render target when it
    // is acquired, as before. Otherwise the upload waits for the pass to begin and is
    // dropped when that pass clears the whole image or its first draw overwrites it
    // (MetalImageCache.DeferredRefresh). The switch turns off the wait and the drop together.
    public static readonly bool SkipOverwrittenRefresh =
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_SKIP_OVERWRITTEN_REFRESH"), "0", StringComparison.Ordinal);

    public static readonly bool CoalesceFlushes =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_FLUSH_COALESCE"), "0", StringComparison.Ordinal);

    public static readonly int CoalesceMaxSubmissions =
        PositiveInt("SHARPEMU_METAL_V2_FLUSH_COALESCE_SUBMISSIONS", 8);

    public static readonly int CoalesceMaxEncoders =
        PositiveInt("SHARPEMU_METAL_V2_FLUSH_COALESCE_ENCODERS", 64);

    public static readonly int CoalesceMaxMicroseconds =
        PositiveInt("SHARPEMU_METAL_V2_FLUSH_COALESCE_US", 2000);

    private static int PositiveInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    // ConcurrentDictionary.TryAdd takes the bucket lock even when the key exists, and
    // the hot bind paths ask every time; the lock-free lookup answers the repeats.
    public static void WarnOnce(string key, string message)
    {
        if (!_reported.ContainsKey(key) && _reported.TryAdd(key, 0))
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] {message}");
        }
    }

    public static void InfoOnce(string key, string message)
    {
        if (!_reported.ContainsKey(key) && _reported.TryAdd(key, 0))
        {
            Console.Error.WriteLine($"[METAL-V2][INFO] {message}");
        }
    }

    public static void Trace(string message)
    {
        if (Verbose)
        {
            Console.Error.WriteLine($"[METAL-V2][TRACE] {message}");
        }
    }
}
