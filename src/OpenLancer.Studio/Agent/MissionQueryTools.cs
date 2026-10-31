using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;

namespace OpenLancer.Studio.Agent;

/// <summary>A name of a script that OpenLancer does not run yet, with its count of lines.</summary>
/// <param name="Name">The name of a condition or an action, or the group of the actions that never run.</param>
internal sealed record OpenLancerGapRow(string Name, int Lines, EngineMark Mark);

/// <summary>A row of <c>list_missions</c>: the row of the Missions browser and what OpenLancer runs of the
/// script.</summary>
/// <param name="Ref">The <c>msn:</c> reference of the script.</param>
/// <param name="Slot">The story slot that loads the script, for example <c>M02</c>, or null.</param>
/// <param name="ModId">The last mod that changes or replaces the script, or null.</param>
/// <param name="Errors">The errors that show: the errors of the active mod.</param>
/// <param name="Warnings">The warnings that show. A problem of the base game does not count.</param>
/// <param name="OpenLancer">What OpenLancer runs of the script, for example "Runs" or "2 lines do nothing".</param>
/// <param name="Gaps">The names that OpenLancer does not run yet. The retail game runs them.</param>
internal sealed record MissionListRow(string Ref, string? Slot, MissionScriptState State, string? ModId, int Triggers,
    int Errors, int Warnings, long Version, [property: JsonPropertyName("openlancer")] string OpenLancer,
    IReadOnlyList<OpenLancerGapRow> Gaps);

/// <summary>The result of <c>list_missions</c>: the story slots and the other scripts.</summary>
internal sealed record MissionsView(IReadOnlyList<MissionListRow> Scripts);

/// <summary>A group of the actions of a trigger: one chip of the summary level of the graph.</summary>
/// <param name="Group">The id of the action group, for example <c>spawn</c>, or <c>other</c>.</param>
/// <param name="Label">The short name of the group, as the chip shows it.</param>
/// <param name="Count">The number of actions of the group.</param>
internal sealed record ActionChipRow(string Group, string Label, int Count);

/// <summary>A line of a trigger in short form: its reference, its key, its plain name and its values by
/// position.</summary>
internal sealed record TriggerLineRow(string Ref, string Key, string? Plain, IReadOnlyList<string> Texts);

/// <summary>A trigger in the summary form of the graph (section 7.1 of the MCP1 design): the sentence of its condition
/// and the chips of its actions. <c>get_trigger</c> gives the full trigger.</summary>
/// <param name="Ref">The <c>trg:</c> reference.</param>
/// <param name="Event">The sentence of the condition that the game uses, or null without a condition.</param>
internal sealed record TriggerSummaryRow(string Ref, string Nickname, string? Event)
{
    public bool StartsWithMission { get; init; }

    public bool Repeats { get; init; }

    /// <summary>The system hint of the trigger, or null.</summary>
    public string? System { get; init; }

    public IReadOnlyList<ActionChipRow> Actions { get; init; } = [];

    /// <summary>The <c>trg:</c> references of the triggers that the trigger starts.</summary>
    public IReadOnlyList<string> Starts { get; init; } = [];

    public int Errors { get; init; }

    public int Warnings { get; init; }

    /// <summary>The lines of the trigger, with the argument <c>include_lines</c>, else null.</summary>
    public IReadOnlyList<TriggerLineRow>? Lines { get; init; }
}

/// <summary>A step of a completeness panel.</summary>
internal sealed record CompletenessView(string Title, int Done, int Total, IReadOnlyList<MissionStepView> Steps);

/// <summary>The result of <c>get_mission</c>: the summary and the completeness, and with the level <c>triggers</c> one
/// page of the triggers in summary form.</summary>
internal sealed record MissionView(MissionOverview Summary, CompletenessView Completeness)
{
    /// <summary>One page of the triggers, with the level <c>triggers</c>, else null.</summary>
    public Page<TriggerSummaryRow>? Triggers { get; init; }
}

/// <summary>The result of <c>mission_start_plan</c>: the start plan of the query. The plan is one property, because its
/// <c>target</c> is also a property of a tool error, and the output schema has the properties of both at its
/// root.</summary>
internal sealed record StartPlanResult(StartPlanView Plan);

/// <summary>
/// The mission query tools of section 7.1 of the MCP1 design. Each one calls a read query of the mission layer
/// (section 4.12 of the M6b design), so the agent and the mission studio read the same model.
/// </summary>
internal static class MissionQueryTools
{
    public static ToolStep ListMissions(ToolCall call)
    {
        var missions = call.Session.Missions;
        return ToolResult.Of(new MissionsView(missions.Queries.ListScripts().Select(row =>
        {
            var report = MissionSummary.OpenLancer(missions.Model(ScriptRef.Parse(row.Ref).Path).Index);
            return new MissionListRow(row.Ref, row.Slot, row.State, row.ModId, row.Triggers, row.Errors, row.Warnings,
                row.Version, report.Text, report.Gaps.Select(g => new OpenLancerGapRow(g.Name, g.Lines, g.Mark)).ToList());
        }).ToList()));
    }

    public static ToolStep GetMission(ToolCall call)
    {
        var session = call.Session;
        var script = ScriptRef.Parse(call.Args.Text("script")!);
        var summary = session.Missions.Queries.GetMission(script);
        var model = session.Missions.Model(script.Path);
        var report = MissionCompleteness.OfMission(model.Analysis);
        var view = new MissionView(summary, new CompletenessView(report.Title, report.Done, report.Total,
            report.Steps.Select(s => new MissionStepView(s.Name, s.State, s.Reason)).ToList()));
        if (call.Args.Text("level") != "triggers")
            return ToolResult.Of(view);
        var lines = call.Args.Flag("include_lines");
        // A trigger reference names the first trigger with its nickname, as the game does.
        var triggers = model.Index.Triggers.Where(t => !t.IsDuplicate).ToList();
        return ToolResult.Of(view with { Triggers = call.Paged(triggers, t => Summary(model, t, lines)) });
    }

    public static ToolStep GetTrigger(ToolCall call) => ToolResult.Of(
        Lines(call.Session.Missions.Queries.GetTrigger(TriggerRef.Parse(call.Args.Text("ref")!)), call.Args.Flag("include_lines")));

    public static ToolStep DescribeMissionName(ToolCall call)
    {
        var key = call.Args.Text("key")!.Trim();
        var palette = call.Session.Missions.Queries.ListPalette();
        return palette.FirstOrDefault(n => n.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } row
            ? ToolResult.Of(row)
            : throw new EditException(new EditError(EditErrorKind.NotFound, "NameNotFound",
                AgentTexts.Error("mission_name_none", ("key", key)) + ItemRefs.Hint(key, palette.Select(n => n.Name)))
            {
                Field = "key",
            });
    }

    /// <summary>The schema of the fields of an input of <c>mission_dry_run</c> (section 7.3 of the M6c design), without
    /// <c>kind</c>. The query reads each input and gives the reason for an input that does not fit.</summary>
    internal static IReadOnlyDictionary<string, JsonObject> DryRunFields { get; } = new Dictionary<string, JsonObject>
    {
        ["trigger"] = new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = TextParam.MaxLength },
        ["line"] = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["values"] = new JsonObject { ["type"] = "object" },
            },
            ["required"] = new JsonArray("key"),
            ["additionalProperties"] = false,
        },
        ["on"] = new() { ["type"] = "boolean" },
        ["count"] = new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = Data.Missions.Run.DryRun.MaxHeartbeats },
        ["until_timer"] = new() { ["type"] = "boolean" },
        ["system"] = new() { ["type"] = new JsonArray("string", "null") },
        ["value"] = new() { ["type"] = "boolean" },
    };

    /// <summary>The dry run query (section 7.2 of the M6c design): the inputs fold over the model of the script now. The
    /// query keeps no state, so the agent sends all inputs each time. The request reader of the JSON host reads the
    /// arguments, so the tool and the host read one request in one way.</summary>
    public static ToolStep DryRun(ToolCall call)
    {
        var request = new JsonObject();
        foreach (var name in new[] { "script", "inputs", "timer_bound", "since_step" })
        {
            if (call.Args.Node(name) is { } value)
                request[name] = value.DeepClone();
        }
        return ToolResult.Of(call.Session.Missions.Queries.DryRun(RunJson.ReadRequest(request)));
    }

    /// <summary>The targets of a start plan in this build. The retail targets come with the linked game (milestone
    /// M6c-3).</summary>
    internal static IReadOnlyList<string> StartTargets { get; } = [RunJson.OpenLancerTarget];

    /// <summary>The arguments of a start of a mission play: the input of <c>mission_start_plan</c> and of
    /// <c>mission_play</c>.</summary>
    internal static ToolInput StartInput { get; } = new(
        new TextParam("script", true, null, null),
        new TextParam("start", false, null, RunJson.MissionStartName),
        new TextParam("target", false, StartTargets, RunJson.OpenLancerTarget),
        new TextParam("start_place", false, null, RunJson.SuggestedPlace));

    /// <summary>
    /// The start plan query (section 7.2 of the M6c design): the plan of a start of a script now, as the Play start page
    /// shows it. A plan that cannot start is data: the status <c>refused</c> and the reason. The request reader of the
    /// JSON host reads the arguments, so <c>mission_start_plan</c> and a play of the same start read one request in one
    /// way.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a prefab, a start of another
    /// script or a place that is not valid. <see cref="EditErrorKind.NotFound"/> for a script or a trigger that does not
    /// resolve.</exception>
    public static ToolStep StartPlan(ToolCall call)
    {
        var read = StartRequest(call.Args);
        // An unknown trigger of the start names start. Each other error of the query is an error of the script.
        return ToolResult.Of(new StartPlanResult(EditTools.At("script", () => call.Session.Missions.Queries.StartPlan(read))));
    }

    /// <summary>The request of a start of the arguments of <see cref="StartInput"/>, read with the request reader of the
    /// JSON host (<see cref="RunJson.ReadStartRequest"/>). So <c>mission_start_plan</c> and <c>mission_play</c> read one
    /// start in one way.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a prefab, a start of another
    /// script or a place that is not valid.</exception>
    internal static StartPlanRequest StartRequest(ToolArgs args)
    {
        var script = EditTools.At("script", () => ScriptRef.Parse(args.Text("script")!));
        if (MissionRefs.IsPrefab(script.Path))
            throw ToolInput.Bad("script", AgentTexts.Error("mission_start_prefab", ("ref", script)));
        var request = new JsonObject();
        foreach (var name in new[] { "script", "target", "start_place" })
        {
            if (args.Text(name) is { } value)
                request[name] = value;
        }
        // The reader takes the trg: form. A sec: reference of a trigger section names the same trigger (section 5.6 of
        // the MCP1 design).
        if (args.Text("start") is { } start)
            request["start"] = start == RunJson.MissionStartName ? start : MissionEditTools.TriggerOf(start, "start").ToString();
        // The script is valid, so an error of the reader without a field is the read of the reference of the start.
        return EditTools.At("start", () => RunJson.ReadStartRequest(request));
    }

    /// <summary>A trigger in summary form: the sentence of its condition and one chip for each action group, in the
    /// order of the groups of the schema, as the graph shows them.</summary>
    private static TriggerSummaryRow Summary(MissionModel model, MissionTrigger trigger, bool lines)
    {
        var index = model.Index;
        var problems = model.Problems.Where(p => p.Target == trigger.Section.Ref && p.Origin?.Kind != OriginKind.BaseGame)
            .ToList();
        return new TriggerSummaryRow(new TriggerRef(model.Script, trigger.Nickname).ToString(), trigger.Nickname,
            trigger.Condition?.Sentence())
        {
            StartsWithMission = trigger.StartsActive,
            Repeats = trigger.Repeats,
            System = trigger.SystemHint,
            Actions = trigger.Actions.GroupBy(a => a.Name?.Group ?? "")
                .OrderBy(g => index.Schema.Group(g.Key)?.Order ?? int.MaxValue)
                .Select(g => new ActionChipRow(g.Key.Length > 0 ? g.Key : "other", index.Schema.Group(g.Key)?.Chip ?? "Other",
                    g.Count()))
                .ToList(),
            Starts = index.Links.Where(l => l.Source == trigger && l.Kind == MissionLinkKind.Start && l.Target != null)
                .Select(l => new TriggerRef(model.Script, l.Target!.Nickname).ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Errors = problems.Count(p => p.Severity == DiagnosticSeverity.Error),
            Warnings = problems.Count(p => p.Severity == DiagnosticSeverity.Warning),
            Lines = lines
                ? trigger.Rows.Select(r => new TriggerLineRow(
                    new RowRef(new ItemRef(model.Script, trigger.Section.Ref.Id), r.Entry).Reference, r.Key, r.Name?.Plain,
                    Enumerable.Range(0, r.Count).Select(i => r.Value(i) ?? "").ToList())).ToList()
                : null,
        };
    }

    /// <summary>A trigger with the raw texts of its lines only on request. The named values and the plain names stay,
    /// so the agent reads the actions without the raw lines (UI rule 5).</summary>
    private static MissionTriggerView Lines(MissionTriggerView trigger, bool raw) => raw
        ? trigger
        : trigger with { Lines = trigger.Lines.Select(l => l with { Texts = [] }).ToList() };
}
