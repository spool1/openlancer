using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The Play start page of the Inspector (sections 5.3 and 8.3 of the M6c design): the plan of the play start of a
/// mission, with the choices of the start and of the start place. It is a model without ImGui, so the tests check it.
/// The plan reads the model of the script now. Play makes the plan again from the saved script.
/// </summary>
/// <param name="Script">The install path of the script.</param>
/// <param name="Setting">The play start of the settings.</param>
internal sealed record PlayStartPageModel(string Script, PlayStartSetting Setting, StartPlan Plan)
{
    /// <summary>The info icon of the page (section 8.5 of the M6c design). The texts about Ctrl+F5 and the save folder
    /// of the game come with the start in the linked game (milestone M6c-3).</summary>
    public static readonly HelpText Help = new("Play start",
        "The point where Play starts this mission: the mission start, a checkpoint or any trigger. F5 plays from it in " +
        "OpenLancer.",
        "A start save in the play folder. For an approximate start, a test layer with setup triggers. The mod does not " +
        "change. An export never holds them.");

    /// <summary>The info icon of an approximate start (section 8.5 of the M6c design).</summary>
    public static readonly HelpText ApproximateHelp = new("Approximate start",
        "A start at a trigger that no checkpoint saves. The editor runs the path to the trigger and puts its state into " +
        "setup triggers: ships, orders, sides, objectives and locks. Some state can differ, for example damage or the " +
        "cargo of a ship.", "Two setup triggers in a test layer of the play.");

    // The tooltips of section 8.3 of the M6c design.
    public const string StartTip = "The point where Play starts the mission.";
    public const string SlotTip = "The story slot of the mission. The start save names its mission.";
    public const string StageTip = "The story stage in the start save. After the accept, it is the accept stage of the slot.";
    public const string AcceptTip = "The start comes after the player accepts the mission.";
    public const string PlaceTip = "Where the player starts. The suggestion comes from the start trigger and from the path to it.";
    public const string ShipTip = "The ship package of the Play menu.";
    public const string WaitTip = "These triggers are active when the play starts, as at this point of the mission.";
    public const string SetupTip = "The setup triggers of an approximate start. They are in a test layer of the play. The mod " +
                                   "does not change.";

    public const string PlayTip = "Play the mission from this start in OpenLancer (F5 in the Mission tab).";

    /// <summary>The model of the play start of a script.</summary>
    public static PlayStartPageModel Of(MissionAnalysis analysis, PlayStartSetting setting)
    {
        var (place, dock) = setting.PlaceChoice;
        return new PlayStartPageModel(analysis.Script.Path, setting,
            StartPlan.Make(analysis.Index, analysis.Script.Slot, setting.Start, place, dock));
    }

    /// <summary>The choices of the field "Start": the mission start, each checkpoint, and the trigger of the play start
    /// when it is another trigger.</summary>
    public IReadOnlyList<(MissionStartPoint Start, string Label)> StartChoices(MissionIndex index)
    {
        var choices = new List<(MissionStartPoint, string)> { (MissionStartPoint.MissionStart, "Mission start") };
        choices.AddRange(DryRun.Checkpoints(index).Select(t =>
            (MissionStartPoint.At(t.Nickname), StartPlan.NameOf(StartKind.Checkpoint, MissionStartPoint.At(t.Nickname)))));
        if (!Setting.Start.IsMissionStart && !choices.Any(c => c.Item1.Equals(Setting.Start)))
            choices.Add((Setting.Start, Plan.StartName));
        return choices;
    }

    /// <summary>The text of the start place choice: "Suggested: {place}", "At base {base}" or "At the Scene
    /// camera".</summary>
    public string PlaceText => PlaceTextOf(Setting, Plan);

    /// <summary>The suggested place in words.</summary>
    public string SuggestedText => SuggestedTextOf(Plan);

    /// <summary>The text of the start place choice of a play start and its plan. The Play start page and the tooltip of
    /// the Play button of a trigger node show it.</summary>
    public static string PlaceTextOf(PlayStartSetting setting, StartPlan plan) => setting.PlaceChoice.Place switch
    {
        StartPlaceChoice.Base => $"At base {setting.PlaceChoice.Base}",
        StartPlaceChoice.Camera => "At the Scene camera",
        _ => $"Suggested: {SuggestedTextOf(plan)}",
    };

    private static string SuggestedTextOf(StartPlan plan) => plan.CanStart ? plan.Place.Text : "none";

    /// <summary>The story stage: its name and its number.</summary>
    public string StageText =>
        $"{StoryStages.NameOf(Plan.Stage)} ({Plan.Stage.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The setup triggers as read-only raw INI, or null for a start without them.</summary>
    public string? SetupIni => Plan.Setup.Count == 0 ? null : Plan.SetupPatch;

    /// <summary>The setup triggers as the text of the raw INI editor, or null for a start without them.</summary>
    public RawText? SetupText => setupText ??= SetupIni is { } ini ? RawText.Plain(ini) : null;

    private RawText? setupText;
}
