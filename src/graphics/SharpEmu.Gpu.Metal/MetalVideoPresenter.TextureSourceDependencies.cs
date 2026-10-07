// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal;

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
}
