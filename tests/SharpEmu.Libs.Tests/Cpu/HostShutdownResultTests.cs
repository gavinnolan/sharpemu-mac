// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;
namespace SharpEmu.Libs.Tests.Cpu;

public sealed class HostShutdownResultTests
{
    [NativeX64Theory]
    [InlineData(true, true, true, null, 123UL, false, true)]
    [InlineData(true, false, true, null, 0UL, false, false)]
    [InlineData(true, true, false, "native access violation", ulong.MaxValue, false, false)]
    [InlineData(true, true, true, "guest import-loop guard", 123UL, false, false)]
    [InlineData(false, false, true, null, 0UL, true, false)]
    [InlineData(false, false, true, null, 7UL, false, false)]
    public void CompletionRequiresReturnedRedirectWithoutAnExecutionFailure(bool requested, bool redirected,
        bool returned, string? error, ulong value, bool success, bool stopped)
    {
        var modules = new ModuleManager(); modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var type = typeof(DirectExecutionBackend);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (requested)
        {
            backend.RequestHostShutdown("first request");
            backend.RequestHostShutdown("second request");
            Assert.Null(backend.LastError); // request is not a backend error or completed lifecycle
        }
        // The same slot that callback scopes clear/restore cannot erase the retained cause.
        type.GetProperty(nameof(DirectExecutionBackend.LastError))!.SetValue(backend, error);
        type.GetField("_hostShutdownEntryRedirected", flags)!.SetValue(backend, redirected ? 1 : 0);
        object?[] arguments = [value, returned, null];
        Assert.Equal(success, (bool)type.GetMethod("CompleteNativeEntryResult", flags)!.Invoke(backend, arguments)!);
        Assert.Equal(success ? OrbisGen2Result.ORBIS_GEN2_OK : OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP, arguments[2]);
        Assert.Equal(stopped, (bool)type.GetProperty("HostShutdownCompleted", flags)!.GetValue(backend)!);
        if (stopped)
        {
            Assert.Contains("first request", backend.LastError);
            Assert.DoesNotContain("second request", backend.LastError);
            Assert.Contains("teardown is unverified", backend.LastError);
            Assert.DoesNotContain("import loop", backend.LastError);
        }
        else if (error is not null) Assert.Equal(error, backend.LastError);
        else if (!success) Assert.DoesNotContain("repeating import loop", backend.LastError);
    }

    [NativeX64Fact]
    public void RequestDoesNotOverwriteAnExistingGuestFailure()
    {
        var modules = new ModuleManager(); modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        typeof(DirectExecutionBackend).GetProperty(nameof(DirectExecutionBackend.LastError))!.SetValue(backend, "original guest error");
        backend.RequestHostShutdown("stop after failure");
        Assert.Equal("original guest error", backend.LastError);
    }
}
