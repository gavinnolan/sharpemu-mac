// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Genuine ledger copies over owned pinned CLR bytes. These controls do not
    // execute native selectors or manufacture a kernel/GPU completion receipt.
    private sealed unsafe class DisplayLedgerMemory : ICpuMemory, ICpuMappedCopySource, IDisposable
    {
        internal const int ByteCount = 65536;
        internal readonly object Gate = new();
        internal readonly HostMappedCopyLedger Ledger;
        internal readonly HostBackingObject Backing;
        private GCHandle pin;
        internal int Copies, Reads;
        internal Action? AfterCopy;
        internal CpuMappedCopyResult? Result;
        internal Exception? CopyFailure;
        internal CpuMappedCopyObservation? First;
        internal DisplayLedgerMemory()
        {
            var storage = new byte[ByteCount + 16384];
            pin = GCHandle.Alloc(storage, GCHandleType.Pinned);
            var alias = ((ulong)pin.AddrOfPinnedObject() + 16383) & ~16383UL;
            Backing = new(alias, ByteCount, 16384, _ => new(HostBackingReleaseStatus.Released));
            Ledger = new(Gate, 16384);
            lock (Gate) Ledger.RegisterBacking(Backing);
            Map();
            for (var i = 0; i < ByteCount; i++) ((byte*)alias)[i] = (byte)(i * 17 + (i >> 8));
        }
        internal void Map()
        {
            lock (Gate)
            {
                var view = Ledger.PrepareView(Backing, Address, ByteCount, 0, HostPageProtection.ReadWrite);
                Assert.NotNull(view); Assert.True(Ledger.BeforeNativeEffect(Address, ByteCount)); Ledger.PublishView(view!);
            }
        }
        internal byte[] Expected => new ReadOnlySpan<byte>((void*)Backing.AliasBase, ByteCount).ToArray();
        public bool TryRead(ulong address, Span<byte> destination)
        { Reads++; Assert.Equal(Address, address); Assert.Equal(ByteCount, destination.Length); Expected.CopyTo(destination); return true; }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong footprint, ulong source,
            Span<byte> destination, out CpuMappedCopyObservation? observation)
        {
            Copies++; observation = null;
            if (CopyFailure is not null) throw CopyFailure;
            if (Result is { } forced) { destination.Fill(0xEE); return forced; }
            var result = Ledger.TryReadMappedCopy(Backing, address, footprint, source, destination, out observation);
            First ??= observation;
            AfterCopy?.Invoke(); return result;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => Ledger.IsCurrent(Backing, observation);
        public void Dispose() { Backing.Dispose(); if (pin.IsAllocated) pin.Free(); }
    }
    private static object? ReadOriginalDisplay(DisplayLedgerMemory memory, byte[] bytes, string method = "ReadDisplayOriginalSourceCore")
    {
        object?[] args = [memory, Address, 64u, 128u, 128u, 4u, MtlPixelFormat.Rgba8Unorm,
            MetalVideoPresenter.CurrentStorageCpuSeedSession, bytes, DisplayLedgerMemory.ByteCount, null];
        try { Assert.True((bool)Presenter.GetMethod(method, Private)!.Invoke(null, args)!); }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); }
        return args[^1];
    }
    private static object Property(object original, string name) => original.GetType().GetProperty(name)!.GetValue(original)!;
    private static void ValidateOriginalDisplay(object original)
    {
        try { Presenter.GetMethod("ValidateDisplayOriginalCopy", Private)!.Invoke(null, [original]); }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); }
    }

    [Fact]
    public void DisplayOriginalFullPitchedCopyKeepsPaddingAndOriginalLedgerObservation()
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        var bytes = new byte[DisplayLedgerMemory.ByteCount];
        var original = ReadOriginalDisplay(memory, bytes)!;
        Assert.Equal(memory.Expected, bytes); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        Assert.Same(memory.First, Property(original, "Mapping")); Assert.Equal(128u, Property(original, "Pitch"));
        ValidateOriginalDisplay(original);
    }

    [Theory]
    [InlineData("rejected")] [InlineData("mapping")] [InlineData("content")] [InlineData("session")]
    public void DisplayOriginalRejectsChangedCopyWithoutLegacyFallbackOrRecapture(string kind)
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        if (kind == "rejected") memory.Result = CpuMappedCopyResult.Rejected;
        if (kind == "mapping") memory.AfterCopy = memory.Map;
        if (kind == "content") memory.AfterCopy = () => GuestImageWriteTracker.NotifyManagedWrite(Address, 1);
        if (kind == "session") memory.AfterCopy = () => Field("_selectedStorageMipSession").SetValue(null,
            MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
        Assert.Throws<NotSupportedException>(() => ReadOriginalDisplay(memory, new byte[DisplayLedgerMemory.ByteCount]));
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void DisplayUnavailableOverwritesPartialOptionalBytesWithoutPositiveOrigin()
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory { Result = CpuMappedCopyResult.Unavailable };
        var bytes = new byte[DisplayLedgerMemory.ByteCount];
        Assert.Null(ReadOriginalDisplay(memory, bytes)); Assert.Equal(memory.Expected, bytes);
        Assert.Equal(1, memory.Copies); Assert.Equal(1, memory.Reads);
    }

    [Fact]
    public void DisplayOriginalPropagatesProviderFailureAndNeverReadsAgain()
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        var original = new IOException("original display copy failure"); memory.CopyFailure = original;
        Assert.Same(original, Assert.Throws<IOException>(() => ReadOriginalDisplay(memory, new byte[DisplayLedgerMemory.ByteCount])));
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void DisplayOriginalSameAddressAbaNeverRestampsItsCapturedMapping()
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        var original = ReadOriginalDisplay(memory, new byte[DisplayLedgerMemory.ByteCount])!;
        var mapping = Property(original, "Mapping"); memory.Map();
        Assert.Throws<NotSupportedException>(() => ValidateOriginalDisplay(original));
        Assert.Same(mapping, Property(original, "Mapping")); Assert.Equal(1, memory.Copies);
    }

    [Fact]
    public void DisplayDefaultOffUsesOnlyExistingCompleteOrdinaryRead()
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        Assert.False(MetalVideoPresenter.RgbaStorageRuntimePublicationRequested);
        var bytes = new byte[DisplayLedgerMemory.ByteCount];
        Assert.Null(ReadOriginalDisplay(memory, bytes, "ReadDisplayOriginalSource"));
        Assert.Equal(memory.Expected, bytes); Assert.Equal(0, memory.Copies); Assert.Equal(1, memory.Reads);
    }

    [Theory]
    [InlineData("texture")] [InlineData("width")] [InlineData("height")] [InlineData("depth")]
    [InlineData("array")] [InlineData("type")] [InlineData("version")] [InlineData("session")]
    public void DisplayOriginalScalarOwnerRejectsIdentityShapeVersionAndSessionDrift(string kind)
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        var original = ReadOriginalDisplay(memory, new byte[DisplayLedgerMemory.ByteCount])!;
        // Scalar owner validator only; this sentinel is NEVER sent to Metal and
        // cannot qualify a native texture, command buffer or hardware execution.
        var image = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, nonPublic: true)!;
        foreach (var (name, value) in new (string, object)[] { ("Texture", (nint)123), ("Width", 64u),
            ("Height", 128u), ("Initialized", true), ("ContentVersion", 1), ("Format", MtlPixelFormat.Rgba8Unorm) }) Set(image, name, value);
        var owner = Activator.CreateInstance(Presenter.GetNestedType("DisplayOriginalOwner", BindingFlags.NonPublic)!,
            [original, (nint)123, 1])!;
        var session = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        var validate = Presenter.GetMethod("IsDisplayOriginalOwnerCurrentLocked", Private)!;
        Assert.True((bool)validate.Invoke(null, [image, owner, memory, session])!);
        switch (kind)
        {
            case "texture": Set(image, "Texture", (nint)124); break;
            case "width": Set(image, "Width", 65u); break;
            case "height": Set(image, "Height", 129u); break;
            case "depth": Set(image, "Depth", 2u); break;
            case "array": Set(image, "ArrayLength", 2u); break;
            case "type": Set(image, "TextureType", 3u); break;
            case "version": Set(image, "ContentVersion", 0); break;
            case "session": session++; break;
        }
        Assert.False((bool)validate.Invoke(null, [image, owner, memory, session])!);
        Assert.Same(memory.First, Property(original, "Mapping")); Assert.Equal(1, memory.Copies);
    }

    [Fact]
    public void DisplayOriginalPhysicalComparisonUsesCanonicalBackingIdentity()
    {
        ActualArchitecture(); using var state = new State(); using var memory = new DisplayLedgerMemory();
        using var other = new DisplayLedgerMemory();
        var original = ReadOriginalDisplay(memory, new byte[DisplayLedgerMemory.ByteCount])!;
        var otherOriginal = ReadOriginalDisplay(other, new byte[DisplayLedgerMemory.ByteCount])!;
        var compare = Presenter.GetMethod("PhysicalMappingOverlaps", Private)!;
        Assert.True((bool)compare.Invoke(null, [Property(original, "Mapping"), Property(original, "Mapping")])!);
        // Equal virtual addresses are NOT evidence of shared physical backing.
        Assert.False((bool)compare.Invoke(null, [Property(original, "Mapping"), Property(otherOriginal, "Mapping")])!);
        Assert.Equal(1, memory.Copies); Assert.Equal(1, other.Copies);
    }
}
