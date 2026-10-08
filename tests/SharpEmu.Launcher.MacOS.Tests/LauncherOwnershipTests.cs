// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.ObjectModel;
using System.Reflection;
using SharpEmu.Application;
using SharpEmu.Launcher.MacOS;
using Xunit;
namespace SharpEmu.Launcher.MacOS.Tests;

public sealed class LauncherOwnershipTests
{
    [Fact]
    public void NativeFrontendHasNoPortableUiOrGuestLibraryReferenceOrFriendShortcut()
    {
        var assembly = typeof(MacLauncher).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("Avalonia") ||
            name.Name.StartsWith("Skia") || name.Name is "SharpEmu.GUI" or "SharpEmu.Libs" or "SharpEmu.Core");
        Assert.DoesNotContain(assembly.GetCustomAttributesData(), attribute => attribute.AttributeType.Name == "InternalsVisibleToAttribute");
    }

    [Theory]
    [InlineData("[inherit]", true, 0)]
    [InlineData("[empty]", false, 0)]
    [InlineData("KEY=1\nVALUE=two words", false, 2)]
    public void NativeSettingsEditorDistinguishesInheritedAndEmptyLists(string text, bool inherited, int count)
    {
        var parse = typeof(MacLauncher).GetMethod("ParseValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        var value = (List<string>?)parse.Invoke(null, [typeof(List<string>), text]);
        if (inherited) Assert.Null(value); else Assert.Equal(count, value!.Count);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("not a number")]
    public void NativeSettingsEditorRejectsInvalidNumbersBeforeMutatingModel(string text)
    {
        var parse = typeof(MacLauncher).GetMethod("ParseValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.IsType<FormatException>(Assert.Throws<TargetInvocationException>(() => parse.Invoke(null, [typeof(double), text])).InnerException);
    }

    private sealed class EditorModel { public int Count { get; set; } = 7; public double Scale { get; set; } = 1; }
    [Fact]
    public void ModelessEditorValidatesAllValuesBeforeMutationAndCloseRevokesSave()
    {
        var type = typeof(MacLauncher).GetNestedType("SettingsEditSession", BindingFlags.NonPublic)!;
        var model = new EditorModel(); var saves = 0;
        var session = Activator.CreateInstance(type, [model, null, (Action)(() => saves++)])!;
        var rows = (System.Collections.IEnumerable)type.GetProperty("Rows")!.GetValue(session)!;
        foreach (var row in rows)
        {
            var property = (PropertyInfo)row.GetType().GetProperty("Property")!.GetValue(row)!;
            row.GetType().GetField("Value")!.SetValue(row, property.Name == "Count" ? "19" : "NaN");
        }
        var save = type.GetMethod("Save")!; var close = type.GetMethod("Close")!;
        Assert.IsType<FormatException>(Assert.Throws<TargetInvocationException>(() => save.Invoke(session, null)).InnerException);
        Assert.Equal(7, model.Count); Assert.Equal(1, model.Scale); Assert.Equal(0, saves);
        foreach (var row in rows)
            if (((PropertyInfo)row.GetType().GetProperty("Property")!.GetValue(row)!).Name == "Scale") row.GetType().GetField("Value")!.SetValue(row, "2.5");
        save.Invoke(session, null); Assert.Equal(19, model.Count); Assert.Equal(2.5, model.Scale); Assert.Equal(1, saves);
        Assert.Equal(true, close.Invoke(session, null)); Assert.Equal(false, close.Invoke(session, null));
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => save.Invoke(session, null)).InnerException);
        Assert.Equal(1, saves);
    }
    [Fact]
    public void RefreshedCatalogSelectionUsesPathAcrossReorderAndClearsWhenFilteredOut()
    {
        var select = typeof(MacLauncher).GetMethod("PreservedSelectionIndex", BindingFlags.NonPublic | BindingFlags.Static)!;
        var first = new CatalogGame("Alpha", "A", "1", "/fixture/alpha/eboot.bin", 0, null, null);
        var refreshed = first with { Name = "New Alpha" };
        var beta = first with { Name = "Beta", Path = "/fixture/beta/eboot.bin" };
        Assert.Equal(1, select.Invoke(null, [new[] { beta, refreshed }, first.Path]));
        Assert.Equal(-1, select.Invoke(null, [new[] { beta }, first.Path]));
        Assert.Equal(-1, select.Invoke(null, [new[] { first }, null]));
    }

    [Fact]
    public async Task OwnedSyntheticChildDrainsBothStreamsAndReportsExitOnce()
    {
        if (!OperatingSystem.IsMacOS()) return; // actual Mac process owner qualifier
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type)!;
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>(); var exits = 0;
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        type.GetEvent("Output")!.AddEventHandler(owner, (Action<long, string, bool>)((session, text, error) => lines.Enqueue(text)));
        type.GetEvent("Exited")!.AddEventHandler(owner, (Action<long, int>)((session, code) => { Interlocked.Increment(ref exits); done.TrySetResult(code); }));
        var plan = new LauncherLaunchPlan(new ReadOnlyCollection<string>(["-c", "printf 'stdout-last\\n'; printf 'stderr-last\\n' >&2; exit 7"]),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
        type.GetMethod("Start")!.Invoke(owner, ["/bin/sh", plan]);
        Assert.Equal(7, await done.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, exits); Assert.Contains("stdout-last", lines); Assert.Contains("stderr-last", lines);
        Assert.False((bool)type.GetProperty("IsRunning")!.GetValue(owner)!);
        owner.Dispose(); owner.Dispose();
    }
    [Fact]
    public async Task StopAffectsOnlyTheLaunchersOwnedChild()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var first = (IDisposable)Activator.CreateInstance(type)!;
        using var second = (IDisposable)Activator.CreateInstance(type)!;
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        type.GetEvent("Exited")!.AddEventHandler(first, (Action<long, int>)((session, code) => stopped.TrySetResult(code)));
        var plan = new LauncherLaunchPlan(Array.AsReadOnly(new[] { "30" }),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
        type.GetMethod("Start")!.Invoke(first, ["/bin/sleep", plan]);
        type.GetMethod("Start")!.Invoke(second, ["/bin/sleep", plan]);
        type.GetMethod("Stop")!.Invoke(first, null);
        Assert.Equal(-2, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False((bool)type.GetProperty("IsRunning")!.GetValue(first)!);
        Assert.True((bool)type.GetProperty("IsRunning")!.GetValue(second)!);
        type.GetMethod("Stop")!.Invoke(first, null); // repeat cannot discover another process
        Assert.True((bool)type.GetProperty("IsRunning")!.GetValue(second)!);
    }

    [Fact]
    public async Task CooperativeTermPreservesFinalOutputAndDoesNotWaitForForcedDeadline()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type)!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        type.GetEvent("Output")!.AddEventHandler(owner, (Action<long, string, bool>)((_, line, _) => { lines.Enqueue(line); if (line == "ready") ready.TrySetResult(); }));
        type.GetEvent("Exited")!.AddEventHandler(owner, (Action<long, int>)((_, code) => exited.TrySetResult(code)));
        var plan = new LauncherLaunchPlan(Array.AsReadOnly(new[] { "-c", "trap 'printf \"term-final\\n\"; exit 0' TERM; printf 'ready\\n'; while :; do sleep 0.1; done" }),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
        type.GetMethod("Start")!.Invoke(owner, ["/bin/sh", plan]);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var elapsed = System.Diagnostics.Stopwatch.StartNew(); type.GetMethod("Stop")!.Invoke(owner, null);
        Assert.Equal(-2, await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("term-final", lines); Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TermIgnoringOwnedTreeReachesForcedDeadlineAndCompletesInheritedReaders()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [TimeSpan.FromMilliseconds(250)], null)!;
        var descendantPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        type.GetEvent("Output")!.AddEventHandler(owner, (Action<long, string, bool>)((_, line, _) =>
        {
            if (line.StartsWith("descendant:", StringComparison.Ordinal) && int.TryParse(line[11..], out var pid)) descendantPid.TrySetResult(pid);
        }));
        type.GetEvent("Exited")!.AddEventHandler(owner, (Action<long, int>)((_, code) => exited.TrySetResult(code)));
        var plan = new LauncherLaunchPlan(Array.AsReadOnly(new[] { "-c", "trap '' TERM; sleep 30 & child=$!; printf 'descendant:%s\\n' \"$child\"; wait \"$child\"" }),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
        type.GetMethod("Start")!.Invoke(owner, ["/bin/sh", plan]);
        var pid = await descendantPid.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var descendant = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            Assert.False(descendant.HasExited);
            type.GetMethod("Stop")!.Invoke(owner, null);
            // OnExited waits for the redirected readers before notifying; an
            // orphan inheriting stdout/stderr would keep this completion pending.
            Assert.Equal(-2, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(descendant.HasExited);
            Assert.False((bool)type.GetProperty("IsRunning")!.GetValue(owner)!);
        }
        finally
        {
            if (!descendant.HasExited) descendant.Kill(entireProcessTree: true); // this fixture's exact retained child only
        }
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.B.dylib")] private static extern int getpgid(int pid);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.B.dylib")] private static extern int getsid(int pid);
    [Fact]
    public async Task ExitedRootCannotOrphanPipeHoldingGroupMemberOrAffectAnotherOwner()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [TimeSpan.FromSeconds(2)], null)!;
        using var unrelated = (IDisposable)Activator.CreateInstance(type)!;
        var descendantPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        type.GetEvent("Output")!.AddEventHandler(owner, (Action<long, string, bool>)((_, line, _) =>
        {
            if (line.StartsWith("descendant:",StringComparison.Ordinal)) descendantPid.TrySetResult(int.Parse(line[11..]));
        }));
        type.GetEvent("Exited")!.AddEventHandler(owner, (Action<long,int>)((_,code) => exited.TrySetResult(code)));
        var env = new ReadOnlyDictionary<string,string>(new Dictionary<string,string>());
        type.GetMethod("Start")!.Invoke(unrelated,["/bin/sleep",new LauncherLaunchPlan(Array.AsReadOnly(new[] {"30"}),env)]);
        type.GetMethod("Start")!.Invoke(owner,["/bin/sh",new LauncherLaunchPlan(Array.AsReadOnly(new[] {"-c","trap '' TERM; sleep 30 & child=$!; printf 'descendant:%s\\n' \"$child\"; exit 7"}),env)]);
        var pid = await descendantPid.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var run = type.GetField("current",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
        var parent = (System.Diagnostics.Process)run.GetType().GetField("Process")!.GetValue(run)!;
        var parentPid = parent.Id;
        // WaitForExitAsync can subscribe behind the owner's Exited callback,
        // which intentionally waits for the inherited readers. Observe actual
        // root exit independently; do not require those readers to complete yet.
        var rootExitDeadline = System.Diagnostics.Stopwatch.StartNew();
        while (!parent.HasExited && rootExitDeadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(parent.HasExited);
        using var descendant = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            Assert.True(parent.HasExited); Assert.False(descendant.HasExited);
            Assert.Equal(parentPid,getpgid(pid)); Assert.Equal(parentPid,getsid(pid));
            Assert.False(exited.Task.IsCompleted); // reader ownership outlives root PID
            type.GetMethod("Stop")!.Invoke(owner,null);
            Assert.Equal(-2,await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(descendant.HasExited);
            Assert.True((bool)type.GetProperty("IsRunning")!.GetValue(unrelated)!);
            Assert.False((bool)type.GetProperty("IsRunning")!.GetValue(owner)!);
        }
        finally { if (!descendant.HasExited) descendant.Kill(entireProcessTree:true); }
    }

    [Fact]
    public async Task NaturalRootExitAutomaticallyCleansPipeHoldingDescendantAndPreservesCode()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[TimeSpan.FromMilliseconds(250)],null)!;
        var descendantPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        type.GetEvent("Output")!.AddEventHandler(owner,(Action<long,string,bool>)((_,line,_) => { if (line.StartsWith("descendant:",StringComparison.Ordinal)) descendantPid.TrySetResult(int.Parse(line[11..])); }));
        type.GetEvent("Exited")!.AddEventHandler(owner,(Action<long,int>)((_,code) => exited.TrySetResult(code)));
        var plan = new LauncherLaunchPlan(Array.AsReadOnly(new[] {"-c","trap '' TERM; sleep 30 & child=$!; printf 'descendant:%s\\n' \"$child\"; exit 7"}),new ReadOnlyDictionary<string,string>(new Dictionary<string,string>()));
        type.GetMethod("Start")!.Invoke(owner,["/bin/sh",plan]);
        var pid = await descendantPid.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var descendant = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            Assert.Equal(7,await exited.Task.WaitAsync(TimeSpan.FromSeconds(5))); // no user Stop
            await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(descendant.HasExited);
            Assert.False((bool)type.GetProperty("IsRunning")!.GetValue(owner)!);
        }
        finally { if (!descendant.HasExited) descendant.Kill(entireProcessTree:true); }
    }

    [Fact]
    public async Task BoundedStreamReaderHandlesHugeUnterminatedLineCrLfAndFinalTail()
    {
        var reader = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!.GetMethod("ReadBoundedLinesAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        var lines = new List<string>();
        using var input = new StringReader(new string('x',1000000)+"\r\nnext\r\nfinal");
        await (Task)reader.Invoke(null,[input,(Action<string>)lines.Add])!;
        Assert.Equal(new[] {new string('x',4096)+" [truncated]","next","final"},lines);
    }

    [Fact]
    public void CleanupDiagnosticCancelsOnlyItsOwnPendingQuit()
    {
        var frontend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MacLauncher));
        var type = typeof(MacLauncher); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        type.GetField("activeChildSession",flags)!.SetValue(frontend,2L);
        var pending = type.GetField("closeAfterChildExit",flags)!; pending.SetValue(frontend,true);
        var messages = new List<string>(); var report = type.GetMethod("DispatchChildCleanupFailure",flags)!;
        report.Invoke(frontend,[1L,(Action)(() => messages.Add("stale"))]); Assert.True((bool)pending.GetValue(frontend)!); Assert.Empty(messages);
        report.Invoke(frontend,[2L,(Action)(() => messages.Add("visible failure"))]); Assert.False((bool)pending.GetValue(frontend)!); Assert.Equal(new[] {"visible failure"},messages);
    }

    [Fact]
    public async Task FailedTermAndKillKeepSessionOwnedAndExplicitRetryCompletes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[TimeSpan.FromMilliseconds(250)],null)!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        type.GetEvent("Output")!.AddEventHandler(owner,(Action<long,string,bool>)((_,line,_) => { if (line == "READY") ready.TrySetResult(); }));
        type.GetEvent("CleanupFailed")!.AddEventHandler(owner,(Action<long,string>)((_,message) => { if (message.Contains("deadline expired")) failure.TrySetResult(message); }));
        type.GetEvent("Exited")!.AddEventHandler(owner,(Action<long,int>)((_,code) => exited.TrySetResult(code)));
        var env = new ReadOnlyDictionary<string,string>(new Dictionary<string,string>());
        var plan = new LauncherLaunchPlan(Array.AsReadOnly(new[] {"-c","trap '' TERM; printf 'READY\n'; sleep 30"}),env);
        type.GetMethod("Start")!.Invoke(owner,["/bin/sh",plan]); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var run = type.GetField("current",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
        var seam = type.GetField("signalFailureForTesting",BindingFlags.Instance|BindingFlags.NonPublic)!;
        seam.SetValue(owner,(Func<int,int,int>)((_,signal) => signal is 15 or 9 ? 1 : 0));
        try
        {
            type.GetMethod("Stop")!.Invoke(owner,null);
            Assert.Contains("owned session retained",await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(exited.Task.IsCompleted); Assert.True((bool)type.GetProperty("IsRunning")!.GetValue(owner)!);
            await ((Task)run.GetType().GetField("Cleanup")!.GetValue(run)!).WaitAsync(TimeSpan.FromSeconds(2)); // failure is handled, not an unobserved task fault
            Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => type.GetMethod("Start")!.Invoke(owner,["/bin/sh",plan])).InnerException);
        }
        finally { seam.SetValue(owner,null); type.GetMethod("Stop")!.Invoke(owner,null); }
        Assert.Equal(-2,await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await ((Task)run.GetType().GetField("Observation")!.GetValue(run)!).WaitAsync(TimeSpan.FromSeconds(2));
        await ((Task)run.GetType().GetField("Cleanup")!.GetValue(run)!).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False((bool)type.GetProperty("IsRunning")!.GetValue(owner)!);
    }
    [Fact]
    public async Task FailureObserverCanRetryBeforeTheFailedCleanupTaskReturns()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[TimeSpan.FromMilliseconds(250)],null)!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seam = type.GetField("signalFailureForTesting",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var retryCount = 0;
        type.GetEvent("Output")!.AddEventHandler(owner,(Action<long,string,bool>)((_,line,_) => { if (line == "READY") ready.TrySetResult(); }));
        type.GetEvent("Exited")!.AddEventHandler(owner,(Action<long,int>)((_,code) => exited.TrySetResult(code)));
        type.GetEvent("CleanupFailed")!.AddEventHandler(owner,(Action<long,string>)((_,message) =>
        {
            if (!message.Contains("deadline expired") || Interlocked.Increment(ref retryCount) != 1) return;
            seam.SetValue(owner,null);
            type.GetMethod("Stop")!.Invoke(owner,null); // synchronous, inside first task's notification
        }));
        var plan = new LauncherLaunchPlan(Array.AsReadOnly(new[] {"-c","trap '' TERM; printf 'READY\n'; sleep 30"}),new ReadOnlyDictionary<string,string>(new Dictionary<string,string>()));
        type.GetMethod("Start")!.Invoke(owner,["/bin/sh",plan]); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        seam.SetValue(owner,(Func<int,int,int>)((_,signal) => signal is 15 or 9 ? 1 : 0));
        try { type.GetMethod("Stop")!.Invoke(owner,null); Assert.Equal(-2,await exited.Task.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(1,retryCount); }
        finally { seam.SetValue(owner,null); type.GetMethod("Stop")!.Invoke(owner,null); }
    }

    [Fact]
    public async Task ActualPumpReaderFailureIsVisibleAndNaturalRootCodeIsPreserved()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[TimeSpan.FromMilliseconds(250)],null)!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        type.GetEvent("Output")!.AddEventHandler(owner,(Action<long,string,bool>)((_,line,_) => { if (line == "READY") ready.TrySetResult(); }));
        type.GetEvent("CleanupFailed")!.AddEventHandler(owner,(Action<long,string>)((_,message) => { errors.Enqueue(message); if (message.Contains("reader failed")) failure.TrySetResult(message); }));
        type.GetEvent("Exited")!.AddEventHandler(owner,(Action<long,int>)((_,code) => exited.TrySetResult(code)));
        var signalSeam = type.GetField("signalFailureForTesting",BindingFlags.Instance|BindingFlags.NonPublic)!;
        // Leave the real root time to exit 7 independently of the failed reader.
        signalSeam.SetValue(owner,(Func<int,int,int>)((_,signal) => signal == 15 ? 1 : 0));
        var readSeam = type.GetField("readerFailureForTesting",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var plan = new LauncherLaunchPlan(Array.AsReadOnly(new[] {"-c","trap '' TERM; sleep 30 & printf 'READY\n'; sleep 0.4; printf 'TICK\n'; exit 7"}),new ReadOnlyDictionary<string,string>(new Dictionary<string,string>()));
        type.GetMethod("Start")!.Invoke(owner,["/bin/sh",plan]); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var injected = 0;
        readSeam.SetValue(owner,(Action<bool>)(error => { if (!error && Interlocked.Exchange(ref injected,1) == 0) throw new IOException("controlled reader failure"); }));
        try
        {
            Assert.Contains("controlled reader failure",await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(7,await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False((bool)type.GetProperty("IsRunning")!.GetValue(owner)!);
            var runErrors = errors.ToArray(); Assert.Contains(runErrors,message => message.Contains("signal 15 failed"));
        }
        finally { readSeam.SetValue(owner,null); signalSeam.SetValue(owner,null); type.GetMethod("Stop")!.Invoke(owner,null); }
    }

    [Fact]
    public async Task QueuedNotificationsFromExitedSessionCannotMutateANewerLaunch()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.OwnedEmulatorProcess")!;
        using var owner = (IDisposable)Activator.CreateInstance(type)!;
        var frontend = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MacLauncher));
        var select = typeof(MacLauncher).GetMethod("SelectChildSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dispatch = typeof(MacLauncher).GetMethod("DispatchChildNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var queued = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = new List<string>();
        type.GetEvent("Output")!.AddEventHandler(owner, (Action<long, string, bool>)((session, line, _) => queued.Enqueue(() => dispatch.Invoke(frontend, [session, (Action)(() => mutations.Add(line))]))));
        type.GetEvent("Exited")!.AddEventHandler(owner, (Action<long, int>)((session, code) =>
        {
            queued.Enqueue(() => dispatch.Invoke(frontend, [session, (Action)(() => mutations.Add($"exit:{code}"))]));
            finished.TrySetResult();
        }));
        var env = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        var first = (long)type.GetMethod("Start")!.Invoke(owner, ["/bin/sh", new LauncherLaunchPlan(Array.AsReadOnly(new[] { "-c", "printf 'old-output\\n'; exit 7" }), env)])!;
        select.Invoke(frontend, [first]);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(queued.Count >= 2); // actual output and actual exit remain deferred
        var second = (long)type.GetMethod("Start")!.Invoke(owner, ["/bin/sleep", new LauncherLaunchPlan(Array.AsReadOnly(new[] { "30" }), env)])!;
        Assert.True(second > first); select.Invoke(frontend, [second]);
        while (queued.TryDequeue(out var notification)) notification();
        Assert.Empty(mutations); // old jobs cannot close the newer log or replace its status
        dispatch.Invoke(frontend, [second, (Action)(() => mutations.Add("new-session"))]);
        Assert.Equal(new[] { "new-session" }, mutations);
    }

    [Fact]
    public void QuitUnwindsNestedModalsBeforeClosingMainAndConsumesCanceledClose()
    {
        var type = typeof(MacLauncher).GetNestedType("QuitSequence", BindingFlags.NonPublic)!;
        var sequence = Activator.CreateInstance(type)!;
        var request = type.GetMethod("Request")!; var next = type.GetMethod("Continue")!;
        Assert.Equal(1, request.Invoke(sequence, [true])); // settings or nested alert loop
        Assert.Equal(1, next.Invoke(sequence, [true])); // inner alert exited; editor still modal
        Assert.Equal(2, next.Invoke(sequence, [false])); // now main close belongs to outer loop
        Assert.Equal(0, next.Invoke(sequence, [false])); // a canceled child-stop prompt cannot retrigger quit
        Assert.Equal(2, request.Invoke(sequence, [false]));
    }

    [Fact]
    public void CocoaDelegateReturnAbiUsesByteBoolAndUnsignedNativeInteger()
    {
        var boolType = typeof(MacLauncher).GetNestedType("BoolCallback", BindingFlags.NonPublic)!;
        var unsignedType = typeof(MacLauncher).GetNestedType("UnsignedCallback", BindingFlags.NonPublic)!;
        Assert.Equal(typeof(byte), boolType.GetMethod("Invoke")!.ReturnType);
        Assert.Equal(typeof(nuint), unsignedType.GetMethod("Invoke")!.ReturnType);
        var encoding = typeof(MacLauncher).GetProperty("WindowCloseEncoding", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "B@:@" : "c@:@", encoding);
    }
}
