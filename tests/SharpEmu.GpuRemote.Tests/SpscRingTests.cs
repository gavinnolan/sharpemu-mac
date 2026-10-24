// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using Xunit;

namespace SharpEmu.GpuRemote.Tests;

public sealed unsafe class SpscRingTests
{
    private const int Capacity = 4096;

    [Fact]
    public void RecordsSurviveManyWrapsInOrder()
    {
        using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
        var writer = new RingWriter(block.Pointer, Capacity, block.PeerState);
        var reader = new RingReader(block.Pointer, Capacity, block.PeerState);
        var random = new Random(1234);
        var expected = new Queue<(uint Type, byte[] Bytes)>();
        Span<byte> payload = stackalloc byte[writer.MaxPayloadLength];
        void DrainOne()
        {
            Assert.True(reader.TryRead(out var type, out var read));
            var (expectedType, expectedBytes) = expected.Dequeue();
            Assert.Equal(expectedType, type);
            Assert.Equal(expectedBytes, read.ToArray());
            reader.Advance();
        }

        for (var round = 0; round < 20_000; round++)
        {
            var length = random.Next(0, round % 7 == 0 ? writer.MaxPayloadLength + 1 : 200);
            var record = payload[..length];
            random.NextBytes(record);
            var type = (uint)(round + 1);
            while (!writer.TryWrite(type, record))
            {
                DrainOne();
            }

            expected.Enqueue((type, record.ToArray()));

            // Drain in uneven batches so the write position drifts across the whole area.
            for (var drain = random.Next(0, 3); drain > 0 && expected.Count > 0; drain--)
            {
                DrainOne();
            }
        }

        while (expected.Count > 0)
        {
            DrainOne();
        }

        Assert.False(reader.TryRead(out _, out _));
    }

    [Fact]
    public void FullRingRefusesUntilTheReaderAdvances()
    {
        using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
        var writer = new RingWriter(block.Pointer, Capacity, block.PeerState);
        var reader = new RingReader(block.Pointer, Capacity, block.PeerState);
        var written = 0;
        while (writer.TryWrite(5, new byte[56]))
        {
            written++;
        }

        // 64-byte records exactly tile the area.
        Assert.Equal(Capacity / 64, written);
        Assert.True(reader.TryRead(out var type, out var payload));
        Assert.Equal(5u, type);
        Assert.Equal(56, payload.Length);
        reader.Advance();
        Assert.True(writer.TryWrite(5, new byte[56]));
        Assert.False(writer.TryWrite(5, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void LargestRecordFitsAnEmptyRingWhereverItStarts()
    {
        for (var start = 0; start < Capacity; start += 8)
        {
            using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
            var writer = new RingWriter(block.Pointer, Capacity, block.PeerState);
            var reader = new RingReader(block.Pointer, Capacity, block.PeerState);
            // Walk the (empty) ring's position to the start with records of at most 1 KiB.
            for (var position = 0; position < start;)
            {
                var size = Math.Min(1024, start - position);
                Assert.True(writer.TryWrite(1, new byte[size - SpscRing.RecordHeaderSize]));
                Assert.True(reader.TryRead(out _, out _));
                reader.Advance();
                position += size;
            }

            var largest = new byte[writer.MaxPayloadLength];
            largest[0] = 0x11;
            largest[^1] = 0x22;
            Assert.True(writer.TryWrite(2, largest), $"start {start}");
            Assert.True(reader.TryRead(out var type, out var payload));
            Assert.Equal(2u, type);
            Assert.Equal(largest, payload.ToArray());
        }
    }

    [Fact]
    public void TwoPhaseWriteFillsTheRecordInPlace()
    {
        using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
        var writer = new RingWriter(block.Pointer, Capacity, block.PeerState);
        var reader = new RingReader(block.Pointer, Capacity, block.PeerState);
        Assert.True(writer.TryBeginWrite(9, 12, out var destination));
        Assert.False(reader.HasData);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, 0xDEADBEEF);
        Assert.Throws<InvalidOperationException>(() => writer.TryBeginWrite(9, 1, out _));
        writer.EndWrite();
        Assert.True(reader.TryRead(out var type, out var payload));
        Assert.Equal(9u, type);
        Assert.Equal(0xDEADBEEF, BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    [Fact]
    public void RejectsOversizedPayloadsAndThePaddingType()
    {
        using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
        var writer = new RingWriter(block.Pointer, Capacity, block.PeerState);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.TryWrite(1, new byte[writer.MaxPayloadLength + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.TryWrite(SpscRing.PaddingType, ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpscRing.BlockSize(3000));
    }

    [MacOSFact]
    public void ConcurrentProducerAndConsumerAgreeOnEveryByte()
    {
        const int Records = 400_000;
        using var block = new NativeBlock(SpscRing.BlockSize(16 * 1024));
        var writer = new RingWriter(block.Pointer, 16 * 1024, block.PeerState);
        var reader = new RingReader(block.Pointer, 16 * 1024, block.PeerState);
        Exception? producerFailure = null;
        var producer = new Thread(() =>
        {
            try
            {
                var random = new Random(99);
                Span<byte> payload = stackalloc byte[512];
                for (var i = 0; i < Records; i++)
                {
                    var length = random.Next(4, 512);
                    Fill(payload[..length], i);
                    // Zero spin on some records so the doorbell path runs too.
                    if (!writer.Write((uint)(i & 0xFFFF) + 1, payload[..length], 10_000_000_000, i % 3 == 0 ? 0 : 2_000))
                    {
                        throw new TimeoutException($"Write {i} timed out.");
                    }
                }
            }
            catch (Exception exception)
            {
                producerFailure = exception;
            }
        });
        producer.Start();

        var verifier = new Random(99);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Records; i++)
        {
            uint type;
            ReadOnlySpan<byte> payload;
            while (!reader.TryRead(out type, out payload))
            {
                _ = reader.WaitForData(1_000_000_000, i % 5 == 0 ? 0 : 2_000);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60) && producerFailure is null, $"record {i} never came: {producerFailure}");
            }

            Assert.Equal((uint)(i & 0xFFFF) + 1, type);
            Assert.Equal(verifier.Next(4, 512), payload.Length);
            Assert.True(Matches(payload, i), $"record {i} is corrupt");
            reader.Advance();
        }

        producer.Join();
        Assert.Null(producerFailure);
        Assert.False(reader.HasData);
    }

    [MacOSFact]
    public void WaitForDataTimesOut()
    {
        using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
        var reader = new RingReader(block.Pointer, Capacity, block.PeerState);
        var watch = Stopwatch.StartNew();
        Assert.False(reader.WaitForData(30_000_000, spinNanoseconds: 0));
        Assert.InRange(watch.ElapsedMilliseconds, 25, 2_000);
    }

    [MacOSFact]
    public void BlockedWriterWakesWhenTheReaderAdvances()
    {
        using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
        var writer = new RingWriter(block.Pointer, Capacity, block.PeerState);
        var reader = new RingReader(block.Pointer, Capacity, block.PeerState);
        while (writer.TryWrite(1, new byte[1000]))
        {
        }

        var wrote = false;
        var blocked = new Thread(() => wrote = writer.Write(2, new byte[1000], 10_000_000_000, spinNanoseconds: 0));
        blocked.Start();
        Thread.Sleep(50);
        Assert.True(blocked.IsAlive);
        Assert.True(reader.TryRead(out _, out _));
        reader.Advance();
        Assert.True(blocked.Join(5_000));
        Assert.True(wrote);
    }

    [MacOSFact]
    public void WaitsEndWhenThePeerExits()
    {
        using var block = new NativeBlock(SpscRing.BlockSize(Capacity));
        var reader = new RingReader(block.Pointer, Capacity, block.PeerState);
        bool? result = null;
        var waiter = new Thread(() => result = reader.WaitForData(-1, spinNanoseconds: 0));
        waiter.Start();
        Thread.Sleep(50);
        Assert.True(waiter.IsAlive);
        block.MarkPeerExited();
        new Doorbell(&((RingControl*)block.Pointer)->DataBell).WakeAll();
        Assert.True(waiter.Join(5_000));
        Assert.False(result);
    }

    private static void Fill(Span<byte> payload, int seed)
    {
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(seed * 31 + i);
        }
    }

    private static bool Matches(ReadOnlySpan<byte> payload, int seed)
    {
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != (byte)(seed * 31 + i))
            {
                return false;
            }
        }

        return true;
    }
}
