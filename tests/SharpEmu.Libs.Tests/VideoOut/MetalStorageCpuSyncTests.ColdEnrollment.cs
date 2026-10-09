// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Registry/lifetime veto controls only. The uninitialized Metal backend
    // supplied by State is a type/identity owner, never a native device proof.
    [Theory]
    [InlineData("current")] [InlineData("gate")] [InlineData("backend")]
    [InlineData("unknown-backend")] [InlineData("memory")] [InlineData("session")]
    [InlineData("closed")] [InlineData("failure")] [InlineData("texture-cleanup")]
    [InlineData("typed-cleanup")] [InlineData("volume-cleanup")]
    [InlineData("available-image")] [InlineData("native-image")] [InlineData("queued-image")]
    public void ColdOriginalOwnerVetoUsesOnlyExactRegistryAndLifetime(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var saved = new ColdOwnerState();
        var memory = new ColdFrontendMemory(VolumePhysical());
        MetalVideoPresenter.AttachGuestMemory(memory);
        var backend = GuestGpu.Current!;
        var session = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        object candidateBackend = backend;
        ICpuMemory candidateMemory = memory;
        switch (change)
        {
            case "backend": saved.Backend(new MetalGuestGpuBackend()); break;
            case "unknown-backend": candidateBackend = new object(); break;
            case "memory": candidateMemory = new Memory(VolumePhysical()); break;
            case "session": session++; break;
            case "closed": saved.Set("_closed", true); break;
            case "failure": saved.Set("_guestWorkFailure", new InvalidOperationException("owned control failure")); break;
            case "texture-cleanup": saved.Set("_textureSourceCleanupFailure", new InvalidOperationException("owned texture cleanup")); break;
            case "typed-cleanup": saved.Set("_typedCpuSyncCleanupFailure", new InvalidOperationException("owned typed cleanup")); break;
            case "volume-cleanup": saved.Set("_singletonVolumeOriginalCleanupFailure", new InvalidOperationException("owned volume cleanup")); break;
            case "available-image": saved.Add("_availableGuestImages", Address, 73u); break;
            case "native-image":
                // Empty metadata entry is enough to veto; no texture handle or
                // original mapping/native authority is fabricated.
                saved.Add("_guestImages", Address,
                    Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, true)!);
                break;
            case "queued-image": saved.Add("_guestImageWorkSequences", Address, 19L); break;
        }
        bool Query() => MetalVideoPresenter.IsStorageOriginalEnrollmentOwnerCurrent(memory: candidateMemory,
            backend: candidateBackend, address: Address, session: session);
        bool current;
        if (change == "gate") lock (Field("_gate").GetValue(null)!) current = Query();
        else current = Query();
        Assert.Equal(change == "current", current);
        Assert.Equal(0, memory.WaitAttempts); Assert.Equal(0, memory.LegacyWatchAttempts);
        Assert.Equal(0, memory.PlainAttempts); Assert.Equal(0, memory.Reads);
    }

    // Explicitly fresh-worker-only. The cached default-off flag is never
    // reflected or changed by these tests. The executor must start a filtered
    // worker with the flag set before loading the Metal assembly.
    [ColdEnrollmentTheory]
    [InlineData("unavailable")] [InlineData("rejected")] [InlineData("missing-original")]
    [InlineData("mapping")] [InlineData("watch")]
    public void ColdFrontendRefusalUsesOneOriginalAttemptAndNeverLegacyCapture(string refusal)
    {
        ActualArchitecture(); Assert.True(MetalVideoPresenter.StorageOriginalEnrollmentWaitRequested);
        using var state = new VolumeState(); using var saved = new ColdOwnerState();
        var memory = new ColdFrontendMemory(VolumePhysical()); MetalVideoPresenter.AttachGuestMemory(memory);
        if (refusal == "unavailable") memory.Result = CpuMappedCopyResult.Unavailable;
        if (refusal == "rejected") memory.Result = CpuMappedCopyResult.Rejected;
        if (refusal == "missing-original") memory.OmitObservation = true;
        if (refusal == "mapping") memory.MappingCurrent = false;
        if (refusal == "watch") memory.WatchCurrent = false;
        var failure = Assert.Throws<InvalidOperationException>(() => ColdFrontendSnapshot(memory));
        Assert.Contains("one retained original-copy attempt", failure.Message);
        Assert.Equal(1, memory.WaitAttempts);
        Assert.Equal(refusal is "unavailable" or "rejected" ? 0 : 1, memory.ByteCopies);
        Assert.Equal(refusal is "mapping" or "watch" ? 1 : 0, memory.Releases);
        Assert.Equal(0, memory.LegacyWatchAttempts); Assert.Equal(0, memory.PlainAttempts); Assert.Equal(0, memory.Reads);
    }

    [ColdEnrollmentTheory]
    [InlineData("session")] [InlineData("backend")] [InlineData("available-image")]
    [InlineData("queued-image")] [InlineData("token")]
    public void ColdFrontendRetainsFirstOwnerAndReadAcrossProviderVeto(string change)
    {
        ActualArchitecture(); Assert.True(MetalVideoPresenter.StorageOriginalEnrollmentWaitRequested);
        using var state = new VolumeState(); using var saved = new ColdOwnerState();
        var memory = new ColdFrontendMemory(VolumePhysical()); MetalVideoPresenter.AttachGuestMemory(memory);
        memory.BetweenVetoes = () =>
        {
            switch (change)
            {
                case "session": saved.Set("_selectedStorageMipSession", MetalVideoPresenter.CurrentStorageCpuSeedSession + 1); break;
                case "backend": saved.Backend(new MetalGuestGpuBackend()); break;
                case "available-image": saved.Add("_availableGuestImages", Address, 73u); break;
                case "queued-image": saved.Add("_guestImageWorkSequences", Address, 17L); break;
                case "token": GuestImageWriteTracker.NotifyManagedWrite(Address, 1); break;
            }
        };
        Assert.Throws<InvalidOperationException>(() => ColdFrontendSnapshot(memory));
        Assert.Equal(new[] { true, false }, memory.VetoResults);
        Assert.Equal(1, memory.WaitAttempts); Assert.Equal(0, memory.ByteCopies); Assert.Null(memory.LastMapping);
        Assert.Equal(0, memory.LegacyWatchAttempts); Assert.Equal(0, memory.PlainAttempts); Assert.Equal(0, memory.Reads);
        Assert.Equal(0, memory.Releases);
    }

    [ColdEnrollmentTheory]
    [InlineData("unknown-provider")] [InlineData("unknown-backend")]
    public void ColdFrontendRequiresExplicitProviderAndActualBackendBeforeFirstCopy(string change)
    {
        ActualArchitecture(); Assert.True(MetalVideoPresenter.StorageOriginalEnrollmentWaitRequested);
        using var state = new VolumeState(); using var saved = new ColdOwnerState();
        var memory = new ColdFrontendMemory(VolumePhysical()); MetalVideoPresenter.AttachGuestMemory(memory);
        if (change == "unknown-provider") memory.Capable = false;
        // Null would lazily invoke the process composition factory when
        // GuestGpu.Current is read. Use an explicit non-Metal metadata backend.
        else saved.Backend(new SharpEmu.Libs.Tests.Gpu.Pipelines.FakeShaderCompiler());
        Assert.Throws<NotSupportedException>(() => ColdFrontendSnapshot(memory));
        Assert.Equal(0, memory.WaitAttempts); Assert.Equal(0, memory.ByteCopies);
        Assert.Equal(0, memory.LegacyWatchAttempts); Assert.Equal(0, memory.PlainAttempts); Assert.Equal(0, memory.Reads);
    }

    [ColdEnrollmentFact]
    public void ColdFrontendMetadataControlKeepsFirstEpochReadAndSessionThroughOneCopy()
    {
        ActualArchitecture(); Assert.True(MetalVideoPresenter.StorageOriginalEnrollmentWaitRequested);
        using var state = new VolumeState(); using var saved = new ColdOwnerState();
        var memory = new ColdFrontendMemory(VolumePhysical()); MetalVideoPresenter.AttachGuestMemory(memory);
        var snapshot = ColdFrontendSnapshot(memory);
        try
        {
            var origin = GetOrigin(snapshot);
            var observation = Assert.IsType<MetalVideoPresenter.CpuTextureSnapshotObservation>(
                origin.GetType().GetProperty("Observation")!.GetValue(origin));
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            Assert.Equal(memory.InitialSession, origin.GetType().GetProperty("Session")!.GetValue(origin));
            Assert.Equal(memory.InitialEpoch, observation.MappingEpoch);
            Assert.Equal(memory.InitialToken, observation.Token);
            Assert.Equal(memory.InitialToken.WatchGeneration, snapshot.WriteGeneration);
            Assert.Equal(memory.Bytes, snapshot.RgbaPixels); Assert.NotSame(memory.Bytes, snapshot.RgbaPixels);
            Assert.Equal(new[] { true, true, true, true }, memory.VetoResults);
            Assert.Equal(1, memory.WaitAttempts); Assert.Equal(1, memory.ByteCopies);
            Assert.Equal(0, memory.LegacyWatchAttempts); Assert.Equal(0, memory.PlainAttempts); Assert.Equal(0, memory.Reads);
            Assert.Equal(0, memory.Releases);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1, memory.Releases);
    }

    private static GuestDrawTexture ColdFrontendSnapshot(ColdFrontendMemory memory) => Assert.Single(
        MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [LinearVolumeBinding()], out _));

    // Synthetic copied-read adapter: tests frontend sequencing and metadata
    // only. No provider exclusion, page rights or native continuity claim.
    private sealed class ColdFrontendMemory(byte[] bytes) : Memory(bytes), ICpuMappedReadWatchSource
    {
        internal bool Capable = true, MappingCurrent = true, WatchCurrent = true, OmitObservation;
        internal CpuMappedCopyResult Result = CpuMappedCopyResult.Copied;
        internal int WaitAttempts, ByteCopies, LegacyWatchAttempts, PlainAttempts, Releases;
        internal long InitialSession, InitialEpoch;
        internal GuestImageWriteTracker.ContentToken InitialToken;
        internal Action? BetweenVetoes;
        internal CpuMappedCopyObservation? LastMapping;
        internal readonly List<bool> VetoResults = [];
        public bool CanWaitForMappedReadWatchEnrollment => Capable;
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatchAdmissionWait(ulong footprintAddress,
            ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
            Func<bool> originalStillCurrent, out CpuMappedCopyObservation? observation)
        {
            WaitAttempts++; observation = null;
            Assert.Equal(Address, footprintAddress); Assert.Equal(65536UL, footprintByteCount);
            Assert.Equal(Address, sourceAddress); Assert.Equal(65536, destination.Length);
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
            InitialSession = MetalVideoPresenter.CurrentStorageCpuSeedSession;
            InitialEpoch = HostMemory.MappingGeneration;
            Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out InitialToken));
            for (var i = 0; i < 4; i++)
            {
                var current = originalStillCurrent(); VetoResults.Add(current);
                if (!current) return CpuMappedCopyResult.Rejected;
                if (i == 0) BetweenVetoes?.Invoke();
            }
            if (Result != CpuMappedCopyResult.Copied) return Result;
            ByteCopies++; Bytes.CopyTo(destination);
            if (!OmitObservation) observation = LastMapping = new(this, Address, 65536, Address, 65536,
                [new(Address, 65536, Address, 65536, 1, 1, 65536, 0, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]);
            return Result;
        }
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprintAddress,
            ulong footprintByteCount, ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation)
        { LegacyWatchAttempts++; observation = null; throw new InvalidOperationException("Cold path must not recapture a legacy watch."); }
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation)
        { PlainAttempts++; observation = null; throw new InvalidOperationException("Cold path must not fall back to generic capture."); }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation)
        { Assert.Same(LastMapping, observation); return MappingCurrent; }
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation)
        { Assert.Same(LastMapping, observation); return WatchCurrent; }
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation)
        { Assert.Same(LastMapping, observation); Releases++; }
    }

    private sealed class ColdOwnerState : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> fields = [];
        private readonly List<Action> undo = [];
        private readonly FieldInfo backendField = typeof(GuestGpu).GetField("_instance", Private)!;
        private readonly object? oldBackend;
        internal ColdOwnerState() => oldBackend = backendField.GetValue(null);
        internal void Backend(object? value) => backendField.SetValue(null, value);
        internal void Set(string name, object? value)
        {
            var field = Field(name);
            if (!fields.ContainsKey(field)) fields.Add(field, field.GetValue(null));
            field.SetValue(null, value);
        }
        internal void Add(string name, ulong address, object value)
        {
            var registry = (IDictionary)Field(name).GetValue(null)!;
            var existed = registry.Contains(address); var previous = registry[address];
            registry[address] = value;
            undo.Add(() => { if (existed) registry[address] = previous; else registry.Remove(address); });
        }
        public void Dispose()
        {
            for (var i = undo.Count - 1; i >= 0; i--) undo[i]();
            foreach (var entry in fields) entry.Key.SetValue(null, entry.Value);
            backendField.SetValue(null, oldBackend);
        }
    }
}

internal sealed class ColdEnrollmentTheoryAttribute : TheoryAttribute
{
    public ColdEnrollmentTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_METAL_STORAGE_ORIGINAL_ENROLLMENT_WAIT") != "1")
            Skip = "Requires a separately filtered fresh worker with cold original enrollment enabled before assembly load.";
    }
}

internal sealed class ColdEnrollmentFactAttribute : FactAttribute
{
    public ColdEnrollmentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_METAL_STORAGE_ORIGINAL_ENROLLMENT_WAIT") != "1")
            Skip = "Requires a separately filtered fresh worker with cold original enrollment enabled before assembly load.";
    }
}
