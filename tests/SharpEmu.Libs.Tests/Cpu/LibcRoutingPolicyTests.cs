// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class LibcRoutingPolicyTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    public void ParsesTheToggle(string? value, bool expected) =>
        Assert.Equal(expected, LibcRoutingPolicy.Parse(value));

    [Fact]
    public void DeviationNidsMatchTheirNames()
    {
        foreach (var deviation in LibcRoutingPolicy.HleDeviations)
        {
            Assert.True(Aerolib.Instance.TryGetName(deviation.Nid, out var name), deviation.Nid);
            Assert.Equal(deviation.Name, name);
            Assert.False(string.IsNullOrWhiteSpace(deviation.Reason));
        }
    }

    [Fact]
    public void DefaultRoutingKeepsEveryDeviationOnHle()
    {
        Assert.True(LibcRoutingPolicy.IsHleDeviation("8zTFvBIAIN8", lleFirst: false));
        Assert.True(LibcRoutingPolicy.IsHleDeviation("Q3VBxCXhUHs", lleFirst: false));
        Assert.True(LibcRoutingPolicy.IsHleDeviation("QrZZdJ8XsX0", lleFirst: false));
        Assert.False(LibcRoutingPolicy.IsHleDeviation("gQX+4GDQjpM", lleFirst: false));
    }

    [Fact]
    public void LleFirstKeepsOnlyHostInstructionDeviations()
    {
        Assert.True(LibcRoutingPolicy.IsHleDeviation("8zTFvBIAIN8", lleFirst: true));
        Assert.True(LibcRoutingPolicy.IsHleDeviation("Q3VBxCXhUHs", lleFirst: true));
        // FILE objects come from the module under LLE-first, so fputs must too.
        Assert.False(LibcRoutingPolicy.IsHleDeviation("QrZZdJ8XsX0", lleFirst: true));
    }

    [Fact]
    public void UnresolvedImportLogNamesTheSymbol()
    {
        Assert.Equal("access", DirectExecutionBackend.DescribeUnresolvedImport("8vE6Z6VEYyk"));
        Assert.Equal("?", DirectExecutionBackend.DescribeUnresolvedImport("AAAAAAAAAAA"));
    }
}
