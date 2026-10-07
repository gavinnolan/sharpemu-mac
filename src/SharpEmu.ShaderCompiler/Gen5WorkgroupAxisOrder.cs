// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// The logical-to-physical compute workgroup axis remapping, shared by the shader backends
/// that bake it into a pipeline and by the host dispatch path that must permute its group
/// counts the same way. It lives in the shared compiler so neither side depends on the other's
/// backend assembly.
/// </summary>
public static class Gen5WorkgroupAxisOrder
{
    // Vulkan only guarantees maxComputeWorkGroupSize >= (1024, 1024, 64) and
    // maxComputeWorkGroupInvocations >= 1024, but a GNM NUM_THREAD layout can put its
    // largest axis on Z (real PS5 hardware has no such asymmetry between axes). Remap
    // logical GNM axes onto physical dispatch axes so the largest logical size always
    // lands on the physical axis with the most headroom (X), and the smallest lands on Z,
    // which is the only axis with a tight guaranteed limit. This is a pure function of the
    // three thread-group sizes so the host dispatch path can recompute it independently
    // without any shader-compiler metadata plumbing.
    //
    // Returns physicalAxisOfLogical: physicalAxisOfLogical[logical 0=X/1=Y/2=Z] is the
    // physical axis (0=X/1=Y/2=Z) that logical axis is remapped onto.
    public static int[] Compute(uint sizeX, uint sizeY, uint sizeZ)
    {
        if (sizeZ <= 64)
        {
            return [0, 1, 2];
        }

        var sizes = new[] { sizeX, sizeY, sizeZ };
        var byDescendingSize = new[] { 0, 1, 2 };
        Array.Sort(byDescendingSize, (a, b) => sizes[b].CompareTo(sizes[a]));

        var physicalAxisOfLogical = new int[3];
        for (var physical = 0; physical < 3; physical++)
        {
            physicalAxisOfLogical[byDescendingSize[physical]] = physical;
        }

        return physicalAxisOfLogical;
    }
}
