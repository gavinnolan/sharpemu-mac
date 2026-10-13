// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {

        private static bool UsesSampler(string opcode) =>
            opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
            opcode.StartsWith("ImageGather", StringComparison.Ordinal) ||
            opcode == "ImageGetLod";

        // ---- image instruction emission ----

        private bool TryEmitImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay")
            {
                error = $"{instruction.Opcode}: Metal has no guest BVH descriptor/intersection implementation";
                return false;
            }

            string texture;
            string samplerName;
            MslImageClass imageClass;
            uint dstSelect;
            string mipLevel;
            {
                if (TryGetImageElementCases(instruction, image, out var selector, out var elements, out error))
                {
                    // One case per descriptor over a constant element, like a switch on the selector.
                    for (var index = 0; index < elements.Count; index++)
                    {
                        Line($"if ({selector} == {index}u)");
                        Line("{");
                        _indent++;
                        var emitted =
                            TryResolveLayoutImage(instruction, image, out texture, out samplerName, out imageClass, out dstSelect, out mipLevel, out error, elements[index]) &&
                            EmitImageOperation(instruction, image, texture, samplerName, imageClass, dstSelect, mipLevel, out error);
                        _indent--;
                        Line("}");
                        if (!emitted)
                        {
                            return false;
                        }
                    }

                    return true;
                }

                if (error.Length != 0)
                {
                    return false;
                }

                if (!TryResolveLayoutImage(instruction, image, out texture, out samplerName, out imageClass, out dstSelect, out mipLevel, out error))
                {
                    return false;
                }
            }

            return EmitImageOperation(instruction, image, texture, samplerName, imageClass, dstSelect, mipLevel, out error);
        }

        // One image operation over a resolved texture; its results are register writes.
        private bool EmitImageOperation(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            string texture,
            string samplerName,
            MslImageClass imageClass,
            uint dstSelect,
            string mipLevel,
            out string error)
        {
            error = string.Empty;
            var kind = imageClass.ComponentKind;
            var isStorage = imageClass.IsStorage;
            if (instruction.Opcode == "ImageGetResinfo")
            {
                EmitImageGetResinfo(image, texture, imageClass);
                return true;
            }

            if (instruction.Opcode == "ImageGetLod")
            {
                EmitImageGetLod(image, texture, samplerName, imageClass);
                return true;
            }

            if (instruction.Opcode.StartsWith("ImageAtomic", StringComparison.Ordinal))
            {
                return TryEmitImageAtomic(instruction, image, texture, imageClass, out error);
            }

            if (instruction.Opcode is "ImageStore" or "ImageStoreMip")
            {
                if (!isStorage)
                {
                    error = "image store is not bound as storage";
                    return false;
                }

                // Raw signed spatial address components plus the layer, all bounds-checked
                // together so an out-of-range texel drops the write instead of wrapping.
                var spatial = new string[imageClass.SpatialComponents];
                var guards = new List<string>(4);
                for (var component = 0; component < spatial.Length; component++)
                {
                    spatial[component] = Temp("int", $"as_type<int>({ImageIntegerAddress(image, component)})");
                    guards.Add($"{spatial[component]} >= 0");
                    guards.Add($"{spatial[component]} < (int){ImageExtentQuery(texture, imageClass, component, null)}");
                }

                var layerArgument = string.Empty;
                if (imageClass.Arrayed)
                {
                    var layer = Temp("int", $"as_type<int>({ImageIntegerAddress(image, imageClass.SpatialComponents)})");
                    guards.Add($"{layer} >= 0");
                    guards.Add($"{layer} < (int){texture}.get_array_size()");
                    layerArgument = $", (uint){layer}";
                }

                var components = new string[4];
                for (var component = 0; component < 4; component++)
                {
                    var sourceIndex = Gen5ShaderTranslator.GetImageStoreSourceIndex(
                        dstSelect,
                        image.Dmask,
                        component);
                    components[component] = sourceIndex >= 0
                        ? ImageTexelComponent(
                            kind,
                            ImageStoreComponent(image, kind, (uint)sourceIndex))
                        : kind == "float" ? "0.0f" : "0";
                }

                var coordinate = spatial.Length == 1
                    ? $"uint2((uint){spatial[0]}, 0u)"
                    : $"{imageClass.IntegerCoordinateType}({string.Join(", ", spatial.Select(part => $"(uint){part}"))})";

                // Bounds-checked, EXEC-guarded write.
                Line($"if ({ExecCondition(string.Join(" && ", guards))})");
                Line("{");
                _indent++;
                Line($"{texture}.write({VectorLiteral(kind)}({components[0]}, {components[1]}, {components[2]}, {components[3]}), {coordinate}{layerArgument});");
                _indent--;
                Line("}");
                return true;
            }

            if (isStorage && instruction.Opcode is not ("ImageLoad" or "ImageLoadMip"))
            {
                error = $"unsupported storage image opcode {instruction.Opcode}";
                return false;
            }

            string sampled;
            var writeAllComponents = false;
            if (instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                // A storage element is its own mip view; multisampled textures
                // carry a sample operand rather than a mip level.
                var spatial = new string[imageClass.SpatialComponents];
                for (var component = 0; component < spatial.Length; component++)
                {
                    // Metal read returns zero outside the image. Clamping would
                    // incorrectly replace an invalid guest read with an edge texel.
                    spatial[component] = Temp("uint", ImageIntegerAddress(image, component));
                }

                var coordinate = spatial.Length == 1
                    ? $"uint2({spatial[0]}, 0u)"
                    : $"{imageClass.IntegerCoordinateType}({string.Join(", ", spatial)})";
                var arguments = coordinate;
                if (imageClass.Arrayed)
                {
                    arguments += $", {BuildIntegerLayer(image, imageClass.SpatialComponents, texture)}";
                }

                // 2DMsaa/2DMsaaArray take a sample index where a mip level would go; it is
                // the address component right after the coordinates, like the mip operand.
                if (imageClass.Multisampled)
                {
                    arguments += $", {Temp("uint", ImageIntegerAddress(image, imageClass.CoordinateComponents))}";
                }
                else if (!isStorage && imageClass.SupportsMipLevels)
                {
                    arguments += $", {mipLevel}";
                }

                sampled = Temp($"vec<{kind}, 4>", $"{texture}.read({arguments})");
            }
            else if (instruction.Opcode.StartsWith("ImageSample", StringComparison.Ordinal))
            {
                if (!TryEmitImageSample(instruction, image, texture, samplerName, imageClass, out sampled, out error))
                {
                    return false;
                }
            }
            else if (instruction.Opcode.StartsWith("ImageGather4", StringComparison.Ordinal))
            {
                if (!TryEmitImageGather(instruction, image, texture, samplerName, imageClass, out sampled, out error))
                {
                    return false;
                }

                writeAllComponents = true;
            }
            else
            {
                error = $"unsupported image opcode {instruction.Opcode}";
                return false;
            }

            var outputValues = new List<string>(4);
            for (var component = 0; component < 4; component++)
            {
                if (!writeAllComponents && (image.Dmask & (1u << component)) == 0)
                {
                    continue;
                }

                var value = $"{sampled}[{component}]";
                outputValues.Add(kind == "uint" ? value : AsUInt(value));
            }

            if (image.D16)
            {
                for (var index = 0; index < outputValues.Count; index += 2)
                {
                    var low = outputValues[index];
                    var high = index + 1 < outputValues.Count ? outputValues[index + 1] : "0u";
                    StoreVector(
                        image.VectorData + (uint)(index / 2),
                        PackImageD16(kind, low, high));
                }
            }
            else
            {
                for (var index = 0; index < outputValues.Count; index++)
                {
                    StoreVector(image.VectorData + (uint)index, outputValues[index]);
                }
            }

            return true;
        }

        // The size query reports the spatial extent, then the layer count for an arrayed
        // image, then the mip count in w. Components past the image's own rank read zero,
        // mirroring the SPIR-V backend's OpImageQuerySize{Lod} handling.
        private void EmitImageGetResinfo(Gen5ImageControl image, string texture, MslImageClass imageClass)
        {
            // Storage elements and multisampled textures have no queryable mip chain.
            var queryable = !imageClass.IsStorage && !imageClass.Multisampled;
            var lod = queryable ? Temp("uint", ImageIntegerAddress(image, 0)) : null;
            var sizes = new string[imageClass.CoordinateComponents];
            for (var component = 0; component < imageClass.SpatialComponents; component++)
            {
                sizes[component] = Temp("uint", ImageExtentQuery(texture, imageClass, component, lod));
            }

            if (imageClass.Arrayed)
            {
                sizes[imageClass.SpatialComponents] = Temp("uint", $"{texture}.get_array_size()");
            }

            var levels = (image.Dmask & 0x8) != 0 && queryable
                ? Temp("uint", $"{texture}.get_num_mip_levels()")
                : "1u";
            uint outputIndex = 0;
            for (var component = 0; component < 4; component++)
            {
                if ((image.Dmask & (1u << component)) == 0)
                {
                    continue;
                }

                StoreVector(
                    image.VectorData + outputIndex++,
                    component < sizes.Length ? sizes[component]
                        : component == 3 ? levels
                        : "0u");
            }
        }

        // IMAGE_ATOMIC_* over a read_write storage texture. Metal's texture atomics
        // (MSL 3.1) take and return the whole texel as a 4-vector, and the guest
        // binding for an atomic image is always single-channel uint, so only x
        // carries data. The ops Metal has no member for -- the signed minimum and
        // maximum over a uint texture, the wrap-clamping increment and decrement,
        // compare-swap and the float forms -- read the texel, derive the
        // replacement and compare-exchange until the texel they computed from is
        // still the one in memory.
        private bool TryEmitImageAtomic(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            string texture,
            MslImageClass imageClass,
            out string error)
        {
            error = string.Empty;
            var opcode = instruction.Opcode;
            if (!imageClass.IsStorage || imageClass.Multisampled)
            {
                error = $"{opcode} needs a single-sampled storage image binding";
                return false;
            }

            // BindingLayout only gives an atomic binding to a uint image, so this
            // is the one component kind the texel vector can have.
            if (imageClass.ComponentKind != "uint")
            {
                error = $"{opcode} on a {imageClass.ComponentKind} image has no Metal texture atomic";
                return false;
            }

            // The address is bounds-checked like a store: Metal leaves an atomic on
            // an out-of-range texel undefined, so an invalid guest address drops.
            var spatial = new string[imageClass.SpatialComponents];
            var guards = new List<string>(4);
            for (var component = 0; component < spatial.Length; component++)
            {
                spatial[component] = Temp("int", $"as_type<int>({ImageIntegerAddress(image, component)})");
                guards.Add($"{spatial[component]} >= 0");
                guards.Add($"{spatial[component]} < (int){ImageExtentQuery(texture, imageClass, component, null)}");
            }

            var layerArgument = string.Empty;
            if (imageClass.Arrayed)
            {
                var layer = Temp("int", $"as_type<int>({ImageIntegerAddress(image, imageClass.SpatialComponents)})");
                guards.Add($"{layer} >= 0");
                guards.Add($"{layer} < (int){texture}.get_array_size()");
                layerArgument = $", (uint){layer}";
            }

            var coordinate = spatial.Length == 1
                ? $"uint2((uint){spatial[0]}, 0u)"
                : $"{imageClass.IntegerCoordinateType}({string.Join(", ", spatial.Select(part => $"(uint){part}"))})";
            var data = Temp("uint", $"v[{image.VectorData}]");
            // IMAGE_ATOMIC_CMPSWAP and FCMPSWAP read the new value from VDATA and
            // the comparator from the register after it. VDATA is an 8-bit field,
            // so the pair can name one register past the file.
            var comparator = Temp(
                "uint",
                image.VectorData + 1 < VectorRegisterFileCount ? $"v[{image.VectorData + 1}]" : "0u");
            var access = $"{coordinate}{layerArgument}";

            Line($"if ({ExecCondition(string.Join(" && ", guards))})");
            Line("{");
            _indent++;
            var suffix = opcode["ImageAtomic".Length..];
            var direct = suffix switch
            {
                "Swap" => $"{texture}.atomic_exchange({access}, uint4({data}))",
                "Add" => $"{texture}.atomic_fetch_add({access}, uint4({data}))",
                "Sub" => $"{texture}.atomic_fetch_sub({access}, uint4({data}))",
                "Umin" => $"{texture}.atomic_fetch_min({access}, uint4({data}))",
                "Umax" => $"{texture}.atomic_fetch_max({access}, uint4({data}))",
                "And" => $"{texture}.atomic_fetch_and({access}, uint4({data}))",
                "Or" => $"{texture}.atomic_fetch_or({access}, uint4({data}))",
                "Xor" => $"{texture}.atomic_fetch_xor({access}, uint4({data}))",
                _ => string.Empty,
            };

            string original;
            if (direct.Length > 0)
            {
                original = Temp("uint", $"{direct}.x");
            }
            else
            {
                var replacement = suffix switch
                {
                    "Smin" => (Func<string, string>)(value => $"(uint)min(as_type<int>({value}), as_type<int>({data}))"),
                    "Smax" => value => $"(uint)max(as_type<int>({value}), as_type<int>({data}))",
                    // IMAGE_ATOMIC_INC/DEC wrap against DATA rather than counting
                    // freely: INC resets at the limit, DEC reloads it.
                    "Inc" => value => $"{value} >= {data} ? 0u : {value} + 1u",
                    "Dec" => value => $"({value} == 0u || {value} > {data}) ? {data} : {value} - 1u",
                    "Cmpswap" => value => $"{value} == {comparator} ? {data} : {value}",
                    "Fcmpswap" => value =>
                        $"as_type<float>({value}) == as_type<float>({comparator}) ? {data} : {value}",
                    "Fmin" => value =>
                        $"as_type<float>({data}) < as_type<float>({value}) ? {data} : {value}",
                    "Fmax" => value =>
                        $"as_type<float>({data}) > as_type<float>({value}) ? {data} : {value}",
                    _ => null!,
                };
                if (replacement is null)
                {
                    _indent--;
                    Line("}");
                    error = $"unsupported image atomic {opcode}";
                    return false;
                }

                original = EmitImageAtomicExchangeLoop(texture, access, replacement);
            }

            if (image.Glc)
            {
                Line($"v[{image.VectorData}] = {original};");
            }

            _indent--;
            Line("}");
            return true;
        }

        // Reads the texel, derives the replacement from what it observed and swaps
        // it in, retrying until the texel it computed from is still in memory. A
        // failed exchange refreshes the observed value, so the replacement is
        // always derived from the texel the swap is compared against.
        private string EmitImageAtomicExchangeLoop(
            string texture,
            string access,
            Func<string, string> replacement)
        {
            var observed = Temp("uint4", $"{texture}.atomic_load({access})");
            var original = Temp("uint", $"{observed}.x");
            var exchanged = Temp("bool", "false");
            Line($"while (!{exchanged})");
            Line("{");
            _indent++;
            Line($"{original} = {observed}.x;");
            var next = Temp("uint", replacement(original));
            Line($"{exchanged} = {texture}.atomic_compare_exchange_weak({access}, &{observed}, uint4({next}));");
            _indent--;
            Line("}");
            return original;
        }

        // IMAGE_GET_LOD reports the level the sampler would pick for the address:
        // the clamped level in x and the unclamped one in y, which is what the
        // SPIR-V translator's OpImageQueryLod returns. A multisampled texture has
        // no mip chain to pick from, so both halves read zero.
        private void EmitImageGetLod(
            Gen5ImageControl image,
            string texture,
            string samplerName,
            MslImageClass imageClass)
        {
            var levels = new string[2];
            if (imageClass.SupportsSampleLod)
            {
                var coordinates = BuildFloatSpatial(image, 0, imageClass);
                if (imageClass.SpatialComponents == 1)
                {
                    coordinates = Temp("float2", $"float2({coordinates}, 0.5f)");
                }

                levels[0] = Temp("float", $"{texture}.calculate_clamped_lod({samplerName}, {coordinates})");
                levels[1] = Temp("float", $"{texture}.calculate_unclamped_lod({samplerName}, {coordinates})");
            }
            else
            {
                levels[0] = "0.0f";
                levels[1] = "0.0f";
            }

            // A zero DMASK still returns the clamped level, like the size query.
            var mask = image.Dmask != 0 ? image.Dmask : 1u;
            uint outputIndex = 0;
            for (var component = 0; component < 2; component++)
            {
                if ((mask & (1u << component)) == 0)
                {
                    continue;
                }

                StoreVector(image.VectorData + outputIndex++, AsUInt(levels[component]));
            }
        }

        // The extent accessor uses the guest's logical rank. Height-one 2D
        // backing preserves 1D mip queries; multisampled textures take no LOD.
        private static string ImageExtentQuery(string texture, MslImageClass imageClass, int component, string? lod)
        {
            var accessor = component switch
            {
                0 => "get_width",
                1 => "get_height",
                _ => "get_depth",
            };
            return lod is null || !imageClass.SupportsMipLevels
                ? $"{texture}.{accessor}()"
                : $"{texture}.{accessor}({lod})";
        }

        // One spatial float coordinate vector from consecutive address components.
        //
        // Guest cube bodies carry face-relative s/t that the SPIR-V backend biases by -1
        // before sampling; that bias is mirrored here so both backends address cube faces
        // identically. Derivatives have no coordinate origin and must not take this bias.
        private string BuildFloatSpatial(Gen5ImageControl image, int start, MslImageClass imageClass, bool biasCube = true)
        {
            var count = imageClass.SpatialComponents;
            var parts = new string[count];
            for (var component = 0; component < count; component++)
            {
                var value = ImageFloatAddress(image, start + component);
                parts[component] = biasCube && imageClass.Cube && component < 2 ? $"({value}) - 1.0f" : value;
            }

            return count == 1
                ? Temp("float", parts[0])
                : Temp(imageClass.FloatCoordinateType, $"{imageClass.FloatCoordinateType}({string.Join(", ", parts)})");
        }

        // The array layer of a sampled access. Hardware rounds the float layer to the
        // nearest slice, and Metal wants an integer, so the rounding is explicit.
        //
        // Guest cube layers advance by 8 per cube (6 faces plus 2 padding slices) while
        // the host 2D-array view packs 6, so the padding is subtracted. The float-to-integer
        // conversion truncates there, matching the SPIR-V backend's OpConvertFToU.
        private string BuildSampleLayer(Gen5ImageControl image, int component, MslImageClass imageClass, string texture)
        {
            var raw = ImageFloatAddress(image, component);
            string layer;
            if (imageClass.Cube)
            {
                var guest = Temp("uint", $"(uint)max({raw}, 0.0f)");
                layer = Temp("uint", $"{guest} - (({guest} >> 3u) << 1u)");
            }
            else
            {
                layer = Temp("uint", $"(uint)max(rint({raw}), 0.0f)");
            }

            return Temp("uint", $"min({layer}, {texture}.get_array_size() - 1u)");
        }

        // Integer layer accesses retain invalid values so the native read returns
        // zero rather than reading the nearest layer. Cube layers are raw here.
        private string BuildIntegerLayer(Gen5ImageControl image, int component, string texture) =>
            Temp("uint", ImageIntegerAddress(image, component));

        private bool TryEmitImageSample(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            string texture,
            string samplerName,
            MslImageClass imageClass,
            out string sampled,
            out string error)
        {
            sampled = string.Empty;
            error = string.Empty;
            var kind = imageClass.ComponentKind;
            var opcode = instruction.Opcode;
            var hasOffset = opcode.EndsWith("O", StringComparison.Ordinal);
            // SAMPLE_CD is coarse derivatives and SAMPLE_CL is a LOD clamp, not
            // comparisons: their "Cd" and "Cl" are single tokens, so the C in them
            // must not read as the z-compare marker the way the C in SAMPLE_C_CD
            // and SAMPLE_C_L (named CL, with a capital L) does. A false compare
            // also consumes an address slot, which shifts every coordinate.
            var hasCompare = opcode.Contains("SampleC", StringComparison.Ordinal) &&
                !opcode.StartsWith("ImageSampleCd", StringComparison.Ordinal) &&
                !opcode.StartsWith("ImageSampleCl", StringComparison.Ordinal);
            // The C comparison marker precedes D/L/B. Decode the remaining
            // family so CL reads its trailing LOD and CD consumes derivatives.
            var sampleFamily = hasCompare ? opcode.Replace("SampleC", "Sample", StringComparison.Ordinal) : opcode;
            var hasGradients = sampleFamily.Contains("SampleD", StringComparison.Ordinal) ||
                opcode.Contains("SampleCd", StringComparison.Ordinal) ||
                opcode.Contains("SampleCCd", StringComparison.Ordinal);
            var hasZeroLod = opcode.Contains("Lz", StringComparison.Ordinal);
            var hasLod = !hasZeroLod && sampleFamily.Contains("SampleL", StringComparison.Ordinal);
            var hasBias = sampleFamily.Contains("SampleB", StringComparison.Ordinal);
            var spatialComponents = imageClass.SpatialComponents;

            // RDNA MIMG address operands are ordered
            // {offset}{bias}{z-compare}{derivatives}{body}; SAMPLE_L carries LOD
            // as the final body component instead.
            var addressCursor = 0;
            var offsets = new string[spatialComponents];
            Array.Fill(offsets, "0");
            if (hasOffset)
            {
                addressCursor = AlignFullImageAddress(image, addressCursor);
                var packed = Temp(
                    "int",
                    $"as_type<int>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");

                // One signed 6-bit field per spatial axis: x at bit 0, y at 8, z at 16.
                for (var component = 0; component < spatialComponents; component++)
                {
                    offsets[component] = Temp("int", $"extract_bits({packed}, {component * 8}u, 6u)");
                }

                addressCursor += ImageFullAddressSlots(image);
            }

            var bias = hasBias ? Temp("float", ImageFloatAddress(image, addressCursor++)) : "0.0f";
            var reference = "0.0f";
            if (hasCompare)
            {
                addressCursor = AlignFullImageAddress(image, addressCursor);
                reference = Temp(
                    "float",
                    $"as_type<float>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");
                addressCursor += ImageFullAddressSlots(image);
            }

            // Derivatives are one spatial vector per axis pair, with no array layer.
            var gradientType = spatialComponents == 3 ? "gradient3d" : "gradient2d";
            var gradientX = string.Empty;
            var gradientY = string.Empty;
            if (hasGradients)
            {
                gradientX = BuildFloatSpatial(image, addressCursor, imageClass, biasCube: false);
                gradientY = BuildFloatSpatial(image, addressCursor + spatialComponents, imageClass, biasCube: false);
                addressCursor += spatialComponents * 2;
            }

            var coordinates = BuildFloatSpatial(image, addressCursor, imageClass);
            var layerArgument = imageClass.Arrayed
                ? $", {BuildSampleLayer(image, addressCursor + spatialComponents, imageClass, texture)}"
                : string.Empty;
            // Implicit-LOD and bias sampling need the derivatives a fragment quad provides,
            // so Metal only offers them in a fragment function. Outside one the level is
            // explicit, which is what the SPIR-V backend does for the same reason.
            var explicitLod = hasGradients || hasZeroLod || hasLod || _stage != Gen5MslStage.Pixel;
            var lod = hasZeroLod
                ? "0.0f"
                : hasLod
                    ? Temp("float", ImageFloatAddress(image, addressCursor + imageClass.CoordinateComponents))
                    : explicitLod
                        ? "0.0f"
                        : bias;
            if (hasOffset)
            {
                // Per-lane texel offsets fold into normalized coordinates using
                // the selected mip extent, mirroring the SPIR-V translator
                // (Metal sample offsets must be compile-time constants).
                var offsetLod = explicitLod && !hasGradients ? lod : "0.0f";
                var mipLevel = Temp("uint", $"(uint)max((int)({offsetLod}), 0)");
                var scaled = new string[spatialComponents];
                for (var component = 0; component < spatialComponents; component++)
                {
                    scaled[component] =
                        $"(float){offsets[component]} / (float){ImageExtentQuery(texture, imageClass, component, mipLevel)}";
                }

                coordinates = Temp(
                    imageClass.FloatCoordinateType,
                    spatialComponents == 1
                        ? $"{coordinates} + {scaled[0]}"
                        : $"{coordinates} + {imageClass.FloatCoordinateType}({string.Join(", ", scaled)})");
            }

            if (spatialComponents == 1)
            {
                coordinates = Temp("float2", $"float2({coordinates}, 0.5f)");
                if (hasGradients)
                {
                    gradientX = $"float2({gradientX}, 0.0f)";
                    gradientY = $"float2({gradientY}, 0.0f)";
                }
            }

            var samplerArguments = !imageClass.SupportsSampleLod
                ? string.Empty
                : hasGradients
                    ? $", metal::{gradientType}({gradientX}, {gradientY})"
                    : explicitLod
                        ? $", metal::level({lod})"
                        : hasBias
                            ? $", metal::bias({bias})"
                            : string.Empty;
            if (imageClass.DepthCompare)
            {
                // Native depth samplers compare each contributing texel before
                // filtering. Comparing an already filtered color value loses PCF.
                var comparison = hasCompare
                    ? Temp("float", $"{texture}.sample_compare({samplerName}, {coordinates}{layerArgument}, {reference}{samplerArguments})")
                    : Temp("float", $"{texture}.sample({samplerName}, {coordinates}{layerArgument}{samplerArguments})");
                sampled = Temp("float4", hasCompare
                    ? $"float4({comparison}, {comparison}, {comparison}, 1.0f)"
                    : $"float4({comparison}, 0.0f, 0.0f, 1.0f)");
                return true;
            }

            sampled = Temp(
                $"vec<{kind}, 4>",
                $"{texture}.sample({samplerName}, {coordinates}{layerArgument}{samplerArguments})");
            if (hasCompare)
            {
                // Existing non-depth-equivalent color comparison path.
                var passes = Temp("bool", $"{reference} <= (float){sampled}[0]");
                var one = kind == "float" ? "1.0f" : "1";
                var zero = kind == "float" ? "0.0f" : "0";
                sampled = Temp(
                    $"vec<{kind}, 4>",
                    $"{VectorLiteral(kind)}({passes} ? {one} : {zero}, {passes} ? {one} : {zero}, {passes} ? {one} : {zero}, {one})");
            }

            return true;
        }

        private bool TryEmitImageGather(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            string texture,
            string samplerName,
            MslImageClass imageClass,
            out string sampled,
            out string error)
        {
            sampled = string.Empty;
            error = string.Empty;
            var kind = imageClass.ComponentKind;
            var opcode = instruction.Opcode;
            var hasOffset = opcode.EndsWith("O", StringComparison.Ordinal);
            var hasCompare = opcode.Contains("Gather4C", StringComparison.Ordinal);
            if (opcode == "ImageGather4H")
            {
                if (imageClass.Dimension is ImageDimension.Dim1D or ImageDimension.Dim1DArray)
                {
                    error = "unsupported 1D horizontal image gather";
                    return false;
                }

                sampled = EmitHorizontalGather(image, texture, samplerName, imageClass);
                return true;
            }

            // Metal exposes gather on 2D and 2D-array textures only. A 1D gather is
            // emulated from two point samples, the way the SPIR-V backend does it;
            // a volume gather has no hardware or Vulkan equivalent either.
            if (!imageClass.SupportsGather)
            {
                if (imageClass.Dimension == ImageDimension.Dim1D &&
                    opcode == "ImageGather4Lz" &&
                    !hasCompare &&
                    !hasOffset)
                {
                    sampled = EmitOneDimensionalGatherLz(image, texture, samplerName, imageClass);
                    return true;
                }

                error = $"image dimension {imageClass.Dimension} does not support {opcode} on Metal";
                return false;
            }

            var addressCursor = 0;
            var offset = "int2(0)";
            if (hasOffset)
            {
                var packed = Temp(
                    "int",
                    $"as_type<int>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");
                offset = Temp(
                    "int2",
                    $"int2(extract_bits({packed}, 0u, 6u), extract_bits({packed}, 8u, 6u))");
                addressCursor += ImageFullAddressSlots(image);
            }

            var reference = "0.0f";
            if (hasCompare)
            {
                addressCursor = AlignFullImageAddress(image, addressCursor);
                reference = Temp(
                    "float",
                    $"as_type<float>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");
                addressCursor += ImageFullAddressSlots(image);
            }

            var coordinates = BuildFloatSpatial(image, addressCursor, imageClass);
            var layerArgument = imageClass.Arrayed
                ? $", {BuildSampleLayer(image, addressCursor + imageClass.SpatialComponents, imageClass, texture)}"
                : string.Empty;

            // The gathered component is selected from the first dmask bit.
            uint component = 0;
            while (component < 3 && (image.Dmask & (1u << (int)component)) == 0)
            {
                component++;
            }

            var componentName = hasCompare ? "x" : component switch
            {
                0 => "x",
                1 => "y",
                2 => "z",
                _ => "w",
            };
            if (imageClass.DepthCompare && hasCompare)
            {
                sampled = Temp("float4",
                    $"{texture}.gather_compare({samplerName}, {coordinates}{layerArgument}, {reference}, {offset})");
                return true;
            }
            sampled = Temp(
                $"vec<{kind}, 4>",
                $"{texture}.gather({samplerName}, {coordinates}{layerArgument}, {offset}, component::{componentName})");
            if (hasCompare)
            {
                var one = kind == "float" ? "1.0f" : "1";
                var zero = kind == "float" ? "0.0f" : "0";
                var compared = Temp(
                    $"vec<{kind}, 4>",
                    $"{VectorLiteral(kind)}(" +
                    $"{reference} <= (float){sampled}[0] ? {one} : {zero}, " +
                    $"{reference} <= (float){sampled}[1] ? {one} : {zero}, " +
                    $"{reference} <= (float){sampled}[2] ? {one} : {zero}, " +
                    $"{reference} <= (float){sampled}[3] ? {one} : {zero})");
                sampled = compared;
            }

            return true;
        }

        // GATHER4_H returns four horizontally adjacent texels rather than a 2x2
        // footprint, which the SPIR-V translator expresses as a gather with the
        // constant offsets {-1,0}, {0,0}, {1,0} and {2,0}. Metal's gather takes a
        // single offset, so each of the four texels is point-sampled at the
        // footprint corner the gather would have taken it from.
        private string EmitHorizontalGather(
            Gen5ImageControl image,
            string texture,
            string samplerName,
            MslImageClass imageClass)
        {
            var kind = imageClass.ComponentKind;
            var size = Temp("float2", $"float2({texture}.get_width(0), {texture}.get_height(0))");
            var coordinates = BuildFloatSpatial(image, 0, imageClass);
            var layerArgument = imageClass.Arrayed
                ? $", {BuildSampleLayer(image, imageClass.SpatialComponents, imageClass, texture)}"
                : string.Empty;
            // The gather's own corner texel: the lower-left of the 2x2 footprint.
            var corner = Temp("float2", $"floor({coordinates} * {size} - 0.5f) + 0.5f");

            // The gathered component is selected from the first dmask bit.
            uint component = 0;
            while (component < 3 && (image.Dmask & (1u << (int)component)) == 0)
            {
                component++;
            }

            var texels = new string[4];
            for (var texel = 0; texel < texels.Length; texel++)
            {
                var sample = Temp(
                    $"vec<{kind}, 4>",
                    $"{texture}.sample({samplerName}, ({corner} + float2({texel - 1}.0f, 0.0f)) / {size}" +
                    $"{layerArgument}, metal::level(0.0f))");
                texels[texel] = $"{sample}[{component}]";
            }

            return Temp($"vec<{kind}, 4>", $"{VectorLiteral(kind)}({string.Join(", ", texels)})");
        }

        // A 1D gather has no Metal (or Vulkan) instruction, so the two texels the filter
        // would straddle are point-sampled and laid out the way GATHER4 returns them:
        // (v0, v1, v1, v0). Mirrors Gen5SpirvTranslator's EmitOneDimensionalGatherLz.
        private string EmitOneDimensionalGatherLz(
            Gen5ImageControl image,
            string texture,
            string samplerName,
            MslImageClass imageClass)
        {
            var kind = imageClass.ComponentKind;
            var width = Temp("float", $"(float){texture}.get_width(0)");
            var coordinate = Temp("float", ImageFloatAddress(image, 0));
            var left = Temp("float", $"floor({coordinate} * {width} - 0.5f)");

            // The gathered component is selected from the first dmask bit.
            uint component = 0;
            while (component < 3 && (image.Dmask & (1u << (int)component)) == 0)
            {
                component++;
            }

            var first = Temp(
                $"vec<{kind}, 4>",
                $"{texture}.sample({samplerName}, float2(({left} + 0.5f) / {width}, 0.5f), metal::level(0.0f))");
            var second = Temp(
                $"vec<{kind}, 4>",
                $"{texture}.sample({samplerName}, float2(({left} + 1.5f) / {width}, 0.5f), metal::level(0.0f))");
            return Temp(
                $"vec<{kind}, 4>",
                $"{VectorLiteral(kind)}({first}[{component}], {second}[{component}], " +
                $"{second}[{component}], {first}[{component}])");
        }

        private static string VectorLiteral(string kind) => $"vec<{kind}, 4>";

        private static int ImageAddressRegister(Gen5ImageControl image, int component) =>
            image.A16 ? component / 2 : component;

        private static int ImageFullAddressSlots(Gen5ImageControl image) =>
            image.A16 ? 2 : 1;

        private static int AlignFullImageAddress(Gen5ImageControl image, int component) =>
            image.A16 ? (component + 1) & ~1 : component;

        /// <summary>Float address component, unpacking A16 half pairs.</summary>
        private string ImageFloatAddress(Gen5ImageControl image, int component)
        {
            var register = image.GetAddressRegister(ImageAddressRegister(image, component));
            return image.A16
                ? $"(float)as_type<half2>(v[{register}])[{component & 1}]"
                : $"as_type<float>(v[{register}])";
        }

        /// <summary>Integer address component, unpacking A16 16-bit pairs.</summary>
        private string ImageIntegerAddress(Gen5ImageControl image, int component)
        {
            var register = image.GetAddressRegister(ImageAddressRegister(image, component));
            return image.A16
                ? $"((v[{register}] >> {(component & 1) * 16}) & 0xFFFFu)"
                : $"v[{register}]";
        }

        /// <summary>One store-source component, unpacking D16 halves.</summary>
        private string ImageStoreComponent(Gen5ImageControl image, string kind, uint component)
        {
            if (!image.D16)
            {
                return $"v[{image.VectorData + component}]";
            }

            var packed = $"v[{image.VectorData + (component / 2)}]";
            if (kind == "float")
            {
                return AsUInt($"(float)as_type<half2>({packed})[{component & 1}]");
            }

            var low = $"(({packed} >> {(component & 1) * 16}) & 0xFFFFu)";
            return kind == "int"
                ? $"(uint)extract_bits(as_type<int>({low}), 0u, 16u)"
                : low;
        }

        private static string ImageTexelComponent(string kind, string raw) => kind switch
        {
            "int" => $"as_type<int>({raw})",
            "uint" => raw,
            _ => $"as_type<float>({raw})",
        };

        private string PackImageD16(string kind, string low, string high)
        {
            if (kind == "float")
            {
                return $"(((uint)as_type<ushort>(half(as_type<float>({low})))) | (((uint)as_type<ushort>(half(as_type<float>({high})))) << 16))";
            }

            return $"((({low}) & 0xFFFFu) | ((({high}) & 0xFFFFu) << 16))";
        }

        // ---- exports ----

        private bool TryEmitExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 4)
            {
                error = "missing export sources";
                return false;
            }

            if (_stage == Gen5MslStage.Mesh)
            {
                return TryEmitMeshExport(instruction, export);
            }

            if (_stage == Gen5MslStage.Vertex)
            {
                return TryEmitVertexExport(instruction, export);
            }

            if (_stage != Gen5MslStage.Pixel)
            {
                // Compute programs have no export interface.
                return true;
            }

            // EXP.VM communicates the current EXEC mask independently of the
            // export data target. NULL exports are the hardware-defined way to
            // update only fragment validity, and the final VM export wins.
            if (export.ValidMask && _usesPixelValidMask)
            {
                Line("pixel_valid_mask_active = exec;");
            }

            if (export.Target == MrtzExportTarget)
            {
                if (_pixelDepthExport && !export.Compressed && (export.EnableMask & 1u) != 0)
                {
                    const string depth = "sharpemu_out.sharpemu_depth";
                    Line($"{depth} = {ExecSelect($"as_type<float>(v[{instruction.Sources[0].Value}])", depth)};");
                }

                return true;
            }

            Gen5PixelOutputBinding? binding = null;
            foreach (var candidate in _pixelOutputBindings)
            {
                if (candidate.ExportTarget == export.Target)
                {
                    binding = candidate;
                    break;
                }
            }

            if (binding is null)
            {
                return true;
            }

            var field = $"sharpemu_out.mrt{binding.Value.GuestSlot}";
            var componentType = binding.Value.Kind switch
            {
                Gen5PixelOutputKind.Uint => "uint",
                Gen5PixelOutputKind.Sint => "int",
                _ => "float",
            };
            var values = new string[4];
            for (var component = 0; component < 4; component++)
            {
                if ((export.EnableMask & (1u << component)) == 0)
                {
                    var outputComponent = (uint)component;
                    if (!binding.Value.ComponentMapping.IsIdentity)
                    {
                        for (var physicalComponent = 0u; physicalComponent < 4; physicalComponent++)
                        {
                            if (binding.Value.ComponentMapping.Map(physicalComponent) == component)
                            {
                                outputComponent = physicalComponent;
                                break;
                            }
                        }
                    }

                    values[component] = $"{field}[{outputComponent}]";
                    continue;
                }

                if (export.Compressed)
                {
                    var packed = $"v[{instruction.Sources[component >> 1].Value}]";
                    var half = $"(float)as_type<half2>({packed})[{component & 1}]";
                    values[component] = binding.Value.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => $"uint(as_type<ushort2>({packed})[{component & 1}])",
                        Gen5PixelOutputKind.Sint => $"int(as_type<short2>({packed})[{component & 1}])",
                        _ => half,
                    };
                    continue;
                }

                var raw = $"v[{instruction.Sources[component].Value}]";
                values[component] = binding.Value.Kind switch
                {
                    Gen5PixelOutputKind.Uint => raw,
                    Gen5PixelOutputKind.Sint => $"as_type<int>({raw})",
                    _ => $"as_type<float>({raw})",
                };
            }

            if (binding.Value.ComponentMapping != Gen5ColorComponentMapping.Identity)
            {
                var mappedValues = new string[4];
                for (var component = 0; component < mappedValues.Length; component++)
                {
                    mappedValues[component] = values[
                        binding.Value.ComponentMapping.Map((uint)component)];
                }

                values = mappedValues;
            }

            // A lane removed from EXEC keeps the previous output value; killed
            // fragments are discarded in the epilogue.
            Line($"{field} = {ExecSelect($"vec<{componentType}, 4>({values[0]}, {values[1]}, {values[2]}, {values[3]})", field)};");
            return true;
        }

        private bool TryEmitVertexExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            // Target 12 is POS0; 13..15 are POS1..POS3 (point size, layer, viewport
            // index, clip and cull distances); 32..63 are the param outputs. MRTZ is
            // ignored like the SPIR-V side.
            if (export.Target is >= 13 and < 16)
            {
                EmitAuxPositionExport(instruction, export);
                return true;
            }

            string field;
            if (export.Target == 12)
            {
                field = "sharpemu_out.sharpemu_position";
            }
            else if (export.Target is >= 32 and < 64 &&
                     _vertexOutputs.Contains(export.Target - 32))
            {
                field = $"sharpemu_out.param{export.Target - 32}";
            }
            else
            {
                return true;
            }

            var values = new string[4];
            for (var component = 0; component < 4; component++)
            {
                if ((export.EnableMask & (1u << component)) == 0)
                {
                    values[component] = component == 3 ? "1.0f" : "0.0f";
                    continue;
                }

                if (export.Compressed)
                {
                    var packed = $"v[{instruction.Sources[component >> 1].Value}]";
                    values[component] = $"(float)as_type<half2>({packed})[{component & 1}]";
                    continue;
                }

                values[component] = $"as_type<float>(v[{instruction.Sources[component].Value}])";
            }

            var exported = $"float4({values[0]}, {values[1]}, {values[2]}, {values[3]})";
            if (export.Target == 12)
            {
                exported = ToHostClipSpace(exported);
            }

            Line($"{field} = {ExecSelect(exported, field)};");
            if (export.Target == 12)
            {
                EmitInvalidPositionDistance();
            }

            return true;
        }

        /// <summary>
        /// Vertex attribute fetch: the evaluator captured this buffer load as a
        /// fixed-function vertex input, so read the stage_in field instead of
        /// guest memory (bound via MTLVertexDescriptor by the backend).
        /// </summary>
        private bool TryEmitVertexInputFetch(
            Gen5BufferMemoryControl control,
            ShaderVertexInput input,
            out string error)
        {
            error = string.Empty;
            if (control.DwordCount == 0)
            {
                error =
                    $"invalid vertex input fetch components={control.DwordCount}";
                return false;
            }

            for (uint component = 0; component < control.DwordCount; component++)
            {
                var selector = (input.DestinationSelect >> (int)(component * 3)) & 0x7u;
                if (selector == 0)
                {
                    StoreVector(control.VectorData + component, "0u");
                }
                else if (selector == 1)
                {
                    StoreVector(
                        control.VectorData + component,
                        input.NumberFormat is 4u or 5u ? "1u" : "0x3f800000u");
                }
                else if (selector is >= 4u and <= 7u)
                {
                    var sourceComponent = selector - 4u;
                    if (sourceComponent >= input.ComponentCount)
                    {
                        error =
                            $"vertex input destination selector exceeds source components: selector={selector} components={input.ComponentCount}";
                        return false;
                    }

                    var value = input.ComponentCount == 1
                        ? $"sharpemu_vin.in{input.Location}"
                        : $"sharpemu_vin.in{input.Location}[{sourceComponent}]";
                    StoreVector(control.VectorData + component, AsUInt(value));
                }
                else
                {
                    error = $"unsupported vertex input destination selector={selector}";
                    return false;
                }
            }

            return true;
        }

        // ---- interpolation / pixel inputs ----

        // V_INTERP_MOV's selector picks one of the triangle's parameters: P0 is the
        // provoking vertex's value and P10/P20 the two deltas from it. Reading P0
        // alone is exactly what a flat input gives, so an attribute read only that
        // way is declared flat. The deltas need the three vertex values, which
        // vertex_value inputs give (see Gen5MslTranslator.Interpolation.cs); a host
        // without them keeps ordinary interpolation for the remaining attributes --
        // the same fallback the SPIR-V translator takes on a device without
        // fragmentShaderBarycentric.
        private bool ReadsOnlyTheProvokingVertex(uint attribute)
        {
            var read = false;
            foreach (var instruction in _request.Program.Instructions)
            {
                if (instruction.Control is not Gen5InterpolationControl control ||
                    control.Attribute != attribute)
                {
                    continue;
                }

                if (instruction.Opcode != "VInterpMovF32" ||
                    (instruction.Words[0] & 0xFFu) != ProvokingVertexParameter)
                {
                    return false;
                }

                read = true;
            }

            return read;
        }

        private const uint ProvokingVertexParameter = 2;

        private bool TryEmitInterpolation(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5MslStage.Pixel ||
                !_pixelAttributes.Contains(interpolation.Attribute) ||
                instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister)
            {
                error = "invalid interpolated attribute";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            if (_perVertexAttributes.Contains(interpolation.Attribute))
            {
                return TryEmitInterpolationParameter(instruction, interpolation, destination, out error);
            }

            if (_perVertexSourcedAttributes.TryGetValue(interpolation.Attribute, out var flatSource))
            {
                EmitPerVertexSourcedInterpolation(interpolation, destination, flatSource);
                return true;
            }

            StoreVector(
                destination,
                AsUInt($"{PixelInputField(interpolation.Attribute)}[{interpolation.Channel}]"));
            return true;
        }

        /// <summary>
        /// Seeds pixel input VGPRs in SPI_PS_INPUT_ADDR compact order: the
        /// interpolation slots reserve registers even though V_INTERP reads MSL
        /// varyings directly (they carry the barycentrics when the program
        /// interpolates per-vertex inputs by hand), and the position and system
        /// inputs land in the hardware-selected VGPRs.
        /// </summary>
        private void EmitPixelInputState(StringBuilder source)
        {
            uint vgpr = 0;

            void Advance(int bit, uint dwordCount)
            {
                if ((_pixelInputAddress & (1u << bit)) == 0)
                {
                    return;
                }

                if (BarycentricInputRegisters(bit) is { } registers)
                {
                    for (var index = 0; index < registers.Length; index++)
                    {
                        source.AppendLine($"    v[{vgpr + (uint)index}] = as_type<uint>({registers[index]});");
                    }
                }

                vgpr += dwordCount;
            }

            void Input(int bit, string? value)
            {
                var mask = 1u << bit;
                if ((_pixelInputAddress & mask) == 0)
                {
                    return;
                }

                if ((_pixelInputEnable & mask) != 0 && value is not null)
                {
                    source.AppendLine($"    v[{vgpr}] = {value};");
                }

                vgpr++;
            }

            const string fragCoord = "sharpemu_in.sharpemu_frag_coord";
            Advance(0, 2);  // PERSP_SAMPLE
            Advance(1, 2);  // PERSP_CENTER
            Advance(2, 2);  // PERSP_CENTROID
            Advance(3, 3);  // PERSP_PULL_MODEL
            Advance(4, 2);  // LINEAR_SAMPLE
            Advance(5, 2);  // LINEAR_CENTER
            Advance(6, 2);  // LINEAR_CENTROID
            Advance(7, 1);  // LINE_STIPPLE
            Input(8, $"as_type<uint>({fragCoord}.x)");
            Input(9, $"as_type<uint>({fragCoord}.y)");
            Input(10, $"as_type<uint>({fragCoord}.z)");
            // POS_W_FLOAT is W; [[position]].w is 1/W, like Vulkan's FragCoord.w.
            Input(11, $"as_type<uint>(1.0f / {fragCoord}.w)");

            // FRONT_FACE, ANCILLARY, SAMPLE_COVERAGE and POS_FIXED_PT follow the
            // position inputs.
            Input(12, PixelSystemInput(12));
            Input(13, PixelSystemInput(13));
            Input(14, PixelSystemInput(14));
            Input(15, PixelSystemInput(15));
        }
    }
}
