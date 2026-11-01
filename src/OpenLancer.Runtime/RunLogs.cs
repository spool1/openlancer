using System;
using System.IO;
using System.Reflection;
using LibreLancer;
using OpenLancer.Data;

namespace OpenLancer.Runtime;

/// <summary>
/// The log files of the OpenLancer programs. The engine writes the log of a program to <c>&lt;program&gt;.log.txt</c>
/// in <see cref="Folder"/>, and makes the file again at each start. When the log reaches its size limit, the engine
/// copies it to the old file <c>&lt;program&gt;.log.old.txt</c> (<see cref="FLLog.OldSpewPath"/>). So a start would
/// replace the log of the last run, and a restart after a crash would lose the log that a bug report needs.
/// <see cref="KeepLastRun(Assembly)"/> keeps that log before the engine starts the new one.
/// </summary>
public static class RunLogs
{
    private const string LogSuffix = ".log.txt";
    private const string PreviousSuffix = ".log.previous.txt";

    /// <summary>The log folder of OpenLancer, for example <c>%LOCALAPPDATA%\OpenLancer\logs</c> on Windows.</summary>
    public static string Folder => AppHandler.LogFolder(Product.Name);

    /// <summary>The log file of a program, for example <c>OpenLancer.Play.log.txt</c> in <see cref="Folder"/>.</summary>
    /// <param name="program">The name of the program assembly, which names its log, for example
    /// <c>OpenLancer.Play</c>.</param>
    public static string PathOf(string program) => Path.Combine(Folder, program + LogSuffix);

    /// <summary>The log file of the last run of a program, for example <c>OpenLancer.Play.log.previous.txt</c> in
    /// <see cref="Folder"/>.</summary>
    /// <param name="program">The name of the program assembly.</param>
    public static string PreviousPathOf(string program) => Path.Combine(Folder, program + PreviousSuffix);

    /// <summary>Keeps the log of the last run of a program in its previous log. Call it before
    /// <c>AppHandler.Run</c>, which starts the new log.</summary>
    /// <param name="program">The program assembly, for example <c>typeof(Program).Assembly</c>. The engine names the log
    /// with the name of this assembly, so the call takes the name with the same rule.</param>
    public static void KeepLastRun(Assembly program) => KeepLastRun(program.GetName().Name!, Folder);

    /// <summary>
    /// Keeps the log of the last run of a program in a folder. The log moves to the previous log, and its old file moves
    /// to the old file of the previous log, <c>&lt;program&gt;.log.previous.old.txt</c>. A log without an old file
    /// deletes the old file of the previous log, so the two previous files always come from one run.
    /// </summary>
    /// <remarks>Nothing changes without a folder or a log, for example at the first start. On Windows, nothing changes
    /// either when another process writes the log, for example a second editor. The engine opens the log without delete
    /// share, so the move fails, and the engine then gives the new process a log with a number. The call runs before the
    /// crash handler of the engine, so it catches the file errors and does not throw them.</remarks>
    internal static void KeepLastRun(string program, string folder)
    {
        var log = Path.Combine(folder, program + LogSuffix);
        var previous = Path.Combine(folder, program + PreviousSuffix);
        try
        {
            File.Move(log, previous, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var old = FLLog.OldSpewPath(log);
        var previousOld = FLLog.OldSpewPath(previous);
        try
        {
            if (File.Exists(old))
            {
                File.Move(old, previousOld, overwrite: true);
            }
            else
            {
                File.Delete(previousOld);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The previous log has the newest lines of the last run. Its older lines stay in the old file.
        }
    }
}
