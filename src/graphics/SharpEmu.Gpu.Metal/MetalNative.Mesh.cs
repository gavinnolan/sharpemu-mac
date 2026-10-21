// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalNative
{
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    internal static partial nint SendNewMeshPipeline(nint device, nint selector, nint descriptor,
        nuint options, nint reflection, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    internal static partial void SendDrawMesh(nint encoder, nint selector, MtlSize groups,
        MtlSize objectThreads, MtlSize meshThreads);
}
