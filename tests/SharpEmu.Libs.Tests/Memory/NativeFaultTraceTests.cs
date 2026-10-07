// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

[Collection("GuestImageWriteTracker")]
public sealed unsafe class NativeFaultTraceTests
{
    [Fact]
    public void ConcurrentRecordsArePublishedOnceWithoutOverwritingCapacity()
    {
        var path = NewPath();
        try
        {
            using var buffer = new NativeFaultTraceBuffer(path, 256);
            Parallel.For(0, 1024, index => buffer.Write(new NativeFaultTraceRecord
            { Kind = 1, Address = (ulong)index, RawAddress = (ulong)index, Published = 1 }));
            var records = buffer.Snapshot();
            Assert.Equal(256, records.Length);
            Assert.Equal(768, buffer.Dropped);
            Assert.Equal(256, records.Select(static record => record.Sequence).Distinct().Count());
            Assert.All(records, static record => Assert.Equal(record.Address, record.RawAddress));
            var before = records.Select(static record => record.Address).ToArray();
            Assert.Equal(0, buffer.Write(new NativeFaultTraceRecord { Address = ulong.MaxValue }));
            Assert.Equal(before, buffer.Snapshot().Select(static record => record.Address).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    public void InvalidOrExhaustedCounterCannotAddressOutsideBuffer(long count)
    {
        var path = NewPath();
        try
        {
            using var buffer = new NativeFaultTraceBuffer(path, 1);
            typeof(NativeFaultTraceBuffer).GetField("_reserved", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(buffer, count);
            Assert.Equal(0, buffer.Write(new NativeFaultTraceRecord()));
            Assert.Empty(buffer.Snapshot());
            Assert.Equal(1, buffer.Dropped);
        }
        finally { File.Delete(path); }
    }

    [TrackedWriteFaultFact]
    public void NativeEntryAndProtectionRecordsRetainRawFieldsAndAllocateNothingAfterWarmup()
    {
        var path = NewPath();
        using var buffer = new NativeFaultTraceBuffer(path);
        var previous = GuestImageWriteTracker.ExchangeNativeDiagnosticBufferForTests(buffer);
        var enabled = GuestImageWriteTracker.Enabled;
        void* pointer = null;
        try
        {
            GuestImageWriteTracker.Configure(true);
            pointer = HostMemory.Alloc(null, 4096, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_READWRITE);
            Assert.NotEqual(0, (nint)pointer);
            var address = (ulong)pointer;
            GuestImageWriteTracker.Track(address, 4096);
            var invocation = GuestImageWriteTracker.RecordNativeImageFaultEntry(10, 0, 2, address + 16, 1, 6, 0x1234);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFaultWithNativeDiagnostics(address + 16, invocation));
            GuestImageWriteTracker.RecordNativeImageFaultResult(true, invocation);
            Assert.Equal(HostMemory.PAGE_READWRITE, NativePageProtection.Query(address));
            var records = buffer.Snapshot();
            var entry = Assert.Single(records.Where(r => r.Kind == 1 && r.Address == address + 16));
            Assert.Equal(0UL, entry.RawAddress);
            Assert.Equal(2, entry.SignalCode);
            Assert.Equal(6UL, entry.ErrorCode);
            Assert.Equal(1, entry.Access);
            Assert.Equal(1, entry.OwnerState);
            Assert.True(entry.OwnerIdentity > 0 && entry.ArmEpoch > 0);
            var restored = Assert.Single(records.Where(r => r.Kind == 4 && r.Invocation == entry.Invocation));
            Assert.Equal(0, restored.Result);
            Assert.Equal(0, restored.Errno);
            Assert.Equal(3, restored.Protection);
            Assert.True(restored.Nanoseconds >= entry.Nanoseconds);
            Assert.True(restored.Sequence > entry.Sequence);
            // Buffer and scalar entry/result paths must not allocate in a warmed handler.
            invocation = GuestImageWriteTracker.RecordNativeImageFaultEntry(10, address, 2, address, 1, 6, 0x1234);
            GuestImageWriteTracker.RecordNativeImageFaultResult(false, invocation);
            GuestImageWriteTracker.RecordNativeFaultDisposition(invocation, false, 5, 1);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                invocation = GuestImageWriteTracker.RecordNativeImageFaultEntry(10, address, 2, address, 1, 6, 0x1234);
                GuestImageWriteTracker.RecordNativeImageFaultResult(false, invocation);
                GuestImageWriteTracker.RecordNativeFaultDisposition(invocation, false, 5, 1);
            }
            Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        }
        finally
        {
            if (pointer != null)
            {
                GuestImageWriteTracker.Untrack((ulong)pointer);
                Assert.True(HostMemory.Free(pointer, 0, HostMemory.MEM_RELEASE));
            }
            GuestImageWriteTracker.Configure(enabled);
            GuestImageWriteTracker.ExchangeNativeDiagnosticBufferForTests(previous);
            File.Delete(path);
        }
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"sharpemu-native-trace-{Guid.NewGuid():N}.bin");
}
