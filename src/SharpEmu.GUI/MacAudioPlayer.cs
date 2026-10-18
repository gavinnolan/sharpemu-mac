// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

/// <summary>
/// Minimal AVFoundation AVAudioPlayer wrapper (P/Invoke through the Objective-C
/// runtime) that plays a WAV image held in memory. Used by the GUI's title
/// preview music and trophy chimes on macOS.
/// </summary>
internal sealed class MacAudioPlayer : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<bool> Available = new(LoadFramework);

    private static nint _classPlayer;
    private static nint _classData;
    private static nint _selAlloc;
    private static nint _selInitWithBytes;
    private static nint _selInitWithData;
    private static nint _selRelease;
    private static nint _selPlay;
    private static nint _selPause;
    private static nint _selStop;
    private static nint _selSetVolume;
    private static nint _selSetLoops;
    private static nint _selIsPlaying;
    private static nint _selCurrentTime;
    private static nint _selPrepare;

    private nint _player;
    private nint _data;

    private MacAudioPlayer(nint player, nint data)
    {
        _player = player;
        _data = data;
    }

    /// <summary>Creates a player over a WAV image, or null if it cannot be decoded.</summary>
    public static MacAudioPlayer? Create(byte[] wav, bool loop, float volume)
    {
        if (!Available.Value)
        {
            return null;
        }

        var pool = objc_autoreleasePoolPush();
        try
        {
            nint data;
            unsafe
            {
                fixed (byte* p = wav)
                {
                    data = Send(Send(_classData, _selAlloc), _selInitWithBytes, (nint)p, wav.Length);
                }
            }

            if (data == 0)
            {
                return null;
            }

            var player = Send(Send(_classPlayer, _selAlloc), _selInitWithData, data, 0);
            if (player == 0)
            {
                SendVoid(data, _selRelease);
                return null;
            }

            SendLong(player, _selSetLoops, loop ? -1 : 0);
            SendFloat(player, _selSetVolume, volume);
            SendBool(player, _selPrepare);
            return new MacAudioPlayer(player, data);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public bool IsPlaying => WithPool(() => _player != 0 && SendBool(_player, _selIsPlaying) != 0);

    public double CurrentTime => WithPool(() => _player != 0 ? SendDouble(_player, _selCurrentTime) : 0.0);

    public bool Play() => WithPool(() => _player != 0 && SendBool(_player, _selPlay) != 0);

    public void Pause() => WithPool(() => { if (_player != 0) { SendVoid(_player, _selPause); } });

    public void SetVolume(float volume) => WithPool(() => { if (_player != 0) { SendFloat(_player, _selSetVolume, Math.Clamp(volume, 0f, 1f)); } });

    public void Dispose()
    {
        WithPool(() =>
        {
            if (_player != 0)
            {
                SendVoid(_player, _selStop);
                SendVoid(_player, _selRelease);
                _player = 0;
            }

            if (_data != 0)
            {
                SendVoid(_data, _selRelease);
                _data = 0;
            }

        });
    }

    private static T WithPool<T>(Func<T> body)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            return body();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static void WithPool(Action body)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            body();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static bool LoadFramework()
    {
        try
        {
            _ = NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
            _classPlayer = objc_getClass("AVAudioPlayer");
            _classData = objc_getClass("NSData");
            if (_classPlayer == 0 || _classData == 0)
            {
                return false;
            }

            _selAlloc = sel_registerName("alloc");
            _selInitWithBytes = sel_registerName("initWithBytes:length:");
            _selInitWithData = sel_registerName("initWithData:error:");
            _selRelease = sel_registerName("release");
            _selPlay = sel_registerName("play");
            _selPause = sel_registerName("pause");
            _selStop = sel_registerName("stop");
            _selSetVolume = sel_registerName("setVolume:");
            _selSetLoops = sel_registerName("setNumberOfLoops:");
            _selIsPlaying = sel_registerName("isPlaying");
            _selCurrentTime = sel_registerName("currentTime");
            _selPrepare = sel_registerName("prepareToPlay");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint a, nint b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendLong(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendFloat(nint receiver, nint selector, float value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(nint receiver, nint selector);
}
