// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Cpu.Native.Platform;
using Xunit;

namespace SharpEmu.Libs.Tests.ArchitectureRules;

public sealed unsafe class CpuPackageBoundaryTests
{
    [Fact]
    public void SharedDirectExecutionContainsNoOsDeclarationsOrChecks()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src/SharpEmu.Core/Cpu/Native"), "*.cs", SearchOption.AllDirectories))
        {
            var code = Code(File.ReadAllText(file));
            Assert.DoesNotContain("DllImport(", code);
            Assert.DoesNotContain("LibraryImport(", code);
            Assert.DoesNotContain("OperatingSystem.Is", code);
            Assert.DoesNotContain("SharpEmu.Cpu.DirectExecution.MacOS", code);
            Assert.DoesNotContain("SharpEmu.Cpu.DirectExecution.Linux", code);
            Assert.DoesNotContain("SharpEmu.Cpu.DirectExecution.Windows", code);
        }
    }

    [Fact]
    public void CpuPackagesNeedNoFriendAccessToCore()
    {
        var core = XDocument.Load(Path.Combine(Root, "src/SharpEmu.Core/SharpEmu.Core.csproj"));
        Assert.DoesNotContain(core.Descendants("InternalsVisibleTo"), item =>
            ((string?)item.Attribute("Include"))?.StartsWith("SharpEmu.Cpu.DirectExecution.", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(core.Descendants("ProjectReference"), item =>
            ((string?)item.Attribute("Include"))?.Contains("SharpEmu.Cpu.DirectExecution.", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("macos", "MacOS")]
    [InlineData("linux", "Linux")]
    [InlineData("windows", "Windows")]
    public void CpuPackageHasOnlyItsOwnNativeIntegration(string folder, string os)
    {
        var identity = "SharpEmu.Cpu.DirectExecution." + os + ".X64";
        var path = Path.Combine(Root, "src/platforms", folder, identity);
        var project = XDocument.Load(Path.Combine(path, identity + ".csproj"));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), item =>
            ((string?)item.Attribute("Include"))?.Contains("SharpEmu.Cpu.DirectExecution.", StringComparison.Ordinal) == true);
        foreach (var source in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains("/obj/")))
        {
            var code = Code(File.ReadAllText(source));
            if (Path.GetFileName(source) != "AssemblyInfo.cs")
                Assert.Contains("namespace " + identity + ";", code);
            Assert.DoesNotContain("OperatingSystem.Is", code);
            Assert.DoesNotContain("DirectExecutionBackend.", code);
        }
    }

    [Fact]
    public void NormalizedFaultFrameMatchesTheExistingRecoveryAbi()
    {
        Assert.Equal(16, sizeof(DirectExecutionFaultFrame.Pointers));
        Assert.Equal(152, sizeof(DirectExecutionFaultFrame.Record));
        Assert.Equal(0, Marshal.OffsetOf<DirectExecutionFaultFrame.Pointers>("ExceptionRecord").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DirectExecutionFaultFrame.Pointers>("ContextRecord").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<DirectExecutionFaultFrame.Record>("ExceptionInformation").ToInt32());
        Assert.Equal(sizeof(DirectExecutionBackend.EXCEPTION_RECORD), sizeof(DirectExecutionFaultFrame.Record));
        Assert.Equal(DirectExecutionBackend.Win64ContextSize, DirectExecutionFaultFrame.ContextSize);
        Assert.Equal(DirectExecutionBackend.CtxRax, DirectExecutionFaultFrame.RaxOffset);
        Assert.Equal(DirectExecutionBackend.CtxRsp, DirectExecutionFaultFrame.RspOffset);
        Assert.Equal(DirectExecutionBackend.CtxRip, DirectExecutionFaultFrame.RipOffset);
        Assert.Equal(DirectExecutionBackend.Win64ContextXmm0Offset, DirectExecutionFaultFrame.Xmm0Offset);
    }

    private static string Code(string source) => Regex.Replace(source, @"/\*.*?\*/|//[^\r\n]*", "", RegexOptions.Singleline);
    private static string Root => FindRoot();
    private static string FindRoot([CallerFilePath] string source = "")
    {
        for (var path = new DirectoryInfo(Path.GetDirectoryName(source)!); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "SharpEmu.slnx"))) return path.FullName;
        throw new InvalidOperationException("Cannot locate SharpEmu source tree.");
    }
}
