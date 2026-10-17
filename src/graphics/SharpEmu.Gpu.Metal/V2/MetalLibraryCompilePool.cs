// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The threads the runtime's shader modules compile their MSL libraries on, so the GPU
/// worker hands a library to the driver's front end and goes on with the draw's other
/// program instead of waiting for each in turn (MetalRenderHost.CreateShaderModule). A
/// front-end call blocks its thread for the whole compile, tens of milliseconds to
/// seconds, so the pool has its own threads rather than the .NET pool's. First come,
/// first served. SHARPEMU_METAL_V2_ASYNC_LIBRARIES=0 compiles on the caller again.
/// </summary>
internal sealed class MetalLibraryCompilePool
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_ASYNC_LIBRARIES") != "0";

    private static readonly Lazy<MetalLibraryCompilePool> _shared = new(() => new MetalLibraryCompilePool(ThreadCount));

    private readonly BlockingCollection<(Action Work, TaskCompletionSource Done)> _queue = new();

    private MetalLibraryCompilePool(int threads)
    {
        for (var index = 0; index < threads; index++)
        {
            new Thread(Run)
            {
                IsBackground = true,
                Name = $"SharpEmu Metal v2 library compile {index}",
            }.Start();
        }
    }

    /// <summary>The process's pool, started on first use.</summary>
    public static MetalLibraryCompilePool Shared => _shared.Value;

    /// <summary>
    /// As many compiles in flight as the shader prewarm keeps: the driver's front end ran
    /// Demon's Souls' computes no faster with more (see MetalShaderPrewarm.WorkerCount).
    /// </summary>
    internal static int ThreadCount => MetalShaderPrewarm.WorkerCount;

    /// <summary>Queues <paramref name="work"/>; the task ends with it, or with its exception.</summary>
    public Task Run(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((work, done));
        return done.Task;
    }

    private void Run()
    {
        foreach (var (work, done) in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
                done.SetResult();
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        }
    }
}
