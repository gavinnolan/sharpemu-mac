// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static AgcExports.TranslatedImageBinding LinearVolumeBinding(uint width=16,uint height=16,uint depth=16)
    {
        uint[] raw = [(uint)(Address >> 8), 0x03C00000 | (((width-1)&3)<<30), ((width-1)>>2)|((height-1)<<14), 0xA0000FAC, depth-1, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        // T# volume pitch is logical width; existing CPU snapshot/native
        // upload pads each byte row to256, yielding64 RGBA8 texels.
        Assert.Equal((width, height, depth, width, 0u), (descriptor.Width, descriptor.Height, descriptor.Depth, descriptor.Pitch, descriptor.TileMode));
        return new(descriptor, true, 0, [], IsArrayed: false, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
    }
    private static GuestDrawTexture LinearVolumeSnapshot(VolumeMemory memory) => Assert.Single(
        MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [LinearVolumeBinding()], out _));

    private static void PublishLinearVolume(GuestDrawTexture snapshot, object image)
    {
        Images.Remove(Address);
        var nativeBytes=checked((ulong)snapshot.Width*snapshot.Height*snapshot.Depth*4);
        lock(Field("_gate").GetValue(null)!) InvokeVolume("PublishStorageImageLocked",snapshot,null,image,
            (snapshot.Width,snapshot.Height,nativeBytes));
    }

    [Fact]
    public void LinearVolumeActualProducerPreservesEveryPitchedSliceAndFinalPaddingByte()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = LinearVolumeSnapshot(memory);
        try
        {
            var origin = GetOrigin(snapshot); var layout = Assert.IsType<MetalStorageCpuLayout>(origin.GetType().GetProperty("Layout")!.GetValue(origin));
            Assert.Equal((16u,16u,16u,64u), (snapshot.Width,snapshot.Height,snapshot.Depth,snapshot.Pitch));
            Assert.Equal(65536UL, snapshot.SourceByteCount); Assert.Equal(65536, layout.LinearBytes);
            Assert.Equal(memory.Bytes, snapshot.RgbaPixels); Assert.NotSame(memory.Bytes, snapshot.RgbaPixels);
            for (var z=0; z<16; z++) for (var y=0; y<16; y++)
                Assert.Equal(memory.Bytes.AsSpan((z*16+y)*256,256).ToArray(), snapshot.RgbaPixels.AsSpan((z*16+y)*256,256).ToArray());
            Assert.Equal(memory.Bytes[^1],snapshot.RgbaPixels[^1]);
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            Assert.Equal((7u,1u,16u), MetalVideoPresenter.GuestTextureShape(snapshot));
            Assert.Equal(1,memory.Copies); Assert.Equal(0,memory.Reads);
            Assert.Throws<NotSupportedException>(()=>layout.Encode(new byte[65536])); // no volume publisher expansion
            memory.Bytes[65535] ^= 255; Assert.NotEqual(memory.Bytes[65535],snapshot.RgbaPixels[65535]);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1,memory.Releases);
    }

    [Theory] [InlineData(8u,8u,32u)] [InlineData(32u,8u,32u)]
    public void LinearVolumeOriginalCaptureUsesExistingShapePolicyRatherThanOneCapturedCube(uint width,uint height,uint depth)
    {
        ActualArchitecture(); using var state=new VolumeState(); var memory=new VolumeMemory(VolumePhysical());
        var binding=LinearVolumeBinding(width,height,depth);
        var snapshot=Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory,Generation.Gen5),[binding],out _));
        try
        {
            Assert.Equal((width,height,depth,64u),(snapshot.Width,snapshot.Height,snapshot.Depth,snapshot.Pitch));
            Assert.Equal(65536UL,snapshot.SourceByteCount); Assert.Equal(memory.Bytes,snapshot.RgbaPixels);
            Assert.Equal(1,memory.Copies); Assert.Equal(0,memory.Reads);
        }
        finally {MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);}
        Assert.Equal(1,memory.Releases);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void LinearVolumePublicComputeAndDrawQueuesOwnOriginalAfterFrontendRelease(bool draw)
    {
        ActualArchitecture(); using var state=new VolumeState(); var memory=new VolumeMemory(VolumePhysical());
        var snapshot=LinearVolumeSnapshot(memory); var sequence=SubmitVolume([snapshot],draw);
        Assert.True(sequence>0); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0,memory.Releases);
        var pending=TakeVolumeWork(); InvokeVolume("CompleteGuestWork",pending);
        Assert.Equal(sequence,Field("_completedGuestWorkSequence").GetValue(null)); Assert.Equal(1,memory.Releases);
        Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
    }

    [Fact]
    public void LinearVolumeNativeMetadataTransferRetainsExactDepthPitchFormatAndOriginal()
    {
        ActualArchitecture(); using var state=new VolumeState(); using var protection=new VolumeProtectedScalar();
        var memory=new VolumeMemory(VolumePhysical()); var snapshot=LinearVolumeSnapshot(memory); var origin=GetOrigin(snapshot);
        var image=VolumeImage(origin,memory); Set(image,"Width",16u); Set(image,"Height",16u); Set(image,"Depth",16u);
        try
        {
            PublishLinearVolume(snapshot,image); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0,memory.Releases);
            lock(Field("_gate").GetValue(null)!) Assert.True((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked",image,origin)!);
            Field("_executingGuestWorkSequence").SetValue(null,26L); MetalVideoPresenter.RecordStorageImageWrites([snapshot]);
            lock(Field("_gate").GetValue(null)!) Assert.True((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked",image,origin)!);
            Assert.Same(origin,image.GetType().GetField("StorageCpuOrigin")!.GetValue(image));
            lock(Field("_gate").GetValue(null)!) InvokeVolume("NoteFiniteStorageOwnerRetirementLocked",image);
            Assert.Equal(1,memory.Releases);
        }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Theory] [InlineData("depth")] [InlineData("width")] [InlineData("format")]
    public void LinearVolumeWrongNativeOwnerShapeCannotAcquireOriginalWatch(string change)
    {
        ActualArchitecture(); using var state=new VolumeState(); var memory=new VolumeMemory(VolumePhysical());
        var snapshot=LinearVolumeSnapshot(memory); var image=VolumeImage(GetOrigin(snapshot),memory);
        Set(image,"Width",16u); Set(image,"Height",16u); Set(image,"Depth",16u);
        if(change=="depth") Set(image,"Depth",1u);
        if(change=="width") Set(image,"Width",1u);
        if(change=="format") Set(image,"Format",MtlPixelFormat.Rgba8Unorm);
        try { Assert.Throws<TargetInvocationException>(()=>PublishLinearVolume(snapshot,image)); Assert.False(Images.Contains(Address)); Assert.Equal(1,memory.Releases); // rejected publication consumes its unaccepted original watch }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1,memory.Releases);
    }

    [Theory] [InlineData("rejected")] [InlineData("mapping")] [InlineData("watch")]
    public void LinearVolumeRejectedOriginalCannotFallBackToUnwatchedReads(string reason)
    {
        ActualArchitecture(); using var state=new VolumeState(); var memory=new VolumeMemory(VolumePhysical());
        if(reason=="rejected") memory.Result=CpuMappedCopyResult.Rejected;
        if(reason=="mapping") memory.Current=false;
        if(reason=="watch") memory.WatchCurrent=false;
        Assert.Throws<InvalidOperationException>(()=>LinearVolumeSnapshot(memory)); Assert.Equal(0,memory.Reads); Assert.Equal(3,memory.Copies);
        Assert.Equal(reason=="rejected"?0:3,memory.Releases);
    }

    [Theory] [InlineData("raw-mip")] [InlineData("metadata")] [InlineData("shape")] [InlineData("numeric")]
    public void LinearVolumeExistingFormatAndSingleMipPolicyRejectsBeforeOriginalCopy(string change)
    {
        ActualArchitecture(); using var state=new VolumeState(); var memory=new VolumeMemory(VolumePhysical()); var binding=LinearVolumeBinding();
        var raw=binding.ResourceDescriptor!.ToArray();
        if(change=="raw-mip") raw[3]|=1u<<16;
        if(change=="metadata") raw[6]=1;
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw,out var descriptor)); binding=binding with {Descriptor=descriptor,ResourceDescriptor=raw};
        if(change=="shape") binding=binding with {Shape=binding.Shape with {Volume=false}};
        if(change=="numeric") binding=binding with {Shape=binding.Shape with {NumericClass=TextureNumericClass.Float}};
        Assert.Throws<NotSupportedException>(()=>MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory,Generation.Gen5),[binding],out _));
        Assert.Equal(0,memory.Copies); Assert.Equal(0,memory.Reads);
    }
}
