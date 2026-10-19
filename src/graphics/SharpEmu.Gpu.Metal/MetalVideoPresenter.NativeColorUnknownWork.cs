// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private const string NativeColorUnknownWorkMessage = "Native color has an unknown accepted sibling writer.";
    // Cached DTO values only. None of these fields asserts a physical footprint
    // or permits this unknown writer to execute alongside the native borrower.
    private readonly record struct NativeColorUnknownWorkSnapshot(
        string Location, string Kind, string? DebugName, long AcceptedSequence,
        string? AcceptedQueue, ulong AcceptedSubmission, ulong PayloadBytes,
        ulong Address, ulong SecondaryAddress, ulong Bytes, ulong Value,
        uint Width, uint Height, uint Pitch, long Version, int Handle, int BufferIndex,
        long ProducerSequence, string ProducerQueue, ulong ProducerSubmission,
        ulong ProducerAddress, ulong ProducerBytes, long ProducerSession,
        long TakenSequence, string TakenKind, long ExecutingSequence, string? ExecutingQueue, ulong ExecutingSubmission);

    private sealed class NativeColorUnknownWorkException(NativeColorUnknownWorkSnapshot snapshot) :
        NotSupportedException(NativeColorUnknownWorkMessage)
    {
        internal readonly NativeColorUnknownWorkSnapshot Snapshot = snapshot;
    }

    private static NotSupportedException NativeColorUnknownWorkFailure(
        object work, PendingGuestWork? accepted, NativeColorProducer producer, bool diagnostics)
    {
        if (!diagnostics) return new NotSupportedException(NativeColorUnknownWorkMessage);
        // Entire optional capture is best effort, including allocation. A failed
        // diagnostic cannot replace the original conservative refusal.
        try
        {
            string kind = "unclassified"; string? debugName = null;
            ulong address = 0, secondary = 0, bytes = 0, value = 0;
            uint width = 0, height = 0, pitch = 0; long version = 0; int handle = 0, bufferIndex = 0;
            switch (work)
            {
                case OrderedGuestAction action:
                    kind = "ordered-action"; debugName = action.DebugName; break; // never invoke Action
                case OrderedCommandMemoryOperation operation:
                    kind = "command-memory-" + operation.Operation.Kind; debugName = operation.Operation.DebugName;
                    address = operation.Operation.Address; secondary = operation.Operation.SourceAddress;
                    bytes = operation.Operation.ByteCount; value = operation.Operation.Value; break; // never execute payload
                case GuestImageWrite write:
                    kind = "image-write"; address = write.Address;
                    bytes = (ulong)(write.Pixels?.LongLength ?? 0); value = write.FillValue; break;
                case GuestImageBlit blit:
                    kind = "image-blit"; address = blit.SourceAddress; secondary = blit.DestinationAddress; break;
                case OrderedGuestFlip flip:
                    kind = "ordered-flip"; address = flip.Address; width = flip.Width; height = flip.Height;
                    pitch = flip.PitchInPixel; version = flip.Version; handle = flip.VideoOutHandle; bufferIndex = flip.DisplayBufferIndex; break;
                case GlobalDataShareFill fill:
                    kind = "gds-fill"; address = fill.Offset; bytes = fill.Size; value = fill.Value; break;
                case GlobalDataShareCopyFromGuest fromGuest:
                    kind = "gds-copy-from-guest"; address = fromGuest.Offset; bytes = (ulong)fromGuest.Bytes.LongLength; break;
                case GlobalDataShareCopyToGuest toGuest:
                    kind = "gds-copy-to-guest"; address = toGuest.GuestAddress; secondary = toGuest.Offset; bytes = toGuest.Size; break;
            }
            var executing = _submittingGuestQueue;
            var original = producer.Original;
            return new NativeColorUnknownWorkException(new(
                accepted.HasValue ? "pending" : "taken", kind, debugName,
                accepted?.Sequence ?? _takenGuestWorkSequence, accepted?.Queue.Name, accepted?.Queue.SubmissionId ?? 0,
                accepted?.PayloadBytes ?? 0, address, secondary, bytes, value, width, height, pitch, version, handle, bufferIndex,
                producer.Sequence, producer.Queue.Name, producer.Queue.SubmissionId,
                original.Placement.PhysicalAddress, original.Placement.ByteCount, original.Session,
                _takenGuestWorkSequence, NativeColorDiagnosticWorkKind(_takenDepthCubeProducerWork), _executingGuestWorkSequence, executing?.Name, executing?.SubmissionId ?? 0));
        }
        catch { return new NotSupportedException(NativeColorUnknownWorkMessage); }
    }

    private static string NativeColorDiagnosticWorkKind(object? work) => work switch
    {
        null => "none", OffscreenGuestDraw => "offscreen-draw", ComputeGuestDispatch => "compute-dispatch",
        OrderedGuestAction => "ordered-action", GuestImageWrite => "image-write", GuestImageBlit => "image-blit",
        OrderedCommandMemoryOperation => "command-memory",
        OrderedGuestFlip => "ordered-flip", GlobalDataShareFill => "gds-fill",
        GlobalDataShareCopyFromGuest => "gds-copy-from-guest", GlobalDataShareCopyToGuest => "gds-copy-to-guest",
        _ => "unclassified"
    };

    // Bound owned free-form strings only after the existing 64-row reservation.
    private static string NativeColorDiagnosticText(string? value) =>
        value is null ? "not-recorded" : value[..Math.Min(value.Length, 160)].Replace('\r', ' ').Replace('\n', ' ');

    private static string FormatNativeColorUnknownWork(NativeColorUnknownWorkException failure)
    {
        var s = failure.Snapshot;
        return $"[MetalNativeColorRefusal] reason=unknown-accepted-sibling-writer location={s.Location} kind={s.Kind} debug_name={NativeColorDiagnosticText(s.DebugName)} " +
            $"accepted_sequence={s.AcceptedSequence} accepted_queue={NativeColorDiagnosticText(s.AcceptedQueue)} accepted_submission={s.AcceptedSubmission} payload_bytes={s.PayloadBytes} " +
            $"dto_address=0x{s.Address:X} dto_secondary_address=0x{s.SecondaryAddress:X} dto_bytes={s.Bytes} dto_value=0x{s.Value:X} " +
            $"dto_shape={s.Width}x{s.Height} dto_pitch={s.Pitch} dto_version={s.Version} dto_handle={s.Handle} dto_buffer_index={s.BufferIndex} " +
            $"producer_sequence={s.ProducerSequence} producer_queue={NativeColorDiagnosticText(s.ProducerQueue)} producer_submission={s.ProducerSubmission} " +
            $"producer_address=0x{s.ProducerAddress:X} producer_bytes={s.ProducerBytes} producer_session={s.ProducerSession} " +
            $"taken_sequence={s.TakenSequence} taken_kind={s.TakenKind} executing_sequence={s.ExecutingSequence} executing_queue={NativeColorDiagnosticText(s.ExecutingQueue)} executing_submission={s.ExecutingSubmission}";
    }
}
