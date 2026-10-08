// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalCanonicalPublicationRoutingTests
{
    private const ulong Address = 0x71FA000000;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Presenter = typeof(MetalVideoPresenter);
    private static FieldInfo Field(string name) => Presenter.GetField(name, Static)!;
    private static object? Call(string name, params object?[] args) => Presenter.GetMethod(name, Static)!.Invoke(null, args);
    private static void Architecture()
    {
        var actual = RuntimeInformation.ProcessArchitecture.ToString();
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, actual);
        Console.WriteLine($"canonical-routing-managed architecture={actual}; framework={RuntimeInformation.FrameworkDescription}; no native permission proof");
    }

    private sealed class State : IDisposable
    {
        private readonly object? memory = Field("_guestMemory").GetValue(null);
        private readonly object? session = Field("_selectedStorageMipSession").GetValue(null);
        public State(ICpuMemory source) { Field("_guestMemory").SetValue(null, source); }
        public void Dispose() { Field("_guestMemory").SetValue(null, memory); Field("_selectedStorageMipSession").SetValue(null, session); }
    }
    // Own managed bytes and opaque immutable shape only. This does not fabricate
    // actual native provider/protection/completion receipts.
    private sealed class Memory : ICpuMemory, ICpuMappedCopySource
    {
        internal readonly byte[] Bytes = Enumerable.Range(0, 65536).Select(index => (byte)(index * 31 + 7)).ToArray();
        internal bool Current = true;
        internal int OrdinaryReads, MappedReads;
        internal CpuMappedCopyResult Result = CpuMappedCopyResult.Copied;
        internal Action? AfterCopy;
        public bool TryRead(ulong address, Span<byte> destination) { OrdinaryReads++; return false; }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintByteCount, ulong sourceAddress,
            Span<byte> destination, out CpuMappedCopyObservation? observation)
        {
            MappedReads++; observation = null;
            if (Result != CpuMappedCopyResult.Copied) return Result;
            Assert.Equal(Address, footprintAddress); Assert.Equal(Address, sourceAddress);
            Assert.Equal((ulong)destination.Length, footprintByteCount);
            Bytes.AsSpan(0, destination.Length).CopyTo(destination);
            observation = Mapping(Address, (ulong)destination.Length, 0);
            AfterCopy?.Invoke(); return Result;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => Current;
    }
    private static CpuMappedCopyObservation Mapping(ulong address, ulong bytes, ulong offset, long identity = 1) =>
        new(new object(), address, bytes, address, bytes,
            [new(address, 65536, address, bytes, 1, identity, 1024 * 1024, offset, 16384,
                GuestPageProtection.Read | GuestPageProtection.Write)]);

    [Fact]
    public void WritableBufferProofComesFromOneActualOwnedProducerCopy()
    {
        Architecture(); var memory = new Memory(); using var state = new State(memory);
        var bytes = new byte[65536];
        Assert.Equal(CpuMappedCopyResult.Copied, MetalVideoPresenter.TryReadFiniteStorageBuffer(memory, Address, bytes, out var mapping, out var session));
        Assert.Equal(memory.Bytes, bytes);
        var buffer = new GuestMemoryBuffer(Address, bytes, bytes.Length, (ulong)bytes.Length, false, true);
        MetalVideoPresenter.RegisterFiniteStorageBufferOrigin(buffer, memory, mapping!, session);
        Assert.NotNull(Call("RequireFiniteStorageBufferOrigin", buffer));
        Assert.Equal(1, memory.MappedReads); Assert.Equal(0, memory.OrdinaryReads);
    }

    [Theory]
    [InlineData("bytes")] [InlineData("session")] [InlineData("mapping")] [InlineData("memory")]
    public void SubmittedWritableBufferRejectsChangedOriginalProducerFacts(string change)
    {
        Architecture(); var memory = new Memory(); using var state = new State(memory);
        var bytes = new byte[65536];
        Assert.Equal(CpuMappedCopyResult.Copied, MetalVideoPresenter.TryReadFiniteStorageBuffer(memory, Address, bytes, out var mapping, out var session));
        var buffer = new GuestMemoryBuffer(Address, bytes, bytes.Length, (ulong)bytes.Length, false, true);
        MetalVideoPresenter.RegisterFiniteStorageBufferOrigin(buffer, memory, mapping!, session);
        switch (change)
        {
            case "bytes": bytes[^1] ^= 1; break;
            case "session": Field("_selectedStorageMipSession").SetValue(null, session + 1); break;
            case "mapping": memory.Current = false; break;
            case "memory": Field("_guestMemory").SetValue(null, new Memory()); break;
        }
        var failure = Assert.Throws<TargetInvocationException>(() => Call("RequireFiniteStorageBufferOrigin", buffer));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Equal(1, memory.MappedReads); Assert.Equal(0, memory.OrdinaryReads);
    }

    [Theory]
    [InlineData(CpuMappedCopyResult.Unavailable)] [InlineData(CpuMappedCopyResult.Rejected)]
    public void UnqualifiedBufferCannotRegisterAnOriginalCopy(CpuMappedCopyResult result)
    {
        Architecture(); var memory = new Memory { Result = result }; using var state = new State(memory);
        var bytes = new byte[65536];
        Assert.Equal(result, MetalVideoPresenter.TryReadFiniteStorageBuffer(memory, Address, bytes, out var mapping, out _));
        Assert.Null(mapping); Assert.Equal(1, memory.MappedReads); Assert.Equal(0, memory.OrdinaryReads);
        var unknown = new GuestMemoryBuffer(Address, bytes, bytes.Length, (ulong)bytes.Length, false, true);
        Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => Call("RequireFiniteStorageBufferOrigin", unknown)).InnerException);
    }

    [Fact]
    public void SessionResetDuringActualBufferCopyNeverAttachesNewSession()
    {
        Architecture(); var memory = new Memory(); using var state = new State(memory);
        var session = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        memory.AfterCopy = () => Field("_selectedStorageMipSession").SetValue(null, session + 1);
        Assert.Equal(CpuMappedCopyResult.Rejected,
            MetalVideoPresenter.TryReadFiniteStorageBuffer(memory, Address, new byte[65536], out var mapping, out var originalSession));
        Assert.Null(mapping); Assert.Equal(session, originalSession); Assert.Equal(1, memory.MappedReads);
    }

    [Theory]
    [InlineData(0UL, 1L, true)] [InlineData(65536UL, 1L, false)] [InlineData(0UL, 2L, false)]
    public void WritableGlobalDisjointnessUsesPhysicalIdentityRatherThanVirtualAddress(ulong offset, long identity, bool overlap)
    {
        Architecture();
        var image = Mapping(Address, 65536, 0);
        var bufferAlias = Mapping(Address + 0x100000, 65536, offset, identity);
        Assert.Equal(overlap, Call("PhysicalMappingOverlaps", image, bufferAlias));
        Assert.Equal(overlap, Call("PhysicalMappingOverlaps", bufferAlias, image));
    }

    // Scalar tracker controls inject only owner metadata. They do NOT arm real
    // memory or qualify signals/mprotect/native-store completion.
    private sealed class ProtectedScalarState : IDisposable
    {
        internal readonly object Range;
        internal readonly GuestImageWriteTracker.ContentToken Token;
        private readonly object gate = typeof(GuestImageWriteTracker).GetField("_gate", Static)!.GetValue(null)!;
        private readonly IDictionary ranges = (IDictionary)typeof(GuestImageWriteTracker).GetField("_rangesByAddress", Static)!.GetValue(null)!;
        private readonly FieldInfo hook = typeof(GuestImageWriteTracker).GetField("_afterProtectedDirtyConsumeForControl", Static)!;
        private readonly object? previousHook;
        private readonly object? previousEnabled;
        private readonly object? previous;
        internal ProtectedScalarState()
        {
            Range = Activator.CreateInstance(typeof(GuestImageWriteTracker).GetNestedType("TrackedRange", BindingFlags.NonPublic)!, true)!;
            Set("Address", Address); Set("ByteCount", 65536UL); Set("Start", Address); Set("End", Address + 65536);
            Set("Protect", true); Set("Armed", 1); Set("Dirty", 1); Set("WriteGeneration", 5L);
            Token = new((long)Get("DiagnosticIdentity"), 5, 0, -1, 65536, 0);
            lock (gate)
            {
                previous = ranges[Address]; ranges[Address] = Range;
                previousHook = hook.GetValue(null);
                previousEnabled = typeof(GuestImageWriteTracker).GetField("_enabled", Static)!.GetValue(null);
                typeof(GuestImageWriteTracker).GetField("_enabled", Static)!.SetValue(null, true);
            }
        }
        internal object Get(string field) => Range.GetType().GetField(field)!.GetValue(Range)!;
        internal void Set(string field, object value) => Range.GetType().GetField(field)!.SetValue(Range, value);
        internal void Pause(Action action) => hook.SetValue(null, action);
        public void Dispose()
        {
            lock (gate)
            {
                if (previous is null) ranges.Remove(Address); else ranges[Address] = previous;
                hook.SetValue(null, previousHook);
                typeof(GuestImageWriteTracker).GetField("_enabled", Static)!.SetValue(null, previousEnabled);
            }
        }
    }

    [Fact]
    public void ExactOriginalProtectedDirtyAcknowledgmentDoesNotResetGeneration()
    {
        Architecture(); using var state = new ProtectedScalarState();
        Assert.True(GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(Address, 65536, state.Token));
        Assert.Equal(0, state.Get("Dirty")); Assert.Equal(5L, state.Get("WriteGeneration"));
        GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(Address, state.Token);
        Assert.Equal(1, state.Get("Dirty")); Assert.Equal(5L, state.Get("WriteGeneration"));
    }

    [Theory]
    [InlineData("generation")] [InlineData("invalidating")] [InlineData("throw")]
    public void NewFaultPublicationCannotBeLostByConditionalDirtyAcknowledgment(string race)
    {
        Architecture(); using var state = new ProtectedScalarState();
        state.Pause(() =>
        {
            if (race == "generation") state.Set("WriteGeneration", 6L);
            else if (race == "invalidating") state.Set("Armed", 2);
            else throw new InvalidOperationException("ordinary phase control");
        });
        if (race == "throw")
            Assert.Throws<InvalidOperationException>(() => GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(Address, 65536, state.Token));
        else Assert.False(GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(Address, 65536, state.Token));
        Assert.Equal(1, state.Get("Dirty"));
    }

    [Fact]
    public void WrongGenerationOrWatchOwnerCannotAcknowledgeDirty()
    {
        Architecture(); using var state = new ProtectedScalarState();
        Assert.False(GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(Address, 65536, state.Token with { ProtectedGeneration = 4 }));
        Assert.False(GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(Address, 65536, state.Token with { WatchIdentity = 1 }));
        Assert.Equal(1, state.Get("Dirty")); Assert.Equal(5L, state.Get("WriteGeneration"));
    }
}
