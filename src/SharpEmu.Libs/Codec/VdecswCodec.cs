// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Codec;

/// <summary>Decoder session used by libSceVdecsw; the seam tests replace.</summary>
internal interface IVdecswCodec : IDisposable
{
    /// <summary>Decodes one access unit; <paramref name="picture"/> is null when it produced none.</summary>
    bool Decode(byte[] accessUnit, ulong pts, ulong dts, ulong attachedData, out Videodec2DecodedFrame? picture);

    /// <summary>Signals end of stream and appends every picture still buffered, in display order.</summary>
    void Drain(List<Videodec2DecodedFrame> pictures);

    void Reset();
}

/// <summary>FFmpeg H.264 session (Videodec2Decoder) behind <see cref="IVdecswCodec"/>.</summary>
internal sealed class FfmpegVdecswCodec : IVdecswCodec
{
    private readonly Videodec2Decoder _decoder;

    // Output pictures leave the decoder in display order, so the DTS and attached data
    // of the access unit a picture came from are recovered through its PTS.
    private readonly Dictionary<ulong, (ulong Dts, ulong Attached)> _inputs = new();

    private FfmpegVdecswCodec(Videodec2Decoder decoder) => _decoder = decoder;

    public static IVdecswCodec? TryCreate()
    {
        var decoder = Videodec2Decoder.TryCreate();
        return decoder is null ? null : new FfmpegVdecswCodec(decoder);
    }

    public bool Decode(
        byte[] accessUnit, ulong pts, ulong dts, ulong attachedData, out Videodec2DecodedFrame? picture)
    {
        _inputs[pts] = (dts, attachedData);
        if (!_decoder.DecodeAccessUnit(accessUnit, pts, dts, attachedData, out var frame))
        {
            picture = null;
            return false;
        }

        picture = frame is null ? null : Restore(frame);
        return true;
    }

    public void Drain(List<Videodec2DecodedFrame> pictures)
    {
        if (!_decoder.SendEndOfStream())
        {
            return;
        }

        while (_decoder.FlushOutput(out var frame) && frame is not null)
        {
            pictures.Add(Restore(frame));
        }
    }

    public void Reset()
    {
        _inputs.Clear();
        _decoder.Reset();
    }

    public void Dispose() => _decoder.Dispose();

    private Videodec2DecodedFrame Restore(Videodec2DecodedFrame frame) =>
        _inputs.Remove(frame.Pts, out var source)
            ? frame with { Dts = source.Dts, AttachedData = source.Attached }
            : frame;
}
