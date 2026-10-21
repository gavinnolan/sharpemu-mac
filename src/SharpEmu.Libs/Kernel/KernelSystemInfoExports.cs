// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Process identity, system information and the sysctl tree (SPEC §5.1).
/// </summary>
public static class KernelSystemInfoExports
{
    // ---- sysctl -----------------------------------------------------------

    private const int CtlMaxName = 24; // sys/sys/sysctl.h CTL_MAXNAME
    private const int CtlSysctl = 0;
    private const int CtlSysctlName2Oid = 3;
    private const int CtlKern = 1;
    private const int CtlVm = 2;
    private const int CtlHw = 6;
    private const int CtlMachdep = 7;

    // FreeBSD 11 KERN_* / HW_* numbers (sys/sys/sysctl.h).
    private const int KernOstype = 1;
    private const int KernOsrelease = 2;
    private const int KernVersion = 4;
    private const int KernHostname = 10;
    private const int KernProc = 14;
    private const int KernProcPathname = 12;
    private const int KernOsreldate = 24;
    private const int KernArandom = 37;
    private const int HwMachine = 1;
    private const int HwNcpu = 3;
    private const int HwByteorder = 4;
    private const int HwPhysmem = 5;
    private const int HwPagesize = 7;
    private const int HwRealmem = 12;

    // Nodes FreeBSD registers with OID_AUTO get numbers from 0x100 upward at
    // boot; titles reach them only by name (sysctlbyname / name2oid), so any
    // stable number in that range is faithful.
    private const int OidAutoBase = 0x100;

    /// <summary>App-visible CPUs (SPEC §0.2, CPUMASK_13CPU).</summary>
    internal const int AppVisibleCpuCount = 13;

    /// <summary>Page size (SPEC §0.2, SCE_KERNEL_PAGE_SIZE).</summary>
    internal const int GuestPageSize = 16384;

    /// <summary>PS5 physical memory: 16 GiB of GDDR6.</summary>
    internal const ulong ConsolePhysicalMemory = 16UL << 30;

    private const string ProcessPathname = "/app0/eboot.bin";

    private delegate int SysctlReader(CpuContext ctx, int[] mib, out byte[] value);

    private sealed record SysctlNode(string Name, int[] Mib, SysctlReader Read);

    private static readonly SysctlNode[] SysctlNodes =
    [
        Leaf("kern.ostype", [CtlKern, KernOstype], _ => CString("FreeBSD")),
        Leaf("kern.osrelease", [CtlKern, KernOsrelease], _ => CString("11.0-RELEASE")),
        Leaf("kern.version", [CtlKern, KernVersion], _ => CString("FreeBSD 11.0-RELEASE\n")),
        Leaf("kern.hostname", [CtlKern, KernHostname], _ => CString("ps5")),
        Leaf("kern.osreldate", [CtlKern, KernOsreldate], _ => Int32(1100122)),
        new("kern.arandom", [CtlKern, KernArandom], ReadRandom),
        new("kern.proc.pathname", [CtlKern, KernProc, KernProcPathname], ReadProcPathname),
        new("kern.sdk_version", [CtlKern, OidAutoBase], ReadSdkVersion),
        Leaf("hw.machine", [CtlHw, HwMachine], _ => CString("amd64")),
        Leaf("hw.ncpu", [CtlHw, HwNcpu], _ => Int32(AppVisibleCpuCount)),
        Leaf("hw.byteorder", [CtlHw, HwByteorder], _ => Int32(1234)),
        Leaf("hw.physmem", [CtlHw, HwPhysmem], _ => UInt64(ConsolePhysicalMemory)),
        Leaf("hw.pagesize", [CtlHw, HwPagesize], _ => Int32(GuestPageSize)),
        Leaf("hw.realmem", [CtlHw, HwRealmem], _ => UInt64(ConsolePhysicalMemory)),
        Leaf("machdep.tsc_freq", [CtlMachdep, OidAutoBase], _ => UInt64(KernelRuntimeCompatExports.TscFrequency)),
        Leaf("vm.overcommit", [CtlVm, OidAutoBase], _ => Int32(0)),
    ];

    private static SysctlNode Leaf(string name, int[] mib, Func<CpuContext, byte[]> value) =>
        new(name, mib, (CpuContext ctx, int[] _, out byte[] bytes) =>
        {
            bytes = value(ctx);
            return 0;
        });

    private static byte[] CString(string value) => Encoding.ASCII.GetBytes(value + "\0");

    private static byte[] Int32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt64(ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static int ReadRandom(CpuContext ctx, int[] mib, out byte[] value)
    {
        // kern.arandom returns at most 256 bytes per call, as many as asked.
        value = RandomNumberGenerator.GetBytes(256);
        return 0;
    }

    private static int ReadProcPathname(CpuContext ctx, int[] mib, out byte[] value)
    {
        value = [];
        // kern.proc.pathname.<pid>: -1 means the caller.
        if (mib.Length != 4)
        {
            return OrbisErrno.EINVAL;
        }

        if (mib[3] != -1 && mib[3] != KernelRuntimeCompatExports.GuestProcessId)
        {
            return OrbisErrno.ESRCH;
        }

        value = CString(ProcessPathname);
        return 0;
    }

    private static int ReadSdkVersion(CpuContext ctx, int[] mib, out byte[] value)
    {
        value = [];
        if (!KernelRuntimeCompatExports.TryGetProcessSdkVersion(ctx, out var sdkVersion))
        {
            return OrbisErrno.ENOENT;
        }

        value = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, sdkVersion);
        return 0;
    }

    private static SysctlNode? FindNode(int[] mib)
    {
        foreach (var node in SysctlNodes)
        {
            // kern.proc.pathname takes the pid as a trailing name component;
            // every other node is a leaf matched exactly.
            var lengthMismatch = node.Name == "kern.proc.pathname"
                ? mib.Length < node.Mib.Length
                : mib.Length != node.Mib.Length;
            if (lengthMismatch)
            {
                continue;
            }

            if (mib.AsSpan(0, node.Mib.Length).SequenceEqual(node.Mib))
            {
                return node;
            }
        }

        return null;
    }

    private static SysctlNode? FindNode(string name)
    {
        foreach (var node in SysctlNodes)
        {
            if (string.Equals(node.Name, name, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// The sysctl core: errno for the MIB, with FreeBSD old/new semantics.
    /// </summary>
    internal static int Sysctl(
        CpuContext ctx,
        int[] mib,
        ulong oldAddress,
        ulong oldLengthAddress,
        ulong newAddress,
        ulong newLength)
    {
        if (mib.Length < 2 || mib.Length > CtlMaxName)
        {
            return OrbisErrno.EINVAL;
        }

        if (mib[0] == CtlSysctl && mib[1] == CtlSysctlName2Oid && mib.Length == 2)
        {
            return Name2Oid(ctx, oldAddress, oldLengthAddress, newAddress, newLength);
        }

        var node = FindNode(mib);
        if (node is null)
        {
            return OrbisErrno.ENOENT;
        }

        if (newAddress != 0)
        {
            return OrbisErrno.EPERM;
        }

        var errno = node.Read(ctx, mib, out var value);
        if (errno != 0)
        {
            return errno;
        }

        if (node.Name == "kern.arandom" && oldLengthAddress != 0 &&
            ctx.TryReadUInt64(oldLengthAddress, out var requested))
        {
            value = value.AsSpan(0, (int)Math.Min((ulong)value.Length, requested)).ToArray();
        }

        return CopyOut(ctx, value, oldAddress, oldLengthAddress);
    }

    private static int CopyOut(CpuContext ctx, byte[] value, ulong oldAddress, ulong oldLengthAddress)
    {
        if (oldAddress == 0)
        {
            // Size probe: report the full length.
            if (oldLengthAddress != 0 && !ctx.TryWriteUInt64(oldLengthAddress, (ulong)value.Length))
            {
                return OrbisErrno.EFAULT;
            }

            return 0;
        }

        if (oldLengthAddress == 0 || !ctx.TryReadUInt64(oldLengthAddress, out var capacity))
        {
            return OrbisErrno.EFAULT;
        }

        var copied = (int)Math.Min(capacity, (ulong)value.Length);
        if (!ctx.Memory.TryWrite(oldAddress, value.AsSpan(0, copied)) ||
            !ctx.TryWriteUInt64(oldLengthAddress, (ulong)copied))
        {
            return OrbisErrno.EFAULT;
        }

        return copied < value.Length ? OrbisErrno.ENOMEM : 0;
    }

    // sysctl {0, 3}: new = the dotted name, old = the int MIB.
    private static int Name2Oid(
        CpuContext ctx,
        ulong oldAddress,
        ulong oldLengthAddress,
        ulong newAddress,
        ulong newLength)
    {
        if (newAddress == 0 || newLength == 0 || newLength > 1024)
        {
            return OrbisErrno.EINVAL;
        }

        var nameBytes = new byte[newLength];
        if (!ctx.Memory.TryRead(newAddress, nameBytes))
        {
            return OrbisErrno.EFAULT;
        }

        var terminator = Array.IndexOf(nameBytes, (byte)0);
        var name = Encoding.ASCII.GetString(nameBytes, 0, terminator >= 0 ? terminator : nameBytes.Length);
        var node = FindNode(name);
        if (node is null)
        {
            return OrbisErrno.ENOENT;
        }

        var oid = new byte[node.Mib.Length * sizeof(int)];
        for (var i = 0; i < node.Mib.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(oid.AsSpan(i * sizeof(int)), node.Mib[i]);
        }

        return CopyOut(ctx, oid, oldAddress, oldLengthAddress);
    }

    private static int SysctlByName(
        CpuContext ctx,
        string name,
        ulong oldAddress,
        ulong oldLengthAddress,
        ulong newAddress,
        ulong newLength)
    {
        var node = FindNode(name);
        if (node is null)
        {
            return OrbisErrno.ENOENT;
        }

        var mib = node.Name == "kern.proc.pathname"
            ? [.. node.Mib, -1]
            : node.Mib;
        return Sysctl(ctx, mib, oldAddress, oldLengthAddress, newAddress, newLength);
    }

    [SysAbiExport(
        Nid = "DFmMT80xcNI",
        ExportName = "sysctl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSysctl(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var nameLength = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (nameLength < 2 || nameLength > CtlMaxName)
        {
            return KernelErrno.Posix(ctx, OrbisErrno.EINVAL);
        }

        var raw = new byte[nameLength * sizeof(int)];
        if (nameAddress == 0 || !ctx.Memory.TryRead(nameAddress, raw))
        {
            return KernelErrno.Posix(ctx, OrbisErrno.EFAULT);
        }

        var mib = new int[nameLength];
        for (var i = 0; i < mib.Length; i++)
        {
            mib[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * sizeof(int)));
        }

        var errno = Sysctl(
            ctx,
            mib,
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            ctx[CpuRegister.R9]);
        return Finish(ctx, errno);
    }

    [SysAbiExport(
        Nid = "MhC53TKmjVA",
        ExportName = "sysctlbyname",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSysctlByName(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        if (nameAddress == 0 || !TryReadCString(ctx, nameAddress, 256, out var name))
        {
            return KernelErrno.Posix(ctx, OrbisErrno.EFAULT);
        }

        var errno = SysctlByName(
            ctx,
            name,
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8]);
        return Finish(ctx, errno);
    }

    private static int Finish(CpuContext ctx, int errno)
    {
        if (errno != 0)
        {
            return KernelErrno.Posix(ctx, errno);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static bool TryReadCString(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        var bytes = new List<byte>();
        Span<byte> one = stackalloc byte[1];
        for (var i = 0; i < maxLength; i++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)i, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes.ToArray());
                return true;
            }

            bytes.Add(one[0]);
        }

        return false;
    }

    // ---- process identity -------------------------------------------------

    [SysAbiExport(
        Nid = "e6ovBo9ZvJc",
        ExportName = "getppid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int GetParentProcessId(CpuContext ctx) =>
        ReturnInt(ctx, KernelRuntimeCompatExports.GuestParentProcessId);

    // A title runs as uid/gid 0 inside its sandbox (anyps5 Process.cpp agrees).
    [SysAbiExport(
        Nid = "kg4x8Prhfxw",
        ExportName = "getuid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int GetUserId(CpuContext ctx) => ReturnInt(ctx, 0);

    [SysAbiExport(
        Nid = "tvpHe5kBO4E",
        ExportName = "geteuid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int GetEffectiveUserId(CpuContext ctx) => ReturnInt(ctx, 0);

    [SysAbiExport(
        Nid = "AfuS23bX6kg",
        ExportName = "getgid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int GetGroupId(CpuContext ctx) => ReturnInt(ctx, 0);

    [SysAbiExport(
        Nid = "72rYuYoDTWk",
        ExportName = "getegid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int GetEffectiveGroupId(CpuContext ctx) => ReturnInt(ctx, 0);

    private static int ReturnInt(CpuContext ctx, int value)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)value);
        return value;
    }

    // ---- system information -----------------------------------------------

    // SceKernelSystemSwVersion: u64 size; char versionString[0x1C]; u32 version.
    private const int SystemSwVersionSize = 0x28;
    private const int SystemSwVersionStringOffset = 0x08;
    private const int SystemSwVersionStringLength = 0x1C;
    private const int SystemSwVersionValueOffset = 0x24;
    internal const uint SystemSoftwareVersion = 0x01000000;
    private const string SystemSoftwareVersionString = "01.000.000";

    [SysAbiExport(
        Nid = "Mv1zUObHvXI",
        ExportName = "sceKernelGetSystemSwVersion",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetSystemSwVersion(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return KernelErrno.Sce(OrbisErrno.EFAULT);
        }

        var buffer = new byte[SystemSwVersionSize];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, SystemSwVersionSize);
        Encoding.ASCII.GetBytes(SystemSoftwareVersionString)
            .CopyTo(buffer.AsSpan(SystemSwVersionStringOffset, SystemSwVersionStringLength));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(SystemSwVersionValueOffset), SystemSoftwareVersion);
        if (!ctx.Memory.TryWrite(address, buffer))
        {
            return KernelErrno.Sce(OrbisErrno.EFAULT);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // SPEC §5.1: a retail console is never in development mode.
    [SysAbiExport(
        Nid = "UtO0OHMCgmI",
        ExportName = "sceKernelIsDevelopmentMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsDevelopmentMode(CpuContext ctx) => ReturnInt(ctx, 0);

    // 0: the title runs in the normal (non-boost, non-legacy) CPU mode.
    [SysAbiExport(
        Nid = "VOx8NGmHXTs",
        ExportName = "sceKernelGetCpumode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetCpumode(CpuContext ctx) => ReturnInt(ctx, 0);

    [ThreadStatic]
    private static int _currentCpuPlusOne;

    private static int _nextCurrentCpu;

    /// <summary>
    /// A guest CPU index 0..12, stable for the calling host thread. Host
    /// threads are spread round-robin as they first ask (SPEC §5.2).
    /// </summary>
    internal static int GetCurrentCpu()
    {
        if (_currentCpuPlusOne == 0)
        {
            var next = Interlocked.Increment(ref _nextCurrentCpu) - 1;
            _currentCpuPlusOne = (int)((uint)next % AppVisibleCpuCount) + 1;
        }

        return _currentCpuPlusOne - 1;
    }

    [SysAbiExport(
        Nid = "g0VTBxfJyu0",
        ExportName = "sceKernelGetCurrentCpu",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetCurrentCpu(CpuContext ctx) => ReturnInt(ctx, GetCurrentCpu());

    // ---- thread names -----------------------------------------------------

    // thr_set_name / thr_get_name keep a name per kernel thread id
    // (pthread_getthreadid_np). MAXCOMLEN-style bound: 32 bytes with NUL.
    private const int ThreadNameMaxBytes = 32;
    private static readonly ConcurrentDictionary<ulong, string> _threadNames = new();

    [SysAbiExport(
        Nid = "yyZNlNyY6og",
        ExportName = "sceKernelSetThreadName",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetThreadName(CpuContext ctx)
    {
        var threadId = ResolveThreadId(ctx[CpuRegister.Rdi]);
        var nameAddress = ctx[CpuRegister.Rsi];
        if (nameAddress == 0)
        {
            return KernelErrno.Sce(OrbisErrno.EFAULT);
        }

        if (!TryReadCString(ctx, nameAddress, 4096, out var name))
        {
            return KernelErrno.Sce(OrbisErrno.EFAULT);
        }

        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length >= ThreadNameMaxBytes)
        {
            name = Encoding.UTF8.GetString(bytes, 0, ThreadNameMaxBytes - 1);
        }

        _threadNames[threadId] = name;
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "4vkZwDJbMx0",
        ExportName = "sceKernelGetThreadName",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetThreadName(CpuContext ctx)
    {
        var threadId = ResolveThreadId(ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return KernelErrno.Sce(OrbisErrno.EFAULT);
        }

        if (!_threadNames.TryGetValue(threadId, out var name))
        {
            if (threadId != KernelPthreadState.GetCurrentThreadUniqueId())
            {
                return KernelErrno.Sce(OrbisErrno.ESRCH);
            }

            name = string.Empty;
        }

        var buffer = new byte[ThreadNameMaxBytes];
        var encoded = Encoding.UTF8.GetBytes(name);
        encoded.AsSpan(0, Math.Min(encoded.Length, ThreadNameMaxBytes - 1)).CopyTo(buffer);
        if (!ctx.Memory.TryWrite(outputAddress, buffer))
        {
            return KernelErrno.Sce(OrbisErrno.EFAULT);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // An id of 0 or -1 names the calling thread.
    private static ulong ResolveThreadId(ulong raw)
    {
        var id = unchecked((long)raw);
        return id is 0 or -1 ? KernelPthreadState.GetCurrentThreadUniqueId() : raw;
    }

    // ---- getrusage --------------------------------------------------------

    private const int RusageSelf = 0;
    private const int RusageChildren = -1;
    private const int RusageThread = 1;

    // FreeBSD amd64 struct rusage: two timevals then fourteen longs.
    internal const int RusageSize = 2 * 16 + 14 * 8;

    [SysAbiExport(
        Nid = "hHlZQUnlxSM",
        ExportName = "getrusage",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetrusage(CpuContext ctx)
    {
        var who = unchecked((int)ctx[CpuRegister.Rdi]);
        var address = ctx[CpuRegister.Rsi];
        var buffer = new byte[RusageSize];
        switch (who)
        {
            case RusageChildren:
                break; // a title has no reaped children
            case RusageSelf:
            {
                using var process = Process.GetCurrentProcess();
                WriteTimeval(buffer.AsSpan(0), process.UserProcessorTime.Ticks / 10);
                WriteTimeval(buffer.AsSpan(16), process.PrivilegedProcessorTime.Ticks / 10);
                // ru_maxrss is in KiB.
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), process.PeakWorkingSet64 / 1024);
                break;
            }

            case RusageThread:
                KernelClocks.TryGetTime(KernelClocks.ThreadCputime, out var seconds, out var nanoseconds);
                WriteTimeval(buffer.AsSpan(0), seconds * 1_000_000 + nanoseconds / 1000);
                break;
            default:
                return KernelErrno.Posix(ctx, OrbisErrno.EINVAL);
        }

        if (address == 0 || !ctx.Memory.TryWrite(address, buffer))
        {
            return KernelErrno.Posix(ctx, OrbisErrno.EFAULT);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static void WriteTimeval(Span<byte> destination, long microseconds)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, microseconds / 1_000_000);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], microseconds % 1_000_000);
    }

    // ---- sigaction --------------------------------------------------------

    // FreeBSD amd64 struct sigaction: handler (8), int sa_flags, sigset_t
    // sa_mask (4 x u32) — 28 bytes, padded to 32.
    internal const int SigactionSize = 32;
    private const int MaxSignal = 128; // _SIG_MAXSIG
    private const int Sigkill = 9;
    private const int Sigstop = 17;
    private const ulong SigDfl = 0;
    private static readonly object _sigactionGate = new();
    private static readonly byte[][] _sigactions = new byte[MaxSignal + 1][];

    [SysAbiExport(
        Nid = "KiJEPEWRyUY",
        ExportName = "sigaction",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSigaction(CpuContext ctx)
    {
        var signal = unchecked((int)ctx[CpuRegister.Rdi]);
        var actionAddress = ctx[CpuRegister.Rsi];
        var previousAddress = ctx[CpuRegister.Rdx];
        if (signal < 1 || signal > MaxSignal)
        {
            return KernelErrno.Posix(ctx, OrbisErrno.EINVAL);
        }

        byte[]? action = null;
        if (actionAddress != 0)
        {
            action = new byte[SigactionSize];
            if (!ctx.Memory.TryRead(actionAddress, action.AsSpan(0, 28)))
            {
                return KernelErrno.Posix(ctx, OrbisErrno.EFAULT);
            }

            // SIGKILL and SIGSTOP can't be caught or ignored.
            if (signal is Sigkill or Sigstop &&
                BinaryPrimitives.ReadUInt64LittleEndian(action) != SigDfl)
            {
                return KernelErrno.Posix(ctx, OrbisErrno.EINVAL);
            }
        }

        lock (_sigactionGate)
        {
            var previous = _sigactions[signal] ?? new byte[SigactionSize];
            if (previousAddress != 0 && !ctx.Memory.TryWrite(previousAddress, previous.AsSpan(0, 28)))
            {
                return KernelErrno.Posix(ctx, OrbisErrno.EFAULT);
            }

            if (action is not null)
            {
                _sigactions[signal] = action;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "UDCI-WazohQ",
        ExportName = "_sigaction",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSigactionUnderscore(CpuContext ctx) => PosixSigaction(ctx);

    /// <summary>The registered handler for <paramref name="signal"/>, 0 for SIG_DFL.</summary>
    internal static ulong GetSignalHandler(int signal)
    {
        lock (_sigactionGate)
        {
            return signal is >= 1 and <= MaxSignal && _sigactions[signal] is { } action
                ? BinaryPrimitives.ReadUInt64LittleEndian(action)
                : SigDfl;
        }
    }

    internal static void ResetSignalActionsForTests()
    {
        lock (_sigactionGate)
        {
            Array.Clear(_sigactions);
        }
    }

    // ---- memory residency -------------------------------------------------

    private const int MsAsync = 0x1;
    private const int MsInvalidate = 0x2;
    private const int MsValidFlags = MsAsync | MsInvalidate; // MS_SYNC is 0

    // Host memory backing guest pages is never paged out by the guest's
    // view and is coherent with every host reader, so mlock/munlock/msync
    // validate their arguments and succeed (an intentional deviation).
    [SysAbiExport(
        Nid = "3k6kx-zOOSQ",
        ExportName = "sceKernelMlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMlock(CpuContext ctx) =>
        ValidateRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out _)
            ? Succeed(ctx)
            : KernelErrno.Sce(OrbisErrno.EINVAL);

    [SysAbiExport(
        Nid = "xQIIfJ860sk",
        ExportName = "sceKernelMunlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMunlock(CpuContext ctx) => KernelMlock(ctx);

    [SysAbiExport(
        Nid = "DkNebxmJyVI",
        ExportName = "sceKernelMsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMsync(CpuContext ctx)
    {
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);
        if ((flags & ~MsValidFlags) != 0 ||
            (flags & (MsAsync | MsInvalidate)) == (MsAsync | MsInvalidate) ||
            !ValidateRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out _))
        {
            return KernelErrno.Sce(OrbisErrno.EINVAL);
        }

        return Succeed(ctx);
    }

    private static bool ValidateRange(ulong address, ulong length, out ulong end)
    {
        // FreeBSD rounds the start down to a page and the size up; a range
        // that wraps the address space is EINVAL.
        var start = address & ~((ulong)GuestPageSize - 1);
        var size = length + (address - start);
        end = start + size;
        return end >= start && size >= length;
    }

    private static int Succeed(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}
