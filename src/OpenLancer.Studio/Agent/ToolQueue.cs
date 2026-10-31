using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The queue of the agent calls (section 5.5 of the MCP1 design). The SDK takes requests on other threads and runs
/// their handlers at the same time, but a session is for one thread. So each handler puts its call into this queue and
/// waits. The thread that owns the session runs the session part of one call in each <see cref="RunNext"/>. A tail runs
/// on a worker, and the next call can start while it runs.
/// </summary>
/// <remarks>
/// <para>The editor calls <see cref="RunNext"/> once in each frame, before the panels draw, with the reason why the call
/// must wait, for example an open field edit. The headless host calls it on its session thread after
/// <see cref="WaitForCall"/>.</para>
/// <para>A call that waits longer than <see cref="AgentLimits.WaitSeconds"/> ends with <c>busy</c> and the reason. A
/// cancel of a call that did not start removes it. A call whose session part started runs that part to its end.</para>
/// </remarks>
public sealed class ToolQueue : IDisposable
{
    private readonly object gate = new();
    private readonly LinkedList<Pending> waiting = new();
    private readonly Queue<long> accepted = new();
    private readonly SemaphoreSlim queued = new(0);
    private readonly AgentHost host;
    private readonly TimeProvider clock;
    private readonly AuditLog? audit;
    private bool disposed;

    /// <param name="clock">The clock of the waits, the rate and the tails. The tests give a fake clock.</param>
    /// <param name="audit">The audit log, or null (decision D728).</param>
    public ToolQueue(AgentHost host, TimeProvider? clock = null, AuditLog? audit = null)
    {
        this.host = host;
        this.clock = clock ?? TimeProvider.System;
        this.audit = audit;
    }

    /// <summary>The host whose session runs the calls.</summary>
    public AgentHost Host => host;

    /// <summary>The name of the client of the last call, as the client gives it, or null.</summary>
    public string? LastClient { get; private set; }

    /// <summary>The time of the last call, or null.</summary>
    public DateTimeOffset? LastCall { get; private set; }

    /// <summary>The number of calls that wait for their session part.</summary>
    public int Waiting
    {
        get
        {
            lock (gate)
                return waiting.Count;
        }
    }

    /// <summary>
    /// Puts a call into the queue. It can run on any thread. The task ends with the result, or as cancelled when the
    /// client cancels the call before its session part starts or the host closes the queue.
    /// </summary>
    /// <param name="tool">The name of a tool of <see cref="AgentTools.All"/>.</param>
    /// <param name="client">The name of the client program, for the status bar of the editor, or null.</param>
    /// <param name="progress">Gets the progress of the tail of the call, or null when the client did not ask for
    /// progress.</param>
    public Task<ToolResult> CallAsync(string tool, JsonObject? arguments, string? client, CancellationToken cancel,
        IProgress<ToolProgress>? progress = null)
    {
        var limits = host.Limits;
        Pending pending;
        lock (gate)
        {
            if (disposed)
                return Task.FromCanceled<ToolResult>(new CancellationToken(true));
            var now = clock.GetTimestamp();
            while (accepted.Count > 0 && clock.GetElapsedTime(accepted.Peek(), now) >= TimeSpan.FromSeconds(1))
                accepted.Dequeue();
            LastClient = client ?? LastClient;
            LastCall = clock.GetUtcNow();
            if (accepted.Count >= limits.CallsPerSecond)
            {
                var refused = ToolResult.Failure(EditErrorKind.Limit, "CallRate",
                    AgentTexts.Error("limit_calls", ("max", limits.CallsPerSecond)));
                // The call does not run, so its line has no arguments. A client that calls too often then cannot fill
                // the disk with its arguments.
                audit?.Write(clock.GetUtcNow(), tool, arguments, refused.Outcome, ran: false);
                return Task.FromResult(refused);
            }
            accepted.Enqueue(now);
            pending = new Pending(tool, arguments, now, cancel, progress ?? ToolProgress.None);
            pending.Node = waiting.AddLast(pending);
        }
        if (cancel.CanBeCanceled)
            pending.Registration = cancel.Register(() => Withdraw(pending));
        lock (gate)
        {
            if (!disposed)
                queued.Release();
        }
        return pending.Done.Task;
    }

    /// <summary>Waits until a call is in the queue, the time limit ends or the token cancels. For the session thread of
    /// a host without a window.</summary>
    /// <param name="limit">The longest wait, or null to wait without a limit.</param>
    /// <exception cref="OperationCanceledException">The token cancelled.</exception>
    public void WaitForCall(TimeSpan? limit, CancellationToken stop) =>
        queued.Wait(limit ?? Timeout.InfiniteTimeSpan, stop);

    /// <summary>
    /// Runs the session part of the first call, on the thread that owns the session. While <paramref name="waitReason"/>
    /// is not null, no call starts, and each call that waited too long ends with <c>busy</c> and the reason.
    /// </summary>
    /// <param name="waitReason">Why the calls must wait now, in strict STE, or null.</param>
    /// <returns>True when a session part ran.</returns>
    public bool RunNext(string? waitReason = null)
    {
        if (waitReason != null)
        {
            foreach (var pending in Expired())
                Expire(pending, waitReason);
            return false;
        }
        Pending? next;
        lock (gate)
        {
            if (waiting.First is not { } first)
                return false;
            next = first.Value;
            waiting.RemoveFirst();
            next.Node = null;
        }
        next.Registration.Dispose();
        ToolStep step;
        try
        {
            step = AgentTools.Run(host, next.Tool, next.Arguments);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The tool table gives a fault of a handler as a result. This is the last guard, so each call ends with a
            // result and a line of the audit log.
            Finish(next, AgentTools.Failed(next.Tool, e));
            return true;
        }
        if (step.Result is { } result)
            Finish(next, result);
        else
            StartTail(next, step.Tail!);
        return true;
    }

    /// <summary>Closes the queue: each call that waits ends as cancelled, and a new call ends as cancelled at once.
    /// The session part that runs now ends first.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var pending in waiting)
                pending.Done.TrySetCanceled();
            waiting.Clear();
            queued.Dispose();
        }
    }

    /// <summary>Removes a call that did not start, after the client cancelled it.</summary>
    private void Withdraw(Pending pending)
    {
        lock (gate)
        {
            if (pending.Node == null)
                return;
            waiting.Remove(pending.Node);
            pending.Node = null;
        }
        pending.Done.TrySetCanceled(pending.Cancel);
    }

    /// <summary>Removes the calls that waited longer than the wait limit.</summary>
    private List<Pending> Expired()
    {
        var expired = new List<Pending>();
        lock (gate)
        {
            var limit = TimeSpan.FromSeconds(host.Limits.WaitSeconds);
            var now = clock.GetTimestamp();
            for (var node = waiting.First; node != null;)
            {
                var after = node.Next;
                if (clock.GetElapsedTime(node.Value.Queued, now) >= limit)
                {
                    waiting.Remove(node);
                    node.Value.Node = null;
                    expired.Add(node.Value);
                }
                node = after;
            }
        }
        return expired;
    }

    /// <summary>Ends a call that waited too long with <c>busy</c>. The host learns the refusal of a tool that changes
    /// data for its Console, as for each other refusal (section 8.5, item 1 of the MCP1 design). It runs outside the
    /// lock of the queue, on the thread that owns the session.</summary>
    private void Expire(Pending pending, string reason)
    {
        pending.Registration.Dispose();
        var error = new EditError(EditErrorKind.Busy, "Waited",
            AgentTexts.Error("busy", ("reason", reason), ("seconds", host.Limits.WaitSeconds)));
        if (AgentTools.Find(pending.Tool) is { Changes: true } tool && !AgentTools.AsksPreview(pending.Arguments))
            host.Refused(tool.Name, error);
        Finish(pending, ToolResult.Failure(error));
    }

    /// <summary>Runs the tail of a call on a worker, with the time limit of the tail of its tool (section 8.4 of the MCP1
    /// design). An export has a longer limit than a check or an image.</summary>
    private void StartTail(Pending pending, Func<CancellationToken, IProgress<ToolProgress>, Task<ToolResult>> tail)
    {
        var limit = AgentTools.Find(pending.Tool)!.TailLimit(host.Limits);
        var seconds = (long)limit.TotalSeconds;
        // The time limit starts with the session part, not when the worker starts.
        var timeout = new CancellationTokenSource(limit, clock);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, pending.Cancel);
        _ = Task.Run(async () =>
        {
            try
            {
                Finish(pending, await tail(linked.Token, pending.Progress).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Finish(pending, ToolResult.Failure(EditErrorKind.Limit, "TailTime",
                    AgentTexts.Error("limit_tail", ("seconds", seconds))));
            }
            catch (OperationCanceledException)
            {
                pending.Done.TrySetCanceled(pending.Cancel);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Finish(pending, AgentTools.Failed(pending.Tool, e));
            }
            finally
            {
                linked.Dispose();
                timeout.Dispose();
            }
        });
    }

    /// <summary>Ends a call with its result. A result above the size limit becomes the error <c>limit</c>.</summary>
    private void Finish(Pending pending, ToolResult result)
    {
        result = result.Limited(host.Limits);
        audit?.Write(clock.GetUtcNow(), pending.Tool, pending.Arguments, result.Outcome);
        pending.Done.TrySetResult(result);
    }

    /// <summary>A call in the queue.</summary>
    private sealed class Pending(string tool, JsonObject? arguments, long queued, CancellationToken cancel,
        IProgress<ToolProgress> progress)
    {
        public string Tool { get; } = tool;

        public JsonObject? Arguments { get; } = arguments;

        /// <summary>The time stamp of the clock when the call came.</summary>
        public long Queued { get; } = queued;

        public CancellationToken Cancel { get; } = cancel;

        /// <summary>The progress of the tail of the call.</summary>
        public IProgress<ToolProgress> Progress { get; } = progress;

        /// <summary>The place of the call in the queue, or null after it left the queue.</summary>
        public LinkedListNode<Pending>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        public TaskCompletionSource<ToolResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
