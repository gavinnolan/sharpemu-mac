// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [SelectedMappedSeedNativeTheory]
    [InlineData("unrelated")]
    [InlineData("overlap")]
    [InlineData("aba")]
    [InlineData("content")]
    [InlineData("session")]
    [InlineData("closed")]
    [InlineData("unobserved")]
    [InlineData("currentness-unavailable")]
    [InlineData("foreign-origin")]
    [InlineData("wrong-thread")]
    [InlineData("wrong-sequence")]
    [InlineData("consumed-owner")]
    [InlineData("readonly")]
    public void ActualMacOriginalMappedSeedAndFirstUseVetoes(string kind)
    {
        SelectedMappedSeedAssertClr();
        Assert.True(OperatingSystem.IsMacOS());
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_SELECTED_MAPPED_SEED_NATIVE"));
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        output.WriteLine($"selected mapped seed actualCLR={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription}; case={kind}; actual-Mac-alias-copy/no-GPU-upload");
        using var queue = new SelectedMipQueueScope();
        var host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
        var foreign = new SharedBackingViews(host, 786432);
        var views = new SharedBackingViews(host, 786432);
        SelectedMappedSeedHole? hole = null;
        var enabled = GuestImageWriteTracker.Enabled;
        var sequenceField = Presenter.GetField("_executingGuestWorkSequence", Methods)!;
        var oldSequence = sequenceField.GetValue(null);
        ulong address = 0;
        Exception? primary = null;
        try
        {
            Assert.True(views.IsAvailable && foreign.IsAvailable);
            hole = new(host);
            address = hole.Address;
            Assert.True(address < (1UL << 40)); // complete descriptor address must be representable
            Assert.True(views.TryMapReservedRange(address, 786432, 0, HostPageProtection.ReadWrite, out _));
            var (bytes, expected) = SelectedMipSeed();
            Assert.True(views.TryWriteBacking(address, bytes));
            GuestImageWriteTracker.Configure(kind != "unobserved");
            if (kind != "unobserved") GuestImageWriteTracker.TrackManagedWriter(address, 786432);
            var memory = new ActualSelectedMappedMemory(views);
            memory.AfterCopy = () =>
            {
                if (kind is "unrelated" or "unobserved" or "wrong-thread" or "wrong-sequence" or "consumed-owner" or "readonly")
                {
                    var raw = HostMemoryAuthority.Current;
                    var unrelated = raw.Allocate(0, 16384, HostPageProtection.ReadWrite);
                    Assert.NotEqual(0ul, unrelated);
                    try { Assert.True(raw.Protect(unrelated, 16384, HostPageProtection.ReadOnly, out _)); }
                    finally { Assert.True(raw.Free(unrelated)); }
                }
                if (kind == "overlap") Assert.True(host.ChangeAccess(address, 262144, HostPageProtection.ReadOnly));
                if (kind == "aba")
                {
                    Assert.True(views.Unmap(address, 786432, out _));
                    Assert.True(views.TryMapReservedRange(address, 786432, 0, HostPageProtection.ReadWrite, out _));
                }
                if (kind == "content") GuestImageWriteTracker.NotifyManagedWrite(address + 131072, 4);
                if (kind == "session") Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
                if (kind == "closed") views.Dispose();
                if (kind == "currentness-unavailable") memory.CertaintyUnavailable = true; // conservative contract refusal, no forged syscall result
                if (kind == "foreign-origin") memory.ForeignCurrentness = foreign; // actual other backing rejects original observation
            };
            var rawDescriptor = SelectedMipRaw(); rawDescriptor[0] = checked((uint)(address >> 8));
            if (kind is "overlap" or "aba" or "content" or "session" or "closed" or "unobserved" or "currentness-unavailable" or "foreign-origin")
            {
                var refused = Assert.Throws<TargetInvocationException>(() => SelectedMipSnapshots(memory, rawDescriptor));
                Assert.IsType<InvalidOperationException>(refused.InnerException);
                Assert.Equal(0, memory.OrdinaryReads);
                Assert.Equal(0, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
                return;
            }
            var texture = Assert.Single(SelectedMipSnapshots(memory, rawDescriptor,
                access: kind == "readonly" ? GuestImageAccess.Read : GuestImageAccess.Read | GuestImageAccess.Write));
            Assert.Equal(expected, texture.RgbaPixels); Assert.Equal(0, memory.OrdinaryReads);
            Assert.Equal(address, memory.Original!.FootprintAddress);
            Assert.Equal(262144ul, memory.Original.FootprintByteCount);
            Assert.Equal(address + 131072, memory.Original.SourceAddress);
            Assert.Equal(131072ul, memory.Original.SourceByteCount);
            var actualKernelPage = Assert.Single(memory.Original.Segments).ProviderPageSize;
            Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var expectedKernel));
            Assert.Equal(expectedKernel, actualKernelPage);
            output.WriteLine($"selected mapped seed kernel_page={actualKernelPage} provider_page={host.PageSize}");
            var association = SelectedMipAssociation(texture);
            var epoch = (long)association.GetType().GetProperty("MappingEpoch")!.GetValue(association)!;
            Assert.NotEqual(epoch, HostMemory.MappingGeneration);
            // The original graph provenance is never retimestamped or promoted.
            var provenanceTable = Presenter.GetField("_cpuTextureSnapshotProvenance", Methods)!.GetValue(null)!;
            object?[] provenanceArgs = [texture, null];
            Assert.True((bool)provenanceTable.GetType().GetMethod("TryGetValue")!.Invoke(provenanceTable, provenanceArgs)!);
            Assert.False((bool)provenanceArgs[1]!.GetType().GetProperty("Stable")!.GetValue(provenanceArgs[1])!);
            object work;
            long sequence;
            if (kind == "readonly")
                (work, sequence) = SubmitReadonlySelectedWithGlobalWriter(texture, address + 524288);
            else
            {
                var workType = Presenter.GetNestedType("ComputeGuestDispatch", BindingFlags.NonPublic)!;
                work = Activator.CreateInstance(workType, [0UL, null, new[] { texture }, Array.Empty<GuestMemoryBuffer>(),
                    1u, 1u, 1u, 0u, 0u, 0u, null])!;
                lock (Presenter.GetField("_gate", Methods)!.GetValue(null)!)
                    sequence = (long)Presenter.GetMethod("EnqueueGuestWorkLocked", Methods)!.Invoke(null, [work])!;
            }
            Assert.True(sequence > 0);
            sequenceField.SetValue(null, kind == "wrong-sequence" ? sequence + 1 : sequence);
            var begin = Presenter.GetMethod("BeginSelectedStorageMipSeedExecution", Methods)!;
            if (kind == "wrong-sequence")
            {
                var refused = Assert.Throws<TargetInvocationException>(() => begin.Invoke(null, [work]));
                Assert.IsType<InvalidOperationException>(refused.InnerException);
                return;
            }
            var validate = Presenter.GetMethod("ValidateSelectedStorageMipOwner", Methods)!;
            using ((IDisposable)begin.Invoke(null, [work])!)
            {
                validate.Invoke(null, [association, false]); // exact first-use CPU proof; no native image/receipt fabricated
                var live = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, [association, true]));
                Assert.IsType<InvalidOperationException>(live.InnerException); // strict native owner even within scope
                var nested = Assert.Throws<TargetInvocationException>(() => begin.Invoke(null, [work]));
                Assert.IsType<InvalidOperationException>(nested.InnerException);
                if (kind == "wrong-thread")
                {
                    Exception? failure = null;
                    var thread = new Thread(() => { try { validate.Invoke(null, [association, false]); } catch (Exception error) { failure = error; } });
                    thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
                    Assert.IsType<InvalidOperationException>(Assert.IsType<TargetInvocationException>(failure).InnerException);
                }
            }
            var consumed = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, [association, false]));
            Assert.IsType<InvalidOperationException>(consumed.InnerException);
            Assert.Equal(epoch, association.GetType().GetProperty("MappingEpoch")!.GetValue(association));
            if (kind == "readonly")
            {
                AssertSelectedWriterMetadataEmpty();
                output.WriteLine("actual-readonly-selected-copy public-submit/take/exact-first-use/finally-consumed; other global buffer writable; selected image writer/pending debt remained empty; no GPU execution or global-byte-write claim");
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            try
            {
                sequenceField.SetValue(null, oldSequence);
                if (address != 0) GuestImageWriteTracker.Untrack(address);
                GuestImageWriteTracker.Configure(enabled);
            }
            catch (Exception error) { cleanup = error; }
            try { views.Dispose(); if (!views.CleanupComplete) throw new IOException("Actual selected seed view cleanup incomplete."); }
            catch (Exception error) { cleanup ??= error; hole?.Quarantine(); }
            try { foreign.Dispose(); if (!foreign.CleanupComplete) throw new IOException("Actual foreign backing cleanup incomplete."); }
            catch (Exception error) { cleanup ??= error; }
            try { hole?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            if (cleanup is not null)
            {
                if (primary is null) throw cleanup;
                try { primary.Data["SelectedMappedSeedCleanup"] = cleanup; } catch { }
            }
        }
    }

    private sealed class ActualSelectedMappedMemory(SharedBackingViews views) : ICpuMemory, ICpuMappedCopySource
    {
        internal Action? AfterCopy;
        internal CpuMappedCopyObservation? Original;
        internal bool CertaintyUnavailable;
        internal SharedBackingViews? ForeignCurrentness;
        internal int OrdinaryReads;
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? observation)
        {
            var result = views.TryReadMappedCopy(footprint, count, source, bytes, out observation);
            if (result == CpuMappedCopyResult.Copied) { Original = observation; AfterCopy?.Invoke(); }
            return result;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => !CertaintyUnavailable &&
            (ForeignCurrentness ?? views).IsMappedCopyObservationCurrent(observation);
        public bool TryRead(ulong source, Span<byte> bytes) { OrdinaryReads++; return views.TryReadBacking(source, bytes); }
        public bool TryWrite(ulong destination, ReadOnlySpan<byte> bytes) => false;
    }

    private sealed class SelectedMappedSeedHole : IDisposable
    {
        private readonly IHostViewMemory host;
        private readonly ulong allocation;
        private const ulong Size = 1048576;
        private bool quarantined, consumed;
        internal ulong Address { get; }
        internal SelectedMappedSeedHole(IHostViewMemory host)
        {
            this.host = host;
            ulong owned = 0;
            Assert.Equal(0, seed_mach_vm_allocate(seed_mach_task_self(), ref owned, Size, 1));
            allocation = owned;
            try
            {
                Address = checked((owned + 65535) & ~65535ul);
                Assert.True(Address >= owned && Address - owned <= Size - 786432);
                if (!host.UnmapView(owned, Size)) throw new IOException("Whole owned Mach extent placeholder conversion declined.");
            }
            catch { quarantined = true; throw; } // unknown conversion never releases/reclaims an assumed vacancy
        }
        internal void Quarantine() => quarantined = true;
        public void Dispose()
        {
            if (consumed || quarantined) return;
            consumed = true;
            try
            {
                if (!host.FreeHole(allocation, Size)) throw new IOException("Exact owned Mach extent release declined.");
            }
            catch { quarantined = true; throw; }
        }
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_task_self")] private static extern uint seed_mach_task_self();
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_vm_allocate")] private static extern int seed_mach_vm_allocate(uint task, ref ulong address, ulong size, int flags);
}

internal sealed class SelectedMappedSeedNativeTheoryAttribute : TheoryAttribute
{
    public SelectedMappedSeedNativeTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_SELECTED_MAPPED_SEED_NATIVE") != "1")
            Skip = "Actual selected seed Mac/provider controls require a separately granted fresh process; no native pass without opt-in.";
    }
}
