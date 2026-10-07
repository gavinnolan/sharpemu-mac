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
        type.GetEvent("Output")!.AddEventHandler(owner, (Action<string, bool>)((text, error) => lines.Enqueue(text)));
        type.GetEvent("Exited")!.AddEventHandler(owner, (Action<int>)(code => { Interlocked.Increment(ref exits); done.TrySetResult(code); }));
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
        type.GetEvent("Exited")!.AddEventHandler(first, (Action<int>)(code => stopped.TrySetResult(code)));
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

}
