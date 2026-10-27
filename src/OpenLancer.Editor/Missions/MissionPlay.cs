using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LibreLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Missions;

/// <summary>What the mission play request reads of the editor. <see cref="EditorWindow"/> implements it, and the tests
/// give a fake.</summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IMissionPlayEditor : IAgentPlayHost
{
    /// <summary>The full path of the profile file, or null without an open profile.</summary>
    string? ProfilePath { get; }

    /// <summary>The settings of the editor. They keep the play start of each script.</summary>
    EditorSettings Settings { get; }

    ConsoleLog Log { get; }

    /// <summary>The index and the story slot of a script in the last run of the mission checks, or null.</summary>
    (MissionIndex Index, StorySlot? Slot)? IndexOf(string script);

    /// <summary>The index and the story slot of the model of a script now, or null with the reason (section 7 of the M6b
    /// design).</summary>
    (MissionIndex Index, StorySlot? Slot)? ModelOf(string script, out string? problem);

    /// <summary>The place of the start save for the start place of a plan, or null with the reason.</summary>
    StartSavePlace? PlaceOf(StartPlace place, out string? problem);

    /// <summary>The ship package of the Play menu, or null for the default ship.</summary>
    string? PlayPackage { get; }

    /// <summary>The Play status of OpenLancer.</summary>
    PlayStatus PlayStatus { get; }

    /// <summary>The mission start of the running OpenLancer game, or null.</summary>
    IMissionStart? RunningMission { get; }

    /// <summary>The mission starts that Play keeps (<see cref="PlayController.MissionStarts"/>).</summary>
    IReadOnlyList<IMissionStart> MissionStarts { get; }

    /// <summary>True while the game of a play copy runs (Play in the game).</summary>
    bool GameRuns { get; }

    /// <summary>Asks Play for a mission play (<see cref="PlayController.RequestMission"/>).</summary>
    Task<PlayRequestResult> RequestMission(IMissionStart start, string? package, PlaySaveRule save);
}

/// <summary>
/// The mission play request of the shared command layer (section 7.2 of the M6c design, decision D758): the one entry of
/// F5 and Play from here in the Mission tab, the items of the Play menu and the agent tool <c>mission_play</c>. It reads
/// and stores the play start of each script (section 5.2, decision D744). An agent request plays the start of the
/// request and never changes the play start of the user. It never saves on its own: the agent play rule asks the user
/// first (user decision of 2026-10-20).
/// </summary>
internal sealed class MissionPlay(IMissionPlayEditor editor) : IMissionPlayHost
{
    /// <summary>The detail code of an agent request while another play runs.</summary>
    public const string OtherPlayDetail = "OtherPlay";

    /// <summary>The refusal of an agent request while another play runs (section 7.2 of the M6c design).</summary>
    public const string OtherPlayReason = "Another play runs, so the mission play did not start. Stop the other play " +
                                          "first.";

    /// <summary>The reason of each start place while the game data of the profile did not load.</summary>
    public const string NoGameData = "The game data of the profile did not load.";

    /// <summary>The reason of a start at the Scene camera while no system is open.</summary>
    public const string NoCameraSystem = "The start place is the Scene camera, and no system is open. Open a system, or " +
                                         "pick another start place on the Play start page.";

    /// <summary>The reason of a suggested start at the Scene camera in a system that the Scene does not show.</summary>
    public static string CameraSystemReason(string system) =>
        $"The suggested start place is the Scene camera in {system}, because no line of the path gives a point there. " +
        $"The play cannot start in another system. Open {system} in the Scene, or pick a base on the Play start page.";

    /// <summary>
    /// Why a mission play cannot start at a start place, or null when it can (UI rule 2): a system or a base that the
    /// game data does not have, the Scene camera while no system is open, or a suggested Scene camera in a system that
    /// the Scene does not show. The Play start page shows the reason under the start place, and Play gives it when it
    /// refuses. The check has no side effect: it writes nothing to the Console.
    /// </summary>
    /// <param name="items">The game data of the profile, or null when it did not load.</param>
    /// <param name="openSystem">The nickname of the system that the Scene shows, or null.</param>
    internal static string? PlaceProblem(GameItemDb? items, string? openSystem, StartPlace place)
    {
        if (items == null)
        {
            return NoGameData;
        }

        return place.Kind switch
        {
            StartPlaceKind.Space when items.Systems.Get(place.System!) == null =>
                $"The start place names the system '{place.System}', which is not in the game data. Pick a base or the " +
                "Scene camera on the Play start page.",
            StartPlaceKind.Base when items.Bases.Get(place.Base!) is not { System: not null } =>
                $"The start place names the base '{place.Base}', which is not in the game data. Pick a base or the " +
                "Scene camera on the Play start page.",
            StartPlaceKind.Camera when place.System is { } system &&
                                       !system.Equals(openSystem, StringComparison.OrdinalIgnoreCase) =>
                CameraSystemReason(system),
            StartPlaceKind.Camera when openSystem == null => NoCameraSystem,
            _ => null,
        };
    }

    /// <summary>
    /// Why a mission play cannot start at the place choice of a play start for any trigger, or null (UI rule 2): the game
    /// data did not load, the base of the choice "At base" is not in the game data, or the choice "At the Scene camera"
    /// while no system is open. With the choice "Suggested", each trigger has its own place, so only the game data can
    /// stop all of them. The chip of the play start shows the reason, because no node of the graph then shows a Play
    /// button.
    /// </summary>
    /// <param name="items">The game data of the profile, or null when it did not load.</param>
    /// <param name="openSystem">The nickname of the system that the Scene shows, or null.</param>
    internal static string? ChoiceProblem(GameItemDb? items, string? openSystem, PlayStartSetting setting) =>
        setting.PlaceChoice switch
        {
            (StartPlaceChoice.Base, { } dock) => PlaceProblem(items, openSystem,
                new StartPlace(StartPlaceKind.Base, null, dock.Trim(), Vector3.Zero, Quaternion.Identity, "")),
            (StartPlaceChoice.Camera, _) => PlaceProblem(items, openSystem, StartPlace.Camera("")),
            _ => items == null ? NoGameData : null,
        };

    // The last agent request that asked the user, or null. Until it ends, a new agent request gets busy.
    private AgentQuestion? asking;

    /// <summary>The play start of a script of the open profile, or the mission start without a profile.</summary>
    public PlayStartSetting PlayStartOf(string script) => editor.ProfilePath is { } profile
        ? editor.Settings.PlayStartOf(profile, script)
        : PlayStartSetting.Default;

    /// <summary>Sets the play start of a script ("Set as play start" and the Play start page). It has no Undo entry,
    /// because it is not data of the mod.</summary>
    public void SetPlayStart(string script, PlayStartSetting start)
    {
        if (editor.ProfilePath is not { } profile)
        {
            return;
        }

        editor.Settings.SetPlayStart(profile, script, start);
        editor.Settings.Save();
    }

    void IMissionPlayHost.SetPlayStart(string script, MissionStartPoint start) =>
        SetPlayStart(script, PlayStartOf(script) with { Trigger = start.Trigger ?? "" });

    MissionStartPoint IMissionPlayHost.PlayStartOf(string script) => PlayStartOf(script).Start;

    /// <summary>Moves the play starts of the open profile with the trigger renames of a change of the history, also an
    /// Undo or a Redo of a rename (section 5.2 of the M6c design). The starts that Play keeps move too: the start of the
    /// running mission play, of its restart and of a request that waits. So a restart after the rename and its save
    /// starts at the renamed trigger.</summary>
    public void Renamed(MissionScriptsChange change)
    {
        foreach (var start in editor.MissionStarts.OfType<MissionPlayStart>())
        {
            start.Renamed(change);
        }

        if (editor.ProfilePath is { } profile && Rename(editor.Settings, profile, change))
        {
            editor.Settings.Save();
        }
    }

    /// <summary>Moves the play starts of a profile with the trigger renames of a change. Returns true when a play start
    /// changed.</summary>
    internal static bool Rename(EditorSettings settings, string profile, MissionScriptsChange change)
    {
        var changed = false;
        foreach (var (from, to) in change.Renames)
        {
            if (string.Equals(from.Script, to.Script, StringComparison.OrdinalIgnoreCase))
            {
                changed |= settings.RenamePlayStart(profile, from.Script, from.Name, to.Name);
            }
        }

        return changed;
    }

    /// <summary>The name of the play start of a script in the UI texts (section 8.1 of the M6c design): "Mission start",
    /// "Checkpoint {trigger}", "{trigger} (approximate)" or "Test script". The Play start page gives the same name.</summary>
    public string StartNameOf(string script) => NameOf(editor.IndexOf(script), PlayStartOf(script).Start);

    /// <summary>The name of a start of a script with an index, or of a script that the last run does not have.</summary>
    internal static string NameOf((MissionIndex Index, StorySlot? Slot)? script, MissionStartPoint start) =>
        StartPlan.NameOf(script is var (index, slot)
            ? StartPlan.KindOf(index, slot, start)
            : start.IsMissionStart ? StartKind.MissionStart : StartKind.Approximate, start);

    /// <summary>F5 in the Mission tab: plays a script from its play start in OpenLancer (decision D744).</summary>
    public Task<PlayRequestResult> PlayFromPlayStart(string script)
    {
        var start = PlayStartOf(script);
        var (place, dock) = start.PlaceChoice;
        return Request(new MissionPlayRequest(script, start.Start) { Place = place, PlaceBase = dock, FromPlayStart = true });
    }

    /// <summary>"Play from here": sets the play start to a trigger and plays it. The place choice of the play start
    /// stays.</summary>
    public Task<PlayRequestResult> PlayFromHere(string script, string trigger)
    {
        SetPlayStart(script, PlayStartOf(script) with { Trigger = trigger });
        return PlayFromPlayStart(script);
    }

    /// <summary>
    /// The mission play request (section 7.2 of the M6c design). Play saves the active mod, then the start makes its
    /// plan from the model of the saved script and writes the start save and the test layer. While a mission play runs,
    /// the game starts again at the new start. A request of the user does what F5 does. A request of an agent also gives
    /// <c>refused</c> while another play runs and when its plan cannot start, and it follows the agent play rule (see
    /// <see cref="PlayRequests.WithAgentRule"/>).
    /// </summary>
    /// <returns>The result, when the play started or did not start.</returns>
    public Task<PlayRequestResult> Request(MissionPlayRequest request)
    {
        if (editor.ProfilePath == null)
        {
            const string noProfile = "Play needs an open profile whose game data loaded.";
            editor.Log.Add(ConsoleSeverity.Warning, PlayController.Source, noProfile);
            return Task.FromResult(PlayRequestResult.Refused(editor.PlayStatus, "NoProfile", noProfile));
        }

        if (request.Origin == EditOrigin.Agent && AgentRefusal(request) is { } refused)
        {
            return Task.FromResult(refused);
        }

        var question = PlayRequests.AsksUser(editor, request.Origin) ? new AgentQuestion() : null;
        var result = PlayRequests.WithAgentRule(editor, request.Origin, () => editor.PlayStatus, save =>
        {
            if (question == null)
            {
                return editor.RequestMission(new MissionPlayStart(this, request), editor.PlayPackage, save);
            }

            // The user can start another play while the question is open, so the rule of an agent request runs again
            // after the answer.
            question.Answered = true;
            return AgentRefusal(request, afterQuestion: true) is { } late
                ? Task.FromResult(late)
                : editor.RequestMission(new MissionPlayStart(this, request), editor.PlayPackage, save);
        });
        if (question != null)
        {
            question.Result = result;
            asking = question;
        }

        return result;
    }

    /// <summary>
    /// Why a request of an agent cannot start before it asks the user, or null: an earlier agent request that asked the
    /// user did not end, another play runs (section 7.2 of the M6c design), the script or the trigger does not resolve, or
    /// the plan of the model now refuses (section 7.4). The plan of the model now is the plan of the saved script, because
    /// the save writes the model.
    /// </summary>
    /// <param name="afterQuestion">True for the check after the answer of the user. The open question is then the
    /// question of this request.</param>
    private PlayRequestResult? AgentRefusal(MissionPlayRequest request, bool afterQuestion = false)
    {
        var status = editor.PlayStatus;
        if (!afterQuestion && asking is { Result.IsCompleted: false } earlier)
        {
            return PlayRequestResult.Refused(status, PlayRequests.AgentWaitsDetail,
                earlier.Answered ? PlayRequests.AgentRunsReason : PlayRequests.AgentWaitsReason, EditErrorKind.Busy);
        }

        if (editor.GameRuns || status != PlayStatus.Idle && editor.RunningMission == null)
        {
            return PlayRequestResult.Refused(status, OtherPlayDetail, OtherPlayReason);
        }

        if (editor.ModelOf(request.Script, out var problem) is not var (index, slot))
        {
            return PlayRequestResult.Refused(status, MissionErrors.ScriptNotFound, problem!, EditErrorKind.NotFound);
        }

        if (request.Start.Trigger is { } name && index.Trigger(name) == null)
        {
            return PlayRequestResult.Refused(status, MissionErrors.TriggerNotFound, StartPlan.NoTriggerReason(name),
                EditErrorKind.NotFound);
        }

        var plan = StartPlan.Make(index, slot, request.Start, request.Place, request.PlaceBase);
        return plan.CanStart ? null : PlayRequestResult.Refused(status, "StartRefused", plan.Reason!) with { Plan = plan };
    }

    string? IMissionPlayHost.ProfilePath => editor.ProfilePath;

    ConsoleLog IMissionPlayHost.Log => editor.Log;

    (MissionIndex Index, StorySlot? Slot)? IMissionPlayHost.ModelOf(string script, out string? problem) =>
        editor.ModelOf(script, out problem);

    StartSavePlace? IMissionPlayHost.PlaceOf(StartPlace place, out string? problem) => editor.PlaceOf(place, out problem);

    /// <summary>An agent request that asked the user.</summary>
    private sealed class AgentQuestion
    {
        /// <summary>The result of the request. It completes when Play gives the result, which can be after the
        /// answer.</summary>
        public Task<PlayRequestResult>? Result { get; set; }

        /// <summary>True after the user clicked "Save and continue".</summary>
        public bool Answered { get; set; }
    }
}
