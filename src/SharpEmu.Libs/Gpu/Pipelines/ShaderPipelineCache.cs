// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The pipeline provider behind the executor: programs by identity, pipelines by their full static state.
internal sealed partial class ShaderPipelineCache : IShaderPipelineProvider
{
    private const uint VertexUserDataBase = 8;
    private const uint MaxPixelInputs = 32;
    private const uint MaxViewportDimension = 16384;

    // The geometry stage programs its LDS allocation in 128-dword granules, like compute.
    private const uint LocalDataShareGranuleDwords = 128;

    private readonly CpuContext _context;
    private readonly IGuestGpuBackend _compiler;
    private readonly IShaderPipelineHost _host;
    private readonly ShaderHeaderRegistry _registry;
    private readonly ShaderProgramCache _programs;
    private readonly VertexTableMetadataCache _vertexTables = new();
    // The last user data array of each graphics stage, kept under _resolveGate.
    private uint[]? _vertexUserData;
    private uint[]? _pixelUserData;
    private readonly List<Gen5PixelOutputBinding> _boundOutputs = new(ContextRegisters.ColorTargetCount);
    private readonly Dictionary<GraphicsPipelineKey, PipelineHandle> _graphicsPipelines = new();
    private readonly Dictionary<ComputePipelineKey, PipelineHandle> _computePipelines = new();
    private readonly object _gate = new();
    // The pipeline maps: a lookup takes this lock or _gate, an addition both; a cached draw so does
    // not wait for the program preparation a resolving thread holds _gate for.
    private readonly object _pipelineGate = new();
    private readonly bool _strictShaders = Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") != "0";
    private readonly HashSet<(ShaderStage Stage, ulong Hash, uint CodeSize)> _reportedShaderSkips = [];

    public ShaderPipelineCache(CpuContext context, IShaderPipelineHost host, IGuestGpuBackend compiler, ShaderHeaderRegistry registry)
    {
        _context = context;
        _compiler = compiler;
        _host = host;
        _registry = registry;
        _programs = new ShaderProgramCache(context, compiler, host);
    }

    public bool SupportsPrimitiveShaderMeshPipelines => _compiler.SupportsPrimitiveShaderMeshPipelines;

    // The geometry-engine state one NGG subgroup runs under, for a draw the backend will
    // compile as the merged ES/GS workgroup stage. Null keeps the per-vertex path, which is
    // what every backend without mesh pipelines gets.
    private Gen5NggStageInfo? PrimitiveShaderStageInfo(
        VertexStageRegisters vertex,
        ShaderInterfaceRegisters shaderInterface,
        uint shaderStages)
    {
        // Exactly the draws the executor admits as mesh draws, or a program would be
        // compiled as the merged ES/GS stage with no mesh pipeline to run it - and an
        // ordinary vertex program that happens to touch LDS would have its workgroup sized
        // from geometry-engine registers the draw never programmed.
        if (!SupportsPrimitiveShaderMeshPipelines || !VgtShaderStages.SelectsPrimitiveShaderOnly(shaderStages))
        {
            return null;
        }

        return new Gen5NggStageInfo(
            VerticesPerSubgroup: shaderInterface.ExportVerticesPerSubgroup,
            PrimitivesPerSubgroup: shaderInterface.GeometryPrimitivesPerSubgroup,
            MaxOutputPerSubgroup: shaderInterface.MaxOutputPerSubgroup,
            OutputPrimitiveType: shaderInterface.GeometryOutputPrimitiveType,
            PrimitiveAmplificationFactor: shaderInterface.PrimitiveShaderSubgroupControl,
            LocalDataShareBytes: vertex.GeometryResource2.LocalDataShareSize *
                LocalDataShareGranuleDwords * sizeof(uint),

            // GE_CNTL's group sizes live in the user-config bank, which this lookup does not
            // see; they are per-draw launch state and travel in the mesh draw-info block.
            VertexGroupSize: 0,
            PrimitiveGroupSize: 0)
        {
            IsWave64 = VgtShaderStages.IsGeometryWave64(shaderStages),
        };
    }

    // s2..s7 of the merged stage: the launch state the geometry engine loads, never user data.
    private const ulong MergedStageSystemScalars = 0xFCUL;

    // Every merged ES/GS wave starts with SPI_SHADER_USER_DATA_ADDR_LO/HI_GS in s[0:1]. The
    // GS half of a fused program reads its own user data through that pointer, so a fused
    // program compiled as the merged workgroup stage takes the pointer as user data: its
    // user data starts at s0, the pointer fills s[0:1], and s2..s7 stay the system scalars
    // the mesh prologue seeds. Any other program keeps its user data at s8.
    internal ShaderSource WithGeometryUserDataAddress(ShaderSource source, ulong geometryUserDataAddress)
    {
        if (!source.Registered.IsFused || source.UserDataBase != VertexUserDataBase ||
            !ShaderCompileRequest.RequiresPrimitiveShaderWorkgroup(_programs.Decode(source)))
        {
            return source;
        }

        var userData = new uint[VertexUserDataBase + source.UserData.Length];
        userData[0] = (uint)geometryUserDataAddress;
        userData[1] = (uint)(geometryUserDataAddress >> 32);
        source.UserData.CopyTo(userData, (int)VertexUserDataBase);
        return source with
        {
            UserData = userData,
            UserDataBase = 0,
            ExcludedUserDataRegisters = MergedStageSystemScalars,
        };
    }

    public ShaderProgramCache Programs => _programs;

    public int GraphicsPipelineCount => _graphicsPipelines.Count;

    public int ComputePipelineCount => _computePipelines.Count;

    // The user data of one stage: the count from its resource register, else the registers the guest wrote.
    private static uint[] UserData(UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, ulong shaderAddress, string label, ref uint[]? previous)
    {
        var count = declaredCount;
        if (count == 0 && probeWrittenRegisters)
        {
            count = registers.Count;
        }

        if (count > UserScalarRegisters.Capacity)
        {
            throw GpuFatal.Fatal($"The shader declares more user registers than the bank holds: label={label} shader=0x{shaderAddress:X16} count={count}.");
        }

        // Nothing mutates a source's user data, so a draw whose registers match the last one's shares its array.
        var words = registers.Values.AsSpan(0, (int)count);
        if (previous is not null && words.SequenceEqual(previous))
        {
            return previous;
        }

        return previous = words.ToArray();
    }

    private ShaderSource PrepareSource(ulong codeAddress, ShaderStage stage, string label, UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, uint userDataBase, ref uint[]? userDataMemo)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramSourceRead);
        var registered = _registry.Require(codeAddress, label);
        foreach (var (address, sizeBytes) in registered.CodeRanges)
        {
            _host.SynchronizeShaderCode(address, sizeBytes);
        }

        var hash = ShaderIdentity.Compute(_context.Memory, codeAddress, registered.CodeRanges, label);
        var userData = UserData(registers, declaredCount, probeWrittenRegisters, codeAddress, label, ref userDataMemo);
        return new ShaderSource(registered, hash, userData, userDataBase, stage);
    }

    // Program resolution runs on the render thread and on a thread resolving draws ahead of it;
    // the program, decode and materialization caches it updates are used under this lock only.
    // A pipeline lookup takes _pipelineGate instead, so it does not wait for a resolution.
    private readonly object _resolveGate = new();

    public GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        bool depthAspectBound)
    {
        lock (_resolveGate)
        {
            return GetGraphicsProgramsCore(vertex, pixel, shaderInterface, context, targetExportMapping, pixelActive, depthBound,
                depthAspectBound);
        }
    }

    private GraphicsPrograms GetGraphicsProgramsCore(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        bool depthAspectBound)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var vertexSource = PrepareSource(
            vertex.ExportAddress, ShaderStage.Vertex, "vertex", vertex.GeometryUserScalars, vertex.GeometryResource2.UserScalarCount,
            probeWrittenRegisters: true, VertexUserDataBase, ref _vertexUserData);
        var vertexInfo = PrepareVertexInput(vertexSource, shaderInterface, context);
        vertexInfo.IeeeMode = vertex.GeometryResource1.IeeeMode;
        ShaderSource? pixelSource = null;
        PixelInputInfo? pixelInfo = null;
        Gen5PixelOutputBinding[] pixelOutputs = [];
        var dropUnwritten = false;
        var attributeCount = 0u;
        if (pixelActive)
        {
            pixelSource = PrepareSource(
                pixel.Address, ShaderStage.Pixel, "pixel", pixel.UserScalars, pixel.Resource2.UserScalarCount,
                probeWrittenRegisters: true, userDataBase: 0, ref _pixelUserData);
            if (RenderTrace.Enabled)
                RenderTrace.Write($"PixelDrawState shader=0x{pixelSource.Address:X16} hash=0x{pixelSource.Hash:X16} " +
                    $"user_data=[{string.Join(",", pixelSource.UserData.Select(word => $"{word:X8}"))}]");
            using var pixelProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PixelInputResolution);
            var pixelProgram = _programs.Decode(pixelSource);
            attributeCount = InterpolatedAttributeCount(pixelProgram);
            var inputCount = shaderInterface.PixelInputControl & 0x3Fu;
            if (inputCount == 0)
            {
                inputCount = Math.Min(attributeCount, MaxPixelInputs);
            }

            if (inputCount > MaxPixelInputs)
            {
                throw GpuFatal.Fatal($"The pixel program declares too many interpolators: shader=0x{pixel.Address:X16} count={inputCount}.");
            }

            Span<byte> outputModes = stackalloc byte[PixelInputInfo.TargetCount];
            Span<ColorComponentMap> outputMappings = stackalloc ColorComponentMap[PixelInputInfo.TargetCount];
            pixelOutputs = ResolveBoundTargets(context, targetExportMapping, depthBound ? pixelProgram.PixelColorExportMasks : null,
                outputModes, outputMappings);
            pixelInfo = PixelStageInputResolver.Resolve(_context, pixelSource.Registered, shaderInterface, outputModes, outputMappings, inputCount,
                depthAspectBound);
            // SPI_PS_INPUT_CNTL can map an input to any parameter export, beyond the input count;
            // the vertex program must declare every location the pixel program reads.
            attributeCount = Math.Max(attributeCount, ReadVertexOutputCount(pixelProgram, pixelInfo));
            pixelInfo.IeeeMode = pixel.Resource1.IeeeMode;
        }

        var primitiveShader = PrimitiveShaderStageInfo(vertex, shaderInterface, context.ShaderStages);
        if (primitiveShader is { } ngg && vertexSource.Registered.IsFused)
        {
            // The GS half of a fused program assembles its input primitives from LDS, so the
            // mesh prologue needs to know there is one and how much each primitive can emit.
            primitiveShader = ngg with { GeometryVerticesOut = Math.Max(shaderInterface.GeometryMaxVerticesOut, 1u) };
        }

        var vertexCompileSource = primitiveShader is null
            ? vertexSource
            : WithGeometryUserDataAddress(vertexSource, vertex.GeometryUserDataAddress);
        ShaderProgram vertexProgram;
        ShaderProgram pixelProgramHandle = default;
        ShaderStageResources vertexStage;
        ShaderStageResources pixelStage = default;
        lock (_gate)
        {
            using var lockedScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrepareLocked);
            var pushDataCursor = 0u;
            if (pixelSource is not null)
            {
                if (!TryPrepareProgram(
                    pixelSource,
                    new StageCompileOptions
                    {
                        PixelInfo = pixelInfo,
                        PixelOutputs = pixelOutputs,
                        PixelInputEnable = shaderInterface.PixelInputEnable,
                        PixelInputAddress = shaderInterface.PixelInputAddress,
                    },
                    ref pushDataCursor,
                    out pixelProgramHandle,
                    out pixelStage))
                    return new GraphicsPrograms { Available = false };
            }

            if (!TryPrepareProgram(
                vertexCompileSource,
                new StageCompileOptions
                {
                    VertexInfo = vertexInfo,
                    RequiredVertexOutputCount = (int)attributeCount,
                    PrimitiveShader = primitiveShader,
                },
                ref pushDataCursor,
                out vertexProgram,
                out vertexStage))
                return new GraphicsPrograms { Available = false };
        }

        vertexInfo.Stage = vertexStage;
        if (pixelInfo is not null)
        {
            pixelInfo.Stage = pixelStage;
        }

        SolidColorClear? solidClear = null;
        var disableBlending = false;
        if (pixelInfo is not null)
        {
            var vertexProgramWords = _programs.Decode(vertexSource);
            var pixelProgramWords = _programs.Decode(pixelSource!);
            if (IsProceduralFullscreenClearPair(vertexProgramWords, vertexInfo, vertexStage, pixelProgramWords, pixelStage))
            {
                var color = DecodeSolidClearColor(pixelStage.Resources.UserData);
                solidClear = new SolidColorClear(color.Red, color.Green, color.Blue, color.Alpha);
            }
            else
            {
                disableBlending = IsTransparentPremultipliedFill(context, pixelOutputs, vertexInfo, vertexStage, pixelStage);
            }
        }

        return new GraphicsPrograms
        {
            Vertex = vertexProgram,
            Pixel = pixelProgramHandle,
            VertexInput = vertexInfo,
            PixelInput = pixelInfo ?? new PixelInputInfo(),
            SolidClear = solidClear,
            DisableBlending = disableBlending,
            DropUnwrittenColorTargets = dropUnwritten,
            PositionStream = FindPositionStream(vertexInfo),
        };
    }

    // The vertex tables of the draw, the clip-space transform when clipping is off and the
    // draw's clip-space conventions.
    private VertexInputInfo PrepareVertexInput(ShaderSource source, ShaderInterfaceRegisters shaderInterface, ContextRegisters context)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VertexInputResolution);
        var clipSpace = default(ClipSpaceTransform);
        if (context.Clip.ClipDisable)
        {
            ref readonly var viewport = ref context.ScreenViewport.Viewports[0];
            var limits = _host.Limits;
            clipSpace = new ClipSpaceTransform(
                true,
                viewport.XScale,
                viewport.YScale,
                viewport.XOffset,
                viewport.YOffset,
                Math.Min(limits.MaxViewportWidth, MaxViewportDimension) * 0.5f,
                Math.Min(limits.MaxViewportHeight, MaxViewportDimension) * 0.5f);
        }

        // The host viewport the draw state builds: the clip-disabled one is y-down, any
        // other follows the sign of the guest's y scale.
        clipSpace = clipSpace with
        {
            DepthNegativeOneToOne = !context.Clip.DirectXClipSpace,
            PositionYDown = context.Clip.ClipDisable || context.ScreenViewport.Viewports[0].YScale > 0f,
        };
        return VertexInputResolver.ResolveVertexInputs(_context, source.Registered, source.UserData,
            shaderInterface.VertexOutputControl, clipSpace, _vertexTables, source.Hash);
    }

    // The interpolated attributes a pixel program reads, ascending. A program never changes, and this
    // was rebuilt with LINQ on every draw.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Gen5ShaderProgram, uint[]> InterpolatedAttributeCache = new();

    private static uint[] InterpolatedAttributesOf(Gen5ShaderProgram program) =>
        InterpolatedAttributeCache.GetValue(program, static program => program.Instructions
            .Select(static instruction => instruction.Control)
            .OfType<Gen5InterpolationControl>()
            .Select(static control => control.Attribute)
            .Distinct()
            .Order()
            .ToArray());

    // One past the highest parameter location the pixel program reads, resolved as its translator does.
    private static uint ReadVertexOutputCount(Gen5ShaderProgram pixelProgram, PixelInputInfo info)
    {
        var attributes = InterpolatedAttributesOf(pixelProgram);
        if (attributes.Length == 0)
        {
            return 0;
        }

        Span<uint> controls = stackalloc uint[32];
        for (var index = 0u; index < (uint)controls.Length; index++)
        {
            controls[(int)index] = index < info.InputCount && index < (uint)info.InterpolatorSettings.Length
                ? info.InterpolatorSettings[index]
                : index;
        }

        // Attributes are distinct and below 32, so a program never has more than 32 of them.
        Span<uint> locations = attributes.Length <= 32 ? stackalloc uint[attributes.Length] : new uint[attributes.Length];
        Gen5PixelInputMapping.ResolveLocations(controls, attributes, locations);
        var highest = 0u;
        foreach (var location in locations)
        {
            highest = Math.Max(highest, location);
        }

        return highest + 1;
    }

    // The attribute list is ascending, so its last entry is the highest.
    private static uint InterpolatedAttributeCount(Gen5ShaderProgram program)
    {
        var attributes = InterpolatedAttributesOf(program);
        return attributes.Length == 0 ? 0 : attributes[^1] + 1;
    }

    // The bound colour slots in order; each output mode names the kind the pixel program exports.
    // With a depth target bound, a slot the draw never writes is dropped here and from the
    // attachments alike (RenderExecutor.DropUnwrittenColorTargets): unwrittenExportMasks is the
    // pixel program's export mask then, null otherwise.
    private Gen5PixelOutputBinding[] ResolveBoundTargets(ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping,
        uint? unwrittenExportMasks, Span<byte> outputModes, Span<ColorComponentMap> outputMappings)
    {
        var outputs = _boundOutputs;
        outputs.Clear();
        var location = 0u;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (slot != 0 && (context.RenderTargetMaskForSlot(slot) == 0 || context.ColorTargets[slot].BaseAddress == 0))
            {
                continue;
            }

            if (ColorTargetResolver.Resolve(context, slot, 0, ignoreTargetMask: false, out _) is null)
            {
                continue;
            }

            if (unwrittenExportMasks is { } exportMasks &&
                RenderExecutor.IsUnwrittenColorTarget(context, slot, targetExportMapping[(int)slot], exportMasks))
            {
                continue;
            }

            var words = context.ColorTargets[slot];
            if (!_host.TryResolveColorOutput((uint)words.Layout, (uint)words.NumberType, (uint)words.Order, out var kind, out var mapping))
            {
                throw GpuFatal.Fatal($"The color target format has no pixel output kind: slot={slot} layout={(uint)words.Layout} numberType={(uint)words.NumberType} order={(uint)words.Order}.");
            }

            // A bound slot that no export reaches keeps its contents: its color write mask is zero.
            var exportTarget = PixelExportRouting.ExportForSlot(context.ShaderInterface, slot);
            // The high nibble keys the compiled program by the export that feeds the slot.
            outputModes[(int)slot] = (byte)(((uint)kind + 1) | (uint)((exportTarget + 1) << 4));
            outputMappings[(int)slot] = new ColorComponentMap(mapping.Packed);
            // No EXP target reaches 8 and above, so an unfed slot is declared but never written.
            outputs.Add(new Gen5PixelOutputBinding(slot, location++, kind, mapping)
            {
                ExportTarget = exportTarget >= 0 ? (uint)exportTarget : ContextRegisters.ColorTargetCount + slot,
            });
        }

        if (context.BlendControls[0].UsesSecondarySource && (context.ColorTargets[0].Info & (1u << 16)) == 0 &&
            HasGuestSlotZero(outputs))
        {
            if (outputs.Count != 1 || outputs[0].Kind != Gen5PixelOutputKind.Float)
            {
                throw GpuFatal.Fatal("Dual-source blending requires one floating-point color output.");
            }

            // The second export feeds target zero's blend unit, not another attachment.
            outputs[0] = outputs[0] with { ExportTarget = 0 };
            outputs.Add(outputs[0] with { GuestSlot = 1, ExportTarget = 1, BlendSourceIndex = 1 });
        }

        return outputs.ToArray();
    }

    private static bool HasGuestSlotZero(List<Gen5PixelOutputBinding> outputs)
    {
        foreach (var output in outputs)
        {
            if (output.GuestSlot == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static VertexPositionStream? FindPositionStream(VertexInputInfo info)
    {
        foreach (var attribute in info.Attributes)
        {
            if (Gfx10UnifiedFormat.TryDecode(attribute.Descriptor.Format, out var dataFormat, out var numberFormat) &&
                dataFormat == PositionDataFormat && numberFormat == PositionNumberFormat && attribute.BufferIndex >= 0)
            {
                var buffer = info.Buffers[attribute.BufferIndex];
                return new VertexPositionStream(buffer.Address, buffer.Stride, attribute.OffsetBytes);
            }
        }

        return null;
    }

    public ComputeProgram GetComputeProgram(
        ComputeStageRegisters compute,
        ShaderInterfaceRegisters shaderInterface,
        uint dispatchInitiator,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ)
    {
        lock (_resolveGate)
        {
            return GetComputeProgramCore(compute, shaderInterface, dispatchInitiator, dimensionX, dimensionY, dimensionZ);
        }
    }

    private ComputeProgram GetComputeProgramCore(
        ComputeStageRegisters compute,
        ShaderInterfaceRegisters shaderInterface,
        uint dispatchInitiator,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        uint[]? noMemo = null;
        var source = PrepareSource(compute.Address, ShaderStage.Compute, "compute", compute.UserScalars, compute.UserScalarCount, probeWrittenRegisters: false, userDataBase: 0, ref noMemo);
        var input = ComputeStageInputResolver.Resolve(compute, source.Registered, dispatchInitiator, !_host.ComputeWave64Supported, dimensionX, dimensionY, dimensionZ);
        var systemRegisters = DecodeComputeSystemRegisters(compute);
        var program = _programs.Decode(source);
        if (TrySubmitMaskedDwordCopyKernel(program, source, systemRegisters, input, out var description))
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"Consumed a masked dword copy dispatch: shader=0x{compute.Address:X16} hash=0x{source.Hash:X16} {description}");
            }

            return new ComputeProgram { Consumed = true };
        }

        ShaderProgram handle;
        ShaderStageResources stage;
        lock (_gate)
        {
            using var lockedScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPrepareLocked);
            var pushDataCursor = 0u;
            if (!TryPrepareProgram(
                source,
                new StageCompileOptions { ComputeInfo = input, ComputeSystemRegisters = systemRegisters },
                ref pushDataCursor,
                out handle,
                out stage))
            {
                return new ComputeProgram { Available = false };
            }
        }

        input.Stage = stage;
        return new ComputeProgram { Program = handle, Input = input };
    }

    private bool TryPrepareProgram(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor,
        out ShaderProgram program, out ShaderStageResources stage)
    {
        if (_programs.TryGetProgram(source, options, _strictShaders, ref pushDataCursor, out program, out stage, out var rejection))
            return true;

        if (_reportedShaderSkips.Add((source.Stage, source.Hash, source.CodeSize)))
        {
            var tag = source.Stage == ShaderStage.Compute ? "COMPUTE_SKIPPED" : "DRAW_SKIPPED";
            Console.Error.WriteLine($"[GPU][WARN][{tag}] {rejection} " +
                "The operation was not executed. Images and FPS can be incorrect. Set SHARPEMU_STRICT_COMPUTE=1 to stop on this failure.");
        }
        return false;
    }

    // The system registers that follow the user data, in the order the resource register enables them.
    public static Gen5ComputeSystemRegisters DecodeComputeSystemRegisters(ComputeStageRegisters compute)
    {
        var nextRegister = (uint)compute.UserScalarCount;
        uint? workGroupX = null;
        uint? workGroupY = null;
        uint? workGroupZ = null;
        uint? threadGroupSize = null;
        if (compute.ThreadGroupIdXEnable)
        {
            workGroupX = nextRegister++;
        }

        if (compute.ThreadGroupIdYEnable)
        {
            workGroupY = nextRegister++;
        }

        if (compute.ThreadGroupIdZEnable)
        {
            workGroupZ = nextRegister++;
        }

        if (compute.ThreadGroupSizeEnable)
        {
            threadGroupSize = nextRegister;
        }

        return new Gen5ComputeSystemRegisters(workGroupX, workGroupY, workGroupZ, threadGroupSize);
    }

    public PipelineHandle CreateGraphicsPipeline(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        GpuPrimitiveTopology topology,
        bool primitiveRestartEnabled,
        bool disableBlending,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineCreation);
        // The lookup fills objects this thread reuses for every draw; only a new pipeline gets its own.
        var lookup = _graphicsLookup ??= new GraphicsPipelineKey
        {
            Rendering = new PipelineRenderingState(),
            VertexInput = new PipelineVertexInputState(),
            StaticParameters = new PipelineStaticParameters(),
        };
        lookup.Rendering.Reset();
        lookup.VertexInput.Reset();
        lookup.StaticParameters.Reset();
        FillGraphicsState(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
            vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts, lookup.Rendering, lookup.StaticParameters, lookup.VertexInput);
        lookup.VertexProgramId = vertexProgram.Id;
        lookup.PixelProgramId = pixelInput is not null ? pixelProgram.Id : 0;
        lock (_pipelineGate)
        {
            if (_graphicsPipelines.TryGetValue(lookup, out var cached))
            {
                return cached;
            }
        }

        var description = BuildGraphicsDescription(
            colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
            vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts);
        var key = KeyOf(description);
        lock (_gate)
        {
            if (_graphicsPipelines.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"PipelineCache create graphics vertex=0x{vertexProgram.Id:X16} pixel=0x{key.PixelProgramId:X16} colors={key.Rendering.ColorCount} " +
                    $"depth={description.StaticParameters.WithDepth} samples={description.StaticParameters.Samples}");

            }

            var created = _host.CreateGraphicsPipeline(description);
            lock (_pipelineGate)
            {
                _graphicsPipelines.Add(key, created);
            }

            ShaderCacheCounters.CountGraphicsPipeline();
            return created;
        }
    }

    [ThreadStatic]
    private static GraphicsPipelineKey? _graphicsLookup;

    // The full static state of one graphics pipeline from the draw's targets, registers and programs.
    internal static GraphicsPipelineDescription BuildGraphicsDescription(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        GpuPrimitiveTopology topology,
        bool primitiveRestartEnabled,
        bool disableBlending,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram,
        GpuSampleCount noAttachmentSampleCounts)
    {
        var renderingState = new PipelineRenderingState();
        var parameters = new PipelineStaticParameters();
        var vertexState = new PipelineVertexInputState();
        var (vertexStage, pixelStage) = FillGraphicsState(colors, in depth, vertexInput, pixelInput, context, in rendering, topology,
            primitiveRestartEnabled, disableBlending, vertexProgram, pixelProgram, noAttachmentSampleCounts, renderingState, parameters, vertexState);
        return new GraphicsPipelineDescription
        {
            Rendering = renderingState,
            VertexInput = vertexState,
            VertexInfo = vertexInput,
            VertexProgram = vertexProgram,
            VertexStage = vertexStage,
            PixelInfo = pixelInput,
            PixelProgram = pixelProgram,
            PixelStage = pixelStage,
            StaticParameters = parameters,
        };
    }

    // Writes the pipeline state into objects in their initial state; returns the stage programs.
    private static (ShaderProgramInfo VertexStage, ShaderProgramInfo? PixelStage) FillGraphicsState(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        GpuPrimitiveTopology topology,
        bool primitiveRestartEnabled,
        bool disableBlending,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram,
        GpuSampleCount noAttachmentSampleCounts,
        PipelineRenderingState renderingState,
        PipelineStaticParameters parameters,
        PipelineVertexInputState vertexState)
    {
        if (colors.Length > PipelineStaticParameters.ColorAttachmentCount)
        {
            throw GpuFatal.Fatal($"The draw binds more color attachments than a pipeline holds: count={colors.Length}.");
        }

        if (!vertexProgram.IsValid)
        {
            throw GpuFatal.Fatal("The draw has no vertex program.");
        }

        var pixelActive = pixelInput is not null;
        if (pixelActive && !pixelProgram.IsValid)
        {
            throw GpuFatal.Fatal("The draw has an active pixel stage without a pixel program.");
        }

        var vertexStage = vertexInput.Stage.Program ?? throw GpuFatal.Fatal("The vertex stage has no program.");
        var pixelStage = pixelActive ? pixelInput!.Stage.Program ?? throw GpuFatal.Fatal("The pixel stage has no program.") : null;
        var colorCount = (uint)colors.Length;
        parameters.ColorCount = colorCount;
        renderingState.ColorCount = colorCount;
        for (var index = 0; index < colors.Length; index++)
        {
            ref readonly var color = ref colors[index];
            var format = rendering.ColorAttachments[index].Format;
            if (!color.Image.IsValid || format == GpuFormat.Undefined)
            {
                throw GpuFatal.Fatal($"A color attachment has no image or format: slot={color.Slot} format={format}.");
            }

            renderingState.ColorFormats[index] = format;
            // A target the pixel program never exports keeps its contents, as on hardware; the
            // host output would otherwise write an undefined value (e.g. depth-only passes that
            // leave a color target bound and export only to the null target).
            var exportTarget = PixelExportRouting.ExportForSlot(context.ShaderInterface, color.Slot);
            var exported = pixelStage is not null &&
                (exportTarget >= 0 && ((pixelStage.PixelColorExportMasks >> (exportTarget * 4)) & 0xFu) != 0);
            var colorMask = exported ? color.Resolution.ExportMapping.ApplyMask(context.RenderTargetMaskForSlot(color.Slot)) : 0;
            parameters.SetColorMask(index, colorMask);
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"PipelineCache output slot={color.Slot} export={exportTarget} " +
                    $"guestMask=0x{context.RenderTargetMaskForSlot(color.Slot):X} " +
                    $"exported={(exported ? 1 : 0)} shaderMask=0x{pixelStage?.PixelColorExportMasks ?? 0:X8} " +
                    $"mapping=0x{color.Resolution.ExportMapping.Packed:X2} hostMask=0x{colorMask:X} format={(int)format}");
            }
        }

        var withDepth = depth.HasTarget;
        if (withDepth)
        {
            renderingState.DepthFormat = rendering.DepthFormat;
            renderingState.StencilFormat = rendering.StencilFormat;
        }

        var samples = rendering.Samples;
        if (colorCount == 0 && !withDepth)
        {
            samples = 1u << context.AntialiasingConfig.SampleCountLog2;
            var flag = ImageDescription.VulkanSampleCount(samples);
            if (flag == 0 || (noAttachmentSampleCounts & flag) == 0)
            {
                throw GpuFatal.Fatal($"The device cannot rasterize without attachments at the draw's sample count: samples={samples}.");
            }
        }

        if (samples == 0 || ImageDescription.VulkanSampleCount(samples) == 0)
        {
            throw GpuFatal.Fatal($"The draw's sample count is invalid: samples={samples}.");
        }

        var depthState = withDepth ? depth.Target.State : default;
        var rectangleList = topology == GpuPrimitiveTopology.PatchList;
        var mode = context.RasterMode;
        parameters.NegativeOneToOne = !context.Clip.DirectXClipSpace;
        parameters.DepthClipEnable = context.Clip.IsZClipEnabled;
        parameters.Topology = topology;
        parameters.PrimitiveRestartEnable = primitiveRestartEnabled;
        parameters.Samples = samples;
        parameters.SampleShadingEnable = pixelActive && samples > 1 && pixelInput!.SampleShading;
        parameters.WithDepth = withDepth;
        parameters.DepthBoundsTestEnable = depthState.DepthBoundsTestEnabled;
        parameters.DepthMinBounds = depthState.DepthMinBounds;
        parameters.DepthMaxBounds = depthState.DepthMaxBounds;
        parameters.StencilTestEnable = depthState.StencilTestEnabled;
        parameters.StencilFront = withDepth ? depthState.FrontOperations : StencilOperations.Default;
        parameters.StencilBack = withDepth ? depthState.BackOperations : StencilOperations.Default;
        parameters.CullBack = !rectangleList && mode.CullBack;
        parameters.CullFront = !rectangleList && mode.CullFront;
        parameters.FrontFaceClockwise = mode.FrontFaceClockwise;
        for (var index = 0; index < colors.Length; index++)
        {
            var slot = colors[index].Slot;
            var blend = context.BlendControls[slot];
            parameters.SetColorSourceBlend(index, (byte)blend.ColorSourceFactor);
            parameters.SetColorBlendFunction(index, (byte)blend.ColorFunction);
            parameters.SetColorDestinationBlend(index, (byte)blend.ColorDestinationFactor);
            parameters.SetAlphaSourceBlend(index, (byte)blend.AlphaSourceFactor);
            parameters.SetAlphaBlendFunction(index, (byte)blend.AlphaFunction);
            parameters.SetAlphaDestinationBlend(index, (byte)blend.AlphaDestinationFactor);
            parameters.SetSeparateAlphaBlend(index, blend.SeparateAlpha);
            parameters.SetBlendEnable(index, blend.Enable && !disableBlending);
            parameters.SetBlendBypass(index, ((context.ColorTargets[slot].Info >> 16) & 0x1) != 0);
        }

        FillVertexInputState(vertexInput, vertexState);
        return (vertexStage, pixelStage);
    }

    internal static GraphicsPipelineKey KeyOf(GraphicsPipelineDescription description) => new()
    {
        Rendering = description.Rendering,
        VertexProgramId = description.VertexProgram.Id,
        PixelProgramId = description.PixelInfo is not null ? description.PixelProgram.Id : 0,
        VertexInput = description.VertexInput,
        StaticParameters = description.StaticParameters,
    };

    private static void FillVertexInputState(VertexInputInfo info, PipelineVertexInputState state)
    {
        if (info.Buffers.Length > VertexInputInfo.MaxBuffers || info.Attributes.Length > VertexInputInfo.MaxBuffers)
        {
            throw GpuFatal.Fatal($"The vertex input is too large: buffers={info.Buffers.Length} attributes={info.Attributes.Length}.");
        }

        state.BindingCount = (byte)info.Buffers.Length;
        state.AttributeCount = (byte)info.Attributes.Length;
        for (var binding = 0; binding < info.Buffers.Length; binding++)
        {
            var buffer = info.Buffers[binding];
            state.Bindings[binding] = new PipelineVertexBinding(buffer.Stride, buffer.PerInstance);
        }

        for (var index = 0; index < info.Attributes.Length; index++)
        {
            var attribute = info.Attributes[index];
            if (attribute.BufferIndex < 0 || attribute.BufferIndex >= info.Buffers.Length)
            {
                throw GpuFatal.Fatal($"The vertex attribute names a buffer outside the input: attribute={index} buffer={attribute.BufferIndex} buffers={info.Buffers.Length}.");
            }

            state.Attributes[index] = new PipelineVertexAttribute(attribute.OffsetBytes, (byte)attribute.BufferIndex);
        }
    }

    public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineCreation);
        if (!program.IsValid)
        {
            throw GpuFatal.Fatal("The dispatch has no compute program.");
        }

        var stage = input.Stage.Program ?? throw GpuFatal.Fatal("The compute stage has no program.");
        var key = new ComputePipelineKey(program.Id);
        lock (_pipelineGate)
        {
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        lock (_gate)
        {
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"PipelineCache create compute program=0x{program.Id:X16} hash=0x{stage.Hash:X16}");
            }

            var created = _host.CreateComputePipeline(new ComputePipelineDescription { Input = input, Program = program, Stage = stage });
            lock (_pipelineGate)
            {
                _computePipelines.Add(key, created);
            }

            ShaderCacheCounters.CountComputePipeline();
            return created;
        }
    }

    public bool TryCreateComputePipeline(ComputeInputInfo input, ShaderProgram program, out PipelineHandle handle)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineCreation);
        handle = default;
        if (!program.IsValid)
        {
            throw GpuFatal.Fatal("The dispatch has no compute program.");
        }

        var stage = input.Stage.Program ?? throw GpuFatal.Fatal("The compute stage has no program.");
        var key = new ComputePipelineKey(program.Id);
        lock (_pipelineGate)
        {
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                handle = cached;
                return true;
            }
        }

        lock (_gate)
        {
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                handle = cached;
                return true;
            }

            if (!_host.TryCreateComputePipeline(
                    new ComputePipelineDescription { Input = input, Program = program, Stage = stage },
                    out var created))
            {
                return false;
            }

            lock (_pipelineGate)
            {
                _computePipelines.Add(key, created);
            }

            ShaderCacheCounters.CountComputePipeline();
            handle = created;
            return true;
        }
    }
}
