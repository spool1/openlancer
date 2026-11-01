using System;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Runtime;

namespace OpenLancer.Editor;

internal static class Program
{
    /// <summary>
    /// Starts the editor. The command line is in <see cref="EditorOptions"/>. A wrong command line of the bench writes
    /// the message to the error output and gives the exit code 2. A wrong command line of a normal start opens the
    /// editor with the message on the Welcome screen.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        EditorOptions options;
        try
        {
            options = EditorOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        // The engine starts a new log, so keep the log of the last session first.
        RunLogs.KeepLastRun(typeof(Program).Assembly.GetName().Name!);
        EditorWindow? window = null;
        AppHandler.Run(Product.Name, () =>
        {
            window = new EditorWindow(options);
            window.Run();
        }, () => window?.Crashed());
        return window?.ExitCode ?? 0;
    }
}
