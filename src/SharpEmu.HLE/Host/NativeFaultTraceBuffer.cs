// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

/// <summary>Fixed process-lifetime diagnostic storage, never an ownership receipt.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeFaultTraceRecord
{
    public long Published;
    public long Sequence;
    public long Nanoseconds;
    public long Invocation;
    public ulong RawAddress;
    public ulong Address;
    public ulong ErrorCode;
    public ulong Rip;
    public ulong OwnerAddress;
    public ulong OwnerEnd;
    public ulong Size;
    public long OwnerIdentity;
    public long ArmEpoch;
    public long Operation;
    public int Kind;
    public int Signal;
    public int SignalCode;
    public int Access;
    public int OwnerState;
    public int Protection;
    public int Result;
    public int Errno;
    public int OwnerObservation;
    public int Reserved;
    public int Route;
    public int Reason;
}

internal sealed unsafe class NativeFaultTraceBuffer : IDisposable
{
    internal const ulong Magic = 0x53454D554641554CUL;
    internal const int Version = 2;
    internal const int HeaderBytes = 128;
    internal const int DefaultCapacity = 4096;
    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _bytes;
    private readonly int _capacity;
    private long _reserved;

    internal NativeFaultTraceBuffer(string path, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, DefaultCapacity);
        _capacity = capacity;
        var byteCount = checked(HeaderBytes + capacity * sizeof(NativeFaultTraceRecord));
        // Allocate actual file blocks and flush on this ordinary host stack;
        // SetLength alone would leave sparse backing that could SIGBUS later.
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            output.Write(new byte[byteCount]);
            output.Flush(flushToDisk: true);
        }
        try
        {
            _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, byteCount, MemoryMappedFileAccess.ReadWrite);
            _view = _file.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _bytes = pointer + _view.PointerOffset;
            new Span<byte>(_bytes, byteCount).Clear(); // pre-touch before publication
            *(ulong*)(_bytes + 0) = Magic;
            *(int*)(_bytes + 8) = Version;
            *(int*)(_bytes + 12) = HeaderBytes;
            *(int*)(_bytes + 16) = sizeof(NativeFaultTraceRecord);
            *(int*)(_bytes + 20) = capacity;
            *(int*)(_bytes + 24) = Environment.SystemPageSize;
            *(int*)(_bytes + 28) = (int)RuntimeInformation.ProcessArchitecture;
            Guid.NewGuid().TryWriteBytes(new Span<byte>(_bytes + 32, 16));
            _view.Flush(); // safe-side only; not a fatal-tail durability guarantee
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal long Dropped => Volatile.Read(ref *(long*)(_bytes + 48));

    // Each slot is assigned once. No retry loop, waiting, file I/O or allocation.
    internal long Write(in NativeFaultTraceRecord record)
    {
        if ((ulong)Volatile.Read(ref _reserved) >= (ulong)_capacity)
        {
            Interlocked.Increment(ref *(long*)(_bytes + 48));
            return 0;
        }
        var index = Interlocked.Increment(ref _reserved) - 1;
        if ((ulong)index >= (ulong)_capacity)
        {
            Interlocked.Increment(ref *(long*)(_bytes + 48));
            return 0;
        }
        var slot = (NativeFaultTraceRecord*)(_bytes + HeaderBytes) + index;
        var unpublished = record;
        unpublished.Published = 0;
        *slot = unpublished;
        slot->Sequence = index + 1;
        Volatile.Write(ref slot->Published, 1);
        return index + 1;
    }

    // Ordinary managed inspection only. Readers never inspect uncommitted slots.
    internal NativeFaultTraceRecord[] Snapshot()
    {
        var result = new List<NativeFaultTraceRecord>();
        for (var index = 0; index < _capacity; index++)
        {
            var slot = (NativeFaultTraceRecord*)(_bytes + HeaderBytes) + index;
            if (Volatile.Read(ref slot->Published) != 0) result.Add(*slot);
        }
        return result.ToArray();
    }

    // Test-owned buffers only. Production retains its instance until process exit:
    // disposal is forbidden while any installed handler can reach it.
    public void Dispose()
    {
        if (_bytes != null) _view!.SafeMemoryMappedViewHandle.ReleasePointer();
        _bytes = null;
        _view?.Dispose();
        _file?.Dispose();
    }
}
