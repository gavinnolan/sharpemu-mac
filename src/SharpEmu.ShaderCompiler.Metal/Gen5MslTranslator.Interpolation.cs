// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        // Pixel input slots V_INTERP_MOV reads one vertex of (P10/P20, or P1/P2 over
        // them), declared as metal::vertex_value<float4>: MSL 4.0 (macOS 26) on an Apple10
        // GPU. The request says whether the host can run that.
        private readonly HashSet<uint> _perVertexAttributes = [];

        // Slots read only through P0, the provoking vertex, which a flat input gives.
        private readonly HashSet<uint> _flatParameterAttributes = [];

        // Slots that share a per-vertex input with a V_INTERP_MOV slot; true for a flat slot.
        private readonly Dictionary<uint, bool> _perVertexSourcedAttributes = [];

        // Slots that read another slot's declared input, keyed by slot.
        private readonly Dictionary<uint, uint> _sharedPixelInputs = [];

        // The [[barycentric_coord]] qualifiers, or null when the input is not declared.
        // Metal allows one perspective and one linear barycentric per fragment function.
        private string? _perspectiveBarycentric;
        private string? _linearBarycentric;

        private const string PerspectiveBarycentricField = "sharpemu_in.sharpemu_bary_persp";
        private const string LinearBarycentricField = "sharpemu_in.sharpemu_bary_linear";

        private static readonly string[] VertexIndexNames = ["first", "second", "third"];

        /// <summary>True when the program declares a vertex_value input, which only MSL 4.0 compiles.</summary>
        private bool UsesPerVertexPixelInputs => _perVertexAttributes.Count != 0;

        private uint EnabledPixelInputs => _pixelInputAddress & _pixelInputEnable;

        // SPI_PS_INPUT_CNTL for a slot: OFFSET in [4:0], FLAT_SHADE in bit 10.
        private uint PixelInputControl(uint attribute) =>
            attribute < (uint)_pixelInputCntl.Length ? _pixelInputCntl[attribute] : attribute;

        /// <summary>
        /// Decides how every V_INTERP slot is declared, before any block is emitted.
        /// Mirrors the SPIR-V translator's DeclareInterpolationParameters and its slot
        /// grouping, except that a slot read only through P0 stays a flat input even
        /// when per-vertex inputs exist: flat is exactly P0 and needs no MSL 4.0.
        /// </summary>
        private void ClassifyPixelInputs()
        {
            foreach (var attribute in _pixelAttributes)
            {
                if (!_request.Program.Instructions.Any(instruction =>
                        instruction.Opcode == "VInterpMovF32" &&
                        instruction.Control is Gen5InterpolationControl control &&
                        control.Attribute == attribute))
                {
                    continue;
                }

                if (ReadsOnlyTheProvokingVertex(attribute))
                {
                    _flatParameterAttributes.Add(attribute);
                }
                else if (_request.SupportsPerVertexPixelInputs)
                {
                    _perVertexAttributes.Add(attribute);
                }
            }

            // Several PS input slots may read one VS parameter (the guest compiler emits a
            // slot per use). Slots of one kind share one host input; when a V_INTERP_MOV
            // slot is among them, the others read the per-vertex values (interpolated with
            // the barycentrics, or vertex 0 for a flat slot).
            bool IsFlatSlot(uint attribute) =>
                (PixelInputControl(attribute) & 0x400u) != 0 ||
                _flatParameterAttributes.Contains(attribute);
            foreach (var group in _pixelAttributes.GroupBy(attribute =>
                         PixelInputControl(attribute) & 0x1Fu))
            {
                var perVertexOwner = group.Where(_perVertexAttributes.Contains).Cast<uint?>().FirstOrDefault();
                var owners = new Dictionary<bool, uint>();
                foreach (var attribute in group)
                {
                    if (perVertexOwner is { } perVertex)
                    {
                        if (attribute != perVertex)
                        {
                            _sharedPixelInputs.Add(attribute, perVertex);
                            if (!_perVertexAttributes.Contains(attribute))
                            {
                                _perVertexSourcedAttributes.Add(attribute, IsFlatSlot(attribute));
                            }
                        }

                        continue;
                    }

                    var flat = IsFlatSlot(attribute);
                    if (owners.TryGetValue(flat, out var owner))
                    {
                        _sharedPixelInputs.Add(attribute, owner);
                    }
                    else
                    {
                        owners.Add(flat, attribute);
                    }
                }
            }

            if (!UsesPerVertexPixelInputs)
            {
                return;
            }

            // A V_INTERP_P1/P2 over a per-vertex slot reads the I/J barycentrics from the
            // input VGPRs, so those are seeded from [[barycentric_coord]]. Metal declares
            // one barycentric per perspective mode at one location, so when a program
            // enables several locations of a mode the pixel center wins; they differ only
            // on a multisampled target.
            var enabled = EnabledPixelInputs;
            if ((enabled & 0xFu) != 0 || _perVertexSourcedAttributes.ContainsValue(false))
            {
                // PERSP_PULL_MODEL (bit 3) and a rebuilt smooth slot are pixel-center values.
                _perspectiveBarycentric = BarycentricLocation(enabled | ((enabled & 0x8u) >> 2), 0) + "perspective";
            }

            if ((enabled & 0x70u) != 0)
            {
                _linearBarycentric = BarycentricLocation(enabled, 4) + "no_perspective";
            }
        }

        // The location of one perspective mode's SAMPLE/CENTER/CENTROID inputs
        // (bits first, first + 1, first + 2): the center unless only others are enabled.
        private static string BarycentricLocation(uint enabled, int first) =>
            (enabled & (2u << first)) != 0 || (enabled & (5u << first)) == 0
                ? "center_"
                : (enabled & (4u << first)) != 0 ? "centroid_" : "sample_";

        /// <summary>The Gen5PsIn members for the V_INTERP slots and the pixel system inputs.</summary>
        private void EmitPixelInputMembers(StringBuilder source)
        {
            var attributes = _pixelAttributes.Where(attribute => !_sharedPixelInputs.ContainsKey(attribute)).ToArray();
            var locations = Gen5PixelInputMapping.ResolveLocations(_pixelInputCntl, attributes);
            for (var index = 0; index < attributes.Length; index++)
            {
                var attribute = attributes[index];
                if (_perVertexAttributes.Contains(attribute))
                {
                    source.AppendLine(
                        $"    vertex_value<float4> attr{attribute} [[user(locn{locations[index]})]];");
                    continue;
                }

                var flat = (PixelInputControl(attribute) & 0x400u) != 0 ||
                    _flatParameterAttributes.Contains(attribute)
                    ? ", flat"
                    : string.Empty;
                source.AppendLine($"    float4 attr{attribute} [[user(locn{locations[index]}){flat}]];");
            }

            if (_perspectiveBarycentric is not null)
            {
                source.AppendLine($"    float3 sharpemu_bary_persp [[barycentric_coord, {_perspectiveBarycentric}]];");
            }

            if (_linearBarycentric is not null)
            {
                source.AppendLine($"    float3 sharpemu_bary_linear [[barycentric_coord, {_linearBarycentric}]];");
            }

            // FRONT_FACE and ANCILLARY are VGPRs the hardware fills for pixel shaders that
            // enable them; SAMPLE_COVERAGE is the pixel's coverage mask.
            var enabled = EnabledPixelInputs;
            if ((enabled & (1u << 12)) != 0)
            {
                source.AppendLine("    bool sharpemu_front_facing [[front_facing]];");
            }

            if ((enabled & (1u << 13)) != 0)
            {
                source.AppendLine("    uint sharpemu_layer [[render_target_array_index]];");
            }

            if ((enabled & (1u << 14)) != 0)
            {
                source.AppendLine("    uint sharpemu_sample_mask [[sample_mask]];");
            }
        }

        private string PixelInputField(uint attribute) =>
            $"sharpemu_in.attr{(_sharedPixelInputs.TryGetValue(attribute, out var owner) ? owner : attribute)}";

        private static string VertexValue(string field, uint vertex, uint channel) =>
            $"{field}.get(vertex_index::{VertexIndexNames[vertex]})[{channel}]";

        // A smooth or flat slot read from a per-vertex input: the vertices weighted by the
        // perspective barycentrics, or vertex 0 (the provoking vertex) for a flat slot.
        private void EmitPerVertexSourcedInterpolation(
            Gen5InterpolationControl interpolation,
            uint destination,
            bool flat)
        {
            var field = PixelInputField(interpolation.Attribute);
            var value = VertexValue(field, 0, interpolation.Channel);
            if (!flat)
            {
                value = $"({value} * {PerspectiveBarycentricField}.x + " +
                    $"{VertexValue(field, 1, interpolation.Channel)} * {PerspectiveBarycentricField}.y + " +
                    $"{VertexValue(field, 2, interpolation.Channel)} * {PerspectiveBarycentricField}.z)";
            }

            StoreVector(destination, AsUInt(value));
        }

        // V_INTERP_MOV P10/P20/P0 and V_INTERP_P1/P2 over the three vertex values. A
        // custom-interpolated slot hands the raw vertex values instead of the deltas.
        private bool TryEmitInterpolationParameter(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var custom = interpolation.Attribute < 32 &&
                (_request.PixelCustomInterpolationMask & (1u << (int)interpolation.Attribute)) != 0;
            var field = PixelInputField(interpolation.Attribute);

            string Parameter(uint mode)
            {
                var value = VertexValue(field, (mode + 1) % 3, interpolation.Channel);
                return !custom && mode < 2
                    ? $"({value} - {VertexValue(field, 0, interpolation.Channel)})"
                    : value;
            }

            string result;
            if (instruction.Opcode == "VInterpMovF32")
            {
                var mode = interpolation.Parameter;
                if (mode >= 3)
                {
                    error = "reserved interpolation parameter selector";
                    return false;
                }

                result = Parameter(mode);
            }
            else if (instruction.Opcode is "VInterpP1F32" or "VInterpP2F32")
            {
                var firstPhase = instruction.Opcode == "VInterpP1F32";
                var source = AsFloat(SourceExpression(instruction.Sources[0], instruction));
                result = $"({Parameter(firstPhase ? 0u : 1u)} * {source} + " +
                    $"{(firstPhase ? Parameter(2) : AsFloat($"v[{destination}]"))})";
            }
            else
            {
                error = $"unsupported interpolation opcode {instruction.Opcode}";
                return false;
            }

            StoreVector(destination, AsUInt(result));
            return true;
        }

        /// <summary>
        /// The I/J barycentrics an interpolation slot's VGPRs carry when the program
        /// interpolates per-vertex inputs by hand, or the I/W, J/W, 1/W of the pull
        /// model. Null when the slot keeps its registers unwritten.
        /// </summary>
        private string[]? BarycentricInputRegisters(int bit)
        {
            if (bit > 6 || !UsesPerVertexPixelInputs || (EnabledPixelInputs & (1u << bit)) == 0)
            {
                return null;
            }

            if (bit == 3)
            {
                // [[position]].w is 1/W, and the perspective-correct barycentric times 1/W
                // is the screen-linear I/W the pull model interpolates.
                const string inverseW = "sharpemu_in.sharpemu_frag_coord.w";
                return
                [
                    $"{PerspectiveBarycentricField}.y * {inverseW}",
                    $"{PerspectiveBarycentricField}.z * {inverseW}",
                    inverseW,
                ];
            }

            var field = bit < 4 ? PerspectiveBarycentricField : LinearBarycentricField;
            return [$"{field}.y", $"{field}.z"];
        }

        // Front facing reads as 1.0f and back facing as 0, which satisfies both the float
        // greater-than and the integer not-equal tests compilers emit for it.
        // ANCILLARY carries the render target array index in bits [28:16]. POS_FIXED_PT
        // holds the integer pixel position: x in bits [15:0] and y in bits [31:16].
        private static string? PixelSystemInput(int bit) => bit switch
        {
            12 => "(sharpemu_in.sharpemu_front_facing ? 0x3F800000u : 0u)",
            13 => "(sharpemu_in.sharpemu_layer << 16)",
            14 => "sharpemu_in.sharpemu_sample_mask",
            15 => "((uint(sharpemu_in.sharpemu_frag_coord.x) & 0xFFFFu) | " +
                "(uint(sharpemu_in.sharpemu_frag_coord.y) << 16))",
            _ => null,
        };
    }
}
