using System;
using System.ComponentModel;
using System.Diagnostics;
using LibreLancer;
using OpenLancer.Studio;

namespace OpenLancer.Editor.Editing;

/// <summary>The output of a process of <see cref="ToolProcess"/>.</summary>
/// <param name="StartError">Why the process did not start, or null. The other values are then empty.</param>
internal sealed record ToolOutput(int ExitCode, string Output, string Errors, string? StartError);

/// <summary>
/// Runs one helper process at a time, for example <c>oltool</c>, and gives its output on the UI thread. A new start
/// stops the running process, and the output of a stopped process is dropped.
/// </summary>
/// <remarks>
/// <para>The engine log and the material map are process-wide, so the editor runs a second data load in a process of
/// its own: the validation of a save (<see cref="ValidationRunner"/>) and the base run of the Console
/// (<see cref="Logs.BaseRunRunner"/>).</para>
/// <para>A thread of its own reads the output and waits for the exit, and a second thread of its own reads the error
/// output (see <see cref="OwnThread"/>). An async read of a redirected output holds a thread of the thread pool until
/// data comes, so a process that runs for seconds would hold two pool threads.</para>
/// </remarks>
internal sealed class ToolProcess : IDisposable
{
    private Process? process;

    // Changes when a process starts or stops, so that the output of a stopped process is dropped.
    private int generation;

    /// <summary>True while a process runs.</summary>
    public bool Running => process != null;

    /// <summary>Starts a process and stops the running one.</summary>
    /// <param name="done">Gets the output on the UI thread. It does not run for a process that stops. When the process
    /// does not start, it runs at once.</param>
    public void Start(ProcessStartInfo info, IUIThread ui, Action<ToolOutput> done)
    {
        Stop();
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        Process started;
        try
        {
            started = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            done(new ToolOutput(-1, "", "", $"{info.FileName} did not start: {e.Message}"));
            return;
        }

        process = started;
        var id = ++generation;
        OwnThread.Run(() =>
        {
            ToolOutput output;
            try
            {
                var errors = OwnThread.Run(started.StandardError.ReadToEnd);
                var text = started.StandardOutput.ReadToEnd();
                started.WaitForExit();
                output = new ToolOutput(started.ExitCode, text, errors.GetAwaiter().GetResult(), null);
            }
            finally
            {
                started.Dispose();
            }

            ui.QueueUIThread(() =>
            {
                if (id != generation)
                {
                    return;
                }

                process = null;
                done(output);
            });
        });
    }

    /// <summary>Stops the running process. Its output is dropped.</summary>
    /// <returns>True when a process ran.</returns>
    public bool Stop()
    {
        if (process == null)
        {
            return false;
        }

        generation++;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process ended already. Its task disposes it.
        }

        process = null;
        return true;
    }

    public void Dispose() => Stop();
}
