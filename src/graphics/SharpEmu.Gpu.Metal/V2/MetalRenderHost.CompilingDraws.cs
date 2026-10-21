// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;

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
/// Wide mode (SHARPEMU_METAL_V2_ASYNC_GRAPHICS=wide, the default for Grand Theft Auto V) fits a
/// deferred renderer whose G-buffer is cleared nowhere but its depth. It also leaves out draws
/// into colour targets that passes only drew into, not rebuilt, in each of those frames; draws
/// that write depth or stencil, into a depth target every frame clears the depth of, its stencil
/// loaded or not; and draws whose only writes outside their targets are mip statistics (the
/// texture streamer's feedback, gathered over frames). A left-out object's pixels keep last
/// frame's G-buffer and stencil values under a cleared depth that marks them empty, so the object
/// is only missing (pop-in) while its pipeline compiles. Grand Theft Auto V clears depth each frame
/// and keeps stencil (no pass of a 2 s window clears both, 150-300 clear depth alone), and most of
/// its pixel programs write mip statistics: without wide mode it leaves out nothing and waits
/// ~190 ms on each new library walking into a new area (Los Santos intro, every library compiled
/// cold: 51 s of library waits, 23 s with it).
/// </summary>
internal sealed partial class MetalRenderHost
{
    private static readonly string? AsynchronousGraphicsMode =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_ASYNC_GRAPHICS");

    private static readonly bool AsynchronousGraphicsDefault = AsynchronousGraphicsMode != "0";

    // Wide mode is the default for the titles below, whose renderers it fits (Grand Theft Auto V: depth
    // cleared every frame, shadow cascades redrawn whole); not where a target a pass draws into every frame
    // can still be a cache updated in place, like UE5's virtual shadow maps. =wide turns it on for any
    // title, =1 off for these.
    private static readonly HashSet<string> WideTitles = new(StringComparer.Ordinal) { "PPSA04264", "PPSA03420" };

    private static bool WideDefault() => AsynchronousGraphicsMode switch
    {
        "wide" => true,
        null or "" => WideTitles.Contains(VideoOutBackendServices.GetApplicationTitleId()),
        _ => false,
    };

    /// <summary>The frames, this one included, a target has to have been rebuilt in.</summary>
    internal const int RebuiltFrames = 3;

    internal readonly record struct TargetSubresource(nint Texture, uint Level, uint Layer);

    // The flip a target subresource was last rebuilt in, and a bit per frame back from it.
    private readonly Dictionary<TargetSubresource, (long Flip, uint Frames)> _rebuiltTargets = new();

    // The same for colour targets a pass drew into at all, and depth targets a pass began by
    // clearing the depth of (wide mode only).
    private readonly Dictionary<TargetSubresource, (long Flip, uint Frames)> _drawnTargets = new();
    private readonly Dictionary<TargetSubresource, (long Flip, uint Frames)> _depthClearedTargets = new();

    // Why the host's part refused a draw it was asked about (each window, for the PERF line).
    private long _leaveOutAllowed;
    private long _leaveOutRefusedColor;
    private long _leaveOutRefusedDepth;
    private long _leaveOutRefusedClears;
    private long _flips;
    private long _rebuiltTargetsPruned;
    private long _drawsSkippedCompiling;
    private long _pipelineWaitsBlocking;

    /// <summary>Whether draws may be left out while their pipeline compiles; tests set it.</summary>
    internal bool AsynchronousGraphics { get; set; } = AsynchronousGraphicsDefault;

    /// <summary>Wide mode: depth writes, and colour targets drawn into every frame; tests set it.</summary>
    internal bool AsynchronousWide { get; set; } = WideDefault();

    /// <summary>Draws left out while their pipeline compiled, and pipelines the worker waited for.</summary>
    internal (long Skipped, long Waits) CompilingDrawCounters =>
        (Interlocked.Read(ref _drawsSkippedCompiling), Interlocked.Read(ref _pipelineWaitsBlocking));

    public bool LeavesOutDrawsWhileCompiling => AsynchronousGraphics;

    public bool LeavesOutDepthWritesWhileCompiling => AsynchronousGraphics && AsynchronousWide;

    public bool LeavesOutMipStatisticsWhileCompiling => AsynchronousGraphics && AsynchronousWide;

    /// <summary>The host's leave-out answers since the last call: allowed, and refused for a colour target, the depth target, a pending clear.</summary>
    internal (long Allowed, long Color, long Depth, long Clears) TakeLeaveOutCounters() =>
        (Interlocked.Exchange(ref _leaveOutAllowed, 0), Interlocked.Exchange(ref _leaveOutRefusedColor, 0),
         Interlocked.Exchange(ref _leaveOutRefusedDepth, 0), Interlocked.Exchange(ref _leaveOutRefusedClears, 0));

    private string FormatLeaveOutCounters()
    {
        var (allowed, color, depth, clears) = TakeLeaveOutCounters();
        return $"allowed={allowed} color={color} depth={depth} clears={clears} blocking_builds_by=({TakeBlockingBuildReasons()})";
    }

    /// <summary>The compiler threads; tests hold their gate.</summary>
    internal MetalPipelineCompiler PipelineCompiler => Compiler;

    // The reason the last draw asked about was refused, and the pipelines built blocking by it.
    private string _lastLeaveOutRefusal = "none";
    private readonly ConcurrentDictionary<string, long> _blockingBuildsByRefusal = new();

    public void NoteLeaveOutRefused(string reason) => _lastLeaveOutRefusal = reason;

    // A pipeline the worker builds itself: what kept its draw from being left out.
    private void NoteBlockingBuild() => _blockingBuildsByRefusal.AddOrUpdate(_lastLeaveOutRefusal, 1, static (_, count) => count + 1);

    internal string TakeBlockingBuildReasons()
    {
        var parts = new List<string>();
        foreach (var reason in _blockingBuildsByRefusal.Keys)
        {
            if (_blockingBuildsByRefusal.TryRemove(reason, out var count) && count != 0)
            {
                parts.Add($"{reason}={count}");
            }
        }

        return string.Join(" ", parts);
    }

    public bool MayLeaveOutWhileCompiling(in RenderingState rendering)
    {
        if (!AsynchronousGraphics)
        {
            return false;
        }

        _lastLeaveOutRefusal = "host";

        if (_pendingClears.Count != 0)
        {
            Interlocked.Increment(ref _leaveOutRefusedClears);
            _lastLeaveOutRefusal = "pending-clear";
            return false;
        }

        for (var index = 0; index < rendering.ColorAttachmentCount; index++)
        {
            if (index >= _attachmentCount || index >= MaxColorAttachments)
            {
                Interlocked.Increment(ref _leaveOutRefusedColor);
                _lastLeaveOutRefusal = "color-target";
                return false;
            }

            var target = new TargetSubresource(_attachmentTextures[index], _attachmentRealLevels[index], _attachmentRealLayers[index]);
            if (!RebuiltEveryFrame(target) && !(AsynchronousWide && EveryFrame(_drawnTargets, target)))
            {
                Interlocked.Increment(ref _leaveOutRefusedColor);
                _lastLeaveOutRefusal = "color-not-every-frame";
                return false;
            }
        }

        ref readonly var depthStencil = ref rendering.DepthStencilAttachment;
        var depthTarget = new TargetSubresource(_depthAttachmentTexture, _depthAttachmentRealLevel, _depthAttachmentRealLayer);
        if (depthStencil.View.Handle != 0 && (depthStencil.HasDepth || depthStencil.HasStencil) &&
            !RebuiltEveryFrame(depthTarget) &&
            !(AsynchronousWide && depthStencil.HasDepth && DepthClearedEveryFrame(depthTarget)))
        {
            Interlocked.Increment(ref _leaveOutRefusedDepth);
            _lastLeaveOutRefusal = "depth-not-every-frame";
            return false;
        }

        Interlocked.Increment(ref _leaveOutAllowed);
        _lastLeaveOutRefusal = "allowed";
        return true;
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

    // A pass began with this colour attachment (wide mode).
    private void NoteColorTargetDrawn(int index)
    {
        if (AsynchronousWide && index < _attachmentCount && index < MaxColorAttachments)
        {
            NoteTargetIn(_drawnTargets, new TargetSubresource(
                _attachmentTextures[index], _attachmentRealLevels[index], _attachmentRealLayers[index]));
        }
    }

    // A pass began by clearing the depth of its depth/stencil attachment, its stencil loaded or not (wide mode).
    private void NoteDepthCleared() =>
        NoteDepthCleared(new TargetSubresource(_depthAttachmentTexture, _depthAttachmentRealLevel, _depthAttachmentRealLayer));

    internal void NoteDepthCleared(TargetSubresource target)
    {
        if (AsynchronousWide)
        {
            NoteTargetIn(_depthClearedTargets, target);
        }
    }

    internal bool DepthClearedEveryFrame(TargetSubresource target) => EveryFrame(_depthClearedTargets, target);

    // A pass began by clearing the depth/stencil attachment.
    private void NoteDepthTargetRebuilt() =>
        NoteTargetRebuilt(new TargetSubresource(_depthAttachmentTexture, _depthAttachmentRealLevel, _depthAttachmentRealLayer));

    internal void NoteTargetRebuilt(TargetSubresource target) => NoteTargetIn(_rebuiltTargets, target);

    internal bool RebuiltEveryFrame(TargetSubresource target) => EveryFrame(_rebuiltTargets, target);

    private void NoteTargetIn(Dictionary<TargetSubresource, (long Flip, uint Frames)> targets, TargetSubresource target)
    {
        if (target.Texture == 0 || !AsynchronousGraphics)
        {
            return;
        }

        var flip = Interlocked.Read(ref _flips);
        PruneRebuiltTargets(flip);
        targets[target] = targets.TryGetValue(target, out var seen)
            ? (flip, Shift(seen.Frames, flip - seen.Flip) | 1)
            : (flip, 1u);
    }

    private bool EveryFrame(Dictionary<TargetSubresource, (long Flip, uint Frames)> targets, TargetSubresource target)
    {
        const uint every = (1u << RebuiltFrames) - 1;
        var flip = Interlocked.Read(ref _flips);
        return targets.TryGetValue(target, out var seen) && (Shift(seen.Frames, flip - seen.Flip) & every) == every;
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
        Prune(_rebuiltTargets, flip);
        Prune(_drawnTargets, flip);
        Prune(_depthClearedTargets, flip);
    }

    private static void Prune(Dictionary<TargetSubresource, (long Flip, uint Frames)> targets, long flip)
    {
        List<TargetSubresource>? stale = null;
        foreach (var (target, seen) in targets)
        {
            if (flip - seen.Flip >= RebuiltFrames)
            {
                (stale ??= []).Add(target);
            }
        }

        foreach (var target in stale ?? [])
        {
            targets.Remove(target);
        }
    }
}
