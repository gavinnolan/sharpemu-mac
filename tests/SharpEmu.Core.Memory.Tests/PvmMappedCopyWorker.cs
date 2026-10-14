// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Core.Memory.Tests;

internal static partial class PvmMappedCopyWorker
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || !args[0].StartsWith("--pvm-mapped-copy=", StringComparison.Ordinal)) return 80;
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_PVM_MAPPED_COPY_NATIVE") != "1") return 81;
#if SHARPEMU_HOST_MACOS
        if (!OperatingSystem.IsMacOS()) return 82;
        var scenario = args[0]["--pvm-mapped-copy=".Length..];
        try
        {
            var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
            Assert.False(string.IsNullOrWhiteSpace(expected));
            Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
            var framework = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK");
            Assert.False(string.IsNullOrWhiteSpace(framework));
            Assert.Equal(framework, RuntimeInformation.FrameworkDescription);
            Stage("composition", scenario);
            Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
                assembly => assembly.GetName().Name == "SharpEmu.Libs.Tests");
            Assert.False(HostMemoryAuthority.IsInitialized);
            HostMemoryAuthority.Initialize(SharpEmu.Host.MacOS.MacOSHost.CreateMemory);
            var memory = HostMemoryAuthority.Current;
            var views = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
            Assert.IsAssignableFrom<IHostMappedCopyMemory>(views);
            // Read the actual startup measurement; no synthetic ledger/provider
            // or fabricated geometry is substituted when it is unavailable.
            var state = views.GetType().Assembly.GetType("SharpEmu.Host.MacOS.MacMappedCopyState", true)!;
            var ledger = (HostMappedCopyLedger?)state.GetProperty("Ledger", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Assert.NotNull(ledger);
            var kernelPage = ledger!.NativePageSize;
            Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var expectedPage));
            Assert.Equal(expectedPage, kernelPage);
            Assert.True(kernelPage > 0 && kernelPage <= GuestMemoryLayout.GuestPage);
            Assert.Equal(0ul, GuestMemoryLayout.GuestPage % kernelPage);
            Assert.Equal((ulong)Environment.SystemPageSize, views.PageSize);
            Assert.Equal(0ul, kernelPage % views.PageSize);
            Console.WriteLine($"pvm-geometry kernel_page={kernelPage} provider_page={views.PageSize} granularity={views.Granularity}");
            Execute(memory, views, kernelPage, scenario);
            Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
                assembly => assembly.GetName().Name == "SharpEmu.Libs.Tests");
            Stage("pass", scenario);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 83; }
#else
        return 82;
#endif
    }

    private static void Stage(string name, string scenario)
    {
        Console.WriteLine($"pvm-mapped-copy stage={name} case={scenario} architecture={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription}");
        Console.Out.Flush();
    }

#if SHARPEMU_HOST_MACOS
    private static void Execute(IHostMemory memory, IHostViewMemory views, ulong kernelPage, string scenario)
    {
        if (scenario is not ("full-copy" or "unrelated-canonical" or "aba-close" or "unsupported-wrapper") &&
            !IsSingleViewWriteScenario(scenario))
            throw new ArgumentException("Unknown worker scenario.", nameof(scenario));
        PhysicalVirtualMemory? pvm = null;
        Exception? primary = null;
        ulong unrelated = 0;
        var constructorReturned = false;
        var disposalReturned = false;
        try
        {
            Stage("real-constructor-self-test-enter", scenario);
            // The fresh-process outer deadline bounds the constructor's real
            // self-test and low-address search too. No uninitialized instance,
            // private owner injection or search fallback is permitted here.
            pvm = new PhysicalVirtualMemory(memory, views, backingBytes: 8 * GuestMemoryLayout.GuestPage,
                preReserveGuestAddressSpace: false);
            constructorReturned = true;
            Stage("real-constructor-self-test-return", scenario);
            if (IsSingleViewWriteScenario(scenario))
            {
                ExecuteSingleViewWrite(pvm, views, kernelPage, scenario);
                return;
            }
            var tracked = new TrackedCpuMemory(pvm);
            var extent = 2 * GuestMemoryLayout.GuestPage;
            var alignment = Math.Max(views.Granularity, GuestMemoryLayout.GuestPage);
            Assert.True(pvm.TryHoldRangeAtOrAbove(0x2_0000_0000, extent, alignment, out var address));
            Assert.True(address != 0 && address % alignment == 0);
            Assert.True(pvm.TryHoldRange(address, extent));
            var offset = GuestMemoryLayout.GuestPage;
            Assert.True(pvm.TryMapBacked(address, extent, offset, GuestPageProtection.Read | GuestPageProtection.Write, out var failure), $"map failure={failure}");
            Stage("owned-map", scenario);
            var seed = new byte[checked((int)extent)];
            for (var index = 0; index < seed.Length; index++)
                seed[index] = (byte)((index % (int)kernelPage) * 19 + (index / (int)kernelPage) * 73 + 11);
            Assert.True(pvm.TryWriteBacking(address, seed));
            var detached = new byte[seed.Length];
            Assert.Equal(CpuMappedCopyResult.Copied, tracked.TryReadMappedCopy(address, extent, address, detached, out var old));
            Assert.NotNull(old);
            Assert.Equal(seed, detached);
            Assert.True(tracked.IsMappedCopyObservationCurrent(old!));
            Assert.Equal(address, old!.FootprintAddress);
            Assert.Equal(extent, old.FootprintByteCount);
            Assert.Equal(extent, old.SourceByteCount);
            var segment = Assert.Single(old.Segments);
            Assert.Equal(offset, segment.MappingBackingOffset);
            Assert.Equal(kernelPage, segment.ProviderPageSize);
            Assert.Equal(address, segment.MappingAddress);
            Assert.Equal(extent, segment.MappingByteCount);
            Assert.True(segment.BackingIdentity > 0 && segment.MappingIncarnation > 0);
            Console.WriteLine($"pvm-owned-copy address=0x{address:X} size={extent} physical_offset={offset} backing_identity={segment.BackingIdentity} incarnation={segment.MappingIncarnation}");

            if (scenario == "full-copy")
            {
                // Source lies on the second kernel page; full kernel footprint
                // is retained even when the detached payload is only 257 bytes.
                var suffix = new byte[257];
                var source = address + kernelPage + 31;
                Assert.Equal(CpuMappedCopyResult.Copied, tracked.TryReadMappedCopy(address, extent, source, suffix, out var partial));
                Assert.Equal(seed.AsSpan(checked((int)kernelPage + 31), suffix.Length).ToArray(), suffix);
                Assert.Equal(source, partial!.SourceAddress);
                Assert.Equal(extent, partial.FootprintByteCount);
                Assert.True(pvm.TryWriteBacking(address, new byte[seed.Length]));
                Assert.Equal(seed, detached);
                Assert.True(tracked.IsMappedCopyObservationCurrent(old)); // mapping only; bytes changed
                var current = new byte[seed.Length];
                Assert.Equal(CpuMappedCopyResult.Copied, pvm.TryReadMappedCopy(address, extent, address, current, out _));
                Assert.All(current, value => Assert.Equal((byte)0, value));
            }
            else if (scenario == "unrelated-canonical")
            {
                unrelated = memory.Allocate(0, kernelPage, HostPageProtection.ReadWrite);
                Assert.NotEqual(0ul, unrelated);
                Assert.True(unrelated + kernelPage <= address || address + extent <= unrelated);
                Assert.True(tracked.IsMappedCopyObservationCurrent(old));
                Assert.True(memory.Protect(unrelated, kernelPage, HostPageProtection.ReadOnly, out _));
                Assert.True(tracked.IsMappedCopyObservationCurrent(old));
                Assert.True(memory.Protect(unrelated, kernelPage, HostPageProtection.ReadWrite, out _));
                Assert.True(tracked.IsMappedCopyObservationCurrent(old));
                var released = unrelated;
                unrelated = 0; // consume before uncertain native outcome; no cleanup retry
                Assert.True(memory.Free(released));
                Assert.True(tracked.IsMappedCopyObservationCurrent(old));
                Assert.Equal(CpuMappedCopyResult.Copied, tracked.TryReadMappedCopy(address, extent, address, detached, out _));
                Assert.Equal(seed, detached);
            }
            else if (scenario == "aba-close")
            {
                Assert.True(pvm.TryUnmapBacked(address, extent));
                Assert.False(tracked.IsMappedCopyObservationCurrent(old));
                Assert.True(pvm.TryMapBacked(address, extent, offset + extent, GuestPageProtection.Read | GuestPageProtection.Write, out failure), $"remap failure={failure}");
                var replacement = Enumerable.Repeat((byte)0xD7, seed.Length).ToArray();
                Assert.True(pvm.TryWriteBacking(address, replacement));
                Assert.Equal(CpuMappedCopyResult.Copied, tracked.TryReadMappedCopy(address, extent, address, detached, out var newer));
                Assert.Equal(replacement, detached);
                Assert.Equal(offset + extent, newer!.Segments[0].MappingBackingOffset);
                Assert.NotEqual(segment.MappingIncarnation, newer.Segments[0].MappingIncarnation);
                Assert.Equal(segment.BackingIdentity, newer.Segments[0].BackingIdentity);
                Assert.False(tracked.IsMappedCopyObservationCurrent(old));
                var closing = pvm;
                pvm = null; // consume before uncertain cleanup; never retry it
                closing.Dispose();
                disposalReturned = true;
                Assert.False(tracked.IsMappedCopyObservationCurrent(newer));
                Assert.Equal(CpuMappedCopyResult.Unavailable, tracked.TryReadMappedCopy(address, extent, address, detached, out var closed));
                Assert.Null(closed);
            }
            else
            {
                var wrapper = new UnsupportedMemory(pvm);
                var unsupported = new TrackedCpuMemory(wrapper);
                Array.Fill(detached, (byte)0xA5);
                Assert.Equal(CpuMappedCopyResult.Unavailable, unsupported.TryReadMappedCopy(address, extent, address, detached, out var unavailable));
                Assert.Null(unavailable);
                Assert.False(unsupported.IsMappedCopyObservationCurrent(old));
                Assert.Equal(0, wrapper.ReadCalls);
                Assert.All(detached, value => Assert.Equal((byte)0xA5, value));
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            try
            {
                if (unrelated != 0)
                {
                    var released = unrelated; unrelated = 0;
                    if (!memory.Free(released)) throw new IOException("Unrelated allocation release declined; outcome retained until process exit.");
                }
            }
            catch (Exception error) { cleanup = error; }
            try
            {
                if (pvm is not null)
                {
                    var closing = pvm; pvm = null;
                    closing.Dispose();
                    disposalReturned = true;
                }
            }
            catch (Exception error) { cleanup ??= error; }
            if (cleanup is not null)
            {
                if (primary is null) throw cleanup;
                try { primary.Data["PvmMappedCopyCleanup"] = cleanup; } catch { }
                Console.Error.WriteLine($"pvm-secondary-cleanup {cleanup}");
            }
            else Stage(!constructorReturned ? "constructor-no-return-process-exit" :
                disposalReturned ? "owned-cleanup-return" : "cleanup-not-confirmed-process-exit", scenario);
        }
    }

    private sealed class UnsupportedMemory(ICpuMemory inner) : ICpuMemory
    {
        internal int ReadCalls;
        public bool TryRead(ulong address, Span<byte> destination) { ReadCalls++; return inner.TryRead(address, destination); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => inner.TryWrite(address, source);
    }
#endif
}
