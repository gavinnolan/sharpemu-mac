// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler.Metal.Tests;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    // Each row requires a NEW worker. Actual Submit -> Drain creates/uploads the
    // native owner; no provider-only success or fabricated protected token.
    [SelectedNativeOriginalFact] public void SelectedNativeOriginalUnrelatedTwoDispatches() => ActualSelectedNativeOwner("unrelated");
    [SelectedNativeOriginalFact] public void SelectedNativeOriginalReadonlyTwoDispatches() => ActualSelectedNativeOwner("readonly");
    [SelectedNativeOriginalFact] public void SelectedNativeOriginalPhysicalWriteVeto() => ActualSelectedNativeOwner("physical-write");
    [SelectedNativeOriginalFact] public void SelectedNativeOriginalManagedWriteVeto() => ActualSelectedNativeOwner("managed-write");
    [SelectedNativeOriginalFact] public void SelectedNativeOriginalRemapABAVeto() => ActualSelectedNativeOwner("aba");
    [SelectedNativeOriginalFact] public void SelectedNativeOriginalSessionVeto() => ActualSelectedNativeOwner("session");

    private void ActualSelectedNativeOwner(string scenario)
    {
        SelectedMappedSeedAssertClr();
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE"));
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        Assert.True(RequireDevice());
        var pending = new SelectedMipQueueScope();
        var host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
        var views = new SharedBackingViews(host, 786432);
        SelectedMappedSeedHole? hole = null;
        SelectedNativeExecutionState? native = null;
        var enabled = GuestImageWriteTracker.Enabled;
        ulong address = 0;
        Exception? primary = null;
        try
        {
            Assert.True(views.IsAvailable);
            hole = new(host); address = hole.Address;
            Assert.True(address < 1UL << 40);
            Assert.True(views.TryMapReservedRange(address, 786432, 0, HostPageProtection.ReadWrite, out _));
            var (guest, expected) = SelectedMipSeed();
            Assert.True(views.TryWriteBacking(address, guest));
            GuestImageWriteTracker.Configure(true); // BeginReadSnapshot establishes REAL protection.
            var memory = new SelectedNativeMemory(views);
            native = new(memory);
            var raw = SelectedMipRaw(); raw[0] = checked((uint)(address >> 8));
            var first = Assert.Single(SelectedMipSnapshots(memory, raw,
                access: scenario == "readonly" ? GuestImageAccess.Read : GuestImageAccess.Read | GuestImageAccess.Write));
            var originalMapping = Assert.IsType<CpuMappedCopyObservation>(memory.Original);
            Assert.Equal(16384ul, Assert.Single(originalMapping.Segments).ProviderPageSize);
            Assert.True(GuestImageWriteTracker.TryGetProtectedObservedContentToken(address, 262144, out var originalToken));
            Assert.True(memory.IsMappedCopyWriteWatchCurrent(originalMapping));
            var association = SelectedMipAssociation(first);
            var epoch = (long)association.GetType().GetProperty("MappingEpoch")!.GetValue(association)!;
            var coordinate = (11 * 256 + 17) * 4;
            if (scenario == "readonly")
            {
                native.Dispatch(first, load: true, 0, address + 524288);
                Assert.Equal(expected.AsSpan(coordinate, 4).ToArray(), ReadSelectedResult(views, address).AsSpan(0, 4).ToArray());
            }
            else
            {
                native.Dispatch(first, load: false, 42.5f, address + 524288);
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(coordinate, 4), BitConverter.SingleToUInt32Bits(42.5f));
            }
            var images = (IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!;
            var image = Assert.Single(images.Values.Cast<object>());
            var texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(expected, ReadNativeRows(texture, 256, 128, 4));
            Assert.Single(((IDictionary)Presenter.GetField("_selectedNativeOriginalProofs", Methods)!.GetValue(null)!).Values);
            var proof = ((IDictionary)Presenter.GetField("_selectedNativeOriginalProofs", Methods)!.GetValue(null)!).Values.Cast<object>().Single();
            var seed = proof.GetType().GetField("Seed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(proof)!;
            Assert.Same(originalMapping, seed.GetType().GetField("Mapping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(seed));

            var unrelated = HostMemoryAuthority.Current.Allocate(0, 16384, HostPageProtection.ReadWrite);
            Assert.NotEqual(0ul, unrelated);
            try { Assert.True(HostMemoryAuthority.Current.Protect(unrelated, 16384, HostPageProtection.ReadOnly, out _)); }
            finally { Assert.True(HostMemoryAuthority.Current.Free(unrelated)); }
            Assert.NotEqual(epoch, HostMemory.MappingGeneration);
            Assert.True(memory.IsMappedCopyObservationCurrent(originalMapping));
            Assert.True(memory.IsMappedCopyWriteWatchCurrent(originalMapping));
            Assert.True(GuestImageWriteTracker.TryGetProtectedObservedContentToken(address, 262144, out var unchanged));
            Assert.Equal(originalToken, unchanged);

            GuestDrawTexture? next = null;
            if (scenario == "session") next = Assert.Single(SelectedMipSnapshots(memory, raw));
            if (scenario is "physical-write" or "managed-write")
            {
                if (scenario == "managed-write") Assert.True(memory.TryWrite(address + 131072, new byte[] { 91 }));
                else Assert.True(views.TryWriteBacking(address + 131072, new byte[] { 91 }));
                Assert.False(memory.IsMappedCopyWriteWatchCurrent(originalMapping));
            }
            if (scenario == "aba")
            {
                Assert.True(views.Unmap(address, 786432, out _));
                Assert.True(views.TryMapReservedRange(address, 786432, 0, HostPageProtection.ReadWrite, out _));
                Assert.False(memory.IsMappedCopyObservationCurrent(originalMapping));
            }
            if (scenario == "session") Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
            if (scenario is "physical-write" or "managed-write" or "aba")
            {
                var refused = Assert.Throws<TargetInvocationException>(() => SelectedMipSnapshots(memory, raw));
                Assert.IsType<InvalidOperationException>(refused.InnerException);
                Assert.Equal(0, memory.OrdinaryReads); // no stale CPU fallback
                output.WriteLine($"selected-native case={scenario}; first production upload/dispatch CBstatus=4; original proof veto; no second dispatch or fallback");
                return;
            }
            if (scenario == "session")
            {
                var refused = Assert.Throws<TargetInvocationException>(() => native.Dispatch(next!, false, -17.25f, address + 524288));
                Assert.IsType<InvalidOperationException>(refused.InnerException);
                output.WriteLine("selected-native case=session; released native handle never inspected; stale DTO admission veto");
                return;
            }
            next = Assert.Single(SelectedMipSnapshots(memory, raw, access: GuestImageAccess.Read));
            Assert.Empty(next.RgbaPixels); Assert.Equal(1, memory.WatchedCopies); Assert.Equal(0, memory.OrdinaryReads);
            native.Dispatch(next, load: true, 0, address + 524288);
            var result = ReadSelectedResult(views, address);
            Assert.Equal(expected.AsSpan(coordinate, 4).ToArray(), result.AsSpan(0, 4).ToArray());
            Assert.Equal(new byte[8], result.AsSpan(4, 8).ToArray());
            Assert.Equal(BitConverter.SingleToUInt32Bits(1f), BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(12)));
            var second = Assert.Single(SelectedMipSnapshots(memory, raw));
            native.Dispatch(second, load: false, -17.25f, address + 524288);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(coordinate, 4), BitConverter.SingleToUInt32Bits(-17.25f));
            Assert.Equal(expected, ReadNativeRows(texture, 256, 128, 4));
            Assert.Same(image, Assert.Single(images.Values.Cast<object>()));
            Assert.Same(originalMapping, memory.Original); Assert.Equal(epoch, association.GetType().GetProperty("MappingEpoch")!.GetValue(association));
            result.CopyTo(guest.AsSpan(524288, 16));
            var actualGuest = new byte[guest.Length]; Assert.True(views.TryReadBacking(address, actualGuest));
            Assert.Equal(guest, actualGuest); // GPU stores never publish tiled guest RAM in this slice.
            output.WriteLine($"selected-native case={scenario}; actualCLR={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}; kernel_page=16384; provider_page={host.PageSize}; upload/store/unrelated-effect/read/store; all32768 native texels+786432 guest bytes exact; original mapping/token unchanged; completedCBstatus=4");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { cleanup ??= error; } }
            Attempt(() => native?.Dispose());
            if (native?.Quarantined != true) Attempt(pending.Dispose);
            Attempt(() => { if (address != 0) GuestImageWriteTracker.Untrack(address); GuestImageWriteTracker.Configure(enabled); });
            Attempt(() =>
            {
                try { views.Dispose(); if (!views.CleanupComplete) throw new IOException("Selected native alias cleanup incomplete."); }
                catch { hole?.Quarantine(); throw; }
            });
            Attempt(() => hole?.Dispose());
            if (cleanup is not null) { if (primary is null) throw cleanup; try { primary.Data["SelectedNativeCleanup"] = cleanup; } catch { } }
        }
    }

    private static byte[] ReadSelectedResult(SharedBackingViews views, ulong address)
    { var result = new byte[16]; Assert.True(views.TryReadBacking(address + 524288, result)); return result; }

    private sealed class SelectedNativeMemory(SharedBackingViews views) : ICpuMemory, ICpuMappedReadWatchSource
    {
        internal CpuMappedCopyObservation? Original;
        internal int OrdinaryReads, WatchedCopies;
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation) =>
            views.TryReadMappedCopy(address, count, source, bytes, out observation);
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        { var result = views.TryReadMappedCopyWithWriteWatch(address, count, source, bytes, out observation); if (result == CpuMappedCopyResult.Copied) { Original = observation; WatchedCopies++; } return result; }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => views.IsMappedCopyObservationCurrent(observation);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation) => views.IsMappedCopyWriteWatchCurrent(observation);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation) => views.ReleaseMappedCopyWriteWatch(observation);
        public bool TryRead(ulong source, Span<byte> bytes) { OrdinaryReads++; return views.TryReadBacking(source, bytes); }
        public bool TryWrite(ulong destination, ReadOnlySpan<byte> bytes)
        { GuestImageWriteTracker.NotifyManagedWrite(destination, (ulong)bytes.Length); return views.TryWriteBacking(destination, bytes); }
    }

    private sealed class SelectedNativeExecutionState : IDisposable
    {
        private readonly List<(FieldInfo Field, object? Value)> saved = [];
        private readonly List<object> shaders = [];
        private readonly nint pool, queue;
        private bool unknown;
        internal bool Quarantined => unknown;
        internal SelectedNativeExecutionState(ICpuMemory memory)
        {
            Assert.False((bool)Presenter.GetField("_textureSourceGraphEnabled", Methods)!.GetValue(null)!);
            Assert.Null(Presenter.GetField("_currentUploadPage", Methods)!.GetValue(null));
            Assert.Empty((IEnumerable)Presenter.GetField("_retiredUploadPages", Methods)!.GetValue(null)!);
            Assert.Empty((IEnumerable)Presenter.GetField("_freeUploadPages", Methods)!.GetValue(null)!);
            Assert.Empty((IEnumerable)Presenter.GetField("_touchedUploadPages", Methods)!.GetValue(null)!);
            pool = reuseSend(reuseClass("NSAutoreleasePool"), Selector("new"));
            if (pool == 0) throw new InvalidOperationException("Selected native worker has no autorelease pool.");
            queue = reuseSend(Device.Value, Selector("newCommandQueue"));
            if (queue == 0) { SendVoid(pool, Selector("drain")); throw new InvalidOperationException("Selected native worker has no owned queue."); }
            try
            {
            foreach (var (name, value) in new (string, object?)[] { ("_guestMemory", memory), ("_completedGuestWorkSequence", 0L),
                ("_executingGuestWorkSequence", 0L), ("_lastFlushedCommandBuffer", (nint)0), ("_batchCommandBuffer", (nint)0), ("_batchOpen", false), ("_uploadTouchStamp", 1) })
            { var field = Presenter.GetField(name, Methods)!; saved.Add((field, field.GetValue(null))); field.SetValue(null, value); }
            }
            catch (Exception primary)
            {
                foreach (var (field, value) in saved) field.SetValue(null, value);
                try { SendVoid(queue, Selector("release")); } catch (Exception secondary) { try { primary.Data["QueueConstructionCleanup"] = secondary; } catch { } }
                try { SendVoid(pool, Selector("drain")); } catch (Exception secondary) { try { primary.Data["PoolConstructionCleanup"] = secondary; } catch { } }
                throw;
            }
        }
        internal void Dispatch(GuestDrawTexture texture, bool load, float value, ulong outputAddress)
        {
            Assert.True(Gen5ImageFixtures.TryCompile(load ? "ImageLoad" : "ImageStore", 9, out var shader, out var error,
                unifiedFormat: 22, dmask: load ? 15u : 1u, addressValues: [17, 11], storeResults: load,
                storeValues: load ? null : [BitConverter.SingleToUInt32Bits(value), 0, 0, 0]), error);
            var compiled = Activator.CreateInstance(Presenter.Assembly.GetTypes().Single(type => type.Name == "MetalCompiledGuestShader"), [shader])!;
            shaders.Add(compiled);
            var binding = new GuestStageBindings(GuestStageKind.Compute, load ? [0] : [], [0], [], [], null, [], [], false, false, 0);
            GuestMemoryBuffer[] buffers = load ? [new(outputAddress, new byte[16], 16, 16, false, Writable: true)] : [];
            var sequence = (long)Presenter.GetMethod("SubmitComputeDispatch", Methods)!.Invoke(null,
                [0UL, compiled, new[] { texture }, buffers, 1u, 1u, 1u, 0u, 0u, 0u, load, binding])!;
            Assert.True(sequence > 0);
            try
            {
                Presenter.GetMethod("DrainGuestWorkCore", Methods)!.Invoke(null, [Device.Value, queue, 5L * System.Diagnostics.Stopwatch.Frequency]);
                var command = (nint)Presenter.GetField("_lastFlushedCommandBuffer", Methods)!.GetValue(null)!;
                Assert.NotEqual((nint)0, command); SendVoid(command, Selector("waitUntilCompleted"));
                Assert.Equal(4ul, ReadInteger(command, Selector("status")));
                Assert.True((bool)Presenter.GetMethod("IsGuestWorkCompletedLocked", Methods)!.Invoke(null, [sequence])!);
            }
            catch { unknown = true; throw; }
        }
        public void Dispose()
        {
            // Unknown native completion retains all native objects until this
            // bounded fresh worker exits; never free storage beneath AGX.
            if (unknown) return;
            Exception? failure = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { failure ??= error; } }
            var pages = ((IEnumerable)Presenter.GetField("_retiredUploadPages", Methods)!.GetValue(null)!).Cast<object>()
                .Concat(((IEnumerable)Presenter.GetField("_freeUploadPages", Methods)!.GetValue(null)!).Cast<object>())
                .Append(Presenter.GetField("_currentUploadPage", Methods)!.GetValue(null)).OfType<object>().Distinct(ReferenceEqualityComparer.Instance).ToArray();
            foreach (var page in pages)
            {
                var last = (nint)page.GetType().GetField("LastCommandBuffer")!.GetValue(page)!;
                if (last != 0) Attempt(() => { SendVoid(last, Selector("waitUntilCompleted")); Assert.Equal(4ul, ReadInteger(last, Selector("status"))); });
            }
            if (failure is not null) { unknown = true; throw failure; }
            Attempt(() => Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null));
            foreach (var page in pages)
            {
                var last = (nint)page.GetType().GetField("LastCommandBuffer")!.GetValue(page)!;
                if (last != 0) Attempt(() => SendVoid(last, Selector("release")));
                Attempt(() => SendVoid((nint)page.GetType().GetField("Buffer")!.GetValue(page)!, Selector("release")));
            }
            var cache = (IDictionary)Presenter.GetField("_computePipelineCache", Methods)!.GetValue(null)!;
            foreach (var shader in shaders)
            {
                if (cache.Contains(shader)) { var pipeline = (nint)cache[shader]!; cache.Remove(shader); Attempt(() => SendVoid(pipeline, Selector("release"))); }
                var library = (nint)shader.GetType().GetField("CachedLibrary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shader)!;
                if (library != 0) Attempt(() => SendVoid(library, Selector("release")));
            }
            var command = (nint)Presenter.GetField("_lastFlushedCommandBuffer", Methods)!.GetValue(null)!;
            if (command != 0) Attempt(() => SendVoid(command, Selector("release")));
            ((IList)Presenter.GetField("_retiredUploadPages", Methods)!.GetValue(null)!).Clear();
            ((IList)Presenter.GetField("_touchedUploadPages", Methods)!.GetValue(null)!).Clear();
            var free = Presenter.GetField("_freeUploadPages", Methods)!.GetValue(null)!; free.GetType().GetMethod("Clear")!.Invoke(free, null);
            Presenter.GetField("_currentUploadPage", Methods)!.SetValue(null, null);
            foreach (var (field, value) in saved) field.SetValue(null, value);
            Attempt(() => SendVoid(queue, Selector("release"))); Attempt(() => SendVoid(pool, Selector("drain")));
            if (failure is not null) { unknown = true; throw failure; }
        }
    }
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")] private static extern nint reuseClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint reuseSend(nint receiver, nint selector);
}

internal sealed class SelectedNativeOriginalFactAttribute : FactAttribute
{
    public SelectedNativeOriginalFactAttribute()
    { if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_SELECTED_NATIVE_REUSE") != "1") Skip = "Selected original native proof requires separately granted fresh device workers."; }
}
