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
        public bool GroupReady, StopRequested, CleanupStarted, ReaderFailed, CleanupCanRetry;
        public Task Cleanup = Task.CompletedTask, Observation = Task.CompletedTask;
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
    public event Action<long, string>? CleanupFailed;
    // Private deterministic OS-failure seams; never selected by runtime configuration.
    private Func<int, int, int>? signalFailureForTesting = null;
    private Action<bool>? readerFailureForTesting = null;
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
            var started = false;
            try
            {
                if (!(started = child.Start())) throw new InvalidOperationException("Could not start the owned emulator.");
                run.Pid = child.Id; current = run;
                run.Output = PumpAsync(run, child.StandardOutput, false);
                run.Error = PumpAsync(run, child.StandardError, true);
                run.Observation = ObserveRunAsync(run);
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
        try
        {
            await ReadBoundedLinesCoreAsync(reader, line =>
            {
                if (error)
                {
                    lock (gate)
                    {
                        // Only the helper's first own-PID acknowledgement establishes
                        // private-group authority; guest text cannot replace it.
                        if (!run.GroupReady && line == $"SHARPEMU_CHILD_GROUP_READY:{run.Pid}")
                        {
                            run.GroupReady = true;
                            if (run.CleanupStarted) TrySendSignal(run, 15);
                            return;
                        }
                    }
                }
                Output?.Invoke(run.Session, line, error);
            }, () => readerFailureForTesting?.Invoke(error)).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsOrdinary(failure))
        {
            lock (gate)
            {
                run.ReaderFailed = true;
                ReportFailure(run, $"{(error ? "stderr" : "stdout")} reader failed: {failure.Message}");
                // Dispose only our failed endpoint. Other reader and actual root
                // completion remain independently observed; this is not EOF proof.
                try { reader.Dispose(); } catch (Exception cleanup) when (IsOrdinary(cleanup)) { ReportFailure(run, $"reader disposal failed: {cleanup.Message}"); }
                // Schedule after Start publishes both reader tasks and observation.
                if (ReferenceEquals(current, run)) BeginCleanupLocked(run, false);
            }
        }
    }
    private static Task ReadBoundedLinesAsync(TextReader reader, Action<string> line) => ReadBoundedLinesCoreAsync(reader, line, null);
    private static async Task ReadBoundedLinesCoreAsync(TextReader reader, Action<string> line, Action? beforeRead)
    {
        var buffer = new char[4096]; var text = new StringBuilder(4096); var truncated = false;
        int count;
        while (true)
        {
            beforeRead?.Invoke();
            count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
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
    private static bool IsOrdinary(Exception failure) => failure is not OutOfMemoryException and not AccessViolationException;
    private void ReportFailure(Run run, string message)
    {
        // The retained session remains unavailable for Start. UI diagnostics may
        // cancel a pending Quit, but cannot authorize dropping ownership.
        try { CleanupFailed?.Invoke(run.Session, message); }
        catch (Exception observerFailure) when (IsOrdinary(observerFailure)) { Debug.WriteLine(observerFailure); }
    }
    private async Task ObserveRunAsync(Run run)
    {
        try
        {
            await run.Process.WaitForExitAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (!ReferenceEquals(current, run)) return;
                BeginCleanupLocked(run, false);
            }
            await Task.WhenAll(run.Output, run.Error).ConfigureAwait(false);
            Task cleanup;
            lock (gate) { if (!ReferenceEquals(current, run)) return; cleanup = run.Cleanup; }
            await cleanup.ConfigureAwait(false);
            lock (gate) TryCompleteLocked(run);
        }
        catch (Exception failure) when (IsOrdinary(failure))
        {
            lock (gate) if (ReferenceEquals(current, run)) ReportFailure(run, $"exit observation failed: {failure.Message}");
        }
    }
    private void BeginCleanupLocked(Run run, bool userStop)
    {
        if (userStop) run.StopRequested = true;
        // Explicit retry may re-send TERM; the existing deadline still owns KILL.
        TrySendSignal(run, 15);
        if (!run.CleanupStarted || run.Cleanup.IsCompleted || run.CleanupCanRetry)
        {
            run.CleanupStarted = true; run.CleanupCanRetry = false;
            run.Cleanup = CleanupAsync(run, defer: true);
        }
    }
    private bool TrySendSignal(Run run, int signal)
    {
        try
        {
            var destination = run.GroupReady ? -run.Pid : run.Pid;
            if (!run.GroupReady && run.Process.HasExited) return true;
            var injected = signalFailureForTesting?.Invoke(destination, signal) ?? 0;
            var error = injected != 0 ? injected : Signal(destination, signal) == 0 ? 0 : Marshal.GetLastPInvokeError();
            if (error is 0 or 3) return true; // ESRCH is absence, not successful execution.
            throw new System.ComponentModel.Win32Exception(error);
        }
        catch (Exception failure) when (IsOrdinary(failure))
        { ReportFailure(run, $"signal {signal} failed: {failure.Message}; owned session retained"); return false; }
    }
    private bool IsGroupGone(Run run)
    {
        if (!run.GroupReady) return !run.ReaderFailed && run.Process.HasExited;
        var injected = signalFailureForTesting?.Invoke(-run.Pid, 0) ?? 0;
        if (injected != 0) throw new System.ComponentModel.Win32Exception(injected);
        if (Signal(-run.Pid, 0) == 0) return false;
        var error = Marshal.GetLastPInvokeError();
        if (error == 3) return true;
        throw new System.ComponentModel.Win32Exception(error);
    }
    private bool TryCompleteLocked(Run run)
    {
        if (!ReferenceEquals(current, run)) return true;
        if (!run.Process.HasExited || !run.Output.IsCompleted || !run.Error.IsCompleted || !IsGroupGone(run)) return false;
        var code = run.StopRequested ? -2 : run.Process.ExitCode;
        current = null;
        try { Exited?.Invoke(run.Session, code); }
        catch (Exception failure) when (IsOrdinary(failure)) { ReportFailure(run, $"exit notification failed: {failure.Message}"); }
        finally { run.Process.Dispose(); }
        return true;
    }
    public void Stop()
    {
        lock (gate) if (current is { } run) BeginCleanupLocked(run, true);
    }
    private async Task CleanupAsync(Run run, bool defer)
    {
        try
        {
            if (defer) await Task.Yield();
            lock (gate)
            {
                if (!ReferenceEquals(current, run)) return;
                run.CleanupStarted = true;
                TrySendSignal(run, 15);
            }
            try { await Task.WhenAll(run.Process.WaitForExitAsync(), run.Output, run.Error).WaitAsync(stopGracePeriod).ConfigureAwait(false); }
            catch (TimeoutException) { }
            lock (gate)
            {
                if (TryCompleteLocked(run)) return;
                // Only the established private group or pre-ack owned PID is signalled.
                // Success does not prove death; actual absence is checked below.
                TrySendSignal(run, 9);
            }
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(10).ConfigureAwait(false);
                lock (gate) if (TryCompleteLocked(run)) return;
            }
            lock (gate) if (ReferenceEquals(current, run))
            {
                // Publish retry eligibility before notification: its handler may
                // synchronously request another attempt before this Task returns.
                run.CleanupCanRetry = true;
                ReportFailure(run, "cleanup deadline expired without confirmed root/reader/private-group completion; owned session retained. Retry Stop.");
            }
        }
        catch (Exception failure) when (IsOrdinary(failure))
        {
            lock (gate) if (ReferenceEquals(current, run))
            {
                run.CleanupCanRetry = true;
                ReportFailure(run, $"cleanup observation failed: {failure.Message}; owned session retained. Retry Stop.");
            }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Stop(); // no main-thread wait; retained tasks observe actual completion
    }
}
