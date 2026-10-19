// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Hashing;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// What a Metal render pipeline descriptor holds, as bytes: two pipelines whose keys are
/// equal build the same descriptor and so share one state (MetalSharedHandleTable). Compared
/// whole, not by a hash alone, so a collision can never hand a draw another pipeline's state.
/// </summary>
internal sealed class MetalPipelineStateKey : IEquatable<MetalPipelineStateKey>
{
    private readonly byte[] _bytes;
    private readonly int _hash;

    private MetalPipelineStateKey(byte[] bytes)
    {
        _bytes = bytes;
        _hash = (int)XxHash3.HashToUInt64(bytes);
    }

    public int Length => _bytes.Length;

    public bool Equals(MetalPipelineStateKey? other) =>
        other is not null && _hash == other._hash && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as MetalPipelineStateKey);

    public override int GetHashCode() => _hash;

    /// <summary>Appends the fields in the order the descriptor is filled in.</summary>
    public sealed class Builder
    {
        private byte[] _buffer = new byte[256];
        private int _length;

        public Builder Add(ulong value)
        {
            Reserve(sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
            _length += sizeof(ulong);
            return this;
        }

        public Builder Add(UInt128 value) => Add((ulong)value).Add((ulong)(value >> 64));

        public Builder Add(bool value) => Add(value ? 1UL : 0UL);

        public MetalPipelineStateKey Build() => new(_buffer.AsSpan(0, _length).ToArray());

        private void Reserve(int count)
        {
            if (_length + count > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
            }
        }
    }
}
