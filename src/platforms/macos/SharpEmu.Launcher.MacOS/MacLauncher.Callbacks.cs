// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using static SharpEmu.Launcher.MacOS.AppKitNative;
namespace SharpEmu.Launcher.MacOS;

public sealed partial class MacLauncher
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ActionCallback(nint self, nint selector, nint sender);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CountCallback(nint self, nint selector, nint table);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ValueCallback(nint self, nint selector, nint table, nint column, nint row);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetValueCallback(nint self, nint selector, nint table, nint value, nint column, nint row);
    private static nint RegisterTargetClass()
    {
        var cls = AllocateClass(Class("NSObject"), "SharpEmuNativeLauncherTarget", 0);
        if (cls == 0) throw new InvalidOperationException("Could not register the native launcher target.");
        Add(cls, "action:", new ActionCallback((_, _, sender) => Dispatch(owner => { if (owner.actions.TryGetValue(sender, out var action)) action(); })), "v@:@");
        Add(cls, "tick:", new ActionCallback((_, _, _) => Dispatch(owner => owner.Tick())), "v@:@");
        Add(cls, "launch:", new ActionCallback((_, _, sender) => Dispatch(owner => { if (sender == owner.table) owner.LaunchSelected(); })), "v@:@");
        Add(cls, "editList:", new ActionCallback((_, _, _) => Dispatch(owner => owner.EditList())), "v@:@");
        Add(cls, "controlTextDidChange:", new ActionCallback((_, _, notification) => Dispatch(owner => { if (Send(notification, Selector("object")) == owner.search) owner.Filter(); })), "v@:@");
        Add(cls, "tableViewSelectionDidChange:", new ActionCallback((_, _, notification) => Dispatch(owner => { if (Send(notification, Selector("object")) == owner.table) owner.Select(); })), "v@:@");
        Add(cls, "numberOfRowsInTableView:", new CountCallback((_, _, table) => active is { closed: false } owner ? table == owner.editorTable ? owner.editorRows.Count : table == owner.table ? owner.visible.Count : 0 : 0), "q@:@");
        Add(cls, "tableView:objectValueForTableColumn:row:", new ValueCallback((_, _, table, column, row) =>
        {
            try
            {
                if (active is not { closed: false } owner) return 0;
                var id = Text(Send(column, Selector("identifier"))); var index = (int)row;
                if (table == owner.editorTable && index >= 0 && index < owner.editorRows.Count) return String(id == "setting" ? owner.editorRows[index].Label : owner.editorRows[index].Value);
                if (table != owner.table || index < 0 || index >= owner.visible.Count) return 0;
                var game = owner.visible[index]; return String(id switch { "title" => game.TitleId ?? string.Empty, "version" => game.Version ?? string.Empty, _ => game.Name });
            }
            catch { return 0; } // never unwind managed exceptions into Cocoa
        }), "@@:@@q");
        Add(cls, "tableView:setObjectValue:forTableColumn:row:", new SetValueCallback((_, _, table, value, column, row) => Dispatch(owner =>
        {
            var index = (int)row;
            if (table == owner.editorTable && index >= 0 && index < owner.editorRows.Count && Text(Send(column, Selector("identifier"))) == "value") owner.editorRows[index].Value = Text(value);
        })), "v@:@@@q");
        Add(cls, "windowShouldClose:", new CountCallback((_, _, window) =>
        {
            try
            {
                if (active is not { } owner) return 1;
                if (window == owner.modalWindow) { owner.modalAccepted = false; Send(owner.app, Selector("stopModal")); return 1; }
                if (window != owner.window || !owner.child.IsRunning) return 1;
                var alert = New("NSAlert");
                try
                {
                    Send(alert, Selector("setMessageText:"), String("Stop the running game and quit?"));
                    Send(alert, Selector("addButtonWithTitle:"), String("Stop and Quit")); Send(alert, Selector("addButtonWithTitle:"), String("Cancel"));
                    if (Send(alert, Selector("runModal")) != 1000) return 0;
                    owner.child.Stop(); return 1;
                }
                finally { Release(alert); }
            }
            catch { return 0; }
        }), "c@:@");
        Add(cls, "applicationShouldTerminate:", new CountCallback((_, _, _) =>
        {
            Dispatch(owner => Send(owner.window, Selector("performClose:"), 0));
            return 0; // Own Quit through window close/stop, preserving managed disposal.
        }), "q@:@");
        Add(cls, "windowWillClose:", new ActionCallback((_, _, notification) => Dispatch(owner =>
        {
            if (Send(notification, Selector("object")) != owner.window) return;
            owner.child.Stop(); owner.settings.Save(); Send(owner.app, Selector("stop:"), 0);
        })), "v@:@");
        RegisterClass(cls); return cls;
    }
    private static void Add(nint cls, string name, Delegate callback, string encoding)
    {
        callbacks.Add(callback); // Runtime class IMPs live for the process, not one window.
        if (!AddMethod(cls, Selector(name), Marshal.GetFunctionPointerForDelegate(callback), encoding)) throw new InvalidOperationException($"Cannot install native action {name}.");
    }
    private static void Dispatch(Action<MacLauncher> action)
    {
        if (active is not { closed: false } owner) return;
        try { action(owner); } catch (Exception exception) { owner.Append(exception.Message); }
    }
}
