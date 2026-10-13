// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The descriptor words and user data captured for one program at draw time, plus the
// flattened resource table and the device-address ranges the planner could bound.
public sealed class ResourceSnapshot
{
    public uint[][] Buffers { get; init; } = [];
    public uint[][] Images { get; init; } = [];
    public uint[][] Samplers { get; init; } = [];
    public uint[] FlattenedResourceTable { get; init; } = [];
    public uint[] UserData { get; init; } = [];
    public DeviceAddressRange[] DeviceAddressRanges { get; init; } = [];

    /// <summary>
    /// The same snapshot carrying this draw's user data.
    /// </summary>
    /// <remarks>
    /// A cached materialization is shared by every draw whose descriptors evaluate the same
    /// way, which includes draws that differ in user-data words the plan never reads — words
    /// a shader can still read as inline constants through its binding layout. Those come
    /// from the draw, not from the entry that was cached with it.
    /// </remarks>
    public ResourceSnapshot WithUserData(uint[] userData) =>
        ReferenceEquals(UserData, userData) || UserData.AsSpan().SequenceEqual(userData)
            ? this
            : new ResourceSnapshot
            {
                Buffers = Buffers,
                Images = Images,
                Samplers = Samplers,
                FlattenedResourceTable = FlattenedResourceTable,
                UserData = userData,
                DeviceAddressRanges = DeviceAddressRanges,
            };
}

// One device-address handle's evaluated base and bounded extent for this draw.
public readonly record struct DeviceAddressRange(uint Handle, ulong Base, ulong Size, bool Planned, bool Written);
