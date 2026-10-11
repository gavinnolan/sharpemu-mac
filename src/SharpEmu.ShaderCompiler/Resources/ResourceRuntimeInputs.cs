// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// Reads one dword of guest memory; false when the address cannot be read.
public delegate bool GuestWordReader(ulong address, out uint word);

// The user-data word offsets one materialization read, collected while it runs. A cache
// compares a cached materialization against a later draw on these words alone: the walk
// evaluated to what it did without reading any other word, so no other word can change it.
// Offsets past the mask's width set Overflowed, and a comparison then covers every word.
public sealed class UserDataReadLog
{
    public const int Width = 64;

    public ulong Mask { get; private set; }
    public bool Overflowed { get; private set; }

    public void Note(int offset)
    {
        if ((uint)offset >= Width) Overflowed = true;
        else Mask |= 1ul << offset;
    }

    public void Reset()
    {
        Mask = 0;
        Overflowed = false;
    }
}

// What one draw supplies to materialise a plan: its user data, the shader base and
// the two memory readers. The clean reader refuses memory the GPU may still own.
public sealed class ResourceRuntimeInputs
{
    public IReadOnlyList<uint> UserData { get; init; } = [];
    public ulong ShaderBase { get; init; }
    public GuestWordReader? ReadMemory { get; init; }
    public GuestWordReader? ReadCleanMemory { get; init; }
    public ComputeSelectorState? ComputeState { get; init; }

    // Told true before the flattened table's words are evaluated and false after, so a reader
    // wrapper can tell the words only the table reads from those the descriptors depend on.
    public Action<bool>? TablePhase { get; init; }

    public ResidentGuestBytesReader? ReadResidentMemory { get; init; }

    public bool ReadsClean { get; init; }

    // Set by a cache that will compare its entry on the words this walk reads; null otherwise.
    public UserDataReadLog? UserDataReads { get; init; }

    public ResourceRuntimeInputs WithReader(GuestWordReader? reader) => new()
    {
        UserData = UserData,
        ShaderBase = ShaderBase,
        ReadMemory = reader,
        ReadCleanMemory = ReadCleanMemory,
        ComputeState = ComputeState,
        TablePhase = TablePhase,
        ReadResidentMemory = ReadResidentMemory,
        ReadsClean = ReadsClean || ReferenceEquals(reader, ReadCleanMemory),
        UserDataReads = UserDataReads,
    };
}

public readonly record struct ComputeSelectorState(uint WaveSize, uint ThreadsX, uint ThreadsY, uint ThreadsZ,
    bool HasPartialWorkgroups, uint LocalDataShareDwords, int LocalInvocationIdComponents);
