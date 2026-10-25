// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
#if SHARPEMU_HOST_MACOS
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Host.MacOS;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

// The two-process split's GPU side, in a fresh process: receive the owner's guest memory over
// Mach, map the backing as a mirror, replicate the views at guest address + K, and read and write
// guest bytes there. The parent drives it over stdin/stdout so a remap lands between two syncs.
[SupportedOSPlatform("macos")]
internal static class GuestMirrorProcessWorker
{
    internal const ulong Marker = 0x5348_5250_4D49_5252;
    internal const ulong Chunk = 0x10000;

    internal static unsafe int Run(string arguments)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_GUEST_MIRROR_PROCESS") != "1") return 80;
        try
        {
            var parts = arguments.Split(';');
            var service = parts[0];
            var address = ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var size = ulong.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var host = MacOSHost.CreateGuestMirrorMemory();
            var clock = Stopwatch.StartNew();
            HostBackingObject? mirror;
            HostSharedRegion? table;
            GuestBackingExportKind kind;
            int objects;
            using (var share = MacOSHost.ConnectGuestMemory(service, TimeSpan.FromSeconds(20)))
            {
                var connected = clock.Elapsed.TotalMilliseconds;
                kind = share.Backing.Kind;
                objects = share.Backing.Entries.Count;
                clock.Restart();
                Assert.True(host.TryMapMirror(share.Backing, out mirror));
                var mapped = clock.Elapsed.TotalMilliseconds;
                Assert.True(host.TryMapSharedRegion(share.ViewTableDescriptor, share.ViewTableBytes, writable: false, out table));
                Console.WriteLine(FormattableString.Invariant(
                    $"child-mapped kind={kind} objects={objects} backing=0x{share.Backing.Size:X} connect_ms={connected:F2} mirror_ms={mapped:F2}"));
            }

            // The descriptor and ports are released: the mappings keep the objects.
            using var heldMirror = mirror!;
            using var heldTable = table!;
            clock.Restart();
            // The default replica: all of the guest range, at 16 TiB, as the GPU process would make it.
            using var replica = GuestReplica.TryAttach(host, heldMirror, heldTable);
            Assert.NotNull(replica);
            Assert.True(replica!.Covers(address, size));
            var attached = clock.Elapsed.TotalMilliseconds;
            var translator = new GuestMirrorTranslator(heldTable, heldMirror);
            Assert.Equal(Marker, *(ulong*)replica!.HostAddress(address));
            Assert.True(translator.TryTranslate(address, 8, out var pointer));
            Assert.Equal(Marker, *(ulong*)pointer);
            *(ulong*)replica.HostAddress(address + 8) = ~Marker;
            Console.WriteLine(FormattableString.Invariant(
                $"child-ready attach_ms={attached:F2} replayed={replica.ReplayedSequence} architecture={RuntimeInformation.ProcessArchitecture}"));
            Console.Out.Flush();

            var command = Console.ReadLine()?.Split(' ');
            Assert.Equal("sync", command?[0]);
            var expected = byte.Parse(command![1], CultureInfo.InvariantCulture);
            clock.Restart();
            var replayed = replica.Sync();
            var synced = clock.Elapsed.TotalMilliseconds;
            Assert.True(replica.IsComplete);
            Assert.Equal(expected, *(byte*)replica.HostAddress(address + Chunk));
            Assert.Equal(Marker, *(ulong*)replica.HostAddress(address));
            Console.WriteLine(FormattableString.Invariant($"child-synced replayed={replayed} resyncs={replica.Resyncs} sync_ms={synced:F3}"));
            Console.Out.Flush();
            Assert.Equal("exit", Console.ReadLine());
            Console.WriteLine($"guest-mirror-child-pass kind={kind} architecture={RuntimeInformation.ProcessArchitecture}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 83; }
    }
}

[SupportedOSPlatform("macos")]
public sealed class GuestMirrorProcessTests(ITestOutputHelper output)
{
    // A small backing, the full 16.4 GiB guest backing, and the split layout's 17.9 GiB backing
    // (SplitBackingLayout: the arena and module images in it too) to measure what sharing it costs.
    [GuestMirrorProcessTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task AnotherProcessSharesGuestMemoryThroughItsReplica(bool chunked, bool fullSize, bool splitLayout)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_GUEST_MIRROR_PROCESS"));
        const ulong Chunk = GuestMirrorProcessWorker.Chunk;
        var size = splitLayout ? SplitBackingLayout.Default.End : fullSize ? GuestMemoryLayout.BackingBytes : 256 * Chunk;
        var host = MacOSHost.CreateViewMemory();
        var clock = Stopwatch.StartNew();
        var views = CreateShareable(host, size, chunked);
        var created = clock.Elapsed.TotalMilliseconds;
        var export = Assert.IsType<GuestBackingExport>(views.BackingExport);
        var table = Assert.IsType<GuestViewTable>(views.ViewTable);
        const ulong range = 4 * Chunk;
        var address = host.FindUnmappedRange(0xF0_0000_0000, range, 0xFC_0000_0000);
        Assert.NotEqual(0UL, address);
        Assert.Equal(address, host.ReserveHole(address, range));
        Process? child = null;
        try
        {
            Assert.True(views.TryMapReservedRange(address, range, 8 * Chunk, HostPageProtection.ReadWrite, out _));
            unsafe { *(ulong*)address = GuestMirrorProcessWorker.Marker; }
            using var server = MacOSHost.ServeGuestMemory(export, table.Region, out var service);
            output.WriteLine(FormattableString.Invariant(
                $"owner kind={export.Kind} objects={export.Entries.Count} backing=0x{size:X} create_ms={created:F2} service={service}"));

            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var start = new ProcessStartInfo(Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet"))
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(GuestMirrorProcessTests).Assembly.Location);
            start.ArgumentList.Add(FormattableString.Invariant($"--guest-mirror-child={service};{address:X};{range:X}"));
            child = Process.Start(start)!;
            var stderr = child.StandardError.ReadToEndAsync();
            output.WriteLine(await ReadUntil(child, "child-mapped"));
            output.WriteLine(await ReadUntil(child, "child-ready"));
            unsafe { Assert.Equal(~GuestMirrorProcessWorker.Marker, *(ulong*)(address + 8)); }

            // Remap the second chunk of the view onto other bytes; the child sees it from its next sync.
            unsafe { new Span<byte>((void*)(views.AliasBase + 40 * Chunk), (int)Chunk).Fill(0x5D); }
            Assert.True(views.Unmap(address + Chunk, Chunk, out var preserved));
            Assert.True(preserved);
            Assert.True(views.TryMapReservedRange(address + Chunk, Chunk, 40 * Chunk, HostPageProtection.ReadWrite, out _));
            await child.StandardInput.WriteLineAsync("sync 93");
            await child.StandardInput.FlushAsync();
            output.WriteLine(await ReadUntil(child, "child-synced"));
            await child.StandardInput.WriteLineAsync("exit");
            await child.StandardInput.FlushAsync();
            var tail = await child.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var errors = await stderr.WaitAsync(TimeSpan.FromSeconds(10));
            output.WriteLine(tail);
            if (errors.Length != 0) output.WriteLine(errors);
            Assert.Equal(0, child.ExitCode);
            Assert.Empty(errors);
            Assert.Contains("guest-mirror-child-pass kind=" + export.Kind, tail);
        }
        finally
        {
            if (child is { HasExited: false }) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            child?.Dispose();
            foreach (var (offset, length) in new[] { (0UL, Chunk), (Chunk, Chunk), (2 * Chunk, 2 * Chunk) })
                if (views.Contains(address + offset, length)) views.Unmap(address + offset, length, out _);
            host.FreeHole(address, range);
            views.Dispose();
        }
    }

    // The backing switches are read when the backing is made; tests in this assembly run one at a time.
    private static SharedBackingViews CreateShareable(IHostViewMemory host, ulong size, bool chunked)
    {
        var backing = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_BACKING");
        var shareable = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_BACKING_SHAREABLE");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_BACKING", chunked ? null : "shm");
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_BACKING_SHAREABLE", "1");
            var views = new SharedBackingViews(host, size);
            Assert.True(views.IsAvailable);
            return views;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_BACKING", backing);
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_BACKING_SHAREABLE", shareable);
        }
    }

    private static async Task<string> ReadUntil(Process child, string prefix)
    {
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.NotNull(line);
            if (line!.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }
    }
}

internal sealed class GuestMirrorProcessTheoryAttribute : TheoryAttribute
{
    public GuestMirrorProcessTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_GUEST_MIRROR_PROCESS") != "1")
            Skip = "Cross-process guest memory sharing requires separately authorized SHARPEMU_TEST_REQUIRE_GUEST_MIRROR_PROCESS=1.";
    }
}
#endif
