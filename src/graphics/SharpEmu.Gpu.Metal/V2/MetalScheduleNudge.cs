// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Calls <c>-waitUntilScheduled</c> on committed command buffers from a thread of its
/// own, so the driver is asked to take a label-carrying submission now without the
/// GPU worker waiting for the answer.
/// </summary>
/// <remarks>
/// "Scheduled" is not the moment the driver picks a buffer up. Measured on Silent Hill's
/// title, a buffer with 77 ms of GPU work returned from <c>-waitUntilScheduled</c> 75 ms
/// after its commit, and one queued behind 43 ms of earlier work returned 40 ms after it:
/// the call returns when the kernel has finished feeding the buffer to the GPU, which for
/// a large buffer is close to when the GPU finishes it. On the worker that was 1.7 s of
/// every 3 s spent stopped while the GPU ran, with nothing recorded for the GPU to start
/// on next.
/// </remarks>
internal sealed class MetalScheduleNudge : IDisposable
{
    private static readonly nint SelWaitUntilScheduled = MetalNative.Selector("waitUntilScheduled");
    private static readonly nint SelRetain = MetalNative.Selector("retain");
    private static readonly nint SelRelease = MetalNative.Selector("release");

    private readonly BlockingCollection<nint> _buffers = new(new ConcurrentQueue<nint>());
    private readonly Thread _thread;

    public MetalScheduleNudge()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu Metal v2 schedule nudge",
        };
        _thread.Start();
    }

    /// <summary>Asks the driver to schedule a committed command buffer, without waiting.</summary>
    public void Nudge(nint commandBuffer)
    {
        MetalNative.Send(commandBuffer, SelRetain);
        try
        {
            if (_buffers.TryAdd(commandBuffer))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // Disposed: nothing is nudged any more.
        }

        MetalNative.SendVoid(commandBuffer, SelRelease);
    }

    private void Run()
    {
        _ = SharpEmu.HLE.Host.HostThreadQualityOfService.TryApplyToCurrentThread(
            SharpEmu.HLE.Host.HostThreadQos.UserInteractive);
        foreach (var buffer in _buffers.GetConsumingEnumerable())
        {
            MetalNative.SendVoid(buffer, SelWaitUntilScheduled);
            MetalNative.SendVoid(buffer, SelRelease);
        }
    }

    public void Dispose()
    {
        _buffers.CompleteAdding();
        _ = _thread.Join(TimeSpan.FromSeconds(1));
    }
}
