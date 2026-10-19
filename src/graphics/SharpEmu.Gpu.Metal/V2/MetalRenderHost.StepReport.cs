// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Gpu.Metal.V2;

// The dispatcher step cap, made observable. Every translated program bounds its
// while(active){switch(pc)} loop so a mistranslated loop-exit condition terminates the
// invocation instead of wedging the queue, but that bound used to be silent: a shader
// that spun to the cap only looked slow, and nothing said which one it was. The last
// field of every stage argument buffer is now a pointer to one word of this buffer,
// private to the program's hash, which the dispatcher increments when it gives up. The
// host reads the words back after each flush, so hitting the cap reads as
// [METAL-V2][STEP-CAP] with the hash to dump with SHARPEMU_SHADER_MSL_DUMP_HASH.
internal sealed partial class MetalRenderHost
{
    private const int StepReportSlots = 1024;

    // Each program's report is a counter followed by seven probe words a capped invocation
    // may fill (SHARPEMU_MSL_STEPCAP_PROBE).
    private const int StepReportStride = 8;
    private const int MaxStepCapReports = 64;

    private nint _stepReportBuffer;
    private nint _stepReportContents;
    private ulong _stepReportGpuAddress;
    private int _stepReportUsed;
    private int _stepCapReports;
    private readonly Dictionary<ulong, int> _stepReportSlotByHash = new();
    private readonly ulong[] _stepReportHashBySlot = new ulong[StepReportSlots];
    private readonly string[] _stepReportStageBySlot = new string[StepReportSlots];
    private readonly long[] _stepCapTotalBySlot = new long[StepReportSlots];

    /// <summary>
    /// The GPU address of the step-report word reserved for one program, or zero when
    /// no slot is left; the shader null-checks the pointer, so zero just disables the
    /// report for that program.
    /// </summary>
    private ulong StepReportAddress(ulong hash, string stage)
    {
        if (!_stepReportSlotByHash.TryGetValue(hash, out var slot))
        {
            if (_stepReportUsed == StepReportSlots)
            {
                MetalV2Diagnostics.WarnOnce(
                    "step-report-full",
                    $"The step-report buffer has no slot left; later programs cannot report the " +
                    $"dispatcher step cap: slots={StepReportSlots}.");
                return 0;
            }

            if (_stepReportBuffer == 0)
            {
                _stepReportBuffer = _guestMemory.CreateReportBuffer(
                    StepReportSlots * StepReportStride * sizeof(uint),
                    "SharpEmu StepReport",
                    out _stepReportContents,
                    out _stepReportGpuAddress);
                StepReportWords.Clear();
            }

            slot = _stepReportUsed++;
            _stepReportSlotByHash[hash] = slot;
            _stepReportHashBySlot[slot] = hash;
            _stepReportStageBySlot[slot] = stage;
        }

        NoteUsedResource(_stepReportBuffer, Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite);
        return _stepReportGpuAddress + (ulong)(slot * StepReportStride * sizeof(uint));
    }

    private unsafe Span<uint> StepReportWords =>
        _stepReportContents == 0
            ? []
            : new Span<uint>((void*)_stepReportContents, StepReportSlots * StepReportStride);

    /// <summary>
    /// Reads back and clears the step-report words. Called once per flush: the counts
    /// may belong to the submission before last, which is immaterial for a diagnostic
    /// that only has to name the shader.
    /// </summary>
    private void ReportStepCaps()
    {
        if (_stepReportUsed == 0 || _stepReportContents == 0)
        {
            return;
        }

        var words = StepReportWords;
        for (var slot = 0; slot < _stepReportUsed; slot++)
        {
            var hits = words[slot * StepReportStride];
            if (hits == 0)
            {
                continue;
            }

            words[slot * StepReportStride] = 0;
            var probe = words.Slice(slot * StepReportStride + 1, StepReportStride - 1);
            var probeText = probe.IndexOfAnyExcept(0u) < 0
                ? string.Empty
                : " probe=[" + string.Join(" ", probe.ToArray().Select(static word => $"{word:X8}")) + "]";
            probe.Clear();
            _stepCapTotalBySlot[slot] += hits;
            if (_stepCapReports >= MaxStepCapReports)
            {
                continue;
            }

            _stepCapReports++;
            Console.Error.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[METAL-V2][STEP-CAP] hash=0x{0:X16} stage={1} invocations={2} total={3}{5}{4}",
                _stepReportHashBySlot[slot],
                _stepReportStageBySlot[slot],
                hits,
                _stepCapTotalBySlot[slot],
                _stepCapReports == MaxStepCapReports ? " (further reports suppressed)" : string.Empty,
                probeText));
        }
    }

    private void DisposeStepReport()
    {
        if (_stepReportBuffer == 0)
        {
            return;
        }

        for (var slot = 0; slot < _stepReportUsed; slot++)
        {
            if (_stepCapTotalBySlot[slot] != 0)
            {
                Console.Error.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "[METAL-V2][STEP-CAP] final hash=0x{0:X16} stage={1} total={2}",
                    _stepReportHashBySlot[slot],
                    _stepReportStageBySlot[slot],
                    _stepCapTotalBySlot[slot]));
            }
        }

        MetalNative.SendVoid(_stepReportBuffer, MetalNative.Selector("release"));
        _stepReportBuffer = 0;
        _stepReportContents = 0;
        _stepReportGpuAddress = 0;
    }

    private static string StepReportStageName(int stageIndex) => stageIndex switch
    {
        StageVertex => "vertex",
        StageFragment => "fragment",
        _ => "compute",
    };
}
