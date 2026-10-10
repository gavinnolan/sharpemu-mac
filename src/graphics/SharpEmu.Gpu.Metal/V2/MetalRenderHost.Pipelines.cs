// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Gpu.Metal.V2;

// Render and compute pipeline states, the MSL libraries behind them, and the
// depth/stencil state objects the dynamic masks need.
internal sealed partial class MetalRenderHost
{
    // One pipeline the executor can bind, with the encoder state Metal keeps outside
    // the pipeline object (cull, winding, fill, depth clip, primitive type).
    private sealed class MetalPipelineEntry
    {
        public ulong Id;
        public nint State;
        public bool Graphics;
        public nuint PrimitiveType;
        public nint CullMode;
        public nint Winding;
        public nint DepthClipMode;
        public nint FillMode;
        public MtlSize ThreadsPerThreadgroup;
        public bool CullsEverything;
        public PipelineStaticParameters? StaticParameters;
        public bool HasDepth;
        public bool HasStencil;

        // Set when the vertex program is an NGG primitive shader compiled as a Metal
        // mesh function; the draw then launches threadgroups instead of vertices.
        public Gen5MslMeshInfo? Mesh;

        // The shader hashes behind this pipeline, formatted once for the per-draw GPU
        // profiler; null unless SHARPEMU_METAL_V2_PROFILE_SHADERS=1.
        public string? Profile;
    }

    // One compiled MSL module: its library, its function, and the shader it came from.
    private sealed class MetalShaderModule
    {
        public nint Library;
        public nint Function;
        public Gen5MslShader Shader = null!;
        public ulong Hash;
    }

    private static readonly nint SelSetAttachmentPixelFormat = MetalNative.Selector("setPixelFormat:");

    private readonly Dictionary<ulong, MetalPipelineEntry> _pipelineEntries = new();
    private readonly List<MetalShaderModule> _modules = new();
    private readonly Dictionary<ulong, nint> _depthStencilStates = new();
    private ulong _nextPipelineId;

    public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
    {
        _ = stage;
        _ = programId;
        if (shader is not MetalCompiledGuestShader metal)
        {
            throw Fatal($"The shader handle was not compiled by the Metal backend: type={shader.GetType().Name}.");
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var library = CompileLibrary(metal.Shader.Source, hash);
        var function = MetalNative.Send(
            library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString(metal.Shader.EntryPoint));
        if (function == 0)
        {
            MetalNative.SendVoid(library, SelRelease);
            throw Fatal(
                $"The MSL library has no entry point: entry={metal.Shader.EntryPoint} hash=0x{hash:X16}.");
        }

        int index;
        // Module creation can reach the host from the shader cache's own threads,
        // while RequireModule runs on the GPU worker; the list is shared state.
        lock (_modules)
        {
            _modules.Add(new MetalShaderModule
            {
                Library = library,
                Function = function,
                Shader = metal.Shader,
                Hash = hash,
            });
            index = _modules.Count;
        }

        Interlocked.Increment(ref _shaderCompilations);
        MetalV2Diagnostics.Trace(
            $"compiled module hash=0x{hash:X16} entry={metal.Shader.EntryPoint} " +
            $"ms={(System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}");
        // Module zero never identifies a module, so the handle is the one-based index.
        return (ulong)index;
    }

    private nint CompileLibrary(string source, ulong hash)
    {
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            var options = MetalNative.Send(
                MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
                MetalNative.Selector("init"));
            if (options == 0)
            {
                throw Fatal("Metal compile options could not be created.");
            }

            try
            {
                // Fast math off (the translated GCN float semantics do not survive
                // it) and an explicit MSL version, so texture atomics are available.
                MetalNative.ApplyMslCompileOptions(options);
                nint error = 0;
                var library = MetalNative.Send(
                    _device,
                    MetalNative.Selector("newLibraryWithSource:options:error:"),
                    MetalNative.NsString(source),
                    options,
                    ref error);
                if (library == 0)
                {
                    throw Fatal(
                        $"An MSL library failed to compile: hash=0x{hash:X16} {MetalNative.DescribeError(error)}");
                }

                return library;
            }
            finally
            {
                MetalNative.SendVoid(options, SelRelease);
            }
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    private MetalShaderModule RequireModule(in ShaderProgram program)
    {
        lock (_modules)
        {
            var index = (int)program.Module - 1;
            if (index < 0 || index >= _modules.Count)
            {
                throw Fatal($"The shader module handle is unknown: module={program.Module} program={program.Id}.");
            }

            return _modules[index];
        }
    }

    private MetalPipelineEntry RequirePipelineEntry(in PipelineHandle pipeline) =>
        _pipelineEntries.TryGetValue(pipeline.Pipeline, out var entry)
            ? entry
            : throw Fatal($"The pipeline handle is unknown: pipeline={pipeline.Pipeline}.");

    public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description)
    {
        var parameters = description.StaticParameters;
        var vertexModule = RequireModule(description.VertexProgram);
        var pixelModule = description.PixelStage is not null && description.PixelProgram.IsValid
            ? RequireModule(description.PixelProgram)
            : null;
        var mesh = vertexModule.Shader.Stage == Gen5MslStage.Mesh ? vertexModule.Shader.MeshInfo : null;
        var descriptorClass = mesh is null ? "MTLRenderPipelineDescriptor" : "MTLMeshRenderPipelineDescriptor";
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class(descriptorClass), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (descriptor == 0)
        {
            throw Fatal($"A Metal render pipeline descriptor could not be created: class={descriptorClass}.");
        }

        nint state;
        try
        {
            if (mesh is null)
            {
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertexModule.Function);
                MetalV2Native.SendVoidValue(
                    descriptor, MetalNative.Selector("setRasterSampleCount:"), Math.Max(parameters.Samples, 1));
            }
            else
            {
                // A mesh function has no stage_in and no vertex descriptor: the
                // primitive shader fetches its own attributes through the resource path.
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setMeshFunction:"), vertexModule.Function);
                MetalV2Native.SendVoidValue(
                    descriptor,
                    MetalNative.Selector("setMaxTotalThreadsPerMeshThreadgroup:"),
                    Math.Max(mesh.ThreadsPerThreadgroup, 1));
                MetalV2Native.SendVoidValue(
                    descriptor, MetalNative.Selector("setRasterSampleCount:"), Math.Max(parameters.Samples, 1));
            }

            if (pixelModule is not null)
            {
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), pixelModule.Function);
            }
            var colorAttachments = MetalNative.Send(descriptor, SelColorAttachments);
            for (var index = 0; index < description.Rendering.ColorCount; index++)
            {
                var attachment = MetalNative.SendAtIndex(colorAttachments, SelObjectAtIndex, (nuint)index);
                var format = MetalV2Formats.PixelFormat(description.Rendering.ColorFormats[index]);
                MetalV2Native.SendVoidValue(attachment, SelSetAttachmentPixelFormat, (nuint)format);
                MetalV2Native.SendVoidValue(
                    attachment,
                    MetalNative.Selector("setWriteMask:"),
                    (nuint)MetalV2Formats.ColorWriteMask(parameters.GetColorMask(index)));
                if (!parameters.GetBlendEnable(index) || parameters.GetBlendBypass(index) || IsIntegerFormat(format))
                {
                    continue;
                }

                MetalNative.SendVoidBool(attachment, MetalNative.Selector("setBlendingEnabled:"), true);
                MetalV2Native.SendVoidValue(
                    attachment,
                    MetalNative.Selector("setSourceRGBBlendFactor:"),
                    (nuint)MetalV2Formats.BlendFactor(parameters.GetColorSourceBlend(index)));
                MetalV2Native.SendVoidValue(
                    attachment,
                    MetalNative.Selector("setDestinationRGBBlendFactor:"),
                    (nuint)MetalV2Formats.BlendFactor(parameters.GetColorDestinationBlend(index)));
                MetalV2Native.SendVoidValue(
                    attachment,
                    MetalNative.Selector("setRgbBlendOperation:"),
                    (nuint)MetalV2Formats.BlendOperation(parameters.GetColorBlendFunction(index)));
                var separate = parameters.GetSeparateAlphaBlend(index);
                MetalV2Native.SendVoidValue(
                    attachment,
                    MetalNative.Selector("setSourceAlphaBlendFactor:"),
                    (nuint)MetalV2Formats.BlendFactor(
                        separate ? parameters.GetAlphaSourceBlend(index) : parameters.GetColorSourceBlend(index)));
                MetalV2Native.SendVoidValue(
                    attachment,
                    MetalNative.Selector("setDestinationAlphaBlendFactor:"),
                    (nuint)MetalV2Formats.BlendFactor(
                        separate ? parameters.GetAlphaDestinationBlend(index) : parameters.GetColorDestinationBlend(index)));
                MetalV2Native.SendVoidValue(
                    attachment,
                    MetalNative.Selector("setAlphaBlendOperation:"),
                    (nuint)MetalV2Formats.BlendOperation(
                        separate ? parameters.GetAlphaBlendFunction(index) : parameters.GetColorBlendFunction(index)));
            }

            var depthFormat = description.Rendering.DepthFormat != GpuFormat.Undefined
                ? MetalV2Formats.PixelFormat(description.Rendering.DepthFormat)
                : MtlFormat.Invalid;
            var stencilFormat = description.Rendering.StencilFormat != GpuFormat.Undefined
                ? MetalV2Formats.PixelFormat(description.Rendering.StencilFormat)
                : MtlFormat.Invalid;
            if (depthFormat != MtlFormat.Invalid)
            {
                MetalV2Native.SendVoidValue(
                    descriptor, MetalNative.Selector("setDepthAttachmentPixelFormat:"), (nuint)depthFormat);
            }

            if (stencilFormat != MtlFormat.Invalid && MetalV2Formats.HasStencil(stencilFormat))
            {
                MetalV2Native.SendVoidValue(
                    descriptor, MetalNative.Selector("setStencilAttachmentPixelFormat:"), (nuint)stencilFormat);
            }

            if (mesh is null && description.VertexInput.AttributeCount != 0)
            {
                var vertexDescriptor = CreateVertexDescriptor(description);
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexDescriptor:"), vertexDescriptor);
                MetalNative.SendVoid(vertexDescriptor, SelRelease);
            }

            nint error = 0;
            state = mesh is null
                ? MetalNative.Send(
                    _device, MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref error)
                : MetalNative.SendNewMeshPipeline(
                    _device,
                    MetalNative.Selector("newRenderPipelineStateWithMeshDescriptor:options:reflection:error:"),
                    descriptor,
                    0,
                    0,
                    ref error);
            if (state == 0)
            {
                throw Fatal(
                    $"A Metal render pipeline state could not be created: vertex=0x{description.VertexStage.Hash:X16} " +
                    $"pixel=0x{description.PixelStage?.Hash ?? 0:X16} colors={description.Rendering.ColorCount} " +
                    $"{MetalNative.DescribeError(error)}");
            }
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }

        Interlocked.Increment(ref _pipelineCreations);
        var entry = new MetalPipelineEntry
        {
            Id = ++_nextPipelineId,
            State = state,
            Graphics = true,
            PrimitiveType = MetalV2Formats.Topology(parameters.Topology),
            CullMode = parameters.CullFront && parameters.CullBack
                ? Mtl.CullModeNone
                : parameters.CullFront ? Mtl.CullModeFront : parameters.CullBack ? Mtl.CullModeBack : Mtl.CullModeNone,
            CullsEverything = parameters.CullFront && parameters.CullBack,
            Winding = parameters.FrontFaceClockwise ? Mtl.WindingClockwise : Mtl.WindingCounterClockwise,
            DepthClipMode = parameters.DepthClipEnable ? Mtl.DepthClipModeClip : Mtl.DepthClipModeClamp,
            FillMode = 0,
            StaticParameters = parameters,
            HasDepth = depthFormatOf(description) != MtlFormat.Invalid,
            HasStencil = description.Rendering.StencilFormat != GpuFormat.Undefined,
            Mesh = mesh,
            Profile = ProfileLabelFor(vertexModule, pixelModule, mesh),
        };
        _pipelineEntries[entry.Id] = entry;
        return new PipelineHandle(entry.Id, 0, false);

        static ulong depthFormatOf(GraphicsPipelineDescription value) =>
            value.Rendering.DepthFormat != GpuFormat.Undefined
                ? MetalV2Formats.PixelFormat(value.Rendering.DepthFormat)
                : MtlFormat.Invalid;
    }

    private static bool IsIntegerFormat(ulong format) => format is
        MtlFormat.R8Uint or MtlFormat.R8Sint or MtlFormat.Rg8Uint or MtlFormat.Rg8Sint or
        MtlFormat.R16Uint or MtlFormat.R16Sint or MtlFormat.Rg16Uint or MtlFormat.Rg16Sint or
        MtlFormat.R32Uint or MtlFormat.R32Sint or MtlFormat.Rg32Uint or MtlFormat.Rg32Sint or
        MtlFormat.Rgba8Uint or MtlFormat.Rgba8Sint or MtlFormat.Rgba16Uint or MtlFormat.Rgba16Sint or
        MtlFormat.Rgba32Uint or MtlFormat.Rgba32Sint or MtlFormat.Rgb10A2Uint;

    // The vertex descriptor: one attribute per fetch, one layout per guest buffer.
    private nint CreateVertexDescriptor(GraphicsPipelineDescription description)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLVertexDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (descriptor == 0)
        {
            throw Fatal("A Metal vertex descriptor could not be created.");
        }

        var attributes = MetalNative.Send(descriptor, MetalNative.Selector("attributes"));
        var layouts = MetalNative.Send(descriptor, MetalNative.Selector("layouts"));
        var vertexInput = description.VertexInput;
        var info = description.VertexInfo;
        for (var binding = 0; binding < vertexInput.BindingCount && binding < MaxVertexBufferSlots; binding++)
        {
            var layout = MetalNative.SendAtIndex(layouts, SelObjectAtIndex, (nuint)(VertexBufferSlotBase + binding));
            var stride = vertexInput.Bindings[binding].Stride;
            // A zero stride means every vertex reads the same record; Metal expresses
            // that as a constant step function with a nonzero stride.
            MetalV2Native.SendVoidValue(layout, MetalNative.Selector("setStride:"), Math.Max(stride, 4u));
            MetalV2Native.SendVoidValue(
                layout,
                MetalNative.Selector("setStepFunction:"),
                (nuint)(stride == 0
                    ? 0
                    : vertexInput.Bindings[binding].Instance ? Mtl.StepFunctionPerInstance : Mtl.StepFunctionPerVertex));
            if (stride == 0)
            {
                MetalV2Native.SendVoidValue(layout, MetalNative.Selector("setStepRate:"), 0);
            }
        }

        for (var index = 0; index < vertexInput.AttributeCount; index++)
        {
            var binding = vertexInput.Attributes[index].Binding;
            if (binding >= MaxVertexBufferSlots)
            {
                continue;
            }

            var resource = info.Attributes[index];
            var compiledComponents = index < description.VertexStage.VertexFetchComponents.Length
                ? description.VertexStage.VertexFetchComponents[index]
                : (byte)0;
            var usedComponents = compiledComponents > 0 ? compiledComponents : (uint)resource.RegisterCount;
            var format = MetalV2Formats.VertexFormat(resource.Descriptor, usedComponents);
            var attribute = MetalNative.SendAtIndex(attributes, SelObjectAtIndex, (nuint)index);
            MetalV2Native.SendVoidValue(attribute, MetalNative.Selector("setFormat:"), (nuint)format);
            MetalV2Native.SendVoidValue(
                attribute, MetalNative.Selector("setOffset:"), (nuint)vertexInput.Attributes[index].Offset);
            MetalV2Native.SendVoidValue(
                attribute, MetalNative.Selector("setBufferIndex:"), (nuint)(VertexBufferSlotBase + binding));
        }

        return descriptor;
    }

    public PipelineHandle CreateComputePipeline(ComputePipelineDescription description)
    {
        var module = RequireModule(description.Program);
        nint error = 0;
        var state = MetalNative.Send(
            _device,
            MetalNative.Selector("newComputePipelineStateWithFunction:error:"),
            module.Function,
            ref error);
        if (state == 0)
        {
            throw Fatal(
                $"A Metal compute pipeline state could not be created: hash=0x{description.Stage.Hash:X16} " +
                $"{MetalNative.DescribeError(error)}");
        }

        Interlocked.Increment(ref _pipelineCreations);
        var threads = new MtlSize
        {
            Width = Math.Max(module.Shader.ThreadgroupSizeX, 1),
            Height = Math.Max(module.Shader.ThreadgroupSizeY, 1),
            Depth = Math.Max(module.Shader.ThreadgroupSizeZ, 1),
        };
        if (threads.Width * threads.Height * threads.Depth == 1 && description.Input.ThreadsX != 0)
        {
            threads = new MtlSize
            {
                Width = Math.Max(description.Input.ThreadsX, 1),
                Height = Math.Max(description.Input.ThreadsY, 1),
                Depth = Math.Max(description.Input.ThreadsZ, 1),
            };
        }

        var entry = new MetalPipelineEntry
        {
            Id = ++_nextPipelineId,
            State = state,
            Graphics = false,
            ThreadsPerThreadgroup = threads,
            Profile = ProfileLabelFor(module, null, null),
        };
        _pipelineEntries[entry.Id] = entry;
        return new PipelineHandle(entry.Id, 0, false);
    }

    // The depth/stencil state object: Metal keeps the compare functions, the write
    // enable and the stencil masks in one immutable object, while the executor hands
    // the masks over as dynamic state, so the object is cached on the pair.
    private nint GetDepthStencilState(MetalPipelineEntry entry, in DynamicDrawState state)
    {
        var parameters = entry.StaticParameters;
        var stencilEnabled = state.StencilTestEnabled && entry.HasStencil && parameters is not null &&
            !MetalV2Diagnostics.IgnoreFragmentTests;
        var key = MixDepthStencilKey(entry, in state, stencilEnabled);
        if (_depthStencilStates.TryGetValue(key, out var found))
        {
            return found;
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLDepthStencilDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (descriptor == 0)
        {
            throw Fatal("A Metal depth/stencil descriptor could not be created.");
        }

        try
        {
            var depthCompare = entry.HasDepth && state.DepthTestEnabled && !MetalV2Diagnostics.IgnoreFragmentTests
                ? MetalV2Formats.CompareFunction(state.DepthCompare)
                : MetalV2Formats.CompareFunction(GpuCompareOp.Always);
            MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setDepthCompareFunction:"), depthCompare);
            MetalNative.SendVoidBool(
                descriptor,
                MetalNative.Selector("setDepthWriteEnabled:"),
                entry.HasDepth && state.DepthWriteEnabled);
            if (stencilEnabled)
            {
                ConfigureStencil(
                    descriptor,
                    MetalNative.Selector("setFrontFaceStencil:"),
                    parameters!.StencilFront,
                    state.FrontStencil);
                ConfigureStencil(
                    descriptor,
                    MetalNative.Selector("setBackFaceStencil:"),
                    parameters.StencilBack,
                    state.BackStencil);
            }

            var created = MetalNative.Send(
                _device, MetalNative.Selector("newDepthStencilStateWithDescriptor:"), descriptor);
            if (created == 0)
            {
                throw Fatal("A Metal depth/stencil state could not be created.");
            }

            _depthStencilStates[key] = created;
            return created;
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }
    }

    private void ConfigureStencil(
        nint descriptor,
        nint selector,
        in StencilOperations operations,
        in StencilMasks masks)
    {
        var stencil = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLStencilDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (stencil == 0)
        {
            throw Fatal("A Metal stencil descriptor could not be created.");
        }

        try
        {
            MetalV2Native.SendVoidValue(
                stencil,
                MetalNative.Selector("setStencilCompareFunction:"),
                MetalV2Formats.CompareFunction(operations.Compare));
            MetalV2Native.SendVoidValue(
                stencil,
                MetalNative.Selector("setStencilFailureOperation:"),
                MetalV2Formats.StencilOperation(operations.FailOperation));
            MetalV2Native.SendVoidValue(
                stencil,
                MetalNative.Selector("setDepthStencilPassOperation:"),
                MetalV2Formats.StencilOperation(operations.PassOperation));
            MetalV2Native.SendVoidValue(
                stencil,
                MetalNative.Selector("setDepthFailureOperation:"),
                MetalV2Formats.StencilOperation(operations.DepthFailOperation));
            MetalV2Native.SendVoidUInt(stencil, MetalNative.Selector("setReadMask:"), masks.CompareMask);
            MetalV2Native.SendVoidUInt(stencil, MetalNative.Selector("setWriteMask:"), masks.WriteMask);
            MetalNative.SendVoid(descriptor, selector, stencil);
        }
        finally
        {
            MetalNative.SendVoid(stencil, SelRelease);
        }
    }

    private static ulong MixDepthStencilKey(MetalPipelineEntry entry, in DynamicDrawState state, bool stencilEnabled)
    {
        var hash = 14695981039346656037UL;
        void Mix(ulong value) => hash = (hash ^ value) * 1099511628211UL;
        Mix(entry.HasDepth ? 1UL : 0UL);
        Mix(state.DepthTestEnabled ? 1UL : 0UL);
        Mix(state.DepthWriteEnabled ? 1UL : 0UL);
        Mix((ulong)state.DepthCompare);
        Mix(stencilEnabled ? 1UL : 0UL);
        if (stencilEnabled && entry.StaticParameters is { } parameters)
        {
            Mix((ulong)parameters.StencilFront.Compare);
            Mix((ulong)parameters.StencilFront.FailOperation);
            Mix((ulong)parameters.StencilFront.PassOperation);
            Mix((ulong)parameters.StencilFront.DepthFailOperation);
            Mix((ulong)parameters.StencilBack.Compare);
            Mix((ulong)parameters.StencilBack.FailOperation);
            Mix((ulong)parameters.StencilBack.PassOperation);
            Mix((ulong)parameters.StencilBack.DepthFailOperation);
            Mix(state.FrontStencil.CompareMask | ((ulong)state.FrontStencil.WriteMask << 32));
            Mix(state.BackStencil.CompareMask | ((ulong)state.BackStencil.WriteMask << 32));
        }

        return hash;
    }

    public bool TryResolveColorOutput(
        uint dataFormat,
        uint numberType,
        uint componentSwap,
        out Gen5PixelOutputKind outputKind,
        out Gen5ColorComponentMapping componentMapping)
    {
        if (MetalGuestFormats.TryDecodeRenderTargetFormat(dataFormat, numberType, componentSwap, out var format))
        {
            outputKind = format.OutputKind;
            componentMapping = format.ExportMapping;
            return true;
        }

        outputKind = default;
        componentMapping = default;
        return false;
    }

    public bool TryReadGuestWord(ulong address, out uint word)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!TryReadGuest(address, bytes))
        {
            word = 0;
            return false;
        }

        word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    public bool TryReadCleanGuestWord(ulong address, out uint word)
    {
        word = 0;
        if (_imageCache.HasGpuModifiedImageBytes(address, sizeof(uint)))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!_backing.TryReadBacking(address, bytes))
        {
            return false;
        }

        word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    public bool TryReadResidentGuestBytes(ulong address, Span<byte> destination, bool clean)
    {
        if (destination.IsEmpty)
        {
            return true;
        }

        if (_imageCache.HasGpuModifiedImageBytes(address, (ulong)destination.Length))
        {
            return false;
        }

        _ = clean;
        return _backing.TryReadBacking(address, destination);
    }

    private void DisposePipelines()
    {
        foreach (var entry in _pipelineEntries.Values)
        {
            if (entry.State != 0)
            {
                MetalNative.SendVoid(entry.State, SelRelease);
            }
        }

        _pipelineEntries.Clear();
        foreach (var state in _depthStencilStates.Values)
        {
            MetalNative.SendVoid(state, SelRelease);
        }

        _depthStencilStates.Clear();
        foreach (var module in _modules)
        {
            if (module.Function != 0)
            {
                MetalNative.SendVoid(module.Function, SelRelease);
            }

            if (module.Library != 0)
            {
                MetalNative.SendVoid(module.Library, SelRelease);
            }
        }

        _modules.Clear();
        foreach (var sampler in _samplers.Values)
        {
            MetalNative.SendVoid(sampler, SelRelease);
        }

        _samplers.Clear();
    }
}
