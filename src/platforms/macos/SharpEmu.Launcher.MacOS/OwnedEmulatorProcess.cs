// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text;
using SharpEmu.Application;
namespace SharpEmu.Launcher.MacOS;

/// <summary>One child handle owned by the launcher. Never discovers or stops external emulator processes.</summary>
internal sealed class OwnedEmulatorProcess : IDisposable
{
    private readonly object gate = new();
    private Process? process;
    private bool disposed;
    private bool stopRequested;
    public event Action<string, bool>? Output;
    public event Action<int>? Exited;
    public bool IsRunning { get { lock (gate) return process is not null; } }
    public void Start(string executable, LauncherLaunchPlan plan)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is not null) throw new InvalidOperationException("An owned emulator is already running.");
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in plan.Arguments) info.ArgumentList.Add(argument);
            foreach (var pair in plan.Environment) info.Environment[pair.Key] = pair.Value;
            var child = new Process { StartInfo = info, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, args) => { if (args.Data is { } line) Output?.Invoke(line, false); };
            child.ErrorDataReceived += (_, args) => { if (args.Data is { } line) Output?.Invoke(line, true); };
            child.Exited += (_, _) => OnExited(child);
            var started = false;
            try
            {
                if (!(started = child.Start())) throw new InvalidOperationException("Could not start the owned emulator.");
                process = child; stopRequested = false;
                child.BeginOutputReadLine(); child.BeginErrorReadLine();
            }
            catch
            {
                if (ReferenceEquals(process, child)) process = null;
                if (started) try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                child.Dispose(); throw;
            }
        }
    }
    private void OnExited(Process child)
    {
        // Process exit can precede the redirected readers' final callbacks.
        // This ordinary callback thread waits; the AppKit main loop does not.
        lock (gate) { if (!ReferenceEquals(process, child)) return; }
        try { child.WaitForExit(); } catch (ObjectDisposedException) { return; }
        int code;
        lock (gate)
        {
            if (!ReferenceEquals(process, child)) return;
            code = stopRequested ? -2 : child.ExitCode;
            process = null;
        }
        // The native frontend marshals this managed callback to its main loop.
        Exited?.Invoke(code);
        child.Dispose();
    }
    public void Stop()
    {
        lock (gate)
        {
            if (process is null) return;
            stopRequested = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Stop();
        // Exit callback owns final Process disposal; do not block Cocoa on a child wait.
    }
}
