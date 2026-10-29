// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Gpu.Metal.Tests.Scenes;

namespace SharpEmu.PerfAudit;

/// <summary>
/// 8-raw. The floor under the emulator's encode phase: the same Objective-C message sends the emulator makes
/// (MetalNative.Send*), issued in a tight loop against a real device, plus commit and GPU wait, plus the cost of
/// the GPU-written-word wait the guest triggers when it reads a GPU-owned word.
/// </summary>
internal static unsafe class MetalRawBenches
{
    private const int Draws = 6000;
    private const int Dispatches = 2700;

    private const string Source = """
        #include <metal_stdlib>
        using namespace metal;
        struct V { float4 p [[position]]; };
        vertex V vs(uint vid [[vertex_id]], constant float4* a [[buffer(0)]]) {
            float2 c[3] = { float2(-1, -1), float2(3, -1), float2(-1, 3) };
            V v; v.p = float4(c[vid] * 0.0001 + a[0].xy, 0, 1); return v;
        }
        fragment half4 fs(V v [[stage_in]], constant float4* b [[buffer(0)]]) { return half4(b[0]); }
        kernel void k(device uint* o [[buffer(0)]], uint t [[thread_position_in_grid]]) { o[t] = t; }
        """;

    private static nint S(string name) => MetalNative.Selector(name);

    public static void Run()
    {
        Console.WriteLine("--- 8-raw. Raw Metal encode / commit / wait floor, GPU-written-word wait ---");
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        var queue = MetalNative.Send(device, S("newCommandQueue"));

        // Timer cost: the phase profile (RenderPhaseProfile) takes two of these per scope.
        long sinkTicks = 0;
        Harness.Measure("8t", "Stopwatch.GetTimestamp() (what each RenderPhaseProfile scope pays twice)", 1, 6331 * 60.0, i => sinkTicks += Stopwatch.GetTimestamp(),
            unit: "call", note: "frame count is a guess: ~60 profiled scope boundaries per draw with the phase profile on");

        var pool = MetalNative.objc_autoreleasePoolPush();
        var options = MetalNative.Send(MetalNative.Send(MetalNative.Class("MTLCompileOptions"), S("alloc")), S("init"));
        MetalNative.SetMslLanguageVersion(options);
        nint error = 0;
        var library = MetalNative.Send(device, S("newLibraryWithSource:options:error:"), MetalNative.NsString(Source), options, ref error);
        if (library == 0) throw new InvalidOperationException(MetalNative.DescribeError(error));
        var fn = (string n) => MetalNative.Send(library, S("newFunctionWithName:"), MetalNative.NsString(n));

        var descriptor = MetalNative.Send(MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), S("alloc")), S("init"));
        MetalNative.SendVoid(descriptor, S("setVertexFunction:"), fn("vs"));
        MetalNative.SendVoid(descriptor, S("setFragmentFunction:"), fn("fs"));
        var colorAttachment = MetalNative.SendAtIndex(MetalNative.Send(descriptor, S("colorAttachments")), S("objectAtIndexedSubscript:"), 0);
        const nuint Rgba8Unorm = 70;
        MetalV2Native.SendVoidValue(colorAttachment, S("setPixelFormat:"), Rgba8Unorm);
        var renderState = MetalNative.Send(device, S("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref error);
        if (renderState == 0) throw new InvalidOperationException(MetalNative.DescribeError(error));
        var computeState = MetalNative.Send(device, S("newComputePipelineStateWithFunction:error:"), fn("k"), ref error);
        if (computeState == 0) throw new InvalidOperationException(MetalNative.DescribeError(error));

        // The default usage is shader-read only; a render target needs RenderTarget (4).
        var textureDescriptor = MetalNative.SendTextureDescriptor(MetalNative.Class("MTLTextureDescriptor"), S("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), Rgba8Unorm, 256, 256, false);
        MetalV2Native.SendVoidValue(textureDescriptor, S("setUsage:"), 4);
        MetalV2Native.SendVoidValue(textureDescriptor, S("setStorageMode:"), 2);
        var texture = MetalNative.Send(device, S("newTextureWithDescriptor:"), textureDescriptor);

        var buffers = new nint[20];
        for (var i = 0; i < buffers.Length; i++) buffers[i] = MetalNative.SendNewBuffer(device, S("newBufferWithLength:options:"), 4096, 0);
        MetalNative.objc_autoreleasePoolPop(pool);

        var selSetVertexBuffer = S("setVertexBuffer:offset:atIndex:");
        var selSetFragmentBuffer = S("setFragmentBuffer:offset:atIndex:");
        var selSetComputeBuffer = S("setBuffer:offset:atIndex:");
        var selDraw = S("drawPrimitives:vertexStart:vertexCount:");

        void RenderRun(string id, string name, int buffersPerDraw, bool pipelinePerDraw, int runs = 7)
        {
            var encode = new List<double>();
            var total = new List<double>();
            var commitWait = new List<double>();
            var bytes = new List<double>();
            for (var run = 0; run < runs + 2; run++)
            {
                var scope = MetalNative.objc_autoreleasePoolPush();
                var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                var commandBuffer = MetalNative.Send(queue, S("commandBuffer"));
                var pass = MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), S("renderPassDescriptor"));
                var attachment = MetalNative.SendAtIndex(MetalNative.Send(pass, S("colorAttachments")), S("objectAtIndexedSubscript:"), 0);
                MetalNative.SendVoid(attachment, S("setTexture:"), texture);
                MetalV2Native.SendVoidValue(attachment, S("setLoadAction:"), 2);
                MetalV2Native.SendVoidValue(attachment, S("setStoreAction:"), 1);
                var encoder = MetalNative.Send(commandBuffer, S("renderCommandEncoderWithDescriptor:"), pass);
                var t1 = Stopwatch.GetTimestamp();
                var selSetPipeline = S("setRenderPipelineState:");
                MetalNative.SendVoid(encoder, selSetPipeline, renderState);
                for (var draw = 0; draw < Draws; draw++)
                {
                    if (pipelinePerDraw) MetalNative.SendVoid(encoder, selSetPipeline, renderState);
                    for (var b = 0; b < buffersPerDraw; b++)
                    {
                        MetalNative.SendSetBuffer(encoder, (b & 1) == 0 ? selSetVertexBuffer : selSetFragmentBuffer, buffers[(draw + b) % buffers.Length], (nuint)((draw & 15) * 256), (nuint)(b >> 1));
                    }

                    MetalNative.SendDrawPrimitives(encoder, selDraw, 3, 0, 3);
                }

                var t2 = Stopwatch.GetTimestamp();
                MetalNative.SendVoid(encoder, S("endEncoding"));
                MetalNative.SendVoid(commandBuffer, S("commit"));
                var t3 = Stopwatch.GetTimestamp();
                MetalNative.SendVoid(commandBuffer, S("waitUntilCompleted"));
                var t4 = Stopwatch.GetTimestamp();
                var allocAfter = GC.GetAllocatedBytesForCurrentThread();
                MetalNative.objc_autoreleasePoolPop(scope);
                if (run < 2) continue; // warm: first runs pay lazy pipeline/driver setup
                var ns = 1e9 / Stopwatch.Frequency;
                encode.Add((t2 - t1) * ns / Draws);
                total.Add((t3 - t0) * ns / Draws);
                commitWait.Add((t4 - t3) * ns / Draws);
                bytes.Add((allocAfter - allocBefore) / (double)Draws);
            }

            Harness.Record(id, name + " [encode loop only]", encode, bytes, 6331, note: $"{buffersPerDraw} setBuffer + 1 draw per item, one encoder, {Draws} draws");
            Harness.Record(id + "e", name + " [encoder create + end + commit]", total, bytes, 6331, note: "adds pass setup, endEncoding, commit (no wait)");
            Harness.Record(id + "w", name + " [GPU wait: waitUntilCompleted after commit]", commitWait, bytes, 6331, note: "GPU execution of 6000 tiny triangles");
        }

        RenderRun("8r1", "raw Metal: 1 draw only (pipeline set once)", 0, false);
        RenderRun("8r2", "raw Metal: 2 setBuffer + 1 draw", 2, false);
        RenderRun("8r3", "raw Metal: 10 setBuffer + 1 draw", 10, false);
        RenderRun("8r4", "raw Metal: 20 setBuffer + 1 draw (heavy bind)", 20, false);
        RenderRun("8r5", "raw Metal: 10 setBuffer + setRenderPipelineState + 1 draw", 10, true);

        // Compute: 2,700 dispatches per frame.
        {
            var encode = new List<double>();
            var wait = new List<double>();
            var bytes = new List<double>();
            for (var run = 0; run < 9; run++)
            {
                var scope = MetalNative.objc_autoreleasePoolPush();
                var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                var commandBuffer = MetalNative.Send(queue, S("commandBuffer"));
                var encoder = MetalNative.Send(commandBuffer, S("computeCommandEncoder"));
                var t1 = Stopwatch.GetTimestamp();
                MetalNative.SendVoid(encoder, S("setComputePipelineState:"), computeState);
                for (var d = 0; d < Dispatches; d++)
                {
                    MetalNative.SendSetBuffer(encoder, selSetComputeBuffer, buffers[d % buffers.Length], (nuint)((d & 15) * 256), 0);
                    MetalNative.SendSetBuffer(encoder, selSetComputeBuffer, buffers[(d + 1) % buffers.Length], 0, 1);
                    MetalNative.SendDispatch(encoder, S("dispatchThreadgroups:threadsPerThreadgroup:"),
                        new MtlSize { Width = 1, Height = 1, Depth = 1 }, new MtlSize { Width = 64, Height = 1, Depth = 1 });
                }

                var t2 = Stopwatch.GetTimestamp();
                MetalNative.SendVoid(encoder, S("endEncoding"));
                MetalNative.SendVoid(commandBuffer, S("commit"));
                MetalNative.SendVoid(commandBuffer, S("waitUntilCompleted"));
                var t4 = Stopwatch.GetTimestamp();
                var allocAfter = GC.GetAllocatedBytesForCurrentThread();
                MetalNative.objc_autoreleasePoolPop(scope);
                if (run < 2) continue;
                var ns = 1e9 / Stopwatch.Frequency;
                encode.Add((t2 - t1) * ns / Dispatches);
                wait.Add((t4 - t2) * ns / Dispatches);
                bytes.Add((allocAfter - allocBefore) / (double)Dispatches);
            }

            Harness.Record("8c1", "raw Metal compute: 2 setBuffer + 1 dispatchThreadgroups [encode loop only]", encode, bytes, 2700, "dispatch");
            Harness.Record("8c2", "raw Metal compute: endEncoding + commit + GPU wait (2700 dispatches, serial)", wait, bytes, 2700, "dispatch", "serial dispatch type: Metal inserts a barrier between dependent dispatches");
        }

        try { GpuWordWait(); }
        catch (InvalidOperationException exception) { Console.WriteLine($"8g  SKIPPED on {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}: {exception.Message}"); }
        GC.KeepAlive(sinkTicks);
    }

    /// <summary>
    /// A guest read of a word the GPU is about to write: the host commits the open encoder and waits for the command buffer
    /// (MetalRenderHost.TryReadGuest -> TryReadGuestCore -> WaitForGuestBytes). Measured through the real rig.
    /// </summary>
    internal static void GpuWordWait()
    {
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        using var rig = new SceneRig(device);
        const uint Groups = 1;
        var buffer = rig.Memory.Allocate(Groups * 64 * 4, 256);
        var stream = rig.Dispatch(new ScenePm4(), ScenePrograms.StoreGlobalId, Descriptors.Buffer(buffer, 4, Groups * 64, format: 20), Groups).ToArray();
        var address = rig.Memory.Upload<uint>(stream, 256);
        var host = rig.Host;
        ulong submission = 1000;
        var wordBytes = new byte[4];
        var enqueueToRead = new List<double>();
        var readOnly = new List<double>();
        var cpuEncode = new List<double>();
        var ns = 1e9 / Stopwatch.Frequency;
        // Compile the program first: a submission parked on an async pipeline compile never wrote the word (arm64 read 0).
        host.CommandStream.EnqueueGraphics(address, (uint)stream.Length, submission++, null);
        DrainStream(host);
        for (var i = 0; i < 400; i++)
        {
            host.CommandStream.EnqueueGraphics(address, (uint)stream.Length, submission++, null);
            var t0 = Stopwatch.GetTimestamp();
            host.RunPendingCommands();
            host.RunCommandStreamSlices(64);
            var t1 = Stopwatch.GetTimestamp();
            if (!host.TryReadGuest(buffer + 4, wordBytes)) throw new InvalidOperationException("read failed");
            var t2 = Stopwatch.GetTimestamp();
            if (BitConverter.ToUInt32(wordBytes) != (1 ^ 0x5A5A)) throw new InvalidOperationException($"word {BitConverter.ToUInt32(wordBytes):X}");
            if (i < 100) continue; // pipeline compile / warmup
            cpuEncode.Add((t1 - t0) * ns);
            readOnly.Add((t2 - t1) * ns);
            enqueueToRead.Add((t2 - t0) * ns);
        }

        Harness.Record("8g1", "one tiny dispatch: CPU parse+encode (RunPendingCommands + RunCommandStreamSlices)", cpuEncode, [0], 20, "wait", "per frame figure assumes ~20 GPU-word waits per frame (guesstimate; PERF-REPORT: 65-76 ms/frame GPU wait)");
        Harness.Record("8g2", "guest word read that must wait: commit + GPU execute + return (TryReadGuest)", readOnly, [0], 20, "wait");
        Harness.Record("8g3", "dispatch submit -> GPU-written word visible (encode + commit + wait)", enqueueToRead, [0], 20, "wait");
        GpuWordWaitBehindLongWork(rig, host, buffer, address, stream.Length, ref submission);
        var probe = new byte[4];
        Harness.Measure("8g4", "same word read again with nothing pending (no wait)", 1, 20, _ => host.TryReadGuest(buffer + 8, probe), unit: "read");
    }

    /// <summary>
    /// 8g5: the word is written by an early submission and the read comes after a later, long submission was recorded
    /// into the same coalesced command buffer. The read needs only the early writer; a whole-buffer wait also waits for
    /// the long work.
    /// </summary>
    private static void GpuWordWaitBehindLongWork(SceneRig rig, MetalRenderHost host, ulong buffer, ulong address, int streamDwords, ref ulong submission)
    {
        const uint LongGroups = 32768; // 2M threads, 8 MB of stores per dispatch
        var big = rig.Memory.Allocate(LongGroups * 64 * 4, 256);
        var longPm4 = new ScenePm4();
        for (var i = 0; i < 6; i++) longPm4 = rig.Dispatch(longPm4, ScenePrograms.StoreGlobalId, Descriptors.Buffer(big, 4, LongGroups * 64, format: 20), LongGroups);
        var longStream = longPm4.ToArray();
        var longAddress = rig.Memory.Upload<uint>(longStream, 256);
        host.CommandStream.EnqueueGraphics(longAddress, (uint)longStream.Length, submission++, null);
        DrainStream(host);
        var wordBytes = new byte[4];
        var readOnly = new List<double>();
        var longOnly = new List<double>();
        var ns = 1e9 / Stopwatch.Frequency;
        for (var i = 0; i < 60; i++)
        {
            host.CommandStream.EnqueueGraphics(address, (uint)streamDwords, submission++, null);
            host.CommandStream.EnqueueGraphics(longAddress, (uint)longStream.Length, submission++, null);
            host.RunPendingCommands();
            host.RunCommandStreamSlices(64);
            var t1 = Stopwatch.GetTimestamp();
            if (!host.TryReadGuest(buffer + 4, wordBytes)) throw new InvalidOperationException("read failed");
            var t2 = Stopwatch.GetTimestamp();
            if (BitConverter.ToUInt32(wordBytes) != (1 ^ 0x5A5A)) throw new InvalidOperationException($"word {BitConverter.ToUInt32(wordBytes):X}");
            host.FlushAndWaitForGpu();
            var t3 = Stopwatch.GetTimestamp();
            if (i < 10) continue;
            readOnly.Add((t2 - t1) * ns);
            longOnly.Add((t3 - t1) * ns);
        }

        Harness.Record("8g5", "word written by an early submission, read after a long later submission in the same buffer (TryReadGuest)", readOnly, [0], 20, "wait");
        Harness.Record("8g6", "same, read + drain of the long work (reference: whole-buffer time)", longOnly, [0], 20, "wait");
    }

    private static void DrainStream(MetalRenderHost host)
    {
        var deadline = Stopwatch.StartNew();
        while (host.CommandStream.HasPending && deadline.Elapsed.TotalSeconds < 60)
        {
            host.RunPendingCommands();
            host.RunCommandStreamSlices(64);
            host.CommitCoalescedFlush();
            host.PollGpuCompletions();
            host.CommandStream.RetryBlocked();
            Thread.Sleep(1);
        }

        host.FlushAndWaitForGpu();
    }
}
