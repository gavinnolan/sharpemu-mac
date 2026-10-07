// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Managed replay of the descriptor and ImageStore encoding captured in Poppy's
// 34fc6d7 diagnostic. This qualifies specialization/snapshot consistency only;
// the fake compiler/host never materializes a native volume or runs the game.
[Collection(SchedulingStateCollection.Name)]
public sealed class PoppyVolumeDescriptorReplayTests
{
    private static uint[] CapturedDescriptor() =>
        [0x7AC109C0, 0xC0B00000, 0x0003C003, 0xA0500004, 0x0000000F, 0x00700000, 0, 0];

    [Fact]
    public void CapturedWord1EncodesR16UintRatherThanBc4Snorm()
    {
        var words = CapturedDescriptor();
        Assert.Equal(11u, GuestImageFormat.FormatOf(words));
        Assert.True(Gfx10UnifiedFormat.TryDecode(GuestImageFormat.FormatOf(words), out var data, out var numeric));
        Assert.Equal(2u, data); // one 16-bit component
        Assert.Equal(4u, numeric); // Uint
        Assert.Equal(ImageNumericClass.Uint, GuestImageFormat.SampledNumericClass(11));
        words[1] = 0xCB000000;
        Assert.Equal(176u, GuestImageFormat.FormatOf(words));
        Assert.Equal(ImageNumericClass.Float, GuestImageFormat.SampledNumericClass(176));
    }

    [Fact]
    public void UintThenBc4ThenUintKeepMatchingPermutationAndDetachedSnapshot()
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        const ulong code = PipelineTestGuest.MemoryBase + 0x1000;
        const ulong header = PipelineTestGuest.MemoryBase + 0x8000;
        // The actual pcB4 store encoding, reduced to the store and end program.
        guest.RegisterProgram(code, header, [0xF0200F10, 0x00000004, 0xBF810000]);
        ShaderProgram Obtain(uint[] words, out ShaderStageResources stage)
        {
            var cursor = 0u;
            return guest.Programs.GetOrCompile(guest.Source(code, ShaderStage.Compute, words),
                PipelineTestGuest.ComputeOptions(), ref cursor, out stage);
        }
        static void Check(ShaderStageResources stage, uint[] expected, ImageNumericClass numeric)
        {
            var image = Assert.Single(stage.Program!.Resources!.Info.Images);
            Assert.Equal(numeric, image.NumericClass);
            Assert.Equal(ImageDimension.Dim3D, image.Dimension);
            Assert.True(image.Written);
            Assert.False(image.Read);
            Assert.False(image.Atomic);
            Assert.Equal(expected, Assert.Single(stage.Resources.Images));
        }

        var input = CapturedDescriptor();
        var first = Obtain(input, out var uintStage);
        input[1] = 0xCB000000; // genuine BC4 SNORM descriptor, not the captured R16 Uint
        var second = Obtain(input, out var bc4Stage);
        var third = Obtain(CapturedDescriptor(), out var cacheHitStage);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, third.Id);
        Assert.Equal(2, guest.Compiler.Compilations);
        Assert.Equal(2, Assert.Single(guest.Programs.Entries).Permutations.Count);
        Check(uintStage, CapturedDescriptor(), ImageNumericClass.Uint);
        Check(cacheHitStage, CapturedDescriptor(), ImageNumericClass.Uint);
        Check(bc4Stage, input, ImageNumericClass.Float);
        Assert.NotSame(input, bc4Stage.Resources.Images[0]);
        input[1] = 0;
        Assert.Equal(176u, GuestImageFormat.FormatOf(bc4Stage.Resources.Images[0]));
        // Materializer acceptance here does not certify compressed storage support.
    }
}
