// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class HostShutdownSliceTests
{
    [NativeX64Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void ImportBoundaryRedirectsOnlyUnwoundLiveSlice(bool requested, bool sliceEnded, bool mapped, bool redirects) =>
        Check(requested, sliceEnded, mapped, redirects);

    [NativeX64Fact]
    public void InactiveBackendCannotRedirectUsingFallbackState() =>
        Check(true, false, true, false, active: false);

    [NativeX64Fact]
    public void InvalidSentinelCannotPatchWritableReturnSlot() =>
        Check(true, false, true, false, sentinel: 0xFFFF);

    [NativeX64Fact]
    public void WorkerOrNestedContextRedirectDoesNotCertifyMainEntryReturn() =>
        Check(true, false, true, true, mainContext: false);

    [NativeX64Fact]
    public void MatchingContextOutsideOriginalNativeCallCannotCertifyEntryReturn() =>
        Check(true, false, true, true, entryInFlight: false);

    private static void Check(bool requested, bool sliceEnded, bool mapped, bool redirects,
        bool active = true, ulong sentinel = 0x123456, bool mainContext = true, bool entryInFlight = true)
    {
        var modules = new ModuleManager(); modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var memory = new ReturnSlotMemory(mapped);
        var context = new CpuContext(memory, Generation.Gen5);
        var type = typeof(DirectExecutionBackend);
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instances = BindingFlags.Instance | BindingFlags.NonPublic;
        var names = new[] { "_activeExecutionBackend", "_activeCpuContext", "_activeEntryReturnSentinelRip", "_activeGuestReturnSlotAddress", "_activeForcedGuestExit" };
        var fields = names.Select(name => type.GetField(name, statics)!).ToArray();
        var saved = fields.Select(field => field.GetValue(null)).ToArray();
        var pack = Marshal.AllocHGlobal(128);
        try
        {
            fields[0].SetValue(null, active ? backend : null);
            fields[1].SetValue(null, context);
            type.GetField("_cpuContext", instances)!.SetValue(backend, mainContext ? context : new CpuContext(memory, Generation.Gen5));
            fields[2].SetValue(null, sentinel);
            fields[3].SetValue(null, ReturnSlotMemory.Address);
            fields[4].SetValue(null, sliceEnded);
            type.GetField("_forcedGuestExit", instances)!.SetValue(backend, requested);
            type.GetField("_hostShutdownEntryInFlight", instances)!.SetValue(backend, entryInFlight ? 1 : 0);
            Marshal.WriteInt64(pack, 96, 0xABCDEF);
            // Global stop already makes this combined property true before
            // the current slice has actually redirected its return.
            if (requested) Assert.True((bool)type.GetProperty("ActiveForcedGuestExit", instances)!.GetValue(backend)!);
            var redirect = type.GetMethod("TryEndGuestSliceForShutdown", instances)!;
            Assert.Equal(redirects, (bool)redirect.Invoke(backend, [pack])!);
            Assert.Equal(redirects ? (long)sentinel : 0xABCDEFL, Marshal.ReadInt64(pack, 96));
            Assert.Equal(redirects ? sentinel : 0UL, memory.Value);
            Assert.Equal(redirects && mainContext && entryInFlight ? 1 : 0, (int)type.GetField("_hostShutdownEntryRedirected", instances)!.GetValue(backend)!);
            Assert.Equal(sliceEnded || redirects, (bool)fields[4].GetValue(null)!);
            if (redirects)
            {
                memory.Value = 0xDEADBEEF;
                Assert.False((bool)redirect.Invoke(backend, [pack])!);
                Assert.Equal(0xDEADBEEFUL, memory.Value);
            }
        }
        finally
        {
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            Marshal.FreeHGlobal(pack);
        }
    }

    private sealed class ReturnSlotMemory(bool mapped) : ICpuMemory
    {
        internal const ulong Address = 0x100000;
        internal ulong Value;
        public bool TryRead(ulong address, Span<byte> bytes) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes)
        {
            if (!mapped || address != Address || bytes.Length != 8) return false;
            Value = BinaryPrimitives.ReadUInt64LittleEndian(bytes); return true;
        }
    }
}
