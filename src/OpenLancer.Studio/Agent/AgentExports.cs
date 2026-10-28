using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The export of the agent that runs now (tool <c>export_mod</c>, section 7.6 of the MCP1 design). One export runs at a
/// time. The Export dialog and the Mods window of the editor wait while it runs (review finding R12 of MCP1-2). Each
/// member is safe on each thread.
/// </summary>
/// <remarks>A stop ends the export before its write. A write that started always goes on to its end and records the
/// export in the publish history. The Export dialog does the same (sections 5.5, item 6 and 8.5, item 6 of the MCP1
/// design).</remarks>
public sealed class AgentExports
{
    private readonly object gate = new();
    private ExportRun? current;

    /// <summary>True from the session part of an export call until the end of its prepare or its write.</summary>
    public bool Running
    {
        get
        {
            lock (gate)
                return current != null;
        }
    }

    /// <summary>Stops the export that runs before its write. A write that started goes on.</summary>
    public void Stop()
    {
        lock (gate)
            current?.Stop();
    }

    /// <summary>Waits until the export that runs ends, for example before the server without a window ends its process.
    /// A prepare that the stop ended before its write writes nothing.</summary>
    public void Wait()
    {
        Task? task;
        lock (gate)
            task = current?.Task;
        try
        {
            task?.Wait();
        }
        catch (AggregateException)
        {
            // The tail of the call gives the fault of the export.
        }
    }

    /// <summary>Takes the place of the one export, or gives null when an export runs.</summary>
    internal ExportRun? Begin()
    {
        lock (gate)
        {
            if (current != null)
                return null;
            return current = new ExportRun(this);
        }
    }

    private void Ended(ExportRun run)
    {
        lock (gate)
        {
            if (current == run)
                current = null;
        }
    }

    /// <summary>One export: its work on a worker, and the stop before its write.</summary>
    internal sealed class ExportRun(AgentExports owner)
    {
        private readonly CancellationTokenSource beforeWrite = new();
        private int writes;

        /// <summary>The work, or null before <see cref="Start{T}"/>.</summary>
        public Task? Task { get; private set; }

        /// <summary>True after the write started. A stop then has no effect.</summary>
        public bool Writes => Volatile.Read(ref writes) == 1;

        /// <summary>Stops the export before its write.</summary>
        public void Stop()
        {
            lock (owner.gate)
            {
                if (writes == 0)
                    beforeWrite.Cancel();
            }
        }

        /// <summary>Starts the work on a worker. The work gets a token that tells it to stop before its write, and calls
        /// <see cref="Write"/> before the write. The run ends with the work.</summary>
        public Task<T> Start<T>(Func<CancellationToken, T> work)
        {
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    return work(beforeWrite.Token);
                }
                finally
                {
                    owner.Ended(this);
                }
            });
            Task = task;
            return task;
        }

        /// <summary>Marks the start of the write, or gives false when a stop came first. After it, a stop has no
        /// effect.</summary>
        public bool Write()
        {
            lock (owner.gate)
            {
                if (beforeWrite.IsCancellationRequested)
                    return false;
                writes = 1;
                return true;
            }
        }

        /// <summary>Ends a run whose work never started, for example after a fault of the session part.</summary>
        public void Cancel() => owner.Ended(this);
    }
}
