// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;
using System.Buffers;
using SharpEmu.HLE.FileSystem;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Positional and scatter/gather file I/O, synchronization, renaming, fcntl,
/// polling and asynchronous I/O (AIO) that complement the base file exports.
/// These share the same guest FD table and path-resolution helpers via the
/// partial <see cref="KernelMemoryCompatExports"/> class.
/// </summary>
public static partial class KernelMemoryCompatExports
{
    // fcntl commands (FreeBSD numbering used by the PS4/PS5 libc).
    private const int F_DUPFD = 0;
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;

    // poll event bits.
    private const short POLLIN = 0x0001;
    private const short POLLOUT = 0x0004;

    private static Stream? GetOpenFile(int fd)
    {
        lock (_fdGate)
        {
            return _openFiles.TryGetValue(fd, out var stream) ? stream : null;
        }
    }

    // A descriptor is a host file or a read-only file served from a mounted archive.
    private static int ReadOpenFileAt(Stream stream, Span<byte> buffer, long offset) => stream switch
    {
        FileStream file => RandomAccess.Read(file.SafeFileHandle, buffer, offset),
        ZArchiveFileStream archived => archived.ReadAt(buffer, offset),
        _ => throw new IOException("The descriptor does not support positional reads."),
    };

    private static void WriteOpenFileAt(Stream stream, ReadOnlySpan<byte> payload, long offset)
    {
        if (stream is not FileStream file)
        {
            throw new IOException("The descriptor is read-only.");
        }

        RandomAccess.Write(file.SafeFileHandle, payload, offset);
    }

    private static void FlushOpenFileToDisk(Stream stream)
    {
        if (stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
    }

    // ---- Positional read/write (do not move the file offset) ----

    [SysAbiExport(Nid = "ezv-RSBNKqI", ExportName = "pread",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPread(CpuContext ctx)
    {
        var result = KernelPreadCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(Nid = "+r3rMFwItV4", ExportName = "sceKernelPread",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelPread(CpuContext ctx) => KernelPreadCore(ctx);

    private static int KernelPreadCore(CpuContext ctx)
    {
        var profile = FileIoProfile.Begin();
        var outcome = KernelPreadCoreInner(ctx);
        FileIoProfile.EndNoted(
            FileIoProfile.Op.Pread,
            profile,
            bytes: outcome == (int)OrbisGen2Result.ORBIS_GEN2_OK
                ? unchecked((long)ctx[CpuRegister.Rax])
                : 0,
            failed: outcome != (int)OrbisGen2Result.ORBIS_GEN2_OK);
        return outcome;
    }

    private static int KernelPreadCoreInner(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var offset = unchecked((long)ctx[CpuRegister.Rcx]);
        if (requested < 0 || (requested > 0 && bufferAddress == 0) || offset < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        FileIoProfile.NotePath(OpenFileName(stream));

        if (requested == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Titles that stream from .pak archives pread in chunks well past the
        // 85 KiB large-object threshold, so a fresh array per call put every
        // read's staging buffer on the LOH.
        var buffer = ArrayPool<byte>.Shared.Rent(requested);
        try
        {
            int read;
            try
            {
                read = ReadOpenFileAt(stream, buffer.AsSpan(0, requested), offset);
            }
            catch (IOException)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            if (read > 0 && stream is FileStream file)
            {
                // pread never advances the file offset, so the kernel's own
                // read-ahead cannot see that a chunked archive read is
                // sequential; say so explicitly. A file served from a mounted
                // archive has no host handle to advise.
                AdvisePreadReadAhead(fd, file, offset, read);
            }

            SharpEmu.HLE.GuestLoadProgress.NoteFileRead(read);

            if (read > 0 && !ctx.Memory.TryWrite(bufferAddress, buffer.AsSpan(0, read)))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = unchecked((ulong)read);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Per guest descriptor: where the next read would have to start to count as
    // continuing, plus the file length, so deciding whether to hint costs no
    // syscall of its own. Dropped when the descriptor closes.
    private readonly record struct PreadStream(long NextOffset, long Length);

    private static readonly ConcurrentDictionary<int, PreadStream> _preadStreams = new();

    private static void AdvisePreadReadAhead(int fd, FileStream stream, long offset, int read)
    {
        if (!HostReadAhead.Enabled)
        {
            return;
        }

        var completed = offset + read;
        long length;
        bool continuesPreviousRead;
        if (_preadStreams.TryGetValue(fd, out var tracked))
        {
            length = tracked.Length;
            continuesPreviousRead = tracked.NextOffset == offset;
        }
        else
        {
            try
            {
                length = RandomAccess.GetLength(stream.SafeFileHandle);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
                return;
            }

            continuesPreviousRead = false;
        }

        _preadStreams[fd] = new PreadStream(completed, length);
        if (!continuesPreviousRead || completed >= length)
        {
            return;
        }

        HostReadAhead.Advise(
            stream.SafeFileHandle,
            completed,
            (int)Math.Min(HostReadAhead.WindowBytes, length - completed));
    }

    internal static void ForgetPreadStream(int fd) => _preadStreams.TryRemove(fd, out _);

    [SysAbiExport(Nid = "C2kJ-byS5rM", ExportName = "pwrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPwrite(CpuContext ctx)
    {
        var result = KernelPwriteCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(Nid = "nKWi-N2HBV4", ExportName = "sceKernelPwrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelPwrite(CpuContext ctx) => KernelPwriteCore(ctx);

    private static int KernelPwriteCore(CpuContext ctx)
    {
        var profile = FileIoProfile.Begin();
        var outcome = KernelPwriteCoreInner(ctx);
        FileIoProfile.End(
            FileIoProfile.Op.Pwrite,
            profile,
            bytes: outcome == (int)OrbisGen2Result.ORBIS_GEN2_OK
                ? unchecked((long)ctx[CpuRegister.Rax])
                : 0,
            failed: outcome != (int)OrbisGen2Result.ORBIS_GEN2_OK);
        return outcome;
    }

    private static int KernelPwriteCoreInner(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var offset = unchecked((long)ctx[CpuRegister.Rcx]);
        if (requested < 0 || (requested > 0 && bufferAddress == 0) || offset < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (requested == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var payload = ArrayPool<byte>.Shared.Rent(requested);
        try
        {
            if (!ctx.Memory.TryRead(bufferAddress, payload.AsSpan(0, requested)))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            try
            {
                WriteOpenFileAt(stream, payload.AsSpan(0, requested), offset);
            }
            catch (IOException)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            ctx[CpuRegister.Rax] = unchecked((ulong)requested);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    // ---- Synchronization ----

    [SysAbiExport(Nid = "juWbTNM+8hw", ExportName = "fsync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFsync(CpuContext ctx)
    {
        var result = KernelFsyncCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(Nid = "fTx66l5iWIA", ExportName = "sceKernelFsync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFsync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "KIbJFQ0I1Cg", ExportName = "fdatasync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFdatasync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "30Rh4ixbKy4", ExportName = "sceKernelFdatasync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFdatasync(CpuContext ctx) => KernelFsyncCore(ctx);

    private static int KernelFsyncCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            if (fd is 0 or 1 or 2)
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            FlushOpenFileToDisk(stream);
        }
        catch (IOException)
        {
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "Y2OqwJQ3lr8", ExportName = "sync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSync(CpuContext ctx)
    {
        lock (_fdGate)
        {
            foreach (var stream in _openFiles.Values)
            {
                try { FlushOpenFileToDisk(stream); } catch (IOException) { }
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Truncation ----

    [SysAbiExport(Nid = "ih4CD9-gghM", ExportName = "ftruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFtruncate(CpuContext ctx)
    {
        var result = KernelFtruncateCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(Nid = "VW3TVZiM4-E", ExportName = "sceKernelFtruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFtruncate(CpuContext ctx) => KernelFtruncateCore(ctx);

    private static int KernelFtruncateCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var length = unchecked((long)ctx[CpuRegister.Rsi]);
        if (length < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            stream.SetLength(length);
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "ayrtszI7GBg", ExportName = "truncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixTruncate(CpuContext ctx)
    {
        var result = KernelTruncateCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result);
    }

    [SysAbiExport(Nid = "WlyEA-sLDf0", ExportName = "sceKernelTruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelTruncate(CpuContext ctx) => KernelTruncateCore(ctx);

    private static int KernelTruncateCore(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var length = unchecked((long)ctx[CpuRegister.Rsi]);
        if (length < 0 || !TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (string.IsNullOrEmpty(hostPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            using var stream = new FileStream(hostPath, FileMode.Open, FileAccess.Write, GuestFileShare);
            stream.SetLength(length);
        }
        catch (FileNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Rename ----

    [SysAbiExport(Nid = "NN01qLRhiqU", ExportName = "rename",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixRename(CpuContext ctx)
    {
        var result = KernelRenameCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result);
    }

    [SysAbiExport(Nid = "52NcYU9+lEo", ExportName = "sceKernelRename",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelRename(CpuContext ctx) => KernelRenameCore(ctx);

    private static int KernelRenameCore(CpuContext ctx)
    {
        if (!TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rdi], MaxGuestStringLength, out var fromGuest) ||
            !TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rsi], MaxGuestStringLength, out var toGuest))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (IsReadOnlyGuestMutationPath(fromGuest) || IsReadOnlyGuestMutationPath(toGuest))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        var fromHost = ResolveGuestPath(fromGuest);
        var toHost = ResolveGuestPath(toGuest);
        if (string.IsNullOrEmpty(fromHost) || string.IsNullOrEmpty(toHost))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            if (Directory.Exists(fromHost))
            {
                Directory.Move(fromHost, toHost);
            }
            else
            {
                File.Move(fromHost, toHost, overwrite: true);
            }
        }
        catch (FileNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (DirectoryNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        InvalidateNegativeStatCacheForPathAndAncestors(toGuest);
        AddNegativeStatCacheForGuestPath(fromGuest);
        InvalidateAprFileSizeCache(fromHost);
        InvalidateAprFileSizeCache(toHost);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Descriptor duplication ----

    [SysAbiExport(Nid = "iiQjzvfWDq0", ExportName = "dup",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixDup(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(fd, out var stream))
            {
                return PosixFailure(
                    ctx,
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                    notFoundErrno: Ebadf);
            }

            // POSIX dup shares the open file description (and offset), which is
            // exactly the shared FileStream reference.
            var newFd = (int)Interlocked.Increment(ref _nextFileDescriptor);
            _openFiles[newFd] = stream;
            ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "wdUufa9g-D8", ExportName = "dup2",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixDup2(CpuContext ctx)
    {
        var oldFd = unchecked((int)ctx[CpuRegister.Rdi]);
        var newFd = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(oldFd, out var stream))
            {
                return PosixFailure(
                    ctx,
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                    notFoundErrno: Ebadf);
            }

            if (oldFd == newFd)
            {
                ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            // If newFd names an open file, dup2 closes it first.
            if (_openFiles.TryGetValue(newFd, out var existing) && !ReferenceEquals(existing, stream))
            {
                try { existing.Dispose(); } catch (IOException) { }
            }

            _openFiles[newFd] = stream;
            ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- fcntl ----

    [SysAbiExport(Nid = "8nY19bKoiZk", ExportName = "fcntl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFcntl(CpuContext ctx)
    {
        var result = KernelFcntlCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : PosixFailure(ctx, result, notFoundErrno: Ebadf);
    }

    [SysAbiExport(Nid = "SoZkxZkCHaw", ExportName = "sceKernelFcntl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFcntl(CpuContext ctx) => KernelFcntlCore(ctx);

    private static int KernelFcntlCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var command = unchecked((int)ctx[CpuRegister.Rsi]);
        var argument = unchecked((int)ctx[CpuRegister.Rdx]);

        switch (command)
        {
            case F_DUPFD:
                lock (_fdGate)
                {
                    if (!_openFiles.TryGetValue(fd, out var stream))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                    }

                    var newFd = Math.Max((int)Interlocked.Increment(ref _nextFileDescriptor), argument);
                    _openFiles[newFd] = stream;
                    ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
                }

                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            case F_GETFD:
            case F_GETFL:
                // No close-on-exec / status flags are tracked; report cleared.
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            case F_SETFD:
            case F_SETFL:
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            default:
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
    }

    // ---- poll / select ----
    // Regular files are always ready for both read and write, so report every
    // requested descriptor as immediately ready.

    [SysAbiExport(Nid = "ku7D4q1Y9PI", ExportName = "poll",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPoll(CpuContext ctx)
    {
        var fdsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (fdsAddress == 0 || count == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // struct pollfd { int fd; short events; short revents; } == 8 bytes.
        var ready = 0;
        Span<byte> buffer = stackalloc byte[8];
        for (uint i = 0; i < count && i < 4096; i++)
        {
            var entry = fdsAddress + i * 8;
            if (!ctx.Memory.TryRead(entry, buffer))
            {
                break;
            }

            var events = BinaryPrimitives.ReadInt16LittleEndian(buffer[4..]);
            var revents = (short)(events & (POLLIN | POLLOUT));
            BinaryPrimitives.WriteInt16LittleEndian(buffer[6..], revents);
            if (revents != 0)
            {
                ready++;
            }

            _ = ctx.Memory.TryWrite(entry, buffer);
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)ready);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "T8fER+tIGgk", ExportName = "select",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSelect(CpuContext ctx)
    {
        // nfds in Rdi; the fd_sets are left as-is (all reported ready) and the
        // ready count returned is nfds so callers proceed without blocking.
        var nfds = unchecked((int)ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = unchecked((ulong)Math.Max(nfds, 0));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
