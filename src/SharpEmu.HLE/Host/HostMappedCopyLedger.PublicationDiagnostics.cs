// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

internal static class HostPublicationDiagnostics
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_CANONICAL_PUBLICATION_DIAGNOSTICS") == "1";
    private static int count;
    private static int readWatchCount, heldPublicationReadWatchCount;

    // Ordinary bounded failure diagnostics only. Samples do not authorize a
    // publication, refresh an observation, or replace its original identity.
    internal static void Declined(string route, string reason, long originalRevision,
        long sampledRevision, long sampledAdmission, int sampledCoverage,
        int secondaryReleased = -1, int secondaryRevoked = -1, long primaryAdmission = -1,
        ulong originalSourceAddress = 0, ulong originalSourceBytes = 0)
    {
        if (!Enabled) return;
        // Expected ordinary reads under our own reservation cannot consume the
        // independent budget for a later post-release refusal or provider failure.
        ref var budget = ref count;
        if (route == "read-watch") budget = ref readWatchCount;
        else if (route == "read-watch-held-publication") budget = ref heldPublicationReadWatchCount;
        var previous = Volatile.Read(ref budget);
        if (previous >= 64 || Interlocked.CompareExchange(ref budget, previous + 1, previous) != previous) return;
        try
        {
            Console.Error.WriteLine($"[publication-decline] route={route} reason={reason} " +
                $"originalRevision={originalRevision} sampledRevision={sampledRevision} " +
                $"sampledAdmission={sampledAdmission} sampledCoverageIncomplete={sampledCoverage} " +
                $"secondaryReleased={secondaryReleased} secondaryRevoked={secondaryRevoked} primaryAdmission={primaryAdmission} " +
                $"originalSourceAddress=0x{originalSourceAddress:X} originalSourceBytes={originalSourceBytes}");
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
