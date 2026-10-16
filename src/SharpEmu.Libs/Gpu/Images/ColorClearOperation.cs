// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;

namespace SharpEmu.Libs.Gpu.Images;

public readonly record struct ColorClearOperation(
    uint ImageLayer, ulong MetadataAddress, ulong MetadataSize, byte MetadataCode, GpuClearColor Color);

public struct UniformMetadataScan(byte expectedCode, ulong expectedBytes)
{
    private ulong _remainingBytes = expectedBytes;
    private bool _rejected = expectedBytes == 0;

    public bool Complete => !_rejected && _remainingBytes == 0;

    public bool Accept(ReadOnlySpan<byte> bytes)
    {
        if (_rejected || bytes.IsEmpty || (ulong)bytes.Length > _remainingBytes ||
            !NativeColorClear.IsUniform(bytes, expectedCode))
        {
            _rejected = true;
            return false;
        }

        _remainingBytes -= (ulong)bytes.Length;
        return true;
    }
}
