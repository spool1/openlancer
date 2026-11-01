using System;
using System.Collections.Concurrent;
using System.Threading;
using LibreLancer;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A UI thread that keeps the queued actions until the test runs them, as a frame of the editor does.</summary>
internal sealed class QueuedUIThread : IUIThread
{
    private readonly BlockingCollection<Action> queue = [];
    private int queuedFromThreadPool;

    /// <summary>The count of actions that a thread of the thread pool queued. The process tests use it. They show that
    /// no action of a program comes from a thread of the pool.</summary>
    public int QueuedFromThreadPool => Volatile.Read(ref queuedFromThreadPool);

    public void QueueUIThread(Action work)
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            Interlocked.Increment(ref queuedFromThreadPool);
        }

        queue.Add(work);
    }

    /// <summary>Runs the next actions, and waits for each one.</summary>
    public void Run(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(queue.TryTake(out var work, TimeSpan.FromSeconds(30)), "No action was queued.");
            work();
        }
    }

    /// <summary>Runs the queued actions in order, also the actions that they queue, until the queue is empty. The
    /// editor runs its queue in the same way.</summary>
    public void RunQueued()
    {
        while (queue.TryTake(out var work))
        {
            work();
        }
    }

    /// <summary>Runs the queued actions in order, and waits for more, until the condition is true.</summary>
    public void RunUntil(Func<bool> done, string what)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done())
        {
            var left = end - DateTime.UtcNow;
            Assert.True(left > TimeSpan.Zero, $"Not in 30 s: {what}");
            if (queue.TryTake(out var work, left))
            {
                work();
            }
        }
    }

    /// <summary>Runs the queued actions in order until no action comes for the time.</summary>
    public void RunUntilQuiet(TimeSpan quiet)
    {
        while (queue.TryTake(out var work, quiet))
        {
            work();
        }
    }

    /// <summary>Waits until the queue has the count of actions, and runs none of them.</summary>
    public void WaitForQueued(int count)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (queue.Count < count)
        {
            Assert.True(DateTime.UtcNow < end, $"{count} actions were not queued in 30 s.");
            Thread.Sleep(10);
        }
    }
}
