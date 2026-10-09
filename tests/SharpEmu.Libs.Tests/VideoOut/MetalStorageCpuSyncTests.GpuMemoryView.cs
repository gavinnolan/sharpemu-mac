// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Core.Cpu;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    [Fact]
    public void RealThreadTrackersKeepOneGpuReaderAndActualOriginalProviderWithoutRecapture()
    {
        ActualArchitecture(); using var state = new VolumeState(); using var owners = new ColdOwnerState();
        using var memory = new DisplayLedgerMemory();
        var firstThread = new TrackedCpuMemory(memory); var secondThread = new TrackedCpuMemory(memory);
        var nestedThread = new TrackedCpuMemory(secondThread);
        var backend = new MetalGuestGpuBackend(); var ownerBackend = GuestGpu.Current!;
        Assert.Same(memory, backend.BindCommandStreamMemory(firstThread));
        MetalVideoPresenter.AttachGuestMemory(firstThread);
        Assert.Same(memory, Field("_guestMemory").GetValue(null));
        var bytes = new byte[DisplayLedgerMemory.ByteCount];
        var original = ReadOriginalDisplay(memory, bytes)!;
        Assert.Same(memory, Property(original, "Memory")); Assert.Same(memory, Property(original, "Source"));
        var mapping = Property(original, "Mapping"); var session = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        Assert.Equal(memory.Expected, bytes); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        Assert.Same(memory, backend.BindCommandStreamMemory(nestedThread));
        MetalVideoPresenter.AttachGuestMemory(secondThread);
        Assert.Same(memory, Field("_guestMemory").GetValue(null));
        ValidateOriginalDisplay(original);
        Assert.True(MetalVideoPresenter.IsStorageOriginalEnrollmentOwnerCurrent(memory, ownerBackend, Address, session));
        Assert.Same(mapping, Property(original, "Mapping")); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        Assert.Null(firstThread.LastFailure); Assert.Null(secondThread.LastFailure);

        using var differentRoot = new DisplayLedgerMemory();
        MetalVideoPresenter.AttachGuestMemory(new TrackedCpuMemory(differentRoot));
        Assert.Same(differentRoot, Field("_guestMemory").GetValue(null));
        Assert.False(MetalVideoPresenter.IsStorageOriginalEnrollmentOwnerCurrent(memory, ownerBackend, Address, session));
        Assert.Throws<InvalidOperationException>(() => backend.BindCommandStreamMemory(new TrackedCpuMemory(differentRoot)));
        Assert.Same(memory, backend.BindCommandStreamMemory(secondThread));
        Assert.Same(mapping, Property(original, "Mapping")); Assert.Equal(1, memory.Copies); Assert.Equal(0, differentRoot.Copies);
        AssertNoGpuViewWorker(backend); Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    [Fact]
    public void BoundMetalBackendRefusesDifferentRootBeforeAnyWorkerOrQueueAdmission()
    {
        ActualArchitecture(); using var state = new State();
        var first = new Memory([1]); var second = new Memory([2]); var backend = new MetalGuestGpuBackend();
        Assert.Same(first, backend.BindCommandStreamMemory(new TrackedCpuMemory(first)));
        Assert.Throws<InvalidOperationException>(() => backend.SubmitDone(new TrackedCpuMemory(second)));
        Assert.Throws<InvalidOperationException>(() => backend.SubmitCommandStream(new TrackedCpuMemory(second), 0, Address, 1, 7, null));
        Assert.Throws<InvalidOperationException>(() => backend.SubmitCommandStreamWithPreparation(new TrackedCpuMemory(second), 0, Address, 1, 7, null));
        Assert.Same(first, backend.BindCommandStreamMemory(first)); AssertNoGpuViewWorker(backend);
        Assert.Equal(0, first.Reads); Assert.Equal(0, second.Reads);
    }

    [Fact]
    public void GenericAndChangingDecoratorsStayOpaqueAndKeepTheirGpuBehavior()
    {
        ActualArchitecture(); using var state = new State();
        var first = new Memory([1]); var second = new Memory([2]); var decorator = new OpaqueGpuViewDecorator(first);
        var backend = new MetalGuestGpuBackend(); var tracker = new TrackedCpuMemory(decorator);
        Assert.Same(decorator, CpuGpuMemoryView.Resolve(tracker));
        Assert.Same(decorator, backend.BindCommandStreamMemory(tracker));
        MetalVideoPresenter.AttachGuestMemory(tracker); Assert.Same(decorator, Field("_guestMemory").GetValue(null));
        var bytes = new byte[1]; Assert.True(CpuGpuMemoryView.Resolve(tracker).TryRead(Address, bytes)); Assert.Equal((byte)0xD3, bytes[0]);
        decorator.Wrapped = second;
        Assert.Same(decorator, CpuGpuMemoryView.Resolve(new TrackedCpuMemory(decorator)));
        Assert.Same(decorator, backend.BindCommandStreamMemory(new TrackedCpuMemory(decorator)));
        Assert.Throws<InvalidOperationException>(() => backend.BindCommandStreamMemory(new OpaqueGpuViewDecorator(first)));
        Assert.Equal(0, decorator.InnerQueries); Assert.Equal(1, decorator.Reads);
        Assert.Equal(0, first.Reads); Assert.Equal(0, second.Reads); AssertNoGpuViewWorker(backend);
        // Opaque identity is not a mapping/content claim; the decorator remains
        // responsible for rejecting its own lost original provider observations.
    }

    [Theory]
    [InlineData("null")] [InlineData("self")] [InlineData("cycle")]
    [InlineData("alternating")] [InlineData("changed-on-unwind")] [InlineData("over-bound")]
    public void InvalidOptInGpuLinksRefuseWithoutReplacingPresenterOrBackend(string change)
    {
        ActualArchitecture(); using var state = new State();
        var first = new Memory([1]); var second = new Memory([2]);
        var control = new GpuViewLinkControl(() => first);
        var backend = new MetalGuestGpuBackend(); Assert.Same(first, backend.BindCommandStreamMemory(first));
        MetalVideoPresenter.AttachGuestMemory(first);
        switch (change)
        {
            case "null": control.Link = () => null; break;
            case "self": control.Link = () => control; break;
            case "cycle":
                var partner = new GpuViewLinkControl(() => control); control.Link = () => partner; break;
            case "alternating": control.Link = () => (control.Queries & 1) == 1 ? first : second; break;
            case "changed-on-unwind": control.Link = () => control.Queries % 3 == 0 ? second : first; break;
            case "over-bound":
                ICpuMemory link = first;
                for (var index = 0; index < 16; index++) { var captured = link; link = new GpuViewLinkControl(() => captured); }
                control.Link = () => link; break;
        }
        Assert.Throws<InvalidOperationException>(() => CpuGpuMemoryView.Resolve(control));
        control.Queries = 0;
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.AttachGuestMemory(control));
        Assert.Same(first, Field("_guestMemory").GetValue(null)); control.Queries = 0;
        Assert.Throws<InvalidOperationException>(() => backend.BindCommandStreamMemory(control));
        Assert.Same(first, backend.BindCommandStreamMemory(first)); AssertNoGpuViewWorker(backend);
        Assert.True(control.Queries <= 33); Assert.Equal(0, first.Reads); Assert.Equal(0, second.Reads);
    }

    [Fact]
    public void OptInLinkChangedBetweenCallsCannotRebindTheOriginalBackend()
    {
        ActualArchitecture(); using var state = new State();
        var first = new Memory([1]); var second = new Memory([2]); ICpuMemory current = first;
        var control = new GpuViewLinkControl(() => current); var backend = new MetalGuestGpuBackend();
        Assert.Same(first, backend.BindCommandStreamMemory(control)); current = second;
        Assert.Throws<InvalidOperationException>(() => backend.BindCommandStreamMemory(control));
        Assert.Same(first, backend.BindCommandStreamMemory(first)); AssertNoGpuViewWorker(backend);
    }

    private static void AssertNoGpuViewWorker(MetalGuestGpuBackend backend)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        Assert.Null(typeof(MetalGuestGpuBackend).GetField("_commandStream", instance)!.GetValue(backend));
        Assert.Null(typeof(MetalGuestGpuBackend).GetField("_commandStreamWorker", instance)!.GetValue(backend));
    }

    private sealed class OpaqueGpuViewDecorator(ICpuMemory wrapped) : ICpuMemory, ICpuMemoryWrapper
    {
        internal ICpuMemory Wrapped = wrapped;
        internal int InnerQueries, Reads;
        public ICpuMemory Inner { get { InnerQueries++; return Wrapped; } }
        public bool TryRead(ulong address, Span<byte> destination) { Reads++; destination.Fill(0xD3); return true; }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
    private sealed class GpuViewLinkControl(Func<ICpuMemory?> link) : ICpuMemory, ICpuGpuMemoryView
    {
        internal Func<ICpuMemory?> Link = link;
        internal int Queries;
        public ICpuMemory GpuMemoryView { get { Queries++; return Link()!; } }
        public bool TryRead(ulong address, Span<byte> destination) => throw new InvalidOperationException("Invalid view cannot read bytes.");
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => throw new InvalidOperationException("Invalid view cannot write bytes.");
    }
}
