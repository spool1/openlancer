using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using OpenLancer.Studio;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The waits of the tests for a process that a test starts. They hold no thread of the thread pool while the process
/// runs (issue 71). An await for the exit gives the thread back to the pool. The read of a redirected output blocks
/// until the process ends, so it gets a thread of its own. On the pool, the read would hold a pool thread, and a full
/// pipe would stop the process when the pool has no free thread.
/// </summary>
internal static class ProcessWaits
{
    /// <summary>Waits for the exit of the process. The test fails with the message at the limit.</summary>
    public static async Task ExitAsync(this Process process, TimeSpan limit, string message)
    {
        var token = TestContext.Current.CancellationToken;
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(limit, token);
        }
        catch (TimeoutException)
        {
            Assert.Fail(message);
        }
    }

    /// <summary>Reads a redirected output to its end on a thread of its own.</summary>
    public static Task<string> ReadToEndOnOwnThread(this StreamReader output) => OwnThread.Run(output.ReadToEnd);
}
