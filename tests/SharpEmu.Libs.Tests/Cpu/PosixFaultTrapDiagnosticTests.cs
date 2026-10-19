// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>Supplied-frame metadata controls only: no signal, protected guest mapping or recovery.</summary>
[Collection("GuestImageWriteTracker")]
public sealed unsafe class PosixFaultTrapDiagnosticTests
{
#if SHARPEMU_CPU_MACOS_X64 || SHARPEMU_CPU_LINUX_X64
    [Theory]
    [InlineData(0UL)] [InlineData(6UL)] [InlineData(13UL)]
    [InlineData(14UL)] [InlineData(48879UL)] [InlineData(65535UL)]
#if SHARPEMU_CPU_LINUX_X64
    [InlineData(0x8000000000000000UL)] [InlineData(ulong.MaxValue)]
#endif
    public void FirstSuppliedPosixFrameRetainsRawTrapWithoutReadingAdjacentCpuOrError(ulong trap)
    {
        WithTrace(buffer =>
        {
            byte* user = stackalloc byte[512];
            byte* machine = stackalloc byte[1024];
            byte* signal = stackalloc byte[128];
            new Span<byte>(user, 512).Clear();
            new Span<byte>(machine, 1024).Clear();
            new Span<byte>(signal, 128).Clear();
#if SHARPEMU_CPU_MACOS_X64
            *(byte**)(user + 48) = machine; // SDK ucontext_t.__mcontext
            *(ushort*)(machine + 0) = checked((ushort)trap);
            *(ushort*)(machine + 2) = 0xA55A; // adjacent __cpu must not leak into trap
            *(uint*)(machine + 4) = 6;
            *(ulong*)(machine + 8) = 0x8120;
            *(ulong*)(machine + 144) = 0x1234;
            *(int*)(signal + 8) = 2;
            *(ulong*)(signal + 24) = 0x8120;
#else
            byte* registers = user + 40; // Linux ucontext_t.uc_mcontext.gregs
            *(ulong*)(registers + 152) = 6;
            *(ulong*)(registers + 160) = trap;
            *(ulong*)(registers + 168) = 0xA55A; // adjacent oldmask must not leak
            *(ulong*)(registers + 128) = 0x1234;
            *(int*)(signal + 8) = 2;
            *(ulong*)(signal + 16) = 0x8120;
#endif
            // SIGILL avoids access classification/probing and never dispatches
            // a handler. Invoke the actual opt-in first-entry diagnostic helper.
            var invocation = Begin(4, (nint)signal, (nint)user);
            var entry = Assert.Single(buffer.Snapshot());
            Assert.Equal(invocation, entry.Invocation);
            Assert.True(invocation > 0);
            Assert.Equal(1, entry.Kind);
            Assert.Equal(1, entry.Reserved);
            Assert.Equal(trap, unchecked((ulong)entry.Operation));
            Assert.Equal(6UL, entry.ErrorCode);
            Assert.Equal(4, entry.Signal);
            Assert.Equal(2, entry.SignalCode);
            Assert.Equal(-1, entry.Access);
            Assert.Equal(0x8120UL, entry.RawAddress);
            Assert.Equal(0x8120UL, entry.Address);
            Assert.Equal(0x1234UL, entry.Rip);
            Assert.Equal(0, entry.Result); // no recovery result or native operation
        });
    }

#if SHARPEMU_CPU_MACOS_X64
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingSuppliedPosixContextRemainsUnobservedRatherThanTrapZero(bool userWithoutMachine)
#else
    [Fact]
    public void MissingSuppliedPosixContextRemainsUnobservedRatherThanTrapZero()
#endif
    {
        WithTrace(buffer =>
        {
            byte* signal = stackalloc byte[128];
            byte* user = stackalloc byte[512];
            new Span<byte>(signal, 128).Clear();
            new Span<byte>(user, 512).Clear();
#if SHARPEMU_CPU_MACOS_X64
            *(ulong*)(signal + 24) = 0x8120;
            var context = userWithoutMachine ? (nint)user : 0;
#else
            // Linux embeds mcontext rather than storing a nullable pointer;
            // only null ucontext represents missing context on that ABI.
            *(ulong*)(signal + 16) = 0x8120;
            nint context = 0;
#endif
            var invocation = Begin(4, (nint)signal, context);
            var entry = Assert.Single(buffer.Snapshot());
            Assert.Equal(invocation, entry.Invocation);
            Assert.Equal(0, entry.Reserved);
            Assert.Equal(0L, entry.Operation);
            Assert.Equal(0UL, entry.ErrorCode);
            Assert.Equal(0UL, entry.Rip);
            Assert.Equal(0x8120UL, entry.Address);
            Assert.Equal(-1, entry.Access);
        });
    }

    [Fact]
    public void DisabledFirstEntryDoesNotReadSuppliedContextOrReserveInvocation()
    {
        var previous = GuestImageWriteTracker.ExchangeNativeDiagnosticBufferForTests(null);
        try
        {
            var field = typeof(GuestImageWriteTracker).GetField("_nativeDiagnosticInvocationCounter", BindingFlags.Static | BindingFlags.NonPublic)!;
            var before = (long)field.GetValue(null)!;
            Assert.Equal(0L, Begin(4, (nint)1, (nint)1));
            Assert.Equal(before, (long)field.GetValue(null)!);
        }
        finally { GuestImageWriteTracker.ExchangeNativeDiagnosticBufferForTests(previous); }
    }

    private static long Begin(int signal, nint info, nint context)
    {
#if SHARPEMU_CPU_MACOS_X64
        const string assembly = "SharpEmu.Cpu.DirectExecution.MacOS.X64";
#else
        const string assembly = "SharpEmu.Cpu.DirectExecution.Linux.X64";
#endif
        var bridge = Assembly.Load(assembly).GetTypes().Single(static type => type.Name == "PosixFaultBridge");
        return (long)bridge.GetMethod("BeginNativeSignalDiagnostics", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [signal, info, context])!;
    }
#endif

    [Fact]
    public void LegacyExplicitEntryCannotClaimObservedTrapFromItsErrorCode()
    {
        WithTrace(buffer =>
        {
            var invocation = GuestImageWriteTracker.RecordNativeImageFaultEntry(10, 0x8120, 2, 0x8120, 1, 6, 0x1234);
            var entry = Assert.Single(buffer.Snapshot());
            Assert.Equal(invocation, entry.Invocation);
            Assert.Equal(6UL, entry.ErrorCode);
            Assert.Equal(0L, entry.Operation);
            Assert.Equal(0, entry.Reserved);
        });
    }

    [Fact]
    public void ExplicitRawTrapRetainsAllBitsWithoutChangingErrorOrRecoveryFields()
    {
        WithTrace(buffer =>
        {
            var invocation = GuestImageWriteTracker.RecordNativeImageFaultEntry(10, 0x8120, 2, 0x8120, 1, 6, 0x1234, ulong.MaxValue, true);
            var entry = Assert.Single(buffer.Snapshot());
            Assert.Equal(invocation, entry.Invocation);
            Assert.Equal(ulong.MaxValue, unchecked((ulong)entry.Operation));
            Assert.Equal(1, entry.Reserved);
            Assert.Equal(6UL, entry.ErrorCode);
            Assert.Equal(0, entry.Result);
            Assert.Equal(0, entry.Route);
            Assert.Equal(0, entry.Reason);
        });
    }

    private static void WithTrace(Action<NativeFaultTraceBuffer> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-trap-metadata-{Guid.NewGuid():N}.bin");
        try
        {
            using var buffer = new NativeFaultTraceBuffer(path, 8);
            var previous = GuestImageWriteTracker.ExchangeNativeDiagnosticBufferForTests(buffer);
            try { action(buffer); }
            finally { GuestImageWriteTracker.ExchangeNativeDiagnosticBufferForTests(previous); }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
