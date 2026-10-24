// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>Versioning of everything both processes must agree on: region layout and messages.</summary>
public static class GpuRemoteProtocol
{
    /// <summary>Bumped on any change to the region layout or a message format.</summary>
    public const uint Version = 1;
}

/// <summary>Record types on the two rings. 0 is never used; <see cref="SpscRing.PaddingType"/> is reserved.</summary>
public enum GpuRemoteMessage : uint
{
    /// <summary>A → B, the first record A writes: <see cref="HelloMessage"/>.</summary>
    Hello = 1,

    /// <summary>B → A, the answer to <see cref="Hello"/>: <see cref="HelloMessage"/> describing B.</summary>
    HelloAck = 2,

    /// <summary>A → B: <see cref="PingMessage"/>, answered with <see cref="Pong"/>.</summary>
    Ping = 3,

    /// <summary>B → A: the <see cref="PingMessage"/> of a <see cref="Ping"/>, echoed.</summary>
    Pong = 4,

    /// <summary>A → B: exit cleanly. No payload.</summary>
    Shutdown = 5,

    /// <summary>A → B: any payload, consumed and dropped (throughput measurement).</summary>
    Sink = 6,

    /// <summary>A → B: <see cref="BurstMessage"/>, asking B to write that many <see cref="BurstItem"/> records back.</summary>
    Burst = 7,

    /// <summary>B → A: one record of a <see cref="Burst"/>.</summary>
    BurstItem = 8,
}

/// <summary>Opcodes of the synchronous call table.</summary>
public enum GpuRemoteSyncOpcode : uint
{
    /// <summary>Echoes the request back as the response, status 0.</summary>
    Ping = 1,
}

/// <summary>Handshake record, sent each way.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct HelloMessage
{
    public uint ProtocolVersion;
    public int ProcessId;

    /// <summary>The sender's <see cref="System.Runtime.InteropServices.Architecture"/>.</summary>
    public uint Architecture;

    /// <summary>1 when the sender runs under Rosetta, 0 when native, -1 unknown.</summary>
    public int Translated;
}

[StructLayout(LayoutKind.Sequential)]
public struct PingMessage
{
    public ulong Sequence;
    public long Timestamp;
}

[StructLayout(LayoutKind.Sequential)]
public struct BurstMessage
{
    public int Count;
    public int PayloadLength;
}

/// <summary>What one side learned about the other in the handshake.</summary>
public readonly record struct GpuPeerInfo(int ProcessId, uint ProtocolVersion, Architecture Architecture, int Translated)
{
    internal static GpuPeerInfo From(in HelloMessage hello) =>
        new(hello.ProcessId, hello.ProtocolVersion, (Architecture)hello.Architecture, hello.Translated);

    /// <summary>This process, as it describes itself in the handshake.</summary>
    public static GpuPeerInfo ThisProcess => From(DescribeThisProcess());

    internal static HelloMessage DescribeThisProcess() => new()
    {
        ProtocolVersion = GpuRemoteProtocol.Version,
        ProcessId = Environment.ProcessId,
        Architecture = (uint)RuntimeInformation.ProcessArchitecture,
        Translated = RemoteNative.ProcessTranslated(),
    };

    public override string ToString() =>
        $"pid {ProcessId}, {Architecture}{(Translated switch { 1 => " (Rosetta)", 0 => " (native)", _ => string.Empty })}, protocol {ProtocolVersion}";
}
