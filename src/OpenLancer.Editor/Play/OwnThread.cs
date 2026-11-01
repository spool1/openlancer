using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenLancer.Editor.Play;

/// <summary>
/// Runs work that blocks while a program of a play runs, on a thread of its own and not on a thread of the thread pool.
/// The read of the output of a game and the wait for its exit are such work. On a pool thread, this work takes a thread
/// from the other work of the editor for the full play. When the computer is busy, the pool can have no free thread for
/// many seconds, and work that waits for one comes late. So the exit of a game would come late too.
/// </summary>
internal static class OwnThread
{
    /// <summary>Starts the work on a new background thread. An exception of the work goes to the task, as for
    /// <see cref="Task.Run(Action)"/>.</summary>
    public static Task Run(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Starts the work on a new background thread, and gives its result.</summary>
    public static Task<T> Run<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
