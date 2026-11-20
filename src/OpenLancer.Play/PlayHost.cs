using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Client;
using LibreLancer.Data.IO;
using LibreLancer.ImageLib;
using LibreLancer.Net;
using LibreLancer.Server;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Runtime;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Play;

/// <summary>
/// Runs the game of a play (sections 10.1 to 10.4 and 12.2 of the M4 design). The game reads its data through the
/// overlay of the play (OL-0020). After the load, it waits until the editor sends start. Then it starts the player
/// from a save in code. A reload loads the data again and continues at the same place. A mission play starts from its
/// start save, and a restart loads the data again and starts the mission again from the start save (section 5.8 of the
/// M6c design).
/// </summary>
/// <remarks>
/// <para>A thread reads the commands of the editor from the standard input and gives each one to the UI thread. The end
/// of the input exits the game, as stop does. The events go to the standard output. A timer takes the trigger changes of
/// the mission observer at most each 100 ms and writes them as mission events (section 6.2 of the M6c design).</para>
/// <para>A capture takes the next frame from the back buffer of the game. That frame has no debug windows. A task
/// writes the PNG file in the capture folder of the play folder and then sends captured.</para>
/// </remarks>
internal sealed class PlayHost
{
    /// <summary>The component of the lines that the play process writes to the game log.</summary>
    public const string LogComponent = "Play";

    /// <summary>The time between two lines of trigger changes (section 6.2 of the M6c design).</summary>
    private static readonly TimeSpan MissionLineTime = TimeSpan.FromMilliseconds(100);

    /// <summary>The writer of the mission lines of the process. An error event ends it, so no mission line follows the
    /// error.</summary>
    private static MissionLines? missionLines;

    private readonly PlayArgs args;
    private readonly FreelancerGame game;

    /// <summary>The file system of the game for its whole life. The resource manager holds it, so a reload gives it the
    /// files of the new overlay.</summary>
    private readonly FileSystem files;

    /// <summary>Gets the trigger changes of each mission runtime of the plays of this process (OL-0070).</summary>
    private readonly MissionObserver observer = new();

    // Only the UI thread uses these fields.
    private bool startReceived;

    /// <summary>The place of the current play: the start of the arguments, or the place of the start save of a mission
    /// play.</summary>
    private PlayStart place;
    private int exitCode = PlayExitCodes.Ok;

    /// <summary>The capture requests that wait for the next frame, oldest first.</summary>
    private readonly List<int> captures = [];

    /// <summary>True when the debug view showed before the first capture request that waits. The frame of a capture
    /// has no debug windows, and the view shows again after that frame.</summary>
    private bool debugBeforeCapture;

    /// <summary>The screenshot handler of the engine. It saves the screenshots of the player (see
    /// <see cref="OnScreenshot"/>).</summary>
    private readonly ScreenshotSaveHandler? screenshots;

    /// <summary>The file name of the screenshot requests of the captures. It marks a frame of a capture, and no file gets
    /// this name.</summary>
    private readonly string captureMark;

    /// <summary>The server of the current play: null before the start and during a reload.</summary>
    private EmbeddedServer? server;

    /// <summary>True when the player of the current play arrived in space or at a base. Only then can a reload save
    /// the player.</summary>
    private bool arrived;

    /// <summary>
    /// Makes the play folder and the game. The game uses the config of the play folder (decision D41), in a window
    /// and with the debug view.
    /// </summary>
    public PlayHost(PlayArgs args, ModProfile profile, ModOverlay overlay)
    {
        this.args = args;
        place = args.Start;
        // The engine config and the save writer of the server make no folder.
        PlayFolder.Create(args.Profile);
        var config = GameConfig.Create(true, () => PlayFolder.ConfigPath(args.Profile));
        // A new config file has an empty freelancer_path, and the INI reader logs an error for that value at each
        // start. So the file keeps the base folder of the profile.
        if (config.FreelancerPath != profile.BaseFolder)
        {
            config.FreelancerPath = profile.BaseFolder;
            config.Save();
        }

        // The host does not save these values. The options of the game save the config, so the other values stay
        // between plays.
        config.Settings.FullScreen = false;
        config.Settings.Debug = true;
        // OL-0020: the game reads the base data and the mods through this one file system object.
        files = overlay.CreateFileSystem();
        config.CustomFileSystem = () => files;
        config.CustomState = FirstState;
        game = new FreelancerGame(config)
        {
            Title = args.ActiveMod == null
                ? $"OpenLancer Play - {profile.Name}"
                : $"OpenLancer Play - {profile.Name} [{args.ActiveMod}]",
        };
        // The engine writes each screenshot through this one handler on a task of its own, so the host cannot know when
        // that file is complete. The host takes the handler, writes the frames of the captures itself and gives the
        // other screenshots to the handler of the engine.
        screenshots = game.ScreenshotSave;
        game.ScreenshotSave = OnScreenshot;
        captureMark = Path.Combine(PlayFolder.CapturesPath(args.Profile), "next-frame");
    }

    /// <summary>Writes an event for the editor on the standard output. <see cref="Console.Out"/> is synchronized and
    /// writes the line in one call. So each thread can send an event, and an event line does not mix with another line.
    /// An error event ends the mission lines first, because the game exits after it.</summary>
    public static void Send(PlayEvent e)
    {
        if (e is PlayEvent.Error)
            missionLines?.Close();
        Console.Out.WriteLine(PlayProtocol.Write(e));
    }

    /// <summary>Runs the game until it exits.</summary>
    /// <returns>The exit code of the process, or null when the engine returned before its first frame. The engine
    /// returns so when SDL, the game window or its graphics context does not start, and it writes the cause to the game
    /// log.</returns>
    public int? Run()
    {
        // A background thread, so that a reader that waits for a line does not keep the process after the game loop.
        var reader = new Thread(ReadCommands) { IsBackground = true, Name = "Play commands" };
        var firstFrame = false;
        // The reader starts in the first frame. Each command goes to the queue of the UI thread, and the queue sends an
        // SDL event. SDL must not get an event from another thread while it starts. The window exists in the first
        // frame, so it also gets its icon there.
        game.QueueUIThread(() =>
        {
            firstFrame = true;
            ProgramIcon.Apply(game);
            reader.Start();
        });
        // The engine gives the host no update of each frame while a state of the game runs. So a timer writes the lines
        // of trigger changes. Console.Out writes each line in one call from any thread.
        var lines = new MissionLines(observer, Console.Out);
        missionLines = lines;
        using (new Timer(_ => lines.Write(), null, MissionLineTime, MissionLineTime))
            game.Run();
        lines.Write();
        return firstFrame ? exitCode : null;
    }

    /// <summary>Lets the game restore the display before the crash window shows, as lancer does.</summary>
    public void Crashed() => game.Crashed();

    /// <summary>Gives each command of the editor to the UI thread. The end of the input exits the game, so a game
    /// whose editor closed the input does not stay.</summary>
    private void ReadCommands()
    {
        while (Console.In.ReadLine() is { } line)
        {
            if (PlayProtocol.ReadCommand(line) is not { } command)
            {
                FLLog.Warning(LogComponent, $"The command '{line}' is not known.");
                continue;
            }

            game.QueueUIThread(() => Execute(command));
            // The game exits. The reader stops too, so that it queues no work while SDL stops.
            if (command.Kind == PlayCommandKind.Stop)
                return;
        }

        game.QueueUIThread(game.Exit);
    }

    private void Execute(PlayCommand command)
    {
        switch (command.Kind)
        {
            case PlayCommandKind.Start:
                // The gate starts the play at its next update. A start that comes before the gate waits for it.
                startReceived = true;
                break;
            case PlayCommandKind.Stop:
                game.Exit();
                break;
            case PlayCommandKind.Reload:
                Reload(command.Save);
                break;
            case PlayCommandKind.Restart:
                Restart(command.Save);
                break;
            case PlayCommandKind.Capture:
                Capture(command.Request);
                break;
        }
    }

    /// <summary>Asks the engine for the next frame of the game, from its back buffer. More requests before that frame
    /// get the same frame. The image shows the game as the player sees it, so the debug view does not draw its windows
    /// until the frame of the capture ends. The engine keeps one screenshot request. A key of the player reads its input
    /// before the queue of the UI thread runs, so a screenshot of the player in the same frame is lost.</summary>
    private void Capture(int request)
    {
        if (captures.Count == 0)
        {
            debugBeforeCapture = game.Debug.Enabled;
            game.Debug.Enabled = false;
            game.Screenshot(captureMark);
        }
        captures.Add(request);
    }

    /// <summary>
    /// Gets each screenshot of the engine on the UI thread, at the end of a frame. A frame of a capture goes to a task
    /// that writes the images. Each other screenshot goes to the handler of the engine. The engine keeps one request
    /// for each frame, so a screenshot of the player can take the place of a capture. The capture then asks for the next
    /// frame. The engine clears its request after this handler, so the new request waits for the next turn of the UI
    /// thread.
    /// </summary>
    private void OnScreenshot(string? filename, int width, int height, Bgra8[] data)
    {
        if (filename != captureMark)
        {
            screenshots?.Invoke(filename, width, height, data);
            if (captures.Count > 0)
                game.QueueUIThread(() => game.Screenshot(captureMark));
            return;
        }

        var requests = captures.ToArray();
        captures.Clear();
        game.Debug.Enabled = debugBeforeCapture;
        var folder = PlayFolder.CapturesPath(args.Profile);
        Task.Run(() => WriteCaptures(folder, requests, width, height, data));
    }

    /// <summary>
    /// Writes the image of a frame for each capture request and sends its event. Each file gets another name first and
    /// then its final name, so the file is complete when the editor reads it. A request whose file the task cannot write
    /// gets capture-failed. The task has no caller, so without the event the editor waits until its time limit.
    /// </summary>
    /// <param name="data">The pixels of the frame, with the bottom row first, as the back buffer has them.</param>
    private static void WriteCaptures(string folder, int[] requests, int width, int height, Bgra8[] data)
    {
        byte[] png;
        try
        {
            using var stream = new MemoryStream();
            PNG.Save(stream, width, height, data, true);
            png = stream.ToArray();
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            foreach (var request in requests)
                Send(CaptureFailed(request, e));
            return;
        }

        foreach (var request in requests)
        {
            var name = PlayFolder.CaptureName(request);
            var path = Path.Combine(folder, name);
            try
            {
                File.WriteAllBytes(path + ".part", png);
                File.Move(path + ".part", path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Send(CaptureFailed(request, e));
                continue;
            }

            Send(new PlayEvent.Captured(request, name, width, height));
        }
    }

    private static PlayEvent.CaptureFailed CaptureFailed(int request, Exception e) =>
        new(request, $"The game could not write the image: {e.Message}");

    /// <summary>
    /// The first state after the load (<see cref="GameConfig.CustomState"/>). It does what <c>LuaMenu</c> does at its
    /// start, because a play does not open the main menu. Then it checks the start of the play and waits for the start
    /// command.
    /// </summary>
    private GameState FirstState(FreelancerGame _)
    {
        // SpaceGameplay reads the cursors and throws without them. Only RoomGameplay loads them itself.
        game.GameData.PopulateCursors();
        // The game states draw their own cursor, so the cursor of the system must not show on top of it.
        game.CursorKind = CursorKind.None;
        if (game.LoadTimer is { } timer)
        {
            timer.Stop();
            FLLog.Info("Game", $"Initial load took {timer.Elapsed.TotalSeconds} seconds");
            game.LoadTimer = null;
        }

        // The data is loaded, so the collector can use its low-latency mode now.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        var problem = PlaySave.Check(game.GameData.Items, args.Start, args.Package);
        if (problem == null)
        {
            Send(new PlayEvent.Waiting(SinceProcessStart()));
        }
        else
        {
            Send(new PlayEvent.Error(problem, Crash: false));
            exitCode = PlayExitCodes.Data;
            game.Exit();
        }

        return new PlayGateState(game, () => startReceived && problem == null, StartPlay);
    }

    /// <summary>
    /// Starts the server from the play save and waits for the spawn (section 10.3). This is the order of New Game in
    /// the main menu. The save is an object, not bytes, so the host starts the server and loads the save itself.
    /// </summary>
    private void StartPlay()
    {
        var clock = Stopwatch.StartNew();
        // The check of the first state read the start save, so it can be read.
        place = args.Start is PlayStart.FromSave fromSave ? PlayStartSave.Read(fromSave.Save).Place : args.Start;
        Begin(place, gameServer =>
        {
            var save = PlaySave.Create(gameServer, args.Start, args.Package);
            gameServer.Start();
            gameServer.LoadSaveGame(save);
        }, (player, dock) => Send(Spawned(player, dock, clock.ElapsedMilliseconds)));
    }

    /// <summary>
    /// Reloads the data of the play from the disk and continues at the same place with the same heading (section 12.2).
    /// The server saves the player and stops. A task loads the new data while the game shows the splash image. Then the
    /// game gets the new data, and a new server loads the save.
    /// </summary>
    /// <remarks>
    /// <see cref="PlayReload.Apply"/> decides if the game gets the new data. When it keeps the old data, the game
    /// continues at the same place and sends reload-failed. When the game left the play, for example with Quit to
    /// menu, the host sends reload-failed and changes nothing. When the save or the stop of the server does not end in
    /// its limit, the host sends an error and ends the process with <see cref="PlayExitCodes.Timeout"/>, because a
    /// server thread that does not end cannot continue the play. Another exception of <see cref="PlayReload.Leave"/>
    /// goes to the crash report (section 6.6), because the server can be stopped then.
    /// </remarks>
    private void Reload(int save)
    {
        if (server == null || !arrived)
        {
            Send(new PlayEvent.ReloadFailed(save, "The game can reload only while the player is in space or at a base."));
            return;
        }

        if (PlayReload.CheckLeave(server.Server) is { } refusal)
        {
            Send(new PlayEvent.ReloadFailed(save, refusal));
            return;
        }

        var clock = Stopwatch.StartNew();
        PlayPlace place;
        try
        {
            place = PlayReload.Leave(server.Server);
        }
        catch (TimeoutException e)
        {
            FLLog.Error(LogComponent, e.Message);
            FLLog.Flush();
            Send(new PlayEvent.Error(e.Message, Crash: false));
            Environment.Exit(PlayExitCodes.Timeout);
            return;
        }

        // The server stopped. The host drops it, and the state change drops its session, so the old play can go.
        server = null;
        arrived = false;
        var target = new PlayGame(files, game.GameData, game.Ui.Data, game.Saves);
        // A reload of a mission play reads its test layer again, so the saved triggers of the setup still exist (section
        // 5.4 of the M6c design).
        game.ChangeState(new PlayReloadState(game, () => PlayReload.Load(args.Profile, args.ActiveMod, args.TestLayer),
            load => Continue(save, place, PlayReload.Apply(load, place, target), clock)));
    }

    /// <summary>Starts a new server at the place and waits for the player. Then it sends reloaded, or reload-failed when
    /// the game kept its old data.</summary>
    /// <param name="failure">The failure of <see cref="PlayReload.Apply"/>, or null when the game has the new
    /// data.</param>
    private void Continue(int save, PlayPlace place, Exception? failure, Stopwatch clock)
    {
        if (failure != null)
            FLLog.Error(LogComponent, $"The reload of save {save} failed, so the game keeps its data: {failure}");
        Begin(place.Start, gameServer => PlayReload.Resume(gameServer, place), (player, at) => Send(failure == null
            ? new PlayEvent.Reloaded(save, clock.ElapsedMilliseconds, player.System, at)
            : new PlayEvent.ReloadFailed(save, failure.Message)));
    }

    /// <summary>
    /// Starts the mission play again from its start save (section 5.8 of the M6c design). The steps are the steps of a
    /// reload: the server saves the player and stops, and a task loads the data of the save again, with the test layer
    /// that the editor wrote for the new start. Then a new server loads the start save, not the autosave. So the edit
    /// shows, and the mission starts again at the same point.
    /// </summary>
    /// <remarks>When the game keeps its old data, it goes on at the place of the player with the autosave, as after a
    /// reload that failed, and sends restart-failed. The cases of a reload that refuse, and its time limits, apply too
    /// (<see cref="Reload"/>).</remarks>
    private void Restart(int save)
    {
        if (args.Start is not PlayStart.FromSave start)
        {
            Send(new PlayEvent.RestartFailed(save, "This play has no start save. Only a mission play can start again."));
            return;
        }

        if (server == null || !arrived)
        {
            Send(new PlayEvent.RestartFailed(save,
                "The game can start the mission again only while the player is in space or at a base."));
            return;
        }

        if (PlayReload.CheckLeave(server.Server) is { } refusal)
        {
            Send(new PlayEvent.RestartFailed(save, refusal));
            return;
        }

        var clock = Stopwatch.StartNew();
        PlayPlace left;
        try
        {
            left = PlayReload.Leave(server.Server);
        }
        catch (TimeoutException e)
        {
            FLLog.Error(LogComponent, e.Message);
            FLLog.Flush();
            Send(new PlayEvent.Error(e.Message, Crash: false));
            Environment.Exit(PlayExitCodes.Timeout);
            return;
        }

        server = null;
        arrived = false;
        var target = new PlayGame(files, game.GameData, game.Ui.Data, game.Saves);
        game.ChangeState(new PlayReloadState(game, () => PlayReload.Load(args.Profile, args.ActiveMod, args.TestLayer),
            load => StartAgain(save, start, left, PlayReload.ApplyStart(load, start, args.Package, target), clock)));
    }

    /// <summary>Starts a new server from the start save and sends restarted. When the game kept its old data, the new
    /// server goes on at the place of the player, and the host sends restart-failed.</summary>
    /// <param name="left">The place of the player when the server stopped.</param>
    /// <param name="failure">The failure of <see cref="PlayReload.ApplyStart"/>, or null when the game has the new
    /// data.</param>
    private void StartAgain(int save, PlayStart.FromSave start, PlayPlace left, Exception? failure, Stopwatch clock)
    {
        if (failure != null)
        {
            FLLog.Error(LogComponent, $"The restart of save {save} failed, so the game keeps its data: {failure}");
            place = left.Start;
            Begin(left.Start, gameServer => PlayReload.Resume(gameServer, left),
                (_, _) => Send(new PlayEvent.RestartFailed(save, failure.Message)));
            return;
        }

        place = PlayStartSave.Read(start.Save).Place;
        Begin(place, gameServer =>
        {
            var restart = PlaySave.Create(gameServer, start, args.Package);
            gameServer.Start();
            gameServer.LoadSaveGame(restart);
        }, (player, at) => Send(new PlayEvent.Restarted(save, clock.ElapsedMilliseconds, player.System, at)));
    }

    /// <summary>
    /// Makes the server and the session of a play, starts the server with a save and waits for the player. The new
    /// server becomes the server of the current play. The mission observer of the host gets the trigger changes of the
    /// new server.
    /// </summary>
    /// <param name="start">The place of the save. In space, the ship gets the heading of the start after the
    /// spawn.</param>
    /// <param name="run">Starts the server and gives it the save.</param>
    /// <param name="arrival">Gets the player and the base, or null in space, when the player arrived.</param>
    private void Begin(PlayStart start, Action<GameServer> run, Action<Player, string?> arrival)
    {
        var play = new EmbeddedServer(game.GameData, game.ResourceManager, PlayFolder.SavesPath(args.Profile));
        // Before the server starts, so the load of the mission reaches the observer.
        play.Server.MissionObserver = observer;
        var session = new CGameSession(game, play);
        run(play.Server);
        server = play;
        var player = play.Server.LocalPlayer!;
        game.ChangeState(new PlayWaitState(session, game, player, start, dock =>
        {
            arrived = true;
            arrival(player, dock);
        }));
    }

    /// <summary>
    /// The event of a spawn. In space, it gives the start position, not the live position of the ship, so its numbers
    /// are finite.
    /// </summary>
    /// <param name="dock">The base, or null in space.</param>
    private PlayEvent.Spawned Spawned(Player player, string? dock, long startMs)
    {
        // The server loaded the character before it sent the spawn, and PlaySave.Check made sure of a ship.
        var ship = player.Character!.Ship!;
        Vector3? position = dock == null && place is PlayStart.InSpace space ? space.Position : null;
        return new PlayEvent.Spawned(player.System, dock, position, ship.Nickname,
            game.GameData.GetString(ship.IdsName), startMs);
    }

    private static long SinceProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
    }
}
