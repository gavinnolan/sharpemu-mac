// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.GpuRemote.Tests;

public sealed unsafe class SharedMemoryRegionTests
{
    [MacOSFact]
    public void SecondMappingSeesTheFirstMappingsBytes()
    {
        using var created = SharedMemoryRegion.Create(SharedMemoryRegion.CreateUniqueName("setest"), 40_000);
        Assert.Equal((nuint)(3 * SharedMemoryRegion.Granularity), created.Size);
        for (var i = 0; i < (int)created.Size; i += 4096)
        {
            created.Pointer[i] = (byte)(i / 4096 + 1);
        }

        using var opened = SharedMemoryRegion.Open(created.Name);
        Assert.Equal(created.Size, opened.Size);
        Assert.False(opened.IsOwner);
        Assert.NotEqual((nint)created.Pointer, (nint)opened.Pointer);
        for (var i = 0; i < (int)opened.Size; i += 4096)
        {
            Assert.Equal((byte)(i / 4096 + 1), opened.Pointer[i]);
        }

        opened.Pointer[123] = 0xA5;
        Assert.Equal(0xA5, created.Pointer[123]);
    }

    [MacOSFact]
    public void CreatedObjectIsOwnerReadWriteOnly()
    {
        // Proves the variadic shm_open mode reaches the kernel on this architecture: a fixed
        // third argument on arm64 would leave the mode to whatever sat on the stack.
        using var created = SharedMemoryRegion.Create(SharedMemoryRegion.CreateUniqueName("setest"), 4096);
        var descriptor = RemoteNative.ShmOpen(created.Name, RemoteNative.O_RDWR, 0);
        Assert.True(descriptor >= 0);
        try
        {
            Assert.True(RemoteNative.TryGetFileStatus(descriptor, out var mode, out var size));
            Assert.Equal(0x180, mode & 0x1FF);
            Assert.Equal((long)created.Size, size);
        }
        finally
        {
            _ = RemoteNative.close(descriptor);
        }
    }

    [MacOSFact]
    public void CreateRefusesAnExistingName()
    {
        using var created = SharedMemoryRegion.Create(SharedMemoryRegion.CreateUniqueName("setest"), 4096);
        Assert.Throws<IOException>(() => SharedMemoryRegion.Create(created.Name, 4096));
    }

    [MacOSFact]
    public void UnlinkRemovesTheNameButKeepsTheMapping()
    {
        using var created = SharedMemoryRegion.Create(SharedMemoryRegion.CreateUniqueName("setest"), 4096);
        created.Pointer[0] = 7;
        created.Unlink();
        Assert.Throws<IOException>(() => SharedMemoryRegion.Open(created.Name));
        Assert.Equal(7, created.Pointer[0]);
    }

    [Theory]
    [InlineData("no-slash")]
    [InlineData("/two/slashes")]
    [InlineData("/this-name-is-far-too-long-for-darwin")]
    public void RejectsNamesDarwinWouldRefuse(string name)
    {
        Assert.Throws<ArgumentException>(() => SharedMemoryRegion.Create(name, 4096));
    }
}
