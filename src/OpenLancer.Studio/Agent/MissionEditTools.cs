using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The mission edit tools of section 7.4 of the MCP1 design. Each tool runs mission commands of the M6b layer through
/// <see cref="EditScope.RunMission"/>, in the batch of the call. So a call is one Undo step with the edit origin Agent,
/// it can be a preview or a step of <c>edit_batch</c>, and it runs in strict mode (decision D681 of M6b).
/// </summary>
internal static class MissionEditTools
{
    /// <summary>The detail code of a request that turns strict mode off.</summary>
    public const string StrictMode = "StrictMode";

    /// <summary>The detail code of a <c>mission_edit</c> call with more commands than the limit.</summary>
    public const string MissionCommands = "MissionCommands";

    /// <summary>The JSON Schema of a mission batch (section 4.13 of the M6b design), made once at the first use. It has
    /// about 340 definitions, so the tool table does not make it when it loads.</summary>
    private static readonly Lazy<JsonObject> BatchSchema = new(MissionJson.Schema);

    /// <summary>
    /// The arguments of <c>mission_edit</c>: the fields of a mission batch, flat, with the schemas of
    /// <c>MissionJson.Schema()</c>, and the common arguments of an edit tool. The definitions of the batch schema go to
    /// the root of the input schema, because each <c>$ref</c> of the commands names <c>#/$defs/...</c>.
    /// </summary>
    /// <remarks>The label, <c>if_revision</c> and <c>confirm</c> are the common arguments of the edit tools, so they have
    /// the same form as in the other tools. A call without a label gets the label of its one command, or "Mission
    /// edit".</remarks>
    public static ToolInput EditInput { get; } = new([
        .. EditTools.Input(true,
            new TextParam("label", false, null, null),
            new SchemaParam("commands", true, JsonValueKind.Array, () => Property("commands")),
            new SchemaParam("options", false, JsonValueKind.Object, Options),
            new SchemaParam("expect_versions", false, JsonValueKind.Object, () => Property("expect_versions"))).Parameters,
    ])
    {
        Definitions = () => (JsonObject)BatchSchema.Value["$defs"]!,
    };

    /// <summary>
    /// The tool <c>mission_edit</c>: runs a mission batch of any commands of the M6b layer as one Undo step. The checks
    /// that need no session come first: strict mode, the command limit and the read of the commands.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for
    /// <c>reject_new_errors: false</c>, an unknown field, an unknown kind or a reference that is not valid.
    /// <see cref="EditErrorKind.Limit"/> for more commands than the limit. Each error of the edit call.</exception>
    public static ToolStep Edit(ToolCall call)
    {
        var args = call.Args;
        // After the read of a batch, false and a missing field are the same. So the check reads the JSON of the call.
        if (args.Node("options")?["reject_new_errors"] is JsonValue strict && strict.GetValueKind() == JsonValueKind.False)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, StrictMode,
                AgentTexts.Error("mission_strict")) { Field = "options.reject_new_errors" });
        }
        var commands = args.Node("commands")!.AsArray();
        // A command group counts as one command. The section limit and the size limit of a request bound its work.
        if (commands.Count > call.Limits.BatchSteps)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, MissionCommands,
                AgentTexts.Error("limit_commands", ("max", call.Limits.BatchSteps))) { Field = "commands" });
        }
        var batch = Read(args, commands);
        return EditTools.Apply(call, scope => scope.RunMission(batch, "commands"));
    }

    /// <summary>The batch of the arguments, read by the JSON reader of the mission layer. An error of a command names
    /// the command by its index, for example <c>commands[2]</c>.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: see <see cref="Edit"/>.</exception>
    private static MissionBatch Read(ToolArgs args, JsonArray commands)
    {
        var read = new List<MissionCommand>(commands.Count);
        for (var i = 0; i < commands.Count; i++)
        {
            try
            {
                read.Add(MissionJson.ReadCommand(commands[i]?.ToJsonString() ?? "null"));
            }
            catch (EditException e)
            {
                throw new EditException(e.Error with { Index = i, Field = $"commands[{i}]" });
            }
        }
        var shell = new JsonObject { ["label"] = args.Text("label") ?? "", ["commands"] = new JsonArray() };
        foreach (var name in new[] { "options", "expect_versions" })
        {
            if (args.Node(name) is { } value)
                shell[name] = value.DeepClone();
        }
        try
        {
            return MissionJson.ReadBatch(shell.ToJsonString()) with { Commands = read };
        }
        catch (EditException e) when (e.Error.Detail == MissionErrors.UnknownField)
        {
            // The input check refuses an unknown argument, so an unknown field is a field of the options.
            throw new EditException(e.Error with { Field = "options." + e.Error.Field });
        }
    }

    /// <summary>The schema of a field of the batch, without its help: <c>agent-tools.json</c> gives the help of each
    /// argument.</summary>
    private static JsonObject Property(string name)
    {
        var property = (JsonObject)BatchSchema.Value["properties"]![name]!.DeepClone();
        property.Remove("description");
        return property;
    }

    /// <summary>The schema of the options. The tool always runs in strict mode, so the default of
    /// <c>reject_new_errors</c> is true here.</summary>
    private static JsonObject Options()
    {
        var options = Property("options");
        options["properties"]!["reject_new_errors"]!["default"] = true;
        return options;
    }

    // ---- The trigger tools ----

    /// <summary>The detail code of a trigger or a line of another script or trigger than the edit needs.</summary>
    public const string OtherItem = "OtherItem";

    /// <summary>The most actions of one trigger tool call (section 7.8 of the MCP1 design).</summary>
    public const int MaxActions = 64;

    /// <summary>The form of a new mission name of section 4.3 of the M6b design: letters, digits, <c>_</c> and
    /// <c>-</c>, at most 47 characters. The command also refuses a name that is a number.</summary>
    private static readonly string NamePattern = $"^[A-Za-z0-9_-]{{1,{MissionNames.MaxName}}}$";

    /// <summary>The row of the start line of the old target in a trigger that <see cref="InsertTriggerOnLink"/> makes:
    /// its lines are the nickname, the condition and that start line.</summary>
    private const int InsertedStartRow = 2;

    /// <summary>The details of the errors of a named value of a line.</summary>
    private static readonly HashSet<string> ValueDetails =
    [
        MissionErrors.NoSuchValue, MissionErrors.ValueKind, MissionErrors.WordNotInSet, MissionErrors.WholeNumber,
        MissionErrors.PatchText,
    ];

    private static readonly ToolParam Nickname =
        new TextParam("nickname", false, null, null, MissionNames.MaxName, NamePattern);

    public static ToolInput AddTriggerInput { get; } = WithLines(
        new TextParam("script", true, null, null),
        Nickname,
        new LineParam("condition", true),
        new LinesParam("actions", MaxActions),
        new FlagParam("starts_with_mission"),
        new FlagParam("repeats"),
        new TextParam("system", false, null, null),
        new TextListParam("started_by", false));

    public static ToolInput SetConditionInput { get; } = WithLines(
        new TextParam("trigger", true, null, null), new LineParam("condition", true));

    public static ToolInput AddActionInput { get; } = WithLines(
        new TextParam("trigger", true, null, null), new LineParam("action", true), new TextParam("before", false, null, null));

    public static ToolInput SetActionInput { get; } = new(EditTools.Input(false,
        new TextParam("line", true, null, null), new MissionValuesParam("values", true),
        new TextParam("expect_key", false, null, null)).Parameters.ToArray());

    public static ToolInput MoveActionInput { get; } = new(EditTools.Input(false,
        new TextParam("line", true, null, null), new TextParam("before", false, null, null)).Parameters.ToArray());

    public static ToolInput LinkTriggersInput { get; } = new(EditTools.Input(false,
        new TextParam("from", true, null, null),
        new TextParam("to", true, null, null),
        new TextParam("kind", false, AgentTools.Names<LinkKind>(), "start"),
        new FlagParam("remove")).Parameters.ToArray());

    public static ToolInput InsertTriggerInput { get; } = WithLines(
        new TextParam("line", true, null, null), Nickname, new LineParam("condition", true),
        new LinesParam("actions", MaxActions));

    /// <summary>
    /// The tool <c>mission_add_trigger</c>: <see cref="AddTrigger"/> with the condition and the actions, then a Start
    /// link from each trigger of <c>started_by</c>, in one request. A link is the command of the link gesture of the
    /// graph (see <see cref="StartLink"/>). The triggers of <c>started_by</c> must be in the script. Without a nickname,
    /// a trigger with starters gets the made name of <see cref="MissionNaming.Free"/>, as the gesture of the graph that
    /// adds and links a trigger does.
    /// </summary>
    public static ToolStep NewTrigger(ToolCall call)
    {
        var args = call.Args;
        var script = EditTools.At("script", () => ScriptRef.Parse(args.Text("script")!));
        var condition = MissionLineInput.TriggerLine(args.Node("condition")!, "condition", action: false);
        var actions = Lines(args, "actions");
        var starters = (args.Texts("started_by") ?? [])
            .Select((text, i) => new TriggerRef(script.Path, NameIn(text, script.Path, $"started_by[{i}]"))).ToList();
        return EditTools.Apply(call, scope =>
        {
            var analysis = scope.Session.Missions.AnalysisOf(script.Path);
            var nickname = args.Text("nickname") ?? (starters.Count > 0 ? FreeTriggerName(analysis, script.Path) : null);
            var add = new AddTrigger(script)
            {
                Nickname = nickname,
                Condition = condition,
                Actions = actions,
                StartsWithMission = args.Flag("starts_with_mission"),
                Repeats = args.Flag("repeats"),
                System = args.Text("system"),
            };
            MissionCommand[] commands = [add, .. starters.Select(s => StartLink(analysis, s, nickname!, LinkKind.Start))];
            scope.RunMission(new MissionBatch(add.HistoryLabel, commands), error => error.Index is > 0 and var i
                ? $"started_by[{i - 1}]"
                : NameOr(error, error.Detail == MissionErrors.PatchText ? "system" : "script"));
        });
    }

    /// <summary>The tool <c>mission_set_condition</c>: <see cref="SetCondition"/>.</summary>
    public static ToolStep ChangeCondition(ToolCall call)
    {
        var args = call.Args;
        var trigger = TriggerOf(args.Text("trigger")!, "trigger");
        var condition = MissionLineInput.TriggerLine(args.Node("condition")!, "condition", action: false);
        return EditTools.Apply(call, scope =>
            scope.RunMission(new MissionBatch("", [new SetCondition(trigger, condition)]), _ => "trigger"));
    }

    /// <summary>The tool <c>mission_add_action</c>: <see cref="AddAction"/>, before a line of the same trigger or at
    /// the end.</summary>
    public static ToolStep NewAction(ToolCall call)
    {
        var args = call.Args;
        var trigger = TriggerOf(args.Text("trigger")!, "trigger");
        var action = MissionLineInput.TriggerLine(args.Node("action")!, "action", action: true);
        var before = args.Text("before") is { } text ? RowIn(text, trigger, "before") : (int?)null;
        return EditTools.Apply(call, scope =>
            scope.RunMission(new MissionBatch("", [new AddAction(trigger, action) { Before = before }]),
                error => error.Detail is MissionErrors.RowNotFound or MissionErrors.BadPlace ? "before" : "trigger"));
    }

    /// <summary>The tool <c>mission_set_action</c>: <see cref="SetActionValue"/> for each value, in one request. With
    /// <c>expect_key</c>, a line with another key refuses with <c>RowChanged</c>.</summary>
    public static ToolStep SetActionValues(ToolCall call)
    {
        var args = call.Args;
        var row = EditTools.At("line", () => RowRef.Parse(args.Text("line")!)) with { ExpectKey = args.Text("expect_key") };
        var values = args.Node("values")!.AsObject()
            .Select(v => (Name: v.Key, Value: MissionLineInput.Value(v.Value!))).ToList();
        var label = values.Count == 1 ? "" : "Set " + string.Join(", ", values.Select(v => v.Name));
        var commands = values.Select(v => (MissionCommand)new SetActionValue(row, v.Name, v.Value)).ToList();
        return EditTools.Apply(call, scope => scope.RunMission(new MissionBatch(label, commands),
            error => error.Index is { } i && ValueDetails.Contains(error.Detail ?? "") ? $"values.{values[i].Name}" : "line"));
    }

    /// <summary>The tool <c>mission_move_action</c>: <see cref="ReorderAction"/>, before a line of the same trigger or
    /// to the end. An error names <c>before</c> when its row is the cause: a row that the trigger does not have, a
    /// place before the condition, or a line that is not an action.</summary>
    public static ToolStep MoveAction(ToolCall call)
    {
        var args = call.Args;
        var row = EditTools.At("line", () => RowRef.Parse(args.Text("line")!));
        var before = args.Text("before") is { } text ? RowIn(text, row.Item, "before") : (int?)null;
        return EditTools.Apply(call, scope =>
            scope.RunMission(new MissionBatch("", [new ReorderAction(row) { Before = before }]), error => error.Detail switch
            {
                // A row error of the argument line has a target. A row error of before has none.
                MissionErrors.RowNotFound or MissionErrors.BadPlace when error.Target == null => "before",
                // The command checks the line first. So a refused line with another row is the line of before.
                MissionErrors.NotAnAction when error.Target is { } target && RowRef.Parse(target).Row != row.Row => "before",
                _ => "line",
            }));
    }

    /// <summary>
    /// The tool <c>mission_link_triggers</c>: the command of the link gesture of the graph, or of its "Remove link" with
    /// <c>remove</c>. A plain trigger gets <see cref="LinkTriggers"/> or <see cref="UnlinkTriggers"/>. A member of a
    /// linked prefab instance links through the output of its role with <see cref="SetPrefabPort"/> (see
    /// <see cref="PrefabLinks.LinkCommand"/>). The target is a trigger of the script of the source. The command does not
    /// look it up: a link to a trigger that does not exist adds the error M05, and strict mode refuses it. The strict
    /// check of <c>edit_batch</c> runs at the end, so a step can link to a trigger that a later step adds.
    /// </summary>
    public static ToolStep Link(ToolCall call)
    {
        var args = call.Args;
        var from = TriggerOf(args.Text("from")!, "from");
        var to = NameIn(args.Text("to")!, from.Script, "to");
        var kind = Enum.Parse<LinkKind>(args.Text("kind")!, ignoreCase: true);
        var remove = args.Flag("remove");
        // The label of the plain command, also for a link through an output.
        var label = (remove ? (MissionCommand)new UnlinkTriggers(from, to, kind) : new LinkTriggers(from, to, kind)).HistoryLabel;
        return EditTools.Apply(call, scope =>
        {
            var analysis = scope.Session.Missions.AnalysisOf(from.Script);
            var command = remove ? StopLink(analysis, from, to, kind) : StartLink(analysis, from, to, kind);
            scope.RunMission(new MissionBatch(label, [command]),
                error => error.Detail == MissionErrors.LinkNotFound ? "to" : "from");
        });
    }

    /// <summary>
    /// The tool <c>mission_insert_trigger</c>: <see cref="InsertTriggerOnLink"/>, then <see cref="AddAction"/> for each
    /// action before the start line of the old target, in one request. So the new trigger runs its actions, then starts
    /// the old target. Without a nickname, a trigger with actions gets the made name of
    /// <see cref="MissionNaming.Free"/>.
    /// </summary>
    public static ToolStep InsertTrigger(ToolCall call)
    {
        var args = call.Args;
        var row = EditTools.At("line", () => RowRef.Parse(args.Text("line")!));
        var condition = MissionLineInput.TriggerLine(args.Node("condition")!, "condition", action: false);
        var actions = Lines(args, "actions");
        return EditTools.Apply(call, scope =>
        {
            var script = row.Item.Script;
            var nickname = args.Text("nickname") ??
                           (actions.Count > 0 ? FreeTriggerName(scope.Session.Missions.AnalysisOf(script), script) : null);
            var insert = new InsertTriggerOnLink(row, condition) { Nickname = nickname };
            MissionCommand[] commands =
            [
                insert,
                .. actions.Select((action, i) =>
                    new AddAction(new TriggerRef(script, nickname!), action) { Before = InsertedStartRow + i }),
            ];
            scope.RunMission(new MissionBatch(insert.HistoryLabel, commands),
                error => error.Index is > 0 and var i ? $"actions[{i - 1}]" : NameOr(error, "line"));
        });
    }

    // ---- The item tool ----

    /// <summary>The detail code of a mission ship without the <c>npc</c> field.</summary>
    public const string NeedsNpc = "NeedsNpc";

    /// <summary>The most steps of one order list of <c>mission_add_item</c>, as the most actions of a trigger.</summary>
    public const int MaxSteps = MaxActions;

    /// <summary>The kinds of <c>mission_add_item</c> (section 7.4 of the MCP1 design).</summary>
    public static readonly IReadOnlyList<string> ItemKinds =
    [
        "ship", "formation", "npc", "ship_build", "solar", "loot", "order_list", "objective", "conversation",
        "random_encounter",
    ];

    /// <summary>The keys of a ship that <see cref="AddMissionShip"/> writes. The tool sets each other key with
    /// <see cref="SetItemKey"/>.</summary>
    private static readonly HashSet<string> ShipCommandKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "npc", "system", "position", "orientation", "rel_pos", "label", "init_objectives",
    };

    public static ToolInput AddItemInput { get; } = WithLines(
        new TextParam("script", true, null, null),
        new TextParam("kind", true, ItemKinds, null),
        Nickname,
        new MissionFieldsParam("fields"),
        new LinesParam("steps", MaxSteps));

    /// <summary>
    /// The tool <c>mission_add_item</c>: a new item of a kind with the lines of its fields, as one request. A ship gets
    /// <see cref="AddMissionShip"/> with an existing <c>[NPC]</c>, then <see cref="SetItemKey"/> for each key that the
    /// command does not write. When a formation of the script names the new ship, a <see cref="MoveSection"/> puts the
    /// ship before it, so the formation takes the ship (rule M08). Each other kind gets <see cref="AddItem"/>. An order
    /// list also gets the orders of <c>steps</c>.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a field that does not fit its key,
    /// a ship without <c>fields.npc</c>, or steps of another kind. <see cref="EditErrorKind.InvalidValue"/> for a key that
    /// the section does not have or a value that is not valid. Each error of the edit call.</exception>
    public static ToolStep NewItem(ToolCall call)
    {
        var args = call.Args;
        var script = EditTools.At("script", () => ScriptRef.Parse(args.Text("script")!));
        var kind = args.Text("kind")!;
        var type = kind == "ship" ? "MsnShip" : MissionDraft.TypeOfKind(kind)!;
        var fields = Fields(args.Node("fields")?.AsObject(), type, kind);
        if (args.Has("steps") && kind != "order_list")
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument,
                AgentTexts.Error("mission_item_steps", ("kind", kind))) { Field = "steps" });
        }
        var orders = args.Node("steps")?.AsArray()
            .Select((line, i) => MissionLineInput.OrderLine(line!, $"steps[{i}]")).ToList() ?? [];
        var nickname = args.Text("nickname");
        if (kind == "ship")
            return EditTools.Apply(call, scope => AddShip(scope, script, nickname, fields));
        var add = new AddItem(script, kind)
        {
            Nickname = nickname,
            Lines = [.. fields.SelectMany(f => f.Lines), .. orders],
        };
        var label = $"Add {MissionNames.KindWord(type)} {nickname}".TrimEnd();
        return EditTools.Apply(call, scope =>
            scope.RunMission(new MissionBatch(label, [add]), error => NameOr(error, "script")));
    }

    /// <summary>
    /// The lines of the fields of a new item, by key. The <c>formation</c> and <c>ship</c> lines of a formation come
    /// last, so each ship key comes before them (rule M09), as <see cref="SetItemKey"/> puts it. The other lines keep
    /// the order of the fields.
    /// </summary>
    /// <exception cref="EditException">See <see cref="NewItem"/>. A <c>nickname</c> field, or an order in the fields of
    /// an order list, gives <see cref="EditErrorKind.InvalidArgument"/>.</exception>
    private static List<(string Key, string Field, IReadOnlyList<LineSpec> Lines)> Fields(JsonObject? fields, string type,
        string kind)
    {
        var section = MissionLineInput.SectionSpec(type)!;
        var result = new List<(string Key, string Field, IReadOnlyList<LineSpec> Lines)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields ?? [])
        {
            var field = $"fields.{key}";
            if (!seen.Add(key.Trim()))
            {
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument,
                    AgentTexts.Error("mission_field_twice", ("key", key))) { Field = field });
            }
            var spec = section.Key(key);
            if (spec != null && MissionIndex.Is(spec.Key, "nickname"))
            {
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument,
                    AgentTexts.Error("mission_item_nickname")) { Field = field });
            }
            if (spec == null && !section.OpenKeys)
            {
                var error = MissionSchema.Retail.Directive(key) != null && kind == "order_list"
                    ? new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument, AgentTexts.Error("mission_item_order"))
                    : MissionErrors.NotAKeyError(key, $"[{type}]") with
                    {
                        Message = AgentTexts.Error("mission_item_key", ("key", key), ("kind", MissionNames.KindWord(type)),
                            ("keys", string.Join(", ", section.Keys.Select(k => k.Key).Where(k => !MissionIndex.Is(k, "nickname"))))),
                    };
                throw new EditException(error with { Field = field });
            }
            var lines = MissionLineInput.FieldLines(type, key, value, field);
            result.Add((lines.Count > 0 ? lines[0].Key : spec?.Key ?? key, field, lines));
        }
        return [.. result.OrderBy(f => MissionIndex.Is(type, "MsnFormation") &&
                                     (MissionIndex.Is(f.Key, "formation") || MissionIndex.Is(f.Key, "ship")))];
    }

    /// <summary>
    /// Adds a mission ship in the scope: <see cref="AddMissionShip"/> with the keys that it writes, then one request with
    /// <see cref="SetItemKey"/> for each other key and the move before a formation that names the ship. The command
    /// takes a key in the form of values by name. A key in the form of texts by position goes through
    /// <see cref="SetItemKey"/> as it is. The second request needs the name of the ship, and the layer makes a name when
    /// the call gives none.
    /// </summary>
    /// <exception cref="EditException">The field <c>npc</c> is missing, the fields give both a position and
    /// <c>rel_pos</c>, or a request refuses.</exception>
    private static void AddShip(EditScope scope, ScriptRef script, string? nickname,
        IReadOnlyList<(string Key, string Field, IReadOnlyList<LineSpec> Lines)> fields)
    {
        IReadOnlyList<string> TextsOf(LineSpec line) =>
            MissionValues.Build(MissionSchema.Retail, "MsnShip", LinePlace.Section, line).Texts;

        var npc = fields.FirstOrDefault(f => MissionIndex.Is(f.Key, "npc")).Lines is [var line] && TextsOf(line) is [var name]
            ? name
            : throw new EditException(new EditError(EditErrorKind.InvalidArgument, NeedsNpc, AgentTexts.Error("mission_ship_npc"))
            {
                Field = "fields.npc",
            });
        // The texts of each line of each key that the command takes, by key.
        var taken = fields.Where(f => ShipCommandKeys.Contains(f.Key) && !MissionIndex.Is(f.Key, "npc") &&
                                      f.Lines.Count > 0 && f.Lines.All(l => l.Texts == null))
            .ToDictionary(f => f.Key, f => f.Lines.Select(TextsOf).ToList(), StringComparer.OrdinalIgnoreCase);
        if (fields.Count(f => f.Lines.Count > 0 && (MissionIndex.Is(f.Key, "position") || MissionIndex.Is(f.Key, "rel_pos"))) > 1)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument,
                AgentTexts.Error("mission_ship_place")) { Field = "fields.rel_pos" });
        }
        var add = new AddMissionShip(script)
        {
            Nickname = nickname,
            Npc = npc,
            System = taken.GetValueOrDefault("system") is [[var system]] ? system : null,
            Labels = taken.GetValueOrDefault("label")?.Select(l => l[0]).ToList() ?? [],
            Orders = taken.GetValueOrDefault("init_objectives") is [[var orders]] ? orders : null,
            Position = taken.GetValueOrDefault("position") is [var position] ? Numbers(position) : null,
            Rotation = taken.GetValueOrDefault("orientation") is [var rotation] ? Numbers(rotation) : null,
            Near = taken.GetValueOrDefault("rel_pos") is [var near]
                ? new NearSpec(near[1]) { Heading = Number(near[0]), Range = near.Count > 2 ? Number(near[2]) : null }
                : null,
        };
        var added = scope.RunMission(new MissionBatch(add.HistoryLabel, [add]), error => NameOr(error, "script"));
        var ship = ItemRef.Of(script.Path, "MsnShip", nickname ?? added.Adds.Select(ItemRefs.Parse)
            .First(r => r.Section is { } s && MissionIndex.Is(s.Type, "MsnShip")).Section!.Key!);
        var more = fields.Where(f => f.Lines.Count > 0 && !MissionIndex.Is(f.Key, "npc") && !taken.ContainsKey(f.Key)).ToList();
        var commands = more.Select(f => (MissionCommand)new SetItemKey(ship, f.Key, f.Lines)).ToList();
        // A formation that names the ship before the ship takes no ship (rule M08). The first one in the file wins.
        if (scope.Session.Missions.AnalysisOf(script.Path)?.Index.LaterMembers
                .Where(m => MissionIndex.Is(m.Ship, ship.Section.Key!)).Select(m => m.Formation).MinBy(f => f.Index) is { } formation)
            commands.Add(new MoveSection(ship) { Before = new ItemRef(script.Path, formation.Ref.Id) });
        if (commands.Count > 0)
        {
            scope.RunMission(new MissionBatch(add.HistoryLabel, commands),
                error => error.Index is { } i && i < more.Count ? more[i].Field : "script");
        }
    }

    /// <summary>The numbers of the texts of a position or a rotation in the form of values, which the schema
    /// checked.</summary>
    private static MissionValue Numbers(IReadOnlyList<string> texts) => MissionValue.Of([.. texts.Select(Number)]);

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The arguments of a trigger tool, with the definition of a line at the root of the schema.</summary>
    private static ToolInput WithLines(params ToolParam[] own) =>
        new(EditTools.Input(false, own).Parameters.ToArray()) { Definitions = MissionLineInput.Definitions };

    /// <summary>The actions of an argument, or none.</summary>
    private static IReadOnlyList<LineSpec> Lines(ToolArgs args, string name) =>
        args.Node(name)?.AsArray()
            .Select((line, i) => MissionLineInput.TriggerLine(line!, $"{name}[{i}]", action: true)).ToList() ?? [];

    /// <summary>The argument <c>nickname</c> for an error of the name of a new trigger, else the other argument.</summary>
    private static string NameOr(EditError error, string other) =>
        error.Detail is MissionErrors.NameInvalid or MissionErrors.NameTaken or MissionErrors.NameIdTaken ? "nickname" : other;

    /// <summary>The trigger of a reference: a <c>trg:</c> reference, or a <c>sec:</c> reference of a trigger section
    /// (section 5.6 of the MCP1 design).</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the text is not the reference of a
    /// trigger. The error names the argument.</exception>
    private static TriggerRef TriggerOf(string text, string field) => EditTools.At(field, () =>
        ItemRefs.Parse(text) is { Kind: ItemRefKind.Section, Path: { } path, Section: { Ordinal: 0 } section } &&
        MissionIndex.Is(section.Type, "Trigger") && section.Key is { } name
            ? new TriggerRef(path, name)
            : TriggerRef.Parse(text));

    /// <summary>The nickname of a trigger of a script: a nickname, or the reference of a trigger of the
    /// script.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the reference is not valid, or names a
    /// trigger of another script.</exception>
    private static string NameIn(string text, string script, string field)
    {
        if (!text.Contains(':'))
            return text.Trim();
        var trigger = TriggerOf(text, field);
        if (!VfsPath.Comparer.Equals(trigger.Script, script))
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, OtherItem,
                AgentTexts.Error("mission_other_script", ("ref", text), ("script", new ScriptRef(script)))) { Field = field });
        }
        return trigger.Name;
    }

    /// <summary>The row of a <c>line:</c> reference of a line of a trigger or a section, for the argument
    /// <c>before</c>.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the reference is not valid, or names a
    /// line of another section.</exception>
    private static int RowIn(string text, TriggerRef trigger, string field) => RowIn(text, trigger.Item, field);

    /// <inheritdoc cref="RowIn(string, TriggerRef, string)"/>
    private static int RowIn(string text, ItemRef item, string field)
    {
        var row = EditTools.At(field, () => RowRef.Parse(text));
        if (!SameItem(row.Item, item))
        {
            // The text names a trigger in its trg: form, as the results do.
            var name = MissionIndex.Is(item.Section.Type, "Trigger") && item.Section is { Key: { } key, Ordinal: 0 }
                ? new TriggerRef(item.Script, key).ToString()
                : item.ToString();
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, OtherItem,
                AgentTexts.Error("mission_other_line", ("ref", text), ("item", name))) { Field = field });
        }
        return row.Row;
    }

    /// <summary>True when two items are one item. A trigger is one item by its nickname, without letter case.</summary>
    private static bool SameItem(ItemRef a, ItemRef b) =>
        VfsPath.Comparer.Equals(a.Script, b.Script) && MissionIndex.Is(a.Section.Type, b.Section.Type) &&
        string.Equals(a.Section.Key, b.Section.Key, StringComparison.OrdinalIgnoreCase) &&
        a.Section.Ordinal == b.Section.Ordinal;

    /// <summary>A free made name for a new trigger of a script or of the template of a prefab, by the rule of the layer
    /// (decision D683 of M6b). The graph takes the names of the triggers of its analysis in the same way.</summary>
    /// <param name="analysis">The analysis of the script now (see <see cref="MissionEditor.AnalysisOf"/>), or null when
    /// it has none. The command then gives the error.</param>
    private static string FreeTriggerName(MissionAnalysis? analysis, string script) =>
        MissionNaming.Free(script, "Trigger",
            (analysis?.Index.Triggers ?? []).Select(t => t.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The command of a new link from a trigger, as the link gesture of the graph makes it (see
    /// <see cref="PrefabLinks.LinkCommand"/>). A member without an output for the link, and a trigger that the analysis
    /// does not have, get <see cref="LinkTriggers"/>. The command then refuses with the reason.</summary>
    private static MissionCommand StartLink(MissionAnalysis? analysis, TriggerRef source, string target, LinkKind kind) =>
        (analysis?.Index.Trigger(source.Name) is { } trigger ? PrefabLinks.LinkCommand(analysis, trigger, target, kind) : null) ??
        new LinkTriggers(source, target, kind);

    /// <summary>The command that removes a link from a trigger, as the gesture "Remove link" of the graph makes it (see
    /// <see cref="PrefabLinks.UnlinkCommand"/>).</summary>
    private static MissionCommand StopLink(MissionAnalysis? analysis, TriggerRef source, string target, LinkKind kind) =>
        analysis?.Index.Trigger(source.Name) is { } trigger
            ? PrefabLinks.UnlinkCommand(analysis, trigger, target, kind)
            : new UnlinkTriggers(source, target, kind);
}
