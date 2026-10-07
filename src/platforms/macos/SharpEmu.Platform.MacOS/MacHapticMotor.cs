// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.Libs.Platform.MacOS;

/// <summary>
/// One continuous-rumble channel, built on GCDeviceHaptics plus CoreHaptics.
/// </summary>
/// <remarks>
/// GameController exposes a DualSense's motors as haptic localities rather than
/// as SDL's "large/small motor" byte pair, so each motor becomes its own
/// CHHapticEngine created with <c>-createEngineWithLocality:</c>. Inside the
/// engine a single looping continuous haptic event stands in for the motor, and
/// SDL's intensity bytes become
/// <c>CHHapticDynamicParameterIDHapticIntensityControl</c> updates sent to an
/// advanced pattern player. Intensity 0 stops the player, which is what SDL's
/// effect expiry did.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacHapticMotor : IDisposable
{
    // SDL's rumble calls carried a 5s duration and were refreshed by the guest;
    // a looping player plus an explicit stop at intensity 0 reproduces that
    // without needing a timer.
    private const double EventDuration = 30.0;
    private const double TimeImmediate = 0.0;

    private readonly string _name;
    private nint _engine;
    private nint _player;
    private bool _engineStarted;
    private bool _playing;
    private float _intensity;
    private bool _disposed;

    private MacHapticMotor(string name, nint engine, nint player)
    {
        _name = name;
        _engine = engine;
        _player = player;
    }

    /// <summary>
    /// Builds a motor for one GCHapticsLocality name, or returns null when the
    /// controller has no haptics, the locality is unsupported, or CoreHaptics
    /// refuses the pattern. Callers treat null as "this feature is unavailable"
    /// and silently skip it, matching how SDL no-opped rumble on pads without it.
    /// </summary>
    public static MacHapticMotor? TryCreate(nint haptics, string localityConstant, string name)
    {
        if (haptics == 0)
        {
            return null;
        }

        nint engine = 0;
        nint pattern = 0;
        nint player = 0;
        var created = false;
        try
        {
            var locality = MacCoreHaptics.Locality(localityConstant);
            if (locality == 0)
            {
                return null;
            }

            engine = MacObjC.Send(
                haptics,
                MacObjC.Selector("createEngineWithLocality:"),
                locality);
            if (engine == 0)
            {
                return null;
            }

            engine = MacObjC.Send(engine, MacObjC.Selector("retain"));
            pattern = MacCoreHaptics.TryCreateContinuousPattern(EventDuration);
            if (pattern == 0)
            {
                return null;
            }

            nint error = 0;
            player = MacObjC.SendObjectError(
                engine,
                MacObjC.Selector("createAdvancedPlayerWithPattern:error:"),
                pattern,
                ref error);
            if (player == 0)
            {
                Console.Error.WriteLine(
                    $"[PAD][WARN] CoreHaptics player for {name} unavailable: {MacObjC.DescribeError(error)}");
                return null;
            }

            player = MacObjC.Send(player, MacObjC.Selector("retain"));
            MacObjC.SendVoidBool(player, MacObjC.Selector("setLoopEnabled:"), true);
            created = true;
            return new MacHapticMotor(name, engine, player);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PAD][WARN] CoreHaptics setup for {name} failed: {exception.Message}");
            return null;
        }
        finally
        {
            MacObjC.Release(pattern);
            if (!created)
            {
                MacObjC.Release(player);
                MacObjC.Release(engine);
            }
        }
    }

    /// <summary>Sets the motor level from an SDL-style 0..255 intensity byte.</summary>
    public void SetIntensity(byte intensity)
    {
        if (_disposed)
        {
            return;
        }

        var level = intensity / 255f;
        if (Math.Abs(level - _intensity) < 1f / 512f && (level > 0f) == _playing)
        {
            return;
        }

        _intensity = level;
        try
        {
            if (level <= 0f)
            {
                Stop();
                return;
            }

            if (!Start())
            {
                return;
            }

            var parameter = MacCoreHaptics.TryCreateIntensityParameter(level);
            if (parameter == 0)
            {
                return;
            }

            try
            {
                Span<nint> items = [parameter];
                nint error = 0;
                MacObjC.SendBoolParametersAtTime(
                    _player,
                    MacObjC.Selector("sendParameters:atTime:error:"),
                    MacObjC.ArrayWith(items),
                    TimeImmediate,
                    ref error);
            }
            finally
            {
                MacObjC.Release(parameter);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PAD][WARN] CoreHaptics update for {_name} failed: {exception.Message}");
        }
    }

    private bool Start()
    {
        nint error = 0;
        if (!_engineStarted)
        {
            if (!MacObjC.SendBoolError(_engine, MacObjC.Selector("startAndReturnError:"), ref error))
            {
                Console.Error.WriteLine(
                    $"[PAD][WARN] CoreHaptics engine for {_name} failed to start: {MacObjC.DescribeError(error)}");
                return false;
            }

            _engineStarted = true;
        }

        if (_playing)
        {
            return true;
        }

        error = 0;
        if (!MacObjC.SendBoolTimeError(
                _player,
                MacObjC.Selector("startAtTime:error:"),
                TimeImmediate,
                ref error))
        {
            Console.Error.WriteLine(
                $"[PAD][WARN] CoreHaptics player for {_name} failed to start: {MacObjC.DescribeError(error)}");
            return false;
        }

        _playing = true;
        return true;
    }

    private void Stop()
    {
        if (!_playing)
        {
            return;
        }

        _playing = false;
        nint error = 0;
        MacObjC.SendBoolTimeError(_player, MacObjC.Selector("stopAtTime:error:"), TimeImmediate, ref error);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Stop();
            if (_engineStarted)
            {
                MacObjC.SendVoid(_engine, MacObjC.Selector("stopWithCompletionHandler:"), 0);
                _engineStarted = false;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PAD][WARN] CoreHaptics teardown for {_name} failed: {exception.Message}");
        }

        MacObjC.Release(_player);
        _player = 0;
        MacObjC.Release(_engine);
        _engine = 0;
    }
}

/// <summary>
/// CoreHaptics and GCHapticsLocality constants, which are exported NSString*
/// symbols rather than enum values, plus the pattern construction shared by every
/// <see cref="MacHapticMotor"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacCoreHaptics
{
    private static readonly object Gate = new();
    private static nint _coreHaptics;
    private static nint _gameController;
    private static bool _loaded;
    private static bool _unavailableLogged;

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            NativeLibrary.TryLoad(MacObjC.CoreHapticsFramework, out _coreHaptics);
            NativeLibrary.TryLoad(MacObjC.GameControllerFramework, out _gameController);
        }
    }

    /// <summary>Resolves a GCHapticsLocality* NSString constant by symbol name.</summary>
    public static nint Locality(string symbol)
    {
        EnsureLoaded();
        return MacObjC.ReadPointerConstant(_gameController, symbol);
    }

    /// <summary>
    /// A single looping continuous haptic event at full intensity; the live level
    /// is applied as a dynamic intensity parameter instead of rebuilding patterns.
    /// Returns 0 (and logs once) when CoreHaptics is unavailable.
    /// </summary>
    public static nint TryCreateContinuousPattern(double duration)
    {
        EnsureLoaded();
        var eventType = MacObjC.ReadPointerConstant(_coreHaptics, "CHHapticEventTypeHapticContinuous");
        var intensityId = MacObjC.ReadPointerConstant(_coreHaptics, "CHHapticEventParameterIDHapticIntensity");
        var sharpnessId = MacObjC.ReadPointerConstant(_coreHaptics, "CHHapticEventParameterIDHapticSharpness");
        var eventClass = MacObjC.Class("CHHapticEvent");
        var parameterClass = MacObjC.Class("CHHapticEventParameter");
        var patternClass = MacObjC.Class("CHHapticPattern");
        if (eventType == 0 || intensityId == 0 || sharpnessId == 0 ||
            eventClass == 0 || parameterClass == 0 || patternClass == 0)
        {
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                Console.Error.WriteLine("[PAD][WARN] CoreHaptics is unavailable; controller rumble is disabled.");
            }

            return 0;
        }

        var intensity = NewEventParameter(parameterClass, intensityId, 1f);
        var sharpness = NewEventParameter(parameterClass, sharpnessId, 0.5f);
        try
        {
            if (intensity == 0 || sharpness == 0)
            {
                return 0;
            }

            Span<nint> parameters = [intensity, sharpness];
            var hapticEvent = MacObjC.SendInitHapticEvent(
                MacObjC.Send(eventClass, MacObjC.Selector("alloc")),
                MacObjC.Selector("initWithEventType:parameters:relativeTime:duration:"),
                eventType,
                MacObjC.ArrayWith(parameters),
                0.0,
                duration);
            if (hapticEvent == 0)
            {
                return 0;
            }

            try
            {
                Span<nint> events = [hapticEvent];
                nint error = 0;
                var pattern = MacObjC.SendObjectObjectError(
                    MacObjC.Send(patternClass, MacObjC.Selector("alloc")),
                    MacObjC.Selector("initWithEvents:parameters:error:"),
                    MacObjC.ArrayWith(events),
                    MacObjC.EmptyArray(),
                    ref error);
                if (pattern == 0)
                {
                    Console.Error.WriteLine(
                        $"[PAD][WARN] CoreHaptics pattern creation failed: {MacObjC.DescribeError(error)}");
                }

                return pattern;
            }
            finally
            {
                MacObjC.Release(hapticEvent);
            }
        }
        finally
        {
            MacObjC.Release(intensity);
            MacObjC.Release(sharpness);
        }
    }

    public static nint TryCreateIntensityParameter(float level)
    {
        EnsureLoaded();
        var parameterId = MacObjC.ReadPointerConstant(
            _coreHaptics,
            "CHHapticDynamicParameterIDHapticIntensityControl");
        var parameterClass = MacObjC.Class("CHHapticDynamicParameter");
        if (parameterId == 0 || parameterClass == 0)
        {
            return 0;
        }

        return MacObjC.SendInitWithParameterIdValueTime(
            MacObjC.Send(parameterClass, MacObjC.Selector("alloc")),
            MacObjC.Selector("initWithParameterID:value:relativeTime:"),
            parameterId,
            Math.Clamp(level, 0f, 1f),
            0.0);
    }

    private static nint NewEventParameter(nint parameterClass, nint parameterId, float value) =>
        MacObjC.SendInitWithParameterIdValue(
            MacObjC.Send(parameterClass, MacObjC.Selector("alloc")),
            MacObjC.Selector("initWithParameterID:value:"),
            parameterId,
            value);
}
