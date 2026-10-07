// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal;

using SharpEmu.Libs.Gpu.Scheduling;

internal static partial class MetalVideoPresenter
{
    // Encoded means the producer's immutable-copy commands are ordered on the
    // one serial Metal queue. Native command completion is a separate lifetime
    // event. This private contract never crosses into shared guest DTOs.
    private enum TextureSourceDependencyState { Pending, Encoded, Failed, Missing }
    private readonly record struct TextureSourceDependency(
        long ProducerSequence, string ProducerQueue, TextureSourceDependencyState State, string Context);

    // The inactive foundation currently has captured input owners only. Active
    // admission must additionally supply each writer's initialization/WAW owner
    // before source publication or dependency selection can be enabled.
    private static TextureSourceDependency[] GetGuestTextureSourceDependenciesLocked(object work) =>
        TextureSourcesOf(work)
            .Select(static lease => new TextureSourceDependency(
                lease.Version.ProducerSequence,
                lease.Version.ProducerQueue.Name,
                lease.Version.Failed || lease.References <= 0 ? TextureSourceDependencyState.Failed :
                    lease.Version.Completed ? TextureSourceDependencyState.Encoded : TextureSourceDependencyState.Pending,
                $"image=0x{lease.Version.View.Address:X} version={lease.Version.Identity}"))
            .DistinctBy(static dependency => (dependency.ProducerSequence, dependency.Context))
            .ToArray();

    private static bool GuestTextureSourcesReadyLocked(in PendingGuestWork pending)
    {
        var blocked = false;
        foreach (var dependency in GetGuestTextureSourceDependenciesLocked(pending.Work))
        {
            if (dependency.ProducerSequence <= 0 || dependency.ProducerSequence > _enqueuedGuestWorkSequence ||
                string.IsNullOrEmpty(dependency.ProducerQueue))
                FailGuestTextureSourceSelectionLocked(pending, $"invalid producer identity: {dependency}");
            if (dependency.ProducerSequence == pending.Sequence)
                FailGuestTextureSourceSelectionLocked(pending, $"work depends on its own output: {dependency}");
            switch (dependency.State)
            {
                case TextureSourceDependencyState.Encoded:
                    // Safe immutable-copy encoding on the serial Metal queue
                    // permits the next encoder; hardware completion retains
                    // independent ownership and is not required here.
                    break;
                case TextureSourceDependencyState.Pending:
                    if (!_pendingGuestWorkByQueue.TryGetValue(dependency.ProducerQueue, out var producerQueue) ||
                        !producerQueue.Any(work => work.Sequence == dependency.ProducerSequence))
                        FailGuestTextureSourceSelectionLocked(pending, $"pending producer is no longer queued and has no encoded source: {dependency}");
                    if (dependency.ProducerQueue == pending.Queue.Name)
                        FailGuestTextureSourceSelectionLocked(pending, $"producer is behind this head in its FIFO queue: {dependency}");
                    blocked = true;
                    break;
                case TextureSourceDependencyState.Failed:
                case TextureSourceDependencyState.Missing:
                    FailGuestTextureSourceSelectionLocked(pending, $"required source is {dependency.State}: {dependency}");
                    break;
                default:
                    FailGuestTextureSourceSelectionLocked(pending, $"unknown source readiness: {dependency}");
                    break;
            }
        }
        return !blocked;
    }

    private static void FailGuestTextureSourceSelectionLocked(in PendingGuestWork pending, string reason, Exception? cause = null)
    {
        var failure = cause ?? new InvalidOperationException(reason);
        _guestWorkFailure = failure;
        Monitor.PulseAll(_gate);
        // The creating autorelease pool owns this borrowed command buffer.
        // A failed selection must not commit commands encoded before failure.
        _batchOpen = false;
        _batchCommandBuffer = 0;
        InvalidateTextureSourceVersionsLocked();
        throw GpuFatal.Fatal(
            $"Metal guest texture dependency failed: kind={pending.Work.GetType().Name}" +
            $" sequence={pending.Sequence} queue='{pending.Queue.Name}'" +
            $" submission={pending.Queue.SubmissionId}: {reason}", failure);
    }
}
