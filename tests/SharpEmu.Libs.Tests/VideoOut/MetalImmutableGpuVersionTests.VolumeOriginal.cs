// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [FiniteStoragePublicationNativeFact]
    public void ActualSingletonAndLinearVolumeOriginalWatchesSurviveDisjointCanonicalPublications() =>
        RunActualRuntimeTwoTypedPublications(false, rgba:true, originalVolumes:true);

    [FiniteStoragePublicationNativeFact]
    public void ActualSingletonAndLinearVolumeOriginalWatchesDoNotBypassOverlappingPublicationVeto() =>
        RunActualRuntimeTwoTypedPublications(true, rgba:true, originalVolumes:true);

    [FiniteStoragePublicationNativeFact]
    public void ActualVolumeQueueRetentionSurvivesHeldConstructorReservationThenNativeTransfer() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, queueRetentionWindow: "release");

    [FiniteStoragePublicationNativeFact]
    public void ActualVolumeQueueRetentionDoesNotAuthorizeLaterOverlappingWriter() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, queueRetentionWindow: "overlap");

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint="objc_msgSend")]
    private static extern void GetOriginalVolumeBytes(nint texture,nint selector,nint bytes,nuint rowBytes,
        nuint imageBytes,MtlRegion region,nuint level,nuint slice);

    private static unsafe byte[] ReadOriginalVolume(nint texture,uint width,uint height,uint depth)
    {
        var bytes=new byte[checked((int)(width*height*depth*4))];
        fixed(byte* pointer=bytes)
            GetOriginalVolumeBytes(texture,MetalNative.Selector("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:"),
                (nint)pointer,width*4,width*height*4,new MtlRegion{Width=width,Height=height,Depth=depth},0,0);
        return bytes;
    }

    private sealed partial class NativeState
    {
        public MetalCompiledGuestShader VolumeOriginalShader(uint width,uint height,uint depth)
        {
            var resource=new ImageResource {ResourceClass=ImageResourceClass.Storage,NumericClass=ImageNumericClass.Uint,
                Dimension=ImageDimension.Dim3D,Written=true};
            var kind=ImageDescriptorBinding.ForImage(resource)!.Value;
            var layout=Gen5MslArgumentLayout.Build(new BindingLayout {Descriptors=[new(kind,[0])]});
            var texture=Assert.Single(layout.Fields.Where(field=>field.FieldKind==MslArgumentFieldKind.Textures));
            var source=$$"""
                #include <metal_stdlib>
                using namespace metal;
                struct Gen5StageResources {
                    array<texture3d<uint, access::read_write>,1> {{texture.Name}} [[id({{texture.FirstId}})]];
                };
                kernel void original_volume_write(constant Gen5StageResources& resources [[buffer(0)]]) {
                    resources.{{texture.Name}}[0].write(uint4(17u,34u,51u,68u),uint3({{width-1}}u,{{height-1}}u,{{depth-1}}u));
                }
                """;
            var shader=new MetalCompiledGuestShader(new Gen5MslShader(source,"original_volume_write",Gen5MslStage.Compute,0,ArgumentLayout:layout));
            consumerShaders.Add(shader); return shader;
        }
    }
}
