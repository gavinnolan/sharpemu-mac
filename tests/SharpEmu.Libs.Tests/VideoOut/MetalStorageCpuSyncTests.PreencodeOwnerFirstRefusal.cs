// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static object? PreencodeDiagnosticValue(object first, string name) =>
        first.GetType().GetProperty(name)!.GetValue(first);
    // Actual registry/saved-owner helper, scalar sentinels only. No receipt
    // completion, native selector, provider status classification or encoding.
    [Theory]
    [InlineData("image-original")] [InlineData("layout")] [InlineData("texture")] [InlineData("version")]
    [InlineData("width")] [InlineData("height")] [InlineData("depth")] [InlineData("layers")]
    [InlineData("type")] [InlineData("format")] [InlineData("initialized")] [InlineData("gpu-written")]
    [InlineData("registry-missing")] [InlineData("registry-image")] [InlineData("watch-state")] [InlineData("watch-unknown-producer")]
    public void PreencodeOwnerAttributionCapturesOnlyTheFirstReachedOriginalField(string change)
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var image = fixture.Image; var original = InvokeVolume("CaptureFiniteStoragePrecommitImage", Address, image)!;
        var fields = new[] { "StorageCpuOrigin", "StorageCpuLayout", "Texture", "ContentVersion", "Width", "Height", "Depth",
            "ArrayLength", "TextureType", "Format", "Initialized", "GpuWritten" };
        var values = fields.Select(name => PrecommitGet(image, name)).ToArray();
        var flag = Field("_volumeOwnerDeclineDiagnostics"); var oldFlag = flag.GetValue(null);
        var watch = VolumeWatch(fixture.Origin); var oldState = PrecommitGet(watch, "State"); var oldUnknown = PrecommitGet(watch, "UnknownProducer");
        var mappings = fixture.Memory.MappingQueries; var watches = fixture.Memory.WatchQueries;
        fixture.Memory.ReadStatus = () => throw new IOException("metadata attribution must never classify a provider status");
        try
        {
            flag.SetValue(null, true);
            switch (change)
            {
                case "image-original": Set(image, "StorageCpuOrigin", null); break;
                case "layout": Set(image, "StorageCpuLayout", null); break;
                case "texture": Set(image, "Texture", (nint)124); break;
                case "version": Set(image, "ContentVersion", (int)PrecommitGet(image, "ContentVersion")! + 1); break;
                case "width": Set(image, "Width", (uint)PrecommitGet(image, "Width")! + 1); break;
                case "height": Set(image, "Height", (uint)PrecommitGet(image, "Height")! + 1); break;
                case "depth": Set(image, "Depth", (uint)PrecommitGet(image, "Depth")! + 1); break;
                case "layers": Set(image, "ArrayLength", (uint)PrecommitGet(image, "ArrayLength")! + 1); break;
                case "type": Set(image, "TextureType", (uint)PrecommitGet(image, "TextureType")! + 1); break;
                case "format":
                    var format = PrecommitGet(image, "Format")!; Set(image, "Format", Enum.ToObject(format.GetType(), Convert.ToUInt64(format) + 1)); break;
                case "initialized": Set(image, "Initialized", !(bool)PrecommitGet(image, "Initialized")!); break;
                case "gpu-written": Set(image, "GpuWritten", !(bool)PrecommitGet(image, "GpuWritten")!); break;
                case "registry-missing": Images.Remove(Address); break;
                case "registry-image": Images[Address] = Activator.CreateInstance(image.GetType(), nonPublic: true); break;
                case "watch-state": PrecommitSet(watch, "State", 2); break;
                case "watch-unknown-producer": PrecommitSet(watch, "UnknownProducer", true); break;
            }
            Exception failure;
            lock (Field("_gate").GetValue(null)!)
                failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStoragePreencodeSavedImageLocked", original)).InnerException!;
            Assert.Equal("Finite storage pre-encode ORIGINAL native owner/version changed.", failure.Message);
            var first = failure.GetType().GetField("First", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failure)!;
            Assert.Equal(Address, PreencodeDiagnosticValue(first, "Address"));
            Assert.Equal(change.StartsWith("registry") ? "registry" : change.StartsWith("watch") ? "volume-metadata" : "tuple", PreencodeDiagnosticValue(first, "Branch"));
            Assert.Equal(change == "registry-missing" ? "missing" : change == "registry-image" ? "image-identity" : change, PreencodeDiagnosticValue(first, "Guard"));
            if (change == "texture") { Assert.Equal(123ul, PreencodeDiagnosticValue(first, "SavedScalar")); Assert.Equal(124ul, PreencodeDiagnosticValue(first, "ActualScalar")); }
            if (change is "image-original" or "layout" or "registry-missing" or "registry-image" or "watch-state" or "watch-unknown-producer")
            { Assert.Null(PreencodeDiagnosticValue(first, "SavedScalar")); Assert.Null(PreencodeDiagnosticValue(first, "ActualScalar")); }
            Assert.Equal(mappings, fixture.Memory.MappingQueries); Assert.Equal(watches, fixture.Memory.WatchQueries);
            Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads);
            Assert.Null(Field("_guestWorkFailure").GetValue(null)); Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
        }
        finally
        {
            Images[Address] = image;
            for (var index = 0; index < fields.Length; index++) Set(image, fields[index], values[index]);
            PrecommitSet(watch, "State", oldState); PrecommitSet(watch, "UnknownProducer", oldUnknown); flag.SetValue(null, oldFlag);
        }
    }

    [Fact]
    public void PreencodeOwnerFirstRefusalPreservesRegistryThenTupleThenVolumeMetadataOrder()
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var image = fixture.Image; var saved = InvokeVolume("CaptureFiniteStoragePrecommitImage", Address, image)!;
        var watch = VolumeWatch(fixture.Origin); var oldState = PrecommitGet(watch, "State"); var texture = PrecommitGet(image, "Texture");
        try
        {
            Set(image, "Texture", (nint)124); PrecommitSet(watch, "State", 2); Images.Remove(Address);
            object First() { lock (Field("_gate").GetValue(null)!) return InvokeVolume("InspectFiniteStoragePreencodeSavedImageLocked", saved)!; }
            Assert.Equal("missing", PreencodeDiagnosticValue(First(), "Guard"));
            Images[Address] = image; Assert.Equal("texture", PreencodeDiagnosticValue(First(), "Guard"));
            Set(image, "Texture", texture); Assert.Equal("watch-state", PreencodeDiagnosticValue(First(), "Guard"));
            Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads);
        }
        finally { Images[Address] = image; Set(image, "Texture", texture); PrecommitSet(watch, "State", oldState); }
    }

    [Fact]
    public void CurrentPreencodeOwnerMetadataDoesNotQueryEvenARefusingOriginalReadWatch()
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var saved = InvokeVolume("CaptureFiniteStoragePrecommitImage", Address, fixture.Image)!;
        fixture.Memory.WatchCurrent = false; fixture.Memory.ReadStatus = () => throw new IOException("not metadata");
        var maps = fixture.Memory.MappingQueries; var watches = fixture.Memory.WatchQueries;
        lock (Field("_gate").GetValue(null)!) Assert.Null(InvokeVolume("InspectFiniteStoragePreencodeSavedImageLocked", saved));
        Assert.Equal(maps, fixture.Memory.MappingQueries); Assert.Equal(watches, fixture.Memory.WatchQueries);
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads);
    }

    [Fact]
    public void CachedOffPreencodeOwnerRetainsOriginalExceptionAndNoDiagnosticMetadata()
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var saved = InvokeVolume("CaptureFiniteStoragePrecommitImage", Address, fixture.Image)!;
        var flag = Field("_volumeOwnerDeclineDiagnostics"); var previous = flag.GetValue(null);
        try
        {
            flag.SetValue(null, false); Images.Remove(Address);
            lock (Field("_gate").GetValue(null)!)
            {
                var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStoragePreencodeSavedImageLocked", saved)).InnerException!;
                Assert.IsType<InvalidOperationException>(failure); Assert.Null(failure.GetType().GetField("First"));
                Assert.Equal("Finite storage pre-encode ORIGINAL native owner/version changed.", failure.Message);
            }
        }
        finally { Images[Address] = fixture.Image; flag.SetValue(null, previous); }
    }
}
