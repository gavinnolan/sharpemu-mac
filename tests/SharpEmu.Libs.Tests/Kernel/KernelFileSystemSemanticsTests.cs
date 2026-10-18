// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// fd table, errno and VFS semantics of the file exports (SPEC §5.6), plus the
/// POSIX exports added beside them (access, lstat, getcwd, pipe, readv/writev,
/// statfs/fstatfs).
/// </summary>
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelFileSystemSemanticsTests : IDisposable
{
    private const ulong Base = 0x1_0000;
    private const ulong Tls = Base + 0x100;
    private const ulong PathA = Base + 0x1000;
    private const ulong PathB = Base + 0x1400;
    private const ulong StatBuffer = Base + 0x2000;
    private const ulong IovBuffer = Base + 0x3000;
    private const ulong StatfsBuffer = Base + 0x3800;
    private const ulong Data = Base + 0x4000;
    private const ulong ThreadBuffers = Base + 0x14000;

    private const int ORdonly = 0;
    private const int OWronly = 0x1;
    private const int ORdwr = 0x2;
    private const int OAppend = 0x8;
    private const int OCreat = 0x200;
    private const int OExcl = 0x800;
    private const int ODirectory = 0x20000;

    private readonly string _mount;
    private readonly string _root;
    private readonly FakeCpuMemory _memory;
    private readonly CpuContext _ctx;
    private readonly List<int> _openFds = [];

    public KernelFileSystemSemanticsTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _mount = $"/vfs-{id}";
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-vfs-{id}");
        Directory.CreateDirectory(_root);
        KernelMemoryCompatExports.RegisterGuestPathMount(_mount, _root);
        _memory = new FakeCpuMemory(Base, 0x40000);
        _ctx = NewContext();
    }

    public void Dispose()
    {
        foreach (var fd in _openFds.ToArray())
        {
            Close(fd);
        }

        _ = KernelMemoryCompatExports.UnregisterGuestPathMount(_mount);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private CpuContext NewContext() => new(_memory, Generation.Gen5) { FsBase = Tls };

    private string Host(string name) => Path.Combine(_root, name);

    private string Guest(string name) => $"{_mount}/{name}";

    private int Open(string guestPath, int flags, out int fd)
    {
        _memory.WriteCString(PathA, guestPath);
        _ctx[CpuRegister.Rdi] = PathA;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)flags);
        _ctx[CpuRegister.Rdx] = 0x1B6;
        var result = KernelExports.KernelOpen(_ctx);
        fd = result == 0 ? unchecked((int)_ctx[CpuRegister.Rax]) : -1;
        if (fd >= 0)
        {
            _openFds.Add(fd);
        }

        return result;
    }

    private int OpenOk(string guestPath, int flags)
    {
        Assert.Equal(0, Open(guestPath, flags, out var fd));
        return fd;
    }

    private int Close(int fd)
    {
        _openFds.Remove(fd);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        return KernelMemoryCompatExports.KernelClose(_ctx);
    }

    private int Write(int fd, byte[] payload, CpuContext? ctx = null)
    {
        ctx ??= _ctx;
        _memory.TryWrite(Data, payload);
        ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        ctx[CpuRegister.Rsi] = Data;
        ctx[CpuRegister.Rdx] = (ulong)payload.Length;
        return KernelMemoryCompatExports.KernelWriteUnderscore(ctx);
    }

    private int Read(int fd, int count, out byte[] bytes, CpuContext? ctx = null, ulong buffer = Data)
    {
        ctx ??= _ctx;
        ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        ctx[CpuRegister.Rsi] = buffer;
        ctx[CpuRegister.Rdx] = (ulong)count;
        var result = KernelMemoryCompatExports.KernelReadUnderscore(ctx);
        bytes = [];
        if (result == 0)
        {
            bytes = new byte[(int)ctx[CpuRegister.Rax]];
            Assert.True(_memory.TryRead(buffer, bytes));
        }

        return result;
    }

    private int Stat(string guestPath, bool follow = true)
    {
        _memory.WriteCString(PathA, guestPath);
        _ctx[CpuRegister.Rdi] = PathA;
        _ctx[CpuRegister.Rsi] = StatBuffer;
        return follow
            ? KernelMemoryCompatExports.KernelStat(_ctx)
            : KernelMemoryCompatExports.KernelStatPath(_ctx, followLinks: false);
    }

    private ushort StatMode() => ReadU16(StatBuffer + 8);

    private uint StatInode() => ReadU32(StatBuffer + 4);

    private ushort ReadU16(ulong address)
    {
        Span<byte> b = stackalloc byte[2];
        Assert.True(_memory.TryRead(address, b));
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    private uint ReadU32(ulong address)
    {
        Span<byte> b = stackalloc byte[4];
        Assert.True(_memory.TryRead(address, b));
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private string ReadCString(ulong address, int max)
    {
        var bytes = new byte[max];
        Assert.True(_memory.TryRead(address, bytes));
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? max : end);
    }

    private static int Sce(int errno) => OrbisErrno.ToSceKernelError(errno);

    private void AssertPosixFailure(int result, int errno)
    {
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, _ctx[CpuRegister.Rax]);
        Assert.Equal(errno, KernelErrno.Current(_ctx));
    }

    // ---- read buffers ---------------------------------------------------

    // A contiguous guest region that hands its bytes out in place, as the host mapping does.
    private sealed class InPlaceMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];
        public int InPlaceFills;

        private bool Resolve(ulong address, int length, out int offset)
        {
            offset = (int)(address - baseAddress);
            return address >= baseAddress && length >= 0 && offset + (long)length <= _storage.Length;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!Resolve(virtualAddress, destination.Length, out var offset))
                return false;
            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!Resolve(virtualAddress, source.Length, out var offset))
                return false;
            source.CopyTo(_storage.AsSpan(offset));
            return true;
        }

        public bool TryFillInPlace<TState>(ulong virtualAddress, int length, TState state, GuestSpanFill<TState> fill, out int filled)
        {
            filled = 0;
            if (!Resolve(virtualAddress, length, out var offset))
                return false;
            InPlaceFills++;
            filled = fill(state, _storage.AsSpan(offset, length));
            return true;
        }
    }

    private const int LargeRead = 128 * 1024;

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)(index * 7 + (index >> 12));
        return bytes;
    }

    private static long AllocatedByReads(CpuContext ctx, int fd, ulong buffer, int reads, Action<int, int>? check = null)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < reads; index++)
        {
            ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            ctx[CpuRegister.Rsi] = buffer;
            ctx[CpuRegister.Rdx] = LargeRead;
            var result = KernelMemoryCompatExports.KernelReadUnderscore(ctx);
            check?.Invoke(result, (int)ctx[CpuRegister.Rax]);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // A large read used to allocate its whole length as a staging array (on the LOH past
    // 85 KB). Through a pooled buffer, a warm read allocates nothing proportional to it.
    [Fact]
    public void LargeReadsThroughAStagingBufferDoNotAllocateTheirLength()
    {
        var content = Pattern(LargeRead * 12);
        File.WriteAllBytes(Host("staged.bin"), content);
        var fd = OpenOk(Guest("staged.bin"), ORdonly);
        AllocatedByReads(_ctx, fd, Data, 2, (result, read) => Assert.Equal((0, LargeRead), (result, read)));

        var allocated = AllocatedByReads(_ctx, fd, Data, 8, (result, read) => Assert.Equal((0, LargeRead), (result, read)));
        Assert.True(allocated < LargeRead / 4, $"allocated={allocated}");

        var last = new byte[LargeRead];
        Assert.True(_memory.TryRead(Data, last));
        Assert.Equal(content.AsSpan(LargeRead * 9, LargeRead).ToArray(), last);
    }

    // Guest memory that hands the range out in place takes the read directly: same bytes,
    // same file offset, no staging buffer at all.
    [Fact]
    public void LargeReadsFillGuestMemoryInPlace()
    {
        var content = Pattern(LargeRead * 12);
        File.WriteAllBytes(Host("inplace.bin"), content);
        var fd = OpenOk(Guest("inplace.bin"), ORdonly);
        const ulong buffer = 0x10_0000;
        var memory = new InPlaceMemory(buffer, LargeRead);
        var ctx = new CpuContext(memory, Generation.Gen5) { FsBase = Tls };
        AllocatedByReads(ctx, fd, buffer, 2, (result, read) => Assert.Equal((0, LargeRead), (result, read)));

        var allocated = AllocatedByReads(ctx, fd, buffer, 8, (result, read) => Assert.Equal((0, LargeRead), (result, read)));
        Assert.True(allocated < LargeRead / 4, $"allocated={allocated}");
        Assert.Equal(10, memory.InPlaceFills);

        var last = new byte[LargeRead];
        Assert.True(memory.TryRead(buffer, last));
        Assert.Equal(content.AsSpan(LargeRead * 9, LargeRead).ToArray(), last);

        // The last two chunks, then end of file.
        AllocatedByReads(ctx, fd, buffer, 1, (result, read) => Assert.Equal((0, LargeRead), (result, read)));
        AllocatedByReads(ctx, fd, buffer, 1, (result, read) => Assert.Equal((0, LargeRead), (result, read)));
        AllocatedByReads(ctx, fd, buffer, 1, (result, read) => Assert.Equal((0, 0), (result, read)));
    }

    // ---- fd table -------------------------------------------------------

    [Fact]
    public void OpenReusesTheLowestFreeDescriptor()
    {
        File.WriteAllBytes(Host("a.bin"), [1]);
        var first = OpenOk(Guest("a.bin"), ORdonly);
        var second = OpenOk(Guest("a.bin"), ORdonly);
        Assert.True(second > first);

        Assert.Equal(0, Close(first));
        var third = OpenOk(Guest("a.bin"), ORdonly);
        Assert.Equal(first, third);
    }

    [Fact]
    public void ClosingADupKeepsTheSharedDescriptionOpen()
    {
        File.WriteAllBytes(Host("dup.bin"), [1, 2, 3, 4]);
        var fd = OpenOk(Guest("dup.bin"), ORdonly);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.PosixDup(_ctx));
        var dup = unchecked((int)_ctx[CpuRegister.Rax]);
        _openFds.Add(dup);

        Assert.Equal(0, Read(fd, 2, out var head));
        Assert.Equal([1, 2], head);
        Assert.Equal(0, Close(fd));

        // The offset is shared and the stream survives the original's close.
        Assert.Equal(0, Read(dup, 2, out var tail));
        Assert.Equal([3, 4], tail);
    }

    [Fact]
    public void FcntlDupfdTakesTheLowestFreeAtOrAboveTheArgument()
    {
        File.WriteAllBytes(Host("f.bin"), [1]);
        var fd = OpenOk(Guest("f.bin"), ORdonly);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = 0; // F_DUPFD
        _ctx[CpuRegister.Rdx] = 100;
        Assert.Equal(0, KernelMemoryCompatExports.KernelFcntl(_ctx));
        var dup = unchecked((int)_ctx[CpuRegister.Rax]);
        _openFds.Add(dup);
        Assert.True(dup >= 100);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 1_000_000;
        Assert.Equal(Sce(OrbisErrno.EINVAL), KernelMemoryCompatExports.KernelFcntl(_ctx));
    }

    // ---- open / read / write errnos --------------------------------------

    [Fact]
    public void OpenReportsFreeBsdErrnos()
    {
        File.WriteAllBytes(Host("exists.bin"), [1]);
        Directory.CreateDirectory(Host("dir"));

        Assert.Equal(Sce(OrbisErrno.EEXIST), Open(Guest("exists.bin"), OWronly | OCreat | OExcl, out _));
        Assert.Equal(Sce(OrbisErrno.ENOTDIR), Open(Guest("exists.bin"), ORdonly | ODirectory, out _));
        Assert.Equal(Sce(OrbisErrno.EISDIR), Open(Guest("dir"), OWronly, out _));
        Assert.Equal(Sce(OrbisErrno.ENOENT), Open(Guest("missing.bin"), ORdonly, out _));
    }

    [Fact]
    public void AppendWritesLandAtTheEndAfterAnLseek()
    {
        File.WriteAllBytes(Host("log.txt"), "A"u8.ToArray());
        var fd = OpenOk(Guest("log.txt"), OWronly | OAppend);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0; // SEEK_SET
        Assert.Equal(0, KernelMemoryCompatExports.KernelLseek(_ctx));
        Assert.Equal(0, Write(fd, "B"u8.ToArray()));
        Assert.Equal(0, Close(fd));
        Assert.Equal("AB", File.ReadAllText(Host("log.txt")));
    }

    [Fact]
    public void ReadOnAWriteOnlyDescriptorIsEbadfAndFtruncateOnReadOnlyIsEinval()
    {
        File.WriteAllBytes(Host("rw.bin"), [1, 2, 3]);
        var writeOnly = OpenOk(Guest("rw.bin"), OWronly);
        Assert.Equal(Sce(OrbisErrno.EBADF), Read(writeOnly, 1, out _));

        var readOnly = OpenOk(Guest("rw.bin"), ORdonly);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)readOnly);
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(Sce(OrbisErrno.EINVAL), KernelMemoryCompatExports.KernelFtruncate(_ctx));
        Assert.Equal(3, new FileInfo(Host("rw.bin")).Length);
    }

    [Fact]
    public void ConcurrentReadsOnASharedDescriptorNeverOverlap()
    {
        const int chunk = 4096;
        const int chunks = 64;
        var content = new byte[chunk * chunks];
        for (var i = 0; i < content.Length / 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(i * 4), i);
        }

        File.WriteAllBytes(Host("shared.bin"), content);
        var fd = OpenOk(Guest("shared.bin"), ORdonly);
        var starts = new System.Collections.Concurrent.ConcurrentBag<int>();
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            var ctx = NewContext();
            var buffer = ThreadBuffers + (ulong)(t * 0x2000);
            while (Read(fd, chunk, out var bytes, ctx, buffer) == 0 && bytes.Length > 0)
            {
                Assert.Equal(chunk, bytes.Length);
                var first = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                for (var w = 1; w < chunk / 4; w++)
                {
                    Assert.Equal(first + w, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(w * 4)));
                }

                starts.Add(first);
            }
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Assert.Equal(chunks, starts.Count);
        Assert.Equal(chunks, starts.Distinct().Count());
    }

    // ---- rename / rmdir --------------------------------------------------

    [Fact]
    public void RmdirAndRenameReportDirectoryErrnos()
    {
        Directory.CreateDirectory(Host("full"));
        File.WriteAllBytes(Host("full/x"), [1]);
        Directory.CreateDirectory(Host("empty"));
        File.WriteAllBytes(Host("file"), [1]);

        _memory.WriteCString(PathA, Guest("full"));
        _ctx[CpuRegister.Rdi] = PathA;
        Assert.Equal(Sce(OrbisErrno.ENOTEMPTY), KernelMemoryCompatExports.KernelRmdir(_ctx));
        _memory.WriteCString(PathA, Guest("file"));
        _ctx[CpuRegister.Rdi] = PathA;
        Assert.Equal(Sce(OrbisErrno.ENOTDIR), KernelMemoryCompatExports.KernelRmdir(_ctx));

        Assert.Equal(Sce(OrbisErrno.EISDIR), Rename("file", "empty"));
        Assert.Equal(Sce(OrbisErrno.ENOTDIR), Rename("empty", "file"));
        Assert.Equal(Sce(OrbisErrno.ENOTEMPTY), Rename("empty", "full"));
        Assert.Equal(Sce(OrbisErrno.ENOENT), Rename("nothing", "other"));

        // A directory may replace an empty one.
        Directory.CreateDirectory(Host("src"));
        Assert.Equal(0, Rename("src", "empty"));
        Assert.False(Directory.Exists(Host("src")));
        Assert.True(Directory.Exists(Host("empty")));
    }

    private int Rename(string from, string to)
    {
        _memory.WriteCString(PathA, Guest(from));
        _memory.WriteCString(PathB, Guest(to));
        _ctx[CpuRegister.Rdi] = PathA;
        _ctx[CpuRegister.Rsi] = PathB;
        return KernelMemoryCompatExports.KernelRename(_ctx);
    }

    // ---- stat / getdents -------------------------------------------------

    [Fact]
    public void StatModesAndGetdentsInodesAgree()
    {
        Directory.CreateDirectory(Host("tree"));
        File.WriteAllBytes(Host("tree/entry.dat"), [1, 2]);

        Assert.Equal(0, Stat(Guest("tree")));
        Assert.Equal(0x41ED, StatMode());
        Assert.Equal(0, Stat(Guest("tree/entry.dat")));
        Assert.Equal(0x81A4, StatMode());
        var inode = StatInode();
        Assert.NotEqual(0u, inode);

        var dirFd = OpenOk(Guest("tree"), ORdonly | ODirectory);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)dirFd);
        _ctx[CpuRegister.Rsi] = Data;
        _ctx[CpuRegister.Rdx] = 0x1000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));
        var length = (int)_ctx[CpuRegister.Rax];
        var buffer = new byte[length];
        Assert.True(_memory.TryRead(Data, buffer));

        uint? found = null;
        for (var offset = 0; offset < length;)
        {
            var fileno = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
            var reclen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4));
            var name = Encoding.UTF8.GetString(buffer, offset + 8, buffer[offset + 7]);
            Assert.NotEqual(0u, fileno);
            if (name == "entry.dat")
            {
                found = fileno;
            }

            Assert.True(reclen > 0);
            offset += reclen;
        }

        Assert.Equal(inode, found);
    }

    [Fact]
    public void StatFollowsSymlinksAndLstatDescribesTheLink()
    {
        File.WriteAllBytes(Host("target.bin"), [1, 2, 3]);
        File.CreateSymbolicLink(Host("link"), Host("target.bin"));
        File.CreateSymbolicLink(Host("dangling"), Host("nowhere"));
        File.CreateSymbolicLink(Host("loop-a"), Host("loop-b"));
        File.CreateSymbolicLink(Host("loop-b"), Host("loop-a"));

        Assert.Equal(0, Stat(Guest("link")));
        Assert.Equal(0x81A4, StatMode());
        Assert.Equal(0, Stat(Guest("link"), follow: false));
        Assert.Equal(0xA000, StatMode() & 0xF000);

        Assert.Equal(Sce(OrbisErrno.ENOENT), Stat(Guest("dangling")));
        Assert.Equal(0, Stat(Guest("dangling"), follow: false));
        Assert.Equal(Sce(OrbisErrno.ELOOP), Stat(Guest("loop-a")));

        _memory.WriteCString(PathA, Guest("link"));
        _ctx[CpuRegister.Rdi] = PathA;
        _ctx[CpuRegister.Rsi] = StatBuffer;
        Assert.Equal(0, KernelMemoryCompatExports.PosixLstat(_ctx));
        Assert.Equal(0xA000, StatMode() & 0xF000);
    }

    [Fact]
    public void StatOfAPathBelowAFileIsEnotdir()
    {
        File.WriteAllBytes(Host("plain"), [1]);
        Assert.Equal(Sce(OrbisErrno.ENOTDIR), Stat(Guest("plain/child")));
    }

    [Fact]
    public void AMissOnAWritableMountIsNotCached()
    {
        Assert.Equal(Sce(OrbisErrno.ENOENT), Stat(Guest("later.bin")));
        File.WriteAllBytes(Host("later.bin"), [1]);
        Assert.Equal(0, Stat(Guest("later.bin")));
    }

    [Fact]
    public void MountPrefixesAreCaseSensitive()
    {
        File.WriteAllBytes(Host("cs.bin"), [1]);
        Assert.Equal(0, Stat(Guest("cs.bin")));
        Assert.Equal(Sce(OrbisErrno.ENOENT), Stat(_mount.ToUpperInvariant() + "/cs.bin"));
    }

    // ---- access / getcwd -------------------------------------------------

    [Fact]
    public void AccessChecksExistenceAndModeBits()
    {
        File.WriteAllBytes(Host("acc.bin"), [1]);
        Directory.CreateDirectory(Host("accdir"));

        Assert.Equal(0, Access(Guest("acc.bin"), 0));
        Assert.Equal(0, Access(Guest("acc.bin"), 4 | 2));
        AssertPosixFailure(Access(Guest("acc.bin"), 1), OrbisErrno.EACCES);
        Assert.Equal(0, Access(Guest("accdir"), 1));
        AssertPosixFailure(Access(Guest("gone.bin"), 0), OrbisErrno.ENOENT);
        AssertPosixFailure(Access(Guest("acc.bin/x"), 0), OrbisErrno.ENOTDIR);
        AssertPosixFailure(Access(Guest("acc.bin"), 8), OrbisErrno.EINVAL);
        Assert.True(KernelMemoryCompatExports.IsReadOnlyGuestMutationPath("/app0/eboot.bin"));
    }

    private int Access(string guestPath, int mode)
    {
        _memory.WriteCString(PathA, guestPath);
        _ctx[CpuRegister.Rdi] = PathA;
        _ctx[CpuRegister.Rsi] = (ulong)mode;
        return KernelMemoryCompatExports.PosixAccess(_ctx);
    }

    [Fact]
    public void GetcwdReportsTheGameImageAndErangeForSmallBuffers()
    {
        _ctx[CpuRegister.Rdi] = Data;
        _ctx[CpuRegister.Rsi] = 64;
        KernelMemoryCompatExports.PosixGetcwd(_ctx);
        Assert.Equal(Data, _ctx[CpuRegister.Rax]);
        Assert.Equal("/app0", ReadCString(Data, 64));

        _ctx[CpuRegister.Rdi] = Data;
        _ctx[CpuRegister.Rsi] = 3;
        KernelMemoryCompatExports.PosixGetcwd(_ctx);
        Assert.Equal(0ul, _ctx[CpuRegister.Rax]);
        Assert.Equal(OrbisErrno.ERANGE, KernelErrno.Current(_ctx));

        _ctx[CpuRegister.Rdi] = Data;
        _ctx[CpuRegister.Rsi] = 64;
        Assert.Equal(0, KernelMemoryCompatExports.PosixGetcwdSyscall(_ctx));
        Assert.Equal(0ul, _ctx[CpuRegister.Rax]);
    }

    // ---- pipe ------------------------------------------------------------

    [Fact]
    public void PipeCarriesBytesAndReportsFifoSemantics()
    {
        _ctx[CpuRegister.Rdi] = IovBuffer;
        Assert.Equal(0, KernelMemoryCompatExports.PosixPipe(_ctx));
        var readFd = (int)ReadU32(IovBuffer);
        var writeFd = (int)ReadU32(IovBuffer + 4);
        _openFds.Add(readFd);
        _openFds.Add(writeFd);
        Assert.NotEqual(readFd, writeFd);

        Assert.Equal(0, Write(writeFd, "hello"u8.ToArray()));
        Assert.Equal(0, Read(readFd, 16, out var bytes));
        Assert.Equal("hello", Encoding.ASCII.GetString(bytes));

        Assert.Equal(Sce(OrbisErrno.EBADF), Read(writeFd, 1, out _));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)readFd);
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(Sce(OrbisErrno.ESPIPE), KernelMemoryCompatExports.KernelLseek(_ctx));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)readFd);
        _ctx[CpuRegister.Rsi] = StatBuffer;
        Assert.Equal(0, KernelMemoryCompatExports.KernelFstat(_ctx));
        Assert.Equal(0x1000, StatMode() & 0xF000);

        // Every writer gone: EOF instead of blocking.
        Assert.Equal(0, Close(writeFd));
        Assert.Equal(0, Read(readFd, 16, out var eof));
        Assert.Empty(eof);
    }

    [Fact]
    public void PipeWriteWithNoReaderIsEpipe()
    {
        _ctx[CpuRegister.Rdi] = IovBuffer;
        Assert.Equal(0, KernelMemoryCompatExports.PosixPipe(_ctx));
        var readFd = (int)ReadU32(IovBuffer);
        var writeFd = (int)ReadU32(IovBuffer + 4);
        _openFds.Add(writeFd);
        Assert.Equal(0, Close(readFd));
        Assert.Equal(Sce(OrbisErrno.EPIPE), Write(writeFd, [1]));
    }

    [Fact]
    public void PipeReadBlocksUntilDataArrives()
    {
        _ctx[CpuRegister.Rdi] = IovBuffer;
        Assert.Equal(0, KernelMemoryCompatExports.PosixPipe(_ctx));
        var readFd = (int)ReadU32(IovBuffer);
        var writeFd = (int)ReadU32(IovBuffer + 4);
        _openFds.Add(readFd);
        _openFds.Add(writeFd);

        byte[]? received = null;
        var reader = new Thread(() =>
        {
            Assert.Equal(0, Read(readFd, 8, out var bytes, NewContext(), ThreadBuffers));
            received = bytes;
        });
        reader.Start();
        Thread.Sleep(50);
        Assert.Null(received);
        Assert.Equal(0, Write(writeFd, [7, 8]));
        Assert.True(reader.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal([7, 8], received);
    }

    // ---- readv / writev --------------------------------------------------

    [Fact]
    public void WritevAndReadvWalkEveryIovec()
    {
        var fd = OpenOk(Guest("vec.bin"), ORdwr | OCreat);
        _memory.TryWrite(Data, "abc"u8);
        _memory.TryWrite(Data + 0x100, "defg"u8);
        WriteIovec(0, Data, 3);
        WriteIovec(1, Data + 0x100, 4);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = IovBuffer;
        _ctx[CpuRegister.Rdx] = 2;
        Assert.Equal(0, KernelMemoryCompatExports.PosixWritev(_ctx));
        Assert.Equal(7ul, _ctx[CpuRegister.Rax]);
        Assert.Equal("abcdefg", File.ReadAllText(Host("vec.bin")));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelMemoryCompatExports.KernelLseek(_ctx));

        WriteIovec(0, Data + 0x200, 2);
        WriteIovec(1, Data + 0x300, 10);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = IovBuffer;
        _ctx[CpuRegister.Rdx] = 2;
        Assert.Equal(0, KernelMemoryCompatExports.KernelReadv(_ctx));
        Assert.Equal(7ul, _ctx[CpuRegister.Rax]);
        Assert.Equal("ab", ReadCString(Data + 0x200, 2));
        Assert.Equal("cdefg", ReadCString(Data + 0x300, 5));
    }

    [Fact]
    public void ReadvRejectsBadDescriptorsAndIovecCounts()
    {
        WriteIovec(0, Data, 4);
        _ctx[CpuRegister.Rdi] = 4000;
        _ctx[CpuRegister.Rsi] = IovBuffer;
        _ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(Sce(OrbisErrno.EBADF), KernelMemoryCompatExports.KernelReadv(_ctx));

        _ctx[CpuRegister.Rdi] = 4000;
        _ctx[CpuRegister.Rsi] = IovBuffer;
        _ctx[CpuRegister.Rdx] = 1;
        AssertPosixFailure(KernelMemoryCompatExports.PosixReadv(_ctx), OrbisErrno.EBADF);

        _ctx[CpuRegister.Rdi] = 4000;
        _ctx[CpuRegister.Rsi] = IovBuffer;
        _ctx[CpuRegister.Rdx] = KernelMemoryCompatExports.GuestIovMax + 1;
        AssertPosixFailure(KernelMemoryCompatExports.PosixWritev(_ctx), OrbisErrno.EINVAL);
    }

    private void WriteIovec(int index, ulong address, ulong length)
    {
        Span<byte> entry = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(entry, address);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], length);
        Assert.True(_memory.TryWrite(IovBuffer + (ulong)(index * 16), entry));
    }

    // ---- statfs / fstatfs ------------------------------------------------

    [Fact]
    public void StatfsDescribesTheGuestMount()
    {
        File.WriteAllBytes(Host("fs.bin"), [1]);
        _memory.WriteCString(PathA, Guest("fs.bin"));
        _ctx[CpuRegister.Rdi] = PathA;
        _ctx[CpuRegister.Rsi] = StatfsBuffer;
        Assert.Equal(0, KernelMemoryCompatExports.PosixStatfs(_ctx));
        Assert.Equal(0x20030518u, ReadU32(StatfsBuffer));
        Assert.Equal(0u, ReadU32(StatfsBuffer + 8) & 1); // not MNT_RDONLY
        Assert.Equal(_mount, ReadCString(StatfsBuffer + 384, 88));
        Assert.Equal(255u, ReadU32(StatfsBuffer + 184));

        var fd = OpenOk(Guest("fs.bin"), ORdonly);
        _memory.TryWrite(StatfsBuffer, new byte[KernelMemoryCompatExports.StatfsSize]);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        _ctx[CpuRegister.Rsi] = StatfsBuffer;
        Assert.Equal(0, KernelMemoryCompatExports.KernelFstatfs(_ctx));
        Assert.Equal(_mount, ReadCString(StatfsBuffer + 384, 88));

        _memory.WriteCString(PathA, Guest("absent.bin"));
        _ctx[CpuRegister.Rdi] = PathA;
        _ctx[CpuRegister.Rsi] = StatfsBuffer;
        Assert.Equal(Sce(OrbisErrno.ENOENT), KernelMemoryCompatExports.KernelStatfs(_ctx));

        _ctx[CpuRegister.Rdi] = 4000;
        _ctx[CpuRegister.Rsi] = StatfsBuffer;
        AssertPosixFailure(KernelMemoryCompatExports.PosixFstatfs(_ctx), OrbisErrno.EBADF);
    }

    [Fact]
    public void GameImageStatfsIsReadOnly()
    {
        var bytes = KernelMemoryCompatExports.BuildStatfs(
            new KernelMemoryCompatExports.GuestMount("/app0", "pfs", ReadOnly: true, HostRoot: null));
        Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)) & 1);
        Assert.Equal("pfs", Encoding.ASCII.GetString(bytes, 280, 3));
    }
}
