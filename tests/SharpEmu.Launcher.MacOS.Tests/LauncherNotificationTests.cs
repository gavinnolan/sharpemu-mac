// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Launcher.MacOS;
using Xunit;
namespace SharpEmu.Launcher.MacOS.Tests;

public sealed class LauncherNotificationTests
{
    [Fact]
    public void ContinuousOutputRetainsBoundedTailAndOrderedResourceControlJobs()
    {
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.BoundedLauncherNotifications")!;
        var queue = Activator.CreateInstance(type)!;
        var output = type.GetMethod("PostOutput")!; var post = type.GetMethod("Post")!;
        var take = type.GetMethod("TryTake")!; var controls = new List<string>();
        post.Invoke(queue, [(Action)(() => controls.Add("scan"))]);
        for (var i = 0; i < 3000; i++) output.Invoke(queue, [7L, i.ToString()]);
        post.Invoke(queue, [(Action)(() => controls.Add("exit"))]);
        var retained = new List<string>(); var observed = new List<string>();
        object?[] args = [null];
        while ((bool)take.Invoke(queue, args)!)
        {
            var job = args[0]!; var jobType = job.GetType();
            if (jobType.GetProperty("Action")!.GetValue(job) is Action action) { action(); observed.Add(controls[^1]); }
            else { retained.Add((string)jobType.GetProperty("Line")!.GetValue(job)!); observed.Add("output"); }
        }
        Assert.Equal(1024, retained.Count); Assert.Equal("1976", retained[0]); Assert.Equal("2999", retained[^1]);
        Assert.Equal(new[] { "scan", "exit" }, controls); Assert.Equal("scan", observed[0]); Assert.Equal("exit", observed[^1]);
        Assert.Equal(1976L, type.GetMethod("TakeDroppedCount")!.Invoke(queue, null));
        Assert.Equal(0L, type.GetMethod("TakeDroppedCount")!.Invoke(queue, null));
    }
    [Fact]
    public void OversizedRetainedLineIsTruncatedAndCloseRejectsLateCallbacks()
    {
        var type = typeof(MacLauncher).Assembly.GetType("SharpEmu.Launcher.MacOS.BoundedLauncherNotifications")!;
        var queue = Activator.CreateInstance(type)!;
        type.GetMethod("PostOutput")!.Invoke(queue, [2L, new string('x', 100000)]);
        object?[] args = [null]; Assert.True((bool)type.GetMethod("TryTake")!.Invoke(queue, args)!);
        var line = (string)args[0]!.GetType().GetProperty("Line")!.GetValue(args[0])!;
        Assert.Equal(new string('x',4096)+" [truncated]", line);
        type.GetMethod("Close")!.Invoke(queue,null);
        type.GetMethod("PostOutput")!.Invoke(queue,[2L,"late"]);
        type.GetMethod("Post")!.Invoke(queue,[(Action)(() => throw new Exception("late control"))]);
        Assert.False((bool)type.GetMethod("TryTake")!.Invoke(queue,args)!);
    }
}
