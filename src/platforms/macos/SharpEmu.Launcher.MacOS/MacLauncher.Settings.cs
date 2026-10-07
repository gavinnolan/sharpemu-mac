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
    private void EditGlobalSettings() => EditFields(settings, null, () => { settings = ApplicationSettings.NormalizeFromJson(JsonSerializer.Serialize(settings)); settings.Save(); strings.Load(settings.Language); WatchAndRefresh(); });
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
        finally { Release(selection); }
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
    private void EditFields(object model, string[]? fields, Action saved)
    {
        if (modalWindow != 0) return;
        var before = owned.Count;
        var properties = model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && (fields is null || fields.Contains(property.Name))).ToArray();
        editorRows = properties.Select(property => new EditorRow(property, Regex.Replace(property.Name, "([a-z])([A-Z])", "$1 $2")) { Value = DisplayValue(property.GetValue(model)) }).ToList();
        modalAccepted = false;
        try
        {
            modalWindow = Own(InitWindow(Send(Class("NSWindow"), Selector("alloc")), Selector("initWithContentRect:styleMask:backing:defer:"), new(0, 0, 820, 660), 3, 2, 0));
            Send(modalWindow, Selector("setReleasedWhenClosed:"), 0); Send(modalWindow, Selector("setTitle:"), String(model is PerGameSettings ? "Game Settings" : "Settings"));
            Send(modalWindow, Selector("setDelegate:"), target);
            var content = Send(modalWindow, Selector("contentView"));
            Label(content, new(18, 598, 784, 48), "Edit a value by double-clicking. Lists use one entry per line.\n[inherit] keeps global values; [empty] is an explicit empty list.");
            editorTable = CreateTable(content, new(18, 64, 784, 524), [("setting", "Setting", 320d), ("value", "Value", 430d)], true);
            Send(editorTable, Selector("setDoubleAction:"), Selector("editList:"));
            Button(content, "Cancel", new(574, 16, 104, 32), () => { modalAccepted = false; Send(app, Selector("stopModal")); });
            Button(content, "Save", new(688, 16, 104, 32), () =>
            {
                // End in-progress cell editing before reading its owned value.
                Send(modalWindow, Selector("makeFirstResponder:"), 0);
                try
                {
                    var values = editorRows.Select(row => ParseValue(row.Property.PropertyType, row.Value)).ToArray();
                    for (var index = 0; index < values.Length; index++) editorRows[index].Property.SetValue(model, values[index]);
                    modalAccepted = true; Send(app, Selector("stopModal"));
                }
                catch (Exception exception) { Alert("Invalid Setting", exception.Message); }
            });
            Send(editorTable, Selector("reloadData")); Send(modalWindow, Selector("center"));
            Send(app, Selector("runModalForWindow:"), modalWindow);
            if (modalAccepted) saved();
        }
        finally
        {
            if (editorTable != 0) { Send(editorTable, Selector("setDelegate:"), 0); Send(editorTable, Selector("setDataSource:"), 0); }
            if (modalWindow != 0) { Send(modalWindow, Selector("setDelegate:"), 0); Send(modalWindow, Selector("orderOut:"), 0); }
            foreach (var handle in owned.Skip(before).Reverse()) { actions.Remove(handle); Release(handle); }
            owned.RemoveRange(before, owned.Count - before); modalWindow = editorTable = 0; editorRows = [];
        }
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
        if (row < 0 || row >= editorRows.Count || editorRows[row].Property.PropertyType != typeof(List<string>)) return;
        var item = editorRows[row];
        var alert = New("NSAlert"); var scroll = New("NSScrollView", new(0, 0, 560, 260)); var text = New("NSTextView", new(0, 0, 540, 260));
        try
        {
            Send(alert, Selector("setMessageText:"), String(item.Label));
            Send(alert, Selector("setInformativeText:"), String("One entry per line. [inherit] uses global values; [empty] removes all entries for this override."));
            Send(text, Selector("setString:"), String(item.Value)); Send(scroll, Selector("setHasVerticalScroller:"), 1); Send(scroll, Selector("setDocumentView:"), text);
            Send(alert, Selector("setAccessoryView:"), scroll); Send(alert, Selector("addButtonWithTitle:"), String("Apply")); Send(alert, Selector("addButtonWithTitle:"), String("Cancel"));
            if (Send(alert, Selector("runModal")) == 1000) { item.Value = Text(Send(text, Selector("string"))); Send(editorTable, Selector("reloadData")); }
        }
        finally { Release(alert); Release(scroll); Release(text); }
    }
}
