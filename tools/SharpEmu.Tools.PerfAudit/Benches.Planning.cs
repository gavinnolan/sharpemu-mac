// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.PerfAudit.Planning;
using static SharpEmu.PerfAudit.Planning.RenderExecutorFixtures;

namespace SharpEmu.PerfAudit;

internal static class PlanningBenches
{
    private const int Batch = 2000;

    public static void Run()
    {
        Console.WriteLine("--- 2. Draw planning (RenderExecutor over a no-op host) ---");
        var host = new RecordingRenderHost();
        var pipelines = new FakePipelineProvider();
        var executor = new RenderExecutor(host, pipelines);
        var banks = Banks();
        var auto = Auto(6);

        // 2a: the executor alone, same live banks every draw.
        Harness.Measure("2a", "RenderExecutor.DrawAuto, 1 color target, banks unchanged", Batch, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch; i++) executor.DrawAuto(1, banks, auto);
        }, note: "host calls are no-ops; real host costs are in sections 3-5/8");

        // 2b: the state-tracking step alone: copy-on-write snapshot after one register write.
        var writes = Banks();
        Harness.Measure("2b", "RegisterBanks.PrepareContextWrite + 1 reg write + Snapshot()", Batch, Pm4Benches.FrameDraws, iteration =>
        {
            for (var i = 0; i < Batch; i++)
            {
                writes.PrepareContextWrite();
                writes.Context.DepthCountControl = (uint)(i & 1);
                _ = writes.Snapshot();
            }
        }, note: "what a deferred draw does per draw before it is queued");

        // 2c: a deferred draw: snapshot then plan it.
        var live = Banks();
        Harness.Measure("2c", "Snapshot + RenderExecutor.DrawAuto (deferred draw, color only)", Batch, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch; i++)
            {
                live.PrepareContextWrite();
                live.Context.DepthCountControl = (uint)(i & 1);
                var snapshot = live.Snapshot();
                executor.DrawAuto(1, snapshot, auto);
            }
        });

        // 2d: indexed draw, depth target + one vertex buffer + index buffer, snapshot first.
        pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)]);
        var depthLive = Banks(withDepth: true);
        var indexed = Indexed(6, instances: 2, baseVertex: 3);
        Harness.Measure("2d", "Snapshot + RenderExecutor.DrawIndexed (depth + 1 VB + IB)", Batch, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch; i++)
            {
                depthLive.PrepareContextWrite();
                depthLive.Context.DepthCountControl = (uint)(i & 1);
                var snapshot = depthLive.Snapshot();
                executor.DrawIndexed(7, snapshot, indexed);
            }
        });

        // 2f-2j: index expansion (host has no fan primitive): fan, quad list, 8-bit, custom restart, non-indexed fan.
        host.DrawsTriangleFans = false;
        pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)]);
        const int IndexCount = 1000;
        var wide = new byte[IndexCount * 4];
        for (var i = 0; i < IndexCount; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wide.AsSpan(i * 2), (ushort)(i % 997));
        host.WriteGuest(IndexBase, wide);
        var fanBanks = Banks(primitiveType: 5);
        Harness.Measure("2f", "DrawIndexed triangle fan, 1000 x u16 indices (CPU triangulation)", Batch / 4, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch / 4; i++) executor.DrawIndexed(7, fanBanks, Indexed(IndexCount));
        });
        var quadBanks = Banks(primitiveType: 19);
        Harness.Measure("2g", "DrawIndexed legacy quad list, 1000 x u16 indices", Batch / 4, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch / 4; i++) executor.DrawIndexed(7, quadBanks, Indexed(IndexCount));
        });
        var stripBanks = Banks(primitiveType: PrimitiveTriangleStrip);
        Harness.Measure("2h", "DrawIndexed triangle list, 1000 x u8 indices (widened to u16)", Batch / 4, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch / 4; i++) executor.DrawIndexed(7, Banks(), Indexed(IndexCount, indexType: 2));
        });
        stripBanks.UserConfig.PrimitiveResetControl = 1;
        stripBanks.Context.PrimitiveResetIndex = 0x7FFF;
        Harness.Measure("2i", "DrawIndexed strip, 1000 x u16, custom restart index (widened to u32)", Batch / 4, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch / 4; i++) executor.DrawIndexed(7, stripBanks, Indexed(IndexCount));
        });
        Harness.Measure("2j", "DrawAuto triangle fan, 3000 vertices (CPU triangulation)", Batch / 4, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch / 4; i++) executor.DrawAuto(1, fanBanks, Auto(3000));
        });
        host.DrawsTriangleFans = true;

        // 2k: Snapshot() with no register write since the last one.
        RegisterBanks? sink = null;
        var idle = Banks();
        Harness.Measure("2k", "RegisterBanks.Snapshot() with no write since the last one", Batch, Pm4Benches.FrameDraws, _ =>
        {
            for (var i = 0; i < Batch; i++) sink = idle.Snapshot();
        });

        // 2e: compute dispatch planning.
        pipelines.Graphics = Programs();
        var computeBanks = Banks();
        Harness.Measure("2e", "RenderExecutor.Dispatch (direct, 64-thread group)", Batch, 2700, _ =>
        {
            for (var i = 0; i < Batch; i++) executor.Dispatch(5, computeBanks, 4, 1, 1, 0x41);
        }, unit: "dispatch");
    }
}
