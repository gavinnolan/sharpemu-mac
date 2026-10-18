// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;
namespace SharpEmu.Libs.Tests.Cpu;

[CollectionDefinition("HleDispatchTiming", DisableParallelization = true)]
public sealed class HleDispatchTimingCollection { }

[Collection("HleDispatchTiming")]
public sealed class HleDispatchTimingTests
{
    [Fact]
    public void NestedScopesRetainNamesAndSubtractOnlyNestedGatewayElapsed()
    {
        var parent = HleDispatchTiming.Begin(100); HleDispatchTiming.NameCurrent("parent");
        var child = HleDispatchTiming.Begin(130); HleDispatchTiming.NameCurrent("child");
        var childElapsed = child.Complete(170); var parentElapsed = parent.Complete(200);
        Assert.Equal(new HleDispatchTiming.Elapsed("child", 40, 40), childElapsed);
        Assert.Equal(new HleDispatchTiming.Elapsed("parent", 100, 60), parentElapsed);
        var next = HleDispatchTiming.Begin(220);
        Assert.Equal(new HleDispatchTiming.Elapsed(null, 10, 10), next.Complete(230));
    }

    [Fact]
    public void BoundedOverflowCannotOverwriteOrChargeTheOuterName()
    {
        var before = HleDispatchTiming.OmittedScopes;
        var completed = typeof(DirectExecutionBackend).GetField("_perfHleCompleted", Statics)!;
        var exclusive = typeof(DirectExecutionBackend).GetField("_perfHleExclusiveTicks", Statics)!;
        var record = typeof(DirectExecutionBackend).GetMethod("RecordPerfHleDispatchTime", Statics)!;
        var completedBefore = (long)completed.GetValue(null)!;
        var exclusiveBefore = (long)exclusive.GetValue(null)!;
        var scopes = Enumerable.Range(0, 130).Select(index => HleDispatchTiming.Begin(index)).ToArray();
        for (var index = 129; index >= 0; index--)
        {
            HleDispatchTiming.NameCurrent($"scope{index}");
            var elapsed = scopes[index].Complete(260 - index);
            if (index >= 128) Assert.Equal(default, elapsed);
            else { Assert.Equal($"scope{index}", elapsed.Name); Assert.True(elapsed.Recorded); Assert.Equal(2, elapsed.ExclusiveTicks); }
            record.Invoke(null, [elapsed]);
        }
        Assert.Equal(before + 2, HleDispatchTiming.OmittedScopes);
        Assert.Equal(completedBefore + 128, (long)completed.GetValue(null)!);
        Assert.Equal(exclusiveBefore + 256, (long)exclusive.GetValue(null)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualProfiledGatewayBalancesNestedExportsAndExceptions(bool throws)
    {
        using var fixture = new GatewayFixture();
        fixture.Install("outer", () =>
        {
            fixture.Dispatch(1);
            if (throws) throw new InvalidOperationException("original export failure");
            return 0;
        }, "child", () => 0);
        var costs = Costs();
        var before = ReadCost(costs, "outer"); var childBefore = ReadCost(costs, "child");
        if (throws)
        {
            var wrapped = Assert.Throws<TargetInvocationException>(() => fixture.Dispatch(0));
            Assert.Equal("original export failure", wrapped.InnerException!.Message);
        }
        else Assert.Equal(0UL, fixture.Dispatch(0));
        var after = ReadCost(costs, "outer"); var childAfter = ReadCost(costs, "child");
        Assert.Equal(before.Calls + 1, after.Calls); Assert.Equal(childBefore.Calls + 1, childAfter.Calls);
        Assert.True(after.Inclusive > before.Inclusive); Assert.True(childAfter.Inclusive > childBefore.Inclusive);
        Assert.Equal(after.Inclusive - before.Inclusive - (childAfter.Inclusive - childBefore.Inclusive), after.Exclusive - before.Exclusive);
        // A new successful dispatch after failure must start with no stale frame/name.
        fixture.Dispatch(1); Assert.Equal(childAfter.Calls + 1, ReadCost(costs, "child").Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualManagedGatewayRetainsItsResultAndNestedAttribution(bool throws)
    {
        using var fixture = new GatewayFixture();
        fixture.Install("gatewayOuter", () =>
        {
            Assert.Equal(0UL, fixture.DispatchGateway(1));
            if (throws) throw new InvalidOperationException("original gateway export failure");
            return 0;
        }, "gatewayChild", () => 0);
        var costs = Costs(); var before = ReadCost(costs, "gatewayOuter"); var childBefore = ReadCost(costs, "gatewayChild");
        Assert.Equal(throws ? 18446744071562199298UL : 0UL, fixture.DispatchGateway(0));
        var after = ReadCost(costs, "gatewayOuter"); var childAfter = ReadCost(costs, "gatewayChild");
        if (Environment.GetEnvironmentVariable("SHARPEMU_PERF_HLE") == "1")
        {
            Assert.Equal(before.Calls + 1, after.Calls); Assert.Equal(childBefore.Calls + 1, childAfter.Calls);
            Assert.Equal(after.Inclusive - before.Inclusive - (childAfter.Inclusive - childBefore.Inclusive), after.Exclusive - before.Exclusive);
        }
        else { Assert.Equal(before, after); Assert.Equal(childBefore, childAfter); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualProfiledGatewayClosedReportSinkCannotReplaceResultOrExportFailure(bool throws)
    {
        using var fixture = new GatewayFixture();
        fixture.Install("sinkOuter", () =>
        {
            if (throws) throw new InvalidOperationException("primary export error");
            return 0;
        }, "unused", () => 0);
        var counter = typeof(DirectExecutionBackend).GetField("_perfHleCompleted", Statics)!;
        var started = typeof(DirectExecutionBackend).GetField("_perfHleTotal", Statics)!;
        var startedBefore = (long)started.GetValue(null)!;
        var previous = (long)counter.GetValue(null)!; var output = Console.Error;
        try
        {
            counter.SetValue(null, 499999L); started.SetValue(null, 499999L); Console.SetError(new ClosedWriter());
            if (throws)
                Assert.Equal("primary export error", Assert.Throws<TargetInvocationException>(() => fixture.Dispatch(0)).InnerException!.Message);
            else Assert.Equal(0UL, fixture.Dispatch(0));
        }
        finally { Console.SetError(output); counter.SetValue(null, previous); started.SetValue(null, startedBefore); }
    }
    private sealed class ClosedWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("closed profile sink");
    }

    [Fact]
    public void WarmedTimingScopesDoNotAllocatePerDispatch()
    {
        for (var index = 0; index < 100; index++)
        {
            var warm = HleDispatchTiming.Begin(0); HleDispatchTiming.NameCurrent("warm"); warm.Complete(1);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10000; index++)
        {
            var scope = HleDispatchTiming.Begin(index); HleDispatchTiming.NameCurrent("warm"); scope.Complete(index + 1);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void DisabledActualGatewayDoesNotEnterProfilingOrAllocateTimingFrames()
    {
        Assert.NotEqual("1", Environment.GetEnvironmentVariable("SHARPEMU_PERF_HLE"));
        // A fresh managed thread has no inherited timing TLS; disabled dispatch stays that way.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var fixture = new GatewayFixture(); fixture.Install("disabled", () => 0, "unused", () => 0);
                var counter = typeof(DirectExecutionBackend).GetField("_perfHleCompleted", Statics)!;
                var before = (long)counter.GetValue(null)!;
                var handle = GCHandle.Alloc(fixture.Backend);
                // The gateway publishes a context-transfer stub (or zero) through this slot.
                var transferSlot = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Assert.Equal(0UL, typeof(DirectExecutionBackend).GetMethod("ImportDispatchGatewayManaged", Statics)!.Invoke(null, [GCHandle.ToIntPtr(handle), 0, fixture.Pack, transferSlot]));
                    Assert.Equal(IntPtr.Zero, Marshal.ReadIntPtr(transferSlot));
                    Assert.Null(typeof(HleDispatchTiming).GetField("_thread", Statics)!.GetValue(null));
                    Assert.Equal(before, counter.GetValue(null));
                }
                finally { Marshal.FreeHGlobal(transferSlot); handle.Free(); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10))); Assert.Null(failure);
    }

    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;
    private static System.Collections.IDictionary Costs() => (System.Collections.IDictionary)typeof(DirectExecutionBackend).GetField("_perfHleCosts", Statics)!.GetValue(null)!;
    private static (long Calls, long Inclusive, long Exclusive) ReadCost(System.Collections.IDictionary costs, string name)
    {
        var entry = costs[name]; if (entry is null) return default;
        return ((long)entry.GetType().GetField("Calls")!.GetValue(entry)!,
            (long)entry.GetType().GetField("Ticks")!.GetValue(entry)!, (long)entry.GetType().GetField("ExclusiveTicks")!.GetValue(entry)!);
    }
    private sealed unsafe class GatewayFixture : IDisposable
    {
        // Managed gateway fixture only: no backend constructor, native stubs or guest execution.
        internal DirectExecutionBackend Backend { get; } = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
        internal nint Pack { get; } = Marshal.AllocHGlobal(256);
        internal GatewayFixture()
        {
            new Span<byte>((void*)Pack, 256).Clear();
            typeof(DirectExecutionBackend).GetField("_cpuContext", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Backend, new CpuContext(new NoMemory(), Generation.Gen5));
        }
        internal void Install(string firstName, Func<int> first, string secondName, Func<int> second)
        {
            var entryType = typeof(DirectExecutionBackend).GetNestedType("ImportStubEntry", BindingFlags.NonPublic)!;
            var entries = Array.CreateInstance(entryType, 2);
            var functions = new[] { first, second }; var names = new[] { firstName, secondName };
            for (var index = 0; index < 2; index++)
            {
                const string nid = "0-KXaS70xy4"; // production trivial import classification; fixture supplies managed exports
                var function = functions[index]; var export = new ExportedFunction("fixture", nid, names[index], Generation.Gen5, _ => function());
                entries.SetValue(Activator.CreateInstance(entryType, [0UL, nid, export, false, true, false, false, 0UL]), index);
            }
            typeof(DirectExecutionBackend).GetField("_importEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Backend, entries);
        }
        internal ulong Dispatch(int index) => (ulong)typeof(DirectExecutionBackend).GetMethod("DispatchImportWithProfile", Statics)!.Invoke(null, [Backend, index, Pack])!;
        internal ulong DispatchGateway(int index)
        {
            var handle = GCHandle.Alloc(Backend);
            // The gateway publishes a context-transfer stub (or zero) through this slot.
            var transferSlot = Marshal.AllocHGlobal(IntPtr.Size);
            try { return (ulong)typeof(DirectExecutionBackend).GetMethod("ImportDispatchGatewayManaged", Statics)!.Invoke(null, [GCHandle.ToIntPtr(handle), index, Pack, transferSlot])!; }
            finally { Marshal.FreeHGlobal(transferSlot); handle.Free(); }
        }
        public void Dispose() => Marshal.FreeHGlobal(Pack); // uninitialized fixture owns no production backend native resources
    }
    private sealed class NoMemory : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> bytes) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => false;
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryWriteUInt64(ulong address, ulong value) => false;
    }
}
