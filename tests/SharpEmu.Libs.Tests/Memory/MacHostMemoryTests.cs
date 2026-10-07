// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// Only compiled on macOS: SharpEmu.Host.MacOS is the only host package the test
// project references there (see SharpEmu.Libs.Tests.csproj).
#if SHARPEMU_HOST_MACOS
public sealed unsafe class MacHostMemoryTests
{
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int MapPrivate = 0x02;
    private const int MapAnonymousDarwin = 0x1000;
    private static readonly nint MapFailed = -1;

    [Fact]
    public void ExactAllocationDoesNotReplaceUntrackedDarwinMapping()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var size = checked((nuint)Environment.SystemPageSize);
        var externalMapping = mmap(
            0,
            size,
            ProtRead | ProtWrite,
            MapPrivate | MapAnonymousDarwin,
            -1,
            0);
        Assert.NotEqual(MapFailed, externalMapping);

        try
        {
            var sentinel = new Span<byte>((void*)externalMapping, checked((int)size));
            sentinel.Fill(0xA5);

            var hostMemory = HostBackendUnderTest.CreateMemory();
            var result = hostMemory.Allocate(
                (ulong)externalMapping,
                (ulong)size,
                HostPageProtection.ReadWrite);

            Assert.Equal(0UL, result);
            Assert.All(sentinel.ToArray(), value => Assert.Equal(0xA5, value));
        }
        finally
        {
            Assert.Equal(0, munmap(externalMapping, size));
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(nint addr, nuint length);
}
#endif
