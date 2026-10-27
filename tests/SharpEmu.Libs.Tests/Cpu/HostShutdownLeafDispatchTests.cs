// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class HostShutdownLeafDispatchTests
{
    [NativeX64Theory]
    [InlineData("trivial", false, true)]
    [InlineData("ordinary", false, true)]
    [InlineData("hot-memory", false, true)]
    [InlineData("builder", false, true)]
    [InlineData("trivial", true, true)]
    [InlineData("ordinary", true, true)]
    [InlineData("hot-memory", true, true)]
    [InlineData("builder", true, true)]
    [InlineData("trivial", false, false)]
    [InlineData("ordinary", false, false)]
    [InlineData("hot-memory", false, false)]
    [InlineData("builder", false, false)]
    [InlineData("trivial", true, false)]
    [InlineData("ordinary", true, false)]
    [InlineData("hot-memory", true, false)]
    [InlineData("builder", true, false)]
    public void StopBeforeOrDuringLeafRedirectsTheCurrentSlice(string path, bool duringImport, bool mainContext)
    {
        var modules = new ModuleManager(); modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var type = typeof(DirectExecutionBackend);
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instances = BindingFlags.Instance | BindingFlags.NonPublic;
        // These tests exercise enabled managed leaf dispatch, not environment-forced full dispatch.
        if (path == "trivial" && (bool)type.GetField("_disableTrivialLeafDispatch", statics)!.GetValue(null)!) return;
        if (path == "builder" && !(bool)type.GetField("_builderLeafEnabled", statics)!.GetValue(null)!) return;
        var memory = new SlotMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var bodyCalls = 0;
        void Body()
        {
            bodyCalls++;
            if (duringImport) backend.RequestHostShutdown("leaf-race-test");
        }
        memory.Copy = () => { Body(); return true; };
        var nid = path switch
        {
            "trivial" => "V++UgBtQhn0",
            "hot-memory" => "Q3VBxCXhUHs",
            "builder" => "q88lQ+GP5Yk",
            _ => "shutdown-test-ordinary-leaf",
        };
        var export = new ExportedFunction("test", nid, "shutdown test", Generation.Gen5, c => { Body(); c[CpuRegister.Rax] = 9; return 0; });
        var entryType = type.GetNestedType("ImportStubEntry", BindingFlags.NonPublic)!;
        var constructor = entryType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single();
        var entry = constructor.Invoke([0x200000UL, nid, export, true, true, false, false, 0UL]);
        var entries = Array.CreateInstance(entryType, 1); entries.SetValue(entry, 0);
        type.GetField("_importEntries", instances)!.SetValue(backend, entries);
        var names = new[] { "_activeExecutionBackend", "_activeCpuContext", "_activeEntryReturnSentinelRip", "_activeGuestReturnSlotAddress", "_activeForcedGuestExit", "_activeGuestThreadYieldRequested" };
        var fields = names.Select(n => type.GetField(n, statics)!).ToArray();
        var saved = fields.Select(f => f.GetValue(null)).ToArray();
        var allocation = Marshal.AllocHGlobal(512);
        var pack = allocation + 192; // vector-save space and stack arguments are valid too.
        var sentinel = mainContext ? 0x123456UL : 0x654321UL;
        try
        {
            Marshal.Copy(new byte[512], 0, allocation, 512);
            Marshal.WriteInt64(pack, 0, 0x2000); Marshal.WriteInt64(pack, 8, 0x3000);
            Marshal.WriteInt64(pack, 16, 4); Marshal.WriteInt64(pack, 96, 0xABCDEF);
            fields[0].SetValue(null, backend); fields[1].SetValue(null, context);
            fields[2].SetValue(null, sentinel); fields[3].SetValue(null, SlotMemory.Address);
            fields[4].SetValue(null, false); fields[5].SetValue(null, false);
            type.GetField("_cpuContext", instances)!.SetValue(backend, mainContext ? context : new CpuContext(memory, Generation.Gen5));
            type.GetField("_entryReturnSentinelRip", instances)!.SetValue(backend, 0x777777UL);
            type.GetField("_hostShutdownEntryInFlight", instances)!.SetValue(backend, 1);
            if (!duringImport) backend.RequestHostShutdown("leaf-pre-entry-test");
            var result = (ulong)type.GetMethod("DispatchImportCore", instances)!.Invoke(backend, [0, pack])!;
            Assert.Equal(duringImport ? 1 : 0, bodyCalls);
            Assert.Equal(1UL, result);
            Assert.Equal(1UL, context[CpuRegister.Rax]);
            Assert.Equal((long)sentinel, Marshal.ReadInt64(pack, 96));
            Assert.Equal(sentinel, memory.Value);
            Assert.Equal(mainContext ? 1 : 0, (int)type.GetField("_hostShutdownEntryRedirected", instances)!.GetValue(backend)!);
        }
        finally
        {
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            Marshal.FreeHGlobal(allocation);
        }
    }

    [NativeX64Fact]
    public void LateLeafCheckPreservesAPendingEntryExit()
    {
        var modules = new ModuleManager(); modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var memory = new SlotMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var state = GuestThreadExecution.State;
        var pending = state.PendingEntryExit;
        var type = typeof(DirectExecutionBackend);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var names = new[] { "_activeExecutionBackend", "_activeCpuContext", "_activeEntryReturnSentinelRip", "_activeGuestReturnSlotAddress", "_activeForcedGuestExit", "_activeGuestThreadYieldRequested" };
        var fields = names.Select(n => type.GetField(n, flags)!).ToArray();
        var saved = fields.Select(f => f.GetValue(null)).ToArray();
        var pack = Marshal.AllocHGlobal(128);
        try
        {
            fields[0].SetValue(null, backend); fields[1].SetValue(null, context);
            fields[2].SetValue(null, 0x123456UL); fields[3].SetValue(null, SlotMemory.Address);
            fields[4].SetValue(null, false); fields[5].SetValue(null, false);
            state.PendingEntryExit = true; // a must-not-return outcome belongs to its existing dispatcher.
            backend.RequestHostShutdown("pending-entry-exit-test");
            Marshal.WriteInt64(pack, 96, 0xABCDEF);
            var result = (ulong)typeof(DirectExecutionBackend).GetMethod("CompleteLeafImportShutdown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(backend, [context, pack, 9UL])!;
            Assert.Equal(9UL, result);
            Assert.Equal(0xABCDEFL, Marshal.ReadInt64(pack, 96));
            Assert.True(state.PendingEntryExit);
        }
        finally
        {
            state.PendingEntryExit = pending;
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            Marshal.FreeHGlobal(pack);
        }
    }

    private sealed class SlotMemory : ICpuMemory
    {
        internal const ulong Address = 0x100000;
        internal ulong Value;
        internal Func<bool>? Copy;
        public bool TryRead(ulong address, Span<byte> bytes) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes)
        {
            if (address != Address || bytes.Length != 8) return false;
            Value = BinaryPrimitives.ReadUInt64LittleEndian(bytes); return true;
        }
        public bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length) => Copy?.Invoke() == true;
    }
}
