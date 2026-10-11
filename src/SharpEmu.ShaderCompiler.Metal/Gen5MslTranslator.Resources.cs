// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal;

// The resource path over a compile request: every binding comes from the layout through one
// argument buffer, every descriptor index from the memory table, user data from push data.
public static partial class Gen5MslTranslator
{
    // The range table holds at most 64 merged entries; seven halvings cover them.
    public const uint AddressRangeSearchSteps = 7;
    public const uint MaxAddressRangeCount = 64;

    public static bool TryCompileProgram(
        ShaderCompileRequest request,
        out Gen5MslShader shader,
        out string error,
        Gen5MslCodegenOptions? options = null)
    {
        shader = default!;
        try
        {
            BindingLayoutValidator.Validate(
                request.Bindings,
                request.Resources.Info,
                BindingLayout.CollectUserDataRegisters(request.Program, request.UserDataBase, request.UserDataCount),
                request.UsesGlobalDataShare,
                request.UsesFlattenedTable,
                request.ReadsShaderBase,
                request.Hash,
                request.Stage);
        }
        catch (ResourcePlanException exception)
        {
            error = exception.Message;
            return false;
        }

        if (request.Stage == ShaderStage.Pixel && !ValidatePixelOutputs(request.PixelOutputs, out error))
        {
            return false;
        }

        if (!new CompilationContext(request, options ?? Gen5MslCodegenOptions.FromEnvironment)
                .TryCompile(out shader, out error))
        {
            return false;
        }

        Gen5MslDump.Write(request, shader);
        return true;
    }

    private sealed partial class CompilationContext
    {
        private const string ResourcesName = "sharpemu_resources";
        private const string PushDataName = "sharpemu_push_data";
        private const string GlobalDataShareName = "sharpemu_gds";
        private const string GlobalDataShareWordsName = "sharpemu_gds_words";
        private const string DeviceArguments = "sharpemu_ranges, sharpemu_range_count, sharpemu_faults, sharpemu_fault_words";

        private readonly ShaderCompileRequest _request;
        private readonly Gen5MslArgumentLayout? _argumentLayout;
        private readonly Dictionary<DescriptorBindingKind, MslImageClass> _imageClasses = [];
        private readonly Dictionary<int, string> _indirectKeyScratch = [];
        private bool _hasGlobalDataShare;
        private bool _hasDeviceAddressTable;
        private bool _hasFlattenedTable;
        private bool _hasSamplers;

        private readonly record struct MslImageClass(
            string Field,
            string TextureType,
            string ComponentKind,
            bool IsStorage,
            bool DepthCompare,
            bool Multisampled,
            bool Arrayed,
            bool Cube,
            ImageDimension Dimension,
            IReadOnlyList<uint> Resources)
        {
            /// <summary>Spatial coordinate components, excluding any array layer.</summary>
            public int SpatialComponents => Dimension switch
            {
                ImageDimension.Dim1D or ImageDimension.Dim1DArray => 1,
                ImageDimension.Dim3D => 3,
                _ => 2,
            };

            /// <summary>Address components one access consumes for its coordinates.</summary>
            public int CoordinateComponents => SpatialComponents + (Arrayed ? 1 : 0);

            /// <summary>Metal spatial coordinate vector type for integer addressing.</summary>
            public string IntegerCoordinateType => SpatialComponents switch
            {
                1 => "uint",
                3 => "uint3",
                _ => "uint2",
            };

            /// <summary>Metal spatial coordinate vector type for normalized addressing.</summary>
            public string FloatCoordinateType => SpatialComponents switch
            {
                1 => "float",
                3 => "float3",
                _ => "float2",
            };

            // Guest 1D images are represented as height-one 2D images so their
            // mip/LOD/derivative semantics survive Metal's non-mipmapped 1D API.
            public bool SupportsSampleLod => !Multisampled;
            public bool SupportsMipLevels => !Multisampled;

            /// <summary>Metal exposes gather on 2D and 2D-array textures only.</summary>
            public bool SupportsGather => Dimension
                is ImageDimension.Dim2D or ImageDimension.Dim2DArray;
        }

        private readonly Gen5MslCodegenOptions _options;

        public CompilationContext(ShaderCompileRequest request, Gen5MslCodegenOptions options)
        {
            _request = request;
            _options = options;
            _argumentLayout = Gen5MslArgumentLayout.Build(request.Bindings);
            // An NGG primitive shader that exchanges data through LDS, synchronizes or asks
            // for export space is the merged ES/GS workgroup stage, which only a mesh
            // function can express. Passthrough primitive shaders keep the vertex path.
            _stage = RequiresMeshEmission(request)
                ? Gen5MslStage.Mesh
                : request.Stage switch
                {
                    ShaderStage.Vertex => Gen5MslStage.Vertex,
                    ShaderStage.Pixel => Gen5MslStage.Pixel,
                    _ => Gen5MslStage.Compute,
                };
            if (_stage == Gen5MslStage.Mesh)
            {
                _meshLdsDwords = MeshLdsDwordCount(request);
                _meshInfo = BuildMeshInfo(request, _meshLdsDwords);
            }

            _computeLdsDwords = ComputeLdsDwordCount(request);

            _pixelOutputBindings = request.PixelOutputs;
            _usesPixelValidMask =
                _stage == Gen5MslStage.Pixel &&
                request.Program.Instructions.Any(static instruction => instruction.Control is Gen5ExportControl { ValidMask: true });
            _localSizeX = Math.Max(request.LocalSizeX, 1);
            _localSizeY = Math.Max(request.LocalSizeY, 1);
            _localSizeZ = Math.Max(request.LocalSizeZ, 1);
            _waveLaneCount = request.WaveSize == 64 ? 64u : 32u;
            _requiredVertexOutputCount = request.RequiredVertexOutputCount;
            _pixelInputEnable = request.PixelInputEnable;
            _pixelInputAddress = request.PixelInputAddress;
            _pixelInputCntl = new uint[32];
            for (uint index = 0; index < 32u; index++)
            {
                _pixelInputCntl[index] = request.PixelInputCntl is not null && index < (uint)request.PixelInputCntl.Count
                    ? request.PixelInputCntl[(int)index]
                    : index;
            }
        }

        // ---- declarations ----

        // Records which bindings the layout holds and the texture type of every image class.
        private void DeclareLayoutBindings()
        {
            var request = _request;
            var info = request.Resources.Info;
            foreach (var binding in request.Bindings.Descriptors)
            {
                switch (binding.Kind)
                {
                    case DescriptorBindingKind.Buffers:
                        break;
                    case DescriptorBindingKind.Samplers:
                        _hasSamplers = true;
                        break;
                    case DescriptorBindingKind.GlobalDataShare:
                        _hasGlobalDataShare = true;
                        break;
                    case DescriptorBindingKind.DeviceAddressPageTable:
                        _hasDeviceAddressTable = true;
                        break;
                    case DescriptorBindingKind.FaultBuffer:
                        break;
                    case DescriptorBindingKind.FlattenedResourceTable:
                        _hasFlattenedTable = true;
                        break;
                    case DescriptorBindingKind.ShaderData:
                        break;
                    default:
                        DeclareImageClass(binding, info);
                        break;
                }
            }

            _hasDeviceAddressTable &= request.Bindings.Find(DescriptorBindingKind.FaultBuffer) is not null;
            foreach (var memoryIndex in request.IndirectKeyMemoryIndices)
            {
                _indirectKeyScratch[memoryIndex] = $"indirect_image_key{memoryIndex}";
            }
        }

        // A storage class takes the least access its elements need; a sampled class none.
        private void DeclareImageClass(DescriptorBinding binding, ShaderResourceInfo info)
        {
            var (resourceClass, numericClass, dimension, atomic) = ImageDescriptorBinding.Describe(binding.Kind);
            var cube = ImageDescriptorBinding.IsCube(binding.Kind);
            if (resourceClass == ImageResourceClass.None)
            {
                throw new InvalidOperationException($"binding kind {binding.Kind} is not an image class");
            }

            var isStorage = resourceClass == ImageResourceClass.Storage;
            var componentKind = numericClass switch
            {
                ImageNumericClass.Uint => "uint",
                ImageNumericClass.Sint => "int",
                _ => "float",
            };
            string? access = null;
            if (isStorage)
            {
                var reads = false;
                var writes = false;
                foreach (var resource in binding.Resources)
                {
                    var image = info.Images[(int)resource];
                    reads |= image.Read || image.Atomic || atomic;
                    writes |= image.Written || image.Atomic || atomic;
                }

                access = writes ? (reads ? "read_write" : "write") : "read";
            }

            var multisampled = dimension is ImageDimension.Dim2DMsaa or ImageDimension.Dim2DMsaaArray;
            var arrayed = dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray;
            _imageClasses[binding.Kind] = new MslImageClass(
                Gen5MslArgumentLayout.ImageClassName(binding.Kind),
                Gen5MslArgumentLayout.TextureType(dimension, componentKind, access, ImageDescriptorBinding.IsDepthCompare(binding.Kind)),
                componentKind,
                isStorage,
                ImageDescriptorBinding.IsDepthCompare(binding.Kind),
                multisampled,
                arrayed,
                cube,
                dimension,
                binding.Resources);
        }

        private void EmitResourcePrelude(StringBuilder source) =>
            source.Append(MslTemplates.Render(
                "resource_prelude",
                ("address_mask", FormatULong(DeviceAddressPaging.AddressMask)),
                ("page_bits", DeviceAddressPaging.PageBits.ToString(CultureInfo.InvariantCulture)),
                ("page_offset_mask", FormatULong(DeviceAddressPaging.PageOffsetMask)),
                ("search_steps", AddressRangeSearchSteps.ToString(CultureInfo.InvariantCulture))));

        // The argument buffer struct: one field per layout descriptor in layout order.
        private void EmitResourceStruct(StringBuilder source)
        {
            var layout = _argumentLayout!;
            if (layout.Fields.Count == 0)
            {
                return;
            }

            source.AppendLine();
            source.AppendLine($"struct {Gen5MslArgumentLayout.StructName}");
            source.AppendLine("{");
            foreach (var field in layout.Fields)
            {
                var declaration = field.FieldKind switch
                {
                    MslArgumentFieldKind.BufferPointers => $"array<device uint*, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.BufferByteCounts => $"array<uint, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.Textures => $"array<{_imageClasses[field.Kind].TextureType}, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.Samplers => $"array<sampler, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.Pointer => $"{PointerType(field.Kind)} {field.Name}",
                    MslArgumentFieldKind.StepReport => $"device atomic_uint* {field.Name}",
                    _ => $"uint {field.Name}",
                };
                source.AppendLine($"    {declaration} [[id({field.FirstId})]];");
            }

            source.AppendLine("};");
            source.AppendLine();
        }

        private static string PointerType(DescriptorBindingKind kind) => kind switch
        {
            DescriptorBindingKind.DeviceAddressPageTable => "device const Gen5AddressRange*",
            DescriptorBindingKind.FlattenedResourceTable or DescriptorBindingKind.ShaderData => "device const uint*",
            _ => "device uint*",
        };

        private void EmitResourceParameters(StringBuilder source)
        {
            if (_argumentLayout!.Fields.Count != 0)
            {
                source.AppendLine(
                    $"    constant {Gen5MslArgumentLayout.StructName}& {ResourcesName} [[buffer({Gen5MslArgumentLayout.ResourcesBufferIndex})]],");
            }

            if (_request.Bindings.UsesPushData)
            {
                source.AppendLine($"    constant uint* {PushDataName} [[buffer({Gen5MslArgumentLayout.PushDataBufferIndex})]],");
            }
        }

        // ---- initial state ----

        // Buffer locals, user data and the per-buffer byte biases from the packed offsets.
        private void EmitLayoutInitialState(StringBuilder source)
        {
            var request = _request;
            var layout = request.Bindings;
            var bufferCount = request.Resources.Info.Buffers.Count;
            for (var index = 0; index < bufferCount; index++)
            {
                source.AppendLine($"    device uint* b{index} = {ResourcesName}.buffers[{index}];");
            }

            source.AppendLine($"    uint bias[{Math.Max(bufferCount, 1)}] = {{}};");
            if (_hasGlobalDataShare)
            {
                source.AppendLine($"    device uint* {GlobalDataShareName} = {ResourcesName}.global_data_share;");
                source.AppendLine($"    uint {GlobalDataShareWordsName} = {ResourcesName}.global_data_share_bytes >> 2u;");
            }

            if (_hasDeviceAddressTable)
            {
                source.AppendLine($"    device const Gen5AddressRange* sharpemu_ranges = {ResourcesName}.address_ranges;");
                source.AppendLine($"    uint sharpemu_range_count = min({ResourcesName}.address_range_count, {MaxAddressRangeCount}u);");
                source.AppendLine($"    device uint* sharpemu_faults = {ResourcesName}.fault_bits;");
                source.AppendLine($"    uint sharpemu_fault_words = {ResourcesName}.fault_word_count;");
            }

            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
            {
                var register = layout.UserDataRegisters[index];
                if (register < ScalarRegisterFileCount)
                {
                    source.AppendLine($"    s[{register}] = {ShaderDataDword((uint)index)};");
                }
            }

            for (uint buffer = 0; buffer < layout.MemoryOffsetCount; buffer++)
            {
                source.AppendLine(
                    $"    bias[{buffer}] = ({ShaderDataDword(layout.MemoryOffsetDword + (buffer / 4))} >> {(buffer % 4) * 8}u) & 0xFFu;");
            }

            foreach (var scratch in _indirectKeyScratch.Values.OrderBy(static name => name, StringComparer.Ordinal))
            {
                source.AppendLine($"    uint {scratch} = 0u;");
            }
        }

        // One dword of this stage's block: from push data, or the shader-data buffer when spilled.
        private string ShaderDataDword(uint index)
        {
            var layout = _request.Bindings;
            if (layout.UsesPushData)
            {
                return $"{PushDataName}[{layout.PushDataStartDword + index}]";
            }

            return $"({index}u < {ResourcesName}.shader_data_words ? {ResourcesName}.shader_data[{index}u] : 0u)";
        }

        private string ComputeThreadLimit(uint component)
        {

            if (_request.Bindings is { UsesDispatchThreadLimits: true } layout)
            {
                return ShaderDataDword(layout.DispatchThreadLimitsDword + component);
            }

            var limit = component switch
            {
                0 => _request.ThreadCountX,
                1 => _request.ThreadCountY,
                _ => _request.ThreadCountZ,
            };
            return FormatUInt(limit);
        }

        // The shader base the host pushes for this draw, as two dwords.
        private (string Low, string High) ShaderBaseWords()
        {
            var layout = _request.Bindings;
            if (!layout.UsesShaderBase)
            {
                return ("0u", "0u");
            }

            return (ShaderDataDword(layout.ShaderBaseDword), ShaderDataDword(layout.ShaderBaseDword + 1));
        }

        private string LoadFlattenedWord(string slot) =>
            $"({slot} < {ResourcesName}.flattened_table_words ? {ResourcesName}.flattened_table[{slot}] : 0u)";

        private static string FormatULong(ulong value) => $"0x{value.ToString("X", CultureInfo.InvariantCulture)}ul";

        // A signed immediate widened to the address width.
        private static string SignedOffset64(int offset) => FormatULong(unchecked((ulong)(long)offset));

        // A written access is allowed only inside its handle's tracked range:
        // width <= size, address >= base, address - base <= size - width.
        private string IsWrittenAccessAllowed(int memoryIndex, string address, uint widthBytes)
        {
            if (!_hasFlattenedTable || !_request.WrittenRangeSlotByMemoryIndex.TryGetValue(memoryIndex, out var slot))
            {
                return "false";
            }

            var rangeBase = Temp("ulong", $"(ulong){LoadFlattenedWord($"{slot}u")} | ((ulong){LoadFlattenedWord($"{slot + 1}u")} << 32)");
            var rangeSize = Temp("ulong", $"(ulong){LoadFlattenedWord($"{slot + 2}u")}");
            var masked = Temp("ulong", $"{address} & {FormatULong(DeviceAddressPaging.AddressMask)}");
            return Temp(
                "bool",
                $"{widthBytes}ul <= {rangeSize} && {rangeBase} <= {masked} && ({masked} - {rangeBase}) <= ({rangeSize} - {widthBytes}ul)");
        }

        // The atomic expression over a device word pointer for one opcode suffix.
        private static bool TryGetAtomicExpression(string name, string value, string comparator, out Func<string, string> build)
        {
            Func<string, string>? candidate = name switch
            {
                "Add" => pointer => $"atomic_fetch_add_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Sub" => pointer => $"atomic_fetch_sub_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Umin" => pointer => $"atomic_fetch_min_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Umax" or "UMax" => pointer => $"atomic_fetch_max_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Smin" => pointer => $"(uint)atomic_fetch_min_explicit((device atomic_int*)({pointer}), as_type<int>({value}), memory_order_relaxed)",
                "Smax" => pointer => $"(uint)atomic_fetch_max_explicit((device atomic_int*)({pointer}), as_type<int>({value}), memory_order_relaxed)",
                "And" => pointer => $"atomic_fetch_and_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Or" => pointer => $"atomic_fetch_or_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Xor" => pointer => $"atomic_fetch_xor_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Swap" => pointer => $"atomic_exchange_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Cmpswap" => pointer => $"sharpemu_atomic_compare_swap((device atomic_uint*)({pointer}), {comparator}, {value})",
                "Inc" => pointer => $"sharpemu_atomic_step((device atomic_uint*)({pointer}), true)",
                "Dec" => pointer => $"sharpemu_atomic_step((device atomic_uint*)({pointer}), false)",
                "Fmin" => pointer => $"sharpemu_atomic_float_minmax((device atomic_uint*)({pointer}), {value}, false)",
                "Fmax" => pointer => $"sharpemu_atomic_float_minmax((device atomic_uint*)({pointer}), {value}, true)",
                // DS_MIN/MAX_F32 replace with DATA0 while DATA1 ranks below memory.
                "Freplace" => pointer => $"sharpemu_atomic_float_replace((device atomic_uint*)({pointer}), {value}, {comparator})",
                _ => null,
            };
            build = candidate!;
            return candidate is not null;
        }

        // ---- scalar memory ----

        private bool TryEmitLayoutScalarMemory(Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control, out string error)
        {
            error = string.Empty;
            var request = _request;
            // SOFFSET is read once, before any destination is written: S_BUFFER_LOAD_DWORDX2
            // and wider routinely name a destination that overlaps the offset register
            // (`s[106:107] <- s[8:11], s106`), and the hardware reads every source operand
            // before it writes the first dword. Re-reading the register file per component
            // would compute the second dword's address from the first dword's value, which
            // is what the SPIR-V translator avoids by holding the loaded SSA value.
            var dynamicOffset = control.DynamicOffsetRegister is { } register
                ? Temp("uint", ScalarExpression(register))
                : "0u";
            string? deviceAddress = null;
            for (var component = 0; component < instruction.Destinations.Count; component++)
            {
                var destination = instruction.Destinations[component];
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "invalid scalar-memory destination";
                    return false;
                }

                if (!request.Memory.TryGetIndex(instruction.Pc, (uint)component, out var memoryIndex))
                {
                    error = $"scalar load component {component} has no memory record";
                    return false;
                }

                var entry = request.Memory[memoryIndex];
                if (request.IndirectOffsetKeyMemoryIndices.Contains(memoryIndex))
                    Line($"{_indirectKeyScratch[memoryIndex]} = {dynamicOffset};");
                string value;
                if (entry.PlanningOnly)
                {
                    // A planning-only read without a slot is a heap descriptor the host resolved; nothing is emitted.
                    if (!request.FlattenedSlotByMemoryIndex.TryGetValue(memoryIndex, out var slot))
                    {
                        continue;
                    }

                    if (!_hasFlattenedTable)
                    {
                        error = "flattened read without a flattened table binding";
                        return false;
                    }

                    value = Temp("uint", LoadFlattenedWord($"{slot}u"));
                }
                else if (entry.Kind == MemoryResourceKind.ScalarBuffer)
                {
                    if (entry.Resource == MemoryAccessInfo.NoResource)
                    {
                        error = "scalar buffer load has no dense buffer";
                        return false;
                    }

                    var bindingIndex = (int)entry.Resource;
                    var byteOffset = unchecked((uint)control.ImmediateOffsetBytes + ((uint)component * sizeof(uint)));
                    var address = Temp("uint", ApplyByteBias(bindingIndex, $"({dynamicOffset} + {FormatUInt(byteOffset)})"));
                    value = Temp("uint", LoadDword(bindingIndex, address));
                }
                else
                {
                    if (instruction.Sources.Count == 0 || instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
                    {
                        error = "invalid scalar-memory address";
                        return false;
                    }

                    if (!_hasDeviceAddressTable)
                    {
                        error = "device address access without an address range binding";
                        return false;
                    }

                    if (deviceAddress is null)
                    {
                        var baseRegister = instruction.Sources[0].Value;
                        deviceAddress = Temp(
                            "ulong",
                            $"({Scalar64Expression(baseRegister)} + (ulong){dynamicOffset} + {SignedOffset64(control.ImmediateOffsetBytes)}) & {FormatULong(DeviceAddressPaging.AddressMask & ~3ul)}");
                    }

                    var componentAddress = component == 0 ? deviceAddress : $"({deviceAddress} + {component * sizeof(uint)}ul)";
                    value = Temp("uint", $"sharpemu_load_device_dword({DeviceArguments}, {componentAddress})");
                }

                if (!request.IndirectOffsetKeyMemoryIndices.Contains(memoryIndex) &&
                    _indirectKeyScratch.TryGetValue(memoryIndex, out var keyScratch))
                {
                    Line($"{keyScratch} = {value};");
                }

                StoreScalar(destination.Value, value);
            }

            return true;
        }

        // ---- global memory ----

        private bool TryEmitLayoutGlobalMemory(Gen5ShaderInstruction instruction, Gen5GlobalMemoryControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Opcode.StartsWith("Scratch", StringComparison.Ordinal))
            {
                return TryEmitScratchMemory(instruction, control, out error);
            }

            var request = _request;
            if (!request.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                error = "global access has no memory record";
                return false;
            }

            if (!_hasDeviceAddressTable)
            {
                error = "device address access without an address range binding";
                return false;
            }

            var memoryOpcode = control.UsesFlatAddress ? "Global" + instruction.Opcode["Flat".Length..] : instruction.Opcode;
            var address = control.UsesFlatAddress || control.ScalarAddress >= 125
                ? $"(ulong)v[{control.VectorAddress}] | ((ulong)v[{control.VectorAddress + 1}] << 32)"
                : $"{Scalar64Expression(control.ScalarAddress)} + (ulong)v[{control.VectorAddress}]";
            address = Temp("ulong", $"({address}) + {SignedOffset64(control.OffsetBytes)}");
            var entry = request.Memory[memoryIndex];
            if (control.UsesFlatAddress && entry.AddressSpace != FlatAddressSpace.Global)
            {
                return TryEmitFlatLocalMemory(
                    instruction, control, memoryOpcode, entry.AddressSpace, address, out error);
            }

            var writes = entry.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            var accessBytes = Math.Max((entry.DataBits + 7) / 8, 1u) * Math.Max(entry.DataDwords, 1u);
            var allowed = writes ? IsWrittenAccessAllowed(memoryIndex, address, accessBytes) : "true";

            if (memoryOpcode.StartsWith("GlobalAtomic", StringComparison.Ordinal))
            {
                if (!TryGetAtomicExpression(
                        memoryOpcode["GlobalAtomic".Length..],
                        $"v[{control.SourceVectorRegister}]",
                        $"v[{control.SourceVectorRegister + 1}]",
                        out var build))
                {
                    error = $"unsupported global opcode {instruction.Opcode}";
                    return false;
                }

                Line($"if ({ExecCondition(allowed)})");
                Line("{");
                _indent++;
                var valid = Temp("bool", "false");
                var pointer = Temp("device uint*", $"sharpemu_resolve_device_address({DeviceArguments}, {address}, {valid})");
                Line($"if ({valid})");
                Line("{");
                _indent++;
                var original = Temp("uint", build(pointer));
                if (control.Glc)
                {
                    Line($"v[{control.DestinationVectorRegister}] = {original};");
                }

                _indent--;
                Line("}");
                _indent--;
                Line("}");
                return true;
            }

            if (memoryOpcode.StartsWith("GlobalStore", StringComparison.Ordinal))
            {
                Line($"if ({ExecCondition(allowed)})");
                Line("{");
                _indent++;
                if (TryGetSubdwordStoreInfo(memoryOpcode, out var byteCount, out var sourceShift))
                {
                    var source = sourceShift == 0
                        ? $"v[{control.SourceVectorRegister}]"
                        : $"(v[{control.SourceVectorRegister}] >> {sourceShift})";
                    Line($"sharpemu_store_device_bytes({DeviceArguments}, {address}, {source}, {byteCount}u);");
                }
                else
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        Line($"sharpemu_store_device_dword({DeviceArguments}, {address} + {index * sizeof(uint)}ul, v[{control.SourceVectorRegister + index}]);");
                    }
                }

                _indent--;
                Line("}");
                return true;
            }

            if (TryGetSubdwordLoadInfo(memoryOpcode, out var loadBytes, out var signExtend, out var d16, out var d16High))
            {
                var loaded = Temp(
                    "uint",
                    $"sharpemu_load_device_unaligned({DeviceArguments}, {address}, {loadBytes}u) & {FormatUInt(loadBytes == 1 ? 0xFFu : 0xFFFFu)}");
                if (signExtend)
                {
                    loaded = Temp("uint", $"(uint)extract_bits(as_type<int>({loaded}), 0u, {loadBytes * 8}u)");
                }

                var destination = control.DestinationVectorRegister;
                if (!d16)
                {
                    StoreVector(destination, loaded);
                    return true;
                }

                StoreVector(
                    destination,
                    d16High
                        ? $"(v[{destination}] & 0x0000FFFFu) | (({loaded} & 0xFFFFu) << 16)"
                        : $"(v[{destination}] & 0xFFFF0000u) | ({loaded} & 0xFFFFu)");
                return true;
            }

            if (!memoryOpcode.StartsWith("GlobalLoad", StringComparison.Ordinal))
            {
                error = $"unsupported global opcode {instruction.Opcode}";
                return false;
            }

            var aligned = Temp("ulong", $"{address} & ~3ul");
            for (uint index = 0; index < control.DwordCount; index++)
            {
                var componentAddress = index == 0 ? aligned : $"{aligned} + {index * sizeof(uint)}ul";
                StoreVector(
                    control.DestinationVectorRegister + index,
                    $"sharpemu_load_device_dword({DeviceArguments}, {componentAddress})");
            }

            return true;
        }

        // Routes each lane of an aperture-derived FLAT access like the hardware: LDS
        // or scratch at the low dword when the high dword is in an aperture,
        // otherwise a global access through the page table.
        private bool TryEmitFlatLocalMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            string memoryOpcode,
            FlatAddressSpace addressSpace,
            string address,
            out string error)
        {
            error = string.Empty;
            var store = memoryOpcode.StartsWith("GlobalStoreDword", StringComparison.Ordinal);
            var load = memoryOpcode.StartsWith("GlobalLoadDword", StringComparison.Ordinal);
            if (!store && !load)
            {
                error = $"{instruction.Opcode} through the {addressSpace} flat aperture is not implemented";
                return false;
            }

            var canShare = addressSpace is FlatAddressSpace.Shared or FlatAddressSpace.SharedOrPrivate;
            var canPrivate = addressSpace is FlatAddressSpace.Private or FlatAddressSpace.SharedOrPrivate;
            if (canShare && !_usesLds)
            {
                error = "a shared flat aperture access has no LDS storage";
                return false;
            }

            if (canPrivate && _scratchDwordCount == 0)
            {
                error = "a private flat aperture access has no scratch storage";
                return false;
            }

            BeginExecGuard();
            var offset = Temp("uint", $"(uint){address}");
            var aperture = Temp(
                "uint",
                $"(uint)({address} >> {32 + Gen5InlineConstants.ApertureShift})");

            void Emit(Func<uint, string> word)
            {
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    if (store)
                    {
                        Line($"{word(index)} = v[{control.SourceVectorRegister + index}];");
                    }
                    else
                    {
                        StoreVector(
                            control.DestinationVectorRegister + index,
                            word(index),
                            guardWithExec: false);
                    }
                }
            }

            void Shared() => Emit(index =>
                $"sharpemu_lds[{LdsIndex(offset, index * sizeof(uint))}]");

            void Private() => Emit(index => ScratchWord(offset, index * sizeof(uint)));

            void Global()
            {
                var aligned = Temp("ulong", $"{address} & ~3ul");
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    var dwordAddress = index == 0 ? aligned : $"{aligned} + {index * sizeof(uint)}ul";
                    if (store)
                    {
                        Line($"sharpemu_store_device_dword({DeviceArguments}, {dwordAddress}, v[{control.SourceVectorRegister + index}]);");
                    }
                    else
                    {
                        StoreVector(
                            control.DestinationVectorRegister + index,
                            $"sharpemu_load_device_dword({DeviceArguments}, {dwordAddress})",
                            guardWithExec: false);
                    }
                }
            }

            void Branch(string condition, Action then, Action otherwise)
            {
                Line($"if ({condition})");
                Line("{");
                _indent++;
                then();
                _indent--;
                Line("}");
                Line("else");
                Line("{");
                _indent++;
                otherwise();
                _indent--;
                Line("}");
            }

            var isShared = $"{aperture} == {FormatUInt(Gen5InlineConstants.SharedApertureHigh >> Gen5InlineConstants.ApertureShift)}";
            var isPrivate = $"{aperture} == {FormatUInt(Gen5InlineConstants.PrivateApertureHigh >> Gen5InlineConstants.ApertureShift)}";
            if (canShare && canPrivate)
            {
                Branch(isShared, Shared, () => Branch(isPrivate, Private, Global));
            }
            else if (canShare)
            {
                Branch(isShared, Shared, Global);
            }
            else
            {
                Branch(isPrivate, Private, Global);
            }

            EndExecGuard();
            return true;
        }

        // ---- scratch ----

        // Guest scratch is per-invocation private storage, named by its own FLAT
        // segment rather than by a resource, so it never reaches the device
        // address table: it lives in a thread-local array like the SPIR-V
        // translator's Private one.
        private bool TryEmitScratchMemory(Gen5ShaderInstruction instruction, Gen5GlobalMemoryControl control, out string error)
        {
            error = string.Empty;
            if (_scratchDwordCount == 0)
            {
                error = "scratch storage was not declared";
                return false;
            }

            var address = control.ScalarAddress < 125
                ? ScalarExpression(control.ScalarAddress)
                : $"v[{control.VectorAddress}]";
            if (control.DynamicOffsetRegister is { } dynamicOffsetRegister)
            {
                address = $"{address} + {ScalarExpression(dynamicOffsetRegister)}";
            }

            if (control.OffsetBytes != 0)
            {
                address = $"{address} + {FormatUInt(unchecked((uint)control.OffsetBytes))}";
            }

            var byteAddress = Temp("uint", address);
            if (instruction.Opcode.StartsWith("ScratchStore", StringComparison.Ordinal))
            {
                BeginExecGuard();
                if (TryGetSubdwordStoreInfo(instruction.Opcode, out var byteCount, out var sourceShift))
                {
                    // A sub-dword store merges into the dword it lands in.
                    var source = sourceShift == 0
                        ? $"v[{control.SourceVectorRegister}]"
                        : $"(v[{control.SourceVectorRegister}] >> {sourceShift})";
                    var element = FormatUInt(byteCount == 1 ? 0xFFu : 0xFFFFu);
                    var shift = Temp("uint", $"({byteAddress} & 3u) << 3u");
                    var word = ScratchWord(byteAddress);
                    Line($"{word} = ({word} & ~({element} << {shift})) | (((({source}) & {element})) << {shift});");
                }
                else
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var value = control.SourceIsScalar
                            ? ScalarExpression(control.SourceVectorRegister + index)
                            : $"v[{control.SourceVectorRegister + index}]";
                        Line($"{ScratchWord(byteAddress, index * sizeof(uint))} = {value};");
                    }
                }

                EndExecGuard();
                return true;
            }

            if (!instruction.Opcode.StartsWith("ScratchLoad", StringComparison.Ordinal))
            {
                error = $"unsupported scratch opcode {instruction.Opcode}";
                return false;
            }

            var destination = control.DestinationVectorRegister;
            if (TryGetSubdwordLoadInfo(instruction.Opcode, out var loadBytes, out var signExtend, out var d16, out var d16High))
            {
                var shift = Temp("uint", $"({byteAddress} & 3u) << 3u");
                var loaded = Temp(
                    "uint",
                    $"({ScratchWord(byteAddress)} >> {shift}) & {FormatUInt(loadBytes == 1 ? 0xFFu : 0xFFFFu)}");
                if (signExtend)
                {
                    loaded = Temp("uint", $"(uint)extract_bits(as_type<int>({loaded}), 0u, {loadBytes * 8}u)");
                }

                StoreVector(
                    destination,
                    !d16 ? loaded
                        : d16High
                            ? $"(v[{destination}] & 0x0000FFFFu) | (({loaded} & 0xFFFFu) << 16)"
                            : $"(v[{destination}] & 0xFFFF0000u) | ({loaded} & 0xFFFFu)");
                return true;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                StoreVector(destination + index, ScratchWord(byteAddress, index * sizeof(uint)));
            }

            return true;
        }

        // The scratch array element one byte address names. The dword index wraps
        // into the allocation, so an out-of-range guest address stays inside it.
        private string ScratchWord(string byteAddress, uint offsetBytes = 0)
        {
            var address = offsetBytes == 0 ? byteAddress : $"({byteAddress} + {offsetBytes}u)";
            return _scratchDwordCount > 1
                ? $"sharpemu_scratch[(({address}) >> 2) % {_scratchDwordCount}u]"
                : "sharpemu_scratch[0]";
        }

        // ---- buffers ----

        private bool TryResolveLayoutBuffer(uint pc, out int bindingIndex, out BufferResource resource)
        {
            bindingIndex = -1;
            resource = null!;
            var request = _request;
            if (!request.Memory.TryGetIndex(pc, 0, out var memoryIndex))
            {
                return false;
            }

            var entry = request.Memory[memoryIndex];
            if (entry.Resource == MemoryAccessInfo.NoResource || entry.Resource >= (uint)request.Resources.Info.Buffers.Count)
            {
                return false;
            }

            bindingIndex = (int)entry.Resource;
            resource = request.Resources.Info.Buffers[bindingIndex];
            return true;
        }

        // ---- images ----

        // An access over several descriptors takes one constant-element case per descriptor:
        // the mip operand selects a per-mip element, the indirect key a candidate.
        private bool TryGetImageElementCases(Gen5ShaderInstruction instruction, Gen5ImageControl image, out string selector, out IReadOnlyList<(uint Resource, uint Element)> elements, out string error)
        {
            selector = string.Empty;
            elements = [];
            error = string.Empty;
            var request = _request;
            if (!request.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                return false;
            }

            var entry = request.Memory[memoryIndex];
            var info = request.Resources.Info;
            if (entry.Resource == MemoryAccessInfo.NoResource || entry.Resource >= (uint)info.Images.Count)
            {
                return false;
            }

            var resourceIndex = (int)entry.Resource;
            var imageInfo = info.Images[resourceIndex];
            var kind = ImageDescriptorBinding.ForImage(imageInfo);
            if (kind is null || !_imageClasses.TryGetValue(kind.Value, out var imageClass))
            {
                return false;
            }

            var classElements = imageClass.Resources.ToList();
            var element = classElements.IndexOf((uint)resourceIndex);
            if (element < 0)
            {
                return false;
            }

            if (imageInfo.MipMode == ImageMipMode.DynamicStorage && instruction.Opcode is "ImageLoadMip" or "ImageStoreMip")
            {
                // A mip past the last descriptor matches no case and does nothing.
                selector = Temp("uint", ImageIntegerAddress(image, imageClass.CoordinateComponents));
                elements = Enumerable.Range(0, (int)imageInfo.MipCount)
                    .Select(mip => ((uint)resourceIndex, (uint)element + (uint)mip)).ToList();
                return true;
            }

            if (request.IndirectRootByMemoryIndex.TryGetValue(memoryIndex, out var keyMemoryIndex) && imageInfo.IndirectSearchIterations != 0)
            {
                if (!_hasFlattenedTable)
                {
                    error = "indirect image access without a flattened table binding";
                    return false;
                }

                var candidates = info.Images[(int)imageInfo.IndirectRoot].IndirectResources;
                var candidateElements = new List<(uint Resource, uint Element)>();
                foreach (var candidate in candidates)
                {
                    var candidateKind = ImageDescriptorBinding.ForImage(info.Images[(int)candidate]);
                    if (candidateKind is null || !_imageClasses.TryGetValue(candidateKind.Value, out var candidateClass))
                    {
                        error = $"indirect candidate {candidate} has no declared binding class";
                        return false;
                    }
                    var candidateElement = candidateClass.Resources.ToList().IndexOf(candidate);
                    if (candidateElement < 0)
                    {
                        error = $"indirect candidate {candidate} is not an element of {candidateKind.Value}";
                        return false;
                    }

                    candidateElements.Add((candidate, (uint)candidateElement));
                }

                selector = SelectIndirectCandidate(imageInfo, keyMemoryIndex, (uint)candidateElements.Count);
                elements = candidateElements;
                return true;
            }

            return false;
        }

        // Resolves the image an instruction reads through its class array into a texture local,
        // with its sampler for sampling operations. Every image dimension the binding layout
        // can describe is accessible; the resolved class carries the shape the caller needs
        // to build coordinates with.
        private bool TryResolveLayoutImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string texture,
            out string samplerName,
            out MslImageClass resolved,
            out uint dstSelect,
            out string mipLevel,
            out string error,
            (uint Resource, uint Element)? fixedElement = null)
        {
            error = string.Empty;
            texture = string.Empty;
            samplerName = string.Empty;
            resolved = default;
            dstSelect = DescriptorConstants.IdentityImageSwizzle;
            mipLevel = "0u";
            var request = _request;
            var info = request.Resources.Info;
            if (!request.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                error = "image access has no memory record";
                return false;
            }

            var entry = request.Memory[memoryIndex];
            if (entry.Resource == MemoryAccessInfo.NoResource || entry.Resource >= (uint)info.Images.Count)
            {
                error = "image access has no dense image";
                return false;
            }

            var resourceIndex = (int)(fixedElement?.Resource ?? entry.Resource);
            var imageInfo = info.Images[resourceIndex];
            var bindingKind = ImageDescriptorBinding.ForImage(imageInfo);
            if (bindingKind is null || !_imageClasses.TryGetValue(bindingKind.Value, out var imageClass))
            {
                error = $"image {resourceIndex} has no declared binding class";
                return false;
            }

            // Metal multisampled textures expose read() with a sample index and nothing
            // else: no sampling, no writes. The binding layout already refuses storage
            // MSAA classes, so only the sampled fetch paths can reach one.
            if (imageClass.Multisampled && instruction.Opcode is not ("ImageLoad" or "ImageLoadMip" or "ImageGetResinfo"))
            {
                error = $"multisampled image opcode {instruction.Opcode} is not supported on Metal";
                return false;
            }

            var element = imageClass.Resources.ToList().IndexOf((uint)resourceIndex);
            if (element < 0)
            {
                error = $"image {resourceIndex} is not an element of {bindingKind.Value}";
                return false;
            }

            // Several descriptors (per-mip elements, indirect candidates) need the caller's constant case.
            var severalDescriptors =
                (imageInfo.MipMode == ImageMipMode.DynamicStorage && instruction.Opcode is "ImageLoadMip" or "ImageStoreMip") ||
                (request.IndirectRootByMemoryIndex.ContainsKey(memoryIndex) && imageInfo.IndirectSearchIterations != 0);
            if (fixedElement is null && severalDescriptors)
            {
                error = "image access over several descriptors needs a constant element case";
                return false;
            }

            // A sampled mip load keeps its mip operand; a storage case is already its own mip view.
            // The mip is the address component right after the coordinates, so it moves with
            // the dimension (2 for 2D, 3 for a 2D array or a volume, 1 for 1D).
            if (!imageClass.IsStorage && instruction.Opcode == "ImageLoadMip")
            {
                mipLevel = Temp("uint", ImageIntegerAddress(image, imageClass.CoordinateComponents));
            }

            texture = Temp(imageClass.TextureType, $"{ResourcesName}.{imageClass.Field}[{fixedElement?.Element ?? (uint)element}u]");
            resolved = imageClass;
            if (UsesSampler(instruction.Opcode))
            {
                if (!_hasSamplers)
                {
                    error = "sampling without a sampler binding";
                    return false;
                }

                if (!request.Resources.SamplerByMemoryIndex.TryGetValue(memoryIndex, out var samplerIndex))
                {
                    samplerIndex = entry.Sampler;
                }

                if (samplerIndex == MemoryAccessInfo.NoResource)
                {
                    error = "sampled access has no dense sampler";
                    return false;
                }

                samplerName = Temp("sampler", $"{ResourcesName}.samplers[{samplerIndex}]");
            }

            dstSelect = imageInfo.ShaderSwizzle;
            return true;
        }

        // Searches the sorted key mapping of an indirect table for the captured key; the
        // result is the candidate-local index, candidate 0 when the key is absent or out of range.
        private string SelectIndirectCandidate(ImageResource imageInfo, int keyMemoryIndex, uint candidateCount)
        {
            var key = _indirectKeyScratch.TryGetValue(keyMemoryIndex, out var scratch) ? scratch : "0u";
            var mapping = imageInfo.IndirectMappingOffset;
            var count = Temp("uint", LoadFlattenedWord($"{mapping}u"));
            var low = Temp("uint", "0u");
            var high = Temp("uint", count);
            for (uint iteration = 0; iteration < imageInfo.IndirectSearchIterations; iteration++)
            {
                var middle = Temp("uint", $"{low} + (({high} - {low}) >> 1u)");
                var probeSlot = Temp("uint", $"{mapping + 1}u + ({middle} << 1u)");
                var probeKey = Temp("uint", LoadFlattenedWord(probeSlot));
                var moveUp = Temp("bool", $"{probeKey} < {key} && {low} < {high}");
                Line($"{low} = {moveUp} ? {middle} + 1u : {low};");
                Line($"{high} = {moveUp} ? {high} : {middle};");
            }

            var foundSlot = Temp("uint", $"{mapping + 1}u + ({low} << 1u)");
            var foundKey = Temp("uint", LoadFlattenedWord(foundSlot));
            var found = Temp("bool", $"{low} < {count} && {foundKey} == {key}");
            var mappedSlot = Temp("uint", $"{foundSlot} + 1u");

            // The mapping holds candidate-local indices: the root's candidate list in order.
            var mapped = Temp("uint", LoadFlattenedWord(mappedSlot));
            return Temp("uint", $"{found} && {mapped} < {candidateCount}u ? {mapped} : 0u");
        }

        // ---- global data share ----

        // DS operations with the GDS bit run over the shared buffer with device atomics;
        // lane operations have no GDS form.
        private bool TryEmitGlobalDataShare(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (!_hasGlobalDataShare)
            {
                error = "GDS access without a global data share binding";
                return false;
            }

            var opcode = instruction.Opcode;
            if (opcode.Contains("Swizzle", StringComparison.Ordinal) || opcode.Contains("Bpermute", StringComparison.Ordinal) ||
                opcode.Contains("Permute", StringComparison.Ordinal) || opcode.Contains("Addtid", StringComparison.Ordinal))
            {
                error = $"lane operation {opcode} is not valid on the global data share";
                return false;
            }

            switch (opcode)
            {
                case "DsAppend":
                case "DsConsume":
                    return TryEmitGlobalDataShareCounter(instruction, control, out error);
                case "DsWriteB32":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreGlobalDataShareWord(index, RawSource(instruction, 1));
                    return true;
                }

                case "DsWriteB64":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreGlobalDataShareWord(index, RawSource(instruction, 1));
                    StoreGlobalDataShareWord($"({index} + 1u)", RawSource(instruction, 2));
                    return true;
                }

                case "DsWrite2B64":
                case "DsWrite2St64B64":
                    return TryEmitDataShareWritePair64(instruction, control, out error);
                case "DsWrite2B32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    var first = Temp("uint", GlobalDataShareIndex(address, control.Offset0 * sizeof(uint)));
                    var second = Temp("uint", GlobalDataShareIndex(address, control.Offset1 * sizeof(uint)));
                    StoreGlobalDataShareWord(first, RawSource(instruction, 1));
                    StoreGlobalDataShareWord(second, RawSource(instruction, 2));
                    return true;
                }

                case "DsReadB32":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreVector(instruction.Destinations[0].Value, LoadGlobalDataShareWord(index));
                    return true;
                }

                case "DsReadI8":
                {
                    // The byte the low two bits of the offset address select,
                    // sign-extended out of the word that holds it.
                    var address = Temp("uint", RawSource(instruction, 0));
                    var byteAddress = Temp("uint", $"{address} + {control.SingleOffsetBytes}u");
                    var word = Temp("uint", LoadGlobalDataShareWord(
                        Temp("uint", GlobalDataShareIndex(address, control.SingleOffsetBytes))));
                    var shift = Temp("uint", $"({byteAddress} & 3u) << 3u");
                    StoreVector(
                        instruction.Destinations[0].Value,
                        $"(uint)(((int)(({word} >> {shift}) << 24u)) >> 24)");
                    return true;
                }

                case "DsReadB64":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreVector(instruction.Destinations[0].Value, LoadGlobalDataShareWord(index));
                    StoreVector(instruction.Destinations[1].Value, LoadGlobalDataShareWord($"({index} + 1u)"));
                    return true;
                }

                case "DsRead2B64":
                    return TryEmitDataShareReadPair64(instruction, control, out error);
                case "DsRead2B32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    var first = Temp("uint", GlobalDataShareIndex(address, control.Offset0 * sizeof(uint)));
                    var second = Temp("uint", GlobalDataShareIndex(address, control.Offset1 * sizeof(uint)));
                    StoreVector(instruction.Destinations[0].Value, LoadGlobalDataShareWord(first));
                    StoreVector(instruction.Destinations[1].Value, LoadGlobalDataShareWord(second));
                    return true;
                }

                default:
                    if (Gen5ShaderTranslator.IsDataShareAtomic(opcode))
                    {
                        return TryEmitGlobalDataShareAtomic(instruction, control, out error);
                    }

                    error = $"unsupported GDS opcode {opcode}";
                    return false;
            }
        }

        private bool TryEmitDataShareWritePair64(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            var offsetStride = instruction.Opcode == "DsWrite2St64B64" ? 512u : 8u;
            if (instruction.Sources.Count < 5)
            {
                error = "The paired 64-bit write requires an address and two source-register pairs.";
                return false;
            }

            BeginExecGuard();
            var address = Temp("uint", RawSource(instruction, 0));
            // Equal offsets select only the first source pair.
            var componentCount = control.Offset0 == control.Offset1 ? 2 : 4;
            for (var component = 0; component < componentCount; component++)
            {
                var pairOffset = component < 2 ? control.Offset0 : control.Offset1;
                var byteOffset = pairOffset * offsetStride + (uint)(component % 2) * sizeof(uint);
                var value = RawSource(instruction, component + 1);
                if (control.Gds)
                    StoreGlobalDataShareWord(GlobalDataShareIndex(address, byteOffset), value);
                else
                    Line($"sharpemu_lds[{LdsIndex(address, byteOffset)}] = {value};");
            }
            EndExecGuard();
            return true;
        }

        private bool TryEmitDataShareReadPair64(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 1 || instruction.Destinations.Count < 4)
            {
                error = "The paired 64-bit read requires an address and four destination registers.";
                return false;
            }

            BeginExecGuard();
            var address = Temp("uint", RawSource(instruction, 0));
            var values = new string[4];
            // Capture both values before an overlapping destination changes the address.
            for (var component = 0; component < values.Length; component++)
            {
                var pairOffset = component < 2 ? control.Offset0 : control.Offset1;
                var byteOffset = pairOffset * sizeof(ulong) + (uint)(component % 2) * sizeof(uint);
                values[component] = Temp("uint", control.Gds
                    ? LoadGlobalDataShareWord(GlobalDataShareIndex(address, byteOffset))
                    : $"sharpemu_lds[{LdsIndex(address, byteOffset)}]");
            }
            for (var component = 0; component < values.Length; component++)
                StoreVector(instruction.Destinations[component].Value, values[component], guardWithExec: false);
            EndExecGuard();
            return true;
        }

        private static string GlobalDataShareIndex(string address, uint offsetBytes) =>
            offsetBytes == 0 ? $"({address}) >> 2u" : $"(({address}) + {offsetBytes}u) >> 2u";

        private static string LoadGlobalDataShareWord(string index) =>
            $"({index} < {GlobalDataShareWordsName} ? {GlobalDataShareName}[{index}] : 0u)";

        private void StoreGlobalDataShareWord(string index, string value) =>
            Line(ExecGuarded($"if ({index} < {GlobalDataShareWordsName}) {{ {GlobalDataShareName}[{index}] = {value}; }}"));

        // The atomic suffix TryGetAtomicExpression knows for one DS opcode, shared
        // by the LDS and GDS paths because both encodings name the same operation.
        private static string DataShareAtomicName(string opcode) => opcode switch
        {
            "DsAddU32" or "DsAddRtnU32" => "Add",
            "DsSubU32" or "DsSubRtnU32" => "Sub",
            "DsIncU32" or "DsIncRtnU32" => "Inc",
            "DsDecU32" or "DsDecRtnU32" => "Dec",
            "DsMinI32" or "DsMinRtnI32" => "Smin",
            "DsMaxI32" or "DsMaxRtnI32" => "Smax",
            "DsMinU32" or "DsMinRtnU32" => "Umin",
            "DsMaxU32" or "DsMaxRtnU32" => "Umax",
            "DsAndB32" or "DsAndRtnB32" => "And",
            "DsOrB32" or "DsOrRtnB32" => "Or",
            "DsXorB32" or "DsXorRtnB32" => "Xor",
            "DsWrxchgRtnB32" => "Swap",
            "DsCmpstB32" or "DsCmpstRtnB32" => "Cmpswap",
            _ => string.Empty,
        };

        private bool TryEmitGlobalDataShareAtomic(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            var name = instruction.Opcode switch
            {
                "DsMinF32" or "DsMaxF32" => "Freplace",
                _ => DataShareAtomicName(instruction.Opcode),
            };
            // DS_CMPST reads DATA0 as the comparator and DATA1 as the new value;
            // the conditional float replaces read DATA0 as the replacement value
            // and DATA1 as the operand compared against memory.
            var valueIndex = name == "Cmpswap" ? 2 : 1;
            var comparatorIndex = name == "Freplace" ? 2 : 1;
            var value = instruction.Sources.Count > valueIndex ? RawSource(instruction, valueIndex) : "0u";
            var comparator = instruction.Sources.Count > comparatorIndex ? RawSource(instruction, comparatorIndex) : "0u";
            if (name.Length == 0 || !TryGetAtomicExpression(name, value, comparator, out var build))
            {
                error = $"unsupported GDS opcode {instruction.Opcode}";
                return false;
            }

            var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
            Line($"if ({ExecCondition($"{index} < {GlobalDataShareWordsName}")})");
            Line("{");
            _indent++;
            var original = Temp("uint", build($"{GlobalDataShareName} + {index}"));
            if (instruction.Destinations.Count > 0)
            {
                Line($"v[{instruction.Destinations[0].Value}] = {original};");
            }

            _indent--;
            Line("}");
            return true;
        }

        // Append/consume on the GDS counter at M0's base: M0 must carry a size, and the word must be inside the buffer.
        private bool TryEmitGlobalDataShareCounter(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 1 || instruction.Destinations.Count < 1)
            {
                error = $"missing {instruction.Opcode} operand";
                return false;
            }

            var offset = control.SingleOffsetBytes;
            var m0 = Temp("uint", RawSource(instruction, 0));
            var baseAddress = Temp("uint", $"{m0} >> 16u");
            var sizeBytes = Temp("uint", $"{m0} & 0xFFFFu");
            var index = Temp("uint", GlobalDataShareIndex(baseAddress, offset));
            var inBounds = Temp("bool", $"{sizeBytes} != 0u && {index} < {GlobalDataShareWordsName}");
            var destination = instruction.Destinations[0].Value;
            var operation = instruction.Opcode == "DsAppend" ? "add" : "sub";
            var word = $"(device atomic_uint*)({GlobalDataShareName} + {index})";

            // Graphics stages model one logical lane, so the counter grows by one per invocation.
            if (!HasRealWaveLanes)
            {
                var original = Temp("uint", "0u");
                Line($"if ({ExecCondition(inBounds)}) {{ {original} = atomic_fetch_{operation}_explicit({word}, 1u, memory_order_relaxed); }}");
                StoreVector(destination, $"{inBounds} ? {original} : 0u");
                return true;
            }

            string count;
            string first;
            if (IsWave64)
            {
                Line("sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot(exec);");
                Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
                var low = Temp("uint", "sharpemu_wave_scratch[0]");
                var high = Temp("uint", "sharpemu_wave_scratch[1]");
                count = Temp("uint", $"popcount({low}) + popcount({high})");
                first = Temp(
                    "uint",
                    $"({low} != 0u) ? (uint)ctz({low}) : (({high} != 0u) ? (32u + (uint)ctz({high})) : 0u)");
                Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
                var atomic = $"atomic_fetch_{operation}_explicit({word}, {count}, memory_order_relaxed)";
                var broadcast = EmitWave64ReadFirstLane($"{inBounds} ? {atomic} : 0u");
                StoreVector(destination, $"{inBounds} ? {broadcast} : 0u");
                return true;
            }

            var mask = Temp("uint", "sharpemu_ballot(exec)");
            count = Temp("uint", $"popcount({mask})");
            first = Temp("uint", $"{mask} == 0u ? 0u : (uint)ctz({mask})");
            var firstValue = Temp("uint", "0u");
            Line($"if ({ExecCondition($"{inBounds} && sharpemu_lane == {first}")})");
            Line("{");
            _indent++;
            Line($"{firstValue} = atomic_fetch_{operation}_explicit({word}, {count}, memory_order_relaxed);");
            _indent--;
            Line("}");
            var result = Temp("uint", $"simd_broadcast({firstValue}, {first})");
            StoreVector(destination, $"{inBounds} ? {result} : 0u");
            return true;
        }
    }
}
