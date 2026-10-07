// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

/// <summary>
/// A generational handle to a backend resource slot. The shared render executor passes these
/// around (IRenderHost.FindImage, BindRenderTarget, ResolveImage) without being able to touch
/// what they point at, so the handle is backend-neutral while the slot table that hands them
/// out belongs to the graphics backend.
/// </summary>
/// <remarks>Generation 0 never identifies a live slot, so the default value is invalid.</remarks>
public readonly record struct ResourceSlotIdentifier(uint Index, uint Generation)
{
    public static ResourceSlotIdentifier Invalid => default;

    public bool IsValid => Generation != 0;
}
