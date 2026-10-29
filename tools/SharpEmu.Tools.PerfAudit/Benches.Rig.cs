// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Gpu.Metal;
using SharpEmu.Gpu.Metal.Tests.Scenes;

namespace SharpEmu.PerfAudit;

internal static class RigBenches
{
    public static void Smoke()
    {
        Console.WriteLine("--- rig smoke ---");
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        Console.WriteLine($"device=0x{device:X}");
        using var rig = new SceneRig(device);
        const uint Size = 64;
        var target = rig.ColorTarget(Size, Size);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size);
        var pm4 = rig.DrawMesh(new ScenePm4(), state,
        [
            SceneVertex.At(-1, -1, 0.5f, 1, 0, 0),
            SceneVertex.At(1, -1, 0.5f, 1, 0, 0),
            SceneVertex.At(-1, 1, 0.5f, 1, 0, 0),
        ]);
        var sw = Stopwatch.StartNew();
        rig.Submit(pm4);
        Console.WriteLine($"first submit {sw.Elapsed.TotalMilliseconds:F1} ms");
        var pixels = rig.ReadImage(target, Size, Size);
        var pixel = SceneDraws.Pixel(pixels, Size, 2, Size - 3);
        Console.WriteLine($"pixel(2,{Size - 3}) = 0x{pixel:X8} (expect FF0000FF)");
        if (pixel != 0xFF0000FF)
            throw new InvalidOperationException($"Synthetic triangle readback mismatch: 0x{pixel:X8}");
    }
}
