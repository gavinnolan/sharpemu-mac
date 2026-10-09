// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData("layers")]
    [InlineData("storage")]
    [InlineData("raw")]
    public void NativeR32OriginalViewIdentityRejectsChangedStorageGeometry(string change)
    {
        var original = PlacementTexture(5, 0x7092C80000);
        var copy = change switch
        {
            "layers" => original with { ArrayLayers = 2 },
            "storage" => original with { IsStorage = false },
            "raw" => original with { Descriptor = original.Descriptor!.Select((word, i) => i == 7 ? 1u : word).ToArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False((bool)Presenter.GetMethod("SameOrdinaryStorageView", Methods)!.Invoke(null, [original, copy])!);
        // Pure exact-view refusal only: no provider/native/owner acceptance.
    }

    [Theory]
    [InlineData("write-access")]
    [InlineData("array")]
    [InlineData("dynamic-mip")]
    [InlineData("numeric")]
    [InlineData("storage-shape")]
    [InlineData("descriptor")]
    public void NativeR32ReadonlyFullBindingGuardRejectsContradictoryMetadata(string change)
    {
        var binding = R32DisjointManagedBinding();
        binding = change switch
        {
            "write-access" => binding with { Access = GuestImageAccess.Read | GuestImageAccess.Write },
            "array" => binding with { IsArrayed = true },
            "dynamic-mip" => binding with { DynamicMip = true },
            "numeric" => binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Uint } },
            "storage-shape" => binding with { Shape = binding.Shape with { Storage = true } },
            "descriptor" => binding with { Descriptor = binding.Descriptor with { Width = 511 } },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var type = Presenter.Assembly.GetTypes().Single(t => t.Name == "MetalTextureSnapshots");
        var failure = Assert.Throws<TargetInvocationException>(() => type.GetMethod("RequireR32SampledMipZero", Methods)!.Invoke(null, [binding]));
        Assert.IsType<NotSupportedException>(failure.InnerException);
    }
}
