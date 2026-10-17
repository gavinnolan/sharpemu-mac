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

        // Set for a rectangle list: the pipeline state per control-point index type
        // (none, 16-bit, 32-bit), and the stride of each vertex buffer the patch control
        // points step through (zero for a per-instance or constant one), which a draw
        // with a vertex offset rebinds at that offset.
        public nint[]? RectangleStates;
        public uint[]? RectangleVertexStrides;

        // Set when the vertex program picks a viewport per primitive; the draw then
        // fills every viewport slot rather than only the first.
        public bool WritesViewportIndex;

        // What a pass begun for a draw of this pipeline needs to skip a colour load
        // (MetalPassLoads): the clip positions the vertex program makes from the vertex
        // index alone, and the colour attachments every fragment overwrites whole.
        public float[]? ConstantPositions;
        public uint OverwriteMask;

        // The shader hashes behind this pipeline, formatted once for the per-draw GPU
        // profiler; null unless SHARPEMU_METAL_V2_PROFILE_SHADERS=1.
        public string? Profile;

        // The vertex module's hash (the compute module's, for a compute pipeline), for
        // the tiny-pass diagnostics and the target dump's labels.
        public ulong VertexHash;

        // The vertex descriptor this pipeline was built with, for the draw-state report:
        // a stride, a step function or an attribute format that does not match what the
        // program fetches makes every vertex the same one, and the draw then rasterises
        // nothing at all while looking perfectly ordinary from the outside.
        public string? VertexLayout;
    }

    // One compiled MSL module: its library, its function, and the shader it came from.
    private sealed class MetalShaderModule
    {
        public nint Library;
        public nint Function;
        public Gen5MslShader Shader = null!;
        public ulong Hash;

        // The library compile on the module pool; Library and Function are set once it is
        // done, which RequireModule waits for.
        public Task? Compiling;

        // The post-tessellation form a rectangle-list pipeline draws through, built the
        // first time one uses this vertex program.
        public nint RectangleLibrary;
        public nint RectangleFunction;
    }

    private static readonly nint SelSetAttachmentPixelFormat = MetalNative.Selector("setPixelFormat:");

    private readonly Dictionary<ulong, MetalPipelineEntry> _pipelineEntries = new();
    private readonly List<MetalShaderModule> _modules = new();
    private readonly Dictionary<ulong, nint> _depthStencilStates = new();
    private ulong _nextPipelineId;

    public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
    {
        _ = programId;
        if (shader is not MetalCompiledGuestShader metal)
        {
            throw Fatal($"The shader handle was not compiled by the Metal backend: type={shader.GetType().Name}.");
        }

        if (stage == ShaderStage.Compute)
        {
            MetalShaderPrewarm.NoteRuntimeComputeModule(metal);
        }

        var module = new MetalShaderModule
        {
            Shader = metal.Shader,
            Hash = hash,
        };

        // The library compiles on the module pool and the handle is returned at once: the
        // caller goes on translating the draw's other program, whose library then compiles
        // alongside, and only the pipeline state waits for both (RequireModule). A draw that
        // brought two new programs used to run one front end after the other on the GPU
        // worker, with the translation of the second in between.
        void build()
        {
            try
            {
                if (stage == ShaderStage.Compute)
                {
                    // The background prewarm may have this source in hand; its library is
                    // then a cache hit here once it is done.
                    MetalShaderPrewarm.WaitWhileCompiling(metal);
                }

                BuildModule(module, stage);
            }
            finally
            {
                MetalShaderPrewarm.GameCompileDone();
            }
        }

        // From here until the module is built the background prewarm holds back.
        MetalShaderPrewarm.GameCompileStarted();
        if (MetalLibraryCompilePool.Enabled)
        {
            module.Compiling = MetalLibraryCompilePool.Shared.Run(build);
        }
        else
        {
            build();
        }

        int index;
        // Module creation can reach the host from the shader cache's own threads,
        // while RequireModule runs on the GPU worker; the list is shared state.
        lock (_modules)
        {
            _modules.Add(module);
            index = _modules.Count;
        }

        Interlocked.Increment(ref _shaderCompilations);
        // Module zero never identifies a module, so the handle is the one-based index.
        return (ulong)index;
    }

    // The library and the function of a module; on the module pool or the caller's thread.
    private void BuildModule(MetalShaderModule module, ShaderStage stage)
    {
        var shader = module.Shader;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // The MSL front end runs here, and the driver's back end runs when a pipeline
        // state is made from the function. They are separate costs with separate
        // caches, so they are counted separately. A graphics library the library prewarm
        // already compiled is taken from it instead.
        var library = CompileLibrary(
            shader.Source, module.Hash, shader.UsesPerVertexInputs, prewarmable: stage != ShaderStage.Compute);
        Interlocked.Add(ref _libraryTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            var function = MetalNative.Send(
                library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString(shader.EntryPoint));
            if (function == 0)
            {
                MetalNative.SendVoid(library, SelRelease);
                throw Fatal(
                    $"The MSL library has no entry point: entry={shader.EntryPoint} hash=0x{module.Hash:X16}.");
            }

            module.Library = library;
            module.Function = function;
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }

        MetalV2Diagnostics.Trace(
            $"compiled module hash=0x{module.Hash:X16} entry={shader.EntryPoint} " +
            $"ms={(System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}");
    }

    private nint RequireRectangleFunction(MetalShaderModule module)
    {
        lock (module)
        {
            if (module.RectangleFunction != 0)
            {
                return module.RectangleFunction;
            }

            var library = CompileLibrary(
                Gen5MslRectangleList.BuildSource(module.Shader), module.Hash, usesPerVertexInputs: false, prewarmable: true);
            var function = MetalNative.Send(
                library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString(Gen5MslRectangleList.EntryPoint));
            if (function == 0)
            {
                MetalNative.SendVoid(library, SelRelease);
                throw Fatal(
                    $"The rectangle-list MSL library has no entry point: entry={Gen5MslRectangleList.EntryPoint} hash=0x{module.Hash:X16}.");
            }

            module.RectangleLibrary = library;
            module.RectangleFunction = function;
            return function;
        }
    }

    // Computes are left to the compute prewarm, which compiles them before the game runs.
    private nint CompileLibrary(string source, ulong hash, bool usesPerVertexInputs, bool prewarmable)
    {
        var listed = prewarmable && MetalLibraryPrewarm.Active;
        var key = listed ? MetalLibraryPrewarm.Key(source, usesPerVertexInputs) : 0;
        if (listed && MetalLibraryPrewarm.TryTake(key, out var prewarmed))
        {
            MetalLibraryPrewarm.NoteTaken(key);
            return prewarmed;
        }

        var library = TryCompileLibrary(_device, source, usesPerVertexInputs, out var error);
        if (library == 0)
        {
            throw Fatal($"An MSL library failed to compile: hash=0x{hash:X16} {error}");
        }

        if (listed)
        {
            MetalLibraryPrewarm.NoteCompiled(key, source, usesPerVertexInputs);
        }

        return library;
    }

    // The one MSL front-end call, shared with the shader prewarm so both ask the driver
    // with the same options and hit the same cache entries. Zero and a description on
    // failure; any thread.
    internal static nint TryCompileLibrary(nint device, string source, bool usesPerVertexInputs, out string error)
    {
        error = string.Empty;
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            var options = MetalNative.Send(
                MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
                MetalNative.Selector("init"));
            if (options == 0)
            {
                error = "Metal compile options could not be created.";
                return 0;
            }

            try
            {
                // Fast math off (the translated GCN float semantics do not survive
                // it) and an explicit MSL version, so texture atomics are available.
                MetalNative.ApplyMslCompileOptions(options);
                if (usesPerVertexInputs)
                {
                    MetalNative.SetMslLanguageVersion(options, Gen5MslShader.PerVertexInputsLanguageVersion);
                }

                nint compileError = 0;
                var library = MetalNative.Send(
                    device,
                    MetalNative.Selector("newLibraryWithSource:options:error:"),
                    MetalNative.NsString(source),
                    options,
                    ref compileError);
                if (library == 0)
                {
                    error = MetalNative.DescribeError(compileError);
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

    // The module with its library and function, waiting for a compile still on the pool.
    private MetalShaderModule RequireModule(in ShaderProgram program)
    {
        MetalShaderModule module;
        lock (_modules)
        {
            var index = (int)program.Module - 1;
            if (index < 0 || index >= _modules.Count)
            {
                throw Fatal($"The shader module handle is unknown: module={program.Module} program={program.Id}.");
            }

            module = _modules[index];
        }

        if (module.Compiling is { } compiling)
        {
            if (!compiling.IsCompleted)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                ((IAsyncResult)compiling).AsyncWaitHandle.WaitOne();
                Interlocked.Add(ref _libraryWaitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }

            // A failed compile is fatal where the module is first needed, as it was where
            // the module was made.
            compiling.GetAwaiter().GetResult();
        }

        return module;
    }

    /// <summary>A rectangle list draws one patch per rectangle, so its count is needed up
    /// front; see <see cref="DrawRectangles"/>.</summary>
    public bool TakesIndirectRectangleListArguments => false;

    /// <summary>Metal has no fan primitive; the executor hands fans over as triangle lists.</summary>
    public bool DrawsTriangleFans => false;

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
        // A rectangle list draws through the program's post-tessellation form (see
        // Gen5MslRectangleList); the index type of its control points is part of the
        // pipeline state, so one state is built for each kind of draw.
        var rectangleList = mesh is null && parameters.Topology == GpuPrimitiveTopology.PatchList;
        if (mesh is not null)
        {
            // One line per mesh pipeline: what the launch was sized from, for reading a
            // title's primitive shaders off its log.
            Console.Error.WriteLine(
                $"[METAL-V2][MESH-PIPE] vs=0x{vertexModule.Hash:X16} wave64={(mesh.Wave64 ? 1 : 0)} " +
                $"narrow={(mesh.NarrowWave64 ? 1 : 0)} gs={(mesh.HasGeometryHalf ? 1 : 0)} " +
                $"lds_bytes={mesh.LdsBytes} device_lds={(mesh.DeviceLds ? 1 : 0)} " +
                $"max_v={mesh.MaxVertices} max_p={mesh.MaxPrimitives} threads={mesh.ThreadsPerThreadgroup}");
        }

        if (rectangleList && description.VertexInput.BindingCount > Gen5MslRectangleList.IndexBufferIndex - VertexBufferSlotBase)
        {
            throw Fatal(
                $"A rectangle list binds vertex buffers into the slots its draw block and index buffer use: " +
                $"buffers={description.VertexInput.BindingCount} vertex=0x{description.VertexStage.Hash:X16}.");
        }

        var state = CreateRenderPipelineState(
            description, vertexModule, pixelModule, mesh, rectangleList, Mtl.ControlPointIndexTypeNone, out var vertexLayout);
        nint[]? rectangleStates = rectangleList
            ?
            [
                state,
                CreateRenderPipelineState(
                    description, vertexModule, pixelModule, mesh, true, Mtl.ControlPointIndexTypeUInt16, out _),
                CreateRenderPipelineState(
                    description, vertexModule, pixelModule, mesh, true, Mtl.ControlPointIndexTypeUInt32, out _),
            ]
            : null;

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
            RectangleStates = rectangleStates,
            RectangleVertexStrides = rectangleList ? RectangleVertexStrides(description.VertexInput) : null,
            WritesViewportIndex = vertexModule.Shader.WritesViewportIndex,
            ConstantPositions = mesh is null ? vertexModule.Shader.ConstantPositions : null,
            OverwriteMask = OverwriteMaskOf(description, pixelModule),
            Profile = ProfileLabelFor(vertexModule, pixelModule, mesh),
            VertexHash = vertexModule.Hash,
            VertexLayout = vertexLayout,
        };
        _pipelineEntries[entry.Id] = entry;
        return new PipelineHandle(entry.Id, 0, false);

        static ulong depthFormatOf(GraphicsPipelineDescription value) =>
            value.Rendering.DepthFormat != GpuFormat.Undefined
                ? MetalV2Formats.PixelFormat(value.Rendering.DepthFormat)
                : MtlFormat.Invalid;
    }

    /// <summary>The colour attachments every fragment of the pipeline writes whole: the
    /// pixel program returns a value for it and cannot discard, blending is off (as the
    /// pipeline state below decides it) and the write mask has every channel. One sample.</summary>
    private static uint OverwriteMaskOf(GraphicsPipelineDescription description, MetalShaderModule? pixelModule)
    {
        var parameters = description.StaticParameters;
        var count = (int)Math.Min(description.Rendering.ColorCount, 32u);
        Span<bool> blends = stackalloc bool[count];
        Span<nint> writeMasks = stackalloc nint[count];
        for (var index = 0; index < count; index++)
        {
            var format = MetalV2Formats.PixelFormat(description.Rendering.ColorFormats[index]);
            // The same test the pipeline state below uses to turn blending on.
            blends[index] = parameters.GetBlendEnable(index) && !parameters.GetBlendBypass(index) && !IsIntegerFormat(format);
            writeMasks[index] = MetalV2Formats.ColorWriteMask(parameters.GetColorMask(index));
        }

        return MetalPassLoads.OverwriteMask(
            pixelModule is not null && pixelModule.Shader.CannotDiscard,
            pixelModule?.Shader.ColorOutputMask ?? 0,
            parameters.Samples,
            blends,
            writeMasks);
    }

    private nint CreateRenderPipelineState(
        GraphicsPipelineDescription description,
        MetalShaderModule vertexModule,
        MetalShaderModule? pixelModule,
        Gen5MslMeshInfo? mesh,
        bool rectangleList,
        nuint controlPointIndexType,
        out string? vertexLayout)
    {
        var parameters = description.StaticParameters;
        var descriptorClass = mesh is null ? "MTLRenderPipelineDescriptor" : "MTLMeshRenderPipelineDescriptor";
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class(descriptorClass), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (descriptor == 0)
        {
            throw Fatal($"A Metal render pipeline descriptor could not be created: class={descriptorClass}.");
        }

        nint state;
        vertexLayout = null;
        try
        {
            if (mesh is null)
            {
                MetalNative.SendVoid(
                    descriptor,
                    MetalNative.Selector("setVertexFunction:"),
                    rectangleList ? RequireRectangleFunction(vertexModule) : vertexModule.Function);
                MetalV2Native.SendVoidValue(
                    descriptor, MetalNative.Selector("setRasterSampleCount:"), Math.Max(parameters.Samples, 1));
                if (rectangleList)
                {
                    // Every patch is one rectangle tessellated at factor 1: the four corners as
                    // two triangles. Counter-clockwise output keeps the guest triangle's facing,
                    // since corners (0,0), (1,0) and (0,1) are its three vertices in order.
                    MetalV2Native.SendVoidValue(
                        descriptor, MetalNative.Selector("setTessellationFactorStepFunction:"), Mtl.TessellationFactorStepFunctionConstant);
                    MetalV2Native.SendVoidValue(
                        descriptor, MetalNative.Selector("setTessellationPartitionMode:"), Mtl.TessellationPartitionModeInteger);
                    MetalV2Native.SendVoidValue(
                        descriptor, MetalNative.Selector("setTessellationOutputWindingOrder:"), (nuint)Mtl.WindingCounterClockwise);
                    MetalV2Native.SendVoidValue(
                        descriptor, MetalNative.Selector("setTessellationControlPointIndexType:"), controlPointIndexType);
                }
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

            if (mesh is null && (vertexModule.Shader.WritesLayer || vertexModule.Shader.WritesViewportIndex))
            {
                // Layered and multi-viewport rendering need the primitive class up front.
                MetalV2Native.SendVoidValue(
                    descriptor,
                    MetalNative.Selector("setInputPrimitiveTopology:"),
                    MetalV2Formats.TopologyClass(parameters.Topology));
            }

            if (pixelModule is not null)
            {
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), pixelModule.Function);
            }

            if (parameters.AlphaToCoverageEnable)
            {
                MetalNative.SendVoidBool(descriptor, MetalNative.Selector("setAlphaToCoverageEnabled:"), true);
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
                var vertexDescriptor = CreateVertexDescriptor(description, rectangleList, out vertexLayout);
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexDescriptor:"), vertexDescriptor);
                MetalNative.SendVoid(vertexDescriptor, SelRelease);
            }

            nint error = 0;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
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
            Interlocked.Add(ref _pipelineTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
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

        return state;
    }

    private static bool IsIntegerFormat(ulong format) => format is
        MtlFormat.R8Uint or MtlFormat.R8Sint or MtlFormat.Rg8Uint or MtlFormat.Rg8Sint or
        MtlFormat.R16Uint or MtlFormat.R16Sint or MtlFormat.Rg16Uint or MtlFormat.Rg16Sint or
        MtlFormat.R32Uint or MtlFormat.R32Sint or MtlFormat.Rg32Uint or MtlFormat.Rg32Sint or
        MtlFormat.Rgba8Uint or MtlFormat.Rgba8Sint or MtlFormat.Rgba16Uint or MtlFormat.Rgba16Sint or
        MtlFormat.Rgba32Uint or MtlFormat.Rgba32Sint or MtlFormat.Rgb10A2Uint;

    private static uint[] RectangleVertexStrides(PipelineVertexInputState vertexInput)
    {
        var strides = new uint[Math.Min((int)vertexInput.BindingCount, MaxVertexBufferSlots)];
        for (var binding = 0; binding < strides.Length; binding++)
        {
            strides[binding] = vertexInput.Bindings[binding].Instance ? 0 : vertexInput.Bindings[binding].Stride;
        }

        return strides;
    }

    // The vertex descriptor: one attribute per fetch, one layout per guest buffer. A
    // rectangle list steps its per-vertex buffers per patch control point, which is what
    // hands each corner of the post-tessellation function all three guest vertices.
    private nint CreateVertexDescriptor(GraphicsPipelineDescription description, bool rectangleList, out string? layoutReport)
    {
        var report = MetalV2Diagnostics.ReportVertexLayout ? new System.Text.StringBuilder() : null;
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
                    : vertexInput.Bindings[binding].Instance ? Mtl.StepFunctionPerInstance
                    : rectangleList ? Mtl.StepFunctionPerPatchControlPoint : Mtl.StepFunctionPerVertex));
            if (stride == 0)
            {
                MetalV2Native.SendVoidValue(layout, MetalNative.Selector("setStepRate:"), 0);
            }

            report?.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $" buf{VertexBufferSlotBase + binding}:stride={stride}{(vertexInput.Bindings[binding].Instance ? ":instance" : string.Empty)}");
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
            report?.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $" a{index}:fmt={(int)format}:off={vertexInput.Attributes[index].Offset}" +
                $":buf={VertexBufferSlotBase + binding}:guest={resource.Descriptor.Format}:used={usedComponents}");
        }

        layoutReport = report?.ToString();
        return descriptor;
    }

    // Pipeline states can be built off the command-stream thread, which stops a long
    // compile blocking every queue. Off by default: measured on Demon's Souls it
    // removed the compile from the worker (pipeline_ms fell to four milliseconds a
    // window) without making the title faster, because pipeline creation was never
    // its cost — and a dispatch that suspends re-runs the program preparation and
    // resource materialisation in front of the pipeline request, which are. Turning
    // this on is worth it once a retry no longer repeats that work.
    private static readonly bool AsynchronousPipelines =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_ASYNC_PIPELINES") == "1";

    private static bool SynchronousPipelines => !AsynchronousPipelines;

    private MetalPipelineCompiler? _compiler;

    private MetalPipelineCompiler Compiler => _compiler ??= new MetalPipelineCompiler(
        () => _commandStream?.NotifyPipelineReady(),
        Math.Max(2, Environment.ProcessorCount / 2));

    /// <summary>Compiles that have been started, finished, shared and their time.</summary>
    public (long Started, long Finished, long Deduped, double Milliseconds) TakeCompilerCounters() =>
        _compiler?.TakeCounters() ?? (0, 0, 0, 0);

    public int PipelinesCompiling => _compiler?.InFlight ?? 0;

    public bool SupportsPipelineSuspend => !SynchronousPipelines;

    public void NotePipelineNotReady()
    {
        _pipelineNotReady = true;
        // The dispatch runs again when the pipeline is ready, and asks for its copy again.
        DisarmPendingCopy();
    }

    private bool _pipelineNotReady;

    bool SharpEmu.Libs.Gpu.GpuCommands.ICommandStreamHost.ConsumePipelineNotReady()
    {
        var pending = _pipelineNotReady;
        _pipelineNotReady = false;
        return pending;
    }

    /// <summary>
    /// The pipeline state for a dispatch, or false while the driver is still building
    /// it on the compile pool. The caller suspends the packet and runs it again when
    /// the compile reports in.
    /// </summary>
    public bool TryCreateComputePipeline(ComputePipelineDescription description, out PipelineHandle handle)
    {
        if (SynchronousPipelines)
        {
            handle = CreateComputePipeline(description);
            return true;
        }

        handle = default;
        var module = RequireModule(description.Program);
        var function = module.Function;
        var hash = description.Stage.Hash;
        if (!Compiler.TryTake(description.Program.Id, () => BuildComputePipelineState(function, hash), out var state))
        {
            return false;
        }

        handle = RegisterComputePipeline(description, module, state);
        return true;
    }

    // Runs on the compile pool: MTLDevice is thread safe, and nothing here touches
    // the host's own state.
    private nint BuildComputePipelineState(nint function, ulong hash)
    {
        nint error = 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = MetalNative.Send(
            _device,
            MetalNative.Selector("newComputePipelineStateWithFunction:error:"),
            function,
            ref error);
        Interlocked.Add(ref _pipelineTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        if (state == 0)
        {
            throw Fatal(
                $"A Metal compute pipeline state could not be created: hash=0x{hash:X16} " +
                $"{MetalNative.DescribeError(error)}");
        }

        return state;
    }

    public PipelineHandle CreateComputePipeline(ComputePipelineDescription description)
    {
        var module = RequireModule(description.Program);
        nint error = 0;
        // The driver turns the function into a GPU binary here, on the worker thread,
        // and the guest's dispatch packet waits for it. Measured on Demon's Souls'
        // load phase this is tens to hundreds of milliseconds per new shader.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = MetalNative.Send(
            _device,
            MetalNative.Selector("newComputePipelineStateWithFunction:error:"),
            module.Function,
            ref error);
        Interlocked.Add(ref _pipelineTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        if (state == 0)
        {
            throw Fatal(
                $"A Metal compute pipeline state could not be created: hash=0x{description.Stage.Hash:X16} " +
                $"{MetalNative.DescribeError(error)}");
        }

        return RegisterComputePipeline(description, module, state);
    }

    // The pipeline entry for a finished compute state. Runs on the command stream, so
    // the entry table and the identifier counter stay single threaded.
    private PipelineHandle RegisterComputePipeline(
        ComputePipelineDescription description,
        MetalShaderModule module,
        nint state)
    {
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
            VertexHash = module.Hash,
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
        _wordReads++;
        if (_wordLineThread == Environment.CurrentManagedThreadId && TryReadWordFromLine(address, out word))
        {
            return true;
        }

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

        ApplyPendingCommandProcessorBytes(address, bytes);
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
        if (!_backing.TryReadBacking(address, destination))
        {
            return false;
        }

        ApplyPendingCommandProcessorBytes(address, destination);
        return true;
    }

    private Func<ulong, bool>? _dataWritePending;

    // A data-only table word the GPU may still be writing: the shader is re-planned to load
    // it on the device (DeviceDataLoads). Asked without waiting. A pending store with no
    // evaluated range covers every address, so it names no word in particular and teaches
    // nothing; only a recorded range that reaches this dword does.
    public Func<ulong, bool>? DataWritePending =>
        MetalV2Diagnostics.DataLoadsOnDevice ? _dataWritePending ??= IsDataWordPending : null;

    private bool IsDataWordPending(ulong address) =>
        _gpuWrites.MayCover(address, sizeof(uint)) &&
        _gpuWrites.PendingSerial(address, sizeof(uint), CompletedSerial) != 0 &&
        _gpuWrites.LastMatch.Origin != MetalGpuWriteOrigin.Unbounded;

    // Resource tables are read straight from the backing, so a WRITE_DATA that patched one
    // and has yet to land would otherwise be read as the table it replaced.
    private void ApplyPendingCommandProcessorBytes(ulong address, Span<byte> destination)
    {
        if (_cpOverlay.Count != 0 &&
            _cpOverlay.Coverage(address, (ulong)destination.Length, _completedSerialReader ??= () => CompletedSerial) !=
            MetalOverlayCoverage.None)
        {
            _cpOverlay.Apply(address, destination);
            Interlocked.Increment(ref _overlayReads);
        }
    }

    private void DisposePipelines()
    {
        // Let whatever is still compiling finish before the device goes away.
        _compiler?.Dispose();
        _compiler = null;
        foreach (var entry in _pipelineEntries.Values)
        {
            if (entry.State != 0)
            {
                MetalNative.SendVoid(entry.State, SelRelease);
            }

            // The first rectangle state is entry.State itself.
            for (var index = 1; index < (entry.RectangleStates?.Length ?? 0); index++)
            {
                MetalNative.SendVoid(entry.RectangleStates![index], SelRelease);
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
            try
            {
                module.Compiling?.Wait();
            }
            catch (AggregateException)
            {
                // A compile that failed left nothing to release.
            }

            if (module.Function != 0)
            {
                MetalNative.SendVoid(module.Function, SelRelease);
            }

            if (module.Library != 0)
            {
                MetalNative.SendVoid(module.Library, SelRelease);
            }

            if (module.RectangleFunction != 0)
            {
                MetalNative.SendVoid(module.RectangleFunction, SelRelease);
                MetalNative.SendVoid(module.RectangleLibrary, SelRelease);
            }
        }

        _modules.Clear();
        foreach (var sampler in _samplers.Values)
        {
            MetalNative.SendVoid(sampler.Handle, SelRelease);
        }

        _samplers.Clear();
    }
}
