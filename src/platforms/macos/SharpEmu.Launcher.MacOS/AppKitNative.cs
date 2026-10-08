// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
namespace SharpEmu.Launcher.MacOS;

// Private SDK ABI. No Objective-C handles enter application/settings contracts.
internal static class AppKitNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<nint> AppKit = new(() => NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"));
    private static readonly Lazy<nint> Foundation = new(() => NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation"));
    internal static nint CommonRunLoopMode => Marshal.ReadIntPtr(NativeLibrary.GetExport(Foundation.Value, "NSRunLoopCommonModes"));
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Point(double X, double Y);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(double X, double Y, double Width, double Height);
    [DllImport(ObjC, EntryPoint="objc_getClass")] private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint="sel_registerName")] internal static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint="objc_allocateClassPair")] internal static extern nint AllocateClass(nint parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extra);
    [DllImport(ObjC, EntryPoint="objc_registerClassPair")] internal static extern void RegisterClass(nint cls);
    [DllImport(ObjC, EntryPoint="class_addMethod")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool AddMethod(nint cls, nint selector, nint callback, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern nint Send(nint obj, nint sel);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern nint Send(nint obj, nint sel, nint a);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern nint Send(nint obj, nint sel, nint a, nint b);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern nint Send(nint obj, nint sel, nint a, nint b, nint c);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern nint InitRect(nint obj, nint sel, Rect rect);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern nint InitWindow(nint obj, nint sel, Rect rect, nuint style, nuint backing, byte defer);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern nint InitPopUpButton(nint obj, nint sel, Rect rect, byte pullsDown);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] internal static extern void SetDouble(nint obj, nint sel, double value);
    [DllImport(ObjC, EntryPoint="objc_msgSend")] private static extern nint OtherEvent(nint cls, nint sel, nuint type, Point location, nuint flags, double timestamp, nint windowNumber, nint context, short subtype, nint data1, nint data2);
    internal static void StopOuterLoop(nint app)
    {
        Send(app, Selector("stop:"), 0);
        // stop: is observed after event processing. Wake a main run that was
        // stopped by a timer or after a modal loop returned without a new event.
        var wake = OtherEvent(Class("NSEvent"), Selector("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"), 15, new(0, 0), 0, 0, 0, 0, 0, 0, 0);
        if (wake != 0) Send(app, Selector("postEvent:atStart:"), wake, 1);
    }
    [DllImport(ObjC, EntryPoint="objc_msgSend")] private static extern nint StringUtf8(nint cls, nint sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("/usr/lib/libSystem.B.dylib")] internal static extern int pthread_main_np();
    internal static nint Class(string name) { _ = AppKit.Value; return GetClass(name); }
    internal static nint New(string name) => Send(Send(Class(name), Selector("alloc")), Selector("init"));
    internal static nint New(string name, Rect rect) => InitRect(Send(Class(name), Selector("alloc")), Selector("initWithFrame:"), rect);
    internal static nint String(string value) => StringUtf8(Class("NSString"), Selector("stringWithUTF8String:"), value);
    internal static string Text(nint value) => value == 0 ? string.Empty : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String"))) ?? string.Empty;
    internal static void Release(nint value) { if (value != 0) Send(value, Selector("release")); }
}
