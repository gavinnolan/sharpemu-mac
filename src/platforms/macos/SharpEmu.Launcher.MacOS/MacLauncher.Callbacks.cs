// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using static SharpEmu.Launcher.MacOS.AppKitNative;
namespace SharpEmu.Launcher.MacOS;

public sealed partial class MacLauncher
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ActionCallback(nint self, nint selector, nint sender);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BoolCallback(nint self, nint selector, nint window);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint UnsignedCallback(nint self, nint selector, nint sender);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CountCallback(nint self, nint selector, nint table);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ValueCallback(nint self, nint selector, nint table, nint column, nint row);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetValueCallback(nint self, nint selector, nint table, nint value, nint column, nint row);
    private static string WindowCloseEncoding => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@" : "c@:@";
    private static nint RegisterTargetClass()
    {
        var cls = AllocateClass(Class("NSObject"), "SharpEmuNativeLauncherTarget", 0);
        if (cls == 0) throw new InvalidOperationException("Could not register the native launcher target.");
        Add(cls, "action:", new ActionCallback((_, _, sender) => Dispatch(owner => { if (owner.actions.TryGetValue(sender, out var action)) action(); })), "v@:@");
        Add(cls, "tick:", new ActionCallback((_, _, _) => Dispatch(owner => owner.Tick())), "v@:@");
        Add(cls, "launch:", new ActionCallback((_, _, sender) => Dispatch(owner => { if (sender == owner.table) owner.LaunchSelected(); })), "v@:@");
        Add(cls, "editList:", new ActionCallback((_, _, _) => Dispatch(owner => owner.EditList())), "v@:@");
        Add(cls, "controlTextDidChange:", new ActionCallback((_, _, notification) => Dispatch(owner =>
        {
            var control = Send(notification, Selector("object"));
            if (control == owner.search) owner.Filter();
            else if (control == owner.editorValue) owner.SettingsTextChanged();
        })), "v@:@");
        Add(cls, "tableViewSelectionDidChange:", new ActionCallback((_, _, notification) => Dispatch(owner =>
        {
            var control = Send(notification, Selector("object"));
            if (control == owner.table) owner.Select();
            else if (control == owner.editorTable) owner.UpdateSettingsControls();
        })), "v@:@");
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
            if (table == owner.editorTable && index >= 0 && index < owner.editorRows.Count && Text(Send(column, Selector("identifier"))) == "value")
            { owner.editorRows[index].Value = Text(value); owner.UpdateSettingsControls(); }
        })), "v@:@@@q");
        Add(cls, "windowShouldClose:", new BoolCallback((_, _, window) =>
        {
            try
            {
                if (active is not { } owner) return 1;
                if (window == owner.editorWindow) { owner.CloseEditor(); return 0; }
                if (window == owner.window && Send(owner.app, Selector("modalWindow")) != 0) { owner.RequestQuit(); return 0; }
                if (window != owner.window || !owner.child.IsRunning) return 1;
                if (owner.closeAfterChildExit) return 0;
                var alert = New("NSAlert");
                try
                {
                    Send(alert, Selector("setMessageText:"), String("Stop the running game and quit?"));
                    Send(alert, Selector("addButtonWithTitle:"), String("Stop and Quit")); Send(alert, Selector("addButtonWithTitle:"), String("Cancel"));
                    if (Send(alert, Selector("runModal")) != 1000) return 0;
                    owner.child.Stop(); owner.closeAfterChildExit = true; owner.SetStatus("Stopping owned emulator…"); return 0;
                }
                finally { Release(alert); }
            }
            catch { return 0; }
        }), WindowCloseEncoding);
        Add(cls, "applicationShouldTerminate:", new UnsignedCallback((_, _, _) =>
        {
            Dispatch(owner => owner.RequestQuit());
            return 0; // Own Quit through window close/stop, preserving managed disposal.
        }), "Q@:@");
        Add(cls, "windowWillClose:", new ActionCallback((_, _, notification) => Dispatch(owner =>
        {
            if (Send(notification, Selector("object")) != owner.window) return;
            try { owner.child.Stop(); owner.settings.Save(); }
            finally { StopOuterLoop(owner.app); }
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
