// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private static readonly Type StoragePlacement = Presenter.Assembly.GetType("SharpEmu.Gpu.Metal.MetalStoragePhysicalPlacement")!;
    private static object[]? storagePlacementQuarantine;
    private const ulong PlacementAllocation = 0xC0000;

    // Literal captured descriptors; these rows test original provider placement,
    // not a synthesized CPU-content or native-producer authority.
    private static uint[] PlacementRaw(int kind) => kind switch
    {
        0 => [0x715D2000, 0xC1D00000, 0x003FC03F, 0x91B0022C, 0, 0x00700000, 0xC07B0000, 0x00704A16],
        1 => [0x704A1700, 0xC0D00000, 0x003FC03F, 0x91B00204, 0, 0x00700000, 0x103B0000, 0x00704A19],
        2 => [0x704A1A00, 0xC0D00000, 0x003FC03F, 0x90900924, 0, 0x00700000, 0, 0],
        3 => [0x70492500, 0xC0700000, 0x0005C00B, 0x90900204, 0, 0x00700000, 0, 0],
        4 => [0x70492300, 0xC4700000, 0x000FC00F, 0x90900FAC, 0, 0x00700000, 0, 0],
        5 => [0x7092C800, 0xC1600000, 0x003FC07F, 0x90900924, 0, 0x00700080, 0, 0],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static GuestDrawTexture PlacementTexture(int kind, ulong address)
    {
        var raw = PlacementRaw(kind);
        raw[0] = checked((uint)(address >> 8));
        raw[1] = (raw[1] & 0xFFFFFF00u) | checked((uint)(address >> 40));
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(address, descriptor.Width, descriptor.Height, descriptor.Format, descriptor.NumberType,
            [], IsFallback: false, IsStorage: true, ResourceMipLevels: descriptor.ResourceMipLevels, Pitch: descriptor.Pitch,
            TileMode: descriptor.TileMode, DstSelect: descriptor.DstSelect, Type: descriptor.Type, Depth: descriptor.Depth,
            Descriptor: raw, Shape: new(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
    }

    private static object PlacementCall(string method, params object?[] arguments)
    {
        try { return StoragePlacement.GetMethod(method, Methods)!.Invoke(null, arguments)!; }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw; }
    }

    private static CpuMappedCopyObservation PlacementMapping(object observation) =>
        (CpuMappedCopyObservation)observation.GetType().GetProperty("Mapping")!.GetValue(observation)!;
    private static (long Backing, ulong Offset, ulong Bytes) PlacementSpan(object span) =>
        ((long)span.GetType().GetProperty("BackingIdentity")!.GetValue(span)!,
         (ulong)span.GetType().GetProperty("Offset")!.GetValue(span)!,
         (ulong)span.GetType().GetProperty("ByteCount")!.GetValue(span)!);

    [StoragePlacementProviderTheory]
    [InlineData(0, 0x40000UL, 0UL, 0x40000UL)]
    [InlineData(1, 0x20000UL, 0UL, 0x20000UL)]
    [InlineData(2, 0x20000UL, 0UL, 0x20000UL)]
    [InlineData(3, 0x10000UL, 0UL, 0x10000UL)]
    [InlineData(4, 0x10000UL, 0UL, 0x10000UL)]
    [InlineData(5, 0xC0000UL, 0x40000UL, 0x80000UL)]
    public void ActualMacStoragePlacementCapturesFullOriginalWriteFootprint(int kind, ulong allocationBytes, ulong offset, ulong writeBytes)
    {
        PlacementAssertRuntime();
        var fixture = new PlacementProviderFixture(); Exception? primary = null;
        try
        {
            var texture = PlacementTexture(kind, fixture.Address);
            var originalRaw = texture.Descriptor!.ToArray();
            var observation = PlacementCall("Capture", fixture.Memory, texture, 47L);
            var mapping = PlacementMapping(observation);
            Assert.Equal((fixture.Address, allocationBytes, fixture.Address + offset, 8UL),
                (mapping.FootprintAddress, mapping.FootprintByteCount, mapping.SourceAddress, mapping.SourceByteCount));
            var spans = ((Array)PlacementCall("WriteSpans", observation)).Cast<object>().ToArray();
            var full = Assert.Single(spans);
            var physical = PlacementSpan(full);
            Assert.Equal((offset, writeBytes), (physical.Offset, physical.Bytes));
            Assert.True(physical.Backing > 0 && writeBytes > mapping.SourceByteCount);
            Assert.True(fixture.Memory.IsMappedCopyObservationCurrent(mapping));
            Assert.All(mapping.Segments, segment =>
            {
                Assert.True(segment.MappingIncarnation > 0 && segment.BackingIdentity == physical.Backing);
                Assert.Equal(fixture.KernelPage, segment.ProviderPageSize);
                Assert.True(segment.MappingBackingOffset + segment.MappingByteCount <= segment.BackingLogicalByteCount);
            });
            var retained = (GuestDrawTexture)observation.GetType().GetProperty("Original")!.GetValue(observation)!;
            Assert.NotSame(texture.Descriptor, retained.Descriptor);
            texture.Descriptor[7] ^= 1;
            Assert.Equal(originalRaw, retained.Descriptor);
            Assert.Equal(47L, observation.GetType().GetProperty("Session")!.GetValue(observation));
            Assert.Empty(retained.RgbaPixels); // eight discarded bytes are not an upload
            Assert.Equal(0, fixture.Memory.OrdinaryReads);
            fixture.AssertBytesUnchanged();
            output.WriteLine($"storage-placement case={kind}; fullAllocation={allocationBytes}; fullWrite={writeBytes}; probe=8; kernel={fixture.KernelPage}; original-only/no-native/no-queue");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { fixture.DisposePreserving(primary); }
    }

    [StoragePlacementProviderTheory]
    [InlineData("readonly")]
    [InlineData("protect")]
    [InlineData("replacement")]
    [InlineData("physical-alias")]
    [InlineData("full-footprint-gap")]
    public void ActualMacStoragePlacementRightsLifetimeAndPhysicalAliases(string kind)
    {
        PlacementAssertRuntime();
        var fixture = new PlacementProviderFixture(readOnly: kind == "readonly"); Exception? primary = null;
        try
        {
            var texture = PlacementTexture(5, fixture.Address);
            if (kind == "readonly")
            {
                Assert.Throws<InvalidOperationException>(() => PlacementCall("Capture", fixture.Memory, texture, 47L));
                var copied = Assert.Single(fixture.Memory.Copies);
                Assert.All(copied.Segments, segment => Assert.Equal(GuestPageProtection.Read, segment.GuestAccess));
            }
            else if (kind == "full-footprint-gap")
            {
                Assert.True(fixture.Views.Unmap(fixture.Address, PlacementAllocation, out var preserved)); Assert.True(preserved);
                // The exact source containing the discarded 8 bytes exists. The
                // rest of the allocation does not: it cannot certify full writes.
                Assert.True(fixture.Views.TryMapReservedRange(fixture.Address + 0x40000, 0x10000, 0x40000, HostPageProtection.ReadWrite, out _));
                var probe = new byte[8];
                Assert.Equal(CpuMappedCopyResult.Copied, fixture.Views.TryReadMappedCopy(fixture.Address + 0x40000,
                    0x10000, fixture.Address + 0x40000, probe, out _));
                Assert.Throws<InvalidOperationException>(() => PlacementCall("Capture", fixture.Memory, texture, 47L));
                Assert.Empty(fixture.Memory.Copies);
            }
            else
            {
                var observation = PlacementCall("Capture", fixture.Memory, texture, 47L);
                var original = PlacementMapping(observation);
                if (kind == "protect")
                {
                    Assert.True(fixture.Host.ChangeAccess(fixture.Address, PlacementAllocation, HostPageProtection.ReadOnly));
                    Assert.False(fixture.Memory.IsMappedCopyObservationCurrent(original));
                    Assert.Throws<InvalidOperationException>(() => PlacementCall("Capture", fixture.Memory, texture, 47L));
                }
                else if (kind == "replacement")
                {
                    Assert.True(fixture.Views.Unmap(fixture.Address, PlacementAllocation, out var preserved)); Assert.True(preserved);
                    Assert.True(fixture.Views.TryMapReservedRange(fixture.Address, PlacementAllocation, PlacementAllocation, HostPageProtection.ReadWrite, out _));
                    Assert.False(fixture.Memory.IsMappedCopyObservationCurrent(original));
                    var replacement = PlacementMapping(PlacementCall("Capture", fixture.Memory, texture, 47L));
                    Assert.NotSame(original, replacement);
                    Assert.Equal(PlacementAllocation, Assert.Single(replacement.Segments).MappingBackingOffset);
                    Assert.NotEqual(Assert.Single(original.Segments).MappingIncarnation, Assert.Single(replacement.Segments).MappingIncarnation);
                    Assert.False(fixture.Memory.IsMappedCopyObservationCurrent(original));
                }
                else
                {
                    Assert.Equal("physical-alias", kind);
                    var aliasAddress = fixture.MapAlias(0);
                    var alias = PlacementCall("Capture", fixture.Memory, PlacementTexture(5, aliasAddress), 47L);
                    var firstSpan = Assert.Single(((Array)PlacementCall("WriteSpans", observation)).Cast<object>());
                    var aliasSpan = Assert.Single(((Array)PlacementCall("WriteSpans", alias)).Cast<object>());
                    Assert.NotEqual(texture.Address, aliasAddress);
                    Assert.True((bool)PlacementCall("Overlap", firstSpan, aliasSpan));
                    var oldAlias = PlacementMapping(alias);
                    Assert.True(fixture.Views.Unmap(aliasAddress, PlacementAllocation, out var preserved)); Assert.True(preserved);
                    Assert.True(fixture.Views.TryMapReservedRange(aliasAddress, PlacementAllocation, PlacementAllocation, HostPageProtection.ReadWrite, out _));
                    Assert.False(fixture.Memory.IsMappedCopyObservationCurrent(oldAlias));
                    var disjoint = PlacementCall("Capture", fixture.Memory, PlacementTexture(5, aliasAddress), 47L);
                    var disjointSpan = Assert.Single(((Array)PlacementCall("WriteSpans", disjoint)).Cast<object>());
                    Assert.False((bool)PlacementCall("Overlap", firstSpan, disjointSpan));
                    Assert.True(fixture.Memory.IsMappedCopyObservationCurrent(original));
                }
            }
            Assert.Equal(0, fixture.Memory.OrdinaryReads);
            fixture.AssertBytesUnchanged();
            output.WriteLine($"storage-placement case={kind}; kernel={fixture.KernelPage}; original-only/no-native/no-queue");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { fixture.DisposePreserving(primary); }
    }

    [Theory]
    [InlineData("sampled")]
    [InlineData("depth")]
    [InlineData("arrayed")]
    [InlineData("array-layers")]
    [InlineData("mip")]
    [InlineData("pitch")]
    [InlineData("raw-size")]
    [InlineData("shape")]
    [InlineData("unknown-access")]
    public void StoragePlacementRefusesInconsistentOriginalMetadataBeforeProviderCopy(string kind)
    {
        var texture = PlacementTexture(5, 0x7092C80000);
        texture = kind switch
        {
            "sampled" => texture with { IsStorage = false },
            "depth" => texture with { Depth = 2 },
            "arrayed" => texture with { ArrayedView = true },
            "array-layers" => texture with { ArrayLayers = 2 },
            "mip" => texture with { MipLevel = 1 },
            "pitch" => texture with { Pitch = texture.Pitch + 1 },
            "raw-size" => texture with { Descriptor = texture.Descriptor![..7] },
            "shape" => texture with { Shape = new(false, false, false, false, TextureNumericClass.Float) },
            "unknown-access" => texture with { Access = GuestImageAccess.Unknown },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var memory = new PlacementForbiddenMemory();
        Assert.Throws<NotSupportedException>(() => PlacementCall("Capture", memory, texture, 47L));
        Assert.Equal(0, memory.Calls);
    }

    private void PlacementAssertRuntime()
    {
        Assert.True(OperatingSystem.IsMacOS());
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_STORAGE_PLACEMENT"));
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        Assert.Equal(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"));
        output.WriteLine($"storage-placement actualCLR={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; actual-Mac-mapping-only");
    }

    private sealed class PlacementForbiddenMemory : ICpuMemory, ICpuMappedCopySource
    {
        internal int Calls;
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { Calls++; observation = null; throw new InvalidOperationException("Metadata refusal must precede provider admission."); }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => throw new InvalidOperationException();
        public bool TryRead(ulong address, Span<byte> bytes) => throw new InvalidOperationException();
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException();
    }

    private sealed class PlacementProviderFixture : IDisposable
    {
        internal readonly IHostViewMemory Host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
        internal readonly SharedBackingViews Views;
        internal readonly R32DisjointMemory Memory;
        internal readonly ulong KernelPage;
        private readonly List<SelectedMappedSeedHole> holes = [];
        private readonly byte[] initial = [];
        internal ulong Address => holes[0].Address;
        internal PlacementProviderFixture(bool readOnly = false)
        {
            Assert.Null(storagePlacementQuarantine);
            Views = new(Host, 2 * PlacementAllocation); Memory = new(Views);
            try
            {
                var state = typeof(SharpEmu.Host.MacOS.MacOSHost).Assembly.GetType("SharpEmu.Host.MacOS.MacMappedCopyState")!;
                KernelPage = Assert.IsType<HostMappedCopyLedger>(state.GetProperty("Ledger", Methods)!.GetValue(null)).NativePageSize;
                Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var expected));
                Assert.Equal(expected, KernelPage);
                Assert.True(Views.IsAvailable);
                var hole = new SelectedMappedSeedHole(Host); holes.Add(hole);
                Assert.True(Address < 1UL << 40);
                Assert.True(Views.TryMapReservedRange(Address, PlacementAllocation, 0,
                    readOnly ? HostPageProtection.ReadOnly : HostPageProtection.ReadWrite, out _));
                initial = new byte[checked((int)Views.Size)];
                // Read-only enrollment keeps the actual zero-initialized backing;
                // RW enrollment also gives the padding/neighbor oracle variation.
                if (!readOnly)
                {
                    for (var i = 0; i < checked((int)PlacementAllocation); i++) initial[i] = (byte)(i * 13 + 37);
                    Assert.True(Views.TryWriteBacking(Address, initial.AsSpan(0, checked((int)PlacementAllocation))));
                }
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { try { primary.Data["PlacementSetupCleanup"] = cleanup; } catch { } }
                throw;
            }
        }
        internal ulong MapAlias(ulong offset)
        {
            var hole = new SelectedMappedSeedHole(Host); holes.Add(hole);
            Assert.True(Views.TryMapReservedRange(hole.Address, PlacementAllocation, offset, HostPageProtection.ReadWrite, out _));
            return hole.Address;
        }
        internal unsafe void AssertBytesUnchanged() => Assert.True(initial.AsSpan().SequenceEqual(new ReadOnlySpan<byte>((void*)Views.AliasBase, initial.Length)));
        internal void DisposePreserving(Exception? primary)
        {
            try { Dispose(); }
            catch (Exception cleanup)
            {
                if (primary is null) throw;
                try { primary.Data["PlacementCleanup"] = cleanup; } catch { }
            }
        }
        public void Dispose()
        {
            Exception? primary = null;
            try { Views.Dispose(); if (!Views.CleanupComplete) throw new IOException("Placement views cleanup incomplete."); }
            catch (Exception failure) { primary = failure; foreach (var hole in holes) hole.Quarantine(); }
            foreach (var hole in holes)
            {
                try { hole.Dispose(); }
                catch (Exception failure) { primary ??= failure; hole.Quarantine(); }
            }
            if (primary is not null)
            {
                storagePlacementQuarantine = [this, Views, Memory, holes];
                ExceptionDispatchInfo.Capture(primary).Throw();
            }
        }
    }
}

internal sealed class StoragePlacementProviderTheoryAttribute : TheoryAttribute
{
    public StoragePlacementProviderTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_STORAGE_PLACEMENT") != "1")
            Skip = "Actual Mac original-placement controls require separately granted fresh workers; no GPU-content or native-borrow authority.";
    }
}
