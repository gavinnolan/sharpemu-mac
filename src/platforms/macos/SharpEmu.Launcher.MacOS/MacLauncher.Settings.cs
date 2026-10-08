// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpEmu.Application;
using static SharpEmu.Launcher.MacOS.AppKitNative;
namespace SharpEmu.Launcher.MacOS;

public sealed partial class MacLauncher
{
    private void EditGlobalSettings()
    {
        var edited = settings;
        EditFields(edited, null, GlobalSettingsSaveAction(edited, normalized => normalized.Save(), normalized =>
        {
            settings = normalized; strings.Load(settings.Language); WatchAndRefresh();
        }));
    }
    private static Action GlobalSettingsSaveAction(ApplicationSettings edited, Action<ApplicationSettings> persist,
        Action<ApplicationSettings> accepted) => () =>
    {
        // The open editor retains its original model even if persistence fails.
        // Publish only after saving, and normalize that same edited model on retry.
        var normalized = ApplicationSettings.NormalizeFromJson(JsonSerializer.Serialize(edited));
        persist(normalized); accepted(normalized);
    };
    private void EditGameSettings()
    {
        if (selected?.TitleId is not { Length: > 0 } title) { Alert("Game Settings", "Select a game with a title ID first."); return; }
        var game = PerGameSettings.Load(title) ?? new();
        EditFields(game, null, () => { game.RemoveInheritedValues(settings); game.Save(title); });
    }
    private void EditCustomEnvironment()
    {
        var selection = New("NSAlert"); nint result;
        try
        {
            Send(selection, Selector("setMessageText:"), String("Custom Environment"));
            Send(selection, Selector("setInformativeText:"), String("Choose saved global values, saved game overrides, or one launch only. Later sources override earlier names."));
            foreach (var title in new[] { "Global", "This Game", "Launch Once", "Cancel" }) Send(selection, Selector("addButtonWithTitle:"), String(title));
            result = Send(selection, Selector("runModal"));
        }
        finally { Release(selection); ContinueQuit(); }
        if (result == 1000)
        {
            var values = new EnvironmentEditor { EnvironmentVariables = [.. GlobalCustomEnvironmentSettings.Load()] };
            EditFields(values, null, () => GlobalCustomEnvironmentSettings.Save(values.EnvironmentVariables));
        }
        else if (result == 1001 && selected?.TitleId is { Length: > 0 } id)
        {
            var game = PerGameSettings.Load(id) ?? new();
            EditFields(game, [nameof(game.CustomEnvironmentVariables)], () => game.Save(id));
        }
        else if (result == 1002 && selected is { } selectedGame)
        {
            var values = new EnvironmentEditor(); EditFields(values, null, () => Launch(selectedGame, values.EnvironmentVariables));
        }
    }
    // One modeless editor at a time. Cocoa's application menu remains active;
    // no nested runModalForWindow loop owns application shutdown.
    private void EditFields(object model, string[]? fields, Action saved)
    {
        if (editorWindow != 0) { Send(editorWindow, Selector("makeKeyAndOrderFront:"), 0); return; }
        var before = owned.Count;
        editorSession = new(model, fields, saved);
        editorRows = editorSession.Rows;
        try
        {
            editorWindow = Own(InitWindow(Send(Class("NSWindow"), Selector("alloc")), Selector("initWithContentRect:styleMask:backing:defer:"), new(0, 0, 820, 660), 3, 2, 0));
            Send(editorWindow, Selector("setReleasedWhenClosed:"), 0); Send(editorWindow, Selector("setTitle:"), String(model is PerGameSettings ? "Game Settings" : "Settings"));
            Send(editorWindow, Selector("setDelegate:"), target);
            var content = Send(editorWindow, Selector("contentView"));
            Label(content, new(18, 598, 784, 48), "Select a setting to edit below, or double-click its value. Lists use one entry per line.\n[inherit] keeps global values; [empty] is an explicit empty list.");
            editorTable = CreateTable(content, new(18, 130, 784, 458), [("setting", "Setting", 320d), ("value", "Value", 430d)], true);
            Send(editorTable, Selector("setDoubleAction:"), Selector("editList:"));
            BuildSettingsControls(content);
            Button(content, "Cancel", new(574, 16, 104, 32), CloseEditor);
            Button(content, "Save", new(688, 16, 104, 32), () =>
            {
                // Commit in-progress cell editing before parsing the detached rows.
                Send(editorWindow, Selector("makeFirstResponder:"), 0);
                try { editorSession!.Save(); CloseEditor(); }
                catch (Exception exception) { Alert("Invalid Setting", exception.Message); }
            });
            editorOwned = owned.Skip(before).ToArray();
            Send(editorTable, Selector("reloadData")); UpdateSettingsControls(); Send(editorWindow, Selector("center"));
            Send(editorWindow, Selector("makeKeyAndOrderFront:"), 0);
        }
        catch
        {
            editorOwned = owned.Skip(before).ToArray();
            CloseEditor(); throw;
        }
    }
    private void CloseEditor()
    {
        if (editorSession is null || !editorSession.Close()) return;
        if (editorTable != 0) { Send(editorTable, Selector("setDelegate:"), 0); Send(editorTable, Selector("setDataSource:"), 0); Send(editorTable, Selector("setTarget:"), 0); }
        if (editorValue != 0) Send(editorValue, Selector("setDelegate:"), 0);
        if (editorWindow != 0) { Send(editorWindow, Selector("setDelegate:"), 0); Send(editorWindow, Selector("orderOut:"), 0); Send(editorWindow, Selector("close")); }
        var handles = editorOwned;
        foreach (var handle in handles) if (actions.Remove(handle)) Send(handle, Selector("setTarget:"), 0);
        editorWindow = editorTable = 0; editorRows = []; editorOwned = []; editorSession = null;
        editorValue = editorChoice = editorSwitch = editorHint = editorList = 0;
        // A Cancel/Save sender or delegate is still on Cocoa's callback stack.
        // Release after returning; Dispose also owns these handles if the loop ends first.
        Post(() => { foreach (var handle in handles.Reverse()) if (owned.Remove(handle)) Release(handle); });
    }
    private sealed class SettingsEditSession
    {
        private readonly object model;
        private readonly Action saved;
        private bool closed;
        public List<EditorRow> Rows { get; }
        public SettingsEditSession(object model, string[]? fields, Action saved)
        {
            this.model = model; this.saved = saved;
            Rows = model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanWrite && (fields is null || fields.Contains(property.Name)))
                .Select(property => new EditorRow(property, Regex.Replace(property.Name, "([a-z])([A-Z])", "$1 $2")) { Value = DisplayValue(property.GetValue(model)) }).ToList();
        }
        public void Save()
        {
            if (closed) throw new InvalidOperationException("The settings editor is closed.");
            var values = Rows.Select(row => ParseValue(row.Property.PropertyType, row.Value)).ToArray();
            for (var index = 0; index < values.Length; index++) Rows[index].Property.SetValue(model, values[index]);
            saved();
        }
        public bool Close() { if (closed) return false; closed = true; return true; }
    }
    private static string DisplayValue(object? value) => value switch
    {
        null => "[inherit]", List<string> list => list.Count == 0 ? "[empty]" : string.Join('\n', list),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? string.Empty,
    };
    private static object? ParseValue(Type type, string value)
    {
        if (value == "[inherit]" && (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null)) return null;
        if (type == typeof(List<string>))
        {
            if (value == "[empty]" || value.Length == 0) return new List<string>();
            return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual == typeof(string)) return value;
        if (actual == typeof(bool) && bool.TryParse(value, out var boolean)) return boolean;
        if (actual == typeof(int) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (actual == typeof(double) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        throw new FormatException($"Expected {actual.Name}; use true/false for switches and a number for numeric settings.");
    }
    private void EditList()
    {
        var row = (int)Send(editorTable, Selector("clickedRow"));
        EditListRow(row);
    }
    private void EditListRow(int row)
    {
        if (row < 0 || row >= editorRows.Count || editorRows[row].Property.PropertyType != typeof(List<string>)) return;
        var item = editorRows[row];
        var alert = New("NSAlert"); var scroll = New("NSScrollView", new(0, 0, 560, 260)); var text = New("NSTextView", new(0, 0, 540, 260));
        try
        {
            Send(alert, Selector("setMessageText:"), String(item.Label));
            Send(alert, Selector("setInformativeText:"), String("One entry per line. [inherit] uses global values; [empty] removes all entries for this override."));
            Send(text, Selector("setString:"), String(item.Value)); Send(scroll, Selector("setHasVerticalScroller:"), 1); Send(scroll, Selector("setDocumentView:"), text);
            Send(alert, Selector("setAccessoryView:"), scroll); Send(alert, Selector("addButtonWithTitle:"), String("Apply")); Send(alert, Selector("addButtonWithTitle:"), String("Cancel"));
            if (Send(alert, Selector("runModal")) == 1000) { item.Value = Text(Send(text, Selector("string"))); Send(editorTable, Selector("reloadData")); UpdateSettingsControls(); }
        }
        finally { Release(alert); Release(scroll); Release(text); ContinueQuit(); }
    }
}
