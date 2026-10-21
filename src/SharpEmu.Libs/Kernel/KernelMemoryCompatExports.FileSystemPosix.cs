// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// The POSIX file-system calls libkernel exports beside the sceKernel*
/// file API: access, lstat, getcwd, pipe, readv/writev and statfs/fstatfs.
/// Each POSIX entry returns -1 and sets errno; the sceKernel*
/// forms return the SCE code of the same errno.
/// </summary>
public static partial class KernelMemoryCompatExports
{
    // sys/unistd.h access(2) modes.
    private const int AccessExecute = 0x1;
    private const int AccessWrite = 0x2;
    private const int AccessRead = 0x4;

    // sys/sys/syslimits.h IOV_MAX (UIO_MAXIOV).
    internal const int GuestIovMax = 1024;
    private const int IovecSize = 16;

    // sys/sys/mount.h (FreeBSD 11).
    internal const int StatfsSize = 472;
    private const uint StatfsVersion = 0x20030518;
    private const ulong MountReadOnly = 0x1;
    private const ulong MountLocal = 0x1000;
    private const int StatfsFstypeOffset = 280;
    private const int StatfsMountFromOffset = 296;
    private const int StatfsMountOnOffset = 384;
    private const int StatfsNameLength = 88;
    private const int StatfsFstypeLength = 16;

    // S_IFIFO | 0600.
    private const ushort KernelStatModeFifo = 0x1180;

    // Relative guest paths resolve against the game image (ResolveGuestPath),
    // so that is the working directory the process reports.
    internal const string GuestWorkingDirectory = "/app0";

    // ---- access / lstat -----------------------------------------------------

    [SysAbiExport(
        Nid = "8vE6Z6VEYyk",
        ExportName = "access",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixAccess(CpuContext ctx)
    {
        var errno = AccessCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));
        if (errno != 0)
        {
            return KernelErrno.Posix(ctx, errno);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int AccessCore(CpuContext ctx, ulong pathAddress, int mode)
    {
        if ((mode & ~(AccessExecute | AccessWrite | AccessRead)) != 0)
        {
            return OrbisErrno.EINVAL;
        }

        if (pathAddress == 0 || !TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return OrbisErrno.EFAULT;
        }

        if (guestPath.Length == 0)
        {
            return OrbisErrno.ENOENT;
        }

        var lengthErrno = GuestPathLengthErrno(guestPath);
        if (lengthErrno != 0)
        {
            return lengthErrno;
        }

        if (IsRandomDevicePath(guestPath))
        {
            // crw-rw-rw-: readable and writable, never executable.
            return (mode & AccessExecute) != 0 ? OrbisErrno.EACCES : 0;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (string.IsNullOrEmpty(hostPath))
        {
            return OrbisErrno.ENOENT;
        }

        var errno = ProbeHostPath(hostPath, out var isDirectory);
        if (errno != 0)
        {
            return errno;
        }

        if ((mode & AccessWrite) != 0 && IsReadOnlyGuestMutationPath(guestPath))
        {
            return OrbisErrno.EROFS;
        }

        // The synthetic modes are 0755 for directories and 0644 for files
        // (deviations.md, Filesystem); the guest runs as uid 0, for whom
        // FreeBSD's vaccess still wants an execute bit before VEXEC.
        if ((mode & AccessExecute) != 0 && !isDirectory)
        {
            return OrbisErrno.EACCES;
        }

        return 0;
    }

    /// <summary>
    /// Whether a host path exists after following links: 0, ENOENT, ENOTDIR or
    /// ELOOP. Writes nothing to guest memory.
    /// </summary>
    private static int ProbeHostPath(string hostPath, out bool isDirectory)
    {
        isDirectory = false;
        switch (HostDirectoryIndex.Describe(hostPath).Verdict)
        {
            case HostDirectoryIndex.Verdict.Directory:
                isDirectory = true;
                return 0;
            case HostDirectoryIndex.Verdict.File:
                return 0;
            case HostDirectoryIndex.Verdict.Absent when TrustsIndexAbsence(hostPath):
                return OrbisErrno.ENOENT;
        }

        if (HostDirectoryExists(hostPath))
        {
            isDirectory = true;
            return 0;
        }

        if (HostFileExists(hostPath))
        {
            return 0;
        }

        if (IsHostSymlink(hostPath))
        {
            try
            {
                _ = File.ResolveLinkTarget(hostPath, returnFinalTarget: true);
            }
            catch (IOException)
            {
                return OrbisErrno.ELOOP;
            }

            return OrbisErrno.ENOENT;
        }

        return MissingPathErrno(hostPath);
    }

    [SysAbiExport(
        Nid = "DRGXpDDh8Ng",
        ExportName = "lstat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixLstat(CpuContext ctx)
    {
        var result = KernelStatPath(ctx, followLinks: false);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return PosixFailure(ctx, result);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // ---- getcwd -------------------------------------------------------------

    [SysAbiExport(
        Nid = "DYivN1nO-JQ",
        ExportName = "getcwd",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetcwd(CpuContext ctx)
    {
        // char *getcwd(char *buf, size_t size): buf on success, NULL + errno.
        var buffer = ctx[CpuRegister.Rdi];
        var errno = GetcwdCore(ctx, buffer, ctx[CpuRegister.Rsi]);
        if (errno != 0)
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = buffer;
        return 0;
    }

    [SysAbiExport(
        Nid = "Qsxbb+aty0U",
        ExportName = "__getcwd",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetcwdSyscall(CpuContext ctx)
    {
        // The __getcwd(2) system call: 0, or -1 + errno.
        var errno = GetcwdCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        if (errno != 0)
        {
            return KernelErrno.Posix(ctx, errno);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int GetcwdCore(CpuContext ctx, ulong buffer, ulong size)
    {
        // FreeBSD libc mallocs a buffer for getcwd(NULL, 0); there is no guest
        // allocator behind this export, so a NULL buffer is EINVAL here
        // (deviations.md, Filesystem).
        if (buffer == 0 || size == 0)
        {
            return OrbisErrno.EINVAL;
        }

        var bytes = Encoding.UTF8.GetBytes(GuestWorkingDirectory + "\0");
        if (size < (ulong)bytes.Length)
        {
            return OrbisErrno.ERANGE;
        }

        return ctx.Memory.TryWrite(buffer, bytes) ? 0 : OrbisErrno.EFAULT;
    }

    // ---- pipe ---------------------------------------------------------------

    [SysAbiExport(
        Nid = "-Jp7F+pXxNg",
        ExportName = "pipe",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPipe(CpuContext ctx)
    {
        var fildes = ctx[CpuRegister.Rdi];
        if (fildes == 0)
        {
            return KernelErrno.Posix(ctx, OrbisErrno.EFAULT);
        }

        var pipe = new GuestPipe();
        var readEnd = new GuestPipeEndStream(pipe, isReadEnd: true);
        var writeEnd = new GuestPipeEndStream(pipe, isReadEnd: false);
        int readFd;
        int writeFd;
        lock (_fdGate)
        {
            readFd = AllocateGuestFileDescriptor();
            if (readFd < 0)
            {
                return KernelErrno.Posix(ctx, OrbisErrno.EMFILE);
            }

            writeFd = AllocateGuestFileDescriptor();
            if (writeFd < 0)
            {
                ReleaseGuestFileDescriptor(readFd);
                return KernelErrno.Posix(ctx, OrbisErrno.EMFILE);
            }

            _openFiles[readFd] = readEnd;
            _openFiles[writeFd] = writeEnd;
        }

        Span<byte> pair = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pair, readFd);
        BinaryPrimitives.WriteInt32LittleEndian(pair[4..], writeFd);
        if (!ctx.Memory.TryWrite(fildes, pair))
        {
            lock (_fdGate)
            {
                _openFiles.Remove(readFd);
                _openFiles.Remove(writeFd);
                ReleaseGuestFileDescriptor(readFd);
                ReleaseGuestFileDescriptor(writeFd);
            }

            readEnd.Dispose();
            writeEnd.Dispose();
            return KernelErrno.Posix(ctx, OrbisErrno.EFAULT);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // ---- readv / writev -----------------------------------------------------

    [SysAbiExport(
        Nid = "I7ImcLds-uU",
        ExportName = "readv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixReadv(CpuContext ctx) => ReadvWritev(ctx, write: false, KernelAbiFlavor.Posix);

    [SysAbiExport(
        Nid = "+WRlkKjZvag",
        ExportName = "_readv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixReadvUnderscore(CpuContext ctx) => ReadvWritev(ctx, write: false, KernelAbiFlavor.Posix);

    [SysAbiExport(
        Nid = "QqxBetgJH+g",
        ExportName = "sceKernelReadv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelReadv(CpuContext ctx) => ReadvWritev(ctx, write: false, KernelAbiFlavor.Sce);

    [SysAbiExport(
        Nid = "Z2aKdxzS4KE",
        ExportName = "writev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixWritev(CpuContext ctx) => ReadvWritev(ctx, write: true, KernelAbiFlavor.Posix);

    [SysAbiExport(
        Nid = "YSHRBRLn2pI",
        ExportName = "_writev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixWritevUnderscore(CpuContext ctx) => ReadvWritev(ctx, write: true, KernelAbiFlavor.Posix);

    [SysAbiExport(
        Nid = "kAt6VDbHmro",
        ExportName = "sceKernelWritev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWritev(CpuContext ctx) => ReadvWritev(ctx, write: true, KernelAbiFlavor.Sce);

    /// <summary>
    /// readv/writev over the single-buffer read/write cores, one iovec at a
    /// time. A short transfer ends the walk, as the kernel's uio loop does. A
    /// failure after some bytes moved reports those bytes, not the error.
    /// </summary>
    private static int ReadvWritev(CpuContext ctx, bool write, KernelAbiFlavor flavor)
    {
        var fd = ctx[CpuRegister.Rdi];
        var iovAddress = ctx[CpuRegister.Rsi];
        var iovCount = unchecked((int)ctx[CpuRegister.Rdx]);
        if (iovCount < 0 || iovCount > GuestIovMax)
        {
            return KernelErrno.Fail(ctx, flavor, OrbisErrno.EINVAL);
        }

        var iov = new (ulong Base, ulong Length)[iovCount];
        if (iovCount > 0)
        {
            if (iovAddress == 0)
            {
                return KernelErrno.Fail(ctx, flavor, OrbisErrno.EFAULT);
            }

            var raw = new byte[iovCount * IovecSize];
            if (!ctx.Memory.TryRead(iovAddress, raw))
            {
                return KernelErrno.Fail(ctx, flavor, OrbisErrno.EFAULT);
            }

            ulong total = 0;
            for (var i = 0; i < iovCount; i++)
            {
                var entry = raw.AsSpan(i * IovecSize, IovecSize);
                iov[i] = (BinaryPrimitives.ReadUInt64LittleEndian(entry),
                          BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]));
                total += iov[i].Length;
                if (iov[i].Length > long.MaxValue || total > long.MaxValue)
                {
                    return KernelErrno.Fail(ctx, flavor, OrbisErrno.EINVAL);
                }
            }
        }

        var savedRsi = ctx[CpuRegister.Rsi];
        var savedRdx = ctx[CpuRegister.Rdx];
        long transferred = 0;
        var failure = (int)OrbisGen2Result.ORBIS_GEN2_OK;
        try
        {
            foreach (var (bufferBase, length) in iov)
            {
                if (length == 0)
                {
                    continue;
                }

                // The cores take at most int.MaxValue bytes per call.
                var chunk = Math.Min(length, int.MaxValue);
                ctx[CpuRegister.Rdi] = fd;
                ctx[CpuRegister.Rsi] = bufferBase;
                ctx[CpuRegister.Rdx] = chunk;
                var result = write ? KernelWriteUnderscore(ctx) : KernelReadUnderscore(ctx);
                if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
                {
                    failure = result;
                    break;
                }

                var moved = unchecked((long)ctx[CpuRegister.Rax]);
                transferred += moved;
                if ((ulong)moved < length)
                {
                    break;
                }
            }
        }
        finally
        {
            ctx[CpuRegister.Rdi] = fd;
            ctx[CpuRegister.Rsi] = savedRsi;
            ctx[CpuRegister.Rdx] = savedRdx;
        }

        if (failure != (int)OrbisGen2Result.ORBIS_GEN2_OK && transferred == 0)
        {
            var errno = OrbisErrno.ToErrno(failure, OrbisErrno.EIO);
            // The cores still report an unknown fd as NOT_FOUND.
            return KernelErrno.Fail(ctx, flavor, errno == OrbisErrno.ENOENT ? OrbisErrno.EBADF : errno);
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)transferred);
        return flavor == KernelAbiFlavor.Posix ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- statfs / fstatfs ---------------------------------------------------

    [SysAbiExport(
        Nid = "ngZnQWyUvGI",
        ExportName = "statfs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixStatfs(CpuContext ctx) => StatfsPath(ctx, KernelAbiFlavor.Posix);

    [SysAbiExport(
        Nid = "axIRjwFyWKc",
        ExportName = "sceKernelStatfs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelStatfs(CpuContext ctx) => StatfsPath(ctx, KernelAbiFlavor.Sce);

    [SysAbiExport(
        Nid = "+rWFNYiNaEM",
        ExportName = "fstatfs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFstatfs(CpuContext ctx) => StatfsDescriptor(ctx, KernelAbiFlavor.Posix);

    [SysAbiExport(
        Nid = "gWzdVBoAIU0",
        ExportName = "sceKernelFstatfs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelFstatfs(CpuContext ctx) => StatfsDescriptor(ctx, KernelAbiFlavor.Sce);

    private static int StatfsPath(CpuContext ctx, KernelAbiFlavor flavor)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        if (pathAddress == 0 || bufferAddress == 0 ||
            !TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return KernelErrno.Fail(ctx, flavor, OrbisErrno.EFAULT);
        }

        if (guestPath.Length == 0)
        {
            return KernelErrno.Fail(ctx, flavor, OrbisErrno.ENOENT);
        }

        var lengthErrno = GuestPathLengthErrno(guestPath);
        if (lengthErrno != 0)
        {
            return KernelErrno.Fail(ctx, flavor, lengthErrno);
        }

        GuestMount mount;
        if (IsRandomDevicePath(guestPath))
        {
            mount = GuestMount.DeviceFileSystem;
        }
        else
        {
            var hostPath = ResolveGuestPath(guestPath);
            if (string.IsNullOrEmpty(hostPath))
            {
                return KernelErrno.Fail(ctx, flavor, OrbisErrno.ENOENT);
            }

            var errno = ProbeHostPath(hostPath, out _);
            if (errno != 0)
            {
                return KernelErrno.Fail(ctx, flavor, errno);
            }

            mount = DescribeGuestMount(guestPath, hostPath);
        }

        return WriteStatfsResult(ctx, flavor, bufferAddress, mount);
    }

    private static int StatfsDescriptor(CpuContext ctx, KernelAbiFlavor flavor)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        if (bufferAddress == 0)
        {
            return KernelErrno.Fail(ctx, flavor, OrbisErrno.EFAULT);
        }

        string? hostPath = null;
        var isDevice = fd is 0 or 1 or 2;
        var isPipe = false;
        lock (_fdGate)
        {
            if (_randomDeviceDescriptors.Contains(fd))
            {
                isDevice = true;
            }
            else if (_openDirectories.TryGetValue(fd, out var directory))
            {
                hostPath = directory.Path;
            }
            else if (_openFiles.TryGetValue(fd, out var stream))
            {
                isPipe = stream is GuestPipeEndStream;
                hostPath = OpenFileName(stream);
            }
        }

        GuestMount mount;
        if (isDevice)
        {
            mount = GuestMount.DeviceFileSystem;
        }
        else if (isPipe)
        {
            // A pipe has no vnode: kern_fstatfs's getvnode refuses it.
            return KernelErrno.Fail(ctx, flavor, OrbisErrno.EINVAL);
        }
        else if (!string.IsNullOrEmpty(hostPath))
        {
            mount = DescribeGuestMount(guestPath: null, hostPath);
        }
        else
        {
            return KernelErrno.Fail(ctx, flavor, OrbisErrno.EBADF);
        }

        return WriteStatfsResult(ctx, flavor, bufferAddress, mount);
    }

    private static int WriteStatfsResult(CpuContext ctx, KernelAbiFlavor flavor, ulong bufferAddress, GuestMount mount)
    {
        if (!ctx.Memory.TryWrite(bufferAddress, BuildStatfs(mount)))
        {
            return KernelErrno.Fail(ctx, flavor, OrbisErrno.EFAULT);
        }

        ctx[CpuRegister.Rax] = 0;
        return flavor == KernelAbiFlavor.Posix ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal readonly record struct GuestMount(string MountOn, string FileSystemType, bool ReadOnly, string? HostRoot)
    {
        public static GuestMount DeviceFileSystem => new("/dev", "devfs", ReadOnly: false, HostRoot: null);
    }

    /// <summary>
    /// The guest mount a path lives on. The guest path decides when there is
    /// one; an fd only has its host path, which is matched against the mount
    /// roots. File-system type names are synthetic (deviations.md).
    /// </summary>
    internal static GuestMount DescribeGuestMount(string? guestPath, string hostPath)
    {
        if (guestPath is not null)
        {
            var normalized = NormalizeGuestStatCachePath(guestPath) ?? guestPath;
            if (!normalized.StartsWith('/') && !normalized.StartsWith("$/", StringComparison.Ordinal) && normalized != "$")
            {
                normalized = GuestWorkingDirectory + "/" + normalized;
            }

            var mountOn = GuestMountPointOf(normalized);
            return CreateGuestMount(mountOn, ResolveGuestPath(mountOn));
        }

        foreach (var (mountOn, root) in EnumerateGuestMountRoots())
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var trimmed = Path.TrimEndingDirectorySeparator(root);
            if (hostPath.Equals(trimmed, HostFsPath.Comparison) ||
                hostPath.StartsWith(trimmed + Path.DirectorySeparatorChar, HostFsPath.Comparison))
            {
                return CreateGuestMount(mountOn, root);
            }
        }

        return CreateGuestMount("/", Path.GetPathRoot(hostPath));
    }

    private static string GuestMountPointOf(string normalizedGuestPath)
    {
        if (normalizedGuestPath == "$" || normalizedGuestPath.StartsWith("$/", StringComparison.Ordinal))
        {
            return "/app0";
        }

        lock (_guestMountGate)
        {
            string? best = null;
            foreach (var mount in _guestMounts.Keys)
            {
                if ((normalizedGuestPath == mount || normalizedGuestPath.StartsWith(mount + "/", StringComparison.Ordinal)) &&
                    (best is null || mount.Length > best.Length))
                {
                    best = mount;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        if (normalizedGuestPath == "/devlog/app" || normalizedGuestPath.StartsWith("/devlog/app/", StringComparison.Ordinal))
        {
            return "/devlog/app";
        }

        var end = normalizedGuestPath.IndexOf('/', 1);
        return end < 0 ? normalizedGuestPath : normalizedGuestPath[..end];
    }

    private static IEnumerable<(string MountOn, string? Root)> EnumerateGuestMountRoots()
    {
        List<(string, string?)> roots;
        lock (_guestMountGate)
        {
            roots = _guestMounts.Select(pair => (pair.Key, (string?)pair.Value)).ToList();
        }

        roots.Add(("/app0", ResolveApp0Root()));
        roots.Add(("/hostapp", ResolveHostappRoot()));
        roots.Add(("/temp0", ResolveTemp0Root()));
        roots.Add(("/download0", ResolveDownload0Root()));
        roots.Add(("/devlog/app", ResolveDevlogAppRoot()));
        // Longest root first, so a mount nested in another wins.
        return roots.OrderByDescending(entry => entry.Item2?.Length ?? 0);
    }

    private static GuestMount CreateGuestMount(string mountOn, string? hostRoot)
    {
        // Matches IsReadOnlyGuestMutationPath: only the game image refuses writes.
        var readOnly = !_writableApp0 && mountOn == "/app0";
        var type = mountOn is "/app0" or "/hostapp" ? "pfs" : "exfatfs";
        return new GuestMount(mountOn, type, readOnly, string.IsNullOrEmpty(hostRoot) ? null : hostRoot);
    }

    internal static byte[] BuildStatfs(GuestMount mount)
    {
        const ulong blockSize = 0x10000;
        ulong totalBlocks = 0;
        ulong freeBlocks = 0;
        if (mount.HostRoot is not null && !mount.ReadOnly)
        {
            try
            {
                var drive = new DriveInfo(mount.HostRoot);
                totalBlocks = (ulong)drive.TotalSize / blockSize;
                freeBlocks = (ulong)drive.AvailableFreeSpace / blockSize;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
            }
        }
        else if (mount.HostRoot is not null)
        {
            // The game image is a sealed read-only package: no free space.
            try
            {
                totalBlocks = (ulong)new DriveInfo(mount.HostRoot).TotalSize / blockSize;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
            }
        }

        var buffer = new byte[StatfsSize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, StatfsVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], StableFileSystemTypeId(mount.FileSystemType));
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], MountLocal | (mount.ReadOnly ? MountReadOnly : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], totalBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], freeBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(span[48..], (long)freeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(span[184..], (uint)GuestNameMax);
        var deviceId = ComputeDeviceId(mount.HostRoot ?? mount.MountOn);
        BinaryPrimitives.WriteUInt32LittleEndian(span[192..], deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[196..], StableFileSystemTypeId(mount.FileSystemType));
        WriteFixedString(span.Slice(StatfsFstypeOffset, StatfsFstypeLength), mount.FileSystemType);
        WriteFixedString(span.Slice(StatfsMountFromOffset, StatfsNameLength), mount.FileSystemType);
        WriteFixedString(span.Slice(StatfsMountOnOffset, StatfsNameLength), mount.MountOn);
        return buffer;
    }

    private static uint StableFileSystemTypeId(string type) => type switch
    {
        "devfs" => 0x71,
        "pfs" => 0x80,
        _ => 0x81,
    };

    private static void WriteFixedString(Span<byte> destination, string value)
    {
        destination.Clear();
        var count = Encoding.UTF8.GetBytes(value.AsSpan(), destination[..^1]);
        destination[count] = 0;
    }

    // ---- pipe streams -------------------------------------------------------

    /// <summary>
    /// The buffer behind one pipe(2). Writes never block: the buffer grows
    /// instead of stopping at the kernel's pipe capacity (deviations.md). A
    /// read blocks until data arrives or every write end is closed.
    /// </summary>
    internal sealed class GuestPipe
    {
        private readonly object _gate = new();
        private byte[] _buffer = new byte[4096];
        private int _head;
        private int _count;
        private bool _readClosed;
        private bool _writeClosed;

        public int Read(Span<byte> destination)
        {
            lock (_gate)
            {
                while (_count == 0 && !_writeClosed)
                {
                    Monitor.Wait(_gate);
                }

                var take = Math.Min(destination.Length, _count);
                var first = Math.Min(take, _buffer.Length - _head);
                _buffer.AsSpan(_head, first).CopyTo(destination);
                _buffer.AsSpan(0, take - first).CopyTo(destination[first..]);
                _head = (_head + take) % _buffer.Length;
                _count -= take;
                return take;
            }
        }

        public void Write(ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (_readClosed)
                {
                    throw new IOException("Broken pipe", 32);
                }

                if (_count + source.Length > _buffer.Length)
                {
                    var grown = new byte[Math.Max(_buffer.Length * 2, _count + source.Length)];
                    var first = Math.Min(_count, _buffer.Length - _head);
                    _buffer.AsSpan(_head, first).CopyTo(grown);
                    _buffer.AsSpan(0, _count - first).CopyTo(grown.AsSpan(first));
                    _buffer = grown;
                    _head = 0;
                }

                var tail = (_head + _count) % _buffer.Length;
                var firstPart = Math.Min(source.Length, _buffer.Length - tail);
                source[..firstPart].CopyTo(_buffer.AsSpan(tail));
                source[firstPart..].CopyTo(_buffer);
                _count += source.Length;
                Monitor.PulseAll(_gate);
            }
        }

        public void CloseEnd(bool readEnd)
        {
            lock (_gate)
            {
                if (readEnd)
                {
                    _readClosed = true;
                }
                else
                {
                    _writeClosed = true;
                }

                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>One end of a <see cref="GuestPipe"/>, held in the fd table.</summary>
    internal sealed class GuestPipeEndStream : Stream
    {
        private readonly GuestPipe _pipe;
        private readonly bool _isReadEnd;
        private bool _closed;
        private long _transferred;

        public GuestPipeEndStream(GuestPipe pipe, bool isReadEnd)
        {
            _pipe = pipe;
            _isReadEnd = isReadEnd;
        }

        public override bool CanRead => _isReadEnd && !_closed;
        public override bool CanSeek => false;
        public override bool CanWrite => !_isReadEnd && !_closed;
        public override long Length => throw new NotSupportedException();

        // The I/O cores trace the offset around each transfer; a pipe has none,
        // so this is the byte count moved through this end.
        public override long Position
        {
            get => _transferred;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_isReadEnd)
            {
                throw new NotSupportedException();
            }

            var read = _pipe.Read(buffer.AsSpan(offset, count));
            _transferred += read;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_isReadEnd)
            {
                throw new NotSupportedException();
            }

            _pipe.Write(buffer.AsSpan(offset, count));
            _transferred += count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                _pipe.CloseEnd(_isReadEnd);
            }

            base.Dispose(disposing);
        }
    }
}
