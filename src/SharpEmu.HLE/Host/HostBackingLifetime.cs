// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

/// <summary>Ordinary disposal result, not a mapping or permission receipt.</summary>
public enum HostBackingReleaseStatus { NotAttempted, Released, Busy, TimedOut, AliasOutcomeUnknown, StorageOutcomeUnknown, CallbackOutcomeUnknown }

/// <summary>Provider-observed cleanup; failed native effects are never implicitly retryable.</summary>
public readonly record struct HostBackingReleaseResult(HostBackingReleaseStatus Status, int NativeError = 0)
{
    public bool AliasReleased => Status is HostBackingReleaseStatus.Released or HostBackingReleaseStatus.StorageOutcomeUnknown;
    public bool Completed => Status == HostBackingReleaseStatus.Released;
}

/// <summary>Ordinary alias/descriptor cleanup failure. Storage may remain owned or quarantined.</summary>
public sealed class HostBackingReleaseException : InvalidOperationException
{
    public HostBackingReleaseException(HostBackingReleaseResult result)
        : base($"Backing release incomplete: {result.Status}; native error {result.NativeError}.") => Result = result;
    public HostBackingReleaseResult Result { get; }
}
