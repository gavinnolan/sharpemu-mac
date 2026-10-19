// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The host's part in leaving a draw out while its pipeline compiles
/// (RenderExecutor.MayLeaveOutWhileCompiling): which targets are rebuilt from nothing every
/// frame. A target subresource is, here, one a pass began by clearing or by overwriting it
/// whole (a clear load action, or a colour load the first draw made dead, MetalPassLoads) in
/// the frame being recorded and in each of the <see cref="RebuiltFrames"/> - 1 frames before
/// it. A draw into such targets that is left out is missing from this frame alone; the next
/// one rebuilds them and draws it. Anything else - a look-up table made once, a shadow or
/// virtual-texture cache updated in place, a capture, a history buffer, a target only ever
/// loaded - keeps its draws however long their pipelines take. Nothing is left out before
/// the title has flipped <see cref="RebuiltFrames"/> times, nor while a clear waits to be
/// folded into the next pass.
/// SHARPEMU_METAL_V2_ASYNC_GRAPHICS=0 waits for every graphics pipeline, as before.
/// </summary>
internal sealed partial class MetalRenderHost
{
    private static readonly bool AsynchronousGraphicsDefault =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_ASYNC_GRAPHICS") != "0";

    /// <summary>The frames, this one included, a target has to have been rebuilt in.</summary>
    internal const int RebuiltFrames = 3;

    internal readonly record struct TargetSubresource(nint Texture, uint Level, uint Layer);

    // The flip a target subresource was last rebuilt in, and a bit per frame back from it.
    private readonly Dictionary<TargetSubresource, (long Flip, uint Frames)> _rebuiltTargets = new();
    private long _flips;
    private long _rebuiltTargetsPruned;
    private long _drawsSkippedCompiling;
    private long _pipelineWaitsBlocking;

    /// <summary>Whether draws may be left out while their pipeline compiles; tests set it.</summary>
    internal bool AsynchronousGraphics { get; set; } = AsynchronousGraphicsDefault;

    /// <summary>Draws left out while their pipeline compiled, and pipelines the worker waited for.</summary>
    internal (long Skipped, long Waits) CompilingDrawCounters =>
        (Interlocked.Read(ref _drawsSkippedCompiling), Interlocked.Read(ref _pipelineWaitsBlocking));

    public bool LeavesOutDrawsWhileCompiling => AsynchronousGraphics;

    /// <summary>The compiler threads; tests hold their gate.</summary>
    internal MetalPipelineCompiler PipelineCompiler => Compiler;

    public bool MayLeaveOutWhileCompiling(in RenderingState rendering)
    {
        if (!AsynchronousGraphics || _pendingClears.Count != 0)
        {
            return false;
        }

        for (var index = 0; index < rendering.ColorAttachmentCount; index++)
        {
            if (index >= _attachmentCount || index >= MaxColorAttachments ||
                !RebuiltEveryFrame(new TargetSubresource(
                    _attachmentTextures[index], _attachmentRealLevels[index], _attachmentRealLayers[index])))
            {
                return false;
            }
        }

        ref readonly var depthStencil = ref rendering.DepthStencilAttachment;
        return depthStencil.View.Handle == 0 || !(depthStencil.HasDepth || depthStencil.HasStencil) ||
            RebuiltEveryFrame(new TargetSubresource(_depthAttachmentTexture, _depthAttachmentRealLevel, _depthAttachmentRealLayer));
    }

    // A flip: the frame the next pass belongs to is a new one. Also drops the compiles of
    // draws left out and never drawn again (MetalPipelineCompiler.DropAbandoned).
    internal void NoteFlipForCompilingDraws()
    {
        Interlocked.Increment(ref _flips);
        _compiler?.DropAbandoned(System.Diagnostics.Stopwatch.GetTimestamp() - 10 * System.Diagnostics.Stopwatch.Frequency);
    }

    // A pass began by clearing this colour attachment or overwriting it whole.
    private void NoteColorTargetRebuilt(int index)
    {
        if (index < _attachmentCount && index < MaxColorAttachments)
        {
            NoteTargetRebuilt(new TargetSubresource(
                _attachmentTextures[index], _attachmentRealLevels[index], _attachmentRealLayers[index]));
        }
    }

    // A pass began by clearing the depth/stencil attachment.
    private void NoteDepthTargetRebuilt() =>
        NoteTargetRebuilt(new TargetSubresource(_depthAttachmentTexture, _depthAttachmentRealLevel, _depthAttachmentRealLayer));

    internal void NoteTargetRebuilt(TargetSubresource target)
    {
        if (target.Texture == 0 || !AsynchronousGraphics)
        {
            return;
        }

        var flip = Interlocked.Read(ref _flips);
        PruneRebuiltTargets(flip);
        _rebuiltTargets[target] = _rebuiltTargets.TryGetValue(target, out var seen)
            ? (flip, Shift(seen.Frames, flip - seen.Flip) | 1)
            : (flip, 1u);
    }

    internal bool RebuiltEveryFrame(TargetSubresource target)
    {
        const uint every = (1u << RebuiltFrames) - 1;
        var flip = Interlocked.Read(ref _flips);
        return _rebuiltTargets.TryGetValue(target, out var seen) && (Shift(seen.Frames, flip - seen.Flip) & every) == every;
    }

    private static uint Shift(uint frames, long flips) => flips >= 32 ? 0 : frames << (int)flips;

    // Targets not rebuilt for as many frames as the rule looks back can no longer qualify.
    private void PruneRebuiltTargets(long flip)
    {
        if (flip == _rebuiltTargetsPruned)
        {
            return;
        }

        _rebuiltTargetsPruned = flip;
        List<TargetSubresource>? stale = null;
        foreach (var (target, seen) in _rebuiltTargets)
        {
            if (flip - seen.Flip >= RebuiltFrames)
            {
                (stale ??= []).Add(target);
            }
        }

        foreach (var target in stale ?? [])
        {
            _rebuiltTargets.Remove(target);
        }
    }
}
