// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Fonts.Abstractions;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Libs.Tests.ArchitectureRules;

public sealed class FontPackageBoundaryTests
{
    [Fact]
    public void SharedGuestFontAbiHasNoSkiaAssemblyReference()
    {
        Assert.DoesNotContain(typeof(FontExports).Assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Skia"));
        Assert.DoesNotContain(typeof(IFontFactory).Assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("SharpEmu.") || reference.Name!.StartsWith("Skia"));
    }
    [Fact]
    public void FontContractDoesNotExposeBackendHandlesOrTypes()
    {
        var surface = typeof(IFontFactory).Assembly.GetExportedTypes();
        foreach (var method in surface.SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)))
        {
            Assert.NotEqual(typeof(nint), method.ReturnType);
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(nint) || parameter.ParameterType.IsPointer);
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType.Namespace?.StartsWith("Skia") == true);
        }
    }
}
