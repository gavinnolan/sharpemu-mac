// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// A second host mapping of a chunked backing, made for the GPU's no-copy windows
/// (single-copy textures, SHARPEMU_METAL_V2_SINGLE_COPY=1). It maps the same chunk objects
/// as the canonical alias, so every byte written through one is seen through the other,
/// except where a chunk has been punched out: there one shared dummy chunk is mapped
/// instead. A window over a punched chunk then never wires, faults in or reads the chunk the
/// guest copy lives in, so that chunk can be emptied while windows around it stay in use.
/// A buffer made over this mapping keeps the objects it was made over, so a punch or a fill
/// is seen only by buffers made afterwards.
/// </summary>
/// <remarks>Host-package contract; made by <see cref="HostBackingObject.CreateGpuAlias"/>.</remarks>
public interface IHostBackingGpuAlias : IDisposable
{
    /// <summary>Host address of the mapping; backing offset 0 is here.</summary>
    ulong Base { get; }

    /// <summary>Bytes mapped: the backing's native alias size.</summary>
    ulong Size { get; }

    /// <summary>Maps the dummy chunk over every chunk of [offset, offset + size) (whole chunks).</summary>
    bool TryPunch(ulong offset, ulong size);

    /// <summary>Maps the backing's own chunks over [offset, offset + size) again (whole chunks).</summary>
    bool TryFill(ulong offset, ulong size);

    /// <summary>Sets every byte of the dummy chunk; a test or debug sentinel in place of zeros.</summary>
    void FillDummy(byte value);
}
