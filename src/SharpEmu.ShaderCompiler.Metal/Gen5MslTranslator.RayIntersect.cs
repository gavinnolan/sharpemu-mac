// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        // IMAGE_BVH_INTERSECT_RAY / IMAGE_BVH64_INTERSECT_RAY and their A16 forms run the
        // node test of Templates/bvh_prelude.msl against the guest BVH through the
        // device-address page table, which only shaders using them carry.
        private void EmitBvhPreludeIfUsed(StringBuilder source)
        {
            if (_request.Program.Instructions.Any(static instruction => instruction.Control is Gen5RayIntersectControl))
            {
                source.Append(MslTemplates.Render("bvh_prelude"));
            }
        }

        // VADDR (RDNA2 ISA Table 48): node_pointer (two dwords for BVH64), ray_extent,
        // ray_origin.xyz, then ray_dir.xyz and ray_inv_dir.xyz as floats, or with A16 as six
        // halves in three dwords, low half first: {dir.x, dir.y}, {dir.z, inv.x},
        // {inv.y, inv.z}. The 128-bit T# is the four SGPRs at SRSRC; VDATA takes the four
        // result dwords once every input has been read.
        private bool TryEmitRayIntersect(Gen5ShaderInstruction instruction, Gen5RayIntersectControl ray, out string error)
        {
            error = string.Empty;
            if (!_hasDeviceAddressTable)
            {
                error = $"{instruction.Opcode}: BVH nodes are read through device addresses and the layout has no address range binding";
                return false;
            }

            var bvh64 = instruction.Opcode == "ImageBvh64IntersectRay";
            string Address(int component) => VectorExpression(ray.GetAddressRegister(component));
            string Float(int component) => $"as_type<float>({Address(component)})";
            string Half(int half, int first) =>
                $"sharpemu_bvh_half(({Address(first + (half / 2))} >> {(half % 2) * 16}u) & 0xFFFFu)";

            var origin = bvh64 ? 3 : 2;
            var direction = origin + 3;
            string Vector(int offset) => ray.A16
                ? $"float3({Half(offset, direction)}, {Half(offset + 1, direction)}, {Half(offset + 2, direction)})"
                : $"float3({Float(direction + offset)}, {Float(direction + offset + 1)}, {Float(direction + offset + 2)})";

            var pointer = bvh64
                ? $"(ulong){Address(0)} | ((ulong){Address(1)} << 32)"
                : $"(ulong){Address(0)}";
            var descriptor = $"uint4({ScalarExpression(ray.ScalarResource)}, {ScalarExpression(ray.ScalarResource + 1)}, " +
                $"{ScalarExpression(ray.ScalarResource + 2)}, {ScalarExpression(ray.ScalarResource + 3)})";

            Line($"if ({ExecCondition("true")})");
            Line("{");
            _indent++;
            var result = Temp(
                "uint4",
                $"sharpemu_bvh_intersect_ray({DeviceArguments}, {descriptor}, {pointer}, {Float(origin - 1)}, " +
                $"float3({Float(origin)}, {Float(origin + 1)}, {Float(origin + 2)}), {Vector(0)}, {Vector(3)})");
            for (var component = 0u; component < Gen5RayIntersectControl.ResultDwords; component++)
            {
                Line($"{VectorExpression(ray.VectorData + component)} = {result}[{component}];");
            }

            _indent--;
            Line("}");
            return true;
        }
    }
}
