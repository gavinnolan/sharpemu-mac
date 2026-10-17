// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The Metal shader cache reads MTL_SHADER_CACHE_SIZE from the native environment when it
// first opens; loading the frameworks puts SharpEmu's size there unless one is set.
public sealed partial class MetalShaderCacheSizeTests
{
    [Fact]
    public void LoadingTheFrameworksSetsTheShaderCacheSizeInTheNativeEnvironment()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var before = Read();
        MetalNative.EnsureFrameworksLoaded();

        Assert.Equal(
            before ?? MetalNative.DefaultShaderCacheBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Read());
    }

    // Metal turns its shader cache off at 4 GiB and above, so the default must stay below it.
    [Fact]
    public void TheDefaultSizeKeepsTheShaderCacheOn()
    {
        var text = MetalNative.DefaultShaderCacheBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(MetalNative.DisablesShaderCache(text));
    }

    [Theory]
    [InlineData("4294967295", false)]
    [InlineData("3221225472", false)]
    [InlineData("4294967296", true)]
    [InlineData("8589934592", true)]
    [InlineData("not a size", false)]
    [InlineData(null, false)]
    public void SizesOfFourGibibytesOrMoreDisableTheShaderCache(string? value, bool disables) =>
        Assert.Equal(disables, MetalNative.DisablesShaderCache(value));

    private static string? Read() => Marshal.PtrToStringUTF8(getenv("MTL_SHADER_CACHE_SIZE"));

    [LibraryImport("/usr/lib/libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint getenv(string name);
}
