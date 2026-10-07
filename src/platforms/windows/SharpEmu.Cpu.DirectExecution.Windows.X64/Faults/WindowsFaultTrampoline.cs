// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Cpu.DirectExecution.Windows.X64;

/// <summary>
/// The emitted pre-filter thunk that stands between the kernel's vectored
/// exception dispatch and the shared managed recovery chain.
/// </summary>
internal static unsafe class WindowsFaultTrampoline
{
	/// <summary>
	/// Emits the native thunk a vectored handler is registered as: it filters
	/// exception codes that must never enter managed code, redirects a native
	/// guest worker that faulted unrecoverably to its abort stub, and - when
	/// the fault happened on a guest stack - puts the host stack bounds back
	/// in the TEB before calling <paramref name="managedHandler"/>.
	/// </summary>
	/// <remarks>
	/// Moved verbatim out of
	/// <c>SharpEmu.Core/Cpu/Native/DirectExecutionBackend.cs</c> by W10. The
	/// only changes are that the four values it used to read from backend
	/// fields now arrive as parameters, and that the neutral memory wrappers
	/// it called in Core are spelled as the host memory calls they already
	/// were. Returns 0 on failure.
	/// </remarks>
	internal static unsafe nint Create(
		nint managedHandler,
		uint hostRspSlotTlsIndex,
		nint tlsGetValueAddress,
		nint workerAbortStub,
		uint workerAbortEligibleTlsIndex)
	{
		// Filter runtime exceptions before entering managed code.
		// Reserve space for native worker recovery and fatal diagnostics.
		const uint stubSize = 2048u;
		void* ptr = HostMemory.Alloc(null, stubSize, 12288u, 64u);
		if (ptr == null)
		{
			return 0;
		}

		byte* code = (byte*)ptr;
		int offset = 0;

		ReadOnlySpan<uint> nonManagedExceptionCodes =
		[
			0xE0434352u, // CLR managed exception
			0xE06D7363u, // MSVC C++ exception
			0xC0000409u, // STATUS_STACK_BUFFER_OVERRUN / FailFast
			0xC00000FDu, // STATUS_STACK_OVERFLOW
			0x40010006u, // ANSI debug output
			0x4001000Au, // Unicode debug output
		];
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x01); // mov rax, [rcx]
		WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x00); // mov eax, [rax] ExceptionCode
		var passJumpOffsets = stackalloc int[nonManagedExceptionCodes.Length];
		int fastFailJumpSlot = -1;
		for (int i = 0; i < nonManagedExceptionCodes.Length; i++)
		{
			WindowsStackBounds.EmitByte(code, ref offset, 0x3D);
			WindowsStackBounds.EmitUInt32(code, ref offset, nonManagedExceptionCodes[i]);
			WindowsStackBounds.EmitByte(code, ref offset, 0x74);
			passJumpOffsets[i] = offset;
			WindowsStackBounds.EmitByte(code, ref offset, 0x00);
			if (nonManagedExceptionCodes[i] == 0xC0000409u)
			{
				fastFailJumpSlot = i;
			}
		}

		WindowsStackBounds.EmitByte(code, ref offset, 0xE9); // jmp mainBody (rel32; FastFail breadcrumb sits between)
		var mainBodyJumpSlot = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);

		int passOffset = offset;
		WindowsStackBounds.EmitByte(code, ref offset, 0x31); WindowsStackBounds.EmitByte(code, ref offset, 0xC0); // xor eax, eax
		WindowsStackBounds.EmitByte(code, ref offset, 0xC3);

		int fastFailPassOffset = offset;
		var fastFailLogInstalled = false;
		nint kernel32 = WindowsExecutionStubs.GetModuleHandle("kernel32.dll");
		nint getStdHandle = kernel32 != 0 ? WindowsExecutionStubs.GetProcAddress(kernel32, "GetStdHandle") : 0;
		nint writeFile = kernel32 != 0 ? WindowsExecutionStubs.GetProcAddress(kernel32, "WriteFile") : 0;
		if (fastFailJumpSlot >= 0 && getStdHandle != 0 && writeFile != 0)
		{
			// Prefix + Context.Rip hex (AMD64 CONTEXT.Rip @ 0xF8) + newline.
			// Keep in sync with WindowsFaultHandling.CreateHandlerThunk.
			ReadOnlySpan<byte> msg =
				"[LOADER][FATAL] VEH_PASS FastFail 0xC0000409 (live trampoline; skip managed VEH) rip=0x"u8;
			ReadOnlySpan<byte> hexDigits = "0123456789ABCDEF"u8;
			// rcx=EXCEPTION_POINTERS*: capture Rip into r10 before clobbering.
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x41);
			WindowsStackBounds.EmitByte(code, ref offset, 0x08); // mov rax, [rcx+8] ContextRecord*
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x90);
			WindowsStackBounds.EmitUInt32(code, ref offset, 0xF8u); // mov r10, [rax+0xF8] Rip
			WindowsStackBounds.EmitByte(code, ref offset, 0x50);
			WindowsStackBounds.EmitByte(code, ref offset, 0x51);
			WindowsStackBounds.EmitByte(code, ref offset, 0x52);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x50);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x51);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x52); // push r10 (Rip)
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
			WindowsStackBounds.EmitByte(code, ref offset, 0xEC); WindowsStackBounds.EmitByte(code, ref offset, 0x40); // sub rsp, 0x40 (hex buf @ +0x30)
			WindowsStackBounds.EmitByte(code, ref offset, 0xB9); WindowsStackBounds.EmitUInt32(code, ref offset, unchecked((uint)-12));
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = getStdHandle;
			offset += sizeof(nint);
			WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x89);
			WindowsStackBounds.EmitByte(code, ref offset, 0x44); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x28); // mov [rsp+0x28], rax  stderr handle
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xC1);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			var msgAbsSlot = offset;
			*(nint*)(code + offset) = 0;
			offset += sizeof(nint);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xC2);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			WindowsStackBounds.EmitUInt32(code, ref offset, (uint)msg.Length);
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8D);
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x20);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xC7);
			WindowsStackBounds.EmitByte(code, ref offset, 0x44); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x20); WindowsStackBounds.EmitUInt32(code, ref offset, 0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xC7);
			WindowsStackBounds.EmitByte(code, ref offset, 0x44); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x38); WindowsStackBounds.EmitUInt32(code, ref offset, 0); // lpOverlapped slot
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = writeFile;
			offset += sizeof(nint);
			WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);

			// Hex-encode Rip. Stack after sub 0x40: [rsp+0x40]=saved Rip (push r10).
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8B);
			WindowsStackBounds.EmitByte(code, ref offset, 0x54); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x40); // mov r10, [rsp+0x40]
			WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			var hexDigitsAbsSlot = offset;
			*(nint*)(code + offset) = 0;
			offset += sizeof(nint); // mov r8, hexDigits
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8D);
			WindowsStackBounds.EmitByte(code, ref offset, 0x5C); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x30); // lea r11, [rsp+0x30] hex out
			WindowsStackBounds.EmitByte(code, ref offset, 0xB9); WindowsStackBounds.EmitUInt32(code, ref offset, 16u); // ecx = 16 nibbles
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xD0); // mov rax, r10
			int hexLoopOffset = offset;
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xC1); WindowsStackBounds.EmitByte(code, ref offset, 0xC0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x04); // rol rax, 4
			WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xC2); // mov edx, eax
			WindowsStackBounds.EmitByte(code, ref offset, 0x83); WindowsStackBounds.EmitByte(code, ref offset, 0xE2); WindowsStackBounds.EmitByte(code, ref offset, 0x0F); // and edx, 0xF
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0xB6);
			WindowsStackBounds.EmitByte(code, ref offset, 0x14); WindowsStackBounds.EmitByte(code, ref offset, 0x10); // movzx edx, byte [r8+rdx]
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x88); WindowsStackBounds.EmitByte(code, ref offset, 0x13); // mov [r11], dl
			WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xC3); // inc r11
			WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xC9); // dec ecx
			WindowsStackBounds.EmitByte(code, ref offset, 0x75);
			WindowsStackBounds.EmitByte(code, ref offset, unchecked((byte)(hexLoopOffset - (offset + 1)))); // jnz hexLoop (rel8)
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0xC6); WindowsStackBounds.EmitByte(code, ref offset, 0x03);
			WindowsStackBounds.EmitByte(code, ref offset, 0x0A); // mov byte [r11], '\n'

			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x8B);
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x28); // mov rcx, [rsp+0x28] stderr
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x8D);
			WindowsStackBounds.EmitByte(code, ref offset, 0x54); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x30); // lea rdx, [rsp+0x30]
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			WindowsStackBounds.EmitUInt32(code, ref offset, 17u); // 16 hex + newline
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8D);
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x20);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xC7);
			WindowsStackBounds.EmitByte(code, ref offset, 0x44); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x20); WindowsStackBounds.EmitUInt32(code, ref offset, 0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xC7);
			WindowsStackBounds.EmitByte(code, ref offset, 0x44); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
			WindowsStackBounds.EmitByte(code, ref offset, 0x38); WindowsStackBounds.EmitUInt32(code, ref offset, 0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = writeFile;
			offset += sizeof(nint);
			WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);

			// Flush redirected stderr so FastFail rip survives process teardown.
			nint flushFileBuffers = kernel32 != 0 ? WindowsExecutionStubs.GetProcAddress(kernel32, "FlushFileBuffers") : 0;
			if (flushFileBuffers != 0)
			{
				WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x8B);
				WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x24);
				WindowsStackBounds.EmitByte(code, ref offset, 0x28); // mov rcx, stderr
				WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
				*(nint*)(code + offset) = flushFileBuffers;
				offset += sizeof(nint);
				WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);
			}

			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
			WindowsStackBounds.EmitByte(code, ref offset, 0xC4); WindowsStackBounds.EmitByte(code, ref offset, 0x40);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5A); // pop r10
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x59);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x58);
			WindowsStackBounds.EmitByte(code, ref offset, 0x5A);
			WindowsStackBounds.EmitByte(code, ref offset, 0x59);
			WindowsStackBounds.EmitByte(code, ref offset, 0x58);
			WindowsStackBounds.EmitByte(code, ref offset, 0x31); WindowsStackBounds.EmitByte(code, ref offset, 0xC0);
			WindowsStackBounds.EmitByte(code, ref offset, 0xC3);

			var msgOffset = offset;
			for (int i = 0; i < msg.Length; i++)
			{
				WindowsStackBounds.EmitByte(code, ref offset, msg[i]);
			}

			var hexDigitsOffset = offset;
			for (int i = 0; i < hexDigits.Length; i++)
			{
				WindowsStackBounds.EmitByte(code, ref offset, hexDigits[i]);
			}

			*(nint*)(code + msgAbsSlot) = (nint)ptr + msgOffset;
			*(nint*)(code + hexDigitsAbsSlot) = (nint)ptr + hexDigitsOffset;
			code[passJumpOffsets[fastFailJumpSlot]] =
				checked((byte)(fastFailPassOffset - (passJumpOffsets[fastFailJumpSlot] + 1)));
			fastFailLogInstalled = true;
		}

		int mainBodyOffset = offset;
		*(int*)(code + mainBodyJumpSlot) = mainBodyOffset - (mainBodyJumpSlot + sizeof(int));
		for (int i = 0; i < nonManagedExceptionCodes.Length; i++)
		{
			if (i == fastFailJumpSlot && fastFailLogInstalled)
			{
				continue;
			}

			code[passJumpOffsets[i]] = checked((byte)(passOffset - (passJumpOffsets[i] + 1)));
		}

		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x54); // push r12
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x55); // push r13
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x56); // push r14
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x57); // push r15
		WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xE4); // mov r12, rsp
		WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xCD); // mov r13, rcx
		WindowsStackBounds.EmitExceptionHostStackBounds(code, ref offset, hostRspSlotTlsIndex, tlsGetValueAddress);

		// Native worker EXECUTE-AV abort without managed VEH.
		// Do NOT catch read/write AVs — workers need managed lazy-commit (tLTJ
		// silent-die when every worker AV was aborted). Execute faults on
		// tbb_thead are the concurrent-managed FailFast case (tLTC).
		int tbbFallthroughJump = -1;
		if (workerAbortStub != 0 &&
			tlsGetValueAddress != 0 &&
			hostRspSlotTlsIndex != uint.MaxValue)
		{
			WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x8B);
			WindowsStackBounds.EmitByte(code, ref offset, 0x45); WindowsStackBounds.EmitByte(code, ref offset, 0x00); // mov rax, [r13]
			WindowsStackBounds.EmitByte(code, ref offset, 0x81); WindowsStackBounds.EmitByte(code, ref offset, 0x38);
			WindowsStackBounds.EmitUInt32(code, ref offset, 0xC0000005u); // cmp dword [rax], AV
			WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x85);
			tbbFallthroughJump = offset;
			WindowsStackBounds.EmitUInt32(code, ref offset, 0u); // jne fallthrough

			// ExceptionInformation[0] == 8 → EXECUTE (DEP). Offset 32 on x64 RECORD.
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
			WindowsStackBounds.EmitByte(code, ref offset, 0xB8); WindowsStackBounds.EmitUInt32(code, ref offset, 32u);
			WindowsStackBounds.EmitByte(code, ref offset, 0x08); // cmp qword [rax+32], 8
			WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x85);
			var tbbNotExecuteJump = offset;
			WindowsStackBounds.EmitUInt32(code, ref offset, 0u);

			int tbbNotEligibleJump = -1;
			if (workerAbortEligibleTlsIndex != uint.MaxValue)
			{
				WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
				WindowsStackBounds.EmitByte(code, ref offset, 0xEC); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
				WindowsStackBounds.EmitByte(code, ref offset, 0xB9);
				WindowsStackBounds.EmitUInt32(code, ref offset, workerAbortEligibleTlsIndex);
				WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
				*(nint*)(code + offset) = tlsGetValueAddress;
				offset += sizeof(nint);
				WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);
				WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
				WindowsStackBounds.EmitByte(code, ref offset, 0xC4); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
				WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x85);
				WindowsStackBounds.EmitByte(code, ref offset, 0xC0);
				WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x84);
				tbbNotEligibleJump = offset;
				WindowsStackBounds.EmitUInt32(code, ref offset, 0u);
			}

			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
			WindowsStackBounds.EmitByte(code, ref offset, 0xEC); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
			WindowsStackBounds.EmitByte(code, ref offset, 0xB9);
			WindowsStackBounds.EmitUInt32(code, ref offset, hostRspSlotTlsIndex);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = tlsGetValueAddress;
			offset += sizeof(nint);
			WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
			WindowsStackBounds.EmitByte(code, ref offset, 0xC4); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x85);
			WindowsStackBounds.EmitByte(code, ref offset, 0xC0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x84);
			var tbbNoHostRspJump = offset;
			WindowsStackBounds.EmitUInt32(code, ref offset, 0u);
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8B);
			WindowsStackBounds.EmitByte(code, ref offset, 0x00); // mov r8, [rax] hostRsp
			WindowsStackBounds.EmitByte(code, ref offset, 0x4D); WindowsStackBounds.EmitByte(code, ref offset, 0x85);
			WindowsStackBounds.EmitByte(code, ref offset, 0xC0);
			WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x84);
			var tbbZeroRspJump = offset;
			WindowsStackBounds.EmitUInt32(code, ref offset, 0u);

			WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x83);
			WindowsStackBounds.EmitByte(code, ref offset, 0xE0); WindowsStackBounds.EmitByte(code, ref offset, 0xF0); // and r8, ~0xF
			WindowsStackBounds.EmitByte(code, ref offset, 0x4D); WindowsStackBounds.EmitByte(code, ref offset, 0x8B);
			WindowsStackBounds.EmitByte(code, ref offset, 0x4D); WindowsStackBounds.EmitByte(code, ref offset, 0x08); // mov r9, [r13+8]
			WindowsStackBounds.EmitByte(code, ref offset, 0x4D); WindowsStackBounds.EmitByte(code, ref offset, 0x89);
			WindowsStackBounds.EmitByte(code, ref offset, 0x81); WindowsStackBounds.EmitUInt32(code, ref offset, 0x98u); // Context.Rsp
			WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
			*(nint*)(code + offset) = workerAbortStub;
			offset += sizeof(nint);
			WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x89);
			WindowsStackBounds.EmitByte(code, ref offset, 0x81); WindowsStackBounds.EmitUInt32(code, ref offset, 0xF8u); // Context.Rip
			WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0xC7);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x78);
			WindowsStackBounds.EmitUInt32(code, ref offset, 0u); // Context.Rax = 0

			WindowsStackBounds.EmitByte(code, ref offset, 0xB8); WindowsStackBounds.EmitUInt32(code, ref offset, unchecked((uint)-1));
			WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x89);
			WindowsStackBounds.EmitByte(code, ref offset, 0xE4); // mov rsp, r12
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5F);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5E);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5D);
			WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5C);
			WindowsStackBounds.EmitByte(code, ref offset, 0xC3);

			int tbbFallthroughOffset = offset;
			*(int*)(code + tbbFallthroughJump) = tbbFallthroughOffset - (tbbFallthroughJump + sizeof(int));
			*(int*)(code + tbbNotExecuteJump) = tbbFallthroughOffset - (tbbNotExecuteJump + sizeof(int));
			if (tbbNotEligibleJump >= 0)
			{
				*(int*)(code + tbbNotEligibleJump) = tbbFallthroughOffset - (tbbNotEligibleJump + sizeof(int));
			}
			*(int*)(code + tbbNoHostRspJump) = tbbFallthroughOffset - (tbbNoHostRspJump + sizeof(int));
			*(int*)(code + tbbZeroRspJump) = tbbFallthroughOffset - (tbbZeroRspJump + sizeof(int));
		}

		WindowsStackBounds.EmitByte(code, ref offset, 0x65); WindowsStackBounds.EmitByte(code, ref offset, 0x48); // mov rax, gs:[8]
		WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x04); WindowsStackBounds.EmitByte(code, ref offset, 0x25);
		WindowsStackBounds.EmitUInt32(code, ref offset, 8u);
		WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x39); WindowsStackBounds.EmitByte(code, ref offset, 0xC4); // cmp r12, rax
		WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x83); // jae guestStack
		int aboveStackJump = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);
		WindowsStackBounds.EmitByte(code, ref offset, 0x65); WindowsStackBounds.EmitByte(code, ref offset, 0x48); // mov rax, gs:[0x10]
		WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x04); WindowsStackBounds.EmitByte(code, ref offset, 0x25);
		WindowsStackBounds.EmitUInt32(code, ref offset, 0x10u);
		WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x39); WindowsStackBounds.EmitByte(code, ref offset, 0xC4); // cmp r12, rax
		WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x82); // jb guestStack
		int belowStackJump = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);

		// A host-stack access violation can originate in managed or JIT code.
		// Do not enter a managed VEH callback from that state. Direct guest code
		// uses the guest stack and keeps the existing managed recovery path.
		WindowsStackBounds.EmitByte(code, ref offset, 0x49); WindowsStackBounds.EmitByte(code, ref offset, 0x8B);
		WindowsStackBounds.EmitByte(code, ref offset, 0x45); WindowsStackBounds.EmitByte(code, ref offset, 0x00); // mov rax, [r13]
		WindowsStackBounds.EmitByte(code, ref offset, 0x81); WindowsStackBounds.EmitByte(code, ref offset, 0x38);
		WindowsStackBounds.EmitUInt32(code, ref offset, 0xC0000005u);
		WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x85);
		int hostNotAccessViolationJump = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);
		WindowsStackBounds.EmitByte(code, ref offset, 0x31); WindowsStackBounds.EmitByte(code, ref offset, 0xC0);
		WindowsStackBounds.EmitSavedStackBounds(code, ref offset, restore: true);
		WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x89);
		WindowsStackBounds.EmitByte(code, ref offset, 0xE4); // mov rsp, r12
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5F);
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5E);
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5D);
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5C);
		WindowsStackBounds.EmitByte(code, ref offset, 0xC3);
		int hostManagedOffset = offset;
		*(int*)(code + hostNotAccessViolationJump) =
			hostManagedOffset - (hostNotAccessViolationJump + sizeof(int));

		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83); WindowsStackBounds.EmitByte(code, ref offset, 0xEC); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
		// A managed handler can wait for another faulting thread.
		// Do not hold a native lock across the callback.
		WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xE9); // mov rcx, r13
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = managedHandler;
		offset += sizeof(nint);
		WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83); WindowsStackBounds.EmitByte(code, ref offset, 0xC4); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
		WindowsStackBounds.EmitByte(code, ref offset, 0xE9);
		int hostRestoreJump = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);

		int guestStackOffset = offset;
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83); WindowsStackBounds.EmitByte(code, ref offset, 0xEC); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
		WindowsStackBounds.EmitByte(code, ref offset, 0xB9);
		WindowsStackBounds.EmitUInt32(code, ref offset, hostRspSlotTlsIndex);
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = tlsGetValueAddress;
		offset += sizeof(nint);
		WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83); WindowsStackBounds.EmitByte(code, ref offset, 0xC4); WindowsStackBounds.EmitByte(code, ref offset, 0x28);
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x85); WindowsStackBounds.EmitByte(code, ref offset, 0xC0); // test rax, rax
		WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x84);
		int missingTlsJump = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);
		WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x18); // mov r11, [rax]
		WindowsStackBounds.EmitByte(code, ref offset, 0x4D); WindowsStackBounds.EmitByte(code, ref offset, 0x85); WindowsStackBounds.EmitByte(code, ref offset, 0xDB); // test r11, r11
		WindowsStackBounds.EmitByte(code, ref offset, 0x0F); WindowsStackBounds.EmitByte(code, ref offset, 0x84);
		int missingHostStackJump = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);
		WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xDC); // mov rsp, r11
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83); WindowsStackBounds.EmitByte(code, ref offset, 0xEC); WindowsStackBounds.EmitByte(code, ref offset, 0x48);
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0x44); WindowsStackBounds.EmitByte(code, ref offset, 0x24); WindowsStackBounds.EmitByte(code, ref offset, 0x38); // mov [rsp+0x38],rax
		WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xE9); // mov rcx, r13
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = managedHandler;
		offset += sizeof(nint);
		WindowsStackBounds.EmitByte(code, ref offset, 0xFF); WindowsStackBounds.EmitByte(code, ref offset, 0xD0);
		WindowsStackBounds.EmitExceptionStackSelection(code, ref offset);
		WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x8B); WindowsStackBounds.EmitByte(code, ref offset, 0x54); WindowsStackBounds.EmitByte(code, ref offset, 0x24); WindowsStackBounds.EmitByte(code, ref offset, 0x38); // mov r10,[rsp+0x38]
		WindowsStackBounds.EmitHostStackBounds(code, ref offset, save: true);
		WindowsStackBounds.EmitByte(code, ref offset, 0x48); WindowsStackBounds.EmitByte(code, ref offset, 0x83); WindowsStackBounds.EmitByte(code, ref offset, 0xC4); WindowsStackBounds.EmitByte(code, ref offset, 0x48);
		WindowsStackBounds.EmitByte(code, ref offset, 0xE9);
		int guestRestoreJump = offset;
		WindowsStackBounds.EmitUInt32(code, ref offset, 0u);

		int passThroughOffset = offset;
		WindowsStackBounds.EmitByte(code, ref offset, 0x31); WindowsStackBounds.EmitByte(code, ref offset, 0xC0); // xor eax, eax
		int restoreOffset = offset;
		WindowsStackBounds.EmitSavedStackBounds(code, ref offset, restore: true);
		WindowsStackBounds.EmitByte(code, ref offset, 0x4C); WindowsStackBounds.EmitByte(code, ref offset, 0x89); WindowsStackBounds.EmitByte(code, ref offset, 0xE4); // mov rsp, r12
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5F);
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5E);
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5D);
		WindowsStackBounds.EmitByte(code, ref offset, 0x41); WindowsStackBounds.EmitByte(code, ref offset, 0x5C);
		WindowsStackBounds.EmitByte(code, ref offset, 0xC3);

		*(int*)(code + aboveStackJump) = guestStackOffset - (aboveStackJump + sizeof(int));
		*(int*)(code + belowStackJump) = guestStackOffset - (belowStackJump + sizeof(int));
		*(int*)(code + hostRestoreJump) = restoreOffset - (hostRestoreJump + sizeof(int));
		*(int*)(code + missingTlsJump) = passThroughOffset - (missingTlsJump + sizeof(int));
		*(int*)(code + missingHostStackJump) = passThroughOffset - (missingHostStackJump + sizeof(int));
		*(int*)(code + guestRestoreJump) = restoreOffset - (guestRestoreJump + sizeof(int));

		if (offset > (int)stubSize)
		{
			Console.Error.WriteLine(
				$"[LOADER][ERROR] Exception handler trampoline overflow: used={offset} cap={stubSize}");
			HostMemory.Free(ptr, 0, 0x8000u);
			return 0;
		}

		Console.Error.WriteLine(
			$"[LOADER][INFO] VEH trampoline built: bytes={offset} native_worker_abort=" +
			$"{(workerAbortStub != 0 && hostRspSlotTlsIndex != uint.MaxValue)}");

		HostMemory.Protect(ptr, stubSize, 32u, out _);
		HostMemory.FlushInstructionCache(ptr, (nuint)offset);
		return (nint)ptr;
	}
}
