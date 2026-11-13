using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LibreLancer;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A UI thread that keeps the queued actions until the test runs them, as a frame of the editor does.</summary>
/// <remarks>The waits are async. A test that waits for an action does not hold a thread of the thread pool, so the work
/// of the editor that queues the action can get one (issue 71).</remarks>
internal sealed class QueuedUIThread : IUIThread
{
    /// <summary>The time that a wait for an action can take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly Channel<Action> queue = Channel.CreateUnbounded<Action>();
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

        queue.Writer.TryWrite(work);
    }

    /// <summary>Runs the next actions, and waits for each one.</summary>
    public async Task RunAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(await NextAsync(Limit), "No action was queued.");
            Take();
        }
    }

    /// <summary>Runs the queued actions in order, also the actions that they queue, until the queue is empty. The
    /// editor runs its queue in the same way.</summary>
    public void RunQueued()
    {
        while (queue.Reader.TryRead(out var work))
        {
            work();
        }
    }

    /// <summary>Runs the queued actions in order, and waits for more, until the condition is true.</summary>
    public async Task RunUntilAsync(Func<bool> done, string what)
    {
        var end = DateTime.UtcNow + Limit;
        while (!done())
        {
            var left = end - DateTime.UtcNow;
            Assert.True(left > TimeSpan.Zero, $"Not in 30 s: {what}");
            if (await NextAsync(left))
            {
                Take();
            }
        }
    }

    /// <summary>Runs the queued actions in order until no action comes for the time.</summary>
    public async Task RunUntilQuietAsync(TimeSpan quiet)
    {
        while (await NextAsync(quiet))
        {
            Take();
        }
    }

    /// <summary>Waits until the queue has the count of actions, and runs none of them.</summary>
    public async Task WaitForQueuedAsync(int count)
    {
        var end = DateTime.UtcNow + Limit;
        while (queue.Reader.Count < count)
        {
            Assert.True(DateTime.UtcNow < end, $"{count} actions were not queued in 30 s.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Runs the next queued action.</summary>
    private void Take()
    {
        Assert.True(queue.Reader.TryRead(out var work));
        work();
    }

    /// <summary>Waits until an action is in the queue, for at most the time. False when no action came.</summary>
    private async Task<bool> NextAsync(TimeSpan wait)
    {
        var test = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(test);
        timeout.CancelAfter(wait);
        try
        {
            return await queue.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!test.IsCancellationRequested)
        {
            return false;
        }
    }
}
