// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

internal static class HostPublicationDiagnostics
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_CANONICAL_PUBLICATION_DIAGNOSTICS") == "1";
    private static int count;

    // Ordinary bounded failure diagnostics only. Samples do not authorize a
    // publication, refresh an observation, or replace its original identity.
    internal static void Declined(string route, string reason, long originalRevision,
        long sampledRevision, long sampledAdmission, int sampledCoverage)
    {
        if (!Enabled || Interlocked.Increment(ref count) > 64) return;
        try
        {
            Console.Error.WriteLine($"[publication-decline] route={route} reason={reason} " +
                $"originalRevision={originalRevision} sampledRevision={sampledRevision} " +
                $"sampledAdmission={sampledAdmission} sampledCoverageIncomplete={sampledCoverage}");
        }
        catch (Exception) { } // diagnostic I/O cannot change the rejection
    }
}

public sealed unsafe partial class HostMappedCopyLedger
{
    private CpuMappedPublicationPreparationResult PublicationDeclined(string reason, long originalRevision = -1)
    {
        if (HostPublicationDiagnostics.Enabled)
            HostPublicationDiagnostics.Declined("provider-prepare", reason, originalRevision,
                -1, -1, -1); // No extra provider query/currentness evaluation.
        return CpuMappedPublicationPreparationResult.Rejected;
    }
}
