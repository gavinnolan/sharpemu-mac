// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Kernel.Syscalls;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel.Syscalls;

public sealed class OrbisSyscallTableTests
{
    [Fact]
    public void CoversEveryProsperoSlot()
    {
        // psdevwiki ps5-Syscalls: 0x0..0x2D2 (workspace_ctrl, 3.00+).
        Assert.Equal(0x2D2, OrbisSyscallTable.MaxNumber);
        Assert.Equal(0x2D3, OrbisSyscallTable.Count);
        for (var number = 0; number <= OrbisSyscallTable.MaxNumber; number++)
        {
            Assert.True(OrbisSyscallTable.TryGet(number, out var entry));
            Assert.Equal(number, entry.Number);
            Assert.StartsWith("sys_", entry.Name, StringComparison.Ordinal);
        }

        Assert.False(OrbisSyscallTable.TryGet(0x2D3, out _));
        Assert.False(OrbisSyscallTable.TryGet(-1, out _));
    }

    [Theory]
    [InlineData(0x000, "sys_syscall", nameof(OrbisSyscallKind.Indirect))]
    [InlineData(0x0C6, "sys___syscall", nameof(OrbisSyscallKind.Indirect))]
    [InlineData(0x003, "sys_read", nameof(OrbisSyscallKind.Forwarded))]
    [InlineData(0x004, "sys_write", nameof(OrbisSyscallKind.Forwarded))]
    [InlineData(0x014, "sys_getpid", nameof(OrbisSyscallKind.Forwarded))]
    [InlineData(0x1DD, "sys_mmap", nameof(OrbisSyscallKind.Forwarded))]
    [InlineData(0x0CA, "sys___sysctl", nameof(OrbisSyscallKind.Forwarded))]
    [InlineData(0x011, "sys_obsolete17", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x097, "sys_number151", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x099, "sys_number153", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x0D2, "sys_lkmnosys", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x0DB, "sys_lkmnosys", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x23E, "sys_obs_sblock_create", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x243, "sys_obs_sblock_xexit", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x248, "sys_obs_eport_close", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x2BB, "sys_number699", nameof(OrbisSyscallKind.NoSys))]
    [InlineData(0x013, "sys_compat.lseek", nameof(OrbisSyscallKind.Compat))]
    [InlineData(0x2D2, "sys_workspace_ctrl", nameof(OrbisSyscallKind.Unmapped))]
    public void MatchesPsdevwiki(int number, string name, string kind)
    {
        Assert.True(OrbisSyscallTable.TryGet(number, out var entry));
        Assert.Equal(name, entry.Name);
        Assert.Equal(kind, entry.Kind.ToString());
    }

    [Theory]
    [InlineData(0x003, "read", "AqBioC2vF3I")]
    [InlineData(0x014, "getpid", "HoLVWNanBBc")]
    [InlineData(0x0CA, "sysctl", "DFmMT80xcNI")]
    [InlineData(0x027, "getppid", "e6ovBo9ZvJc")]
    [InlineData(0x018, "getuid", "kg4x8Prhfxw")]
    [InlineData(0x088, "mkdir", "JGMio+21L4c")]
    public void ForwardsToTheLibkernelWrapperByNid(int number, string exportName, string nid)
    {
        Assert.True(OrbisSyscallTable.TryGet(number, out var entry));
        Assert.Contains(entry.Targets, t => t.ExportName == exportName && t.Nid == nid && t.Flavor == KernelAbiFlavor.Posix);
    }

    [Fact]
    public void EveryTargetNidIsTheHashOfItsName()
    {
        for (var number = 0; number <= OrbisSyscallTable.MaxNumber; number++)
        {
            Assert.True(OrbisSyscallTable.TryGet(number, out var entry));
            Assert.Equal(entry.Kind == OrbisSyscallKind.Forwarded, entry.Targets.Length != 0);
            foreach (var target in entry.Targets)
            {
                Assert.Equal(ComputeNid(target.ExportName), target.Nid);
            }
        }
    }

    [Fact]
    public void ExitIsNotRoutedThroughLibcExit()
    {
        // libc exit() runs atexit handlers; the exit syscall does not.
        Assert.True(OrbisSyscallTable.TryGet(1, out var entry));
        Assert.Contains(entry.Targets, t => t.ExportName == "_exit");
        Assert.DoesNotContain(entry.Targets, t => t.ExportName == "exit");
    }

    [Fact]
    public void BothEntryPointsAreRegisteredExports()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("W0Aa+HAwN6U", out var syscall));
        Assert.Equal("syscall", syscall.Name);
        Assert.True(manager.TryGetExport(GuestSyscallFrame.TrapNid, out var trap));
        Assert.Equal(GuestSyscallFrame.TrapExportName, trap.Name);
        Assert.Equal(GuestSyscallFrame.TrapNid, ComputeNid(GuestSyscallFrame.TrapExportName));
    }

    // scripts/aerolib_catalog.py compute_nid.
    private static string ComputeNid(string name)
    {
        var suffix = Convert.FromHexString("518d64a635ded8c1e6b039b1c3e55230");
        var digest = SHA1.HashData([.. Encoding.UTF8.GetBytes(name), .. suffix]);
        var head = digest[..8];
        Array.Reverse(head);
        return Convert.ToBase64String(head).TrimEnd('=').Replace('/', '-');
    }
}
