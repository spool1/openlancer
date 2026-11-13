using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Play;
using OpenLancer.Studio;

namespace OpenLancer.Editor.Play;

/// <summary>
/// The game process of a play (sections 6.4 and 9.4 of the M4 design). It writes the commands of the editor on the
/// standard input of the game. It gives the events of the game and the warnings and errors of the engine log to the UI
/// thread.
/// </summary>
/// <remarks>
/// <para>On Windows, the process is in a job object that ends the process when the job closes (decision D53, see
/// <see cref="ProcessJob"/>). So the game ends at <see cref="Dispose"/>, and also when the editor process ends in any
/// way. When Windows does not give the job, <see cref="Dispose"/> kills the process.</para>
/// <para>A thread of its own reads the output (see <see cref="OwnThread"/>). It drops the lines that are not events,
/// warnings or errors: the game log of the engine keeps them. After the end of the output and of the process, it gives
/// the exit to the UI thread. So each event and each log line comes before the exit. A second thread of its own reads
/// the error output.</para>
/// </remarks>
internal sealed class PlayProcess : IDisposable
{
    /// <summary>The lines of the error output that the exit gives. The runtime writes the cause of a crash there,
    /// for example a failed assert, and the stack trace after it.</summary>
    private const int ErrorLines = 3;

    private readonly Process process;
    private readonly StreamWriter input;
    private readonly ProcessJob? job;
    private readonly Task reader;

    // Kill, Send and the end of the reader use the process object on two threads.
    private readonly object processLock = new();
    private bool ended;

    private PlayProcess(Process process, StreamWriter input, IUIThread ui, Action<PlayEvent> onEvent,
        Action<string> onLogLine, Action<int, string> onExit)
    {
        this.process = process;
        this.input = input;
        job = ProcessJob.For(process);
        reader = OwnThread.Run(() => Read(ui, onEvent, onLogLine, onExit));
    }

    /// <summary>The program and the arguments that start the play program next to the editor.</summary>
    public static ProcessStartInfo Command(PlayArgs args)
    {
        var info = new ProcessStartInfo(ProgramPaths.Play);
        foreach (var argument in args.ToArguments())
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>Starts the program with redirected input and output, and without a console window. The game window
    /// shows, because it is not a console window.</summary>
    /// <param name="onEvent">Gets each event on the UI thread.</param>
    /// <param name="onLogLine">Gets each warning or error line of the engine log, and each line that starts a load, on
    /// the UI thread (see <see cref="ConsoleLog.ParseGameLine"/>).</param>
    /// <param name="onExit">Gets the exit code and the first lines of the error output on the UI thread, after the
    /// last event.</param>
    /// <exception cref="Win32Exception">The program did not start, for example because it is missing.</exception>
    public static PlayProcess Start(ProcessStartInfo info, IUIThread ui, Action<PlayEvent> onEvent,
        Action<string> onLogLine, Action<int, string> onExit)
    {
        info.RedirectStandardInput = true;
        var process = StartWithOutput(info);
        return new PlayProcess(process, process.StandardInput, ui, onEvent, onLogLine, onExit);
    }

    /// <summary>
    /// Starts the program as <see cref="Start(ProcessStartInfo, IUIThread, Action{PlayEvent}, Action{string}, Action{int, string})"/>
    /// does, but <see cref="Send"/> writes to <paramref name="input"/>, not to the standard input of the program. The
    /// tests give a pipe whose read end stays open after the game ends, so that a write to it succeeds. The input pipe of
    /// a game can also take a write for a moment after the exit (see <see cref="Send"/>).
    /// </summary>
    internal static PlayProcess Start(ProcessStartInfo info, StreamWriter input, IUIThread ui,
        Action<PlayEvent> onEvent, Action<string> onLogLine, Action<int, string> onExit)
    {
        info.RedirectStandardInput = false;
        return new PlayProcess(StartWithOutput(info), input, ui, onEvent, onLogLine, onExit);
    }

    /// <summary>Starts the program with redirected output and error output, and without a console window.</summary>
    private static Process StartWithOutput(ProcessStartInfo info)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return Process.Start(info) ?? throw new InvalidOperationException($"{info.FileName} did not start.");
    }

    /// <summary>Writes a command on the standard input of the game.</summary>
    /// <returns>False when the game ended or its input is closed. It never throws for an ended game (review finding
    /// R6).</returns>
    /// <remarks>A write to the input is no sign that the game runs. Windows gives the exit code of a process before it
    /// closes the handles of the process, and the reader gives the exit when the exit code is there. So the input pipe
    /// can take a write after the exit came. The process tells the end first.</remarks>
    public bool Send(PlayCommand command)
    {
        lock (processLock)
        {
            if (ended || process.HasExited)
            {
                return false;
            }
        }

        try
        {
            input.WriteLine(PlayProtocol.Write(command));
            input.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Closes the standard input. The game exits at the end of its input, as at stop.</summary>
    public void CloseInput()
    {
        try
        {
            input.Dispose();
        }
        catch (IOException)
        {
            // The game ended, so the last write of the input cannot reach it.
        }
    }

    /// <summary>Waits for the end of the game and of its output.</summary>
    /// <returns>False when the game did not end in the time.</returns>
    public bool WaitForExit(TimeSpan limit) => reader.Wait(limit);

    /// <summary>Ends the game process at once. It does nothing when the game ended.</summary>
    public void Kill()
    {
        lock (processLock)
        {
            if (ended)
            {
                return;
            }

            try
            {
                process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // The process ends now. The reader gives its exit.
            }
        }
    }

    /// <summary>Closes the job, which ends the game. Without a job, it kills the game.</summary>
    public void Dispose()
    {
        if (job != null)
        {
            job.Dispose();
        }
        else
        {
            Kill();
        }

        CloseInput();
    }

    private void Read(IUIThread ui, Action<PlayEvent> onEvent, Action<string> onLogLine, Action<int, string> onExit)
    {
        var errorOutput = process.StandardError;
        var errors = OwnThread.Run(() => FirstLines(errorOutput));
        using (var output = process.StandardOutput)
        {
            while (output.ReadLine() is { } line)
            {
                if (PlayProtocol.ReadEvent(line) is { } e)
                {
                    ui.QueueUIThread(() => onEvent(e));
                }
                else if (ConsoleLog.ParseGameLine(line) != null)
                {
                    ui.QueueUIThread(() => onLogLine(line));
                }
            }
        }

        var errorText = errors.GetAwaiter().GetResult();
        process.WaitForExit();
        int code;
        lock (processLock)
        {
            code = process.ExitCode;
            ended = true;
            process.Dispose();
        }

        ui.QueueUIThread(() => onExit(code, errorText));
    }

    /// <summary>The first non-empty lines of the error output, in one line. It reads the output to its end.</summary>
    private static string FirstLines(StreamReader errors)
    {
        var lines = new List<string>();
        using (errors)
        {
            while (errors.ReadLine() is { } line)
            {
                if (lines.Count < ErrorLines && line.Trim() is { Length: > 0 } text)
                {
                    lines.Add(text);
                }
            }
        }

        return string.Join(" ", lines);
    }
}
