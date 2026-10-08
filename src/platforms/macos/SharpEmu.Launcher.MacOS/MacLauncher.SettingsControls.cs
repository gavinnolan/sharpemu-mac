// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Application;
using static SharpEmu.Launcher.MacOS.AppKitNative;
namespace SharpEmu.Launcher.MacOS;

public sealed partial class MacLauncher
{
    private nint editorValue, editorChoice, editorSwitch, editorHint, editorList;
    private bool updatingSettingsControls;
    private enum SettingsControlKind { Text, Integer, Number, Switch, List }
    private sealed record SettingsFieldPresentation(SettingsControlKind Kind, string Hint, string[] Choices, bool Inherited);

    // These are suggestions for the existing string schema, not a new enum schema.
    // Keep configured values that are not in a built-in list (including future values).
    private static SettingsFieldPresentation SettingsPresentation(PropertyInfo property, string value)
    {
        var actual = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var inherited = property.DeclaringType == typeof(PerGameSettings);
        var kind = actual == typeof(List<string>) ? SettingsControlKind.List :
            actual == typeof(bool) && (bool.TryParse(value, out _) || inherited && value == "[inherit]") ? SettingsControlKind.Switch :
            actual == typeof(int) ? SettingsControlKind.Integer : actual == typeof(double) ? SettingsControlKind.Number : SettingsControlKind.Text;
        string[] choices = property.Name switch
        {
            nameof(ApplicationSettings.LogLevel) => ["Trace", "Debug", "Info", "Warning", "Error", "Critical"],
            nameof(ApplicationSettings.WindowMode) => ["Windowed", "Borderless", "Exclusive"],
            nameof(ApplicationSettings.ScalingMode) => ["Fit", "Cover", "Stretch", "Integer"],
            nameof(ApplicationSettings.HdrMode) => ["Auto", "On", "Off"],
            nameof(ApplicationSettings.OverlayMode) => ["Full", "Minimal", "TitleBar"],
            nameof(ApplicationSettings.OverlayCorner) => ["TopLeft", "TopRight", "BottomRight", "BottomLeft"],
            nameof(ApplicationSettings.BinkPlaybackMode) => ["Host", "Guest", "Skip"],
            nameof(ApplicationSettings.LibraryLayout) => ["Carousel", "Grid"],
            nameof(ApplicationSettings.ConsoleType) => ["PS5", "PS4"],
            _ => [],
        };
        if (choices.Length != 0)
        {
            if (inherited) choices = ["[inherit]", .. choices];
            if (!choices.Contains(value, StringComparer.Ordinal)) choices = [value, .. choices];
        }
        var hint = kind switch
        {
            SettingsControlKind.Switch => inherited ? "Off / on / mixed (inherit global)." : "Off / on.",
            SettingsControlKind.Integer => "Whole number. Validation runs before Save.",
            SettingsControlKind.Number => "Finite number with a decimal point. Validation runs before Save.",
            SettingsControlKind.List => "Edit one entry per line; [empty] keeps an explicit empty list.",
            _ => choices.Length != 0 ? "Choose a value, or enter a custom value in the text field." : "Text value.",
        };
        if (inherited && kind != SettingsControlKind.Switch) hint += " [inherit] uses global settings.";
        return new(kind, hint, choices, inherited);
    }
    private static string SettingsSwitchValue(nint state, bool inherited) => state switch
    {
        0 => "false", 1 => "true", -1 when inherited => "[inherit]",
        _ => throw new InvalidOperationException("Invalid settings switch state."),
    };
    private void BuildSettingsControls(nint content)
    {
        editorHint = Label(content, new(18, 94, 784, 28), "Select a setting.");
        editorValue = Own(New("NSTextField", new(18, 60, 474, 30)));
        Send(editorValue, Selector("setDelegate:"), target); Send(content, Selector("addSubview:"), editorValue);
        editorChoice = Own(InitPopUpButton(Send(Class("NSPopUpButton"), Selector("alloc")), Selector("initWithFrame:pullsDown:"), new(506, 60, 286, 30), 0));
        Send(editorChoice, Selector("setTarget:"), target); Send(editorChoice, Selector("setAction:"), Selector("action:"));
        Send(content, Selector("addSubview:"), editorChoice);
        actions.Add(editorChoice, () => SetSettingsRowValue(Text(Send(editorChoice, Selector("titleOfSelectedItem")))));
        editorSwitch = Own(New("NSButton", new(18, 60, 774, 30)));
        Send(editorSwitch, Selector("setButtonType:"), 3); // NSButtonTypeSwitch
        Send(editorSwitch, Selector("setTarget:"), target); Send(editorSwitch, Selector("setAction:"), Selector("action:"));
        Send(content, Selector("addSubview:"), editorSwitch);
        actions.Add(editorSwitch, () =>
        {
            if (SelectedSettingsRow() is { } row)
                SetSettingsRowValue(SettingsSwitchValue(Send(editorSwitch, Selector("state")), SettingsPresentation(row.Property, row.Value).Inherited));
        });
        editorList = Button(content, "Edit Entries…", new(18, 60, 170, 30), () => EditListRow((int)Send(editorTable, Selector("selectedRow"))));
    }
    private EditorRow? SelectedSettingsRow()
    {
        var index = editorTable == 0 ? -1 : (int)Send(editorTable, Selector("selectedRow"));
        return index >= 0 && index < editorRows.Count ? editorRows[index] : null;
    }
    private void UpdateSettingsControls()
    {
        if (editorHint == 0 || updatingSettingsControls) return;
        updatingSettingsControls = true;
        try
        {
            foreach (var control in new[] { editorValue, editorChoice, editorSwitch, editorList }) Send(control, Selector("setHidden:"), 1);
            var row = SelectedSettingsRow();
            if (row is null) { Send(editorHint, Selector("setStringValue:"), String("Select a setting.")); return; }
            var presentation = SettingsPresentation(row.Property, row.Value);
            Send(editorHint, Selector("setStringValue:"), String(row.Label + ": " + presentation.Hint));
            if (presentation.Kind == SettingsControlKind.Switch)
            {
                Send(editorSwitch, Selector("setTitle:"), String(row.Label));
                Send(editorSwitch, Selector("setAllowsMixedState:"), presentation.Inherited ? 1 : 0);
                Send(editorSwitch, Selector("setState:"), row.Value == "[inherit]" ? -1 : bool.Parse(row.Value) ? 1 : 0);
                Send(editorSwitch, Selector("setHidden:"), 0);
            }
            else if (presentation.Kind == SettingsControlKind.List) Send(editorList, Selector("setHidden:"), 0);
            else
            {
                Send(editorValue, Selector("setStringValue:"), String(row.Value));
                Send(editorValue, Selector("setPlaceholderString:"), String(presentation.Kind switch
                {
                    SettingsControlKind.Integer => "Whole number", SettingsControlKind.Number => "Finite number", _ => "Value",
                }));
                Send(editorValue, Selector("setHidden:"), 0);
                if (presentation.Choices.Length != 0)
                {
                    Send(editorChoice, Selector("removeAllItems"));
                    foreach (var choice in presentation.Choices) Send(editorChoice, Selector("addItemWithTitle:"), String(choice));
                    Send(editorChoice, Selector("selectItemWithTitle:"), String(row.Value));
                    Send(editorChoice, Selector("setHidden:"), 0);
                }
            }
        }
        finally { updatingSettingsControls = false; }
    }
    private void SettingsTextChanged()
    {
        if (updatingSettingsControls || SelectedSettingsRow() is not { } row) return;
        row.Value = Text(Send(editorValue, Selector("stringValue")));
        // Leave incomplete numeric input editable. The session validates all fields
        // before mutating the model or calling persistence.
        // reloadData may send a selection notification. Do not replace the
        // field editor's text/caret while the user is still typing.
        updatingSettingsControls = true;
        try { Send(editorTable, Selector("reloadData")); }
        finally { updatingSettingsControls = false; }
    }
    private void SetSettingsRowValue(string value)
    {
        if (SelectedSettingsRow() is not { } row) return;
        row.Value = value; Send(editorTable, Selector("reloadData")); UpdateSettingsControls();
    }
}
