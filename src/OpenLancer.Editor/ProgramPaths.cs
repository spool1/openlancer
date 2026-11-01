using System;
using System.IO;
using OpenLancer.Runtime;

namespace OpenLancer.Editor;

/// <summary>
/// The programs that the editor starts. The build puts each one next to the editor, because the editor project
/// references its project.
/// </summary>
internal static class ProgramPaths
{
    private const string PlayName = "OpenLancer.Play";

    /// <summary>oltool, which validates a save.</summary>
    public static string Oltool => NextToEditor("oltool");

    /// <summary>The game process of Play.</summary>
    public static string Play => NextToEditor(PlayName);

    /// <summary>The log file of the game process of Play. The engine writes it with the name of the program, in the
    /// log folder of the product.</summary>
    public static string PlayLog => RunLogs.PathOf(PlayName);

    /// <summary>The previous log of the game process of Play. Each start of the game moves the log of the last run to
    /// this file (<see cref="RunLogs.KeepLastRun(System.Reflection.Assembly)"/>).</summary>
    public static string PreviousPlayLog => RunLogs.PreviousPathOf(PlayName);

    private static string NextToEditor(string name) =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
}
