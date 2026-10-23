// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Rendering;

// A tessellated draw on a host without a tessellator (IRenderHost.SupportsTessellationEmulation).
//
// Grand Theft Auto V tessellates the near version of its palm trunks and other meshes; the shadow
// passes draw them untessellated, so their shadows fall where the trunks are missing. The draw runs
// as the hardware would run it with every tessellation factor at 1: the merged local/hull program as
// a compute kernel over the patches (one guest wave64 per threadgroup, as many whole patches as fit),
// which hands each patch to the domain stage through the guest's off-chip buffer, and then the domain
// program per vertex at the three corners of every patch, drawn as a triangle list with the pixel
// program as usual. A factor of 1 keeps every patch's corners where they are, so the mesh is the one
// the shadow pass draws, shaded by the draw's own programs (Gen5TessellationLaunch).
//
// Only triangle patches with as many output control points as input ones are taken, and only as many
// patches as the guest's off-chip buffer and factor ring hold at once; anything else is skipped as
// before. SHARPEMU_TESSELLATION=0 skips every tessellated draw.
public sealed partial class RenderExecutor
{
    // VGT_SHADER_STAGES_EN.LS_EN (bits 0-1, on) and HS_EN (bit 2).
    private const uint LocalStageEnable = 0x1;
    private const uint HullStageEnable = 0x4;

    // DI_PT_PATCH, and VGT_TF_PARAM.TYPE's triangle domain.
    private const uint PatchPrimitiveType = 9;
    private const uint TriangleTessellationDomain = 1;

    private static readonly bool EmulateTessellation =
        Environment.GetEnvironmentVariable("SHARPEMU_TESSELLATION") != "0";

    private static int _tessellationRefusalWarnings;
    private long _tessellatedDraws;
    private long _tessellatedPatches;

    internal (long Draws, long Patches) TakeTessellationCounters() =>
        (Interlocked.Exchange(ref _tessellatedDraws, 0), Interlocked.Exchange(ref _tessellatedPatches, 0));

    // Whether this draw is a tessellated one the host runs, and how many control points its patches have.
    private bool IsEmulatedTessellation(RegisterBanks banks, out uint controlPoints)
    {
        controlPoints = 0;
        var stages = banks.Context.ShaderStages;
        if (!EmulateTessellation || !_host.SupportsTessellationEmulation ||
            (stages & LocalStageEnable) == 0 || (stages & HullStageEnable) == 0 ||
            banks.Shader.Vertex.LocalAddress == 0 || banks.UserConfig.PrimitiveType != PatchPrimitiveType)
        {
            return false;
        }

        // VGT_LS_HS_CONFIG: HS_NUM_INPUT_CP [13:8], HS_NUM_OUTPUT_CP [19:14].
        var configuration = banks.Context.ShaderInterface.LocalHullConfiguration;
        var inputPoints = (configuration >> 8) & 0x3F;
        var outputPoints = (configuration >> 14) & 0x3F;
        var domain = banks.Context.ShaderInterface.TessellationFactorParameter & 0x3;
        if (inputPoints == 0 || inputPoints != outputPoints || inputPoints > 32 || domain != TriangleTessellationDomain)
        {
            WarnTessellationRefused(
                $"control points {inputPoints} in, {outputPoints} out, domain {domain} (VGT_LS_HS_CONFIG=0x{configuration:X8})");
            return false;
        }

        controlPoints = inputPoints;
        return true;
    }

    private static void WarnTessellationRefused(string reason)
    {
        if (Interlocked.Increment(ref _tessellationRefusalWarnings) <= 4)
        {
            Console.Error.WriteLine($"[GPU][WARN] A tessellated draw is skipped: {reason}.");
        }
    }

    private void DrawTessellated(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments, uint controlPoints)
    {
        var patchCount = arguments.IndexCount / controlPoints;
        if (patchCount == 0 || arguments.InstanceCount == 0)
        {
            return;
        }

        var patchesPerGroup = ShaderPipelineCache.TessellationPatchesPerGroup(controlPoints);
        var groups = (patchCount + patchesPerGroup - 1) / patchesPerGroup;
        // VGT_HS_OFFCHIP_PARAM.OFFCHIP_BUFFERING [8:0] is one less than the off-chip blocks the guest
        // allocated; the factor ring holds its size over the bytes of one patch.
        var offchipBlocks = (TessellationRings.OffchipParameter & 0x1FF) + 1;
        var factorPatches = TessellationRings.TessFactorRingSize / ShaderPipelineCache.TessellationFactorBytesPerPatch;
        if (groups > offchipBlocks || patchCount > factorPatches)
        {
            WarnTessellationRefused($"{patchCount} patches in {groups} groups, the guest's rings hold {offchipBlocks} groups and {factorPatches} patches");
            return;
        }

        var draw = new DrawCall("DrawTessellated", RecordedOperation.DrawIndex, patchCount * 3, arguments.InstanceCount, arguments.FirstInstance);
        ref var state = ref BeginDrawState();
        if (!TryResolveDrawTargets(banks, in draw, ref state))
        {
            _host.ResetBindings();
            return;
        }

        var inputs = GraphicsProgramInputs.Create();
        Span<ColorComponentMap> mapping = inputs.Mapping;
        foreach (ref readonly var color in BoundColors(ref state))
        {
            mapping[(int)color.Slot] = color.Resolution.ExportMapping;
        }

        var depthAspect = state.Depth.HasTarget &&
            (ViewFormatRules.FullAspects(state.Depth.Target.Target.Format) & GpuImageAspect.DepthBit) != 0;
        var stencilAspect = state.Depth.HasTarget &&
            (ViewFormatRules.DepthAspects(state.Depth.Target.Target.Format) & GpuImageAspect.StencilBit) != 0;
        var programs = _pipelines.GetTessellationPrograms(
            banks.Shader.Vertex, banks.Shader.Pixel, banks.Context.ShaderInterface, banks.Context, mapping,
            state.PixelActive, state.Depth.HasTarget, depthAspect, stencilAspect, controlPoints);
        if (programs is not { Graphics.Available: true, Hull: { Available: true } hull })
        {
            _host.ResetBindings();
            return;
        }

        state.Programs = programs.Graphics;
        if (!TryRunHull(hull, banks, in arguments, controlPoints, patchCount, groups))
        {
            _host.ResetBindings();
            return;
        }

        // The corners of every patch, three vertices each, in patch order (EmitDomainLaunchState).
        var emission = new DrawEmission(false, 0, 0, arguments.FirstInstance, PlainList: true);
        var indexSource = new IndexSource(false, 0, null, 0, GpuIndexType.Uint16);
        _ = RecordDraw(submitId, banks, in draw, ref state, GpuPrimitiveTopology.TriangleList, in emission, in indexSource,
            primitiveRestart: false, setBindDebug: true, setAutoDebug: false);
        _host.ResetBindings();
        if (_tessellatedDraws++ == 0)
        {
            Console.Error.WriteLine(
                $"[GPU][INFO] Tessellated draws run as a hull kernel and a draw of every patch's corners: " +
                $"first draw {patchCount} patches of {controlPoints} control points in {groups} groups, " +
                $"local=0x{banks.Shader.Vertex.LocalAddress:X16} domain=0x{banks.Shader.Vertex.ExportAddress:X16}.");
        }

        _tessellatedPatches += patchCount;
    }

    // Runs the hull kernel over every patch of the draw; false while its pipeline still compiles
    // (the draw is left out this time, before anything of it was recorded).
    private bool TryRunHull(ComputeProgram hull, RegisterBanks banks, in DrawIndexedArguments arguments,
        uint controlPoints, uint patchCount, uint groups)
    {
        var input = hull.Input;
        if (!_pipelines.TryCreateComputePipeline(input, hull.Program, out var pipeline))
        {
            return false;
        }

        // The local half's vertex of each control point, as the geometry engine would hand it over:
        // the index with the draw's vertex offset.
        var indices = ReadGuestIndices(arguments.IndexAddress, patchCount * controlPoints, (GuestIndexType)arguments.IndexTypeAndSize);
        var offset = (int)banks.UserConfig.IndexOffset + arguments.BaseVertex;
        for (var index = 0; index < indices.Length; index++)
        {
            indices[index] = unchecked((uint)((int)indices[index] + offset));
        }

        ReleaseFragmentWrites();
        _host.EndRendering("tessellation");
        using (_host.BeginPreparation())
        {
            var bindings = PrepareBindings(input.Stage);
            _host.BindResources(bindings);
            Span<IPreparedBindings> stages = [bindings];
            _host.CommitBindings(GpuBindPoint.Compute, in pipeline, stages);
            // The off-chip blocks are the ones the last tessellated draw's domain stage read.
            _host.ShaderWriteHazardBarrier();
            _host.BindPipeline(GpuBindPoint.Compute, in pipeline);
            _host.BindHullLaunch(indices, patchCount, arguments.FirstInstance);
            var imageSnapshot = _host.BeginComputeImageSnapshot(bindings);
            _host.Dispatch(groups, 1, 1);
            _host.EndComputeImageSnapshot(imageSnapshot, bindings);
            _host.ShaderAccessBarrier();
        }

        _host.ResetBindings();
        return true;
    }
}
