// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using SharpEmu.Application;
namespace SharpEmu.Launcher.MacOS;

/// <summary>One private child group. Never discovers or stops external emulator processes.</summary>
internal sealed class OwnedEmulatorProcess : IDisposable
{
    private sealed class Run(Process process, long session)
    {
        public readonly Process Process = process;
        public readonly long Session = session;
        public int Pid;
        public bool GroupReady, StopRequested, CleanupStarted;
        public Task Output = Task.CompletedTask, Error = Task.CompletedTask;
    }
    private readonly object gate = new();
    private Run? current;
    private bool disposed;
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
    public bool IsRunning { get { lock (gate) return current is not null; } }
    public long Start(string executable, LauncherLaunchPlan plan)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is not null) throw new InvalidOperationException("An owned emulator is already running.");
            var helper = Path.Combine(AppContext.BaseDirectory, "SharpEmu.ChildHost");
            if (!File.Exists(helper)) throw new FileNotFoundException("The native private child host is missing.", helper);
            var info = new ProcessStartInfo(helper)
            {
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            info.ArgumentList.Add(executable);
            foreach (var argument in plan.Arguments) info.ArgumentList.Add(argument);
            foreach (var pair in plan.Environment) info.Environment[pair.Key] = pair.Value;
            var child = new Process { StartInfo = info, EnableRaisingEvents = true };
            var run = new Run(child, checked(++session));
            child.Exited += (_, _) => _ = OnExitedAsync(run);
            var started = false;
            try
            {
                if (!(started = child.Start())) throw new InvalidOperationException("Could not start the owned emulator.");
                run.Pid = child.Id; current = run;
                run.Output = PumpAsync(run, child.StandardOutput, false);
                run.Error = PumpAsync(run, child.StandardError, true);
                return run.Session;
            }
            catch
            {
                if (ReferenceEquals(current, run)) current = null;
                if (started) try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                child.Dispose(); throw;
            }
        }
    }
    private async Task PumpAsync(Run run, TextReader reader, bool error)
    {
        await ReadBoundedLinesAsync(reader, line =>
        {
            if (error)
            {
                lock (gate)
                {
                    // Only this helper's first exact own-PID acknowledgement can
                    // establish group authority. Guest output cannot change it.
                    if (!run.GroupReady && line == $"SHARPEMU_CHILD_GROUP_READY:{run.Pid}")
                    {
                        run.GroupReady = true;
                        if (run.CleanupStarted) SendSignal(run, 15);
                        return;
                    }
                }
            }
            Output?.Invoke(run.Session, line, error);
        }).ConfigureAwait(false);
    }
    private static async Task ReadBoundedLinesAsync(TextReader reader, Action<string> line)
    {
        var buffer = new char[4096]; var text = new StringBuilder(4096); var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') { Emit(); continue; }
                if (text.Length < 4096) text.Append(buffer[i]); else truncated = true;
            }
        }
        if (text.Length != 0 || truncated) Emit();
        void Emit()
        {
            if (text.Length != 0 && text[^1] == '\r') text.Length--;
            line(text.ToString() + (truncated ? " [truncated]" : string.Empty)); text.Clear(); truncated = false;
        }
    }
    private async Task OnExitedAsync(Run run)
    {
        Task readers; bool startCleanup;
        lock (gate)
        {
            if (!ReferenceEquals(current, run)) return;
            // Natural root exit must not strand private-group pipe holders.
            // Descendant cleanup is distinct from an explicit user Stop.
            startCleanup = BeginCleanupLocked(run, false);
            readers = Task.WhenAll(run.Output, run.Error);
        }
        if (startCleanup) _ = ForceStopAfterGraceAsync(run);
        try { await readers.ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        int code;
        lock (gate)
        {
            if (!ReferenceEquals(current, run)) return;
            code = run.StopRequested ? -2 : run.Process.ExitCode; current = null;
        }
        try { Exited?.Invoke(run.Session, code); }
        finally { run.Process.Dispose(); }
    }
    private bool BeginCleanupLocked(Run run, bool userStop)
    {
        if (userStop) run.StopRequested = true;
        if (run.CleanupStarted) return false;
        run.CleanupStarted = true;
        try { SendSignal(run, 15); }
        catch { run.CleanupStarted = false; throw; }
        return true;
    }
    private static void SendSignal(Run run, int signal)
    {
        var destination = run.GroupReady ? -run.Pid : run.Pid;
        if (!run.GroupReady && run.Process.HasExited) return;
        if (Signal(destination, signal) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 3) throw new System.ComponentModel.Win32Exception(error); // ESRCH: group/root already gone
        }
    }
    public void Stop()
    {
        Run run; bool startCleanup;
        lock (gate)
        {
            if (current is null) return;
            run = current; startCleanup = BeginCleanupLocked(run, true);
        }
        if (startCleanup) _ = ForceStopAfterGraceAsync(run);
    }
    private async Task ForceStopAfterGraceAsync(Run run)
    {
        try
        {
            await Task.WhenAll(run.Process.WaitForExitAsync(), run.Output, run.Error).WaitAsync(stopGracePeriod).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (TimeoutException)
        {
            lock (gate)
            {
                if (!ReferenceEquals(current, run)) return;
                // The group remains addressable even after its root exited while
                // a member holds redirected pipes. Never scan names or other PIDs.
                SendSignal(run, 9);
            }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Stop(); // completion owns Process disposal; Cocoa never waits here
    }
}
