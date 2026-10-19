// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.Platform.MacOS;
using SharpEmu.Platform.MacOS.Windowing;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Tools.MacWindowSmoke;

/// <summary>
/// Manual smoke harness for the native macOS presentation window: opens a
/// <see cref="MacHostWindow"/>, clears its CAMetalLayer with Metal for a few
/// seconds and exits, reporting what it observed.
/// </summary>
/// <remarks>
/// Exercises exactly what the emulator depends on and nothing else — AppKit
/// window and menu setup, the non-blocking NSApp event drain inside
/// <see cref="MacHostWindow.Run"/>, the layer-hosting CAMetalLayer with its
/// Retina contentsScale and drawable resize, the keyboard hotkeys and the
/// GameController poll. It runs natively on arm64, unlike the emulator itself
/// which needs osx-x64 for the guest CPU backend.
///
/// Usage: <c>dotnet run --project tools/SharpEmu.Tools.MacWindowSmoke [seconds]</c>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("This smoke harness is macOS only.");
            return 2;
        }

        // The display catalog is a registered provider, so the harness composes the macOS
        // platform package the same way the application does.
        MacPlatform.Register();

        var fullscreen = args.Contains("--fullscreen", StringComparer.Ordinal);
        var seconds = args
            .Where(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            .Select(argument => double.TryParse(argument, out var parsed) ? parsed : 0.0)
            .FirstOrDefault(value => value > 0, 2.0);
        return RunSmoke(seconds, fullscreen);
    }

    [SupportedOSPlatform("macos")]
    private static int RunSmoke(double seconds, bool fullscreen)
    {
        Console.WriteLine($"displays={DescribeDisplays()}");
        var options = HostVideoOptions.Default with
        {
            Width = 1280,
            Height = 720,
            WindowMode = fullscreen ? HostWindowMode.ExclusiveFullscreen : HostWindowMode.Windowed,
        };
        var hudToggles = 0;
        using var window = new MacHostWindow("SharpEmu Metal smoke", options, () => hudToggles++);

        var layer = window.CreateMetalLayer();
        Console.WriteLine($"metal_layer=0x{layer:X} pixel_size={window.PixelSize}");
        if (layer == 0)
        {
            Console.Error.WriteLine("FAIL: no CAMetalLayer");
            return 1;
        }

        var device = Metal.MTLCreateSystemDefaultDevice();
        if (device == 0)
        {
            Console.Error.WriteLine("FAIL: no Metal device");
            return 1;
        }

        Console.WriteLine($"device={Metal.ReadString(Metal.Send(device, Metal.Sel("name")))}");
        Metal.SendVoid(layer, Metal.Sel("setDevice:"), device);
        // 80 == MTLPixelFormatBGRA8Unorm, matching the presenter.
        Metal.Send(layer, Metal.Sel("setPixelFormat:"), 80);
        Metal.SendVoidBool(layer, Metal.Sel("setOpaque:"), true);
        Metal.SendVoidBool(layer, Metal.Sel("setFramebufferOnly:"), true);
        var queue = Metal.Send(device, Metal.Sel("newCommandQueue"));

        var frames = 0;
        var missedDrawables = 0;
        var surfaceResyncs = 0;
        var lastSize = window.PixelSize;
        var resizes = 0;
        var timer = Stopwatch.StartNew();
        window.Run(
            initialize: () => Console.WriteLine("loop_started=true"),
            render: _ =>
            {
                if (timer.Elapsed.TotalSeconds >= seconds)
                {
                    window.Close();
                    return;
                }

                if (window.ConsumeSurfaceRestore())
                {
                    surfaceResyncs++;
                }

                var size = window.PixelSize;
                if (size != lastSize)
                {
                    lastSize = size;
                    resizes++;
                }

                Metal.SendVoidSize(
                    layer,
                    Metal.Sel("setDrawableSize:"),
                    new Metal.Size { Width = size.Width, Height = size.Height });
                if (ClearOnce(queue, layer, timer.Elapsed.TotalSeconds))
                {
                    frames++;
                }
                else
                {
                    missedDrawables++;
                }
            },
            closing: () => Console.WriteLine("loop_closing=true"));

        Console.WriteLine(
            $"frames={frames} missed_drawables={missedDrawables} surface_resyncs={surfaceResyncs} " +
            $"resizes={resizes} final_pixel_size={lastSize} hud_toggles={hudToggles} " +
            $"closed_by_user={window.ClosedByUser} elapsed={timer.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"native_modules_sdl={DescribeSdlModules()}");
        return frames > 0 ? 0 : 1;
    }

    /// <summary>
    /// Proves the headline invariant: nothing on the macOS presentation path
    /// pulls SDL into the process.
    /// </summary>
    private static string DescribeSdlModules()
    {
        try
        {
            var loaded = Process.GetCurrentProcess().Modules
                .Cast<ProcessModule>()
                .Select(module => module.ModuleName ?? string.Empty)
                .Where(name => name.Contains("SDL", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return loaded.Length == 0 ? "none" : string.Join(",", loaded);
        }
        catch (Exception exception)
        {
            return $"unavailable ({exception.GetType().Name})";
        }
    }

    [SupportedOSPlatform("macos")]
    private static string DescribeDisplays()
    {
        var displays = HostDisplayCatalog.Query();
        return string.Join(
            " | ",
            displays.Select(display =>
                $"[{display.Index}] {display.Name}: {display.Modes.Count} modes, " +
                $"best {display.Modes.FirstOrDefault()}"));
    }

    /// <summary>
    /// One clear-and-present through a render command encoder, with the clear
    /// colour animated so a watcher can see the window is live.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static bool ClearOnce(nint queue, nint layer, double time)
    {
        var pool = Metal.objc_autoreleasePoolPush();
        try
        {
            var drawable = Metal.Send(layer, Metal.Sel("nextDrawable"));
            if (drawable == 0)
            {
                return false;
            }

            var texture = Metal.Send(drawable, Metal.Sel("texture"));
            var descriptor = Metal.Send(
                Metal.Class("MTLRenderPassDescriptor"),
                Metal.Sel("renderPassDescriptor"));
            var attachment = Metal.SendIndex(
                Metal.Send(descriptor, Metal.Sel("colorAttachments")),
                Metal.Sel("objectAtIndexedSubscript:"),
                0);
            Metal.SendVoid(attachment, Metal.Sel("setTexture:"), texture);
            Metal.SendVoidUInt(attachment, Metal.Sel("setLoadAction:"), 2); // MTLLoadActionClear
            Metal.SendVoidUInt(attachment, Metal.Sel("setStoreAction:"), 1); // MTLStoreActionStore
            Metal.SendVoidClearColor(
                attachment,
                Metal.Sel("setClearColor:"),
                new Metal.ClearColor
                {
                    Red = 0.5 + (0.5 * Math.Sin(time * 3.0)),
                    Green = 0.1,
                    Blue = 0.5 + (0.5 * Math.Cos(time * 3.0)),
                    Alpha = 1.0,
                });

            var commandBuffer = Metal.Send(queue, Metal.Sel("commandBuffer"));
            var encoder = Metal.Send(
                commandBuffer,
                Metal.Sel("renderCommandEncoderWithDescriptor:"),
                descriptor);
            Metal.SendVoid(encoder, Metal.Sel("endEncoding"));
            Metal.SendVoid(commandBuffer, Metal.Sel("presentDrawable:"), drawable);
            Metal.SendVoid(commandBuffer, Metal.Sel("commit"));
            return true;
        }
        finally
        {
            Metal.objc_autoreleasePoolPop(pool);
        }
    }
}

/// <summary>Minimal Objective-C/Metal interop, local to this tool.</summary>
[SupportedOSPlatform("macos")]
internal static partial class Metal
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClearColor
    {
        public double Red;
        public double Green;
        public double Blue;
        public double Alpha;
    }

    [LibraryImport(MetalFramework)]
    public static partial nint MTLCreateSystemDefaultDevice();

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLibrary)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjCLibrary)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendIndex(nint receiver, nint selector, nuint index);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidUInt(nint receiver, nint selector, nuint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidSize(nint receiver, nint selector, Size size);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidClearColor(nint receiver, nint selector, ClearColor color);

    public static nint Class(string name) => objc_getClass(name);

    public static nint Sel(string name) => sel_registerName(name);

    public static string? ReadString(nint nsString)
    {
        if (nsString == 0)
        {
            return null;
        }

        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }
}
