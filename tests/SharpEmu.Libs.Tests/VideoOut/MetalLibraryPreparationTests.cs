// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Actual command-worker→Metal backend preparation→renderer admission. Library
// callback is managed-only, so these rows never create/dereference native handles.
[Collection(SchedulingStateCollection.Name)]
public sealed class MetalLibraryPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualWorkerPreparesOriginalShaderIdentitiesBeforeRendererAdmission(bool compute)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var worker = new CommandStreamWorker(queue, host, static () => false, true, "Metal managed preparation");
        var backend = Backend(queue, worker);
        var vs = Shader("vs", Gen5MslStage.Vertex); var shader = Shader("target", compute ? Gen5MslStage.Compute : Gen5MslStage.Pixel);
        var prepared = new List<MetalCompiledGuestShader>();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var field = PresenterField("_prepareLibraryOverride"); var previous = field.GetValue(null);
        field.SetValue(null, (Action<MetalCompiledGuestShader, uint?>)((actual, fixedAttributes) =>
        {
            Assert.Null(fixedAttributes); Assert.Equal(0, PresenterField("_pendingGuestWorkCount").GetValue(null));
            prepared.Add(actual);
            if (ReferenceEquals(actual, shader)) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        }));
        var invoked = false;
        host.BeforeGuestRead = _ =>
        {
            if (invoked) return; invoked = true;
            Submit(backend, shader, vs, compute);
        };
        var words = StreamRunner.Packet(PacketOpcode.NumInstances, 3);
        host.WriteWords(StreamRunner.CommandAddress, words);
        var receipt = queue.EnqueueWithPreparation(0, StreamRunner.CommandAddress, (uint)words.Length, 71, null);
        worker.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, PresenterField("_pendingGuestWorkCount").GetValue(null));
            var wait = Task.Run(receipt.WaitUntilPrepared);
            Assert.False(wait.IsCompleted); release.Set();
            Assert.Equal(GuestSubmissionPreparationResult.PreparedPrefix, await wait.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(compute ? new[] { shader } : new[] { vs, shader }, prepared);
            Assert.Equal(1, PresenterField("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0L, PresenterField("_completedGuestWorkSequence").GetValue(null));
            Assert.Equal((nint)0, shader.CachedLibrary); // Managed callback is no native-library receipt.
        }
        finally { release.Set(); try { worker.Stop(); } finally { field.SetValue(null, previous); } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualWorkerPreparationRefusalNeverAdmitsItsDrawOrDispatch(bool compute)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var worker = new CommandStreamWorker(queue, host, static () => false, true, "Metal preparation refusal");
        var backend = Backend(queue, worker);
        var shader = Shader("refused", compute ? Gen5MslStage.Compute : Gen5MslStage.Pixel);
        var original = new NotSupportedException("original cold native-library refusal boundary");
        var field = PresenterField("_prepareLibraryOverride"); var previous = field.GetValue(null);
        field.SetValue(null, (Action<MetalCompiledGuestShader, uint?>)((_, _) => throw original));
        host.BeforeGuestRead = _ => Submit(backend, shader, Shader("vs", Gen5MslStage.Vertex), compute);
        var words = StreamRunner.Packet(PacketOpcode.NumInstances, 3); host.WriteWords(StreamRunner.CommandAddress, words);
        var receipt = queue.EnqueueWithPreparation(0, StreamRunner.CommandAddress, (uint)words.Length, 72, null);
        worker.Start();
        try
        {
            Assert.Same(original, Assert.Throws<NotSupportedException>(() => receipt.WaitUntilPrepared()));
            Assert.Equal(0, PresenterField("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0L, PresenterField("_completedGuestWorkSequence").GetValue(null));
            Assert.Empty(host.EndOfPipeWrites);
        }
        finally { try { Assert.Same(original, Assert.Throws<NotSupportedException>(() => worker.Stop())); } finally { field.SetValue(null, previous); } }
    }

    private static MetalCompiledGuestShader Shader(string entry, Gen5MslStage stage) =>
        new(new Gen5MslShader("// managed boundary only", entry, stage, AttributeCount: 0));
    private static FieldInfo PresenterField(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static MetalGuestGpuBackend Backend(CommandStreamQueue queue, CommandStreamWorker worker)
    {
        // Skip backend constructor tracker warmup; every invoked method uses its
        // genuine injected worker/queue and production submission implementation.
        var backend = (MetalGuestGpuBackend)RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend));
        typeof(MetalGuestGpuBackend).GetField("_commandStream", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(backend, queue);
        typeof(MetalGuestGpuBackend).GetField("_commandStreamWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(backend, worker);
        return backend;
    }
    private static void Submit(MetalGuestGpuBackend backend, MetalCompiledGuestShader shader, MetalCompiledGuestShader vs, bool compute)
    {
        if (compute)
            Assert.True(backend.SubmitComputeDispatch(1, shader, [], [], 1, 1, 1, 0, 0, 0, 1, 1, 1, false, true) > 0);
        else backend.SubmitOffscreenTranslatedDraw(shader, [], [], 0, [new GuestRenderTarget(0x1000, 4, 4, 10, 0)], vs);
    }
}
