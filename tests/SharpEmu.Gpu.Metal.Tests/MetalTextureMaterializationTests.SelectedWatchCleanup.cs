// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private const BindingFlags SelectedCleanupMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedWatchScopeConsumesAllRestoresTlsAndPreservesPrimary(bool hasPrimary)
    {
        SelectedMappedSeedAssertClr();
        var failureField = Presenter.GetField("_selectedNativeOriginalCleanupFailure", Methods)!;
        var tlsField = Presenter.GetField("_selectedStorageMipSeedExecutingWork", Methods)!;
        var oldFailure = failureField.GetValue(null); var oldTls = tlsField.GetValue(null);
        var first = new InvalidOperationException("first watch cleanup");
        var second = new InvalidOperationException("second watch cleanup");
        var memory = new CleanupOnlySelectedWatchMemory(first, second);
        var work = new object();
        var seeds = NewCleanupSeeds(memory, work, 2);
        memory.BeforeRelease = () =>
        {
            var seed = seeds.GetValue(memory.Releases - 1)!;
            Assert.Equal(3, CleanupSeedField(seed, "Phase"));
            Assert.Equal(1, CleanupSeedField(seed, "WriteWatchConsumed"));
            Assert.Null(CleanupSeedField(seed, "Thread")); Assert.Null(CleanupSeedField(seed, "Work"));
        };
        var scopeType = Presenter.GetNestedType("SelectedStorageMipSeedExecutionScope", BindingFlags.NonPublic)!;
        var scope = Activator.CreateInstance(scopeType, SelectedCleanupMembers, null, [work, seeds], null)!;
        var finish = scopeType.GetMethod("Finish", SelectedCleanupMembers)!;
        var primary = hasPrimary ? new InvalidOperationException("original dispatch failure") : null;
        try
        {
            failureField.SetValue(null, null); tlsField.SetValue(null, work);
            if (primary is null)
                Assert.Same(first, Assert.Throws<TargetInvocationException>(() => finish.Invoke(scope, [null])).InnerException);
            else
            {
                finish.Invoke(scope, [primary]);
                Assert.Same(first, primary.Data["MetalSelectedSeedWatchCleanup"]);
            }
            Assert.Equal(2, memory.Releases); Assert.Null(tlsField.GetValue(null));
            Assert.Same(first, failureField.GetValue(null));
            foreach (var seed in seeds)
            {
                Assert.Equal(3, CleanupSeedField(seed!, "Phase"));
                Assert.Equal(1, CleanupSeedField(seed!, "WriteWatchConsumed"));
                Assert.Null(CleanupSeedField(seed!, "Thread")); Assert.Null(CleanupSeedField(seed!, "Work"));
            }
            finish.Invoke(scope, [primary]); Assert.Equal(2, memory.Releases);
        }
        finally { failureField.SetValue(null, oldFailure); tlsField.SetValue(null, oldTls); }
    }

    [Fact]
    public void SelectedNativeWatchCloseConsumesRegistryBeforeCallbacksAndAttemptsAll()
    {
        SelectedMappedSeedAssertClr();
        var failureField = Presenter.GetField("_selectedNativeOriginalCleanupFailure", Methods)!;
        var oldFailure = failureField.GetValue(null);
        var sessionField = Presenter.GetField("_selectedStorageMipSession", Methods)!;
        var oldSession = (long)sessionField.GetValue(null)!;
        var close = Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!;
        var proofs = (IDictionary)Presenter.GetField("_selectedNativeOriginalProofs", Methods)!.GetValue(null)!;
        Assert.Empty(proofs);
        foreach (var name in new[] { "_selectedStorageMipImages", "_selectedStorageMipOwners", "_selectedStorageMipPending", "_selectedStorageMipNativeQueues" })
            Assert.Empty((IDictionary)Presenter.GetField(name, Methods)!.GetValue(null)!);
        var first = new InvalidOperationException("native owner watch cleanup");
        var memory = new CleanupOnlySelectedWatchMemory(first, null)
        {
            BeforeRelease = () => { Assert.Empty(proofs); close.Invoke(null, null); }
        };
        var seeds = NewCleanupSeeds(memory, new object(), 2);
        var keyType = Presenter.GetNestedType("SelectedStorageMipKey", BindingFlags.NonPublic)!;
        var proofType = Presenter.GetNestedType("SelectedNativeOriginalProof", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, true)!;
        try
        {
            failureField.SetValue(null, null);
            for (var i = 0; i < seeds.Length; i++)
            {
                var keyCtor = keyType.GetConstructors(SelectedCleanupMembers).Single();
                var descriptor = Activator.CreateInstance(keyCtor.GetParameters()[1].ParameterType)!;
                var key = keyCtor.Invoke([-(long)i - 1, descriptor, 1u]);
                // Cleanup-only objects: no handle, validated observation, native
                // publication or currentness success is fabricated by this control.
                var proof = Activator.CreateInstance(proofType, SelectedCleanupMembers, null, [null, seeds.GetValue(i), image], null)!;
                proofs.Add(key, proof);
            }
            Assert.Same(first, Assert.Throws<TargetInvocationException>(() => close.Invoke(null, null)).InnerException);
            Assert.Empty(proofs); Assert.Equal(2, memory.Releases); Assert.Same(first, failureField.GetValue(null));
            Assert.Equal(checked(oldSession + 1), sessionField.GetValue(null));
            Assert.Equal(false, Presenter.GetField("_selectedStorageMipCloseInProgress", Methods)!.GetValue(null));
            Assert.Null(Presenter.GetMethod("ReleaseSelectedNativeOriginalProofsLocked", Methods)!.Invoke(null, null));
            Assert.Equal(2, memory.Releases);
            foreach (var seed in seeds) Assert.Equal(1, CleanupSeedField(seed!, "WriteWatchConsumed"));
        }
        finally { proofs.Clear(); failureField.SetValue(null, oldFailure); sessionField.SetValue(null, oldSession); }
    }

    [Fact]
    public void SelectedOriginalWatchCleanupCannotReplaceMalformedCopyCause()
    {
        SelectedMappedSeedAssertClr();
        var cleanup = new InvalidOperationException("secondary original watch cleanup");
        var memory = new CleanupOnlySelectedWatchMemory(cleanup, null);
        var mapping = NewCleanupObservation();
        var primary = new InvalidOperationException("original malformed copy");
        Presenter.GetMethod("ReleaseOriginalSelectedWriteWatch", Methods)!.Invoke(null, [memory, mapping, primary]);
        Assert.Same(cleanup, primary.Data["MetalSelectedOriginalWatchCleanup"]); Assert.Equal(1, memory.Releases);
    }

    private static object? CleanupSeedField(object seed, string name) => seed.GetType().GetField(name, SelectedCleanupMembers)!.GetValue(seed);
    private static Array NewCleanupSeeds(CleanupOnlySelectedWatchMemory source, object work, int count)
    {
        var seedType = Presenter.GetNestedType("SelectedStorageMipSeed", BindingFlags.NonPublic)!;
        var ctor = seedType.GetConstructors(SelectedCleanupMembers).Single();
        var seeds = Array.CreateInstance(seedType, count);
        for (var i = 0; i < count; i++)
        {
            var read = Activator.CreateInstance(ctor.GetParameters()[2].ParameterType)!;
            var seed = ctor.Invoke([source, NewCleanupObservation(), read]);
            seedType.GetField("Phase", SelectedCleanupMembers)!.SetValue(seed, 2);
            seedType.GetField("Work", SelectedCleanupMembers)!.SetValue(seed, work);
            seedType.GetField("Thread", SelectedCleanupMembers)!.SetValue(seed, Thread.CurrentThread);
            seeds.SetValue(seed, i);
        }
        return seeds;
    }
    private static CpuMappedCopyObservation NewCleanupObservation()
    {
        var segments = new[] { new CpuMappingFootprintSegment(0x10000, 0x1000, 0x10000, 0x1000,
            1, 1, 0x1000, 0, 0x1000, GuestPageProtection.Read) };
        // Constructor shape metadata only; the refusing source never treats it
        // as an OS receipt or returns Copied/current/positive native authority.
        return (CpuMappedCopyObservation)Activator.CreateInstance(typeof(CpuMappedCopyObservation),
            SelectedCleanupMembers, null, [new object(), 0x10000UL, 0x1000UL, 0x10000UL, 4UL, segments], null)!;
    }
    private sealed class CleanupOnlySelectedWatchMemory(Exception? first, Exception? second) : ICpuMemory, ICpuMappedReadWatchSource
    {
        internal int Releases;
        internal Action? BeforeRelease;
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation)
        {
            Releases++; BeforeRelease?.Invoke();
            var error = Releases == 1 ? first : second;
            if (error is not null) throw error;
        }
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => false;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation) => false;
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong bytes, ulong source, Span<byte> destination,
            out CpuMappedCopyObservation? observation) { observation = null; return CpuMappedCopyResult.Unavailable; }
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong bytes, ulong source, Span<byte> destination,
            out CpuMappedCopyObservation? observation) => TryReadMappedCopy(address, bytes, source, destination, out observation);
    }
}
