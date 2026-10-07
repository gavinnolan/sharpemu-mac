// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
namespace SharpEmu.Application;

/// <summary>An owned snapshot of arguments and environment overrides for one isolated child.</summary>
public sealed record LauncherLaunchPlan(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)
{
    public static LauncherLaunchPlan Create(string executable, ApplicationSettings global, PerGameSettings? perGame,
        IEnumerable<string>? globalCustom = null, IEnumerable<string>? oneShotCustom = null)
    {
        var effective = EffectiveLaunchSettings.Resolve(global, perGame);
        var args = new List<string>
        {
            "--cpu-engine=native", $"--log-level={effective.LogLevel}",
            $"--console={global.ConsoleType.ToLowerInvariant()}", $"--console-language={global.ConsoleLanguage}",
        };
        if (effective.StrictDynlibResolution) args.Add("--strict");
        if (effective.ImportTraceLimit > 0) args.Add($"--trace-imports={effective.ImportTraceLimit}");
        args.AddRange([
            $"--window-mode={effective.WindowMode.ToLowerInvariant()}", $"--resolution={effective.Resolution}",
            $"--display={effective.DisplayIndex}", $"--refresh-rate={effective.RefreshRate}",
            $"--scaling={effective.ScalingMode.ToLowerInvariant()}", $"--vsync={(effective.VSync ? "on" : "off")}",
            $"--hdr={effective.HdrMode.ToLowerInvariant()}", $"--overlay={(effective.OverlayEnabled ? "on" : "off")}",
            $"--overlay-mode={effective.OverlayMode.ToLowerInvariant()}",
            $"--overlay-corner={effective.OverlayCorner.ToLowerInvariant()}", Path.GetFullPath(executable),
        ]);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in effective.EnvironmentToggles)
        {
            var separator = entry.IndexOf('=');
            var name = separator < 0 ? entry : entry[..separator];
            var value = separator < 0 ? "1" : entry[(separator + 1)..];
            if (name.StartsWith("SHARPEMU_", StringComparison.OrdinalIgnoreCase) &&
                name.Length > "SHARPEMU_".Length && value.Length != 0 &&
                !name.Equals("SHARPEMU_DEFAULT_PROFILE", StringComparison.OrdinalIgnoreCase)) environment[name] = value;
        }
        environment["SHARPEMU_DEFAULT_PROFILE"] = ApplicationSettings.NormalizeDefaultProfile(global.DefaultProfile);
        environment[StrictComputeSettings.VariableName] = StrictComputeSettings.GetLaunchValue(effective.EnvironmentToggles);
        // Preserve the existing launcher's bare-name check and custom overrides last.
        environment["SHARPEMU_DISABLE_IMPORT_LOOP_GUARD"] = effective.EnvironmentToggles.Contains("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD") ? "1" : "0";
        environment["SHARPEMU_BINK_HOST_PLAYBACK"] = global.BinkPlaybackMode switch { "Guest" => "guest", "Skip" => "skip", _ => "native" };
        environment["SHARPEMU_RENDER_SCALE"] = global.RenderResolutionScale.ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var entry in CustomEnvironmentVariables.Merge(globalCustom, perGame?.CustomEnvironmentVariables, oneShotCustom))
            if (CustomEnvironmentVariables.TryParseEntry(entry, out var name, out var value)) environment[name] = value;
        return new(Array.AsReadOnly(args.ToArray()), new ReadOnlyDictionary<string, string>(environment));
    }
}
