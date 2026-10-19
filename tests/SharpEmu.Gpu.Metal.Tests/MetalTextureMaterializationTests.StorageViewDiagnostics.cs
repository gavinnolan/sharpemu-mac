// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData("Width", 5u, "observed_width=5")]
    [InlineData("Height", 7u, "observed_height=7")]
    [InlineData("Format", 53u, "observed_format=53")]
    [InlineData("TextureType", 7u, "observed_type=7")]
    [InlineData("Depth", 9u, "observed_depth=9")]
    [InlineData("ArrayLength", 3u, "observed_layers=3")]
    public void StorageViewMismatchDiagnosticKeepsExactLiveAndRequestedScalarsBeforeNativeAccess(string field, uint wrong, string expected)
    {
        const ulong address = 0x71F5400100;
        var descriptor = new GuestDrawTexture(address, 2, 3, 4, 7, new byte[24], IsFallback: false, IsStorage: true,
            Pitch: 2, Type: 9, Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
        var type = Presenter.GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(type, nonPublic: true)!;
        var formatType = type.GetField("Format")!.FieldType;
        foreach (var (name, value) in new (string, object)[] { ("Width", 2u), ("Height", 3u), ("Format", Enum.ToObject(formatType, 55u)),
            ("TextureType", 2u), ("Depth", 1u), ("ArrayLength", 1u), ("Initialized", true) })
            type.GetField(name)!.SetValue(image, value);
        type.GetField(field)!.SetValue(image, field == "Format" ? Enum.ToObject(formatType, wrong) : wrong);
        var registry = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
        var gate = Presenter.GetField("_gate", Methods)!.GetValue(null)!;
        lock (gate) { Assert.False(registry.Contains(address)); registry.Add(address, image); }
        try
        {
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [nint.Zero, descriptor]));
            var cause = Assert.IsType<NotSupportedException>(failure.InnerException);
            Assert.Contains(expected, cause.Message);
            Assert.Contains("requested_width=2 requested_height=3 requested_format=55", cause.Message);
            Assert.Contains("requested_type=2 requested_depth=1 requested_layers=1", cause.Message);
            Assert.Contains("guest_format=4/7 guest_type=9 base_mip=0 selected_mip=0 numeric_class=Float", cause.Message);
            lock (gate) Assert.Same(image, registry[address]);
        }
        finally { lock (gate) registry.Remove(address); }
    }
}
