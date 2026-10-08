// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Launcher.MacOS;

// Output may be untrusted and continuous. Keep a bounded tail without losing
// ordered control jobs (exit, scan, close) that own resources or UI transitions.
internal sealed class BoundedLauncherNotifications
{
    internal const int MaximumOutputJobs = 1024;
    internal const int MaximumLineCharacters = 4096;
    internal sealed record Notification(long Session, string? Line, Action? Action);
    private readonly object gate = new();
    private readonly LinkedList<Notification> jobs = new();
    private readonly Queue<LinkedListNode<Notification>> output = new();
    private long dropped;
    private bool closed;
    public void Post(Action action)
    {
        lock (gate) { if (!closed) jobs.AddLast(new Notification(0, null, action)); }
    }
    public void PostOutput(long session, string line)
    {
        if (line.Length > MaximumLineCharacters) line = line[..MaximumLineCharacters] + " [truncated]";
        lock (gate)
        {
            if (closed) return;
            if (output.Count == MaximumOutputJobs) { jobs.Remove(output.Dequeue()); dropped++; }
            output.Enqueue(jobs.AddLast(new Notification(session, line, null)));
        }
    }
    public bool TryTake(out Notification? notification)
    {
        lock (gate)
        {
            if (jobs.First is not { } node) { notification = null; return false; }
            jobs.RemoveFirst(); notification = node.Value;
            if (notification.Line is not null) output.Dequeue();
            return true;
        }
    }
    public long TakeDroppedCount() { lock (gate) { var result = dropped; dropped = 0; return result; } }
    public void Close() { lock (gate) { closed = true; jobs.Clear(); output.Clear(); dropped = 0; } }
}
