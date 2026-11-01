using System;
using System.Threading;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Runtime;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Play;

/// <summary>
/// The game process of Play (section 10.1 of the M4 design). The editor starts it with the arguments of
/// <see cref="PlayArgs"/>. The game loads the profile through the mod overlay, waits for the start command of the
/// editor and starts the player at the start of the arguments. <see cref="PlayProtocol"/> gives the commands and the
/// events.
/// </summary>
internal static class Program
{
    // 1 after the first crash report. A crash on the UI thread of a Debug build gets to both reports.
    private static int crashReported;

    [STAThread]
    private static int Main(string[] args)
    {
        PlayArgs play;
        try
        {
            play = PlayArgs.Parse(args);
        }
        catch (FormatException e)
        {
            PlayHost.Send(new PlayEvent.Error(e.Message, Crash: false));
            return PlayExitCodes.Arguments;
        }

        // An unhandled exception ends the process, also on the data loader thread and on the server thread, which have
        // no catch. So the editor gets the cause before the end.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject);

        // The engine starts a new log, so keep the log of the last run first. A crash report of the editor that waits
        // until this start names the previous log.
        RunLogs.KeepLastRun(typeof(Program).Assembly.GetName().Name!);
        PlayHost? host = null;
        // Null until the play gives a code. The engine can return before it runs the play (PlayExitCodes.NotStarted).
        int? exitCode = null;
        AppHandler.Run(Product.Name, () =>
        {
            try
            {
                var view = LoadView(play);
                if (view == null)
                {
                    exitCode = PlayExitCodes.Data;
                    return;
                }

                host = new PlayHost(play, view.Value.Profile, view.Value.Overlay);
                exitCode = host.Run();
            }
            catch (Exception e) when (CrashOnUIThread(e))
            {
                // The filter returns false, so this block never runs.
            }
        }, () => host?.Crashed());
        return exitCode ?? NotStarted();

        // A Release build of AppHandler catches an exception of the UI thread, shows its crash window and returns. The
        // filter runs before that catch. It returns false, so the exception goes on as before: to that crash window in a
        // Release build, and out of Main to the handler of unhandled exceptions in a Debug build.
        bool CrashOnUIThread(Exception e)
        {
            ReportCrash(e);
            exitCode = PlayExitCodes.Crash;
            return false;
        }
    }

    /// <summary>The profile and the data of the play (decision D38), or null after an error event.</summary>
    private static (ModProfile Profile, ModOverlay Overlay)? LoadView(PlayArgs play)
    {
        try
        {
            var profile = ModProfile.Load(play.Profile);
            if (play.ActiveMod != null && profile.IndexOf(play.ActiveMod) < 0)
                return Refuse($"The profile {profile.Name} has no mod '{play.ActiveMod}'.");
            // A mission play adds its test layer after the mods of the play (section 5.4 of the M6c design).
            var overlay = PlayView.Build(profile, play.ActiveMod, play.TestLayer);
            if (PlayView.MergeErrors(overlay) is { } errors)
                return Refuse(errors);
            return (profile, overlay);
        }
        catch (ModLoadException e)
        {
            // The profile or a mod is not valid, or a save of the editor in a mod of the play is not complete.
            return Refuse(e.Message);
        }
    }

    private static (ModProfile, ModOverlay)? Refuse(string reason)
    {
        PlayHost.Send(new PlayEvent.Error(reason, Crash: false));
        return null;
    }

    /// <summary>
    /// Writes the whole exception to the game log and sends the error event with <c>crash</c>, once for each process.
    /// The event has only the message, so the log keeps the type and the stack of the exception (section 11.4).
    /// </summary>
    private static void ReportCrash(object exception)
    {
        if (Interlocked.Exchange(ref crashReported, 1) != 0)
            return;
        FLLog.Error(PlayHost.LogComponent, $"The game crashed: {exception}");
        // FLLog writes from a queue on its own thread, and the process can end when this method returns.
        FLLog.Flush();
        PlayHost.Send(new PlayEvent.Error(exception is Exception e ? e.Message : $"{exception}", Crash: true));
    }

    /// <summary>The engine returned before the play ran. The engine wrote the cause to the game log.</summary>
    private static int NotStarted()
    {
        PlayHost.Send(new PlayEvent.Error("The game did not start. The game log gives the cause.", Crash: false));
        return PlayExitCodes.NotStarted;
    }
}
