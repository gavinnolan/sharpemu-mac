// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// One global Objective-C block that Metal calls when a command buffer completes,
/// so the host learns about a completion instead of polling for it.
///
/// <c>-[MTLSharedEvent waitUntilSignaledValue:timeoutMS:]</c> measured as not waking
/// this process on a GPU signal, which left the completion path a one-millisecond
/// poll: measured at two to five milliseconds of latency per host wait, and a wait
/// that outlived the poll deadline cost a quarter of a second. A completion handler
/// is a real wakeup, and it needs no per-buffer object: the block is a global literal
/// (<c>_NSConcreteGlobalBlock</c>, <c>BLOCK_IS_GLOBAL</c>) whose <c>invoke</c> is an
/// <see cref="UnmanagedCallersOnlyAttribute"/> function, built once and handed to
/// every <c>addCompletedHandler:</c>.
///
/// The handler carries no state: the shared event still orders completions, so all
/// the wakeup has to say is "look again".
/// </summary>
internal static unsafe class MetalCompletionSignal
{
    // Block_private.h: BLOCK_IS_GLOBAL.
    private const int BlockIsGlobal = 1 << 28;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    private static readonly object _gate = new();
    private static Action? _handler;
    private static nint _block;
    private static bool _attempted;
    private static long _invocations;

    /// <summary>The block to hand to <c>addCompletedHandler:</c>, or zero.</summary>
    public static nint Block => Volatile.Read(ref _block);

    public static long Invocations => Interlocked.Read(ref _invocations);

    /// <summary>
    /// Routes every command-buffer completion to <paramref name="handler"/>. Returns
    /// false when the block could not be built, which leaves the caller polling.
    /// </summary>
    public static bool TryBind(Action handler)
    {
        lock (_gate)
        {
            _handler = handler;
            if (!_attempted)
            {
                _attempted = true;
                _block = TryCreateBlock();
            }

            return _block != 0;
        }
    }

    public static void Unbind(Action handler)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_handler, handler))
            {
                _handler = null;
            }
        }
    }

    private static nint TryCreateBlock()
    {
        try
        {
            if (!NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out var library) ||
                !NativeLibrary.TryGetExport(library, "_NSConcreteGlobalBlock", out var isa) ||
                isa == 0)
            {
                return 0;
            }

            var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            descriptor->Reserved = 0;
            descriptor->Size = (nuint)sizeof(BlockLiteral);
            var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
            block->Isa = isa;
            block->Flags = BlockIsGlobal;
            block->Reserved = 0;
            block->Invoke = (nint)(delegate* unmanaged<nint, nint, void>)&Invoke;
            block->Descriptor = (nint)descriptor;
            return (nint)block;
        }
        catch (Exception exception)
        {
            MetalV2Diagnostics.WarnOnce(
                "completion-block",
                $"A Metal completion handler block could not be built; completions will be polled: {exception.Message}");
            return 0;
        }
    }

    // Metal calls this on one of its own threads. It must not throw and must not
    // block: all it does is wake whoever is waiting on the shared event.
    [UnmanagedCallersOnly]
    private static void Invoke(nint block, nint commandBuffer)
    {
        _ = block;
        _ = commandBuffer;
        try
        {
            Interlocked.Increment(ref _invocations);
            Volatile.Read(ref _handler)?.Invoke();
        }
        catch
        {
            // A completion wakeup can never fail the submission that produced it.
        }
    }
}
