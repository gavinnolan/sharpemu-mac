// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

/// <summary>
/// libSceSsl in "offline" mode: contexts and connections are real handles with correct
/// lifecycle (init before use, bad ids, double term), but there is no TLS transport, so a
/// connection can never be established.
/// </summary>
/// <remarks>
/// Error codes: INVALID_ID (0x8095F006), NOT_FOUND (0x8095F004) and OUT_OF_SIZE (0x8095F008)
/// are used by both references; 0x8095F007 for a null statistics pointer and 0x8095177A for
/// invalid arguments come from the one reference that covers them. Connect/read/write on a
/// connection that cannot exist report SCE_NET_ERROR_ENOTCONN (0x80410139), the documented
/// socket error; no reference pins an SSL-specific value for this case. Loaded certificates are
/// counted, not parsed or retained, so the CA list and certificate queries report NOT_FOUND.
/// </remarks>
public static class SslExports
{
    private const int SslErrorNotFound = unchecked((int)0x8095F004);
    private const int SslErrorInvalidId = unchecked((int)0x8095F006);
    private const int SslErrorInvalidPointer = unchecked((int)0x8095F007);
    private const int SslErrorOutOfSize = unchecked((int)0x8095F008);
    private const int SslErrorInvalidArgument = unchecked((int)0x8095177A);
    private const int NetErrorNotConnected = unchecked((int)0x80410139);
    private const int MaxHostLength = 1024;

    private static readonly ConcurrentDictionary<int, SslContext> _contexts = new();
    private static readonly ConcurrentDictionary<int, SslConnection> _connections = new();
    private static int _nextContextId;
    private static int _nextConnectionId = 0x4000;

    private sealed class SslContext(ulong poolSize)
    {
        public ulong PoolSize { get; } = poolSize;

        public int CaCount { get; set; }

        public bool HasClientIdentity { get; set; }
    }

    private sealed record SslConnection(int ContextId, int SocketId, string Host)
    {
        public ulong VerifyCallback { get; set; }

        public ulong VerifyArgument { get; set; }
    }

    internal static void ResetRuntimeState()
    {
        _contexts.Clear();
        _connections.Clear();
        _nextContextId = 0;
        _nextConnectionId = 0x4000;
    }

    internal static int LiveContextCount => _contexts.Count;

    internal static int LiveConnectionCount => _connections.Count;

    [SysAbiExport(
        Nid = "hdpVEUDFW3s",
        ExportName = "sceSslInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslInit(CpuContext ctx)
    {
        var poolSize = ctx[CpuRegister.Rdi];
        if (poolSize == 0)
        {
            return ctx.SetReturn(SslErrorOutOfSize);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        _contexts[id] = new SslContext(poolSize);

        TraceSsl("init", id, poolSize);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0K1yQ6Lv-Yc",
        ExportName = "sceSslTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslTerm(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.TryRemove(id, out _))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        foreach (var pair in _connections)
        {
            if (pair.Value.ContextId == id)
            {
                _connections.TryRemove(pair.Key, out _);
            }
        }

        TraceSsl("term", id, 0);
        return ctx.SetReturn(0);
    }

    // (sslCtxId, SceSslMemoryPoolStats*): { size_t poolSize, maxInuseSize, currentInuseSize; int reserved }
    [SysAbiExport(
        Nid = "-PoIzr3PEk0",
        ExportName = "sceSslGetMemoryPoolStats",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslGetMemoryPoolStats(CpuContext ctx)
    {
        var statsAddress = ctx[CpuRegister.Rsi];
        if (statsAddress == 0)
        {
            return ctx.SetReturn(SslErrorInvalidPointer);
        }

        Span<byte> stats = stackalloc byte[32];
        if (!_contexts.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var context))
        {
            ctx.Memory.TryWrite(statsAddress, stats);
            return ctx.SetReturn(SslErrorInvalidId);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(stats, context.PoolSize);
        return ctx.Memory.TryWrite(statsAddress, stats)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(SslErrorInvalidPointer);
    }

    // (sslCtxId, caCertNum, SceSslData** caList, SceSslData* clientCert, SceSslData* privateKey)
    [SysAbiExport(
        Nid = "Ab7+DH+gYyM",
        ExportName = "sceSslLoadCert",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslLoadCert(CpuContext ctx)
    {
        if (!_contexts.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var context))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        var caCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var hasCert = ctx[CpuRegister.Rcx] != 0;
        var hasKey = ctx[CpuRegister.R8] != 0;
        if (caCount < 0 || (caCount > 0 && ctx[CpuRegister.Rdx] == 0) || hasCert != hasKey)
        {
            return ctx.SetReturn(SslErrorInvalidArgument);
        }

        lock (context)
        {
            context.CaCount = checked(context.CaCount + caCount);
            context.HasClientIdentity |= hasCert;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "UQ+3Qu7v3cA",
        ExportName = "sceSslUnloadCert",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslUnloadCert(CpuContext ctx)
    {
        if (!_contexts.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var context))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        lock (context)
        {
            context.CaCount = 0;
            context.HasClientIdentity = false;
        }

        return ctx.SetReturn(0);
    }

    // (sslCtxId, SceSslCaList* { void** caList; int caNum })
    [SysAbiExport(
        Nid = "qOn+wm28wmA",
        ExportName = "sceSslGetCaList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslGetCaList(CpuContext ctx) => QueryCertificates(ctx, 16);

    // (sslCtxId, SceSslCaCerts* { SceSslData* certs; size_t num; void* pool })
    [SysAbiExport(
        Nid = "TDfQqO-gMbY",
        ExportName = "sceSslGetCaCerts",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslGetCaCerts(CpuContext ctx) => QueryCertificates(ctx, 24);

    [SysAbiExport(
        Nid = "+DzXseDVkeI",
        ExportName = "sceSslFreeCaList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslFreeCaList(CpuContext ctx) => FreeCertificates(ctx, 16);

    [SysAbiExport(
        Nid = "qIvLs0gYxi0",
        ExportName = "sceSslFreeCaCerts",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslFreeCaCerts(CpuContext ctx) => FreeCertificates(ctx, 24);

    // (sslCtxId, socketId, hostName) -> connection id
    [SysAbiExport(
        Nid = "tuscfitnhEo",
        ExportName = "sceSslCreateConnection",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslCreateConnection(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        if (!TryReadHost(ctx, ctx[CpuRegister.Rdx], out var host))
        {
            return ctx.SetReturn(SslErrorInvalidArgument);
        }

        var id = Interlocked.Increment(ref _nextConnectionId);
        _connections[id] = new SslConnection(contextId, unchecked((int)ctx[CpuRegister.Rsi]), host);
        TraceSsl("create_connection", id, unchecked((ulong)contextId));
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "HJ1n138CQ2g",
        ExportName = "sceSslDeleteConnection",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslDeleteConnection(CpuContext ctx)
    {
        return _connections.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(SslErrorInvalidId);
    }

    // (connectionId, callback, userArg)
    [SysAbiExport(
        Nid = "E4a-ahM57QQ",
        ExportName = "sceSslSetVerifyCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslSetVerifyCallback(CpuContext ctx)
    {
        if (!_connections.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var connection))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        connection.VerifyCallback = ctx[CpuRegister.Rsi];
        connection.VerifyArgument = ctx[CpuRegister.Rdx];
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "zXvd6iNyfgc",
        ExportName = "sceSslConnect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslConnect(CpuContext ctx) => FailTransport(ctx);

    // (connectionId, buffer, length, flags)
    [SysAbiExport(
        Nid = "jltWpVKtetg",
        ExportName = "sceSslRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslRead(CpuContext ctx) => FailTransport(ctx);

    // (connectionId, buffer, length, flags)
    [SysAbiExport(
        Nid = "iNjkt9Poblw",
        ExportName = "sceSslWrite",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslWrite(CpuContext ctx) => FailTransport(ctx);

    [SysAbiExport(
        Nid = "viRXSHZYd0c",
        ExportName = "sceSslClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslClose(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceSsl("close", id, 0);
        return ctx.SetReturn(0);
    }

    private static int FailTransport(CpuContext ctx)
    {
        return _connections.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi]))
            ? ctx.SetReturn(NetErrorNotConnected)
            : ctx.SetReturn(SslErrorInvalidId);
    }

    // Certificates are not retained, so there is never a list to hand out.
    private static int QueryCertificates(CpuContext ctx, int structureSize)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(SslErrorInvalidArgument);
        }

        Span<byte> zeros = stackalloc byte[structureSize];
        if (!_contexts.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            ctx.Memory.TryWrite(outputAddress, zeros);
            return ctx.SetReturn(SslErrorNotFound);
        }

        ctx.Memory.TryWrite(outputAddress, zeros);
        return ctx.SetReturn(SslErrorNotFound);
    }

    // Nothing is ever allocated for a list, so freeing one only accepts the empty list.
    private static int FreeCertificates(CpuContext ctx, int structureSize)
    {
        var listAddress = ctx[CpuRegister.Rsi];
        if (listAddress == 0)
        {
            return ctx.SetReturn(SslErrorInvalidArgument);
        }

        Span<byte> list = stackalloc byte[structureSize];
        if (!ctx.Memory.TryRead(listAddress, list) || BinaryPrimitives.ReadUInt64LittleEndian(list) != 0)
        {
            return ctx.SetReturn(SslErrorInvalidArgument);
        }

        list.Clear();
        ctx.Memory.TryWrite(listAddress, list);
        return ctx.SetReturn(0);
    }

    private static bool TryReadHost(CpuContext ctx, ulong address, out string host)
    {
        host = string.Empty;
        if (address == 0)
        {
            return false;
        }

        Span<byte> one = stackalloc byte[1];
        var bytes = new List<byte>();
        while (bytes.Count < MaxHostLength)
        {
            if (!ctx.Memory.TryRead(address + (ulong)bytes.Count, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                host = System.Text.Encoding.Latin1.GetString(bytes.ToArray());
                return true;
            }

            bytes.Add(one[0]);
        }

        return false;
    }

    private static void TraceSsl(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SSL"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] ssl.{operation} id={id} arg0=0x{arg0:X16}");
    }
}
