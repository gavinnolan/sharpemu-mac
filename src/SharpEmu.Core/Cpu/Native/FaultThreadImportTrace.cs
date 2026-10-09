// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Core.Cpu.Native;

// Detached diagnostic metadata only. The original dispatch return is recorded
// exactly; Returned does not mean semantic success, bytes or native authority.
internal enum FaultThreadImportStatus { Entered, Returned, Threw }
internal readonly record struct FaultThreadImportEntry(long Sequence, object BackendOwner, int ImportIndex,
    string Nid, int ManagedThreadId, ulong GuestThreadHandle,
    FaultThreadImportStatus Status, ulong? Result);

internal sealed class FaultThreadImportTrace
{
    internal const int Capacity = 64;
    private readonly FaultThreadImportEntry[] entries = new FaultThreadImportEntry[Capacity];
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private long sequence;
    private int count;

    internal long Begin(object backendOwner, int importIndex, string nid, ulong guestThreadHandle)
    {
        RequireOwner();
        ArgumentNullException.ThrowIfNull(backendOwner);
        var current = checked(++sequence);
        entries[(int)((current - 1) % Capacity)] = new(current, backendOwner, importIndex, nid, ownerThread, guestThreadHandle,
            FaultThreadImportStatus.Entered, null);
        if (count < Capacity) count++;
        return current;
    }

    internal void Complete(long originalSequence, ulong result) => Finish(originalSequence, FaultThreadImportStatus.Returned, result);
    internal void Threw(long originalSequence) => Finish(originalSequence, FaultThreadImportStatus.Threw, null);

    private void Finish(long originalSequence, FaultThreadImportStatus status, ulong? result)
    {
        RequireOwner();
        if (originalSequence <= 0 || originalSequence > sequence) return;
        var index = (int)((originalSequence - 1) % Capacity);
        var original = entries[index];
        // Nested imports may have evicted this exact entry. Never mark a younger
        // slot as the return of an older call or overwrite its first completion.
        if (original.Sequence != originalSequence || original.Status != FaultThreadImportStatus.Entered) return;
        entries[index] = original with { Status = status, Result = result };
    }

    internal FaultThreadImportEntry[] Snapshot(object backendOwner, int managedThreadId, ulong guestThreadHandle)
    {
        RequireOwner();
        if (managedThreadId != ownerThread) return [];
        var result = new List<FaultThreadImportEntry>(count);
        for (var current = sequence - count + 1; current <= sequence; current++)
        {
            var entry = entries[(int)((current - 1) % Capacity)];
            if (entry.Sequence == current && ReferenceEquals(entry.BackendOwner, backendOwner) && entry.ManagedThreadId == managedThreadId &&
                entry.GuestThreadHandle == guestThreadHandle) result.Add(entry);
        }
        return result.ToArray();
    }

    private void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Fault import diagnostics cannot access another managed thread's ring.");
    }
}
