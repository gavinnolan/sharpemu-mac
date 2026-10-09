// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    // These controls stop before tracker/provider/native setup. The positive
    // original copy and physical alias proofs belong to the real Mac fixture.
    [Theory]
    [InlineData("short-raw")]
    [InlineData("word2-reserved")]
    [InlineData("word3-view")]
    [InlineData("word4-control")]
    [InlineData("word5-control")]
    [InlineData("word6-reserved")]
    [InlineData("array-shape")]
    [InlineData("storage-shape")]
    [InlineData("dynamic-mip")]
    [InlineData("read-write-access")]
    public void OriginalR32MipZeroInvalidViewRefusesBeforeAnySourceRead(string scenario)
    {
        var binding = R32DisjointManagedBinding();
        var raw = binding.ResourceDescriptor!.ToArray();
        binding = scenario switch
        {
            "short-raw" => binding with { ResourceDescriptor = raw[..7] },
            "word2-reserved" => ChangeWord(2, 0x80000000),
            "word3-view" => ChangeWord(3, 1),
            "word4-control" => ChangeWord(4, 1),
            "word5-control" => ChangeWord(5, 1),
            "word6-reserved" => ChangeWord(6, 1),
            "array-shape" => binding with { IsArrayed = true },
            "storage-shape" => binding with { Shape = new(false, false, true, false, TextureNumericClass.Float) },
            "dynamic-mip" => binding with { DynamicMip = true },
            "read-write-access" => binding with { Access = GuestImageAccess.Read | GuestImageAccess.Write },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var memory = new R32DisjointNoReadMemory();
        var failure = Assert.Throws<TargetInvocationException>(() => R32DisjointManagedSnapshots(memory, binding));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Writes);

        AgcExports.TranslatedImageBinding ChangeWord(int index, uint bit)
        {
            raw[index] ^= bit;
            return binding with { ResourceDescriptor = raw };
        }
    }

    [Fact]
    public void OriginalR32MipZeroMissingMappedWatchProviderNeverFallsBackToOrdinaryRead()
    {
        var memory = new R32DisjointNoReadMemory();
        var failure = Assert.Throws<TargetInvocationException>(() => R32DisjointManagedSnapshots(memory, R32DisjointManagedBinding()));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Contains("mapped read-watch provider", failure.InnerException!.Message);
        Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalR32MipZeroPublicPendingOrTakenGlobalWriterRefusesBeforeProvider(bool taken)
    {
        using var queue = new SelectedMipQueueScope();
        var takenFields = new[] { "_takenGuestWorkSequence", "_takenGuestPrefixProducerSequence", "_takenDepthCubeProducerWork" }
            .Select(name => Presenter.GetField(name, Methods)!).ToArray();
        var old = takenFields.Select(field => field.GetValue(null)).ToArray();
        try
        {
            var buffer = new GuestMemoryBuffer(0x7092CC0000, new byte[4], 4, 4, false, Writable: true);
            var sequence = (long)Presenter.GetMethod("SubmitComputeDispatch", Methods)!.Invoke(null,
                [0UL, null, Array.Empty<GuestDrawTexture>(), new[] { buffer }, 1u, 1u, 1u, 0u, 0u, 0u, true, null])!;
            Assert.True(sequence > 0);
            if (taken)
            {
                object?[] arguments = [null];
                Assert.True((bool)Presenter.GetMethod("TryTakeGuestWork", Methods)!.Invoke(null, arguments)!);
                Assert.Equal(sequence, arguments[0]!.GetType().GetProperty("Sequence")!.GetValue(arguments[0]));
            }
            var memory = new R32DisjointProviderMustNotRun();
            var failure = Assert.Throws<TargetInvocationException>(() => R32DisjointManagedSnapshots(memory, R32DisjointManagedBinding()));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Contains("unqualified ordinary image, buffer or guest writer", failure.InnerException!.Message);
            Assert.Equal(0, memory.ProviderCalls); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Writes);
            // A real public admission/take occurred, but no shader, CPU copy,
            // encoding, completion or acknowledgement is supplied by this test.
            Assert.Equal(sequence, (long)Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null)!);
            Assert.Equal(taken ? 0 : 1, (int)Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null)!);
        }
        finally
        {
            for (var index = 0; index < takenFields.Length; index++) takenFields[index].SetValue(null, old[index]);
        }
    }

    private static AgcExports.TranslatedImageBinding R32DisjointManagedBinding()
    {
        uint[] raw = [0x7092C800, 0xC1600000, 0x003FC07F, 0x90900924, 0, 0x00700080, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, false, 0, [], ResourceDescriptor: raw,
            Shape: new(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
    }

    private static object? R32DisjointManagedSnapshots(ICpuMemory memory, AgcExports.TranslatedImageBinding binding)
    {
        var snapshots = Presenter.Assembly.GetTypes().Single(type => type.Name == "MetalTextureSnapshots");
        return snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
            [new CpuContext(memory, Generation.Gen5), new[] { binding }, 0]);
    }

    private class R32DisjointNoReadMemory : ICpuMemory
    {
        internal int Reads, Writes;
        public bool TryRead(ulong address, Span<byte> destination)
        { Reads++; throw new InvalidOperationException("Refusing metadata must never read original bytes."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        { Writes++; throw new InvalidOperationException("Refusing metadata must never write original bytes."); }
    }

    private sealed class R32DisjointProviderMustNotRun : R32DisjointNoReadMemory, ICpuMappedReadWatchSource
    {
        internal int ProviderCalls;
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? original)
        { original = null; ProviderCalls++; throw new InvalidOperationException("Pending writer veto must precede provider copy."); }
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? original) => TryReadMappedCopy(address, count, source, bytes, out original);
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original)
        { ProviderCalls++; throw new InvalidOperationException("No original mapping should have been acquired."); }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original)
        { ProviderCalls++; throw new InvalidOperationException("No original watch should have been acquired."); }
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original)
        { ProviderCalls++; throw new InvalidOperationException("No original watch should need release."); }
    }
}
