// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Pure reporting controls, no provider query, pin, receipt, wait or native
    // authority. Actual provider provenance is covered by Host memory controls.
    [Theory]
    [InlineData(false, false, false, "none-diagnostic-only-no-wait-authority")]
    [InlineData(true, false, false, "publication-local-ticket-no-wait-authority")]
    [InlineData(false, true, false, "validation-local-ticket-no-wait-authority")]
    [InlineData(false, false, true, "original-read-local-ticket-no-wait-authority")]
    public void DiagnosticLocalTicketContextAlwaysReportsNoWaitAuthority(bool publication, bool validation,
        bool originalRead, string expected)
    {
        ActualArchitecture();
        Assert.Equal(expected, Presenter.GetMethod("DescribeDiagnosticLocalTicket", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [publication, validation, originalRead]));
    }
}
