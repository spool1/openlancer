using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Play;

namespace OpenLancer.Editor.Missions;

/// <summary>One row of the field "Ship" of the Play start page (issue #48).</summary>
/// <param name="Package">The ship package of the row, or null for the ship of the profile.</param>
/// <param name="Text">The text of the row: the plain name of the ship of the package.</param>
/// <param name="Hint">The nickname of the package as a dim hint when two rows have the same name, else null.</param>
internal sealed record ShipRow(string? Package, string Text, string? Hint);

/// <summary>The field "Ship" of the Play start page (issue #48), without ImGui.</summary>
/// <param name="Rows">The rows of the picker: the ship of the profile, then each ship package of the edit view by the
/// name of its ship.</param>
/// <param name="Text">The text of the value of the field.</param>
/// <param name="Tooltip">The tooltip of the value.</param>
/// <param name="Problem">The reason when the edit view cannot give the ship of the value, else null. Play then refuses
/// with this reason.</param>
/// <param name="Starter">True when the value gives the starter ship: no ship package, or a package whose ship is the
/// ship of a new character.</param>
internal sealed record ShipField(IReadOnlyList<ShipRow> Rows, string Text, string Tooltip, string? Problem, bool Starter);

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
        "setup triggers: ships, orders, sides, objectives and locks. The setup makes only the objects of the start " +
        "system. The ships that travel with the player start next to the player. Some state can differ, for example " +
        "damage or the cargo of a ship.", "Two setup triggers in a test layer of the play.");

    // The tooltips of section 8.3 of the M6c design.
    public const string StartTip = "The point where Play starts the mission.";
    public const string SlotTip = "The story slot of the mission. The start save names its mission.";
    public const string StageTip = "The story stage in the start save. After the accept, it is the accept stage of the slot.";
    public const string AcceptTip = "The start comes after the player accepts the mission.";
    public const string PlaceTip = "Where the player starts. The suggestion is where the player is at this trigger in the " +
                                   "game: docked at a base, or in space at a point. After a launch without a point, the " +
                                   "suggestion is the base of the launch. After a jump without a point, it is the jump gate " +
                                   "or hole of the jump. When no line of the path gives a place, it is the Scene camera.";
    public const string SourceTip = "The line of the script that tells where the player is at this trigger. When that line " +
                                    "gives no system or no point, the text names the line that gives the system or the point.";
    public const string ShipTip = "The ship and the loadout of the player in this mission play. The start save holds " +
                                  "them. The default is the ship of the profile, which the Play menu shows.";

    /// <summary>The label of the default row of the field "Ship".</summary>
    public const string ProfileShipLabel = "Ship of the profile";

    /// <summary>The tooltip of the default row of the field "Ship".</summary>
    public const string ProfileRowTip = "The ship of the profile, which the Play menu shows. Use for Play in Database > " +
                                        "Goods sets it.";

    /// <summary>The warning of a combat start with the starter ship (issue #48).</summary>
    public const string CombatWarning = "This start is in combat, and the ship of the play is the starter ship. Enemy " +
                                        "ships can destroy it fast. Pick a stronger ship in the Ship field.";
    public const string WaitTip = "These triggers are active when the play starts, as at this point of the mission.";
    public const string SetupTip = "The setup triggers of an approximate start. They are in a test layer of the play. The mod " +
                                   "does not change.";
    public const string LeftOutTip = "The setup makes only the objects of the start system. At this trigger, these objects " +
                                     "are in another system, or a trigger of the start makes them again. The setup also " +
                                     "removes each line that names these objects.";
    public const string CompanionsTip = "These ships travel with the player at this trigger. The setup puts them next to the " +
                                        "player. Then their last orders continue.";
    public const string RowTip = "Selects the trigger of its spawn line.";

    // The labels of the rows of the start place. The tooltip of the Play button of a node uses the same labels.
    public const string PlaceLabel = "Start place";
    public const string SourceLabel = "Source of the place";

    public const string PlayTip = "Play the mission from this start in OpenLancer (F5 in the Mission tab).";

    /// <summary>The model of the play start of a script.</summary>
    public static PlayStartPageModel Of(MissionAnalysis analysis, PlayStartSetting setting)
    {
        var (place, dock) = setting.PlaceChoice;
        return new PlayStartPageModel(analysis.Script.Path, setting,
            StartPlan.Make(analysis.Index, analysis.Script.Slot, setting.Start, place, dock));
    }

    /// <summary>
    /// The field "Ship" of a play start (issue #48). The first row is the ship of the profile, as the Play menu names it.
    /// The other rows are the ship packages of the edit view, sorted by the names of their ships. A row whose name another
    /// row also has shows its package nickname as a hint. The value is the package of the setting, else the ship of the
    /// profile. A package that the edit view cannot resolve gives the reason of <see cref="PlayShips.ShipPart"/>.
    /// </summary>
    /// <param name="view">The edit view, or null without an open profile.</param>
    /// <param name="profilePackage">The ship package of the Play menu, or null for the default ship.</param>
    public static ShipField ShipFieldOf(ShipView? view, PlayStartSetting setting, string? profilePackage)
    {
        var profile = PlayShips.ShipText(view, profilePackage);
        IReadOnlyList<(string Package, string Ship)> packages = view == null ? [] : PlayShips.Packages(view);
        var shared = packages.GroupBy(p => p.Ship, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ShipRow> { new(null, $"{ProfileShipLabel}: {profile.Text}", null) };
        rows.AddRange(packages.OrderBy(p => p.Ship, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Package, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ShipRow(p.Package, p.Ship, shared.Contains(p.Ship) ? p.Package : null)));

        string? problem = null;
        var package = setting.PackageOr(profilePackage);
        var part = package == null || view == null ? null : PlayShips.ShipPart(view.Edits, package, out problem);
        var starter = package == null ||
                      part != null && string.Equals(part.Ship, view?.DefaultShip, StringComparison.OrdinalIgnoreCase);
        if (setting.ShipPackage is not { } field)
        {
            return new ShipField(rows, rows[0].Text, profile.Tooltip, problem, starter);
        }

        if (rows.FirstOrDefault(r => string.Equals(r.Package, field, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            return new ShipField(rows, row.Hint == null ? row.Text : $"{row.Text} ({row.Hint})", PackageTip(field),
                problem, starter);
        }

        return new ShipField(rows, $"{field} (not in the data)", problem ?? $"The edit view has no ship package {field}.",
            problem, starter);
    }

    /// <summary>The tooltip of a row of the field "Ship".</summary>
    public static string ShipRowTip(ShipRow row) => row.Package == null ? ProfileRowTip : PackageTip(row.Package);

    /// <summary>The tooltip of a ship package of the field "Ship".</summary>
    private static string PackageTip(string package) => $"The ship package {package}.";

    /// <summary>The warning of a combat start whose field "Ship" gives the starter ship, or null (issue #48). Only the
    /// Play start page shows it.</summary>
    public string? CombatWarningOf(ShipField ship) =>
        Plan is { CanStart: true, Combat: true } && ship is { Starter: true, Problem: null } ? CombatWarning : null;

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

    /// <summary>The suggested place in words, also while the place choice is a base or the Scene camera.</summary>
    public string SuggestedText => SuggestedTextOf(Plan);

    /// <summary>True when the page shows the row "Source of the place".</summary>
    public bool ShowsSource => ShowsSourceOf(Setting, Plan);

    /// <summary>The text of the start place choice of a play start and its plan. The Play start page and the tooltip of
    /// the Play button of a node show it.</summary>
    public static string PlaceTextOf(PlayStartSetting setting, StartPlan plan) => setting.PlaceChoice.Place switch
    {
        StartPlaceChoice.Base => $"At base {setting.PlaceChoice.Base}",
        StartPlaceChoice.Camera => "At the Scene camera",
        _ => $"Suggested: {SuggestedTextOf(plan)}",
    };

    /// <summary>True when the source of the suggested place shows: for the place choice "Suggested" of a plan that can
    /// start. The Play start page and the tooltip of the Play button of a node use it.</summary>
    public static bool ShowsSourceOf(PlayStartSetting setting, StartPlan plan) =>
        plan.CanStart && setting.PlaceChoice.Place == StartPlaceChoice.Suggested;

    private static string SuggestedTextOf(StartPlan plan) => plan.CanStart ? plan.Suggested.Text : "none";

    /// <summary>True when the page shows the two lists of the world setup: for an approximate start whose start system
    /// is known.</summary>
    public bool ShowsWorld => Plan is { CanStart: true, Kind: StartKind.Approximate, StartSystem: not null };

    /// <summary>The title of the list of the objects that the setup leaves out.</summary>
    public static string LeftOutLabel(int count) =>
        $"Objects that the setup leaves out ({count.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The title of the list of the ships that start next to the player.</summary>
    public static string CompanionsLabel(int count) =>
        $"Ships that start next to the player ({count.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>A row of the list of the objects that the setup leaves out: "{object} in {system}", "{object}. Trigger
    /// {trigger} makes it again at the start.", "Fuse {fuse} on {target} in {system}" or "Fuse {fuse} on {target}. No line
    /// makes {target}."</summary>
    public static string LeftOutText(LeftOutObject row) => row switch
    {
        { MadeAgainBy: { } again } => $"{row.Object}. Trigger {again} makes it again at the start.",
        { Kind: StartObjectKind.Fuse, System: null } => $"Fuse {row.Fuse} on {row.Object}. No line makes {row.Object}.",
        { Kind: StartObjectKind.Fuse } => $"Fuse {row.Fuse} on {row.Object} in {row.System}",
        _ => $"{row.Object} in {row.System}",
    };

    /// <summary>A row of the list of the ships that start next to the player: "{object}: {reasons}".</summary>
    public static string CompanionText(StartCompanion row)
    {
        var reasons = new List<string>();
        if (row.Signs.HasFlag(CompanionSigns.Orders))
            reasons.Add("Its orders keep it with the player.");
        if (row.Signs.HasFlag(CompanionSigns.PlayerFormation))
            reasons.Add("It flies in the formation of the player.");
        if (row.Signs.HasFlag(CompanionSigns.Jump))
            reasons.Add($"It jumped with the player from {row.JumpedFrom}.");
        if (row.Signs.HasFlag(CompanionSigns.Escort))
            reasons.Add("The player must stay near it.");
        if (row.Point == CompanionPoint.JumpHere)
            reasons.Add($"Its point comes from {row.PointTrigger}.");
        else if (row.Point == CompanionPoint.Near)
            reasons.Add("It is already near the player.");
        return $"{row.Object}: {string.Join(" ", reasons)}";
    }

    /// <summary>The story stage: its name and its number.</summary>
    public string StageText =>
        $"{StoryStages.NameOf(Plan.Stage)} ({Plan.Stage.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The setup triggers as read-only raw INI, or null for a start without them.</summary>
    public string? SetupIni => Plan.Setup.Count == 0 ? null : Plan.SetupPatch;

    /// <summary>The setup triggers as the text of the raw INI editor, or null for a start without them.</summary>
    public RawText? SetupText => setupText ??= SetupIni is { } ini ? RawText.Plain(ini) : null;

    private RawText? setupText;
}
