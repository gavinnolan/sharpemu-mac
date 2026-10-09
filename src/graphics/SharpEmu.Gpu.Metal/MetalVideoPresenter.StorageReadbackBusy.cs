// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static Action<object>? _finiteStorageBeforeReadbackValidationForControl;
    private static Action? _finiteStorageReadbackBusyForControl;
    private static Action<object>? _finiteStorageAfterReadbackAllocationForControl;
    private const int FiniteStorageReadbackBusyMilliseconds = 500;

    private sealed class FiniteStorageReadbackCohortBusy(FiniteStoragePreencode original) : OriginalCanonicalDirtyBusy(
        "Metal finite storage ORIGINAL healthy native cohort is busy after compute and before readback encoding.")
    { internal readonly FiniteStoragePreencode Original = original; }

    // Trusted call site immediately follows the SAME actual compute encoder's
    // successful endEncoding. This is no status4 or guest publication receipt.
    private static void SealFiniteStorageComputeEncoding(FiniteStoragePreencode original,
        ComputeGuestDispatch dispatch, nint commandBuffer, nint queue)
    {
        lock (_gate)
        {
            ValidateFiniteStoragePreencodeOwnersLocked(original);
            if (!ReferenceEquals(original.Dispatch, dispatch) || original.CommandBuffer != commandBuffer || original.Queue != queue)
                throw new InvalidOperationException("Finite storage compute end lost its original dispatch/batch.");
            original.ComputeEncodingEnded = true;
        }
    }

    // No blit encoder, CB retain or staging allocation exists during this wait.
    // Reuse only the successful PRE-COMPUTE originals; never recapture a cohort
    // after dispatch. Every allocation/encoding path after return is one-shot.
    private static void ValidateFiniteStorageBeforeReadbackAllocation(FiniteStoragePreencode original,
        FiniteStorageWriteback receipt, nint commandBuffer)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Finite storage readback validation requires a released presenter gate.");
        if (!ReferenceEquals(original.Receipt, receipt) || original.CommandBuffer != commandBuffer)
            throw new InvalidOperationException("Finite storage readback cannot adopt another producer receipt/command buffer.");
        while (true)
        {
            try
            {
                _finiteStorageBeforeReadbackValidationForControl?.Invoke(receipt);
                ValidateFiniteStoragePhaseOriginal(original, afterCompute: true);
                lock (_gate)
                {
                    ValidateFiniteStoragePhaseOwnersLocked(original, afterCompute: true);
                    original.ReadbackAllocationStarted = true; // consumes retry before the first native allocation/effect
                }
                return;
            }
            catch (OriginalCanonicalDirtyBusy busy)
            {
                WaitFiniteStorageReadbackBusy(original, busy);
            }
        }
    }

    private static void WaitFiniteStorageReadbackBusy(FiniteStoragePreencode original, OriginalCanonicalDirtyBusy busy)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Finite storage readback Busy wait cannot hold the presenter gate.");
        lock (_gate)
        {
            ValidateFiniteStoragePhaseOwnersLocked(original, afterCompute: true);
            if (busy is SingletonVolumeOriginalBusy volume)
            {
                if (!original.Images.Any(image => ReferenceEquals(image.Origin, volume.Original)))
                    throw new InvalidOperationException("Readback Busy cannot adopt another original volume.");
            }
            else if (busy is not FiniteStorageReadbackCohortBusy cohort || !ReferenceEquals(cohort.Original, original))
                throw new InvalidOperationException("Readback continuation cannot retry another phase or unrelated Busy reason.");
        }
        original.FirstReadbackBusy ??= busy;
        if (original.ReadbackBusyStarted == 0) original.ReadbackBusyStarted = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(original.ReadbackBusyStarted).TotalMilliseconds >= FiniteStorageReadbackBusyMilliseconds)
        {
            try { original.FirstReadbackBusy.Data["FiniteStorageReadbackWait"] = "500ms; same compute-ended ORIGINAL batch; no dispatch/readback allocation/encoding replay"; } catch { }
            ExceptionDispatchInfo.Capture(original.FirstReadbackBusy).Throw();
        }
        _finiteStorageReadbackBusyForControl?.Invoke();
        Thread.Sleep(1);
    }
}
