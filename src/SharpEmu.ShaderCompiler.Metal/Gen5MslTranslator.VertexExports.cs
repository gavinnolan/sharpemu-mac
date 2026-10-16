// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Text;

namespace SharpEmu.ShaderCompiler.Metal;

// The auxiliary position exports: POS1..POS3 carry point size, render target array
// index, viewport index and the clip and cull distances. PA_CL_VS_OUT_CNTL (the
// request's PositionExportControl) says which of the misc, CCDIST0 and CCDIST1 vectors
// are exported and in which order they occupy the POS1.. slots; this mirrors the SPIR-V
// translator's DeclareAuxPositionOutputs/EmitAuxPositionExport.
//
// Metal has [[point_size]], [[render_target_array_index]], [[viewport_array_index]] and
// up to eight [[clip_distance]] planes, but no cull distance. A cull plane is folded into
// the clip distance array: a negative guest distance becomes -1 and anything else (zero,
// positive, NaN) becomes a huge positive value. A primitive with every vertex negative is
// then clipped away whole, which is Vulkan's cull rule; a primitive with any vertex kept
// is clipped only where the interpolated value drops below zero, which with the huge
// value is the primitive's own edge between the negative vertices — nothing a sample can
// land inside, so it stays whole as the cull rule requires.
//
// In the mesh stage the layer and the viewport index are per-primitive outputs in Metal.
// The guest exports them per vertex and the primitive takes its provoking (first)
// vertex's value, so each lane parks its values in threadgroup memory for the epilogue.
public static partial class Gen5MslTranslator
{
    private const uint MaxHostClipDistances = 8;

    // What a kept cull-distance vertex writes into its emulated clip plane.
    private const string CullPassDistance = "1.0e18f";

    private sealed partial class CompilationContext
    {
        private bool _writesPointSize;
        private bool _writesLayer;
        private bool _writesViewportIndex;
        private uint _clipDistanceCount;
        private uint _cullDistanceCount;
        private uint _invalidPositionClipDistance = uint.MaxValue;
        private uint _hostPlaneClipDistance = uint.MaxValue;

        /// <summary>The host clip-distance planes this program writes: guest clip planes,
        /// the zero-position plane, the fixed-function planes the host does not clip, then
        /// the emulated cull planes.</summary>
        private uint HostClipDistanceCount =>
            Math.Min(_clipDistanceCount + _cullDistanceCount, MaxHostClipDistances);

        private readonly record struct PositionExportComponent(
            uint ClipDistance,
            uint CullDistance,
            bool PointSize,
            bool Layer,
            bool Viewport);

        private static PositionExportComponent DecodePositionExportComponent(
            uint control,
            uint positionIndex,
            uint component)
        {
            if (positionIndex == 0 || component >= 4)
            {
                return new(uint.MaxValue, uint.MaxValue, false, false, false);
            }

            // VS_OUT_MISC_VEC_ENA, VS_OUT_CCDIST0_VEC_ENA and VS_OUT_CCDIST1_VEC_ENA (bits
            // 21-23) pack the enabled vectors into POS1 onwards in that order.
            var slot = positionIndex - 1;
            var vector = 3u;
            for (var index = 0u; index < 3; index++)
            {
                if ((control & (1u << (int)(21 + index))) == 0)
                {
                    continue;
                }

                if (slot == 0)
                {
                    vector = index;
                    break;
                }

                slot--;
            }

            if (vector == 3)
            {
                return new(uint.MaxValue, uint.MaxValue, false, false, false);
            }

            if (vector == 0)
            {
                // The misc vector: point size in x (USE_VTX_POINT_SIZE, bit 16), the render
                // target array index and the viewport index share z (bits 18 and 19).
                return new(
                    uint.MaxValue,
                    uint.MaxValue,
                    component == 0 && (control & (1u << 16)) != 0,
                    component == 2 && (control & (1u << 18)) != 0,
                    component == 2 && (control & (1u << 19)) != 0);
            }

            var scalar = (vector - 1) * 4 + component;
            var lower = scalar == 0 ? 0u : (1u << (int)scalar) - 1u;
            var clip = control & 0xffu;
            var cull = (control >> 8) & 0xffu;
            return new(
                (clip & (1u << (int)scalar)) != 0
                    ? (uint)BitOperations.PopCount(clip & lower)
                    : uint.MaxValue,
                (cull & (1u << (int)scalar)) != 0
                    ? (uint)BitOperations.PopCount(cull & lower)
                    : uint.MaxValue,
                false,
                false,
                false);
        }

        /// <summary>Discovers which auxiliary outputs the program writes. Runs before block
        /// emission, since both the output struct and the exports depend on it.</summary>
        private void DeclareAuxPositionOutputs()
        {
            var clipCount = 0u;
            var cullCount = 0u;
            foreach (var instruction in _request.Program.Instructions)
            {
                if (instruction.Control is not Gen5ExportControl { Target: >= 13 and < 16 } export)
                {
                    continue;
                }

                for (var component = 0u; component < 4; component++)
                {
                    if ((export.EnableMask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    var output = DecodePositionExportComponent(
                        _request.PositionExportControl,
                        export.Target - 12,
                        component);
                    _writesPointSize |= output.PointSize;
                    _writesLayer |= output.Layer;
                    _writesViewportIndex |= output.Viewport;
                    if (output.ClipDistance != uint.MaxValue)
                    {
                        clipCount = Math.Max(clipCount, output.ClipDistance + 1);
                    }

                    if (output.CullDistance != uint.MaxValue)
                    {
                        cullCount = Math.Max(cullCount, output.CullDistance + 1);
                    }
                }
            }

            if (_request.SupportsClipDistance && clipCount + cullCount < MaxHostClipDistances &&
                _request.Program.Instructions.Any(static instruction =>
                    instruction.Control is Gen5ExportControl { Target: 12, EnableMask: not 0 }))
            {
                // Use a separate plane so auxiliary position exports keep their own distances.
                _invalidPositionClipDistance = clipCount++;
            }

            var hostPlanes = (uint)HostPlaneDistances("p").Count;
            if (hostPlanes != 0)
            {
                // The guest clips these planes in fixed function; the host only through clip
                // distances, which a program that exports no position cannot write.
                if (!_request.SupportsClipDistance ||
                    !_request.Program.Instructions.Any(static instruction =>
                        instruction.Control is Gen5ExportControl { Target: 12, EnableMask: not 0 }) ||
                    clipCount + hostPlanes + cullCount > MaxHostClipDistances)
                {
                    throw new NotSupportedException(
                        $"the guest clip planes ({_request.ClipSpace}) need {hostPlanes} host clip distances beside " +
                        $"{clipCount} clip and {cullCount} cull distances: clip distances " +
                        $"{(_request.SupportsClipDistance ? "available" : "unsupported")}, at most {MaxHostClipDistances}");
                }

                _hostPlaneClipDistance = clipCount;
                clipCount += hostPlanes;
            }

            _clipDistanceCount = clipCount;
            _cullDistanceCount = cullCount;
        }

        /// <summary>The clip distance of every fixed-function plane the program clips, from
        /// the guest's exported position <paramref name="position"/> in its own clip space.</summary>
        private List<string> HostPlaneDistances(string position)
        {
            var planes = _request.ClipSpace.DepthPlanes;
            var distances = new List<string>();
            if ((planes & ShaderDepthPlanes.NearAtZero) != 0)
            {
                distances.Add($"{position}.z");
            }

            if ((planes & ShaderDepthPlanes.NearAtNegativeW) != 0)
            {
                distances.Add($"{position}.z + {position}.w");
            }

            if ((planes & ShaderDepthPlanes.Far) != 0)
            {
                distances.Add($"{position}.w - {position}.z");
            }

            return distances;
        }

        /// <summary>The fixed-function plane distances of the guest position in
        /// <paramref name="position"/>, written into the output <paramref name="output"/>.</summary>
        private IEnumerable<string> HostPlaneStatements(string position, string output)
        {
            var distances = HostPlaneDistances(position);
            for (var index = 0; index < distances.Count; index++)
            {
                yield return $"{output}.sharpemu_clip_distance[{_hostPlaneClipDistance + (uint)index}] = {distances[index]};";
            }
        }

        /// <summary>The auxiliary fields of the vertex output struct (the vertex stage's
        /// Gen5VsOut or the mesh stage's vertex). The layer and viewport index are
        /// per-vertex only in the vertex stage.</summary>
        private void EmitAuxPositionFields(StringBuilder source, bool perVertexLayer)
        {
            if (_writesPointSize)
            {
                source.AppendLine("    float sharpemu_point_size [[point_size]];");
            }

            if (perVertexLayer && _writesLayer)
            {
                source.AppendLine("    uint sharpemu_layer [[render_target_array_index]];");
            }

            if (perVertexLayer && _writesViewportIndex)
            {
                source.AppendLine("    uint sharpemu_viewport [[viewport_array_index]];");
            }

            if (HostClipDistanceCount != 0)
            {
                source.AppendLine(
                    $"    float sharpemu_clip_distance [[clip_distance]] [{HostClipDistanceCount}];");
            }
        }

        private bool MeshWritesPrimitiveOutputs =>
            _stage == Gen5MslStage.Mesh && (_writesLayer || _writesViewportIndex);

        private string LayerOutput => _stage == Gen5MslStage.Mesh
            ? "sharpemu_mesh_layer"
            : "sharpemu_out.sharpemu_layer";

        private string ViewportOutput => _stage == Gen5MslStage.Mesh
            ? "sharpemu_mesh_viewport"
            : "sharpemu_out.sharpemu_viewport";

        private bool ConvertsPositionToHostClipSpace =>
            _request.ClipSpace.Enabled ||
            _request.ClipSpace.PositionYDown ||
            _request.ClipSpace.PositionXMirror ||
            _request.ClipSpace.DepthNegativeOneToOne;

        /// <summary>The exported POS0 <paramref name="position"/> in Metal's clip space, held in
        /// a temporary when anything changes, after the fixed-function plane distances are
        /// taken from the guest's position.</summary>
        private string ToHostClipSpace(string position)
        {
            var planes = _hostPlaneClipDistance != uint.MaxValue;
            if (!ConvertsPositionToHostClipSpace && !planes)
            {
                return position;
            }

            var converted = Temp("float4", position);
            foreach (var statement in HostPlaneStatements(converted, "sharpemu_out"))
            {
                Line(ExecGuarded(statement));
            }

            foreach (var statement in HostClipSpaceStatements(converted))
            {
                Line(statement);
            }

            return converted;
        }

        /// <summary>
        /// Brings the guest position in <paramref name="position"/> to Metal's clip space in
        /// place. A clip-disabled draw's position still divides by w: its window coordinates
        /// go through the viewport transform the SPIR-V translator applies
        /// (ConvertPositionToClipSpace), against the same maximum-size viewport the draw state
        /// then sets, scaled by w so the host's divide lands on them. Vulkan's NDC is y-down and
        /// Metal's y-up, and Metal takes no negative viewport height: where the host viewport
        /// is y-down — the clip-disabled one always, else a positive guest y scale — y is
        /// negated so the position lands on Vulkan's pixels. Neither host takes a negative
        /// viewport width, so a mirrored guest x scale negates x. Metal's clip volume is z in
        /// [0, w] with no negative-one-to-one control, so a guest [-w, w] depth becomes
        /// (z + w) / 2, which the viewport's near and far then map exactly as Vulkan maps z.
        /// </summary>
        private IEnumerable<string> HostClipSpaceStatements(string position)
        {
            var clip = _request.ClipSpace;
            if (clip.Enabled)
            {
                // Window x = x / w * scale + offset, so x' / w = window / half - 1 keeps w
                // for the host's divide and for perspective-correct interpolation.
                yield return
                    $"{position}.x = ({position}.x * {FloatBits(clip.ScaleX)} + {FloatBits(clip.OffsetX)} * {position}.w) / " +
                    $"{FloatBits(clip.HalfExtentX)} - {position}.w;";
                yield return
                    $"{position}.y = ({position}.y * {FloatBits(clip.ScaleY)} + {FloatBits(clip.OffsetY)} * {position}.w) / " +
                    $"{FloatBits(clip.HalfExtentY)} - {position}.w;";
            }

            if (clip.PositionXMirror)
            {
                yield return $"{position}.x = -{position}.x;";
            }

            if (clip.PositionYDown)
            {
                yield return $"{position}.y = -{position}.y;";
            }

            if (clip.DepthNegativeOneToOne)
            {
                yield return $"{position}.z = ({position}.z + {position}.w) * 0.5f;";
            }
        }

        private static string FloatBits(float value) =>
            $"as_type<float>(0x{BitConverter.SingleToUInt32Bits(value):X8}u)";

        /// <summary>POS0's zero-position plane, written after the position itself.</summary>
        private void EmitInvalidPositionDistance()
        {
            if (_invalidPositionClipDistance == uint.MaxValue)
            {
                return;
            }

            // A zero position has an undefined perspective divide. Collapse its primitive
            // to the remaining edge, as the guest's clipping-error cull does.
            Line(ExecGuarded(
                $"sharpemu_out.sharpemu_clip_distance[{_invalidPositionClipDistance}] = " +
                "all(sharpemu_out.sharpemu_position == float4(0.0f)) ? -1.0f : 0.0f;"));
        }

        private void EmitAuxPositionExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            var positionIndex = export.Target - 12;
            for (var component = 0; component < 4; component++)
            {
                if ((export.EnableMask & (1u << component)) == 0)
                {
                    continue;
                }

                var output = DecodePositionExportComponent(
                    _request.PositionExportControl,
                    positionIndex,
                    (uint)component);
                var clipIndex = output.ClipDistance;
                var cullIndex = output.CullDistance == uint.MaxValue
                    ? uint.MaxValue
                    : _clipDistanceCount + output.CullDistance;
                if (cullIndex >= MaxHostClipDistances)
                {
                    cullIndex = uint.MaxValue;
                }

                if (!output.PointSize &&
                    !output.Layer &&
                    !output.Viewport &&
                    clipIndex == uint.MaxValue &&
                    cullIndex == uint.MaxValue)
                {
                    continue;
                }

                var raw = export.Compressed
                    ? $"as_type<uint>((float)as_type<half2>(v[{instruction.Sources[component >> 1].Value}])[{component & 1}])"
                    : $"v[{instruction.Sources[component].Value}]";
                var bits = Temp("uint", raw);
                var value = $"as_type<float>({bits})";
                if (output.Layer)
                {
                    Line(ExecGuarded($"{LayerOutput} = {bits} & 0x7FFu;"));
                }

                if (output.Viewport)
                {
                    Line(ExecGuarded($"{ViewportOutput} = ({bits} >> 16u) & 0xFu;"));
                }

                if (output.PointSize)
                {
                    Line(ExecGuarded($"sharpemu_out.sharpemu_point_size = {value};"));
                }

                if (clipIndex != uint.MaxValue)
                {
                    Line(ExecGuarded($"sharpemu_out.sharpemu_clip_distance[{clipIndex}] = {value};"));
                }

                if (cullIndex != uint.MaxValue)
                {
                    Line(ExecGuarded(
                        $"sharpemu_out.sharpemu_clip_distance[{cullIndex}] = " +
                        $"{value} < 0.0f ? -1.0f : {CullPassDistance};"));
                }
            }
        }
    }
}
