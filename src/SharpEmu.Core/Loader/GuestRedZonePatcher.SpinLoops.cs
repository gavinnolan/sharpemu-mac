// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

// Generic spin-loop nap: redirect each guest `pause` that sits in a short backward loop through a
// trampoline that calls the shared SpinNapRuntime probe (which naps a thread that has spun past
// the idle threshold) and then runs the original instruction. Title-agnostic; it reuses the same
// decoding, branch-target and jump-table guards, trampoline allocation and relocation as the
// red-zone pass. See SpinNapRuntime for the host side.
internal static partial class GuestRedZonePatcher
{
    // Window after a pause in which a backward branch must close the loop for the site to count as
    // a spin. Longer bodies are not tight spins and are left alone.
    private const int MaxSpinLoopBodyBytes = 96;

    internal readonly record struct SpinPatchResult(
        int Functions,
        int PauseSites,
        int PatchedSites,
        int FailedSites,
        int UnrelocatableSites,
        int NonLoopSites);

    public static SpinPatchResult PatchSpinLoops(
        IVirtualMemory memory,
        PhysicalVirtualMemory physicalMemory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        ulong imageSize,
        ReadOnlySpan<byte> probeCode,
        string moduleName)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(physicalMemory);
        ArgumentNullException.ThrowIfNull(programHeaders);
        if (probeCode.IsEmpty ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !TryDecodeFunctionStarts(memory, programHeaders, imageBase, out var functionStarts))
        {
            return default;
        }

        var sites = CollectSpinSites(memory, programHeaders, imageBase, functionStarts, out var result);
        if (sites.Count == 0)
        {
            return result;
        }

        sites.Sort(static (left, right) => left.Address.CompareTo(right.Address));
        // The probe sits at the arena's start so every site's `call rel32` reaches it.
        var requiredBytes = AlignUp(checked((ulong)sites.Count * 128UL + (ulong)probeCode.Length + PageSize), PageSize);
        if (!TryAllocateTrampolines(physicalMemory, imageBase, imageSize, requiredBytes, sites, out var cursor))
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] {moduleName} spin-nap: no trampoline range for {sites.Count} sites.");
            return result with { FailedSites = sites.Count };
        }

        var end = cursor + requiredBytes;
        var probeAddress = cursor;
        if (!memory.TryWrite(cursor, probeCode))
        {
            Console.Error.WriteLine($"[LOADER][WARN] {moduleName} spin-nap: could not place the probe.");
            return result with { FailedSites = sites.Count };
        }

        cursor = AlignUp(cursor + (ulong)probeCode.Length, 16);
        var patched = 0;
        var failed = 0;
        var previousEnd = 0UL;
        foreach (var site in sites)
        {
            if (site.Address < previousEnd || !TryPatchSpinSite(memory, site, probeAddress, ref cursor, end))
            {
                failed++;
                continue;
            }

            previousEnd = site.Address + (ulong)site.ByteLength;
            patched++;
        }

        result = result with { PatchedSites = patched, FailedSites = failed };
        Console.Error.WriteLine(
            $"[LOADER] {moduleName} spin-nap: functions={result.Functions} pause_sites={result.PauseSites} " +
            $"patched={result.PatchedSites} failed={result.FailedSites} unrelocatable={result.UnrelocatableSites} " +
            $"non_loop={result.NonLoopSites}.");
        return result;
    }

    private static List<PatchSite> CollectSpinSites(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        IReadOnlyList<ulong> functionStarts,
        out SpinPatchResult result)
    {
        var sites = new List<PatchSite>();
        var functionCount = 0;
        var pauseSites = 0;
        var unrelocatable = 0;
        var nonLoop = 0;
        var decoded = new List<DecodedInstruction>();
        var branchTargets = new HashSet<ulong>();

        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.Load ||
                (header.Flags & ProgramHeaderFlags.Execute) == 0 ||
                header.FileSize == 0 ||
                header.FileSize > int.MaxValue)
            {
                continue;
            }

            var segmentStart = imageBase + header.VirtualAddress;
            var segmentEnd = segmentStart + header.FileSize;
            var segmentFunctions = functionStarts
                .Where(address => address >= segmentStart && address < segmentEnd)
                .Distinct()
                .Order()
                .ToArray();
            if (segmentFunctions.Length == 0)
            {
                continue;
            }

            var segmentBytes = GC.AllocateUninitializedArray<byte>((int)header.FileSize);
            if (!memory.TryRead(segmentStart, segmentBytes))
            {
                continue;
            }

            for (var functionIndex = 0; functionIndex < segmentFunctions.Length; functionIndex++)
            {
                var functionStart = segmentFunctions[functionIndex];
                var functionEnd = functionIndex + 1 < segmentFunctions.Length
                    ? segmentFunctions[functionIndex + 1]
                    : segmentEnd;
                if (functionEnd <= functionStart || functionEnd - functionStart > int.MaxValue)
                {
                    continue;
                }

                DecodeFunction(segmentBytes, segmentStart, functionStart, functionEnd, decoded);
                if (decoded.Count == 0)
                {
                    continue;
                }

                var hasPause = false;
                foreach (var entry in decoded)
                {
                    if (entry.Instruction.Code == Code.Pause)
                    {
                        hasPause = true;
                        break;
                    }
                }

                if (!hasPause)
                {
                    continue;
                }

                functionCount++;
                CollectBranchTargets(decoded, branchTargets);
                var canBorrow = TryCollectJumpTableTargets(
                    memory, programHeaders, imageBase, decoded, functionStart, functionEnd, branchTargets);

                var lastSiteEnd = 0UL;
                for (var index = 0; index < decoded.Count; index++)
                {
                    if (decoded[index].Instruction.Code != Code.Pause)
                    {
                        continue;
                    }

                    pauseSites++;
                    var pauseAddress = decoded[index].Instruction.IP;
                    if (!ClosesShortBackwardLoop(decoded, index, pauseAddress))
                    {
                        nonLoop++;
                        continue;
                    }

                    if (!TryBuildPatchSpan(decoded, index, branchTargets, canBorrow, out var span, out _))
                    {
                        unrelocatable++;
                        continue;
                    }

                    if (span.Address < lastSiteEnd)
                    {
                        unrelocatable++;
                        continue;
                    }

                    sites.Add(span);
                    lastSiteEnd = span.Address + (ulong)span.ByteLength;
                }
            }
        }

        result = new SpinPatchResult(functionCount, pauseSites, 0, 0, unrelocatable, nonLoop);
        return sites;
    }

    // A pause counts as a spin when a backward branch within the next few instructions targets at
    // or before the pause, i.e. it closes a tight loop around it.
    private static bool ClosesShortBackwardLoop(IReadOnlyList<DecodedInstruction> decoded, int pauseIndex, ulong pauseAddress)
    {
        var pauseEnd = pauseAddress + 2;
        for (var index = pauseIndex + 1; index < decoded.Count; index++)
        {
            var instruction = decoded[index].Instruction;
            if (instruction.NextIP - pauseEnd > MaxSpinLoopBodyBytes)
            {
                return false;
            }

            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
                instruction.OpCount == 1 &&
                instruction.GetOpKind(0) is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 &&
                instruction.NearBranch64 <= pauseAddress &&
                instruction.NearBranch64 >= decoded[0].Instruction.IP)
            {
                return true;
            }

            if (instruction.FlowControl is FlowControl.Return or FlowControl.IndirectBranch or FlowControl.Call or FlowControl.IndirectCall)
            {
                return false;
            }
        }

        return false;
    }

    // Mirrors TryPatchSite, with a leading probe-call wrapper. The wrapper shifts rsp below the
    // guest red zone (LEA preserves flags), calls the shared probe, and restores rsp; the probe
    // preserves every register and flag, so the relocated instruction runs exactly as before.
    private static bool TryPatchSpinSite(
        IVirtualMemory memory,
        PatchSite site,
        ulong probeAddress,
        ref ulong trampolineCursor,
        ulong trampolineEnd)
    {
        const int leadLength = 21; // lea rsp,[rsp-256] (8) + call rel32 (5) + lea rsp,[rsp+256] (8)
        var codeBase = trampolineCursor + leadLength;

        byte[]? prefix = null;
        if (site.CoreStart > 0 && !TryEncodeSegment(site.Instructions, 0, site.CoreStart, codeBase, out prefix))
        {
            return false;
        }

        var prefixLength = prefix?.Length ?? 0;
        var relocatedAddress = codeBase + (ulong)prefixLength + 5;
        var core = prefix is null
            ? site.Instructions
            : site.Instructions.Skip(site.CoreStart).Take(site.CoreCount).ToList();
        var writer = new ListCodeWriter();
        if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, core, relocatedAddress), out _, out _, BlockEncoderOptions.None))
        {
            return false;
        }

        var relocated = writer.ToArray();
        byte[]? branch = null;
        if (site.TerminalBranch is { } terminalBranch &&
            !TryEncodeSegment([terminalBranch], 0, 1, codeBase + (ulong)prefixLength + 5 + (ulong)relocated.Length + 8, out branch))
        {
            return false;
        }

        var branchLength = branch?.Length ?? 0;
        var trampolineLength = checked(leadLength + prefixLength + 5 + relocated.Length + 8 + branchLength + 5);
        if (trampolineCursor > trampolineEnd || (ulong)trampolineLength > trampolineEnd - trampolineCursor)
        {
            return false;
        }

        var trampoline = new byte[trampolineLength];
        // lea rsp,[rsp-256]
        WriteLeaRsp(trampoline.AsSpan(0, 8), -256);
        // call probe (rel32)
        if (!TryWriteRelativeCall(trampoline.AsSpan(8, 5), trampolineCursor + 8, probeAddress))
        {
            return false;
        }

        // lea rsp,[rsp+256]
        WriteLeaRsp(trampoline.AsSpan(13, 8), 256);

        prefix?.CopyTo(trampoline, leadLength);
        var shiftOffset = leadLength + prefixLength;
        // lea rsp,[rsp-128]
        trampoline[shiftOffset] = 0x48;
        trampoline[shiftOffset + 1] = 0x8D;
        trampoline[shiftOffset + 2] = 0x64;
        trampoline[shiftOffset + 3] = 0x24;
        trampoline[shiftOffset + 4] = 0x80;
        relocated.CopyTo(trampoline, shiftOffset + 5);
        var restoreOffset = shiftOffset + 5 + relocated.Length;
        // lea rsp,[rsp+128]
        trampoline[restoreOffset] = 0x48;
        trampoline[restoreOffset + 1] = 0x8D;
        trampoline[restoreOffset + 2] = 0xA4;
        trampoline[restoreOffset + 3] = 0x24;
        trampoline[restoreOffset + 4] = 0x80;
        trampoline[restoreOffset + 5] = 0;
        trampoline[restoreOffset + 6] = 0;
        trampoline[restoreOffset + 7] = 0;
        branch?.CopyTo(trampoline, restoreOffset + 8);
        var returnJumpOffset = restoreOffset + 8 + branchLength;
        if (!TryWriteRelativeJump(
                trampoline.AsSpan(returnJumpOffset, 5),
                trampolineCursor + (ulong)returnJumpOffset,
                site.Address + (ulong)site.ByteLength))
        {
            return false;
        }

        var originalPatch = new byte[site.ByteLength];
        originalPatch.AsSpan().Fill(0x90);
        if (!TryWriteRelativeJump(originalPatch.AsSpan(0, 5), site.Address, trampolineCursor) ||
            !memory.TryWrite(trampolineCursor, trampoline) ||
            !memory.TryWrite(site.Address, originalPatch))
        {
            return false;
        }

        trampolineCursor = AlignUp(trampolineCursor + (ulong)trampolineLength, 16);
        return true;
    }

    private static void WriteLeaRsp(Span<byte> destination, int displacement)
    {
        // lea rsp,[rsp+disp32]
        destination[0] = 0x48;
        destination[1] = 0x8D;
        destination[2] = 0xA4;
        destination[3] = 0x24;
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], displacement);
    }

    private static bool TryWriteRelativeCall(Span<byte> destination, ulong instructionAddress, ulong targetAddress)
    {
        var displacement = unchecked((long)targetAddress - (long)(instructionAddress + 5));
        if (displacement < int.MinValue || displacement > int.MaxValue)
        {
            return false;
        }

        destination[0] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], (int)displacement);
        return true;
    }
}
