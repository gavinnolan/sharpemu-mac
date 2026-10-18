// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.Prototype;

public sealed class PageRestorationBookkeepingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualPartialUnmapRestoresSurvivorsAndKeepsFailedMembership(int missingPage)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Console.Error.WriteLine($"[RESTORE-PROTOTYPE] partial-unmap start page={missingPage}");
        using var pages = new Pages();
        pages.Remove(missingPage);
        for (var i = 0; i < 3; i++)
        {
            var success = Restore(pages.Members[i], pages.PageBytes);
            Assert.Equal(i != missingPage, success);
            Assert.Equal(i == missingPage ? 1 : 0, pages.Ledgers[i].Watchers);
            Assert.Equal(i != missingPage, pages.Members[i].Released);
            Assert.False(pages.Ledgers[i].Pending);
            if (i != missingPage)
                Assert.Equal(HostMemory.PAGE_READWRITE, NativePageProtection.Query(pages.Address(i)));
            else Assert.True(pages.Members[i].LastError != 0);
        }
        // Surviving releases cannot imply whole-range restoration/retirement.
        Assert.Contains(pages.Members, member => !member.Released);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadonlyFileFailureRetainsWatchWhileOtherActualPagesRestore(int failingPage)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Console.Error.WriteLine($"[RESTORE-PROTOTYPE] readonly-file start page={failingPage}");
        using var pages = new Pages();
        var path = Path.GetTempFileName();
        var fd = -1;
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(pages.PageBytes);
            fd = Open(path, 0); // O_RDONLY; MAP_SHARED cannot acquire write permission
            Assert.True(fd >= 0);
            Assert.Equal((nint)pages.Address(failingPage), Mmap((nint)pages.Address(failingPage),
                (nuint)pages.PageBytes, 1, 0x11, fd, 0)); // PROT_READ, MAP_SHARED|MAP_FIXED
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HostMemory.PAGE_READONLY, NativePageProtection.Query(pages.Address(i)));
                Assert.Equal(i != failingPage, Restore(pages.Members[i], pages.PageBytes));
                Assert.Equal(i == failingPage ? 1 : 0, pages.Ledgers[i].Watchers);
                Assert.Equal(i == failingPage ? HostMemory.PAGE_READONLY : HostMemory.PAGE_READWRITE,
                    NativePageProtection.Query(pages.Address(i)));
            }
            Assert.False(pages.Members[failingPage].Released);
            Assert.True(pages.Members[failingPage].LastError != 0);
        }
        finally { if (fd >= 0) Assert.Equal(0, Close(fd)); File.Delete(path); }
    }

    [Fact]
    public void RealOverlapDoesNotOpenPageUntilLastOwnerRestoresSuccessfully()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Console.Error.WriteLine("[RESTORE-PROTOTYPE] overlap start");
        using var pages = new Pages();
        var ledger = new RestorationPage(pages.Address(0), 2);
        var first = new RestorationMembership(ledger);
        var last = new RestorationMembership(ledger);
        Assert.Equal(ReleaseStep.SharedPageStillWatched, ledger.TryBegin(first, out _));
        Assert.True(first.Released);
        Assert.Equal(1, ledger.Watchers);
        Assert.Equal(HostMemory.PAGE_READONLY, NativePageProtection.Query(ledger.Address));
        Assert.Equal(ReleaseStep.NativeRestoreRequired, ledger.TryBegin(last, out var ticket));
        Assert.False(last.Released);
        Assert.Equal(1, ledger.Watchers); // ownership remains while kernel restoration is pending
        Assert.Equal(HostMemory.PAGE_READONLY, NativePageProtection.Query(ledger.Address));
        Assert.Equal(0, Mprotect((nint)ledger.Address, (nuint)pages.PageBytes, 3));
        Assert.True(ledger.Complete(ticket, true, 0));
        Assert.True(last.Released);
        Assert.Equal(0, ledger.Watchers);
        Assert.Equal(HostMemory.PAGE_READWRITE, NativePageProtection.Query(ledger.Address));
        Assert.False(ledger.Complete(ticket, true, 0)); // copied result cannot complete twice
    }

    [Fact]
    public void UnattemptedPagesAndFailedResultKeepOwnershipWithoutAllocationAfterWarmup()
    {
        var page = new RestorationPage(4096, 1);
        var member = new RestorationMembership(page);
        var unattempted = new RestorationPage(8192, 1);
        var untouched = new RestorationMembership(unattempted);
        Assert.Equal(ReleaseStep.NativeRestoreRequired, page.TryBegin(member, out var warm));
        Assert.True(page.Complete(warm, false, 12));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var step = page.TryBegin(member, out var ticket);
        var completed = page.Complete(ticket, false, 13);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(ReleaseStep.NativeRestoreRequired, step);
        Assert.True(completed);
        Assert.Equal(before, after);
        Assert.False(member.Released);
        Assert.Equal(1, page.Watchers);
        Assert.Equal(13, member.LastError);
        Assert.False(untouched.Released);
        Assert.Equal(1, unattempted.Watchers);
        Assert.False(unattempted.Pending);
    }

    [Fact]
    public void StaleFailedTicketCannotClaimRetryAtMemberPublicationBoundary()
    {
        var page = new RestorationPage(4096, 1);
        var member = new RestorationMembership(page);
        Assert.Equal(ReleaseStep.NativeRestoreRequired, page.TryBegin(member, out var stale));
        Assert.True(page.Complete(stale, false, 12));
        Assert.Equal(ReleaseStep.NativeRestoreRequired, page.TryBegin(member, out var retry,
            afterMemberClaim: () => Assert.False(page.Complete(stale, true, 0))));
        Assert.Equal(1, page.Watchers);
        Assert.True(page.Pending);
        Assert.False(member.Released);
        Assert.True(page.Complete(retry, false, 13));
        Assert.Equal(1, page.Watchers);
    }

    [Fact]
    public async Task PreemptedOldCompletionCannotStealLaterPendingOperation()
    {
        var page = new RestorationPage(4096, 1);
        var member = new RestorationMembership(page);
        Assert.Equal(ReleaseStep.NativeRestoreRequired, page.TryBegin(member, out var old));
        using var paused = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        // A dedicated thread and generous bounds: a starved thread pool on a loaded host
        // must not read as a bookkeeping failure. Every wait returns as soon as it is met.
        var worker = Task.Factory.StartNew(() => page.Complete(old, true, 0, beforeMemberClaim: () =>
        {
            paused.Set();
            Assert.True(proceed.Wait(TimeSpan.FromSeconds(30)));
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(paused.Wait(TimeSpan.FromSeconds(30)));
            Assert.True(page.Complete(old, false, 12));
            Assert.Equal(ReleaseStep.NativeRestoreRequired, page.TryBegin(member, out var retry));
            proceed.Set();
            Assert.False(await worker.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, page.Watchers);
            Assert.True(page.Pending);
            Assert.True(page.Complete(retry, false, 13));
        }
        finally { proceed.Set(); }
    }

    [Fact]
    public void ExhaustedMemberGenerationDeclinesWithoutWrappingOrDroppingOwnership()
    {
        var page = new RestorationPage(4096, 1);
        var member = new RestorationMembership(page) { Token = ((long.MaxValue >> 2) << 2) | 1 };
        Assert.Equal(ReleaseStep.Busy, page.TryBegin(member, out _));
        Assert.Equal(1, page.Watchers);
        Assert.False(page.Pending);
        Assert.False(member.Released);
    }

    private static bool Restore(RestorationMembership member, int pageBytes)
    {
        var step = member.Page.TryBegin(member, out var ticket);
        Assert.Equal(ReleaseStep.NativeRestoreRequired, step);
        var result = Mprotect((nint)member.Page.Address, (nuint)pageBytes, 3);
        var error = result == 0 ? 0 : Marshal.GetLastPInvokeError(); // capture immediately
        Assert.True(member.Page.Complete(ticket, result == 0, error));
        return result == 0;
    }

    private sealed unsafe class Pages : IDisposable
    {
        private static long sequence;
        private readonly ulong allocation;
        private readonly uint task;
        private int removedPage = -1;
        public int PageBytes { get; } = Environment.SystemPageSize;
        public RestorationPage[] Ledgers { get; }
        public RestorationMembership[] Members { get; }
        public Pages()
        {
            // Exact private high range: flags0 fails instead of clobbering an
            // existing mapping. Avoid allocator-near holes reused by JIT code.
            task = MachTaskSelf();
            allocation = 0x00005000_0000_0000UL + (ulong)Interlocked.Increment(ref sequence) * 0x10_0000UL;
            var requested = allocation;
            Assert.Equal(0, MachAllocate(task, ref requested, (ulong)(PageBytes * 3), 0));
            Assert.Equal(allocation, requested);
            try
            {
                new Span<byte>((void*)allocation, PageBytes * 3).Fill(0x51);
                Assert.Equal(0, Mprotect((nint)allocation, (nuint)(PageBytes * 3), 1));
                _ = Marshal.GetLastPInvokeError();
                Assert.Equal(HostMemory.PAGE_READONLY, NativePageProtection.Query(allocation));
                Ledgers = Enumerable.Range(0, 3).Select(i => new RestorationPage(Address(i), 1)).ToArray();
                Members = Ledgers.Select(page => new RestorationMembership(page)).ToArray();
                // Warm the bounded bookkeeping before creating any gap.
                var warmPage = new RestorationPage(0, 1);
                var warmMember = new RestorationMembership(warmPage);
                Assert.Equal(ReleaseStep.NativeRestoreRequired, warmPage.TryBegin(warmMember, out var warm));
                Assert.True(warmPage.Complete(warm, false, 12));
            }
            catch { _ = MachDeallocate(task, allocation, (ulong)(PageBytes * 3)); throw; }
        }
        public ulong Address(int page) => allocation + (ulong)(page * PageBytes);
        public void Remove(int page)
        {
            Assert.Equal(-1, removedPage);
            Assert.Equal(0, Munmap((nint)Address(page), (nuint)PageBytes));
            removedPage = page;
        }
        public void Dispose()
        {
            // Never unmap the released gap: it no longer belongs to this
            // fixture, and could have acquired a different mapping owner.
            for (var i = 0; i < 3; i++)
                if (i != removedPage) Assert.Equal(0, MachDeallocate(task, Address(i), (ulong)PageBytes));
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "mach_task_self")] private static extern uint MachTaskSelf();
    [DllImport("libSystem.B.dylib", EntryPoint = "mach_vm_allocate")] private static extern int MachAllocate(uint task, ref ulong address, ulong bytes, int flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "mach_vm_deallocate")] private static extern int MachDeallocate(uint task, ulong address, ulong bytes);

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)] private static extern int Mprotect(nint address, nuint bytes, int protection);
    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)] private static extern int Munmap(nint address, nuint bytes);
    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)] private static extern nint Mmap(nint address, nuint bytes, int protection, int flags, int fd, long offset);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int fd);
}
