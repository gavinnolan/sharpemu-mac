// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static ColorTargetWords BorrowColorWords => new(Address, 0, 0x40730, 0, 0x907FC1FF, 0x4DC6C000, 72, 0, 0, 0, 0);
    private static GuestRenderTarget ProjectBorrowColor(BorrowColorMemory memory)
    {
        var words = BorrowColorWords;
        var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        return MetalColorMipPlacement.Project(memory, normalized, words,
            new GuestRenderTarget(Address, 512, 512, (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27));
    }
    private static AgcExports.TranslatedImageBinding BorrowColorBinding()
    {
        var address = Address + 0xB0000;
        uint[] raw = [(uint)(address >> 8), 0xC4700000, 0x007FC07F, 0x91B00FAC, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var d));
        return new(d, false, 0, [], ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
    }
    private static void SubmitBorrowColor(GuestRenderTarget target)
    {
        var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "managed_color_receipt", Gen5MslStage.Pixel, 0));
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shader, [], [], 0, [target], null, 3, 1, 4, null, [], null, null, 0);
    }

    [Fact]
    public void NativeColorOriginalProducerCopiesFullSelectedSpanInsideOriginalCompleteChain()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        var target = ProjectBorrowColor(memory);
        try
        {
            Assert.Equal((Address, 0x2B0000UL, Address + 0xB0000, 0x200000), memory.Copy);
            Assert.Equal(Address + 0xB0000, target.Address); Assert.Equal(BorrowColorWords, target.Registers);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.PlainReads);
        }
        finally { MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); }
        Assert.Equal(1, memory.Releases);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); Assert.Equal(1, memory.Releases);
    }

    [Theory]
    [InlineData("rejected")] [InlineData("unavailable")] [InlineData("rights")]
    [InlineData("mapping")] [InlineData("watch")] [InlineData("session")]
    [InlineData("content")] [InlineData("throw")]
    public void NativeColorOriginalRefusalDiscardsCopyAndNeverAdoptsYoungerObservation(string variant)
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var memory = new BorrowColorMemory { Variant = variant };
        Assert.ThrowsAny<Exception>(() => ProjectBorrowColor(memory));
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.PlainReads);
        Assert.Equal(variant is "rejected" or "unavailable" or "throw" ? 0 : 1, memory.Releases);
        Assert.Empty((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!);
    }

    [Fact]
    public void NativeColorRepeatedUnadmittedProjectionReturnsEveryOriginalWatch()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        for (var i = 0; i < 160; i++)
        {
            var target = ProjectBorrowColor(memory);
            MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
            MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        }
        Assert.Equal(160, memory.Copies); Assert.Equal(160, memory.Releases);
        Assert.Empty((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeColorActualOrdinarySubmissionOwnsPreparationUntilPendingOrTakenCancellation(bool taken)
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory);
        var target = ProjectBorrowColor(memory); SubmitBorrowColor(target);
        Assert.True((long)Field("_enqueuedGuestWorkSequence").GetValue(null)! > 0);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); Assert.Equal(0, memory.Releases);
        if (taken) _ = TakeVolumeWork();
        InvokeVolume("CompletePresenterShutdown");
        Assert.Equal(1, memory.Releases); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Empty((IDictionary)Field("_nativeColorQueued").GetValue(null)!);
    }

    [Fact]
    public void NativeColorUnsealedActualWorkCompletionLatchesAndCancelsWithoutAcknowledgment()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory);
        var target = ProjectBorrowColor(memory); SubmitBorrowColor(target);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        var pending = TakeVolumeWork();
        var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("CompleteGuestWork", pending));
        Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.Same(error.InnerException, Field("_guestWorkFailure").GetValue(null));
        Assert.Equal(1, memory.Releases); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Theory]
    [InlineData("write")] [InlineData("raw")]
    [InlineData("mapping")] [InlineData("session")]
    public void NativeColorRefusedSampleNeverReadsCpuPixelsOrRefreshesAcceptedProducer(string change)
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory);
        var target = ProjectBorrowColor(memory); SubmitBorrowColor(target);
        var binding = BorrowColorBinding();
        if (change == "write") binding = binding with { Access = GuestImageAccess.Write, IsStorage = true };
        if (change == "raw") binding = binding with { ResourceDescriptor = binding.ResourceDescriptor!.Take(4).ToArray() };
        if (change == "mapping") memory.Current = false;
        if (change == "session") Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentNativeDepthFaceSession + 1);
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.PlainReads);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
    }

    [Fact]
    public void NativeColorOriginalFullProtectedOwnerIsRequiredEvenWithManagedCoverage()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory); var target = ProjectBorrowColor(memory); SubmitBorrowColor(target);
        var ranges = (IDictionary)typeof(GuestImageWriteTracker).GetField("_rangesByAddress", Private)!.GetValue(null)!;
        var original = ranges[Address + 0xB0000]!;
        original.GetType().GetField("Protect")!.SetValue(original, false);
        try
        {
            // Managed coverage remains complete. It cannot substitute for this
            // original native protected owner in the actual snapshot producer.
            Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address + 0xB0000, 0x200000, out _));
            Assert.False(GuestImageWriteTracker.TryGetProtectedObservedContentToken(Address + 0xB0000, 0x200000, out _));
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [BorrowColorBinding()], out _));
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.PlainReads);
        }
        finally { original.GetType().GetField("Protect")!.SetValue(original, true); MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); }
    }
    [Fact]
    public void NativeColorReadonlyCaptureSurvivesFrontendReleaseButOriginalMutationVetoesIt()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory); var target = ProjectBorrowColor(memory); SubmitBorrowColor(target);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [BorrowColorBinding()], out var fallback));
        Assert.Empty(texture.RgbaPixels); Assert.Null(texture.TiledSource); Assert.Equal(0, fallback); Assert.Equal(1, memory.Copies);
        GuestImageWriteTracker.NotifyManagedWrite(target.Address, 1);
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [BorrowColorBinding()], out _));
        MetalVideoPresenter.ReleasePreparedTextureSources([texture]);
        InvokeVolume("CompletePresenterShutdown"); Assert.Equal(1, memory.Releases);
    }

    [Fact]
    public void NativeColorPublicQueuedConsumerAndNextProducerRetainExactPredecessorUntilCancellation()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory);
        var first = ProjectBorrowColor(memory); SubmitBorrowColor(first);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(first);
        var original = ((IDictionary)Field("_nativeColorLatest").GetValue(null)!)[first.Address]!;
        var sampled = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [BorrowColorBinding()], out _));
        var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "managed_color_consumer", Gen5MslStage.Pixel, 0));
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shader, [sampled], [], 0,
            [first with { Address = 0, Registers = null }], null, 3, 1, 4, null, [], null, null, 0);
        MetalVideoPresenter.ReleasePreparedTextureSources([sampled]);
        var second = ProjectBorrowColor(memory); SubmitBorrowColor(second);
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(second);
        // Real public queue ownership, not a synthetic native version/status seal.
        Assert.False((bool)original.GetType().GetField("Retired", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original)!);
        Assert.Equal(2, original.GetType().GetField("DependentReferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original));
        Assert.Equal(0, memory.Releases);
        InvokeVolume("CompletePresenterShutdown");
        Assert.Equal(2, memory.Releases);
        Assert.Empty((IDictionary)Field("_nativeColorQueued").GetValue(null)!);
        Assert.Empty((IEnumerable)Field("_nativeColorProducers").GetValue(null)!);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public void NativeColorPublicAdmissionRevocationCannotInstallPartialQueueOrProducerOwnership(int rejectedQuery)
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(memory); var target = ProjectBorrowColor(memory);
        var original = ((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!).Cast<object>().Single();
        memory.MappingQueries = 0; memory.RejectAtMappingQuery = rejectedQuery;
        Assert.Throws<NotSupportedException>(() => SubmitBorrowColor(target));
        Assert.Equal(rejectedQuery, memory.MappingQueries);
        Assert.Equal(1, original.GetType().GetField("References", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original));
        Assert.Empty((IDictionary)Field("_nativeColorQueued").GetValue(null)!);
        Assert.Empty((IEnumerable)Field("_nativeColorProducers").GetValue(null)!);
        Assert.Empty((IDictionary)Field("_nativeColorLatest").GetValue(null)!);
        Assert.Equal(0L, Field("_enqueuedGuestWorkSequence").GetValue(null));
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        MetalVideoPresenter.ReleasePreparedNativeColorTarget(target);
        Assert.Equal(1, memory.Releases);
    }

    // Explicitly synthetic provider/mapping outcomes exercise production capture
    // and ordinary queue lifetimes only. No native content or ABI proof is claimed.
    private sealed class BorrowColorMemory : ICpuMemory, ICpuMappedReadWatchSource
    {
        internal int Copies, Releases, PlainReads, MappingQueries;
        internal int RejectAtMappingQuery;
        internal bool Current = true, WatchCurrent = true;
        internal string? Variant;
        internal (ulong Footprint, ulong Bytes, ulong Source, int CopyBytes) Copy;
        public bool TryRead(ulong address, Span<byte> bytes) { PlainReads++; throw new InvalidOperationException("No CPU shadow fallback."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? mapping) =>
            throw new InvalidOperationException("Must mint the original watched copy.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? mapping)
        {
            Copies++; Copy = (address, count, source, bytes.Length); mapping = null;
            if (Variant == "rejected") return CpuMappedCopyResult.Rejected;
            if (Variant == "unavailable") return CpuMappedCopyResult.Unavailable;
            if (Variant == "throw") throw new InvalidOperationException("Original copy failed.");
            mapping = new(new object(), address, count, source, (ulong)bytes.Length,
                [new(address, count, address, count, 1, 1, 0x400000, 0, 16384,
                    Variant == "rights" ? GuestPageProtection.Read : GuestPageProtection.Read | GuestPageProtection.Write)]);
            bytes.Fill(0xAE);
            if (Variant == "mapping") Current = false;
            if (Variant == "watch") WatchCurrent = false;
            if (Variant == "session") Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentNativeDepthFaceSession + 1);
            if (Variant == "content") GuestImageWriteTracker.NotifyManagedWrite(source, 1);
            return CpuMappedCopyResult.Copied;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation mapping) =>
            Current && ++MappingQueries != RejectAtMappingQuery;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation mapping) => WatchCurrent;
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation mapping) => Releases++;
    }
    private sealed class BorrowColorState : IDisposable
    {
        private readonly State state = new();
        private readonly object? takenSequence = Field("_takenGuestWorkSequence").GetValue(null);
        private readonly object? takenPrefix = Field("_takenGuestPrefixProducerSequence").GetValue(null);
        private readonly object? takenWork = Field("_takenDepthCubeProducerWork").GetValue(null);
        private readonly IDictionary ranges = (IDictionary)typeof(GuestImageWriteTracker).GetField("_rangesByAddress", Private)!.GetValue(null)!;
        private readonly object trackerGate = typeof(GuestImageWriteTracker).GetField("_gate", Private)!.GetValue(null)!;
        private readonly object? previousProtected;
        private readonly bool placement = MetalColorMipPlacement.Enabled, borrow = MetalVideoPresenter.NativeColorBorrowEnabled;
        private readonly bool graph = MetalVideoPresenter.TextureSourceGraphEnabled;
        private readonly string[] emptyDictionaries = ["_nativeColorLatest", "_nativeColorQueued", "_nativeColorBatches"];
        internal BorrowColorState()
        {
            Field("_takenGuestWorkSequence").SetValue(null, 0L);
            Field("_takenGuestPrefixProducerSequence").SetValue(null, 0L);
            Field("_takenDepthCubeProducerWork").SetValue(null, null);
            foreach (var name in emptyDictionaries) Assert.Empty((IDictionary)Field(name).GetValue(null)!);
            Assert.Empty((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!);
            MetalColorMipPlacement.Enabled = true; MetalVideoPresenter.NativeColorBorrowEnabled = true;
            Field("_textureSourceGraphEnabled").SetValue(null, false);
            // Synthetic complete protected metadata ONLY. No kernel mprotect,
            // fault handler, permission receipt or native byte proof is supplied.
            var range = Activator.CreateInstance(typeof(GuestImageWriteTracker).GetNestedType("TrackedRange", BindingFlags.NonPublic)!, true)!;
            void SetRange(string name, object value) => range.GetType().GetField(name)!.SetValue(range, value);
            var selected = Address + 0xB0000;
            SetRange("Address", selected); SetRange("ByteCount", 0x200000UL); SetRange("Start", selected); SetRange("End", selected + 0x200000);
            SetRange("Protect", true); SetRange("Armed", 1); SetRange("WriteGeneration", 0L);
            lock (trackerGate) { previousProtected = ranges[selected]; ranges[selected] = range; }
            GuestImageWriteTracker.TrackManagedWriter(selected, 0x200000, source: "native-color-managed-only-control");
        }
        public void Dispose()
        {
            lock (Field("_gate").GetValue(null)!) InvokeVolume("CancelQueuedNativeColorLocked", (object?)null);
            ((IDictionary)Field("_colorPhysicalAllocations").GetValue(null)!).Remove(Address);
            lock (trackerGate)
            {
                if (previousProtected is null) ranges.Remove(Address + 0xB0000); else ranges[Address + 0xB0000] = previousProtected;
            }
            GuestImageWriteTracker.UntrackWatchOnly(Address + 0xB0000);
            MetalColorMipPlacement.Enabled = placement; MetalVideoPresenter.NativeColorBorrowEnabled = borrow;
            Field("_textureSourceGraphEnabled").SetValue(null, graph);
            state.Dispose();
            Field("_takenGuestWorkSequence").SetValue(null, takenSequence);
            Field("_takenGuestPrefixProducerSequence").SetValue(null, takenPrefix);
            Field("_takenDepthCubeProducerWork").SetValue(null, takenWork);
        }
    }
}
