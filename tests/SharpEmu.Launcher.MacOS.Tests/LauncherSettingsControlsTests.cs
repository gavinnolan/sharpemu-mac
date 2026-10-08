// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Text.Json;
using SharpEmu.Application;
using SharpEmu.Launcher.MacOS;
using Xunit;
namespace SharpEmu.Launcher.MacOS.Tests;

public sealed class LauncherSettingsControlsTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static object Presentation(PropertyInfo property, string value) =>
        typeof(MacLauncher).GetMethod("SettingsPresentation", PrivateStatic)!.Invoke(null, [property, value])!;
    private static T Field<T>(object presentation, string name) => (T)presentation.GetType().GetProperty(name)!.GetValue(presentation)!;

    [Theory]
    [InlineData(typeof(ApplicationSettings), nameof(ApplicationSettings.VSync), "true", "Switch", false)]
    [InlineData(typeof(PerGameSettings), nameof(PerGameSettings.VSync), "[inherit]", "Switch", true)]
    [InlineData(typeof(ApplicationSettings), nameof(ApplicationSettings.VSync), "invalid", "Text", false)]
    [InlineData(typeof(PerGameSettings), nameof(PerGameSettings.ImportTraceLimit), "[inherit]", "Integer", true)]
    [InlineData(typeof(ApplicationSettings), nameof(ApplicationSettings.RenderResolutionScale), "0.5", "Number", false)]
    [InlineData(typeof(PerGameSettings), nameof(PerGameSettings.EnvironmentToggles), "[empty]", "List", true)]
    public void ActualSchemaSelectsTypedControlWithoutCoercingInvalidOrInheritedValues(Type schema, string name,
        string value, string kind, bool inherited)
    {
        var presentation = Presentation(schema.GetProperty(name)!, value);
        Assert.Equal(kind, presentation.GetType().GetProperty("Kind")!.GetValue(presentation)!.ToString());
        Assert.Equal(inherited, Field<bool>(presentation, "Inherited"));
    }

    [Fact]
    public void ChoiceMenuKeepsUnknownConfiguredValuesAndPerGameInheritance()
    {
        var property = typeof(PerGameSettings).GetProperty(nameof(PerGameSettings.WindowMode))!;
        var presentation = Presentation(property, "FutureWindowMode");
        Assert.Equal(new[] { "FutureWindowMode", "[inherit]", "Windowed", "Borderless", "Exclusive" }, Field<string[]>(presentation, "Choices"));
        // Editing menu state cannot rewrite the saved model or future presentations.
        Field<string[]>(presentation, "Choices")[0] = "mutated";
        Assert.Equal("FutureWindowMode", Field<string[]>(Presentation(property, "FutureWindowMode"), "Choices")[0]);
        Assert.Equal(new[] { "Warn", "Trace", "Debug", "Info", "Warning", "Error", "Critical" },
            Field<string[]>(Presentation(typeof(ApplicationSettings).GetProperty(nameof(ApplicationSettings.LogLevel))!, "Warn"), "Choices"));
    }

    [Fact]
    public void MixedSwitchMeansInheritanceOnlyForPerGameValues()
    {
        var convert = typeof(MacLauncher).GetMethod("SettingsSwitchValue", PrivateStatic)!;
        Assert.Equal("false", convert.Invoke(null, [(nint)0, false]));
        Assert.Equal("true", convert.Invoke(null, [(nint)1, false]));
        Assert.Equal("[inherit]", convert.Invoke(null, [(nint)(-1), true]));
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => convert.Invoke(null, [(nint)(-1), false])).InnerException);
    }

    [Theory]
    [InlineData(typeof(ApplicationSettings))]
    [InlineData(typeof(PerGameSettings))]
    public void EveryWritableSchemaFieldRemainsEditableAndAnUnchangedSessionRoundTrips(Type schema)
    {
        var model = Activator.CreateInstance(schema)!;
        if (model is PerGameSettings game)
        {
            game.WindowMode = "FutureWindowMode"; game.CustomEnvironmentVariables = ["CASE=value", "case=other"];
            game.EnvironmentToggles = []; game.DisplayIndex = 7; game.VSync = false;
        }
        var before = JsonSerializer.Serialize(model, schema);
        var sessionType = typeof(MacLauncher).GetNestedType("SettingsEditSession", BindingFlags.NonPublic)!;
        var persisted = 0;
        var session = Activator.CreateInstance(sessionType, [model, null, (Action)(() => persisted++)])!;
        var rows = ((System.Collections.IEnumerable)sessionType.GetProperty("Rows")!.GetValue(session)!).Cast<object>().ToArray();
        Assert.Equal(schema.GetProperties().Where(property => property.CanWrite).Select(property => property.Name).Order(),
            rows.Select(row => ((PropertyInfo)row.GetType().GetProperty("Property")!.GetValue(row)!).Name).Order());
        foreach (var row in rows)
        {
            var property = (PropertyInfo)row.GetType().GetProperty("Property")!.GetValue(row)!;
            Assert.NotNull(Presentation(property, (string)row.GetType().GetField("Value")!.GetValue(row)!));
        }
        sessionType.GetMethod("Save")!.Invoke(session, null);
        Assert.Equal(before, JsonSerializer.Serialize(model, schema)); Assert.Equal(1, persisted);
    }

    [Fact]
    public void GlobalPersistenceFailureKeepsEditorModelAndRetryPersistsLaterEdits()
    {
        var model = new ApplicationSettings(); ApplicationSettings? accepted = null;
        var attempts = new List<string>();
        var callback = (Action)typeof(MacLauncher).GetMethod("GlobalSettingsSaveAction", PrivateStatic)!.Invoke(null,
            [model, (Action<ApplicationSettings>)(normalized =>
            {
                attempts.Add(normalized.LogLevel);
                if (attempts.Count == 1) throw new IOException("synthetic persistence failure");
            }), (Action<ApplicationSettings>)(normalized => accepted = normalized)])!;
        var type = typeof(MacLauncher).GetNestedType("SettingsEditSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(type, [model, null, callback])!;
        var rows = ((System.Collections.IEnumerable)type.GetProperty("Rows")!.GetValue(session)!).Cast<object>();
        var log = rows.Single(row => ((PropertyInfo)row.GetType().GetProperty("Property")!.GetValue(row)!).Name == nameof(model.LogLevel));
        log.GetType().GetField("Value")!.SetValue(log, "Debug");
        Assert.IsType<IOException>(Assert.Throws<TargetInvocationException>(() => type.GetMethod("Save")!.Invoke(session, null)).InnerException);
        Assert.Null(accepted);
        log.GetType().GetField("Value")!.SetValue(log, "Critical");
        type.GetMethod("Save")!.Invoke(session, null);
        Assert.Equal(new[] { "Debug", "Critical" }, attempts);
        Assert.Equal("Critical", accepted!.LogLevel); Assert.NotSame(model, accepted);
    }
}
