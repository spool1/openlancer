using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor;

/// <summary>
/// Play in the editor (section 9 of the M4 design): the game of Play, its ship, and the seam of Play (decision D50). Play
/// in the game and the test server of milestone M11 use the same seam (<see cref="IGamePlayHost"/>). The members of the
/// seam delegate to the session, the edits and the Console. They are explicit, so the public surface of the window does
/// not grow. The mission play of the Mission tab (section 5 of the M6c design) reads the window through
/// <see cref="IMissionPlayEditor"/>.
/// </summary>
internal sealed partial class EditorWindow : IPlayEditor, IGamePlayHost, IMissionPlayEditor
{
    // The clock of the watchdogs of Play and of the time from Play to flight.
    private readonly Stopwatch playClock = Stopwatch.StartNew();

    /// <summary>The game of Play: its start, its state and its stop.</summary>
    public PlayController Play { get; }

    /// <summary>Play in the game and the test server (milestone M11).</summary>
    public GamePlay GamePlay { get; }

    /// <summary>
    /// Makes a ship package the ship of Play for the open profile (decision D15): the row action Use for Play of the
    /// Goods table, and Use the default ship of the Play menu. The next play uses it.
    /// </summary>
    /// <param name="package">The nickname of the package, or null for the package of a new multiplayer
    /// character.</param>
    public void UseForPlay(string? package)
    {
        if (Session is not { } session)
        {
            return;
        }

        Settings.SetPlayPackage(session.Profile.FilePath, package);
        Settings.Save();
        var ship = PlayShips.ShipName(session, package);
        Log.Add(ConsoleSeverity.Info, PlayController.Source, package == null
            ? $"Play uses the default ship of a new character{(ship == null ? "" : $": {ship}")}."
            : $"Play uses the ship package {package}{(ship == null ? "" : $": {ship}")}.");
    }

    ConsoleLog IPlayHost.Log => Log;

    PlaySession? IPlayHost.Session => Session is { GameData: not null } session
        ? PlaySession.Of(session.Edits, session.Saves, Edits.IncompleteSave, session.NoEditReason)
        : null;

    SaveOutcome IPlayHost.Save() => Edits.Save();

    ValidationState? IPlayHost.Validation => Edits.Validation;

    event Action<ValidationState>? IPlayHost.Validated
    {
        add => Edits.Validated += value;
        remove => Edits.Validated -= value;
    }

    void IPlayHost.ValidateNow() => Edits.ValidateNow();

    string IPlayEditor.SystemName(string nickname) =>
        Session?.GameData?.Items.Systems.Get(nickname) is { } system ? Session.ItemName(system) : "";

    string IPlayEditor.BaseName(string nickname) =>
        Session?.GameData?.Items.Bases.Get(nickname) is { } dock ? Session.ItemName(dock) : "";

    bool IPlayEditor.SceneBehind => Scene.IsBehind;

    void IPlayEditor.CatchUpScene() => Scene.Flush();

    IReadOnlyList<SpawnShape>? IPlayEditor.SpawnShapes(string system) => Scene.SpawnShapes(system);

    void IPlayEditor.MissionChanged(PlayEvent.MissionChanges changes) => Missions.MissionChanged(changes);

    void IPlayEditor.PlayStarted() => Missions.Live.Started();

    void IPlayEditor.PlayEnded() => Missions.Live.Ended();

    void IGamePlayHost.Ask(string title, string message, IReadOnlyList<Choice> choices) =>
        Choice.Show(title, message, choices);

    void IGamePlayHost.ChooseFolder(Action<string> chosen) => FolderActions.Choose(this, chosen);

    IReadOnlyList<string> IGamePlayHost.ProtectedFolders() =>
        PlayCopy.ProtectedFolders(Settings.WorkspaceOrDefault, Session?.Profile);

    string? IMissionPlayEditor.ProfilePath => Session?.Profile.FilePath;

    EditorSettings IMissionPlayEditor.Settings => Settings;

    ConsoleLog IMissionPlayEditor.Log => Log;

    (MissionIndex Index, StorySlot? Slot)? IMissionPlayEditor.IndexOf(string script) =>
        Missions.Analysis(script) is { } analysis ? (analysis.Index, analysis.Script.Slot) : null;

    (MissionIndex Index, StorySlot? Slot)? IMissionPlayEditor.ModelOf(string script, out string? problem)
    {
        problem = null;
        if (Session?.Edits is not { } edits)
        {
            problem = "No profile is open.";
            return null;
        }

        try
        {
            var model = edits.Missions.Model(script);
            return (model.Index, model.Analysis.Script.Slot);
        }
        catch (EditException e)
        {
            problem = e.Error.Message;
            return null;
        }
    }

    StartSavePlace? IMissionPlayEditor.PlaceOf(StartPlace place, out string? problem)
    {
        problem = StartPlaceProblem(place);
        if (problem != null)
        {
            return null;
        }

        var items = Session!.GameData!.Items;
        switch (place.Kind)
        {
            case StartPlaceKind.Space:
                var system = items.Systems.Get(place.System!)!;
                var point = new PlayStart.InSpace(system.Nickname, place.Position, place.Orientation);
                // A rule of the plan gives the point, so it can be in a planet or in a death zone. The Scene has the
                // shapes of the system only while it shows that system.
                if (Scene.SpawnShapes(system.Nickname) is { } spaceShapes)
                {
                    point = Checked(SpawnPoint.FromPoint(point, spaceShapes));
                }
                else
                {
                    var name = Session.ItemName(system);
                    Log.Add(ConsoleSeverity.Info, PlayController.Source,
                        UncheckedPoint(name.Length > 0 ? $"{name} ({system.Nickname})" : system.Nickname));
                }

                return new StartSavePlace(point.System, null, point.Position, point.Orientation);
            case StartPlaceKind.Base:
                var dock = items.Bases.Get(place.Base!)!;
                return StartSavePlace.AtBase(dock.System!, dock.Nickname);
        }

        // The Scene camera, with the start check of Play from camera (section 9.5 of the M4 design).
        var open = Scene.System!;
        if (Scene.SpawnShapes(open.Nickname) is not { } shapes)
        {
            problem = MissionPlay.NoCameraSystem;
            return null;
        }

        var camera = Checked(SpawnPoint.FromCamera(
            new PlayStart.InSpace(open.Nickname, Scene.CameraPosition, Scene.CameraOrientation), shapes));
        return new StartSavePlace(camera.System, null, camera.Position, camera.Orientation);
    }

    /// <summary>Gives the lines of a start check to the Console and returns its start.</summary>
    private PlayStart.InSpace Checked(SpawnCheck check)
    {
        foreach (var (severity, message) in check.Lines)
        {
            Log.Add(severity, PlayController.Source, message);
        }

        return check.Start;
    }

    /// <summary>Why a mission play cannot start at a start place now, or null (see
    /// <see cref="MissionPlay.PlaceProblem"/>).</summary>
    internal string? StartPlaceProblem(StartPlace place) =>
        MissionPlay.PlaceProblem(Session?.GameData?.Items, Scene.System != null, place);

    /// <summary>Why a mission play cannot start at the place choice of a play start for any trigger now, or null (see
    /// <see cref="MissionPlay.ChoiceProblem"/>).</summary>
    internal string? StartChoiceProblem(PlayStartSetting setting) =>
        MissionPlay.ChoiceProblem(Session?.GameData?.Items, Scene.System != null, setting);

    /// <summary>The Console note of a start point in space that Play cannot check, because the Scene does not show its
    /// system.</summary>
    internal static string UncheckedPoint(string system) =>
        $"Mission play: the Scene does not show {system}, so Play did not check the start point for planets, stations " +
        "and death zones. Open the system in the Scene to check it at the next play.";

    string? IMissionPlayEditor.PlayPackage => Session is { } session ? Settings.PlayPackageOf(session.Profile.FilePath) : null;

    PlayStatus IMissionPlayEditor.PlayStatus => Play.Status;

    IMissionStart? IMissionPlayEditor.RunningMission => Play.Mission;

    IReadOnlyList<IMissionStart> IMissionPlayEditor.MissionStarts => Play.MissionStarts;

    bool IMissionPlayEditor.GameRuns => GamePlay.GameRuns;

    Task<PlayRequestResult> IMissionPlayEditor.RequestMission(IMissionStart start, string? package, PlaySaveRule save) =>
        Play.RequestMission(start, package, save);

    string? IAgentPlayHost.UnsavedMod => Session is { Edits: { IsDirty: true, ActiveMod: { } mod } } ? mod.Id : null;

    bool IAgentPlayHost.AutoSaveAgentPlay => Settings.Agent.AutoSavePlay;

    void IAgentPlayHost.Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel) =>
        Choice.Show(title, message, choices, cancel);
}
