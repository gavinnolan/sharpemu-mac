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
