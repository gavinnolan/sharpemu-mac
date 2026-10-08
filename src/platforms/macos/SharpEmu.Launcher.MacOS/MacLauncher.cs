// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Application;
using SharpEmu.Logging;
using static SharpEmu.Launcher.MacOS.AppKitNative;
namespace SharpEmu.Launcher.MacOS;

/// <summary>Native no-argument frontend; owns its Cocoa loop and one isolated emulator child.</summary>
public sealed partial class MacLauncher : IDisposable
{
    private static MacLauncher? active;
    private static readonly List<Delegate> callbacks = [];
    private static readonly Lazy<nint> targetClass = new(RegisterTargetClass);
    private readonly List<nint> owned = [];
    private readonly BoundedLauncherNotifications pending = new();
    private readonly Dictionary<nint, Action> actions = [];
    private readonly GameLibraryWatcher watcher = new();
    private readonly OwnedEmulatorProcess child = new();
    private readonly LauncherLocalization strings = LauncherLocalization.Instance;
    private ApplicationSettings settings = ApplicationSettings.Load();
    private List<CatalogGame> games = [], visible = [];
    private readonly Queue<string> lines = new();
    private nint app, target, window, table, search, cover, status, timer, consoleWindow, consoleText;
    private StreamWriter? fileLog;
    private bool closed, scanRunning, closeAfterChildExit;
    private long activeChildSession;
    private readonly QuitSequence quit = new();
    private CatalogGame? selected;
    private const int MaximumConsoleLines = 5000;

    public static int Run()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("AppKit launcher requires macOS.");
        if (pthread_main_np() != 1) throw new InvalidOperationException("AppKit launcher must run on the OS main thread.");
        if (active is not null) throw new InvalidOperationException("AppKit launcher is already active.");
        using var pool = new Pool();
        using var launcher = new MacLauncher();
        active = launcher;
        try { launcher.Build(); Send(launcher.app, Selector("run")); return 0; }
        finally { active = null; }
    }
    private MacLauncher()
    {
        strings.Load(settings.Language);
        watcher.RefreshRequested += (_, _) => Post(Refresh);
        child.Output += (session, line, error) => pending.PostOutput(session, line);
        child.CleanupFailed += (session, message) => Post(() => DispatchChildCleanupFailure(session, () =>
        {
            Append($"Owned emulator cleanup: {message}");
            SetStatus("Cleanup failed; owned emulator retained. Retry Stop before launching or quitting.");
        }));
        child.Exited += (session, code) => Post(() => DispatchChildNotification(session, () => { Append($"Emulator exited ({code})."); fileLog?.Dispose(); fileLog = null; SetStatus(strings.Get("Launch.Launch"));
            if (closeAfterChildExit) { closeAfterChildExit = false; RequestQuit(); }
        }));
    }
    // A child may finish and free its slot before its UI notifications are drained.
    // Only the selected session may mutate the current console/log/status.
    private void SelectChildSession(long session) => activeChildSession = session;
    private void DispatchChildNotification(long session, Action action)
    {
        if (!closed && session == activeChildSession) action();
    }
    private void DispatchChildCleanupFailure(long session, Action diagnostic) => DispatchChildNotification(session, () =>
    {
        closeAfterChildExit = false; // a failed owned stop cannot silently hold Quit pending
        diagnostic();
    });
    private sealed class QuitSequence
    {
        private bool requested;
        public int Request(bool modal) { requested = true; return Continue(modal); }
        public int Continue(bool modal)
        {
            if (!requested) return 0;
            if (modal) return 1; // unwind one Cocoa modal loop, keeping the quit request
            requested = false; return 2; // consume before main close, which may be canceled
        }
    }
    private void RequestQuit() => ApplyQuitAction(quit.Request(Send(app, Selector("modalWindow")) != 0));
    private void ContinueQuit() => ApplyQuitAction(quit.Continue(Send(app, Selector("modalWindow")) != 0));
    private void ApplyQuitAction(int action)
    {
        if (action == 1) Send(app, Selector("stopModal"));
        else if (action == 2) Send(window, Selector("performClose:"), 0);
    }
    private string L(string key, string fallback) { var value = strings.Get(key); return value == key ? fallback : value; }
    private nint Own(nint obj) { if (obj == 0) throw new InvalidOperationException("AppKit object creation failed."); owned.Add(obj); return obj; }
    private void Post(Action action) => pending.Post(action);
    private void Tick()
    {
        if (closed) return;
        ContinueQuit();
        var dropped = pending.TakeDroppedCount();
        if (dropped != 0) Append($"Launcher output backlog: discarded {dropped} older lines.");
        for (var count = 0; count < 128 && pending.TryTake(out var job); count++)
            try
            {
                if (job!.Action is { } action) action();
                else DispatchChildNotification(job.Session, () => Append(job.Line!));
            }
            catch (Exception exception) { Append(exception.Message); }
    }
    private void Build()
    {
        app = Send(Class("NSApplication"), Selector("sharedApplication"));
        Send(app, Selector("setActivationPolicy:"), 0);
        target = Own(NewFromClass(targetClass.Value));
        Send(app, Selector("setDelegate:"), target);
        window = Own(InitWindow(Send(Class("NSWindow"), Selector("alloc")), Selector("initWithContentRect:styleMask:backing:defer:"), new(0, 0, 1180, 760), 15, 2, 0));
        Send(window, Selector("setReleasedWhenClosed:"), 0);
        Send(window, Selector("setTitle:"), String("SharpEmu"));
        Send(window, Selector("setDelegate:"), target);
        var content = Send(window, Selector("contentView"));
        BuildMenu();
        Button(content, L("Library.AddFolder", "Add Folder…"), new(16, 708, 120, 32), AddFolder);
        Button(content, L("Library.OpenFile", "Open Executable…"), new(142, 708, 150, 32), OpenExecutable);
        Button(content, L("Launch.Launch", "Launch"), new(298, 708, 100, 32), LaunchSelected);
        Button(content, L("Launch.Stop", "Stop"), new(404, 708, 80, 32), () => child.Stop());
        Button(content, L("Page.Options", "Settings…"), new(490, 708, 110, 32), EditGlobalSettings);
        Button(content, "Game Settings…", new(606, 708, 140, 32), EditGameSettings);
        Button(content, "Environment…", new(752, 708, 130, 32), EditCustomEnvironment);
        Button(content, L("Console.Title", "Console"), new(888, 708, 110, 32), ShowConsole);
        search = Own(New("NSSearchField", new(16, 665, 790, 30)));
        Send(search, Selector("setPlaceholderString:"), String(L("Library.SearchWatermark", "Search library")));
        Send(search, Selector("setDelegate:"), target); Send(content, Selector("addSubview:"), search);
        table = CreateTable(content, new(16, 64, 790, 590), [("name", "Game", 460d), ("title", "Title ID", 130d), ("version", "Version", 100d)], false);
        cover = Own(New("NSImageView", new(830, 246, 328, 402))); Send(content, Selector("addSubview:"), cover);
        Button(content, "Open Game Folder", new(830, 199, 160, 32), RevealSelected);
        Button(content, "Copy Path", new(996, 199, 160, 32), () => Copy(selected?.Path));
        Button(content, "Copy Title ID", new(830, 159, 160, 32), () => Copy(selected?.TitleId));
        Button(content, "Hide from Library", new(996, 159, 160, 32), HideSelected);
        Button(content, "Refresh", new(830, 119, 160, 32), Refresh);
        Button(content, "Manage Folders…", new(996, 119, 160, 32), () => EditGlobalSettingsFields([nameof(settings.GameFolders), nameof(settings.ExcludedGames)]));
        status = Label(content, new(16, 20, 1142, 28), L("Launch.Idle", "Idle"));
        Send(app, Selector("finishLaunching")); Send(window, Selector("center"));
        Send(window, Selector("makeKeyAndOrderFront:"), 0); Send(app, Selector("activateIgnoringOtherApps:"), 1);
        // Scheduling a timer requires the double ABI; ownership remains local.
        timer = CreateTimer(.05, target, Selector("tick:")); Own(Send(timer, Selector("retain")));
        Send(Send(Class("NSRunLoop"), Selector("mainRunLoop")), Selector("addTimer:forMode:"), timer, CommonRunLoopMode);
        games = GameCatalogCache.Load(settings.GameFolders); Filter(); WatchAndRefresh();
        if (settings.CheckForUpdatesOnStartup) _ = CheckUpdatesAsync(false);
    }
    private nint Button(nint parent, string title, Rect rect, Action action)
    {
        var button = Own(New("NSButton", rect)); Send(button, Selector("setTitle:"), String(title));
        Send(button, Selector("setBezelStyle:"), 1); Send(button, Selector("setTarget:"), target);
        Send(button, Selector("setAction:"), Selector("action:")); Send(parent, Selector("addSubview:"), button);
        actions.Add(button, action); return button;
    }
    private nint Label(nint parent, Rect rect, string text)
    {
        var field = Own(New("NSTextField", rect)); Send(field, Selector("setEditable:"), 0);
        Send(field, Selector("setSelectable:"), 1); Send(field, Selector("setBezeled:"), 0); Send(field, Selector("setDrawsBackground:"), 0);
        Send(field, Selector("setStringValue:"), String(text)); Send(parent, Selector("addSubview:"), field); return field;
    }
    private nint CreateTable(nint parent, Rect rect, (string Id, string Title, double Width)[] columns, bool editable)
    {
        var scroll = Own(New("NSScrollView", rect)); Send(scroll, Selector("setHasVerticalScroller:"), 1);
        var result = Own(New("NSTableView", new(0, 0, rect.Width, rect.Height)));
        foreach (var item in columns)
        {
            var column = Own(Send(Send(Class("NSTableColumn"), Selector("alloc")), Selector("initWithIdentifier:"), String(item.Id)));
            Send(column, Selector("setTitle:"), String(item.Title)); SetDouble(column, Selector("setWidth:"), item.Width);
            Send(column, Selector("setEditable:"), editable && item.Id == "value" ? 1 : 0);
            Send(result, Selector("addTableColumn:"), column);
        }
        Send(result, Selector("setDataSource:"), target); Send(result, Selector("setDelegate:"), target);
        Send(result, Selector("setTarget:"), target); Send(result, Selector("setDoubleAction:"), Selector("launch:"));
        Send(scroll, Selector("setDocumentView:"), result); Send(parent, Selector("addSubview:"), scroll); return result;
    }
    private void WatchAndRefresh() { watcher.Watch(settings.GameFolders); Refresh(); }
    private void Refresh()
    {
        if (scanRunning || closed) return;
        scanRunning = true; SetStatus("Scanning library…");
        var folders = settings.GameFolders.ToArray(); var excluded = new HashSet<string>(settings.ExcludedGames, GameLibraryPath.Comparer);
        _ = Task.Run(() => GameCatalog.ScanFolders(folders, excluded)).ContinueWith(task => Post(() =>
        {
            scanRunning = false;
            if (task.IsCompletedSuccessfully) { games = task.Result; GameCatalogCache.Save(folders, games); Filter(); SetStatus($"{games.Count} games"); }
            else SetStatus("Library scan failed.");
        }), TaskScheduler.Default);
    }
    private void Filter()
    {
        var query = Text(Send(search, Selector("stringValue")));
        visible = games.Where(game => game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            game.TitleId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToList();
        var selectedIndex = PreservedSelectionIndex(visible, selected?.Path);
        Send(table, Selector("reloadData"));
        if (selectedIndex >= 0)
            Send(table, Selector("selectRowIndexes:byExtendingSelection:"), Send(Class("NSIndexSet"), Selector("indexSetWithIndex:"), selectedIndex), 0);
        else Send(table, Selector("deselectAll:"), 0);
        Select();
    }
    private static int PreservedSelectionIndex(IReadOnlyList<CatalogGame> filtered, string? path)
    {
        if (path is null) return -1;
        for (var index = 0; index < filtered.Count; index++)
            if (GameLibraryPath.Comparer.Equals(filtered[index].Path, path)) return index;
        return -1;
    }
    private void Select()
    {
        var row = (long)Send(table, Selector("selectedRow")); selected = row >= 0 && row < visible.Count ? visible[(int)row] : null;
        Send(cover, Selector("setImage:"), 0);
        if (selected?.CoverPath is { } path)
        {
            var image = Send(Send(Class("NSImage"), Selector("alloc")), Selector("initWithContentsOfFile:"), String(path));
            try { Send(cover, Selector("setImage:"), image); } finally { Release(image); }
        }
        SetStatus(selected is null ? "Select a game" : $"{selected.Name}  {selected.TitleId}  {selected.Version}");
    }
    private void SetStatus(string message) { if (!closed) Send(status, Selector("setStringValue:"), String(message)); }
    private void Append(string line)
    {
        if (closed) return;
        lines.Enqueue(line); while (lines.Count > MaximumConsoleLines) lines.Dequeue();
        try { fileLog?.WriteLine(line); } catch (IOException) { fileLog?.Dispose(); fileLog = null; }
        if (consoleText != 0) Send(consoleText, Selector("setString:"), String(string.Join('\n', lines)));
    }
    private void Copy(string? text)
    {
        if (text is null) return;
        var pasteboard = Send(Class("NSPasteboard"), Selector("generalPasteboard")); Send(pasteboard, Selector("clearContents"));
        Send(pasteboard, Selector("setString:forType:"), String(text), String("public.utf8-plain-text"));
    }
    private void RevealSelected()
    {
        if (selected is null) return;
        var url = Send(Class("NSURL"), Selector("fileURLWithPath:"), String(Path.GetDirectoryName(selected.Path)!));
        Send(Send(Class("NSWorkspace"), Selector("sharedWorkspace")), Selector("openURL:"), url);
    }
    private void HideSelected()
    {
        if (selected is null) return;
        if (!settings.ExcludedGames.Contains(selected.Path, GameLibraryPath.Comparer)) settings.ExcludedGames.Add(selected.Path);
        settings.Save(); Refresh();
    }
    private static nint NewFromClass(nint cls) => Send(Send(cls, Selector("alloc")), Selector("init"));
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint="objc_msgSend")] private static extern nint CreateTimerMessage(nint cls, nint selector, double interval, nint target, nint action, nint userInfo, byte repeats);
    private static nint CreateTimer(double interval, nint target, nint selector) => CreateTimerMessage(Class("NSTimer"), Selector("timerWithTimeInterval:target:selector:userInfo:repeats:"), interval, target, selector, 0, 1);
    private sealed class Pool : IDisposable { private readonly nint pool = New("NSAutoreleasePool"); public void Dispose() => Release(pool); }
    public void Dispose()
    {
        if (closed && owned.Count == 0) return;
        CloseEditor();
        closed = true; pending.Close(); watcher.Dispose(); child.Dispose(); fileLog?.Dispose(); fileLog = null;
        if (timer != 0) Send(timer, Selector("invalidate"));
        if (app != 0) Send(app, Selector("setDelegate:"), 0);
        if (window != 0) Send(window, Selector("setDelegate:"), 0);
        if (table != 0) { Send(table, Selector("setDelegate:"), 0); Send(table, Selector("setDataSource:"), 0); }
        foreach (var handle in actions.Keys) Send(handle, Selector("setTarget:"), 0);
        if (app != 0) Send(app, Selector("setMainMenu:"), 0);
        foreach (var nativeWindow in new[] { window, consoleWindow })
            if (nativeWindow != 0) { Send(nativeWindow, Selector("setDelegate:"), 0); Send(nativeWindow, Selector("orderOut:"), 0); Send(nativeWindow, Selector("close")); }
        foreach (var handle in owned.AsEnumerable().Reverse()) Release(handle);
        owned.Clear(); actions.Clear();
    }
}
