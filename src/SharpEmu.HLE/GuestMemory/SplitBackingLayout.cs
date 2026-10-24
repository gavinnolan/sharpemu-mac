// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GuestMemory;

/// <summary>
/// The part of the guest backing that holds, for the two-process split, guest memory that is
/// otherwise private to the guest process: the GPU process sees guest memory only through the
/// shared backing (docs/macos/split-shared-memory.md), so whatever the GPU reads or writes has to
/// be a view of it. The backing grows past direct and flexible memory by this much, so the
/// guest's direct-memory budget does not change:
///
/// <list type="bullet">
/// <item>the guest allocator arena (<see cref="GuestMemoryLayout.AllocatorArenaAddress"/>), mapped as
/// one view: VideoOut buffer labels (which the GPU writes), and HLE data in split mode (AGC
/// register-default blobs, AvPlayer frames);</item>
/// <item>module images: every page of an eboot or PRX that no executable segment touches
/// (<c>.rodata</c> with the shader binaries, <c>.data</c>, <c>.bss</c>) is a view of a slice of
/// this part, handed out in load order. Executable pages stay private: a shared view of the
/// backing can never be executable on macOS.</item>
/// </list>
///
/// <para>
/// Only <c>SHARPEMU_GPU_PROCESS=1</c> or <c>SHARPEMU_GUEST_BACKING_SHAREABLE=1</c> asks for it, and
/// only a backing the host made shareable uses it; nothing changes otherwise. The part costs
/// address space and, for the chunked backing, one kept port per 2 MiB; memory only as the guest
/// touches it, exactly as the private memory it replaces.
/// </para>
/// </summary>
public sealed record SplitBackingLayout
{
    // Black Myth: Wukong, the largest title measured, has 297 MiB of non-executable module pages
    // (285 MiB in its eboot, 11.5 MiB in 32 plug-ins); Ghost of Tsushima has 43 MiB.
    public const ulong DefaultImageBytes = 1UL << 30;

    public SplitBackingLayout(ulong offset, ulong arenaBytes, ulong imageBytes,
        ulong arenaAddress = GuestMemoryLayout.AllocatorArenaAddress)
    {
        const ulong page = GuestMemoryLayout.GuestPage;
        if (offset % page != 0 || arenaBytes == 0 || arenaBytes % page != 0 || imageBytes % page != 0 ||
            arenaBytes > ulong.MaxValue - imageBytes || arenaBytes + imageBytes > ulong.MaxValue - offset ||
            arenaAddress == 0 || arenaAddress % page != 0 || arenaBytes > ulong.MaxValue - arenaAddress)
        {
            throw new ArgumentOutOfRangeException(nameof(offset),
                "The split layout needs whole guest pages, an arena, and must fit in the address space.");
        }

        Offset = offset;
        ArenaBytes = arenaBytes;
        ImageBytes = imageBytes;
        ArenaAddress = arenaAddress;
    }

    /// <summary>Where the part starts in the backing: past direct and flexible memory by default.</summary>
    public ulong Offset { get; }

    /// <summary>The guest address of the arena's one view (the GPU process replicates it there).</summary>
    public ulong ArenaAddress { get; }

    public ulong ArenaBytes { get; }

    public ulong ImageBytes { get; }

    public ulong ArenaOffset => Offset;

    public ulong ImageOffset => Offset + ArenaBytes;

    /// <summary>The backing bytes the layout needs: the whole backing ends here at least.</summary>
    public ulong End => ImageOffset + ImageBytes;

    public static SplitBackingLayout Default { get; } =
        new(GuestMemoryLayout.BackingBytes, GuestMemoryLayout.AllocatorArenaBytes, DefaultImageBytes);

    /// <summary>True when the environment asks for the two-process split or a shareable backing.</summary>
    public static bool IsRequested => IsSwitchOn("SHARPEMU_GPU_PROCESS") || IsSwitchOn("SHARPEMU_GUEST_BACKING_SHAREABLE");

    /// <summary>The default layout when the environment asks for it, null otherwise.</summary>
    public static SplitBackingLayout? FromEnvironment() => IsRequested ? Default : null;

    private static bool IsSwitchOn(string name) => Environment.GetEnvironmentVariable(name) == "1";
}
