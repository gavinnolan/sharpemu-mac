// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Platform;

namespace SharpEmu.Core.Cpu.Native;

// The shared side of the W10 fault seam: the entry points a per-OS execution
// platform installs its fault interception in front of, and the one place that
// hands those entry points to the platform.
//
// The recovery logic itself stays here, single-sourced against the Win64
// EXCEPTION_POINTERS/CONTEXT view. Windows delivers that view already; the
// POSIX integrations rebuild it from their signal context so that
// VectoredHandler, the sentinel recovery and the instruction emulators are the
// same code on every OS.
public sealed unsafe partial class DirectExecutionBackend
{
	// The session whose recovery chain the installed hooks call. Process-wide
	// because the hooks are: a fault can arrive on a guest worker thread after
	// the session that created it asked to stop, and the hooks deliberately
	// outlive a disposal that left guest threads running (GuestSessionLeaked).
	private static DirectExecutionBackend? _faultRecoveryBackend;

	/// <summary>
	/// First-chance sentinel recovery. Must run before any managed recovery so
	/// an unresolved-import trap is repaired without dumping full diagnostics.
	/// </summary>
	internal static int SharedRawFaultRecovery(void* exceptionPointers) =>
		TryRecoverUnresolvedSentinel(exceptionPointers);

	/// <summary>
	/// The full shared recovery chain. Returns -1 when the fault is resolved and
	/// the thread may resume with the edited context.
	/// </summary>
	internal static int SharedFaultRecovery(void* exceptionPointers) =>
		_faultRecoveryBackend is { } backend ? backend.VectoredHandler(exceptionPointers) : 0;

	/// <summary>
	/// Last-resort report for a fault nothing recovered. Always returns 0.
	/// </summary>
	internal static int SharedUnhandledFaultReport(void* exceptionPointers) =>
		_faultRecoveryBackend is { } backend ? backend.UnhandledExceptionFilter(exceptionPointers) : 0;

	/// <summary>
	/// Publishes this session as the owner of the shared recovery chain and
	/// installs the platform's fault interception in front of it.
	/// </summary>
	private void InstallPlatformFaultHandling()
	{
		_faultRecoveryBackend = this;
		ExecutionPlatform.InstallFaultHandling(new DirectExecutionFaultHandlers
		{
			RawRecovery = &SharedRawFaultRecovery,
			WarmUp = &WarmUpSharedFaultRecovery,
			TryGetStackBounds = &GetFaultStackBounds,
			Recovery = &SharedFaultRecovery,
			UnhandledReport = &SharedUnhandledFaultReport,
			HostRspSlotTlsIndex = _hostRspSlotTlsIndex,
			TlsGetValueAddress = _tlsGetValueAddress,
			WorkerAbortStub = _workerAbortStub,
			WorkerAbortEligibleTlsIndex = _tbbAbortEligibleTlsIndex,
		});
	}

    private static bool GetFaultStackBounds(ulong stackPointer, ulong* bottom, ulong* top)
    {
        var found = TryGetGuestStackBounds(stackPointer, out var low, out var high);
        *bottom = low;
        *top = high;
        return found;
    }

	/// <summary>
	/// Test hook: the parameters this session would hand a platform fault
	/// thunk, so a per-OS integration test can build the real thunk.
	/// </summary>
	internal (uint HostRspSlotTlsIndex, nint TlsGetValueAddress, nint WorkerAbortStub, uint WorkerAbortEligibleTlsIndex)
		FaultThunkParametersForTest =>
		(_hostRspSlotTlsIndex, _tlsGetValueAddress, _workerAbortStub, _tbbAbortEligibleTlsIndex);

	/// <summary>
	/// Runs the shared recovery branches that a per-OS bridge's fabricated fault
	/// does not reach, so none of them needs the JIT the first time a real fault
	/// arrives. Called from the platform's own warm-up, which must additionally
	/// warm its signal/exception frame entry.
	/// </summary>
	/// <remarks>
	/// Under Rosetta 2 this is not merely a latency concern: the signal
	/// trampoline cannot enter x86 code that has never been translated, so a
	/// cold handler is silently never invoked and the faulting instruction
	/// retries forever.
	/// </remarks>
	internal static void WarmUpSharedFaultRecovery()
	{
		if (_faultRecoveryBackend is not { } backend)
		{
			return;
		}


		// The benign-exception path through VectoredHandler.
		EXCEPTION_RECORD record = default;
		record.ExceptionCode = DBG_PRINTEXCEPTION_C;
		byte* contextRecord = stackalloc byte[Win64ContextSize];
		new Span<byte>(contextRecord, Win64ContextSize).Clear();
		EXCEPTION_POINTERS pointers;
		pointers.ExceptionRecord = &record;
		pointers.ContextRecord = contextRecord;
        // Preserve the original managed recovery call and warm the tiny
        // session-dispatch wrapper too, before a Rosetta signal can enter it.
        _ = SharedFaultRecovery(&pointers);

		// The lazy-commit probe. 0x70000 is never guest-owned, so this walks the
		// vmem region scan and the PRT range check, then bails out silently.
		record.ExceptionCode = 3221225477u;
		record.NumberParameters = 2;
		record.ExceptionInformation[1] = 0x70000;
		_ = backend.TryHandleLazyCommittedPage(&record, 0, 0);
	}
}
