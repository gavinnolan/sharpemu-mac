// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>Cross-platform input state supplied by the SDL game window.</summary>
public static class HostWindowInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> PressedKeys = new();
    private static bool _focused;
    private static bool _gamepadConnected;
    private static string? _gamepadName;
    private static HostGamepadState _gamepadState;
    private static IHostGamepadOutput? _gamepadOutput;
    private static long _version;
    private static readonly WindowInputSource Source = new();

    /// <summary>Moves on every change of the state below; a reader polls it to find changes.</summary>
    public static long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// The whole state at once, for the two-process split's GPU process, which owns the window and
    /// sends it to the guest process: the pressed keys (as many as fit), whether the window has
    /// focus, and the gamepad. Returns how many keys it wrote.
    /// </summary>
    public static int CopyState(Span<int> keys, out bool focused, out HostGamepadState gamepad, out string? gamepadName)
    {
        lock (Gate)
        {
            focused = _focused;
            gamepad = _gamepadConnected ? _gamepadState : default;
            gamepadName = _gamepadConnected ? _gamepadName : null;
            var count = 0;
            foreach (var key in PressedKeys)
            {
                if (count == keys.Length)
                {
                    break;
                }

                keys[count++] = key;
            }

            return count;
        }
    }

    /// <summary>Replaces the pressed keys with <paramref name="keys"/> (the guest process's side of <see cref="CopyState"/>).</summary>
    public static void SetKeys(ReadOnlySpan<int> keys)
    {
        lock (Gate)
        {
            PressedKeys.Clear();
            foreach (var key in keys)
            {
                PressedKeys.Add(key);
            }

            _version++;
        }
    }

    public static void Connect(IHostGamepadOutput? gamepadOutput = null)
    {
        lock (Gate)
        {
            _focused = true;
            _gamepadOutput = gamepadOutput;
            PressedKeys.Clear();
            _version++;
        }

        HostWindowInputSource.Set(Source);
    }

    public static void Disconnect()
    {
        lock (Gate)
        {
            _focused = false;
            _gamepadConnected = false;
            _gamepadName = null;
            _gamepadState = default;
            _gamepadOutput = null;
            PressedKeys.Clear();
            _version++;
        }

        HostWindowInputSource.Clear(Source);
    }

    public static void SetFocused(bool focused)
    {
        lock (Gate)
        {
            _focused = focused;
            if (!focused)
            {
                PressedKeys.Clear();
            }

            _version++;
        }
    }

    public static void SetKey(int virtualKey, bool down)
    {
        lock (Gate)
        {
            if (down)
            {
                PressedKeys.Add(virtualKey);
            }
            else
            {
                PressedKeys.Remove(virtualKey);
            }

            _version++;
        }
    }

    public static void SetGamepad(string? name, HostGamepadState state)
    {
        lock (Gate)
        {
            _gamepadConnected = state.Connected;
            _gamepadName = name;
            _gamepadState = state;
            _version++;
        }
    }

    public static void ClearGamepad()
    {
        lock (Gate)
        {
            _gamepadConnected = false;
            _gamepadName = null;
            _gamepadState = default;
            _version++;
        }
    }

    private sealed class WindowInputSource : IHostWindowInputSource
    {
        public bool HasKeyboardFocus
        {
            get
            {
                lock (Gate)
                {
                    return _focused;
                }
            }
        }

        public bool IsKeyDown(int virtualKey)
        {
            lock (Gate)
            {
                return PressedKeys.Contains(virtualKey);
            }
        }

        public int GetGamepadStates(Span<HostGamepadState> destination)
        {
            lock (Gate)
            {
                if (!_gamepadConnected || destination.IsEmpty)
                {
                    return 0;
                }

                destination[0] = _gamepadState;
                return 1;
            }
        }

        public string? DescribeConnectedGamepad()
        {
            lock (Gate)
            {
                return _gamepadConnected ? _gamepadName ?? "SDL gamepad" : null;
            }
        }

        public void SetRumble(byte largeMotor, byte smallMotor)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetRumble(largeMotor, smallMotor);
        }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetTriggerRumble(leftTrigger, rightTrigger);
        }

        public void SetAdaptiveTriggerEffect(
            HostAdaptiveTriggerEffect? leftTrigger,
            HostAdaptiveTriggerEffect? rightTrigger)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetAdaptiveTriggerEffect(leftTrigger, rightTrigger);
        }

        public void SetLightbar(byte red, byte green, byte blue)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetLightbar(red, green, blue);
        }

        public void ResetLightbar()
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.ResetLightbar();
        }
    }
}

public interface IHostGamepadOutput
{
    void SetRumble(byte largeMotor, byte smallMotor);

    void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger);

    void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger);

    void SetLightbar(byte red, byte green, byte blue);

    void ResetLightbar();
}
