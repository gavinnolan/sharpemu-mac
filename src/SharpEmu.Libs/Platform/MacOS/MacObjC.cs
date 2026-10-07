// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.Libs.Platform.MacOS;

[StructLayout(LayoutKind.Sequential)]
internal struct MacPoint
{
    public double X;
    public double Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MacSize
{
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MacRect
{
    public MacPoint Origin;
    public MacSize Size;

    public MacRect(double x, double y, double width, double height)
    {
        Origin = new MacPoint { X = x, Y = y };
        Size = new MacSize { Width = width, Height = height };
    }

    public bool Contains(MacPoint point) =>
        point.X >= Origin.X && point.X < Origin.X + Size.Width &&
        point.Y >= Origin.Y && point.Y < Origin.Y + Size.Height;
}

/// <summary>GCAcceleration and GCRotationRate: three doubles, same layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MacVector3
{
    public double X;
    public double Y;
    public double Z;
}

/// <summary>
/// GCDualSenseAdaptiveTriggerPositionalResistiveStrengths /
/// GCDualSenseAdaptiveTriggerPositionalAmplitudes: ten floats, one per trigger zone.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MacTriggerPositions
{
    public float Value0;
    public float Value1;
    public float Value2;
    public float Value3;
    public float Value4;
    public float Value5;
    public float Value6;
    public float Value7;
    public float Value8;
    public float Value9;

    public static MacTriggerPositions FromSpan(ReadOnlySpan<float> values)
    {
        var result = default(MacTriggerPositions);
        if (values.Length >= 10)
        {
            result.Value0 = values[0];
            result.Value1 = values[1];
            result.Value2 = values[2];
            result.Value3 = values[3];
            result.Value4 = values[4];
            result.Value5 = values[5];
            result.Value6 = values[6];
            result.Value7 = values[7];
            result.Value8 = values[8];
            result.Value9 = values[9];
        }

        return result;
    }
}

/// <summary>
/// Objective-C runtime access for the macOS-native host window, input and display
/// catalog. Deliberately self-contained — it binds only libobjc plus the OS
/// frameworks through <see cref="LibraryImportAttribute"/>, so this file (and the
/// rest of <c>Platform/MacOS</c>) can move into its own assembly without dragging
/// anything else along, and stays NativeAOT-clean.
/// </summary>
/// <remarks>
/// Struct returns need care. objc_msgSend only works for return values the ABI
/// hands back in registers; anything classified MEMORY must go through
/// objc_msgSend_stret. On x86-64 (the emulator's own RID, osx-x64 under Rosetta)
/// NSRect (4 doubles) and GCAcceleration (3 doubles) are both MEMORY, while on
/// arm64 they are homogeneous float aggregates returned in d0-d3. objc_msgSend_stret
/// does not exist on arm64, so the architecture check below picks the entry point
/// at runtime and the unused declaration is simply never resolved.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static partial class MacObjC
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

    public const string AppKitFramework = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string QuartzCoreFramework = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";
    public const string GameControllerFramework =
        "/System/Library/Frameworks/GameController.framework/GameController";
    public const string CoreHapticsFramework = "/System/Library/Frameworks/CoreHaptics.framework/CoreHaptics";
    public const string CoreGraphicsFramework =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>True where a MEMORY-class struct return needs objc_msgSend_stret.</summary>
    private static readonly bool UseStructReturnThunk =
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLibrary)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjCLibrary)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    public static nint Class(string name) => objc_getClass(name);

    public static nint Selector(string name) => sel_registerName(name);

    // ---- objc_msgSend overloads, one per distinct native signature ----

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument0, nint argument1);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(
        nint receiver,
        nint selector,
        nint argument0,
        nint argument1,
        nint argument2);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendIndex(nint receiver, nint selector, nuint index);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendObjectError(nint receiver, nint selector, nint argument, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendObjectObjectError(
        nint receiver,
        nint selector,
        nint argument0,
        nint argument1,
        ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SendBoolError(nint receiver, nint selector, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SendBoolTimeError(nint receiver, nint selector, double time, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SendBoolParametersAtTime(
        nint receiver,
        nint selector,
        nint parameters,
        double time,
        ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SendBool(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SendBool(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial float SendFloat(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nuint SendUInt(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInt(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial ushort SendUShort(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint argument0, nint argument1);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidUInt(nint receiver, nint selector, nuint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidDouble(nint receiver, nint selector, double argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidSize(nint receiver, nint selector, MacSize size);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidRect(nint receiver, nint selector, MacRect rect);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidFloat2(nint receiver, nint selector, float first, float second);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidFloat3(
        nint receiver,
        nint selector,
        float first,
        float second,
        float third);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidTriggerPositions(
        nint receiver,
        nint selector,
        MacTriggerPositions positions);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidTriggerPositionsFloat(
        nint receiver,
        nint selector,
        MacTriggerPositions positions,
        float frequency);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitWithRgb(
        nint receiver,
        nint selector,
        float red,
        float green,
        float blue);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitWithParameterIdValue(
        nint receiver,
        nint selector,
        nint parameterId,
        float value);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitWithParameterIdValueTime(
        nint receiver,
        nint selector,
        nint parameterId,
        float value,
        double relativeTime);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitHapticEvent(
        nint receiver,
        nint selector,
        nint eventType,
        nint parameters,
        double relativeTime,
        double duration);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitWindow(
        nint receiver,
        nint selector,
        MacRect contentRect,
        nuint styleMask,
        nuint backing,
        [MarshalAs(UnmanagedType.I1)] bool defer);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitWithFrame(nint receiver, nint selector, MacRect frame);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static unsafe partial nint SendArrayWithObjects(
        nint receiver,
        nint selector,
        nint* objects,
        nuint count);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendNextEvent(
        nint receiver,
        nint selector,
        ulong mask,
        nint untilDate,
        nint mode,
        [MarshalAs(UnmanagedType.I1)] bool dequeue);

    // ---- Struct returns ----

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial MacSize SendSize(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial MacPoint SendPoint(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial MacRect SendRectRegisters(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend_stret")]
    private static partial MacRect SendRectMemory(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial MacVector3 SendVector3Registers(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend_stret")]
    private static partial MacVector3 SendVector3Memory(nint receiver, nint selector);

    public static MacRect SendRect(nint receiver, nint selector) =>
        UseStructReturnThunk ? SendRectMemory(receiver, selector) : SendRectRegisters(receiver, selector);

    public static MacVector3 SendVector3(nint receiver, nint selector) =>
        UseStructReturnThunk
            ? SendVector3Memory(receiver, selector)
            : SendVector3Registers(receiver, selector);

    // ---- Conveniences ----

    public static bool Responds(nint receiver, string selector) =>
        receiver != 0 && SendBool(receiver, Selector("respondsToSelector:"), Selector(selector));

    public static bool IsKindOf(nint receiver, string className)
    {
        if (receiver == 0)
        {
            return false;
        }

        var type = Class(className);
        return type != 0 && SendBool(receiver, Selector("isKindOfClass:"), type);
    }

    /// <summary>Autoreleased NSString — valid only inside an autorelease pool
    /// unless the caller retains it.</summary>
    public static nint NsString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>Retained NSString, for values cached across autorelease pools.</summary>
    public static nint RetainedNsString(string value) => Send(NsString(value), Selector("retain"));

    public static string? ReadNsString(nint value)
    {
        if (value == 0)
        {
            return null;
        }

        var utf8 = Send(value, Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    public static nuint ArrayCount(nint array) =>
        array == 0 ? 0 : SendUInt(array, Selector("count"));

    public static nint ArrayObjectAt(nint array, nuint index) =>
        array == 0 ? 0 : SendIndex(array, Selector("objectAtIndex:"), index);

    public static unsafe nint ArrayWith(ReadOnlySpan<nint> objects)
    {
        fixed (nint* items = objects)
        {
            return SendArrayWithObjects(
                Class("NSArray"),
                Selector("arrayWithObjects:count:"),
                items,
                (nuint)objects.Length);
        }
    }

    public static nint EmptyArray() => Send(Class("NSArray"), Selector("array"));

    public static void Release(nint value)
    {
        if (value != 0)
        {
            SendVoid(value, Selector("release"));
        }
    }

    public static string DescribeError(nint error)
    {
        if (error == 0)
        {
            return "unknown error";
        }

        return ReadNsString(Send(error, Selector("localizedDescription"))) ?? "unknown error";
    }

    /// <summary>
    /// Reads an NSString* (or other pointer) constant exported by a framework,
    /// e.g. CHHapticEventTypeHapticContinuous or GCHapticsLocalityLeftHandle.
    /// Returns 0 when the symbol is absent on this OS version.
    /// </summary>
    public static nint ReadPointerConstant(nint frameworkHandle, string symbol)
    {
        if (frameworkHandle == 0 ||
            !NativeLibrary.TryGetExport(frameworkHandle, symbol, out var address) ||
            address == 0)
        {
            return 0;
        }

        return Marshal.ReadIntPtr(address);
    }
}
