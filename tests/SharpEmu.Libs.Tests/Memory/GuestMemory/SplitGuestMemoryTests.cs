// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

/// <summary>
/// The two-process split's layout (<see cref="SplitBackingLayout"/>): guest memory the GPU process
/// needs and that is private to the guest process otherwise is mapped from the shared backing, so
/// the bytes are the same through the backing's mirror (as the GPU process maps it) at the layout's
/// offsets. The guest allocator arena is one view; HLE data comes from it; a module image's
/// non-executable pages are views of the image slice while its code stays private and executable.
/// </summary>
[Collection(GuestMemoryStateCollection.Name)]
public sealed unsafe class SplitGuestMemoryTests : IDisposable
{
    private const ulong Page = GuestMemoryLayout.GuestPage;
    private const ulong ArenaBytes = 64 * Page;
    private const ulong ImageBytes = 32 * Page;

    // A PS5-style module: code, then .rodata, .data and .bss each on their own guest pages.
    private const ulong TextBytes = 2 * Page;
    private const ulong RodataOffset = TextBytes;
    private const ulong DataOffset = RodataOffset + Page;
    private const ulong BssOffset = DataOffset + Page;
    private const ulong ModuleBytes = BssOffset + 2 * Page;

    private readonly bool _shareable = HostBackendUnderTest.SetShareableBacking(true);

    public void Dispose()
    {
        HostBackendUnderTest.SetShareableBacking(null);
        HostBackendUnderTest.SetChunkedBacking(null);
        SelfLoader.ModuleSearchRangeForTests = null;
        SplitPrivateAccess.EnabledForTests = null;
        SplitPrivateAccess.WarningSinkForTests = null;
    }

    private static IHostGuestMirrorMemory MirrorHost(IHostViewMemory host) => Assert.IsAssignableFrom<IHostGuestMirrorMemory>(host);

    // Some direct memory below the layout, as the real backing has, so offsets are not trivially zero.
    private static SplitBackingLayout Layout(ulong arenaAddress, ulong imageBytes = ImageBytes) =>
        new(16 * Page, ArenaBytes, imageBytes, arenaAddress);

    private static PhysicalVirtualMemory CreateMemory(IHostViewMemory host, SplitBackingLayout layout) =>
        CreateMemory(host, layout, out _);

    private static PhysicalVirtualMemory CreateMemory(IHostViewMemory host, SplitBackingLayout layout,
        out ExecutableAsDataHostMemory privateMemory)
    {
        privateMemory = new ExecutableAsDataHostMemory();
        return new PhysicalVirtualMemory(privateMemory, host, layout.End, splitLayout: layout);
    }

    /// <summary>
    /// The host's private memory, except that this test process (arm64; the guest's code is x86-64
    /// anyway) maps no page writable and executable: code is mapped without execute here, and the
    /// protection each private 4 KiB page was last asked for is recorded instead. A protection of
    /// part of a 16 KiB host page applies to the whole page, where the x86-64 process's 4 KiB pages
    /// take it as asked.
    /// </summary>
    internal sealed class ExecutableAsDataHostMemory : IHostMemory
    {
        private const ulong HostPage = 0x1000;
        private static readonly ulong NativePage = (ulong)Environment.SystemPageSize;
        private readonly IHostMemory _inner = HostBackendUnderTest.CreateMemory();
        private readonly Dictionary<ulong, HostPageProtection> _requested = new();

        public HostPageProtection RequestedAt(ulong address)
        {
            lock (_requested) return _requested.TryGetValue(address & ~(HostPage - 1), out var protection) ? protection : HostPageProtection.NoAccess;
        }

        private void Record(ulong address, ulong size, HostPageProtection protection)
        {
            lock (_requested)
            {
                for (var page = address & ~(HostPage - 1); page < address + size; page += HostPage) _requested[page] = protection;
            }
        }

        private static HostPageProtection WithoutExecute(HostPageProtection protection) => protection switch
        {
            HostPageProtection.ReadWriteExecute or HostPageProtection.ExecuteWriteCopy => HostPageProtection.ReadWrite,
            HostPageProtection.ReadExecute or HostPageProtection.Execute => HostPageProtection.ReadOnly,
            _ => protection,
        };

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            var address = _inner.Allocate(desiredAddress, size, WithoutExecute(protection));
            if (address != 0) Record(address, size, protection);
            return address;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            _inner.Reserve(desiredAddress, size, WithoutExecute(protection));

        public bool Commit(ulong address, ulong size, HostPageProtection protection) =>
            _inner.Commit(address, size, WithoutExecute(protection));

        public bool Free(ulong address) => _inner.Free(address);

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            var start = address & ~(NativePage - 1);
            var end = (address + size + NativePage - 1) & ~(NativePage - 1);
            var changed = _inner.Protect(start, end - start, WithoutExecute(protection), out rawOldProtection);
            if (changed) Record(address, size, protection);
            return changed;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection) =>
            _inner.ProtectRaw(address, size, rawProtection, out rawOldProtection);

        public bool Query(ulong address, out HostRegionInfo info) => _inner.Query(address, out info);

        public bool TryQueryAllocation(ulong address, out HostAddressRange range) => _inner.TryQueryAllocation(address, out range);

        public void FlushInstructionCache(ulong address, ulong size) => _inner.FlushInstructionCache(address, size);
    }

    private static HostBackingObject MapMirror(IHostViewMemory host, PhysicalVirtualMemory memory)
    {
        Assert.True(MirrorHost(host).TryMapMirror(Assert.IsType<GuestBackingExport>(memory.GuestBackingExport), out var mirror));
        return mirror!;
    }

    private static GuestMirrorTranslator Translator(IHostViewMemory host, PhysicalVirtualMemory memory, HostBackingObject mirror,
        out HostSharedRegion tableMapping)
    {
        var table = Assert.IsType<GuestViewTable>(memory.GuestViewTable);
        Assert.True(MirrorHost(host).TryMapSharedRegion(table.Region.Descriptor, table.Region.Size, writable: false, out var region));
        tableMapping = region!;
        return new GuestMirrorTranslator(tableMapping, mirror);
    }

    private static IReadOnlyList<(ulong Offset, ulong Size)> ModuleCode => [(0, TextBytes)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArenaIsOneViewOfTheBackingSeenThroughTheMirror(bool chunked)
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetChunkedBacking(chunked);
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        Assert.Same(layout, memory.SplitLayout);
        Assert.True(memory.AllocatesFromBacking);

        Assert.True(memory.TryAllocateGuestMemory(0x100, 0x40, out var label));
        Assert.InRange(label, layout.ArenaAddress, layout.ArenaAddress + ArenaBytes - 0x100);
        Assert.True(memory.AllocatesFromBacking);
        Assert.True(memory.IsBackedView(layout.ArenaAddress));
        Assert.True(memory.IsBackedRange(layout.ArenaAddress, ArenaBytes));

        using var mirror = MapMirror(host, memory);
        var translator = Translator(host, memory, mirror, out var tableMapping);
        using (tableMapping)
        {
            var mirrored = mirror.AliasBase + layout.ArenaOffset + (label - layout.ArenaAddress);
            Assert.True(translator.TryTranslate(label, 0x100, out var translated));
            Assert.Equal(mirrored, translated);
            Assert.True(translator.IsFullyBacked(layout.ArenaAddress, ArenaBytes, out _));

            // A managed write, a guest store, and the GPU process's write (a VideoOut label) all meet.
            Assert.True(memory.TryWrite(label, BitConverter.GetBytes(Marker)));
            Assert.Equal(Marker, *(ulong*)mirrored);
            *(ulong*)(label + 8) = ~Marker;
            Assert.Equal(~Marker, *(ulong*)(mirrored + 8));
            *(ulong*)(mirrored + 16) = Marker ^ 0x55;
            Assert.Equal(Marker ^ 0x55, *(ulong*)(label + 16));
        }
    }

    [Fact]
    public void WithoutAShareableBackingTheLayoutIsOffAndTheArenaPrivate()
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetShareableBacking(false);
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        Assert.Null(memory.SplitLayout);
        Assert.False(memory.SplitsModuleImages);
        Assert.False(memory.AllocatesFromBacking);
        Assert.True(memory.TryAllocateGuestMemory(0x100, 0x40, out var allocation));
        Assert.False(memory.IsBackedRange(allocation, 0x100));
        Assert.False(memory.AllocatesFromBacking);
    }

    [Fact]
    public void HleDataComesFromTheBackedArenaZeroed()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        var context = new CpuContext(memory, Generation.Gen5);

        // Dirty arena memory first: HLE data is zeroed however the arena was used before.
        Assert.True(memory.TryAllocateGuestMemory(0x2000, 0x1000, out var used));
        new Span<byte>((void*)used, 0x2000).Fill(0xA5);
        Assert.True(memory.TryFreeGuestMemory(used));

        Assert.True(KernelMemoryCompatExports.TryAllocateHleData(context, 0x1800, 0x1000, out var blob));
        Assert.InRange(blob, layout.ArenaAddress, layout.ArenaAddress + ArenaBytes - 0x2000);
        Assert.Equal(0UL, blob % 0x1000);
        Assert.True(memory.IsBackedRange(blob, 0x1800));
        Assert.All(new ReadOnlySpan<byte>((void*)blob, 0x2000).ToArray(), value => Assert.Equal(0, value));
        Assert.True(SplitPrivateAccess.IsBacked(context.Memory, blob, 0x1800));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModuleDataPagesAreViewsAndCodeStaysPrivate(bool chunked)
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetChunkedBacking(chunked);
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout, out var privateMemory);
        Assert.True(memory.SplitsModuleImages);
        var imageBase = ProbeGuestAddress(host, ModuleBytes);
        Assert.True(memory.TryAllocateModuleImage(imageBase, ModuleBytes, ModuleCode, out var placed));
        Assert.Equal(imageBase, placed);
        Assert.Equal(ModuleBytes - TextBytes, memory.SplitImageBytesInUse);

        var text = Pattern(0x100, 0x11);
        var rodata = Pattern((int)Page, 0x22);
        var data = Pattern(0x200, 0x33);
        MapLikeTheLoader(memory, imageBase, text, rodata, data);

        // Code is private, as before; everything else is one view of the image slice.
        Assert.False(memory.IsBackedView(imageBase));
        Assert.False(memory.IsBackedRange(imageBase, 1));
        Assert.True(memory.IsBackedRange(imageBase + RodataOffset, ModuleBytes - RodataOffset));
        var code = Assert.Single(memory.SnapshotRegions(), region => region.VirtualAddress == imageBase);
        Assert.Equal(TextBytes, code.MemorySize);
        Assert.NotEqual(ProgramHeaderFlags.None, code.Protection & ProgramHeaderFlags.Execute);
        Assert.Equal(HostPageProtection.ReadExecute, privateMemory.RequestedAt(imageBase));
        Assert.Equal(HostPageProtection.ReadExecute, privateMemory.RequestedAt(imageBase + TextBytes - 1));
        Assert.Equal(HostPageProtection.NoAccess, privateMemory.RequestedAt(imageBase + RodataOffset));
        var loadedText = new byte[text.Length];
        Assert.True(memory.TryRead(imageBase, loadedText));
        Assert.Equal(text, loadedText);

        // The guest sees .rodata read-only and .data/.bss writable, through views.
        Assert.True(HostViewRegions.TryQuery(imageBase + RodataOffset, out var rodataInfo));
        Assert.Equal(HostViewRegions.RawProtection(HostPageProtection.ReadOnly), rodataInfo.Protect);
        Assert.True(HostViewRegions.TryQuery(imageBase + DataOffset, out var dataInfo));
        Assert.Equal(HostViewRegions.RawProtection(HostPageProtection.ReadWrite), dataInfo.Protect);

        using var mirror = MapMirror(host, memory);
        var translator = Translator(host, memory, mirror, out var tableMapping);
        using (tableMapping)
        {
            var slice = mirror.AliasBase + layout.ImageOffset;
            Assert.True(translator.TryTranslate(imageBase + RodataOffset, ModuleBytes - RodataOffset, out var translated));
            Assert.Equal(slice, translated);
            Assert.False(translator.IsFullyBacked(imageBase, ModuleBytes, out var firstPrivate));
            Assert.Equal(imageBase, firstPrivate);
            Assert.False(translator.TryTranslate(imageBase, 1, out _));

            // Shader binaries in .rodata, the loaded .data, the zeroed .bss.
            Assert.Equal(rodata, new ReadOnlySpan<byte>((void*)slice, rodata.Length).ToArray());
            Assert.Equal(data, new ReadOnlySpan<byte>((void*)(slice + Page), data.Length).ToArray());
            Assert.All(new ReadOnlySpan<byte>((void*)(slice + Page + (ulong)data.Length), (int)(3 * Page) - data.Length).ToArray(),
                value => Assert.Equal(0, value));

            // A relocation into read-only data lands through the backing; guest stores to .data
            // and .bss (a GPU label in a global) are the same memory the mirror reads.
            Assert.True(memory.TryWrite(imageBase + RodataOffset + 0x40, BitConverter.GetBytes(Marker)));
            Assert.Equal(Marker, *(ulong*)(slice + 0x40));
            *(ulong*)(imageBase + DataOffset + 0x300) = ~Marker;
            Assert.Equal(~Marker, *(ulong*)(slice + Page + 0x300));
            *(ulong*)(imageBase + BssOffset + Page + 8) = Marker ^ 0x99;
            Assert.Equal(Marker ^ 0x99, *(ulong*)(slice + 2 * Page + Page + 8));
            *(uint*)(slice + 2 * Page) = 0xC0FFEE;
            Assert.Equal(0xC0FFEEu, *(uint*)(imageBase + BssOffset));
        }
    }

    [Fact]
    public void ASegmentAcrossACodePageIsFilledAndProtectedPieceByPiece()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout, out var privateMemory);
        var imageBase = ProbeGuestAddress(host, ModuleBytes);
        // Code ends part way into its last page and .rodata starts right after it there.
        const ulong codeEnd = TextBytes - 0x800;
        Assert.True(memory.TryAllocateModuleImage(imageBase, ModuleBytes, [(0, codeEnd)], out _));
        var text = Pattern(0x100, 0x61);
        var rodata = Pattern((int)(0x800 + Page), 0x72);
        memory.Map(imageBase, codeEnd, 0, text, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        memory.Map(imageBase + codeEnd, (ulong)rodata.Length, 0, rodata, ProgramHeaderFlags.Read);

        // The page code touches stays private and executable, .rodata's own pages are a view.
        Assert.False(memory.IsBackedRange(imageBase + codeEnd, 0x800));
        Assert.True(memory.IsBackedRange(imageBase + TextBytes, Page));
        Assert.Equal(rodata, new ReadOnlySpan<byte>((void*)(imageBase + codeEnd), rodata.Length).ToArray());
        Assert.Equal(HostPageProtection.ReadExecute, privateMemory.RequestedAt(imageBase + TextBytes - 0x100));
        Assert.True(HostViewRegions.TryQuery(imageBase + TextBytes, out var view));
        Assert.Equal(HostViewRegions.RawProtection(HostPageProtection.ReadOnly), view.Protect);
        using var mirror = MapMirror(host, memory);
        Assert.Equal(rodata.AsSpan(0x800).ToArray(),
            new ReadOnlySpan<byte>((void*)(mirror.AliasBase + layout.ImageOffset), (int)Page).ToArray());
    }

    [Fact]
    public void TheLoaderMapsAModulesDataFromTheBacking()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        var searchStart = ProbeGuestAddress(host, 64 * Page);
        SelfLoader.ModuleSearchRangeForTests = (searchStart, searchStart + 64 * Page);
        var rodata = Pattern((int)Page, 0x44);
        var data = Pattern(0x180, 0x55);

        var image = new SelfLoader().LoadAdditional(BuildModule(Pattern(0x40, 0xCC), rodata, data), memory, null!, null, null, "split.prx");

        var imageBase = memory.SnapshotRegions()
            .Where(region => region.VirtualAddress >= searchStart && region.VirtualAddress < searchStart + 64 * Page)
            .Min(region => region.VirtualAddress);
        Assert.InRange(imageBase, searchStart, searchStart + 64 * Page - ModuleBytes);
        Assert.Equal(0UL, imageBase % Page);
        Assert.Equal(4, image.ProgramHeaders.Count);
        Assert.False(memory.IsBackedRange(imageBase, TextBytes));
        Assert.True(memory.IsBackedRange(imageBase + RodataOffset, ModuleBytes - RodataOffset));
        using var mirror = MapMirror(host, memory);
        var slice = mirror.AliasBase + layout.ImageOffset;
        Assert.Equal(rodata, new ReadOnlySpan<byte>((void*)slice, rodata.Length).ToArray());
        Assert.Equal(data, new ReadOnlySpan<byte>((void*)(slice + Page), data.Length).ToArray());
    }

    [Fact]
    public void TheImageSliceIsHandedOutInLoadOrderAndZeroedWhenReused()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        var first = ProbeGuestAddress(host, ModuleBytes);
        var second = ProbeGuestAddress(host, ModuleBytes);
        Assert.True(memory.TryAllocateModuleImage(first, ModuleBytes, ModuleCode, out _));
        Assert.True(memory.TryAllocateModuleImage(second, ModuleBytes, ModuleCode, out _));
        Assert.Equal(2 * (ModuleBytes - TextBytes), memory.SplitImageBytesInUse);
        using (var mirror = MapMirror(host, memory))
        {
            var translator = Translator(host, memory, mirror, out var tableMapping);
            using (tableMapping)
            {
                Assert.True(translator.TryGetOffset(first + RodataOffset, out var firstOffset, out _));
                Assert.True(translator.TryGetOffset(second + RodataOffset, out var secondOffset, out _));
                Assert.Equal(layout.ImageOffset, firstOffset);
                Assert.Equal(layout.ImageOffset + ModuleBytes - TextBytes, secondOffset);
            }
        }

        *(ulong*)(first + DataOffset) = Marker;
        memory.Clear();
        Assert.Equal(0UL, memory.SplitImageBytesInUse);
        var again = ProbeGuestAddress(host, ModuleBytes);
        Assert.True(memory.TryAllocateModuleImage(again, ModuleBytes, ModuleCode, out _));
        Assert.True(memory.IsBackedRange(again + RodataOffset, ModuleBytes - RodataOffset));
        Assert.Equal(0UL, *(ulong*)(again + DataOffset));
    }

    [Fact]
    public void AModuleTheSliceCannotHoldStaysPrivate()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes), imageBytes: 2 * Page);
        using var memory = CreateMemory(host, layout);
        var imageBase = ProbeGuestAddress(host, ModuleBytes);
        Assert.True(memory.TryAllocateModuleImage(imageBase, ModuleBytes, ModuleCode, out var placed));
        Assert.Equal(imageBase, placed);
        Assert.False(memory.IsBackedRange(imageBase + RodataOffset, Page));
        Assert.True(memory.IsAccessible(imageBase, ModuleBytes));
        Assert.Equal(0UL, memory.SplitImageBytesInUse);
    }

    [Fact]
    public void AnOccupiedRangeIsRefusedWithNothingLeftMapped()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        var imageBase = ProbeGuestAddress(host, ModuleBytes);
        // Something else holds the image's .data page.
        Assert.Equal(imageBase + DataOffset, host.ReserveHole(imageBase + DataOffset, Page));
        try
        {
            Assert.False(memory.TryAllocateModuleImage(imageBase, ModuleBytes, ModuleCode, out _));
            Assert.Equal(0UL, memory.SplitImageBytesInUse);
            Assert.Empty(memory.SnapshotRegions().Where(region => region.VirtualAddress < imageBase + ModuleBytes &&
                region.VirtualAddress + region.MemorySize > imageBase));
            Assert.True(memory.TryAllocateAtExact(imageBase, TextBytes, executable: false, out _));
        }
        finally
        {
            Assert.True(host.FreeHole(imageBase + DataOffset, Page));
        }
    }

    [Fact]
    public void PiecesFollowTheExecutablePagesOfAPs5Layout()
    {
        // Black Myth: Wukong's eboot, scaled down: code, .rodata, .data, .data+.bss, then a segment
        // with no access that starts in .bss's last page and ends part way into a page.
        const ulong imageBase = 0x8_0000_0000;
        var end = imageBase + 0x2364_5000;
        var pieces = PhysicalVirtualMemory.PlanModuleImage(imageBase, end, [(0, 0x118E_F56C)]);
        Assert.Equal(
            new[]
            {
                new PhysicalVirtualMemory.ModuleImagePiece(imageBase, 0x118F_0000, false),
                new PhysicalVirtualMemory.ModuleImagePiece(imageBase + 0x118F_0000, 0x2364_4000 - 0x118F_0000, true),
                new PhysicalVirtualMemory.ModuleImagePiece(imageBase + 0x2364_4000, 0x1000, false),
            },
            pieces);
    }

    [Fact]
    public void PiecesKeepPartialPagesAndInnerCodePrivate()
    {
        // A base 4 KiB past a guest page: the head and tail partial pages stay private, and so
        // does every page a code span in the middle touches.
        const ulong imageBase = 0x8_0400_1000;
        var end = imageBase + 0x2_0000;
        var pieces = PhysicalVirtualMemory.PlanModuleImage(imageBase, end, [(0x9000, 0x10), (0x9F00, 0x400)]);
        Assert.Equal(
            new[]
            {
                new PhysicalVirtualMemory.ModuleImagePiece(imageBase, 0x3000, false),
                new PhysicalVirtualMemory.ModuleImagePiece(0x8_0400_4000, 0x4000, true),
                new PhysicalVirtualMemory.ModuleImagePiece(0x8_0400_8000, 0x4000, false),
                new PhysicalVirtualMemory.ModuleImagePiece(0x8_0400_C000, 0x1_4000, true),
                new PhysicalVirtualMemory.ModuleImagePiece(0x8_0402_0000, 0x1000, false),
            },
            pieces);
        Assert.Equal(end - imageBase, (ulong)pieces.Sum(piece => (long)piece.Size));

        // Smaller than a guest page: one private piece. No code at all: all but the edges backed.
        Assert.Equal(new[] { new PhysicalVirtualMemory.ModuleImagePiece(imageBase, 0x2000, false) },
            PhysicalVirtualMemory.PlanModuleImage(imageBase, imageBase + 0x2000, []));
        Assert.Equal(
            new[] { new PhysicalVirtualMemory.ModuleImagePiece(0x8_0400_0000, 0x8000, true) },
            PhysicalVirtualMemory.PlanModuleImage(0x8_0400_0000, 0x8_0400_8000, []));
    }

    [Fact]
    public void TheGpuPathNamesPrivateMemoryOnceInSplitMode()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var label));
        var stack = ProbeGuestAddress(host, Page);
        memory.Map(stack, Page, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var reader = $"test-{Guid.NewGuid():N}";
        var warnings = new List<string>();
        SplitPrivateAccess.WarningSinkForTests = warnings.Add;

        // Off (the default), nothing is checked.
        SplitPrivateAccess.EnabledForTests = false;
        Assert.True(SplitPrivateAccess.CheckGpuRead(memory, stack, 0x100, reader));

        SplitPrivateAccess.EnabledForTests = true;
        var before = SplitPrivateAccess.PrivateReads;
        Assert.True(SplitPrivateAccess.CheckGpuRead(memory, label, 0x1000, reader));
        Assert.False(SplitPrivateAccess.CheckGpuRead(memory, stack + 0x40, 0x100, reader));
        Assert.False(SplitPrivateAccess.CheckGpuRead(memory, stack + 0x80, 0x100, reader));
        Assert.Equal(before + 2, SplitPrivateAccess.PrivateReads);
        var warning = Assert.Single(warnings);
        Assert.Contains(reader, warning);
        Assert.Contains($"first_private=0x{stack + 0x40:X16}", warning);
        Assert.False(SplitPrivateAccess.IsBacked(memory, stack, 0x100));
        Assert.True(SplitPrivateAccess.IsBacked(memory, label, 0x1000));
    }

    [Fact]
    public void AReplicaCoversEveryWindowItIsGiven()
    {
        if (!_shareable) return;
        var host = HostViewMemory.Create();
        var layout = Layout(ProbeGuestAddress(host, ArenaBytes));
        using var memory = CreateMemory(host, layout);
        Assert.True(memory.TryAllocateGuestMemory(0x100, 0x40, out var label));
        var imageBase = ProbeGuestAddress(host, ModuleBytes);
        Assert.True(memory.TryAllocateModuleImage(imageBase, ModuleBytes, ModuleCode, out _));
        *(ulong*)label = Marker;
        *(ulong*)(imageBase + DataOffset) = ~Marker;

        using var mirror = MapMirror(host, memory);
        var table = memory.GuestViewTable!;
        Assert.True(MirrorHost(host).TryMapSharedRegion(table.Region.Descriptor, table.Region.Size, writable: false, out var tableMapping));
        using (tableMapping)
        {
            (ulong, ulong)[] windows = imageBase < layout.ArenaAddress
                ? [(imageBase, imageBase + ModuleBytes), (layout.ArenaAddress, layout.ArenaAddress + ArenaBytes)]
                : [(layout.ArenaAddress, layout.ArenaAddress + ArenaBytes), (imageBase, imageBase + ModuleBytes)];
            using var replica = GuestReplica.TryAttach(MirrorHost(host), mirror, tableMapping!, GuestReplica.DefaultBase, windows);
            Assert.NotNull(replica);
            Assert.True(replica!.IsComplete);
            Assert.True(replica.Covers(label, 8));
            Assert.False(replica.Covers(layout.ArenaAddress + ArenaBytes - 8, 16));
            Assert.Equal(Marker, *(ulong*)replica.HostAddress(label));
            Assert.Equal(~Marker, *(ulong*)replica.HostAddress(imageBase + DataOffset));
        }

        // The default windows reach the guest range and the arena's fixed address.
        Assert.Contains(GuestReplica.DefaultWindows, window =>
            window.Start <= GuestMemoryLayout.AllocatorArenaAddress &&
            window.End >= GuestMemoryLayout.AllocatorArenaAddress + GuestMemoryLayout.AllocatorArenaBytes);
        Assert.Contains(GuestReplica.DefaultWindows, window => window.Start == 0 && window.End == GuestReplica.DefaultWindowEnd);
        Assert.True(GuestReplica.DefaultBase + GuestReplica.DefaultWindows[^1].End <= 1UL << 47);
    }

    // Program headers and contents as SelfLoader.MapLoadSegments maps them.
    private static void MapLikeTheLoader(PhysicalVirtualMemory memory, ulong imageBase, byte[] text, byte[] rodata, byte[] data)
    {
        memory.Map(imageBase, TextBytes, 0, text, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        memory.Map(imageBase + RodataOffset, Page, 0, rodata, ProgramHeaderFlags.Read);
        memory.Map(imageBase + DataOffset, Page, 0, data, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(imageBase + BssOffset, 2 * Page, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(seed + index * 7);
        }

        return bytes;
    }

    // A decrypted PS5 module (ET_DYN, OSABI 9, ABI 2) with the four segments of the layout above,
    // each at a file offset congruent to its address modulo a guest page.
    private static byte[] BuildModule(byte[] text, byte[] rodata, byte[] data)
    {
        const int elfHeaderSize = 0x40;
        const int programHeaderSize = 0x38;
        var image = new byte[(int)DataOffset + data.Length];
        var elf = image.AsSpan(0, elfHeaderSize);
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        elf[7] = 9;
        elf[8] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x10..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x12..], 62);
        BinaryPrimitives.WriteUInt32LittleEndian(elf[0x14..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elf[0x18..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(elf[0x20..], elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x34..], elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x36..], programHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x38..], 4);

        void Segment(int index, uint flags, ulong fileOffset, ulong address, byte[] contents, ulong memorySize)
        {
            var header = image.AsSpan(elfHeaderSize + index * programHeaderSize, programHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], flags);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], fileOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], address);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], address);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], (ulong)contents.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x28..], memorySize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x30..], Page);
            contents.CopyTo(image.AsSpan((int)fileOffset));
        }

        Segment(0, 5, Page, 0, text, TextBytes);
        Segment(1, 4, RodataOffset, RodataOffset, rodata, Page);
        Segment(2, 6, DataOffset, DataOffset, data, Page);
        Segment(3, 6, 0, BssOffset, [], 2 * Page);
        return image;
    }
}
