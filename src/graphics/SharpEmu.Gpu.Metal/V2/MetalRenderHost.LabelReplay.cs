// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

// The labels and command-processor stores a submission carries, kept until it has finished:
// a submission the GPU discards (one that hung, or an innocent victim of another's hang)
// copies none of them, and a guest that waits on one of those labels then waits forever.
// Ghost of Tsushima's title screen and a Black Myth: Wukong fight froze that way after a
// single GPU hang. Their bytes were staged by the host, so the host can still write them:
// in stream order, and only where no later submission has written since, which a replay of
// an older value would undo. SHARPEMU_METAL_V2_LABEL_REPLAY=0 drops them as before.
internal sealed partial class MetalRenderHost
{
    private static readonly bool LabelReplayEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_LABEL_REPLAY") != "0";

    private readonly record struct LabelReplay(ulong Serial, ulong GuestAddress, byte[] Bytes);

    private readonly Queue<LabelReplay> _labelReplays = new();

    // The newest submission to carry a store to each address the queue holds one for.
    private readonly Dictionary<ulong, ulong> _labelReplayNewest = [];

    private long _replayedLabels;
    private int _replayReports;

    // Called as the copies are recorded: their staging bytes are what the host wrote there.
    private unsafe void KeepLabelReplays(ReadOnlySpan<DeferredLabelCopy> copies, ulong serial)
    {
        if (!LabelReplayEnabled)
        {
            return;
        }

        foreach (ref readonly var copy in copies)
        {
            var contents = MetalNative.Send(copy.StagingBuffer, SelBufferContents);
            if (contents == 0 || copy.Bytes <= 0 || copy.Bytes > 256)
            {
                continue;
            }

            var bytes = new ReadOnlySpan<byte>((byte*)contents + copy.StagingOffset, copy.Bytes).ToArray();
            _labelReplays.Enqueue(new LabelReplay(serial, copy.GuestAddress, bytes));
            _labelReplayNewest[copy.GuestAddress] = serial;
        }
    }

    // Called as each submission is sampled, in serial order: a finished one drops what it
    // carried, a failed one writes it.
    private void SettleLabelReplays(ulong serial, bool failed)
    {
        while (_labelReplays.TryPeek(out var replay) && replay.Serial <= serial)
        {
            _labelReplays.Dequeue();
            var newest = _labelReplayNewest.TryGetValue(replay.GuestAddress, out var latest) ? latest : 0;
            if (failed && replay.Serial == serial && newest == serial)
            {
                if (_memory.TryWrite(replay.GuestAddress, replay.Bytes))
                {
                    _imageCache.InvalidateMemory(replay.GuestAddress, (ulong)replay.Bytes.Length);
                    Interlocked.Increment(ref _replayedLabels);
                    if (++_replayReports <= 32)
                    {
                        Console.Error.WriteLine(
                            $"[METAL-V2][WARN] A label the failed submission {serial} carried is written by the host: " +
                            $"address=0x{replay.GuestAddress:X16} bytes={replay.Bytes.Length}.");
                    }
                }
            }

            if (newest <= replay.Serial)
            {
                _labelReplayNewest.Remove(replay.GuestAddress);
            }
        }
    }
}
