// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SharpEmu.Application;
using SharpEmu.Logging;
using static SharpEmu.Launcher.MacOS.AppKitNative;
namespace SharpEmu.Launcher.MacOS;

public sealed partial class MacLauncher
{
    private nint modalWindow, editorTable;
    private List<EditorRow> editorRows = [];
    private bool modalAccepted;
    private sealed record EditorRow(PropertyInfo Property, string Label) { public string Value = string.Empty; }
    private sealed class EnvironmentEditor { public List<string> EnvironmentVariables { get; set; } = []; }
    private void BuildMenu()
    {
        var menu = Own(New("NSMenu"));
        var application = Menu(menu, "SharpEmu");
        MenuAction(application, "About SharpEmu", () => Alert("SharpEmu", "Native macOS launcher · Metal graphics · CoreText fonts\nGuest x64 execution uses Rosetta on Apple Silicon."));
        MenuAction(application, "Settings…", EditGlobalSettings, ",");
        MenuAction(application, "Check for Updates…", () => _ = CheckUpdatesAsync(true));
        MenuAction(application, "Quit SharpEmu", RequestQuit, "q");
        var file = Menu(menu, "File");
        MenuAction(file, "Open Executable…", OpenExecutable, "o"); MenuAction(file, "Add Game Folder…", AddFolder);
        MenuAction(file, "Launch Selected", LaunchSelected, "r"); MenuAction(file, "Stop Owned Game", () => child.Stop());
        var edit = Menu(menu, "Edit");
        StandardMenuAction(edit, "Copy", "copy:", "c"); StandardMenuAction(edit, "Paste", "paste:", "v");
        StandardMenuAction(edit, "Cut", "cut:", "x"); StandardMenuAction(edit, "Select All", "selectAll:", "a");
        var view = Menu(menu, "View");
        MenuAction(view, "Console", ShowConsole); MenuAction(view, "Refresh Library", Refresh);
        var help = Menu(menu, "Help");
        MenuAction(help, "Project Source", () => OpenUrl(ProjectLinks.RepositoryUrl));
        MenuAction(help, "Release Downloads", () => OpenUrl(ProjectLinks.ReleasesUrl));
        MenuAction(help, "Discord", () => OpenUrl("https://discord.com/invite/zdTuUU9Uwn"));
        Send(app, Selector("setMainMenu:"), menu);
    }
    private nint Menu(nint parent, string title)
    {
        var item = Own(New("NSMenuItem")); Send(item, Selector("setTitle:"), String(title));
        var menu = Own(Send(Send(Class("NSMenu"), Selector("alloc")), Selector("initWithTitle:"), String(title)));
        Send(item, Selector("setSubmenu:"), menu); Send(parent, Selector("addItem:"), item); return menu;
    }
    private void MenuAction(nint menu, string title, Action action, string key = "")
    {
        var item = Own(Send(Send(Class("NSMenuItem"), Selector("alloc")), Selector("initWithTitle:action:keyEquivalent:"), String(title), Selector("action:"), String(key)));
        Send(item, Selector("setTarget:"), target); Send(menu, Selector("addItem:"), item); actions.Add(item, action);
    }
    private void StandardMenuAction(nint menu, string title, string selector, string key)
    {
        var item = Own(Send(Send(Class("NSMenuItem"), Selector("alloc")), Selector("initWithTitle:action:keyEquivalent:"), String(title), Selector(selector), String(key)));
        // nil target deliberately uses Cocoa's focused responder chain.
        Send(menu, Selector("addItem:"), item);
    }
    private static void OpenUrl(string url) => Send(Send(Class("NSWorkspace"), Selector("sharedWorkspace")), Selector("openURL:"), Send(Class("NSURL"), Selector("URLWithString:"), String(url)));
    private string? Choose(bool folder)
    {
        var panel = Send(Class("NSOpenPanel"), Selector("openPanel")); Send(panel, Selector("retain"));
        try
        {
            Send(panel, Selector("setCanChooseDirectories:"), folder ? 1 : 0); Send(panel, Selector("setCanChooseFiles:"), folder ? 0 : 1);
            Send(panel, Selector("setAllowsMultipleSelection:"), 0);
            Send(panel, Selector("setTitle:"), String(folder ? "Choose Game Folder" : "Choose Executable (eboot.bin)"));
            if (Send(panel, Selector("runModal")) != 1) return null;
            return Text(Send(Send(panel, Selector("URL")), Selector("path")));
        }
        finally { Release(panel); ContinueQuit(); }
    }
    private void AddFolder()
    {
        var folder = Choose(true); if (string.IsNullOrWhiteSpace(folder)) return;
        if (!settings.GameFolders.Contains(folder, GameLibraryPath.Comparer)) settings.GameFolders.Add(folder);
        settings.Save(); WatchAndRefresh();
    }
    private void OpenExecutable()
    {
        if (closeAfterChildExit || child.IsRunning) { Alert("Game is running", "Stop the owned game before launching another executable."); return; }
        var file = Choose(false); if (string.IsNullOrWhiteSpace(file)) return;
        var meta = GameCatalog.TryReadParamJson(file);
        Launch(new(meta.Title ?? GameCatalog.GameNameFor(file), meta.TitleId, meta.Version, Path.GetFullPath(file), 0,
            GameCatalog.FindCoverFor(file), GameCatalog.FindBackgroundFor(file)));
    }
    private void LaunchSelected() { if (selected is { } game) Launch(game); }
    private void Launch(CatalogGame game, IReadOnlyList<string>? oneShot = null)
    {
        if (closeAfterChildExit || child.IsRunning) { Alert("Game is running", "Stop the owned game before launching another executable."); return; }
        var executable = FindEmulator();
        if (executable is null) { Alert("Emulator executable not found", "Choose Emulator Path in Settings, or launch the installed SharpEmu application."); return; }
        var perGame = PerGameSettings.Load(game.TitleId);
        var plan = LauncherLaunchPlan.Create(game.Path, settings, perGame, GlobalCustomEnvironmentSettings.Load(), oneShot);
        var effective = EffectiveLaunchSettings.Resolve(settings, perGame);
        lines.Clear(); fileLog?.Dispose(); fileLog = null;
        if (effective.LogToFile)
        {
            try
            {
                var path = ResolveLogPath(game.TitleId, executable); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                fileLog = new StreamWriter(path, false) { AutoFlush = true }; Append($"Log file: {path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Append($"Could not create log file: {exception.Message}"); }
        }
        try
        {
            SelectChildSession(child.Start(executable, plan)); SetStatus($"Running: {game.Name}"); Append($"Launching {game.Path}");
            Send(Send(Class("NSDocumentController"), Selector("sharedDocumentController")), Selector("noteNewRecentDocumentURL:"), Send(Class("NSURL"), Selector("fileURLWithPath:"), String(game.Path)));
        }
        catch (Exception exception) { fileLog?.Dispose(); fileLog = null; Alert("Could not launch emulator", exception.Message); }
    }
    private string? FindEmulator()
    {
        var candidates = new List<string>(); if (!string.IsNullOrWhiteSpace(settings.EmulatorPath)) candidates.Add(settings.EmulatorPath);
        if (Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("SharpEmu", StringComparison.OrdinalIgnoreCase)) candidates.Add(self);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "SharpEmu")); candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "SharpEmu"));
        return candidates.FirstOrDefault(File.Exists) is { } found ? Path.GetFullPath(found) : null;
    }
    private string ResolveLogPath(string? titleId, string executable)
    {
        var id = string.IsNullOrWhiteSpace(titleId) ? "UNKNOWN" : titleId;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(settings.LogFilePath))
        {
            if (settings.OverrideLogFile) return settings.LogFilePath;
            foreach (var invalid in Path.GetInvalidFileNameChars()) id = id.Replace(invalid.ToString(), string.Empty, StringComparison.Ordinal);
            var path = settings.LogFilePath; var name = $"{Path.GetFileNameWithoutExtension(path)}-{id}-{stamp}{Path.GetExtension(path)}";
            return string.IsNullOrEmpty(Path.GetDirectoryName(path)) ? name : Path.Combine(Path.GetDirectoryName(path)!, name);
        }
        foreach (var invalid in Path.GetInvalidFileNameChars()) id = id.Replace(invalid, '_');
        return Path.Combine(SharpEmuPaths.ResolveDataRoot(Path.GetDirectoryName(executable)!), "user", "logs", $"{id}-{stamp}.log");
    }
    private async Task CheckUpdatesAsync(bool manual)
    {
        try
        {
            var sha = typeof(MacLauncher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SharpEmu.BuildSha")?.Value;
            var update = await Updater.CheckAsync(sha).ConfigureAwait(false);
            Post(() => { if (update is not null) Alert("Update available", $"{update.TagName}\nDownload the signed application from Release Downloads in Help. In-place bundle replacement is not supported."); else if (manual) Alert("Updates", "No newer verified release was found for this build."); });
        }
        catch (Exception exception) { Post(() => { Append($"Update check failed: {exception.Message}"); if (manual) Alert("Update check failed", exception.Message); }); }
    }
    private void ShowConsole()
    {
        if (consoleWindow == 0)
        {
            consoleWindow = Own(InitWindow(Send(Class("NSWindow"), Selector("alloc")), Selector("initWithContentRect:styleMask:backing:defer:"), new(0, 0, settings.ConsoleWindowWidth, settings.ConsoleWindowHeight), 15, 2, 0));
            Send(consoleWindow, Selector("setReleasedWhenClosed:"), 0); Send(consoleWindow, Selector("setTitle:"), String("SharpEmu Console"));
            var parent = Send(consoleWindow, Selector("contentView"));
            var scroll = Own(New("NSScrollView", new(12, 54, settings.ConsoleWindowWidth - 24, settings.ConsoleWindowHeight - 66)));
            Send(scroll, Selector("setHasVerticalScroller:"), 1);
            consoleText = Own(New("NSTextView", new(0, 0, settings.ConsoleWindowWidth - 44, settings.ConsoleWindowHeight - 66)));
            Send(consoleText, Selector("setEditable:"), 0); Send(scroll, Selector("setDocumentView:"), consoleText); Send(parent, Selector("addSubview:"), scroll);
            Button(parent, "Clear", new(12, 12, 100, 30), () => { lines.Clear(); Send(consoleText, Selector("setString:"), String(string.Empty)); });
            Button(parent, "Copy All", new(120, 12, 100, 30), () => Copy(string.Join('\n', lines)));
            Send(consoleWindow, Selector("center"));
        }
        Send(consoleText, Selector("setString:"), String(string.Join('\n', lines))); Send(consoleWindow, Selector("makeKeyAndOrderFront:"), 0);
    }
    private void Alert(string title, string text)
    {
        var alert = New("NSAlert"); try { Send(alert, Selector("setMessageText:"), String(title)); Send(alert, Selector("setInformativeText:"), String(text)); Send(alert, Selector("runModal")); } finally { Release(alert); ContinueQuit(); }
    }
}
