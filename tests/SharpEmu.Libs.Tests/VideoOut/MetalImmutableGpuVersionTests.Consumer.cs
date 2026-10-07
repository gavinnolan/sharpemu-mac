// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActualComputeArgumentBufferReadsCapturedOldAndNewGpuVersions(bool storage, bool disposePreparationBeforeExecution)
    {
        if (!DeviceRequested()) return;
        using var state = new NativeState();
        output.WriteLine($"Device={state.DeviceName}; process_arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        var firstBuffer = state.Buffer();
        var first = state.AdmitColor();
        state.EncodeClear(first, firstBuffer, new MtlClearColor { Red = 1, Alpha = 1 });
        state.Commit(firstBuffer); state.Wait(firstBuffer); state.Recycle();
        var oldRead = state.Capture(storage);
        var oldConsumer = state.AdmitConsumer(oldRead);
        if (disposePreparationBeforeExecution) MetalVideoPresenter.ReleasePreparedTextureSources([oldRead]);
        var secondBuffer = state.Buffer();
        var second = state.AdmitColor();
        state.EncodeClear(second, secondBuffer, new MtlClearColor { Green = 1, Alpha = 1 });
        state.Commit(secondBuffer); state.Wait(secondBuffer); state.Recycle();
        var newRead = state.Capture(storage);
        var newConsumer = state.AdmitConsumer(newRead);
        if (disposePreparationBeforeExecution) MetalVideoPresenter.ReleasePreparedTextureSources([newRead]);
        Assert.Equal(Rgba(255, 0, 0, 255), state.ExecuteConsumer(oldConsumer));
        Assert.Equal(Rgba(0, 255, 0, 255), state.ExecuteConsumer(newConsumer));
        if (!disposePreparationBeforeExecution) MetalVideoPresenter.ReleasePreparedTextureSources([oldRead, newRead]);
        state.Recycle();
        Assert.Equal((nint)0, Get(first, "ImmutableTexture"));
        Assert.Equal(128UL, state.NativeBytes);
        output.WriteLine($"Actual production ExecuteComputeDispatch→EncodeStageResources argument-buffer shader read all16old/red and new/green texels; storage={storage}, preparation_disposed={disposePreparationBeforeExecution}.");
    }

    private sealed partial class NativeState
    {
        public string? DeviceName => MetalNative.ReadNsString(MetalNative.Send(device, MetalNative.Selector("name")));
        private const ulong OutputAddress = 0x71F1_0000_00;
        private static MetalCompiledGuestShader ConsumerShader(bool storage)
        {
            var image = new ImageResource
            {
                ResourceClass = storage ? ImageResourceClass.Storage : ImageResourceClass.Sampled,
                NumericClass = ImageNumericClass.Float,
                Dimension = ImageDimension.Dim2D,
                Read = true,
            };
            var imageKind = ImageDescriptorBinding.ForImage(image)!.Value;
            var layout = Gen5MslArgumentLayout.Build(new BindingLayout
            {
                Descriptors = [new(DescriptorBindingKind.Buffers, [0]), new(imageKind, [0])],
            });
            var pointer = layout.Fields.Single(field => field.FieldKind == MslArgumentFieldKind.BufferPointers);
            var count = layout.Fields.Single(field => field.FieldKind == MslArgumentFieldKind.BufferByteCounts);
            var texture = layout.Fields.Single(field => field.FieldKind == MslArgumentFieldKind.Textures);
            // Independent native oracle for the actual production binding ABI.
            // This does not stand in for Gen5 ISA opcode translation proof.
            var source = $$"""
                #include <metal_stdlib>
                using namespace metal;
                struct Gen5StageResources {
                    device uint* buffers[1] [[id({{pointer.FirstId}})]];
                    uint buffer_bytes[1] [[id({{count.FirstId}})]];
                    {{(storage ? "texture2d<float, access::read>" : "texture2d<float>")}} {{texture.Name}}[1] [[id({{texture.FirstId}})]];
                };
                kernel void immutable_consume(constant Gen5StageResources& resources [[buffer(0)]], uint3 tid [[thread_position_in_grid]]) {
                    uint index = tid.y * 4u + tid.x;
                    if (tid.x < 4u && tid.y < 4u && index * 4u + 4u <= resources.buffer_bytes[0]) {
                        uint4 pixel = uint4(round(resources.{{texture.Name}}[0].read(tid.xy) * 255.0f));
                        resources.buffers[0][index] = pixel.x | (pixel.y << 8) | (pixel.z << 16) | (pixel.w << 24);
                    }
                }
                """;
            return new(new Gen5MslShader(source, "immutable_consume", Gen5MslStage.Compute, 0, ArgumentLayout: layout));
        }
        public object AdmitConsumer(GuestDrawTexture texture)
        {
            var shader = ConsumerShader(texture.IsStorage);
            var destination = new GuestMemoryBuffer(OutputAddress, Enumerable.Repeat((byte)0xA5, 64).ToArray(), 64, 64, false, Writable: true);
            var binding = new GuestStageBindings(GuestStageKind.Compute, [0], [0], [], [], null, [], [], false, false, 0);
            var sequence = MetalVideoPresenter.SubmitComputeDispatch(0, shader, [texture], [destination], 4, 4, 1, 0, 0, 0, true, binding);
            var pending = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<IEnumerable>()
                .SelectMany(static queue => queue.Cast<object>()).Single(item => (long)item.GetType().GetProperty("Sequence")!.GetValue(item)! == sequence);
            return pending.GetType().GetProperty("Work")!.GetValue(pending)!;
        }
        public byte[] ExecuteConsumer(object work)
        {
            var oldMemory = Field("_guestMemory").GetValue(null);
            var memory = new FakeCpuMemory(OutputAddress, 64);
            Assert.True(memory.TryWrite(OutputAddress, Enumerable.Repeat((byte)0xA5, 64).ToArray()));
            Field("_guestMemory").SetValue(null, memory);
            try
            {
                Call("ExecuteComputeDispatch", device, queue, work);
                // The production writeback waits for real completion and writes
                // through the actual GuestMemoryBuffer/ICpuMemory route.
                var result = new byte[64]; Assert.True(memory.TryRead(OutputAddress, result));
                Call("ReleaseTextureSourceWorkOwnerLocked", work);
                Call("RecycleCompletedUploadPages");
                Recycle();
                return result;
            }
            finally
            {
                Field("_guestMemory").SetValue(null, oldMemory);
                var last = (nint)Field("_lastFlushedCommandBuffer").GetValue(null)!;
                if (last != 0) MetalNative.SendVoid(last, MetalNative.Selector("release"));
                Field("_lastFlushedCommandBuffer").SetValue(null, (nint)0);
            }
        }
    }
}
