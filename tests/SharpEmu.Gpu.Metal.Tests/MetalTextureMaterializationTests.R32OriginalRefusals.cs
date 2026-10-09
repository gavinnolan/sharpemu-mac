// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private static object[]? r32OriginalRefusalQuarantine;

    // Fresh ordinary-Mac-provider workers. These rows deliberately do NOT
    // create a Metal device/encoder/command buffer or claim status4.
    [R32OriginalRefusalFact] public void ActualR32OriginalDifferentVirtualAddressPhysicalOverlapRefusesAdmission() => R32OriginalRefusal("physical-overlap");
    [R32OriginalRefusalFact] public void ActualR32OriginalMappingReplacementRefusesAdmission() => R32OriginalRefusal("mapping-replacement");
    [R32OriginalRefusalFact] public void ActualR32OriginalRightsChangeRefusesAdmission() => R32OriginalRefusal("rights");
    [R32OriginalRefusalFact] public void ActualR32OriginalSessionChangeRefusesAdmission() => R32OriginalRefusal("session");
    [R32OriginalRefusalFact] public void ActualR32OriginalDifferentPixelsCannotAdoptCopiedProof() => R32OriginalRefusal("pixels");

    private void R32OriginalRefusal(string scenario)
    {
        SelectedMappedSeedAssertClr();
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_R32_ORIGINAL_REFUSAL"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE"));
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        Assert.Equal(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"));
        Assert.Null(r32OriginalRefusalQuarantine);
        var queue = new SelectedMipQueueScope();
        var host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
        var views = new SharedBackingViews(host, 0xC0000);
        SelectedMappedSeedHole? originalHole = null, aliasHole = null;
        R32DisjointMemory? memory = null;
        GuestDrawTexture? aliasSeed = null;
        ulong address = 0, aliasAddress = 0;
        var tracker = GuestImageWriteTracker.Enabled;
        Exception? primary = null;
        var completedBefore = Presenter.GetField("_completedGuestWorkSequence", Methods)!.GetValue(null);
        try
        {
            Assert.True(views.IsAvailable);
            originalHole = new(host); address = originalHole.Address;
            Assert.True(address < 1UL << 40 && (address & 65535) == 0);
            Assert.True(views.TryMapReservedRange(address, 0xC0000, 0, HostPageProtection.ReadWrite, out _));
            var (guest, expectedMip0, _) = R32DisjointIndependentSeed();
            Assert.True(views.TryWriteBacking(address, guest));
            Assert.False(GuestImageWriteTracker.TryGetContentToken(address + 0x40000, out _));
            GuestImageWriteTracker.Configure(true); // cold production observation; no prearm/managed coverage
            memory = new(views);
            var read = R32OriginalBinding(address, storage: false);
            var snapshot = Assert.Single(R32OriginalSnapshots(memory, read));
            Assert.Equal(expectedMip0, snapshot.RgbaPixels);
            var mapping = Assert.Single(memory.Copies);
            Assert.Equal((address, 0xC0000UL, address + 0x40000, 0x80000UL),
                (mapping.FootprintAddress, mapping.FootprintByteCount, mapping.SourceAddress, mapping.SourceByteCount));
            Assert.True(memory.IsMappedCopyObservationCurrent(mapping));
            Assert.False(memory.IsMappedCopyWriteWatchCurrent(mapping)); // actual original watch was consumed after copy
            Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var kernelPage));
            Assert.All(mapping.Segments, segment => Assert.Equal(kernelPage, segment.ProviderPageSize));

            if (scenario == "pixels")
            {
                var faithful = snapshot with { Descriptor = snapshot.Descriptor!.ToArray() };
                Presenter.GetMethod("CopyCapturedTextureSource", Methods)!.Invoke(null, [snapshot, faithful]);
                Assert.True((bool)Presenter.GetMethod("IsOwnedR32SampledMipZero", Methods)!.Invoke(null, [faithful])!);
                var changed = snapshot with { RgbaPixels = snapshot.RgbaPixels.ToArray() };
                var refusal = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("CopyCapturedTextureSource", Methods)!.Invoke(null, [snapshot, changed]));
                Assert.IsType<InvalidOperationException>(refusal.InnerException);
                Assert.False((bool)Presenter.GetMethod("IsOwnedR32SampledMipZero", Methods)!.Invoke(null, [changed])!);
            }
            else
            {
                GuestDrawTexture[] textures = [snapshot];
                if (scenario == "physical-overlap")
                {
                    aliasHole = new(host); aliasAddress = aliasHole.Address;
                    Assert.True(aliasAddress != address && aliasAddress < 1UL << 40 && (aliasAddress & 65535) == 0);
                    // Alias M1 at B+20000 resolves to A's physical M0+40000.
                    // Only the actual original M1 prefix is mapped; no fabricated
                    // full backing receipt or new original M0 capture is supplied.
                    Assert.True(views.TryMapReservedRange(aliasAddress, 0x40000, 0x20000, HostPageProtection.ReadWrite, out _));
                    aliasSeed = Assert.Single(R32OriginalSnapshots(memory, R32OriginalBinding(aliasAddress, storage: true)));
                    var aliasMapping = memory.Copies.Last();
                    Assert.Equal((aliasAddress, 0x40000UL, aliasAddress + 0x20000, 0x20000UL),
                        (aliasMapping.FootprintAddress, aliasMapping.FootprintByteCount, aliasMapping.SourceAddress, aliasMapping.SourceByteCount));
                    var originalSegment = Assert.Single(mapping.Segments);
                    var aliasSegment = Assert.Single(aliasMapping.Segments);
                    Assert.Equal(originalSegment.BackingIdentity, aliasSegment.BackingIdentity);
                    Assert.Equal(originalSegment.MappingBackingOffset + mapping.SourceAddress - originalSegment.MappingAddress,
                        aliasSegment.MappingBackingOffset + aliasMapping.SourceAddress - aliasSegment.MappingAddress);
                    Assert.True(memory.IsMappedCopyObservationCurrent(mapping));
                    Assert.True(memory.IsMappedCopyObservationCurrent(aliasMapping));
                    textures = [snapshot, aliasSeed];
                }
                else if (scenario == "mapping-replacement")
                {
                    Assert.True(views.Unmap(address, 0xC0000, out _));
                    Assert.True(views.TryMapReservedRange(address, 0xC0000, 0, HostPageProtection.ReadWrite, out _));
                    Assert.False(memory.IsMappedCopyObservationCurrent(mapping));
                }
                else if (scenario == "rights")
                {
                    Assert.True(host.ChangeAccess(address, 0xC0000, HostPageProtection.ReadOnly));
                    Assert.False(memory.IsMappedCopyObservationCurrent(mapping));
                }
                else if (scenario == "session")
                {
                    Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
                    Assert.True(memory.IsMappedCopyObservationCurrent(mapping));
                }
                else throw new ArgumentOutOfRangeException(nameof(scenario));

                // A native-only global output causes REAL Submit admission to
                // inspect the sampled proof. It has no guest writeback and is
                // never encoded/allocated because admission must first refuse.
                var global = new GuestMemoryBuffer(address, new byte[4], 4, 4, false, Writable: true, WriteBackToGuest: false);
                var failure = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("SubmitComputeDispatch", Methods)!.Invoke(null,
                    [0UL, null, textures, new[] { global }, 1u, 1u, 1u, 0u, 0u, 0u, true, null]));
                Assert.IsType<InvalidOperationException>(failure.InnerException);
                Assert.Contains(scenario == "physical-overlap" ? "physically overlapping" : scenario == "session" ? "session" : "mapping changed",
                    failure.InnerException!.Message);
            }

            Assert.Equal(0L, Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null));
            Assert.Equal(0, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
            Assert.Equal(completedBefore, Presenter.GetField("_completedGuestWorkSequence", Methods)!.GetValue(null));
            Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!);
            Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!);
            Assert.Equal(0, memory.OrdinaryReads);
            output.WriteLine($"r32-original-refusal scenario={scenario}; actualCLR={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; kernel_ledger_page={kernelPage}; provider_page={host.PageSize}; actual-original-mapping; no-admission/no-native-allocation/no-ack; ordinary-Mac-provider-only/no-Metal-status-claim");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null;
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
            if (aliasSeed is not null) Attempt(() => R32ReleaseUnadmittedOriginalSeed(aliasSeed));
            Attempt(queue.Dispose);
            Attempt(() => { if (address != 0) GuestImageWriteTracker.Untrack(address); });
            Attempt(() => { if (address != 0) GuestImageWriteTracker.Untrack(address + 0x40000); });
            Attempt(() => { if (aliasAddress != 0) GuestImageWriteTracker.Untrack(aliasAddress); });
            Attempt(() =>
            {
                try { views.Dispose(); if (!views.CleanupComplete) throw new IOException("Original R32 refusal views cleanup incomplete."); }
                catch { originalHole?.Quarantine(); aliasHole?.Quarantine(); throw; }
            });
            if (cleanup is null)
            {
                Attempt(() => aliasHole?.Dispose());
                Attempt(() => originalHole?.Dispose());
            }
            if (cleanup is not null) r32OriginalRefusalQuarantine = [queue, views, originalHole!, aliasHole!, memory!, aliasSeed!];
            Attempt(() => GuestImageWriteTracker.Configure(tracker));
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["R32OriginalRefusalCleanup"] = cleanup; } catch { }
            }
        }
    }

    private static AgcExports.TranslatedImageBinding R32OriginalBinding(ulong address, bool storage)
    {
        uint[] raw = [(uint)(address >> 8), 0xC1600000, 0x003FC07F, storage ? 0x90911FACu : 0x90900924u, 0, 0x00700080, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, storage, storage ? 1u : 0u, [], ResourceDescriptor: raw,
            Shape: new(false, false, storage, false, TextureNumericClass.Float), Access: storage ? GuestImageAccess.Write : GuestImageAccess.Read);
    }

    private static IReadOnlyList<GuestDrawTexture> R32OriginalSnapshots(ICpuMemory memory, AgcExports.TranslatedImageBinding binding) =>
        (IReadOnlyList<GuestDrawTexture>)R32DisjointManagedSnapshots(memory, binding)!;

    private static void R32ReleaseUnadmittedOriginalSeed(GuestDrawTexture texture)
    {
        var association = SelectedMipAssociation(texture);
        var seeds = Presenter.GetField("_selectedStorageMipSeeds", Methods)!.GetValue(null)!;
        object?[] lookup = [association, null];
        Assert.True((bool)seeds.GetType().GetMethod("TryGetValue")!.Invoke(seeds, lookup)!);
        var seed = lookup[1]!;
        Assert.Equal(0, seed.GetType().GetField("Phase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(seed));
        object?[] release = [seed, null];
        Presenter.GetMethod("TryReleaseSelectedSeedWatch", Methods)!.Invoke(null, release);
        Assert.Null(release[1]); // exact consumed watch, never handed to a native owner
    }
}

internal sealed class R32OriginalRefusalFactAttribute : FactAttribute
{
    public R32OriginalRefusalFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_R32_ORIGINAL_REFUSAL") != "1")
            Skip = "Requires a fresh real Mac original-provider refusal worker; no Metal execution.";
    }
}
