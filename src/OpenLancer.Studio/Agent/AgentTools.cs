using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The agent tool table (section 5.3 of the MCP1 design): one row for each tool, in the fixed order of
/// <c>tools/list</c>. Both hosts serve this table, so a tool behaves the same in both (decision D700). A tool that waits
/// for another milestone is not in the table of this build (section 7.9).
/// </summary>
/// <remarks>This build has the query tools of section 7.1 (milestone MCP1-1a) and the dry run query of M6c-1. It also
/// has the edit tools of sections 7.2, 7.3 and 7.6 and the play tools of section 7.6 (milestone MCP1-2), and the view
/// tools of section 7.7 (milestone MCP1-1b).
/// The order of the rows follows the sections of the design, so a later part puts its tools at the place of its
/// section. The view tools are tools of the editor host.</remarks>
public static class AgentTools
{
    private static readonly ToolParam Cursor = new TextParam("cursor", false, null, null);
    private static readonly ToolParam Limit = new WholeParam("limit", 1);
    private static readonly ToolParam TextFilter = new TextParam("text", false, null, null);
    private static readonly ToolParam Width = new WholeParam("width", ViewTools.MinSide, ViewTools.DefaultWidth);
    private static readonly ToolParam Height = new WholeParam("height", ViewTools.MinSide, ViewTools.DefaultHeight);

    /// <summary>The tools in the order of <c>tools/list</c>.</summary>
    public static IReadOnlyList<StudioTool> All { get; } =
    [
        Query("studio_status", ToolGroup.Studio, ToolInput.None, typeof(StudioStatus), StatusQueries.Status),
        Query("list_systems", ToolGroup.Scene, new ToolInput(TextFilter, Cursor, Limit), typeof(Page<SystemRow>),
            SceneQueries.ListSystems),
        Query("get_system", ToolGroup.Scene, new ToolInput(Text("system", true)), typeof(SystemView), SceneQueries.GetSystem),
        Query("list_items", ToolGroup.Scene, new ToolInput(
                Text("system", true),
                new ChoicesParam("kinds", Names<SceneItemKind>()),
                new ChoicesParam("object_kinds", Names<SceneObjectKind>()),
                TextFilter,
                new GroupParam("near", [new PointParam("point", true), new NumberParam("radius", true, 0)]),
                new TextParam("origin", false, Names<ValueOrigin>(), null),
                Cursor, Limit),
            typeof(Page<SceneItemRow>), SceneQueries.ListItems),
        Query("get_item", ToolGroup.Studio,
            new ToolInput(Text("ref", true), new FlagParam("include_raw"), new FlagParam("include_used_by")),
            typeof(ItemView), ItemQueries.GetItem),
        Query("find_references", ToolGroup.Studio, new ToolInput(Text("ref"), Text("name"), Cursor, Limit),
            typeof(ReferencesView), ItemQueries.FindReferences),
        Query("list_values", ToolGroup.Studio, new ToolInput(
                new TextParam("list", true, ListRules.Retail.Lists.Keys.Order(StringComparer.Ordinal).ToList(), null),
                TextFilter, Text("system"), Text("script"), Cursor, Limit),
            typeof(Page<ValueRow>), ItemQueries.ListValues),
        Query("search_assets", ToolGroup.Scene, new ToolInput(
                new TextParam("kind", false, Names<AssetKind>(), null),
                Text("category"), TextFilter, Cursor, Limit),
            typeof(Page<AssetItemRow>), ItemQueries.SearchAssets),
        Query("list_prop_groups", ToolGroup.Scene, new ToolInput(TextFilter), typeof(PropGroupsView),
            ItemQueries.ListPropGroups),
        Query("list_missions", ToolGroup.Missions, ToolInput.None, typeof(MissionsView), MissionQueryTools.ListMissions),
        Query("get_mission", ToolGroup.Missions, new ToolInput(
                Text("script", true),
                new TextParam("level", false, ["summary", "triggers"], "summary"),
                new FlagParam("include_lines"),
                Cursor, Limit),
            typeof(MissionView), MissionQueryTools.GetMission),
        Query("get_trigger", ToolGroup.Missions, new ToolInput(Text("ref", true), new FlagParam("include_lines")),
            typeof(MissionTriggerView), MissionQueryTools.GetTrigger),
        Query("describe_mission_name", ToolGroup.Missions, new ToolInput(Text("key", true)),
            typeof(MissionPaletteName), MissionQueryTools.DescribeMissionName),
        Query("mission_dry_run", ToolGroup.Missions, new ToolInput(
                Text("script", true),
                new ObjectsParam("inputs", true, RunJson.KindNames, MissionQueryTools.DryRunFields),
                new TextParam("timer_bound", false, ["shortest", "longest"], "shortest"),
                new WholeParam("since_step", 0, 0)),
            typeof(DryRunView), MissionQueryTools.DryRun),
        Query("list_cutscenes", ToolGroup.Cutscenes,
            new ToolInput(new TextParam("category", false, Names<CutsceneFolder>(), null), TextFilter, Cursor, Limit),
            typeof(Page<CutsceneRow>), CutsceneQueries.ListCutscenes),
        Query("get_cutscene", ToolGroup.Cutscenes, new ToolInput(Text("ref", true)), typeof(CutsceneView),
            CutsceneQueries.GetCutscene),
        Query("get_raw_ini", ToolGroup.Studio, new ToolInput(Text("ref"), Text("file")), typeof(RawIniView),
            ItemQueries.GetRawIni),
        Query("check", ToolGroup.Studio, new ToolInput(
                new TextParam("scope", false, Names<CheckScope>().Where(s => s != "target").ToList(), "all"),
                Text("ref"),
                new TextParam("target", false, ["vanilla-1.1", "hde-0.7.1"], null),
                new FlagParam("include_base_game"),
                Cursor, Limit),
            typeof(CheckView), CheckQueries.Check),
        Query("get_console", ToolGroup.Studio, new ToolInput(
                Text("source"),
                new TextParam("severity", false, Names<AgentSeverity>(), "info"),
                new FlagParam("include_base_game"),
                new WholeParam("since", 0),
                Limit),
            typeof(ConsoleView), CheckQueries.Console, ToolHosts.Editor),
        Edit("set_fields", ToolHints.ChangingIdempotent,
            EditTools.Input(false, Text("ref", true), new FieldsParam("fields", true)), ItemEditTools.SetFields),
        Edit("set_text", ToolHints.Changing, EditTools.Input(false,
                Text("ref", true), Text("field", true), new TextParam("text", true, null, null, ItemEditTools.MaxText)),
            ItemEditTools.SetText),
        Edit("rename_item", ToolHints.Changing, EditTools.Input(true, Text("ref", true), Text("new_name", true)),
            ItemEditTools.RenameItem),
        Edit("delete_items", ToolHints.Changing, EditTools.Input(true, new TextListParam("refs", true)),
            ItemEditTools.DeleteItems),
        Edit("duplicate_items", ToolHints.Adding,
            EditTools.Input(false, new TextListParam("refs", true), new PointParam("offset", false)),
            ItemEditTools.DuplicateItems),
        Edit("scene_create_object", ToolHints.Adding, EditTools.Input(false,
                Text("system", true),
                new TextParam("template", true, SceneEditTools.ObjectTemplates, null),
                Text("archetype", true),
                new PointParam("position", true),
                new PointParam("rotation", false),
                Text("nickname"),
                new TextParam("name", false, null, null, SceneEditTools.MaxName),
                Text("parent"),
                Text("base"),
                new FieldsParam("fields", false)),
            SceneEditTools.CreateObject, ToolGroup.Scene),
        Edit("scene_create_zone", ToolHints.Adding, EditTools.Input(false,
                Text("system", true),
                new TextParam("shape", false, SceneEditTools.Shapes, SceneEditTools.Shapes[0]),
                new PointParam("position", true),
                new NumbersParam("size", false, 1, 3),
                new PointParam("rotation", false),
                Text("nickname"),
                new FieldsParam("fields", false)),
            SceneEditTools.CreateZone, ToolGroup.Scene),
        Edit("scene_create_light", ToolHints.Adding, EditTools.Input(false,
                Text("system", true), new PointParam("position", true), Text("nickname"), new FieldsParam("fields", false)),
            SceneEditTools.CreateLight, ToolGroup.Scene),
        Edit("scene_spawn_asset", ToolHints.Adding, EditTools.Input(false,
                Text("system", true), Text("asset", true), new PointParam("position", true), Text("parent"), Text("base")),
            SceneEditTools.SpawnAsset, ToolGroup.Scene),
        Edit("scene_transform", ToolHints.Changing, EditTools.Input(false,
                new RowsParam("moves", true, [
                    Text("ref", true),
                    new PointParam("position", false),
                    new PointParam("rotation", false),
                    new NumbersParam("size", false, 1, 3),
                ]),
                new TextParam("mode", false, SceneEditTools.Modes, SceneEditTools.Modes[0])),
            SceneEditTools.Transform, ToolGroup.Scene),
        Edit("scene_set_component", ToolHints.Changing, EditTools.Input(false,
                Text("ref", true),
                new TextParam("component", true,
                    SceneEditTools.Components.Select(c => Snake(c.ToString())).ToList(), null),
                new TextParam("action", true, SceneEditTools.Actions, null),
                Text("value")),
            SceneEditTools.SetComponent, ToolGroup.Scene),
        Edit("scene_place_wreck", ToolHints.Adding, EditTools.Input(false,
                Text("system", true),
                new PointParam("position", true, BuilderTools.MaxCoordinate),
                new PointParam("rotation", false),
                Text("archetype", true),
                Text("ship_archetype", true),
                new RowsParam("loot", false, BuilderTools.CargoRow),
                new RowsParam("equipment", false, BuilderTools.EquipmentRow),
                Text("nickname"),
                new TextParam("name", false, null, null, SceneEditTools.MaxName)),
            BuilderTools.PlaceWreck, ToolGroup.Scene),
        Edit("scene_place_patrol_route", ToolHints.Adding, EditTools.Input(false,
            [
                Text("system", true),
                Text("name", true),
                new TextParam("usage", false, BuilderTools.Usages, BuilderTools.Usages[0]),
                new PointsParam("points", true, 2, BuilderTools.MaxCoordinate),
                new NumberParam("radius", false, 1),
                .. BuilderTools.EncounterBlock(false),
                new FieldsParam("fields", false),
            ]),
            BuilderTools.PlaceRoute, ToolGroup.Scene),
        Edit("scene_set_encounters", ToolHints.Changing,
            EditTools.Input(false, [Text("zone", true), .. BuilderTools.EncounterBlock(true)]),
            BuilderTools.SetEncounters, ToolGroup.Scene),
        Edit("prop_group_place", ToolHints.Adding, EditTools.Input(false,
                Text("group", true), Text("system", true), new PointParam("anchor", true), Text("parent"),
                new FlagParam("leave_out_error_lines")),
            SceneEditTools.PlaceGroup, ToolGroup.Scene),
        AssetWrite("prop_group_save", ToolHints.Adding,
            PropGroupTools.Input(new TextListParam("refs", true), Text("nickname"), Text("name")), PropGroupTools.Save),
        AssetWrite("prop_group_rename", ToolHints.Changing,
            PropGroupTools.Input(Text("group", true), Text("nickname", true)), PropGroupTools.Rename),
        AssetWrite("prop_group_delete", ToolHints.Changing, PropGroupTools.Input(Text("group", true)),
            PropGroupTools.Delete),
        Edit("create_loadout", ToolHints.Adding, EditTools.Input(false,
                Text("nickname", true),
                Text("archetype", true),
                new RowsParam("equipment", false, BuilderTools.EquipmentRow),
                new RowsParam("cargo", false, BuilderTools.CargoRow)),
            BuilderTools.CreateLoadout),
        Edit("create_from_template", ToolHints.Adding, EditTools.Input(false,
                new TextParam("kind", true, DataEditTools.TemplateKinds, null),
                Text("template", true),
                Text("nickname", true)),
            DataEditTools.CreateFromTemplate),
        SessionTool("save", ToolHints.Changing, ToolInput.None, typeof(SaveView), SaveTools.Save),
        SessionTool("undo", ToolHints.Changing, ToolInput.None, typeof(HistoryStepView), HistoryTools.Undo),
        SessionTool("redo", ToolHints.Changing, ToolInput.None, typeof(HistoryStepView), HistoryTools.Redo),
        // A step can remove data, so the hints of the batch are the hints of its worst step.
        SessionTool("edit_batch", ToolHints.Changing, new ToolInput(Text("label"),
                new StepsParam("steps", () => BatchTool.StepTools), EditTools.Preview, EditTools.WithPatch),
            typeof(BatchResult), BatchTool.Run),
        SessionTool("export_mod", ToolHints.Changing, new ToolInput(
                new ChoicesParam("targets", PublishTools.Targets),
                new ChoicesParam("formats", PublishTools.Formats),
                new FlagParam("replace_version")),
            typeof(ExportView), PublishTools.Export, limits => TimeSpan.FromMinutes(limits.ExportMinutes)),
        SessionTool("publish_checklist", ToolHints.Query, ToolInput.None, typeof(ChecklistView), PublishTools.Checklist),
        PlayTool("play", ToolHints.Changing, new ToolInput(
                new TextParam("start", true, PlayTools.Starts, null),
                Text("system"),
                new PointParam("position", false, BuilderTools.MaxCoordinate),
                new PointParam("orientation", false),
                Text("base")),
            typeof(PlayRequestView), PlayTools.Play),
        // A stop removes no data and starts nothing. Pause does not keep a game of the agent running.
        PlayTool("play_stop", new ToolHints(ReadOnly: false, Destructive: false, Idempotent: false), ToolInput.None,
            typeof(PlayStopView), PlayTools.Stop, runsInPause: true),
        View("open_view", ToolGroup.Studio, ToolHints.View, new ToolInput(Text("system"), Text("mission"), Text("cutscene")),
            typeof(OpenedView), ViewTools.OpenView),
        View("select_items", ToolGroup.Studio, ToolHints.View,
            new ToolInput(new TextListParam("refs", true), new FlagParam("frame", true)),
            typeof(SelectionView), ViewTools.SelectItems),
        View("render_scene", ToolGroup.Scene, ToolHints.Query, new ToolInput(
                Text("system"),
                new GroupParam("camera", [
                    new PointParam("position", true, ShotCamera.MaxCoordinate),
                    new PointParam("look_at", true, ShotCamera.MaxCoordinate),
                    new NumberParam("fov", false, ShotCamera.MinFov, ShotCamera.MaxFov),
                ]),
                new TextListParam("frame", false),
                Width, Height,
                new FlagParam("zones", true)),
            typeof(SceneShotView), ViewTools.RenderScene),
        View("render_cutscene", ToolGroup.Cutscenes, ToolHints.Query,
            new ToolInput(Text("cutscene", true), new NumberParam("time", false, 0), Text("camera"), Width, Height),
            typeof(CutsceneShotView), ViewTools.RenderCutscene),
        View("capture_panel", ToolGroup.Studio, ToolHints.Query,
            new ToolInput(new TextParam("panel", true, Names<AgentPanel>(), null)),
            typeof(PanelShotView), ViewTools.CapturePanel),
    ];

    /// <summary>The tool with a name, or null.</summary>
    public static StudioTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>The tools of a host that only reads: each tool that changes nothing (section 6.2 of the MCP1 design).
    /// An editor tool stays in the list and gives <c>not_available</c> in the host without a window.</summary>
    public static IReadOnlyList<StudioTool> ReadOnly { get; } = All.Where(t => t.Hints.ReadOnly).ToList();

    /// <summary>
    /// Runs the session part of a call on the thread that owns the session: the host and the group checks, the check
    /// of the arguments, and the handler. A refusal is a tool error with its kind and its detail code. A result gives
    /// the revision of the session part (section 8.6, item 3 of the MCP1 design).
    /// </summary>
    /// <remarks>
    /// <para>An exception of a handler or of a tail that is not a refusal is a fault of the server. The call then gives
    /// the error <c>step_failed</c> with the detail <c>ToolFailed</c>, so the agent reads a code and the audit log has
    /// the call. An edit tool runs its commands in a batch, and the batch cancels at the exception. So nothing
    /// changed.</para>
    /// <para>Pause refuses each tool that changes the data, writes files or starts a play (decision D734). The host gets
    /// each refusal of a tool that changes something, except of a preview, for its Console (section 8.5 of the MCP1
    /// design). The error <c>needs_confirmation</c> asks and does not refuse (section 8.1), so the host does not get
    /// it.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The table has no tool with the name. The SDK binding refuses an unknown
    /// tool before it queues the call.</exception>
    public static ToolStep Run(AgentHost host, string name, JsonObject? arguments)
    {
        var tool = Find(name) ?? throw new ArgumentException($"The tool table has no tool {name}.", nameof(name));
        // The checks before the read of the arguments read the flag preview from the JSON, so a refused preview adds no
        // Console entry either.
        if (Unavailable(host, tool) is { } unavailable)
            return Refuse(host, tool, unavailable, AsksPreview(arguments));
        if (host.Paused && tool.Pausable)
        {
            return Refuse(host, tool, new EditError(EditErrorKind.Paused, "Paused", AgentTexts.Error("paused")),
                AsksPreview(arguments));
        }
        ToolStep step;
        ToolArgs? args = null;
        try
        {
            args = tool.Input.Read(name, arguments);
            step = tool.Run(new ToolCall(host, name, args));
        }
        catch (EditException e)
        {
            // Arguments that do not pass the input check give no args. The flag then comes from the JSON.
            return Refuse(host, tool, e.Error, args?.Flag("preview") ?? AsksPreview(arguments));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failed(name, e);
        }
        var revision = host.Session?.Revision;
        if (step.Result is { } result)
            return result.WithRevision(revision);
        var tail = step.Tail!;
        return ToolStep.Then(async (cancel, progress) =>
        {
            try
            {
                return (await tail(cancel, progress).ConfigureAwait(false)).WithRevision(revision);
            }
            catch (EditException e)
            {
                return ToolResult.Failure(e.Error);
            }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
            {
                return Failed(name, e);
            }
        });
    }

    /// <summary>The error of a tool that the host does not have, or of a tool of a group that the user hides, or
    /// null.</summary>
    internal static EditError? Unavailable(AgentHost host, StudioTool tool)
    {
        var hosts = host.Kind == AgentHostKind.Editor ? ToolHosts.Editor : ToolHosts.Headless;
        if ((tool.Hosts & hosts) == 0)
            return new EditError(EditErrorKind.NotAvailable, "EditorOnly", AgentTexts.Error("editor_only", ("tool", tool.Name)));
        return host.HiddenGroups.Contains(tool.Group) ? GroupHidden(tool.Group) : null;
    }

    /// <summary>True when the arguments of a call ask for a preview: <c>"preview": true</c>.</summary>
    internal static bool AsksPreview(JsonObject? arguments) =>
        arguments?["preview"] is JsonValue value && value.TryGetValue<bool>(out var preview) && preview;

    /// <summary>The tool error of a refusal. The host gets the refusal of a tool that changes data, except of a preview
    /// and of a question (<c>needs_confirmation</c>), for its Console.</summary>
    private static ToolResult Refuse(AgentHost host, StudioTool tool, EditError error, bool preview)
    {
        if (tool.Changes && !preview && error.Kind != EditErrorKind.NeedsConfirmation)
            host.Refused(tool.Name, error);
        return ToolResult.Failure(error);
    }

    /// <summary>The error of a tool of a group that the user hides (decision D725 of the MCP1 design). A view tool and
    /// an edit tool also give it for an item of such a group.</summary>
    internal static EditError GroupHidden(ToolGroup group) => new(EditErrorKind.NotAvailable, "GroupHidden",
        AgentTexts.Error("group_hidden", ("group", Snake(group.ToString()))));

    /// <summary>The error of a call whose handler or tail failed with an exception (see <see cref="Run"/>).</summary>
    internal static ToolResult Failed(string tool, Exception e) => ToolResult.Failure(EditErrorKind.StepFailed, "ToolFailed",
        AgentTexts.Error("tool_failed", ("tool", tool), ("reason", e.Message)));

    /// <summary>The snake case of an enum name, as the JSON of the results writes it, for example
    /// <c>lower_mod</c>.</summary>
    internal static string Snake(string name) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    /// <summary>The JSON names of the values of an enum, in the order of the enum, for the words of an argument. The
    /// results write the same names.</summary>
    internal static IReadOnlyList<string> Names<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => Snake(v.ToString())).ToList();

    private static TextParam Text(string name, bool required = false) => new(name, required, null, null);

    private static StudioTool Query(string name, ToolGroup group, ToolInput input, Type output, Func<ToolCall, ToolStep> run,
        ToolHosts hosts = ToolHosts.Both) => new(name, ToolKind.Query, group, hosts, ToolHints.Query, input, output, run);

    /// <summary>A view tool (section 7.7 of the MCP1 design). Only the editor host has the views.</summary>
    private static StudioTool View(string name, ToolGroup group, ToolHints hints, ToolInput input, Type output,
        Func<ToolCall, ToolStep> run) => new(name, ToolKind.View, group, ToolHosts.Editor, hints, input, output, run);

    /// <summary>A tool of the session as a whole, of the group Studio (section 7.6 of the MCP1 design), for example
    /// <c>undo</c>. It cannot be a step of <c>edit_batch</c>.</summary>
    /// <param name="tail">The time limit of the tail of the tool, or null for the tail limit of the limits.</param>
    private static StudioTool SessionTool(string name, ToolHints hints, ToolInput input, Type output,
        Func<ToolCall, ToolStep> run, Func<AgentLimits, TimeSpan>? tail = null) =>
        new(name, ToolKind.Session, ToolGroup.Studio, ToolHosts.Both, hints, input, output, run)
        {
            TailLimit = tail ?? StudioTool.TailOfLimits,
        };

    /// <summary>A play tool of section 7.6 of the MCP1 design, in the group Play. Only the editor host can play
    /// (decision D739).</summary>
    /// <param name="runsInPause">True for a tool that Pause does not refuse (see <see cref="StudioTool.RunsInPause"/>).</param>
    private static StudioTool PlayTool(string name, ToolHints hints, ToolInput input, Type output,
        Func<ToolCall, ToolStep> run, bool runsInPause = false) =>
        new(name, ToolKind.Play, ToolGroup.Play, ToolHosts.Editor, hints, input, output, run) { RunsInPause = runsInPause };

    /// <summary>An edit tool (sections 7.2 and 7.3 of the MCP1 design). Its result is the result of the command layer,
    /// and it can be a step of <c>edit_batch</c>. A generic edit tool and a data tool are in the group Studio.</summary>
    private static StudioTool Edit(string name, ToolHints hints, ToolInput input, Func<ToolCall, ToolStep> run,
        ToolGroup group = ToolGroup.Studio) =>
        new(name, ToolKind.Edit, group, ToolHosts.Both, hints, input, typeof(EditResult), run);

    /// <summary>An asset write of a prop group (section 7.3 of the MCP1 design), in the group Scene. It writes files at
    /// once, outside the edit history, so it cannot be a step of <c>edit_batch</c>. Its result has the form of the
    /// result of an edit.</summary>
    private static StudioTool AssetWrite(string name, ToolHints hints, ToolInput input, Func<ToolCall, ToolStep> run) =>
        new(name, ToolKind.AssetWrite, ToolGroup.Scene, ToolHosts.Both, hints, input, typeof(EditResult), run);
}
