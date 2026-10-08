// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using SharpEmu.Application;
namespace SharpEmu.Launcher.MacOS;

/// <summary>One child handle owned by the launcher. Never discovers or stops external emulator processes.</summary>
internal sealed class OwnedEmulatorProcess : IDisposable
{
    private readonly object gate = new();
    private Process? process;
    private bool disposed;
    private bool stopRequested;
    private long session;
    private readonly TimeSpan stopGracePeriod;
    public OwnedEmulatorProcess() : this(TimeSpan.FromSeconds(10)) { }
    private OwnedEmulatorProcess(TimeSpan grace)
    {
        if (grace <= TimeSpan.Zero || grace > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(grace));
        stopGracePeriod = grace;
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
    private static extern int Signal(int pid, int signal);
    public event Action<long, string, bool>? Output;
    public event Action<long, int>? Exited;
    public bool IsRunning { get { lock (gate) return process is not null; } }
    public long Start(string executable, LauncherLaunchPlan plan)
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
            var currentSession = checked(++session);
            var child = new Process { StartInfo = info, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, args) => { if (args.Data is { } line) Output?.Invoke(currentSession, line, false); };
            child.ErrorDataReceived += (_, args) => { if (args.Data is { } line) Output?.Invoke(currentSession, line, true); };
            child.Exited += (_, _) => OnExited(child, currentSession);
            var started = false;
            try
            {
                if (!(started = child.Start())) throw new InvalidOperationException("Could not start the owned emulator.");
                process = child; stopRequested = false;
                child.BeginOutputReadLine(); child.BeginErrorReadLine();
                return currentSession;
            }
            catch
            {
                if (ReferenceEquals(process, child)) process = null;
                if (started) try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                child.Dispose(); throw;
            }
        }
    }
    private void OnExited(Process child, long currentSession)
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
        try { Exited?.Invoke(currentSession, code); }
        finally { child.Dispose(); }
    }
    public void Stop()
    {
        Process child;
        lock (gate)
        {
            if (process is null || stopRequested) return;
            child = process;
            // Request cooperative host shutdown before any forced termination.
            // Only this retained child is targeted; never search for emulator names.
            if (!child.HasExited && Signal(child.Id, 15) != 0 && !child.HasExited)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            stopRequested = true;
        }
        _ = ForceStopAfterGraceAsync(child);
    }
    private async Task ForceStopAfterGraceAsync(Process child)
    {
        try { await child.WaitForExitAsync().WaitAsync(stopGracePeriod).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (TimeoutException)
        {
            lock (gate)
            {
                if (!ReferenceEquals(process, child)) return;
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // exit won the race
                catch (System.ComponentModel.Win32Exception) when (child.HasExited) { }
            }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Stop();
        // Exit callback owns final Process disposal; do not block Cocoa on a child wait.
    }
}
