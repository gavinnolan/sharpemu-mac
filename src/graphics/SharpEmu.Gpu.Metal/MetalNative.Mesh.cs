// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalNative
{
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    internal static partial nint SendNewMeshPipeline(nint device, nint selector, nint descriptor,
        nuint options, nint reflection, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    internal static partial void SendDrawMesh(nint encoder, nint selector, MtlSize groups,
        MtlSize objectThreads, MtlSize meshThreads);
}

// Native transport for the mesh stage; guest stage admission/translation remains
// separate. Functions belong to the caller, the returned pipeline has one retain.
internal static class MetalMeshPipeline
{
    internal static nint Create(nint device, nint meshFunction, nint fragmentFunction,
        uint colorFormat, nuint maximumMeshThreads)
    {
        if (device == 0 || meshFunction == 0 || fragmentFunction == 0 ||
            colorFormat == 0 || maximumMeshThreads == 0 || maximumMeshThreads > 1024)
            throw new ArgumentException("Mesh pipeline requires real functions, format and bounded threads.");
        var descriptor = MetalNative.Send(MetalNative.Send(MetalNative.Class("MTLMeshRenderPipelineDescriptor"),
            MetalNative.Selector("alloc")), MetalNative.Selector("init"));
        if (descriptor == 0) throw new NotSupportedException("Metal mesh pipeline descriptors are unavailable.");
        nint pipeline = 0;
        Exception? failure = null;
        try
        {
            MetalNative.SendVoid(descriptor, MetalNative.Selector("setMeshFunction:"), meshFunction);
            MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragmentFunction);
            MetalNative.Send(descriptor, MetalNative.Selector("setMaxTotalThreadsPerMeshThreadgroup:"), checked((nint)maximumMeshThreads));
            var colors = MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments"));
            var color = MetalNative.SendAtIndex(colors, MetalNative.Selector("objectAtIndexedSubscript:"), 0);
            MetalNative.Send(color, MetalNative.Selector("setPixelFormat:"), (nint)colorFormat);
            nint error = 0;
            pipeline = MetalNative.SendNewMeshPipeline(device,
                MetalNative.Selector("newRenderPipelineStateWithMeshDescriptor:options:reflection:error:"),
                descriptor, 0, 0, ref error);
            if (pipeline == 0) throw new InvalidOperationException("Metal mesh pipeline creation failed: " + MetalNative.DescribeError(error));
        }
        catch (Exception exception) { failure = exception; }
        ReleaseOwned(ref descriptor, ref failure);
        if (failure is not null)
        {
            ReleaseOwned(ref pipeline, ref failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return pipeline;
    }

    private static void ReleaseOwned(ref nint handle, ref Exception? failure)
    {
        var owned = handle; handle = 0; // consume exact retain before the callback
        if (owned == 0) return;
        try { MetalNative.SendVoid(owned, MetalNative.Selector("release")); }
        catch (Exception cleanup)
        {
            if (failure is null) failure = cleanup;
            else
            {
                try { failure.Data["mesh.cleanup"] = failure.Data["mesh.cleanup"] is Exception previous ? new AggregateException(previous, cleanup) : cleanup; }
                catch (Exception) { } // secondary diagnostic bookkeeping cannot hide the primary
            }
        }
    }

    internal static void Encode(nint encoder, nint pipeline, nuint groupCount, nuint meshThreadCount)
    {
        if (encoder == 0 || pipeline == 0 || groupCount == 0 || meshThreadCount == 0 || meshThreadCount > 1024)
            throw new ArgumentException("Mesh draw requires a pipeline, encoder and bounded launch.");
        var nativeMaximum = checked((nuint)MetalNative.Send(pipeline, MetalNative.Selector("maxTotalThreadsPerMeshThreadgroup")));
        if (nativeMaximum == 0 || meshThreadCount > nativeMaximum)
            throw new ArgumentException("Mesh launch exceeds this pipeline's native thread bound.");
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);
        MetalNative.SendDrawMesh(encoder,
            MetalNative.Selector("drawMeshThreadgroups:threadsPerObjectThreadgroup:threadsPerMeshThreadgroup:"),
            new MtlSize { Width = groupCount, Height = 1, Depth = 1 },
            new MtlSize { Width = 1, Height = 1, Depth = 1 },
            new MtlSize { Width = meshThreadCount, Height = 1, Depth = 1 });
    }
}
