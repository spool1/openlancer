using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenLancer.Studio;

/// <summary>
/// Runs blocking work that a frame of the editor, an agent call or a test waits for. The work gets a thread of its own,
/// not a thread of the thread pool. Examples are the read of the output of a game, the wait for the exit of a process,
/// a check that waits for a lock, and the write of an export. On a pool thread, this work takes a thread from the other
/// work of the program while it blocks. When the computer is busy, the pool can have no free thread for many seconds,
/// and work that waits for one comes late. So the exit of a game or the end of a check would come late too.
/// </summary>
internal static class OwnThread
{
    /// <summary>Starts the work on a new background thread. An exception of the work goes to the task, as for
    /// <see cref="Task.Run(Action)"/>.</summary>
    public static Task Run(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Starts the work on a new background thread, and gives its result.</summary>
    /// <param name="cancel">Cancels the task when it comes before the start of the work, as for
    /// <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>.</param>
    public static Task<T> Run<T>(Func<T> work, CancellationToken cancel = default) =>
        Task.Factory.StartNew(work, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
