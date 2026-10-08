// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // A CPU reader owns these exact accepted receipts, independently of the
    // publication/work owner. Cancellation never inventories borrowers as work.
    private sealed class GuestPresentationPayloadBorrow(Presentation presentation, GuestPayloadReceipt[] receipts)
    {
        public readonly Presentation Presentation = presentation;
        public readonly GuestPayloadReceipt[] Receipts = receipts;
        public readonly int ThreadId = Environment.CurrentManagedThreadId;
        public bool Released;
    }
    private static readonly HashSet<GuestPresentationPayloadBorrow> _activeGuestPresentationPayloadBorrows = [];
    private static int _guestPresentationBorrowShutdownTimeoutMilliseconds = 2000;
    private static int _guestPresentationBorrowShutdownWaiters;
    private static bool _guestPresentationBorrowShutdownIncomplete;

    private static bool TryTakePresentationWithPayloadBorrow(long presentedSequence, out Presentation presentation,
        out GuestPresentationPayloadBorrow? borrow)
    {
        lock (_gate)
        {
            borrow = null;
            if (_textureSourceGraphEnabled && (_closed || _guestWorkFailure is not null))
            {
                presentation = default!;
                return false;
            }
            if (!TryTakePresentation(presentedSequence, out presentation)) return false;
            if (!_textureSourceGraphEnabled || presentation.TranslatedDraw is null) return true;
            if (!_guestPayloadOwners.TryGetValue(presentation, out var owner) || !owner.Accepted || owner.Released)
                throw new InvalidOperationException("Metal selected presentation has no live accepted payload owner.");
            if (owner.Receipts.Length == 0) return true;
            if (_activeGuestPresentationPayloadBorrows.Count >= 64)
                throw new InvalidOperationException("Metal presentation CPU borrow limit exceeded.");
            foreach (var receipt in owner.Receipts)
                if (receipt.Returned || receipt.References <= 0 || receipt.References == int.MaxValue ||
                    receipt.IndexLease is { Returned: true })
                    throw new InvalidOperationException("Metal selected presentation has stale payload return authority.");
            // All metadata allocation/validation precedes pinning. Owner's
            // immutable array is reused; release never looks up a newer receipt.
            var candidate = new GuestPresentationPayloadBorrow(presentation, owner.Receipts);
            _activeGuestPresentationPayloadBorrows.EnsureCapacity(_activeGuestPresentationPayloadBorrows.Count + 1);
            _activeGuestPresentationPayloadBorrows.Add(candidate);
            foreach (var receipt in candidate.Receipts) receipt.References++;
            borrow = candidate;
            return true;
        }
    }

    private static void ReleaseGuestPresentationPayloadBorrow(GuestPresentationPayloadBorrow? borrow, Exception? primaryFailure)
    {
        if (borrow is null) return;
        lock (_gate)
        {
            if (borrow.Released) return;
            // Only an actually registered exact take may decrement references.
            if (!_activeGuestPresentationPayloadBorrows.Remove(borrow))
                throw new InvalidOperationException("Metal presentation CPU borrow has no live owner.");
            borrow.Released = true;
            try { ReleaseGuestPayloadReceiptsLocked(borrow.Receipts); }
            catch (Exception cleanupFailure)
            {
                // Preserve a failure from the frame. Cleanup cannot replace it
                // or retry a consumed receipt after its array has been rerented.
                LatchTextureSourceDrainFailureLocked(_guestWorkFailure ?? primaryFailure ?? cleanupFailure);
                _textureSourceCleanupFailure ??= cleanupFailure;
                if (primaryFailure is null)
                    throw GpuFatal.Fatal("Metal presentation CPU borrow release failed.", cleanupFailure);
            }
            finally { Monitor.PulseAll(_gate); }
        }
    }

    private static bool WaitForGuestPresentationPayloadBorrows()
    {
        if (Monitor.IsEntered(_gate))
            throw new InvalidOperationException("Metal presentation shutdown must not wait while holding the presenter gate.");
        lock (_gate)
        {
            var selfBorrow = _activeGuestPresentationPayloadBorrows.Any(borrow => borrow.ThreadId == Environment.CurrentManagedThreadId);
            var deadline = Stopwatch.GetTimestamp() + (long)(_guestPresentationBorrowShutdownTimeoutMilliseconds * (Stopwatch.Frequency / 1000.0));
            _guestPresentationBorrowShutdownWaiters++;
            try
            {
                while (_activeGuestPresentationPayloadBorrows.Count != 0)
                {
                    var remaining = (int)Math.Max(0, Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency));
                    if (selfBorrow || remaining == 0)
                    {
                        _guestPresentationBorrowShutdownIncomplete = true;
                        _textureSourceCleanupFailure ??= new InvalidOperationException(selfBorrow
                            ? "Metal shutdown retained presentation CPU borrows owned by the closing thread; native owners remain quarantined."
                            : "Metal shutdown timed out draining presentation CPU borrows; borrowed receipts and native owners remain quarantined.");
                        Console.Error.WriteLine($"[LOADER][ERROR] {_textureSourceCleanupFailure.Message}");
                        return false;
                    }
                    Monitor.Wait(_gate, remaining);
                }
                _guestPresentationBorrowShutdownIncomplete = false;
                return true;
            }
            finally { _guestPresentationBorrowShutdownWaiters--; }
        }
    }
}
