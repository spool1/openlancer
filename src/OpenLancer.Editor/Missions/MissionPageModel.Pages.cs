using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Ui;
using static OpenLancer.Editor.Missions.MissionCommandRefs;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// What a mission page needs beyond the analysis: the texts and the names of the game data. The Inspector gives them
/// from the session on the UI thread. A test gives fixed functions.
/// </summary>
/// <param name="ShowBaseGame">True while the base-game messages show.</param>
/// <param name="HidesOpenLancer">True when the active mod has a targets list without OpenLancer (UI rule 4).</param>
internal sealed record PageContext(MissionRunResult Result, MissionAnalysis Analysis, bool ShowBaseGame,
    bool HidesOpenLancer)
{
    /// <summary>Gives the text of a text id, or an empty text.</summary>
    public Func<int, string> Text { get; init; } = _ => "";

    /// <summary>Gives the voices file of a comm line, or null when no voices file has it.</summary>
    public Func<string, string?> CommFile { get; init; } = _ => null;

    /// <summary>Gives the name of a system, or null. Null when the page knows no system names.</summary>
    public Func<string, string?>? SystemName { get; init; }

    /// <summary>Gives the name text of an item of the game data of a kind, for example a faction, or null.</summary>
    public Func<MissionValueKind, string, string?> NameOf { get; init; } = (_, _) => null;

    /// <summary>The acts of the graph file, or null for the acts of the analysis (decision D679).</summary>
    public MissionActs? Acts { get; init; }

    /// <summary>
    /// Gives why a section cannot change, or null when the user can edit it (section 8.1 of the M6b design). The Inspector
    /// gives the reasons of the session. Without a function, each section is read-only, as in a view without an edit
    /// session.
    /// </summary>
    public Func<SectionRef, string?> ReadOnlyOf { get; init; } = _ => MissionPageModel.FieldNote;

    /// <summary>True when the user can edit a section.</summary>
    public bool Editable(SectionRef section) => ReadOnlyOf(section) == null;

    /// <summary>Gives the lines of a section that the active mod sets, by their index in the section (section 8.1 of the
    /// M6b design). Without a function, no line has the blue bar.</summary>
    public Func<SectionRef, IReadOnlySet<int>> ChangedOf { get; init; } = _ => new HashSet<int>();

    /// <summary>Gives the lines of the new base of a section whose base changed after the edit, or null (section 6.3 of
    /// the M6b design). Without a function, no page shows the banner.</summary>
    public Func<SectionRef, IReadOnlyList<string>?> BaseOf { get; init; } = _ => null;

    /// <summary>True for a shared ship build file of the missions. Random encounters can use its builds too.</summary>
    public Func<string, bool> SharedBuildFile { get; init; } = _ => false;

    /// <summary>Gives the files that the <c>npc_ship_file</c> line of a script can name (#18). Without a function, the
    /// field "Ship build file" lists no file.</summary>
    public Func<IReadOnlyList<ShipBuildFileChoice>> ShipBuildFiles { get; init; } = () => [];

    /// <summary>Gives why a script cannot get a new ship build file at an install path, or null (#18). Without a
    /// function, each path refuses, as in a view without an edit session.</summary>
    public Func<string, string, string?> NewShipFileRefusal { get; init; } = (_, _) => MissionPageModel.FieldNote;

    /// <summary>Gives the default name of a new ship build file of a script. Without a function, the name is
    /// <see cref="MissionEditor.DefaultShipFileName"/>.</summary>
    public Func<string, string> FreeShipFileName { get; init; } = _ => MissionEditor.DefaultShipFileName;

    /// <summary>Gives the ship builds of a script that the game does not find when the <c>npc_ship_file</c> line names
    /// another file, or a new empty file for null (see <see cref="MissionEditor.LostBuilds"/>). Without a function, no
    /// build is lost.</summary>
    public Func<string, string?, IReadOnlyList<string>> LostBuilds { get; init; } = (_, _) => [];

    /// <summary>Gives true when W has a file at the path that a value of an <c>npc_ship_file</c> line names. Without a
    /// function, no value names a file.</summary>
    public Func<string, bool> NamesFile { get; init; } = _ => false;

    /// <summary>Gives why the user cannot change the prefab instances of a script, or null. Without a function, no
    /// instance can change, as in a view without an edit session.</summary>
    public Func<string, string?> ScriptReadOnly { get; init; } = _ => MissionPageModel.FieldNote;

    /// <summary>Gives the overrides of a linked instance that the last save does not have: the fields, and the roles of
    /// the removed members (section 5.6 of the M6b design). Without a function, each override is saved.</summary>
    public Func<InstanceRef, (IReadOnlySet<string> Fields, IReadOnlySet<string> Members)> UnsavedOverrides { get; init; } =
        _ => (new HashSet<string>(), new HashSet<string>());

    /// <summary>Gives a free file name of a new prefab asset for a wanted name. Without a function, the name
    /// stays.</summary>
    public Func<string, string> FreePrefabId { get; init; } = id => id;

    /// <summary>The prefab of the template that the analysis holds in prefab mode, or null for a mission script (section
    /// 8.6 of the M6b design).</summary>
    public PrefabModeModel? Prefab { get; init; }

    /// <summary>Why the session takes no edits, or null. A built-in prefab and a prefab of a lower mod cannot change, but
    /// the session can still make a variant of them in the active mod. Without a value, the session takes no edits, as
    /// in a view without an edit session.</summary>
    public string? SessionReadOnly { get; init; } = MissionPageModel.FieldNote;

    /// <summary>The trace of the dry run of the mission, or null while no run exists (section 4.5 of the M6c design).
    /// The Trigger page then shows the section "Run".</summary>
    public MissionTrace? Run { get; init; }

    /// <summary>The time now in the live view, in seconds since the game loaded the mission, or 0 for the dry
    /// run.</summary>
    public double RunNow { get; init; }

    /// <summary>True when the active mod sets a line.</summary>
    public bool Changed(MissionLine line) => ChangedOf(line.Section.Ref).Contains(line.Entry);

    /// <summary>True when the active mod sets a line of a key of a section.</summary>
    public bool Changed(MissionSection section, string key) =>
        Analysis.Index.LinesOf(section).Any(l => MissionIndex.Is(l.Key, key) && Changed(l));
}

/// <summary>The pages of the items of a mission (sections 10.3 to 10.17 of the M6a design).</summary>
internal sealed partial record MissionPageModel
{
    /// <summary>The page of a selection of the Mission tab. It is the Mission page, or the page of a trigger, a line, a
    /// cast item, an order list, an objective or a conversation. It can also be the page of a label, an act or a
    /// behavior prefab, or the plain page of another section.</summary>
    public static MissionPageModel For(PageContext context, MissionRef item)
    {
        var analysis = context.Analysis;
        var index = analysis.Index;
        var section = item.Section is { } target ? analysis.Document.Find(target) : null;
        var trigger = section?.Kind == MissionSectionKind.Trigger
            ? index.Triggers.FirstOrDefault(t => t.Section == section)
            : null;
        switch (item.Kind)
        {
            case MissionItemKind.Mission when context.Prefab is { } prefab:
                return PrefabPage(context, prefab, item.Key);
            case MissionItemKind.Mission:
                return Mission(context);
            case MissionItemKind.Trigger when trigger != null:
                return Trigger(context, trigger, -1);
            case MissionItemKind.Row when trigger != null:
                var line = index.LinesOf(section!).FirstOrDefault(l => l.Entry == item.Row);
                if (line != null && MissionIndex.Is(line.Key, "Act_CallThorn"))
                    return CutsceneLink(context, trigger, line);
                if (line != null && (MissionIndex.Is(line.Key, "Act_AddRTC") || MissionIndex.Is(line.Key, "Act_RemoveRTC")))
                    return BaseSceneLink(context, line);
                return Trigger(context, trigger, item.Row);
            case MissionItemKind.Ship when section != null:
                return Ship(context, section);
            case MissionItemKind.Formation when section != null:
                return Formation(context, section);
            case MissionItemKind.Solar when section != null:
                return Solar(context, section);
            case MissionItemKind.Loot when section != null:
                return Loot(context, section);
            case MissionItemKind.Npc when section != null:
                return Npc(context, section);
            case MissionItemKind.OrderList when section != null:
                return OrderList(context, section);
            case MissionItemKind.Objective when section != null:
                return Objective(context, section);
            case MissionItemKind.Conversation when section != null:
                return Conversation(context, section);
            case MissionItemKind.RandomEncounter when section != null:
                return RandomEncounter(context, section);
            case MissionItemKind.Label when item.Key != null:
                return Label(context, item.Key);
            case MissionItemKind.Act when item.Key != null:
                return Act(context, item.Key);
            case MissionItemKind.PrefabInstance when analysis.Prefabs?.Instances.FirstOrDefault(i => i.Key == item.Key) is
                { } instance:
                return PrefabInstance(context, instance);
            case MissionItemKind.PrefabKind when KindOfKey(analysis, item.Key) is { } definition:
                return PrefabKind(context, definition, item.Key!);
            default:
                return Item(analysis, item, context.ShowBaseGame);
        }
    }

    /// <summary>The info icon of the section "Run" of the Trigger page.</summary>
    internal static readonly HelpText RunHelp = new("Run",
        "The state of this trigger in the dry run of the Mission tab. It shows if the trigger waits, how many times it " +
        "ran, and its last change with its cause.", "Nothing.");

    /// <summary>The section "Run" of the Trigger page while a run exists (section 4.5 of the M6c design): the state,
    /// the step of the last start, the runs, and the last change with its cause. A trigger of a prefab instance shows
    /// the name of the prefab first, as in the Run log.</summary>
    /// <param name="now">The time now in the live view, or 0 for the dry run.</param>
    internal static PageSection RunSection(MissionTrace trace, MissionTrigger trigger, MissionAnalysis analysis, double now = 0)
    {
        string Name(string nickname) => RunOverlay.TriggerName(analysis, nickname);
        var state = trace.StateOf(trigger.Nickname);
        var chips = state == null ? [] : RunOverlay.ChipsOf(state, trace, now);
        var fields = new List<PageField>
        {
            new("State", chips.Count == 0 ? "Not active" : string.Join(", ", chips.Select(c => c.Text)),
                "The state of the trigger after the last step of the run.")
            {
                Chips = [.. chips.Select(c => new PageChip(c.Text, c.Tooltip, null))],
            },
        };
        if (state != null)
        {
            if (state.ActiveSinceTime is { } time)
                fields.Add(new PageField("Became active at", TraceStep.Clock(time), "The time of the last start of the trigger."));
            else if (state.ActiveSinceStep > 0)
                fields.Add(new PageField("Became active at", $"Step {Number(state.ActiveSinceStep)}",
                    "The step of the last start of the trigger."));
            fields.Add(new PageField("Ran", state.Runs == 1 ? "1 time" : $"{Number(state.Runs)} times",
                "How many times the trigger ran in the run."));
            if (state.LastChange?.TextWith(Name) is { } last)
                fields.Add(new PageField("Last change", last, "The last start, run or stop of the trigger."));
            if (state.LastStep > 0 && state.LastStep <= trace.Steps.Count)
                fields.Add(new PageField("Cause", trace.Steps[state.LastStep - 1].TitleWith(Name),
                    "The step of the last change."));
        }
        return new PageSection("run", "Run", EditorIcons.Flask, RunHelp, fields, []);
    }

    // ---- The trigger page (section 10.3).

    /// <summary>The Trigger page: the trigger component, "Starts when", one section for each action group, the sources,
    /// the OpenLancer marks, the problems and the Advanced section.</summary>
    /// <param name="row">The line that the selection names, or -1. The page opens its section.</param>
    private static MissionPageModel Trigger(PageContext context, MissionTrigger trigger, int row)
    {
        var analysis = context.Analysis;
        var index = analysis.Index;
        var schema = MissionSchema.Retail;
        var keys = schema.Section("Trigger");
        var script = analysis.Script.Path;
        var sections = new List<PageSection>();
        var tref = TriggerOf(trigger);
        var editable = context.Editable(trigger.Section.Ref);
        PageField Key(string key, string value) => (keys?.Key(key) is { } spec
            ? new PageField(spec.Label, value, spec.Help) { Hard = spec.Hard }
            : new PageField(key, value)) with { Changed = context.Changed(trigger.Section, key) };

        sections.Add(new PageSection("trigger", "Trigger", EditorIcons.Bolt, schema.Help["triggerComponent"],
        [
            Key("InitState", trigger.StartsActive ? "Yes" : "No") with
            {
                Edit = BoolEdit(context, trigger.Section, keys?.Key("InitState")?.Label ?? "Starts with the mission",
                    trigger.StartsActive, on => new SetTriggerStart(tref, on)),
            },
            Key("repeatable", trigger.Repeats ? "Yes" : "No") with
            {
                Edit = BoolEdit(context, trigger.Section, keys?.Key("repeatable")?.Label ?? "Repeats", trigger.Repeats,
                    on => new SetTriggerRepeats(tref, on)),
            },
        ], []));

        // Starts when: the event sentence, then the named fields of the condition. A condition before the last one is
        // struck through (TR1).
        var startsWhen = new List<PageField>();
        var template = context.Prefab?.Template;
        if (trigger.Condition is { } condition)
        {
            startsWhen.Add(new PageField("Event", condition.Sentence(), EventTip)
            {
                Edit = EventEdit(context, trigger), Changed = context.Changed(condition),
                Actions = template != null && editable
                    ? LineChoices(context.Prefab!.Asset.Ref, template, condition, PrefabFieldKind.Condition).ToList()
                    : [],
            });
            startsWhen.AddRange(ValueFields(context, condition, (spec, value) => new SetConditionValue(tref, spec.Name, value)));
        }
        else if (template != null && SlotLines(template, index, trigger, PrefabFieldKind.Condition).FirstOrDefault() is { } slot)
        {
            // In prefab mode, the condition can be the line of a condition field (section 8.6 of the M6b design).
            startsWhen.Add(SlotLineField(template, slot, "Event", editable));
        }
        else
        {
            startsWhen.Add(new PageField("Event", "No condition", TriggerRules.NoConditionMessage(trigger.Nickname),
                Severity: DiagnosticSeverity.Error) { Edit = EventEdit(context, trigger) });
        }

        foreach (var ignored in trigger.Conditions.Take(Math.Max(0, trigger.Conditions.Count - 1)))
        {
            startsWhen.Add(new PageField($"Line {LineNumber(ignored)}", LineText(ignored), schema.Notes["ignoredCondition"])
            {
                Struck = true, Changed = context.Changed(ignored),
            });
        }

        sections.Add(new PageSection("startsWhen", "Starts when", MissionGlyphs.OfEvent(trigger.Condition?.Name?.Group),
            schema.Help["startsWhen"], startsWhen, []));
        if (context.Run is { } trace)
            sections.Add(RunSection(trace, trigger, context.Analysis, context.RunNow));

        // One section for each action group, in group order. Each line shows its line number, so the order stays
        // visible.
        string? focus = row >= 0 && trigger.Condition?.Entry == row ? "startsWhen" : null;
        foreach (var group in trigger.Actions.GroupBy(a => a.Name?.Group ?? "").OrderBy(g => schema.Group(g.Key)?.Order ?? int.MaxValue))
        {
            var info = schema.Group(group.Key);
            var id = $"group.{(group.Key.Length > 0 ? group.Key : "other")}";
            var fields = new List<PageField>();
            foreach (var action in group)
            {
                fields.Add(new PageField($"Line {LineNumber(action)}", MissionGraph.ActionText(action), action.Name?.Plain,
                    new MissionRef(script, MissionItemKind.Row, trigger.Section.Ref, Row: action.Entry))
                {
                    Heading = true, Actions = ActionMenu(context, trigger, action), Changed = context.Changed(action),
                });
                var actionRow = RowOf(action);
                fields.AddRange(ValueFields(context, action,
                    (spec, value) => new SetActionValue(actionRow, spec.Name, value)));
                if (action.Entry == row)
                    focus = id;
            }

            sections.Add(new PageSection(id, info?.Section ?? "Other lines", MissionGlyphs.OfGroup(group.Key), info?.Help, fields,
                [])
            {
                AddGroup = editable && info != null ? group.Key : null,
            });
        }

        // In prefab mode, the lines of a lines field show with their field (section 8.6 of the M6b design).
        if (template != null && SlotLines(template, index, trigger, PrefabFieldKind.Lines).ToList() is { Count: > 0 } slotLines)
        {
            sections.Add(new PageSection("fieldLines", "Lines of fields", EditorIcons.List, null,
                slotLines.Select(l => SlotLineField(template, l, $"Line {LineNumber(l)}", editable)).ToList(), []));
        }

        // Started by, Stopped by, Watched by and Loaded by.
        foreach (var (id, title, kind) in new[]
                 {
                     ("startedBy", "Started by", MissionLinkKind.Start), ("stoppedBy", "Stopped by", MissionLinkKind.Stop),
                     ("watchedBy", "Watched by", MissionLinkKind.Watch), ("loadedBy", "Loaded by", MissionLinkKind.Load),
                 })
        {
            var links = index.LinksTo(trigger).Where(l => l.Kind == kind && l.Source != trigger).ToList();
            // "Started by" and "Stopped by" are lists of triggers with Add and Remove (section 8.4 of the M6b design).
            // "Watched by" and "Loaded by" stay read-only with "Go to".
            LinkKind? linkKind = kind switch
            {
                MissionLinkKind.Start => LinkKind.Start,
                MissionLinkKind.Stop => LinkKind.Stop,
                _ => null,
            };
            var edits = editable && linkKind != null;
            if (links.Count == 0 && !edits)
                continue;
            var fields = links.Select(l => RowField(script, l.Line) with
            {
                Actions = edits
                    ? [new PageAction("Remove", One($"Unlink {l.Source.Nickname} from {trigger.Nickname}",
                        new UnlinkTriggers(TriggerOf(l.Source), trigger.Nickname, linkKind!.Value)))]
                    : [],
                Changed = context.Changed(l.Line),
            }).ToList();
            if (edits)
                fields.Add(AddLinkField(context, trigger, linkKind!.Value, "Add"));
            sections.Add(new PageSection(id, title, EditorIcons.Link, null, fields, []));
        }

        var marked = trigger.Rows.Where(r => r.Mark != EngineMark.Runs).ToList();
        if (!context.HidesOpenLancer && marked.Count > 0)
        {
            sections.Add(new PageSection("openlancer", "OpenLancer", EditorIcons.Rocket, schema.Help["openLancer"],
                marked.Select(r => new PageField($"Line {LineNumber(r)}", $"{LineText(r)}: {OpenLancerGap.MarkText(r.Mark)}",
                    OpenLancerGap.MarkTooltip(r.Mark))).ToList(), []));
        }

        sections.Add(Problems(analysis, SectionProblems(analysis, trigger.Section), context.ShowBaseGame));

        var advanced = new List<PageField>();
        if (trigger.SystemHint != null || editable)
        {
            var hint = trigger.SystemHint;
            var systemLabel = keys?.Key("system")?.Label ?? "System";
            advanced.Add(new PageField(systemLabel, hint != null ? SystemText(context, hint) : "None", keys?.Key("system")?.Help)
            {
                Changed = context.Changed(trigger.Section, "system"),
                Edit = new FieldEdit(EditWidget.Picker, $"Set {systemLabel}")
                {
                    Value = hint ?? "",
                    List = MissionSchema.ListOf(MissionValueKind.System),
                    NoneWord = "",
                    Make = value => One($"Set {systemLabel}", new SetTriggerSystem(tref)
                    {
                        System = value.Scalar is { Length: > 0 } system ? system : null,
                    }),
                    ReadOnly = context.ReadOnlyOf(trigger.Section.Ref),
                },
            });
        }

        foreach (var entry in trigger.LinesBeforeNickname)
        {
            var line = index.LinesOf(trigger.Section)[entry];
            advanced.Add(new PageField($"Line {LineNumber(line)}", LineText(line), schema.Notes["beforeNickname"])
            {
                Struck = true, Changed = context.Changed(line),
                Actions = editable
                    ?
                    [
                        new PageAction("Move it after the nickname", One("Move line",
                            new MoveLine(RowOf(line)) { Before = trigger.NicknameLine + 1 })),
                        new PageAction("Remove the line", One("Remove line", new RemoveLine(RowOf(line)))),
                    ]
                    : [],
            });
        }

        advanced.Add(new PageField("Lines in file order", trigger.Rows.Count.ToString(CultureInfo.InvariantCulture)) { Heading = true });
        advanced.AddRange(trigger.Rows.Select((r, i) => new PageField($"Line {LineNumber(r)}", LineText(r))
        {
            Actions = editable ? FileOrderMenu(trigger, i) : [], Changed = context.Changed(r),
        }));
        sections.Add(new PageSection("advanced", "Advanced", EditorIcons.Cog, null, advanced, [], Advanced: true));

        var lead = analysis.Prefabs?.Of(trigger) is { } instance
            ? new[]
            {
                new PageField("Part of", $"{instance.Definition.Name} {instance.Name}", instance.Definition.Help.FirstSentence,
                    new MissionRef(script, MissionItemKind.PrefabInstance, Key: instance.Key)),
            }
            : [];
        return new MissionPageModel(trigger.Nickname, "Trigger", EditorIcons.Bolt, schema.Help["trigger"], null, sections,
            trigger.Section.Ref)
        {
            HeaderChips = TriggerChips(trigger),
            Lead = lead,
            FocusSection = focus,
            PageTrigger = trigger,
            ReadOnly = ReadOnlyText(context, trigger.Section),
            Base = BaseOf(context, trigger.Section),
        };
    }

    /// <summary>The banner of a section whose base changed after the edit, or null (section 6.3 of the M6b design).
    /// "Keep my lines" runs <see cref="AcceptBase"/>, and "Use the new base" runs <see cref="RevertSection"/>.</summary>
    private static BaseChange? BaseOf(PageContext context, MissionSection section)
    {
        if (!context.Editable(section.Ref) || context.BaseOf(section.Ref) is not { } lines)
            return null;
        var item = ItemOf(section);
        var name = section.Nickname ?? section.Ref.Id.ToString();
        return new BaseChange(lines, section.Section.Select(MissionEditor.LineText).ToList(),
            One($"Keep my lines of {name}", new AcceptBase(item)),
            One($"Use the new base of {name}", new RevertSection(item)));
    }

    /// <summary>The tooltip of the field "Event" (section 8.11 of the M6b design).</summary>
    internal const string EventTip = "The event that the trigger waits for. Pick another event to change the condition.";

    /// <summary>The read-only reason of a section that the page shows under its header, or null. The default reason of
    /// a context without an edit session does not show.</summary>
    private static string? ReadOnlyText(PageContext context, MissionSection section) =>
        context.ReadOnlyOf(section.Ref) is { } reason && reason != FieldNote ? reason : null;

    /// <summary>The row menu of a line of "Lines in file order": "Move up" and "Move down" with
    /// <see cref="MoveLine"/>. The first nickname line does not move, and no line moves across it.</summary>
    /// <param name="at">The index of the line in the rows after the nickname.</param>
    private static IReadOnlyList<PageAction> FileOrderMenu(MissionTrigger trigger, int at)
    {
        var rows = trigger.Rows;
        var row = RowOf(rows[at]);
        var result = new List<PageAction>();
        if (at > 0)
            result.Add(new PageAction("Move up", One("Move line", new MoveLine(row) { Before = rows[at - 1].Entry })));
        if (at + 1 < rows.Count)
            result.Add(new PageAction("Move down", One("Move line",
                new MoveLine(row) { Before = at + 2 < rows.Count ? rows[at + 2].Entry : null })));
        return result;
    }

    /// <summary>The chips of a trigger: "Start", "Repeats" and its system line (section 6.2 of the M6a design). The node
    /// of the graph and the Trigger page show the same chips.</summary>
    internal static IReadOnlyList<string> TriggerChips(MissionTrigger trigger)
    {
        var chips = new List<string>();
        if (trigger.StartsActive)
            chips.Add("Start");
        if (trigger.Repeats)
            chips.Add("Repeats");
        if (trigger.SystemHint is { } system)
            chips.Add(system);
        return chips;
    }

    /// <summary>The tooltip of a chip of a trigger (section 13.1 of the M6a design).</summary>
    internal static string TriggerChipTip(string chip) => chip switch
    {
        "Start" => "This trigger starts with the mission (InitState = ACTIVE).",
        "Repeats" => "This trigger becomes active again each time it runs (repeatable = true).",
        _ => "The system line of this trigger. The game does not use it. The editor uses it to place the trigger.",
    };

    /// <summary>The line number of a line in its section, from 1.</summary>
    private static string LineNumber(MissionLine line) => (line.Entry + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The plain name and the values of a line, as the graph shows it. A key of a section has the label of the
    /// schema, for example "Repeats".</summary>
    private static string LineText(MissionLine line) =>
        line.Kind() == MissionNameKind.Action ? MissionGraph.ActionText(line) : MissionGraph.LineText(line);

    /// <summary>A field of a line of another trigger, with a link that selects the line.</summary>
    private static PageField RowField(string script, MissionLine line) =>
        new(line.Trigger?.Nickname ?? line.Section.Nickname ?? line.Section.Ref.Id.ToString(),
            $"Line {LineNumber(line)}: {LineText(line)}", null,
            line.Trigger != null
                ? new MissionRef(script, MissionItemKind.Row, line.Section.Ref, Row: line.Entry)
                : MissionRef.OfSection(line.Section.Ref, line.Entry));

    /// <summary>
    /// The named fields of the values of a line, from the schema. A name shows its name text and its nickname, or the
    /// "Missing" look when it does not resolve. A position shows one field for each number. A value list in one line
    /// shows as a list field.
    /// </summary>
    /// <param name="make">Makes the command of a new value of a spec, or null for read-only fields.</param>
    private static IEnumerable<PageField> ValueFields(PageContext context, MissionLine line,
        Func<MissionValueSpec, MissionValue, MissionCommand>? make = null)
    {
        if (line.Name is not { } name)
            yield break;
        var editable = make != null && context.Editable(line.Section.Ref);
        foreach (var spec in name.Values)
        {
            // An edit shows each value that the line needs, also an empty one, so the user can set it.
            if (line.ValueOf(spec) is null && !(editable && !spec.Optional))
                continue;
            if (spec.List != null && MissionSchema.Retail.ValueList(name.Name, spec.Name) is { } listSpec)
            {
                var model = ListFieldModel.Read(listSpec, [ListFieldLine.Of(line.Line)]);
                yield return new PageField(listSpec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), spec.Help)
                {
                    List = model, Hard = spec.Hard, Changed = context.Changed(line),
                    Edit = make != null ? ValueListEdit(context, line, spec, listSpec.Label, v => make(spec, v)) : null,
                };
                continue;
            }

            yield return ValueField(context, line, spec, spec.Label, make == null ? null : v => make(spec, v));
        }
    }

    /// <summary>
    /// The field of one value spec of a line (section 10.1 of the M6a design). A name of the script or of the game data
    /// gives a reference, with the "Missing" look when it does not resolve. A position or a rotation gives one part for
    /// each number. A text id shows its text. Another value shows as text.
    /// </summary>
    /// <param name="make">Makes the command of a new value, or null for a read-only field.</param>
    /// <remarks>In prefab mode, a value of a line of a template also has the value choice "Fixed value" or "Field:
    /// {label}" (section 8.6 of the M6b design).</remarks>
    private static PageField ValueField(PageContext context, MissionLine line, MissionValueSpec spec, string label,
        Func<MissionValue, MissionCommand>? make = null)
    {
        var field = ShownValue(context, line, spec, label) with { Changed = context.Changed(line) };
        field = make == null ? field : field with { Edit = ValueEdit(context, line, spec, label, make) };
        return make != null && context.Prefab?.Template is { } template
            ? ValueChoice(context, template, line, spec, label, field)
            : field;
    }

    /// <summary>The field of one value spec of a line as it shows.</summary>
    private static PageField ShownValue(PageContext context, MissionLine line, MissionValueSpec spec, string label)
    {
        var values = Enumerable.Range(spec.Index, Math.Max(0, Math.Min(spec.End, line.Count) - spec.Index))
            .Select(line.Value).ToList();
        if (spec.Kind is MissionValueKind.Position or MissionValueKind.Rotation && values.Count == spec.Width &&
            values.All(v => v != null))
        {
            string[] axes = spec.Kind == MissionValueKind.Position ? ["X", "Y", "Z"] : ["W", "X", "Y", "Z"];
            return new PageField(label, string.Join(", ", values), spec.Help)
            {
                Hard = spec.Hard,
                Parts = axes.Select((axis, i) => (axis, values[i]!)).ToList(),
            };
        }

        if (values.Count == 1 && values[0] is { } value && line.References.FirstOrDefault(r => r.ValueIndex == spec.Index) is
                { State: MissionReferenceState.Found or MissionReferenceState.Missing } reference)
            return ReferenceField(context, label, spec, value, reference);

        var parts = new List<string>();
        foreach (var text in values.OfType<string>())
        {
            parts.Add(spec.Kind == MissionValueKind.TextId && int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var id) && context.Text(id) is { Length: > 0 } named
                ? $"{named} ({text})"
                : text);
        }

        return new PageField(label, string.Join(", ", parts), spec.Help) { Hard = spec.Hard };
    }

    /// <summary>The field of a name: a reference with the name text and the nickname. It links to an item of the script
    /// or to a system. A name that does not resolve has the "Missing" look.</summary>
    private static PageField ReferenceField(PageContext context, string label, MissionValueSpec spec, string value,
        MissionReference reference)
    {
        var missing = reference.State == MissionReferenceState.Missing;
        object? link = reference.Section != null ? MissionRef.OfSection(reference.Section.Ref, -1)
            : spec.Kind == MissionValueKind.System && !missing ? new SystemLink(value)
            : null;
        var name = spec.Kind == MissionValueKind.System ? context.SystemName?.Invoke(value)
            : reference.Section == null ? context.NameOf(spec.Kind, value)
            : null;
        return new PageField(label, missing ? $"Missing: {value}" : name is { Length: > 0 } ? $"{name} ({value})" : value,
            spec.Help, link)
        {
            Hard = spec.Hard,
            Missing = missing,
            Reference = new PageReference(value, name is { Length: > 0 } ? name : null, MissionGlyphs.OfValue(spec.Kind),
                ValueKindText(spec.Kind)),
        };
    }

    /// <summary>The kind of a name in plain words, for the tooltip of a reference.</summary>
    private static string ValueKindText(MissionValueKind kind) => kind switch
    {
        MissionValueKind.Trigger => "Trigger",
        MissionValueKind.Ship or MissionValueKind.Group or MissionValueKind.Object => "Mission item",
        MissionValueKind.Formation => "Formation",
        MissionValueKind.Solar => "Solar",
        MissionValueKind.Loot => "Loot",
        MissionValueKind.ObjList => "Order list",
        MissionValueKind.Dialog => "Conversation",
        MissionValueKind.Objective => "Objective",
        MissionValueKind.Npc => "NPC",
        MissionValueKind.RandomEncounter => "Random encounter",
        MissionValueKind.System => "System",
        MissionValueKind.Base => "Base",
        MissionValueKind.SystemObject => "Object",
        MissionValueKind.Faction => "Faction",
        MissionValueKind.ShipArch => "Ship archetype",
        MissionValueKind.Loadout => "Loadout",
        MissionValueKind.Pilot => "Pilot",
        MissionValueKind.Voice => "Voice",
        MissionValueKind.Good => "Good",
        MissionValueKind.Equipment => "Equipment",
        MissionValueKind.NpcShipArch => "Ship build",
        MissionValueKind.FormationShape => "Formation shape",
        MissionValueKind.CommLine => "Comm line",
        MissionValueKind.SpaceCutscene => "Space cutscene",
        MissionValueKind.BaseScene => "Base scene",
        _ => kind.ToString(),
    };

    /// <summary>The name of a system and its nickname, or the nickname.</summary>
    private static string SystemText(PageContext context, string nickname) =>
        context.SystemName?.Invoke(nickname) is { Length: > 0 } name ? $"{name} ({nickname})" : nickname;

    /// <summary>The problems of the checks for a section.</summary>
    private static List<Diagnostic> SectionProblems(MissionAnalysis analysis, params MissionSection[] sections) =>
        analysis.Problems.Where(p => p.Target != null && sections.Any(s => s.Ref.Equals(p.Target))).ToList();

    // ---- The pages of the cast (sections 10.6 to 10.9 and 10.13).

    /// <summary>
    /// The fields of keys of a section, in the order of the keys, with the labels and the help texts of the schema. A
    /// key with a list field gives a list field. A key with more than one value gives a heading and one named field for
    /// each value (section 10.6 of the M6a design). A key that the section does not have shows only while the user can
    /// edit the section: as "Not set" with the widget that adds the key (section 8.4 of the M6b design). Else it does not
    /// show (UI rule 4).
    /// </summary>
    private static List<PageField> KeyFields(PageContext context, MissionSection section, params string[] keys)
    {
        var spec = MissionSchema.Retail.Section(section.Type);
        var lines = context.Analysis.Index.LinesOf(section);
        var editable = context.Editable(section.Ref);
        var fields = new List<PageField>();
        foreach (var key in keys)
        {
            var keyLines = lines.Where(l => MissionIndex.Is(l.Key, key)).ToList();
            if (keyLines.Count == 0)
            {
                if (editable && NewKeyField(context, section, key) is { } added)
                    fields.Add(added);
                continue;
            }
            var keySpec = spec?.Key(key);
            if (MissionSchema.Retail.KeyList(section.Type, key) is { } listSpec)
            {
                var model = ListFieldModel.Read(listSpec, keyLines.Select(l => ListFieldLine.Of(l.Line)).ToList());
                fields.Add(new PageField(listSpec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), keySpec?.Help)
                {
                    List = model, Hard = keySpec?.Hard == true, Edit = KeyListEdit(context, section, key, listSpec.Label),
                    Changed = keyLines.Any(context.Changed),
                });
                continue;
            }

            var line = keyLines[0];
            var label = keySpec?.Label ?? key;
            var specs = line.Specs.Count > 0
                ? line.Specs
                : [new MissionValueSpec(0, key, label, MissionValueKind.Text) { Rest = true, Help = keySpec?.Help }];
            if (specs.Count == 1)
            {
                fields.Add(ValueField(context, line, specs[0] with { Help = specs[0].Help ?? keySpec?.Help }, label,
                    KeyValue(line, specs[0])));
                continue;
            }

            // Named slots: the heading has the help of the key, and each value has its own field.
            fields.Add(new PageField(label, LineText(line), keySpec?.Help)
            {
                Heading = true, Hard = keySpec?.Hard == true, Changed = context.Changed(line),
            });
            fields.AddRange(specs.Where(s => line.ValueOf(s) != null).Select(s => ValueField(context, line, s, s.Label,
                KeyValue(line, s))));
        }

        return fields;
    }

    /// <summary>The section "Used by": the lines that name an item, each with a link.</summary>
    private static PageSection UsedBy(PageContext context, IReadOnlyList<MissionLine> lines, string title = "Used by") =>
        new("usedBy", $"{title} ({lines.Count.ToString(CultureInfo.InvariantCulture)})", EditorIcons.Link, null,
            lines.Select(l => RowField(context.Analysis.Script.Path, l)).ToList(),
            lines.Count == 0 ? ["No line of this mission names this item."] : []);

    /// <summary>The page of an item of the script with the common parts: the problems, Used by and the INI
    /// block.</summary>
    /// <param name="usedBy">The title of the section of the lines that name the item, for example "Given by".</param>
    private static MissionPageModel ItemPage(PageContext context, MissionSection section, string kind, char glyph,
        HelpText help, CompletenessReport? completeness, List<PageSection> sections, IReadOnlyList<PageField>? lead = null,
        string usedBy = "Used by")
    {
        sections.Add(UsedBy(context, MissionUses.Naming(context.Analysis.Index, section), usedBy));
        sections.Add(Problems(context.Analysis, SectionProblems(context.Analysis, section), context.ShowBaseGame));
        var item = ItemOf(section);
        var editable = context.Editable(section.Ref);
        var ship = section.Kind == MissionSectionKind.Ship;
        return new MissionPageModel(section.Nickname ?? section.Ref.Id.ToString(), kind, glyph, help, completeness,
            sections, section.Ref)
        {
            Lead = lead ?? [],
            ReadOnly = ReadOnlyText(context, section),
            Base = BaseOf(context, section),
            // The page menu of an item of the script (section 8.4 of the M6b design). A mission ship goes with its NPC
            // and its build when no other item uses them (decision D669).
            PageActions = editable && section.Nickname != null
                ?
                [
                    new PageAction("Duplicate", One($"Duplicate {section.Nickname}", new DuplicateItem(item))),
                    new PageAction("Delete", One($"Delete {section.Nickname}",
                        ship ? new RemoveMissionShip(item) : new RemoveItem(item))),
                ]
                : [],
            Rename = editable && section.Nickname != null
                ? name => name.Trim().Length == 0 || name.Trim() == section.Nickname
                    ? null
                    : One($"Rename {section.Nickname} to {name.Trim()}", new RenameItem(item, name.Trim()))
                : null,
        };
    }

    /// <summary>The page of a mission ship (section 10.6 of the M6a design): the ship, its pilot and voice, and its
    /// ship build. The page has the completeness of the three parts.</summary>
    private static MissionPageModel Ship(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var index = context.Analysis.Index;
        var sections = new List<PageSection>();
        var ship = new List<PageField>();
        var lines = index.LinesOf(section);
        bool Has(string key) => lines.Any(l => MissionIndex.Is(l.Key, key));

        // The start: a position and a rotation, or a place near an anchor.
        ship.AddRange(StartFields(context, section));
        ship.AddRange(KeyFields(context, section, "system", "label"));
        // The engine keeps the first of the initial orders and the initial behavior. Without one, the page offers the
        // initial orders.
        var first = lines.FirstOrDefault(l => MissionIndex.Is(l.Key, "init_objectives") || MissionIndex.Is(l.Key, "init_metabehavior"));
        ship.AddRange(KeyFields(context, section, first?.Key ?? "init_objectives"));
        ship.AddRange(KeyFields(context, section, "arrival_obj", "jumper", "random_name", "radius", "cargo"));
        var hullLabel = schema.Section("MsnShip")?.Key("hit_points")?.Label ?? "Hull points";
        ship.AddRange(Has("hit_points")
            ? KeyFields(context, section, "hit_points")
            : [new PageField(hullLabel, "Not set", schema.Notes["hullPointsNotSet"])
            {
                Edit = new FieldEdit(EditWidget.WholeNumber, $"Set {hullLabel}")
                {
                    Make = value => One($"Set {hullLabel}", new SetItemKey(ItemOf(section), "hit_points",
                        [LineSpec.Raw("hit_points", value.Scalar ?? "0")])),
                    ReadOnly = context.ReadOnlyOf(section.Ref),
                },
            }]);
        var notes = new List<string>();
        if (section.Nickname != null && index.TakenShips.TryGetValue(section.Nickname, out var formation) && formation != section)
            notes.Add($"Formation {formation.Nickname} takes this ship. A spawn of this ship spawns nothing.");
        sections.Add(new PageSection("ship", "Ship", EditorIcons.SpaceShuttle, schema.Help["ship"], ship, notes));

        var npc = MissionCompleteness.NpcOf(index, section);
        if (npc != null)
            sections.Add(ShipPilot(context, section, npc));

        if (MissionCompleteness.BuildOf(context.Result.ShipBuilds, index, section) is { } build)
            sections.Add(ShipBuild(context, build, section));

        var lead = npc != null && index.LinesOf(npc).FirstOrDefault(l => MissionIndex.Is(l.Key, "individual_name")) is { } nameLine
            ? new[] { new PageField("Name text", ValueField(context, nameLine, nameLine.Specs.FirstOrDefault() ??
                new MissionValueSpec(0, "name", "Name text", MissionValueKind.TextId), "Name text").Value) }
            : [];
        return ItemPage(context, section, "Mission ship", EditorIcons.SpaceShuttle, schema.Help["missionShip"],
            MissionCompleteness.OfShip(context.Result, context.Analysis, section), sections, lead);
    }

    /// <summary>The section "Pilot and voice" of an <c>[NPC]</c>.</summary>
    private static PageSection PilotAndVoice(PageContext context, MissionSection npc) =>
        new("pilot", "Pilot and voice", EditorIcons.User, MissionSchema.Retail.Help["pilotAndVoice"],
            KeyFields(context, npc, "affiliation", "voice", "space_costume", "individual_name", "npc_ship_arch"), []);

    /// <summary>
    /// The section "Pilot and voice" of a mission ship (section 8.4 of the M6b design). While the user can edit the ship,
    /// its first field picks another <c>[NPC]</c> ("Use another pilot..."). When other ships of the script use the same
    /// <c>[NPC]</c>, the chip "Used by {n} ships" shows, and "This ship only" gives the ship its own copy of the
    /// <c>[NPC]</c> in one undo entry.
    /// </summary>
    private static PageSection ShipPilot(PageContext context, MissionSection ship, MissionSection npc)
    {
        var pilot = PilotAndVoice(context, npc);
        if (!context.Editable(ship.Ref) || npc.Nickname is not { } name)
            return pilot;
        var index = context.Analysis.Index;
        var users = index.Document.OfKind(MissionSectionKind.Ship)
            .Count(s => MissionCompleteness.NpcOf(index, s) == npc);
        var fields = KeyFields(context, ship, "npc").Select(f => f with { Help = UseAnotherPilotTip }).ToList();
        if (users > 1)
        {
            fields.Add(new PageField("Use of the pilot", UsedByText(users), PilotUsersTip)
            {
                Chips = [new PageChip(UsedByText(users), PilotUsersTip, null)],
            });
        }

        fields.AddRange(pilot.Fields);
        var taken = index.Document.OfKind(MissionSectionKind.Npc).Select(s => s.Nickname).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copy = MissionNaming.Free(context.Analysis.Script.Path, "NPC", taken, name);
        return pilot with
        {
            Fields = fields,
            Actions = users > 1
                ?
                [
                    new PageAction("This ship only", new MissionBatch($"This ship only: {name}",
                    [
                        new DuplicateItem(ItemOf(npc)) { NewName = copy },
                        new SetItemKey(ItemOf(ship), "npc", [LineSpec.Raw("npc", copy)]),
                    ]), Tooltip: ThisShipOnlyPilotTip),
                ]
                : [],
        };
    }

    /// <summary>The tooltip of the button "This ship only" of the pilot of a ship.</summary>
    internal const string ThisShipOnlyPilotTip = "Give this ship its own copy of the pilot and voice. Then change the copy.";

    /// <summary>
    /// The start of a mission ship or a formation (section 8.4 of the M6b design): "Start position" with the rotation, or
    /// "Placed near" an object. While the user can edit the item, the field "Start" switches between the two with
    /// <see cref="PlaceMissionShip"/>. A new place near an object names no object, so the anchor shows "Missing" until
    /// the user picks one.
    /// </summary>
    private static List<PageField> StartFields(PageContext context, MissionSection section)
    {
        var near = context.Analysis.Index.LinesOf(section).Any(l => MissionIndex.Is(l.Key, "rel_pos"));
        var fields = new List<PageField>();
        if (context.Editable(section.Ref))
        {
            var item = ItemOf(section);
            fields.Add(new PageField("Start", near ? StartNear : StartAt, StartTip)
            {
                Edit = new FieldEdit(EditWidget.Words, "Set the start")
                {
                    Value = near ? "near" : "position",
                    Choices = [("position", StartAt, null), ("near", StartNear, null)],
                    Make = value => (value.Scalar == "near") == near ? null
                        : One("Set the start", value.Scalar == "near"
                            ? new PlaceMissionShip(item) { Near = new NearSpec(MissionLibrary.NoName) }
                            : new PlaceMissionShip(item) { Position = MissionValue.Of(0, 0, 0) }),
                },
            });
        }

        fields.AddRange(near ? KeyFields(context, section, "rel_pos") : KeyFields(context, section, "position", "orientation"));
        return fields;
    }

    private const string StartAt = "At a position";
    private const string StartNear = "Near an object";

    private const string StartTip = "Where the item starts: at a position with a rotation, or near an object of the " +
                                    "system, with a heading and a range.";

    /// <summary>The tooltip of the field that picks another pilot of a ship (section 8.11 of the M6b design).</summary>
    internal const string UseAnotherPilotTip = "Pick another pilot and voice for this ship.";

    /// <summary>The tooltip of the button "This ship only" (section 8.11 of the M6b design).</summary>
    internal const string ThisShipOnlyTip = "Give this ship its own copy of the ship build. Then change the copy.";

    /// <summary>
    /// The section "Ship build" of an <c>[NPCShipArch]</c> of the mission ship files, with the labels and the help texts
    /// of the schema. A name of the game data shows its name text, or the "Missing" look. The loadout has the chip
    /// "Used by {n} ships" and the button "This ship only" (decision D670).
    /// </summary>
    /// <param name="ship">The mission ship of the page, or null.</param>
    private static PageSection ShipBuild(PageContext context, NpcShipBuild build, MissionSection? ship = null)
    {
        var schema = MissionSchema.Retail;
        var world = context.Analysis.Index.World;
        var fields = new List<PageField>();
        var buildItem = new ItemRef(build.Ref.Path, build.Ref.Id);
        // The build fields take the read-only reason of the ship build file. A change of a build of a shared file asks
        // first, so each widget runs one batch at the end of its gesture (review finding RB2 of the M6b design).
        var readOnly = context.ReadOnlyOf(build.Ref);
        var shared = context.SharedBuildFile(build.Ref.Path);
        FieldEdit Edit(string key, MissionValueKind kind, string label, int row) =>
            new(MissionSchema.ScopeOf(kind) == MissionKindScope.World && MissionSchema.ListOf(kind) != null
                ? EditWidget.Picker
                : kind == MissionValueKind.WholeNumber ? EditWidget.WholeNumber : EditWidget.Text, $"Set {label}")
            {
                Value = MissionIndex.Text(build.Section[row][0]),
                List = MissionSchema.ListOf(kind),
                Make = value => One($"Set {label}",
                    new SetLineValue(new RowRef(buildItem, row) { ExpectKey = key }, value) { Name = key }),
                ReadOnly = readOnly,
                Confirms = shared,
            };
        foreach (var key in schema.ShipBuild.Keys.Where(k => !MissionIndex.Is(k.Key, "nickname")))
        {
            if (build.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, key.Key) && e.Count > 0) is not { } entry)
                continue;
            var row = build.Section.ToList().IndexOf(entry);
            if (schema.KeyList(schema.ShipBuild.Type, key.Key) is { } listSpec)
            {
                var model = ListFieldModel.Read(listSpec, [ListFieldLine.Of(entry)]);
                fields.Add(new PageField(listSpec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), key.Help)
                {
                    List = model, Hard = key.Hard,
                    Edit = new FieldEdit(EditWidget.List, $"Set {listSpec.Label}")
                    {
                        MakeList = list => One($"Set {listSpec.Label}", new SetItemKey(buildItem, key.Key,
                            list.Write().Select(l => LineSpec.Raw(l.Key, [.. l.Texts])).ToList())),
                        ReadOnly = readOnly,
                        Confirms = shared,
                    },
                });
                continue;
            }

            var value = MissionIndex.Text(entry[0]);
            if (MissionSchema.ScopeOf(key.Kind) != MissionKindScope.World || !world.Knows(key.Kind))
            {
                fields.Add(new PageField(key.Label, value, key.Help) { Hard = key.Hard, Edit = Edit(key.Key, key.Kind, key.Label, row) });
                continue;
            }

            var missing = !world.Has(key.Kind, value);
            var name = context.NameOf(key.Kind, value);
            fields.Add(new PageField(key.Label, missing ? $"Missing: {value}" : name is { Length: > 0 } ? $"{name} ({value})" : value,
                key.Help, build.Ref)
            {
                Hard = key.Hard,
                Missing = missing,
                Reference = new PageReference(value, name is { Length: > 0 } ? name : null, MissionGlyphs.OfValue(key.Kind),
                    ValueKindText(key.Kind)),
                Edit = Edit(key.Key, key.Kind, key.Label, row),
            });
            if (ship != null && MissionIndex.Is(key.Key, "loadout"))
            {
                var ships = ShipsOfBuild(context, build);
                fields.Add(new PageField("Use of the build", UsedByText(ships), UsedByTip(ships, shared))
                {
                    Chips = [new PageChip(UsedByText(ships), UsedByTip(ships, shared), null)],
                });
            }
        }

        var actions = ship != null && context.Editable(ship.Ref) && build.Value("loadout") is { } loadout
            ? new[]
            {
                new PageAction("This ship only", One("This ship only",
                    new SetShipLoadout(ItemOf(ship), loadout, LoadoutScope.ThisShip)), Tooltip: ThisShipOnlyTip),
            }
            : [];
        return new PageSection("build", "Ship build", EditorIcons.Wrench, schema.Help["shipBuild"], fields, [])
        {
            Actions = actions,
        };
    }

    /// <summary>The page of a mission formation (section 10.7 of the M6a design).</summary>
    private static MissionPageModel Formation(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var sections = new List<PageSection>
        {
            new("formation", "Formation", EditorIcons.Users, schema.Help["formation"],
            [
                .. KeyFields(context, section, "formation"), .. StartFields(context, section),
                .. KeyFields(context, section, "system", "npc", "label", "init_objectives", "arrival_obj"),
            ], []),
            new("members", "Members", EditorIcons.SpaceShuttle, schema.Help["formationMembers"],
                KeyFields(context, section, "ship"), []),
        };
        return ItemPage(context, section, "Formation", EditorIcons.Users, schema.Help["formation"], null, sections);
    }

    /// <summary>The page of a mission solar (section 10.8 of the M6a design).</summary>
    private static MissionPageModel Solar(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var sections = new List<PageSection>
        {
            new("solar", "Solar", EditorIcons.Satellite, schema.Help["solar"],
                KeyFields(context, section, "archetype", "loadout", "base", "faction", "position", "orientation", "system",
                    "visit", "label", "string_id", "radius"), []),
            new("pilot", "Pilot and voice", EditorIcons.User, schema.Help["pilotAndVoice"],
                KeyFields(context, section, "pilot", "voice", "costume"), []),
        };
        return ItemPage(context, section, "Solar", EditorIcons.Satellite, schema.Help["solar"], null, sections);
    }

    /// <summary>The page of mission loot (section 10.9 of the M6a design).</summary>
    private static MissionPageModel Loot(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var sections = new List<PageSection>
        {
            new("loot", "Loot", EditorIcons.Box, schema.Help["loot"],
                KeyFields(context, section, "archetype", "position", "rel_pos_obj", "rel_pos_offset", "orientation",
                    "velocity", "angular_velocity", "equip_amount", "health", "can_jettison", "system", "label", "string_id"),
                []),
        };
        return ItemPage(context, section, "Loot", EditorIcons.Box, schema.Help["loot"], null, sections);
    }

    /// <summary>The page of an <c>[NPC]</c> (section 10.13 of the M6a design).</summary>
    private static MissionPageModel Npc(PageContext context, MissionSection section) =>
        ItemPage(context, section, "NPC", EditorIcons.User, MissionSchema.Retail.Help["npc"], null,
            [PilotAndVoice(context, section)]);

    // ---- The other pages of section 10.

    /// <summary>The page of an order list (section 10.10 of the M6a design): its order prefab and its orders as a list
    /// field. It also lists the lines that give it.</summary>
    private static MissionPageModel OrderList(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var lead = context.Analysis.Prefabs?.Instances.Where(i => i.CoreSection == section)
            .Select(i => new PageField("Order prefab", i.Definition.Name, i.Definition.Help.FirstSentence,
                new MissionRef(context.Analysis.Script.Path, MissionItemKind.PrefabInstance, Key: i.Key))).ToList() ?? [];
        var orders = new List<PageField>();
        if (schema.OrderList(section.Type) is { } spec)
        {
            var lines = section.Section.Where(e => spec.Keys.Any(k => MissionIndex.Is(e.Name, k))).Select(ListFieldLine.Of).ToList();
            var model = ListFieldModel.Read(spec, lines);
            orders.Add(new PageField(spec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), spec.Help)
            {
                List = model, Edit = KeyListEdit(context, section, "orders", spec.Label),
            });
        }

        // Each point of an order has "Pick in Scene" (section 8.4 of the M6b design).
        var point = 0;
        foreach (var line in context.Editable(section.Ref) ? context.Analysis.Index.LinesOf(section) : [])
        {
            if (line.Name?.Values.FirstOrDefault(v => v.Kind == MissionValueKind.Position) is not { } position ||
                line.ValueOf(position) == null)
                continue;
            point++;
            orders.Add(ValueField(context, line, position,
                    $"Point {point.ToString(CultureInfo.InvariantCulture)}", KeyValue(line, position)) with
                {
                    Help = $"The point of line {LineNumber(line)}: {line.Name.Plain}.",
                });
        }

        var sections = new List<PageSection>
        {
            new("orders", "Orders", EditorIcons.Route, schema.Help["orderList"], orders, []),
        };
        return ItemPage(context, section, "Order list", EditorIcons.List, schema.Help["orderList"], null, sections, lead,
            "Given by");
    }

    /// <summary>The page of an objective (section 10.11 of the M6a design): the type, the texts, the state and the
    /// place. It also lists the lines that set, complete or hide it.</summary>
    private static MissionPageModel Objective(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var typeSpec = schema.Section("NNObjective")?.Key("type");
        var fields = new List<PageField>();
        var typeLine = context.Analysis.Index.LinesOf(section).FirstOrDefault(l => MissionIndex.Is(l.Key, "type"));
        if (context.Editable(section.Ref))
        {
            fields.AddRange(ObjectiveEdits(context, section, typeLine));
        }
        else if (section.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "type")) is { Count: > 0 } type)
        {
            var kind = MissionIndex.Text(type[0]);
            fields.Add(new PageField(typeSpec?.Label ?? "Type", ObjectiveTypes.GetValueOrDefault(kind.ToLowerInvariant(), kind),
                typeSpec?.Help) { Hard = typeSpec?.Hard == true });
            foreach (var id in MissionIndex.ObjectiveTextIds(type))
            {
                var text = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                           context.Text(number) is { Length: > 0 } value
                    ? $"{value} ({id})"
                    : id;
                fields.Add(new PageField("Text", text));
            }

            if ((MissionIndex.Is(kind, "navmarker") || MissionIndex.Is(kind, "rep_inst")) && type.Count > 6)
            {
                var system = MissionIndex.Text(type[1]);
                fields.Add(new PageField("System", SystemText(context, system), null, new SystemLink(system))
                {
                    Reference = new PageReference(system, context.SystemName?.Invoke(system) is { Length: > 0 } name ? name : null,
                        MissionGlyphs.OfValue(MissionValueKind.System), "System"),
                });
                var position = Enumerable.Range(4, 3).Select(i => MissionIndex.Text(type[i])).ToList();
                fields.Add(new PageField("Position", string.Join(", ", position))
                {
                    Parts = new[] { "X", "Y", "Z" }.Select((axis, i) => (axis, position[i])).ToList(),
                });
                if (MissionIndex.Is(kind, "rep_inst") && type.Count > 7)
                    fields.Add(new PageField("Object", MissionIndex.Text(type[7])));
            }
            else if (MissionIndex.Is(kind, "base") && type.Count > 1)
            {
                var baseName = MissionIndex.Text(type[1]);
                var name = context.NameOf(MissionValueKind.Base, baseName);
                fields.Add(new PageField("Base", name is { Length: > 0 } ? $"{name} ({baseName})" : baseName)
                {
                    Reference = new PageReference(baseName, name is { Length: > 0 } ? name : null,
                        MissionGlyphs.OfValue(MissionValueKind.Base), "Base"),
                });
            }
        }

        fields.AddRange(KeyFields(context, section, "state", "hidden"));
        var users = MissionUses.Naming(context.Analysis.Index, section);
        var script = context.Analysis.Script.Path;
        var sections = new List<PageSection>
        {
            new("objective", "Objective", EditorIcons.Bullseye, schema.Help["objective"], fields, []),
            Lines("setBy", "Set by", users.Where(l => MissionIndex.Is(l.Key, "Act_SetNNObj")), script),
            Lines("completedBy", "Completed by", users.Where(l => MissionIndex.Is(l.Key, "Act_SetNNState")), script),
            Lines("hiddenBy", "Hidden by", users.Where(l => MissionIndex.Is(l.Key, "Act_SetNNHidden")), script),
            Problems(context.Analysis, SectionProblems(context.Analysis, section), context.ShowBaseGame),
        };
        return new MissionPageModel(section.Nickname ?? section.Ref.Id.ToString(), "Objective", EditorIcons.Bullseye,
            schema.Help["objective"], null, sections, section.Ref)
        {
            ReadOnly = ReadOnlyText(context, section), Base = BaseOf(context, section),
        };
    }

    /// <summary>The words of the type of an objective, as retail writes them, with their plain names. The game shows an
    /// objective as a text, as a point in space, as an object or as a base.</summary>
    private static readonly IReadOnlyDictionary<string, string> ObjectiveTypes = new Dictionary<string, string>
    {
        ["ids"] = "A text",
        ["navmarker"] = "A point in space",
        ["rep_inst"] = "An object",
        ["base"] = "A base",
    };

    /// <summary>The values of the <c>type</c> line of an objective type, by their place in the line: 0 is the type
    /// word. A text id is a number, and a name that the user picks later is <see cref="MissionLibrary.NoName"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> ObjectiveSlots = new Dictionary<string, string[]>
    {
        ["ids"] = ["type", "text"],
        ["navmarker"] = ["type", "system", "text", "text2", "x", "y", "z"],
        ["rep_inst"] = ["type", "system", "text", "text2", "x", "y", "z", "object"],
        ["base"] = ["type", "base", "text"],
    };

    /// <summary>
    /// The edit fields of an objective (section 8.4 of the M6b design): "Type", the texts, and the system, the position
    /// with "Pick in Scene", the object or the base that the type needs. The values of the <c>type</c> line have no
    /// names in the schema. Each edit writes the whole line with <see cref="SetItemKey"/>, so a line of another type
    /// gets the values of the new type. A value that both types have keeps its value, and a new value gets a first
    /// value.
    /// </summary>
    /// <param name="type">The <c>type</c> line, or null for an objective without one. "Type" then adds the line.</param>
    private static List<PageField> ObjectiveEdits(PageContext context, MissionSection section, MissionLine? type)
    {
        var item = ItemOf(section);
        var kind = (type?.Value(0) ?? "").Trim().ToLowerInvariant();
        var slots = ObjectiveSlots.GetValueOrDefault(kind) ?? ["type"];
        // The current value of each slot of the line.
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; type != null && i < slots.Length && i < type.Count; i++)
            current[slots[i]] = type.Value(i) ?? "";
        string[] LineOf(string newKind, IReadOnlyDictionary<string, string> values) =>
        [
            newKind,
            .. ObjectiveSlots[newKind].Skip(1).Select(slot => values.GetValueOrDefault(slot) ?? slot switch
            {
                "text" or "text2" or "x" or "y" or "z" => "0",
                _ => MissionLibrary.NoName,
            }),
        ];
        MissionBatch? Write(string label, string newKind, IReadOnlyDictionary<string, string> values) =>
            ObjectiveSlots.ContainsKey(newKind)
                ? One($"Set {label}", new SetItemKey(item, "type", [LineSpec.Raw("type", LineOf(newKind, values))]))
                : null;
        MissionBatch? Set(string label, params (string Slot, string Value)[] changes)
        {
            var values = new Dictionary<string, string>(current, StringComparer.Ordinal);
            foreach (var (slot, value) in changes)
                values[slot] = value;
            return Write(label, kind, values);
        }

        var readOnly = context.ReadOnlyOf(section.Ref);
        var changed = type != null && context.Changed(type);
        var typeSpec = MissionSchema.Retail.Section("NNObjective")?.Key("type");
        var fields = new List<PageField>
        {
            new(typeSpec?.Label ?? "Type", type == null ? NotSet : ObjectiveTypes.GetValueOrDefault(kind, kind), typeSpec?.Help)
            {
                Hard = typeSpec?.Hard == true, Changed = changed,
                Edit = new FieldEdit(EditWidget.Words, "Set the type")
                {
                    Value = kind,
                    Choices = ObjectiveTypes.Select(p => (p.Key, p.Value, (string?)null)).ToList(),
                    Make = value => value.Scalar is { } word && !word.Equals(kind, StringComparison.OrdinalIgnoreCase)
                        ? Write("the type", word.ToLowerInvariant(), current)
                        : null,
                    ReadOnly = readOnly,
                },
            },
        };
        if (!ObjectiveSlots.ContainsKey(kind))
            return fields;

        FieldEdit Pick(string label, string slot, MissionValueKind valueKind) => new(EditWidget.Picker, $"Set {label}")
        {
            Value = current.GetValueOrDefault(slot) ?? "",
            List = MissionSchema.ListOf(valueKind),
            Scope = context.Analysis.Script.Path,
            Make = value => Set(label, (slot, value.Scalar ?? "")),
            ReadOnly = readOnly,
        };
        PageField Name(string label, string slot, MissionValueKind valueKind)
        {
            var value = current.GetValueOrDefault(slot) ?? MissionLibrary.NoName;
            var name = valueKind == MissionValueKind.System ? context.SystemName?.Invoke(value) : context.NameOf(valueKind, value);
            var missing = MissionIndex.Is(value, MissionLibrary.NoName);
            return new PageField(label, missing ? $"Missing: {value}" : name is { Length: > 0 } ? $"{name} ({value})" : value)
            {
                Missing = missing, Changed = changed, Edit = Pick(label, slot, valueKind),
            };
        }

        foreach (var slot in ObjectiveSlots[kind].Where(s => s is "text" or "text2"))
        {
            var id = current.GetValueOrDefault(slot) ?? "0";
            var text = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                       context.Text(number) is { Length: > 0 } named
                ? $"{named} ({id})"
                : id;
            var label = slot == "text" ? "Text" : "Second text";
            fields.Add(new PageField(label, text, "The text id of the objective text.")
            {
                Changed = changed,
                Edit = new FieldEdit(EditWidget.TextId, $"Set {label}")
                {
                    Value = id, Make = value => Set(label, (slot, value.Scalar ?? "0")), ReadOnly = readOnly,
                },
            });
        }

        if (kind is "navmarker" or "rep_inst")
        {
            fields.Add(Name("System", "system", MissionValueKind.System));
            var position = new[] { "x", "y", "z" }.Select(a => current.GetValueOrDefault(a) ?? "0").ToList();
            fields.Add(new PageField("Position", string.Join(", ", position))
            {
                Changed = changed,
                Edit = new FieldEdit(EditWidget.Position, "Set the position")
                {
                    Value = string.Join(", ", position),
                    PickInScene = true,
                    PickSystem = current.GetValueOrDefault("system"),
                    Make = value => value.Items.Count == 3
                        ? Set("the position", ("x", value.Items[0].Scalar ?? "0"), ("y", value.Items[1].Scalar ?? "0"),
                            ("z", value.Items[2].Scalar ?? "0"))
                        : null,
                    ReadOnly = readOnly,
                },
            });
        }

        if (kind == "rep_inst")
            fields.Add(Name("Object", "object", MissionValueKind.SystemObject));
        if (kind == "base")
            fields.Add(Name("Base", "base", MissionValueKind.Base));
        return fields;
    }

    /// <summary>A section of the lines of other triggers, each with a link.</summary>
    private static PageSection Lines(string id, string title, IEnumerable<MissionLine> lines, string script)
    {
        var list = lines.ToList();
        return new PageSection(id, title, EditorIcons.Link, null, list.Select(l => RowField(script, l)).ToList(),
            list.Count == 0 ? ["No line."] : []);
    }

    /// <summary>The page of a conversation (section 10.12 of the M6a design): its completeness, its system and its
    /// lines as a list field. It also lists the lines that start it.</summary>
    private static MissionPageModel Conversation(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var sections = new List<PageSection>
        {
            new("conversation", "Conversation", EditorIcons.Comments, schema.Help["conversation"],
                KeyFields(context, section, "system"), []),
            new("lines", "Lines", EditorIcons.VolumeUp, schema.Help["conversationLines"], KeyFields(context, section, "line"), []),
        };
        return ItemPage(context, section, "Conversation", EditorIcons.Comments, schema.Help["conversation"],
            MissionCompleteness.OfConversation(context.Analysis, section), sections, null, "Started by");
    }

    /// <summary>The page of a random encounter (section 10.16 of the M6a design): the steps of its Ambush prefab and
    /// the keys of the encounter. It also lists the triggers that arm it, disarm it and react to its spawn. A key that
    /// the schema does not know goes to Advanced.</summary>
    private static MissionPageModel RandomEncounter(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var analysis = context.Analysis;
        var ambush = analysis.Prefabs?.Instances.FirstOrDefault(i => i.CoreSection == section);
        var keys = schema.Section(section.Type)?.Keys.Select(k => k.Key).Where(k => !MissionIndex.Is(k, "nickname")).ToArray() ?? [];
        var fields = KeyFields(context, section, keys);
        var users = MissionUses.Naming(analysis.Index, section);
        var script = analysis.Script.Path;
        var known = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase) { "nickname" };
        var advanced = section.Section.Where(e => !known.Contains(e.Name.Trim()))
            .Select(e => new PageField(e.Name, string.Join(", ", e.Select(MissionIndex.Text)), schema.Notes["unknownKey"])).ToList();
        var sections = new List<PageSection>
        {
            new("encounter", "Encounter", EditorIcons.Crosshairs, schema.Help["randomEncounter"], fields, []),
            Lines("armedBy", "Armed by", users.Where(l => MissionIndex.Is(l.Key, "Act_EnableEnc")), script),
            Lines("disarmedBy", "Disarmed by", users.Where(l => MissionIndex.Is(l.Key, "Act_DisableEnc")), script),
            Lines("onSpawn", "On spawn", users.Where(l => MissionIndex.Is(l.Key, "Cnd_EncLaunched")), script),
            Problems(analysis, SectionProblems(analysis, section), context.ShowBaseGame),
        };
        if (advanced.Count > 0)
            sections.Add(new PageSection("advanced", "Advanced", EditorIcons.Cog, null, advanced, [], Advanced: true));
        return new MissionPageModel(section.Nickname ?? section.Ref.Id.ToString(), "Random encounter", EditorIcons.Crosshairs,
            schema.Help["randomEncounter"], ambush != null ? MissionCompleteness.OfInstance(analysis, ambush) : null, sections,
            section.Ref);
    }

    /// <summary>The page of a label (section 10.15 of the M6a design): its members and the lines that name it.</summary>
    /// <remarks>Each member has its own label line in its own section. No list codec reads the lines of many sections,
    /// so the page lists the members as rows with a link to each member, not as a list field.</remarks>
    private static MissionPageModel Label(PageContext context, string label)
    {
        var index = context.Analysis.Index;
        var members = index.Labels.TryGetValue(label, out var list) ? list : [];
        var script = context.Analysis.Script.Path;
        var editable = context.Editable(members.FirstOrDefault()?.Ref ?? new SectionRef(script, new SectionIdentity("Mission", null)));
        // Each member has its own label line, so the list of members is one row for each member with "Remove", and a
        // field that adds a member (section 8.4 of the M6b design).
        var memberFields = members.Select(m => new PageField(KindOf(MissionRef.KindOf(m.Kind), null, MissionSchema.Retail).Kind,
            m.Nickname ?? m.Ref.Id.ToString(), null, MissionRef.OfSection(m.Ref, -1))
        {
            Actions = editable
                ? [new PageAction("Remove", One($"Remove {m.Nickname} from {label}", new RemoveLabel(ItemOf(m), label)))]
                : [],
        }).ToList();
        if (editable)
        {
            memberFields.Add(new PageField("Add", "", "Pick a ship, a formation or a solar. It gets this label.")
            {
                Edit = new FieldEdit(EditWidget.AddPicker, $"Add a member to {label}")
                {
                    List = string.Join("+", new[] { MissionValueKind.Ship, MissionValueKind.Formation, MissionValueKind.Solar }
                        .Select(MissionSchema.ListOf).OfType<string>()),
                    Scope = script,
                    Add = name => (index.Item(MissionSectionKind.Ship, name) ?? index.Item(MissionSectionKind.Formation, name) ??
                                   index.Item(MissionSectionKind.Solar, name)) is { } member
                        ? One($"Add {name} to {label}", new AddLabel(ItemOf(member), label))
                        : null,
                },
            });
        }

        var sections = new List<PageSection>
        {
            new("members", $"Members ({members.Count.ToString(CultureInfo.InvariantCulture)})", EditorIcons.Users,
                MissionSchema.Retail.Help["labelMembers"], memberFields, []),
            UsedBy(context, index.ReferencesTo(MissionValueKind.Object, label).Where(r => r.Section == null)
                .Select(r => r.Line).Distinct().ToList()),
        };
        return new MissionPageModel(label, "Label", EditorIcons.Tag, MissionSchema.Retail.Help["label"], null, sections, null);
    }

    /// <summary>The page of an act or of the frame "Not started by the mission" (section 10.17 of the M6a
    /// design).</summary>
    private static MissionPageModel Act(PageContext context, string key)
    {
        var analysis = context.Analysis;
        var acts = context.Acts ?? analysis.Acts ?? MissionActs.Propose(analysis.Index);
        var script = analysis.Script.Path;
        var frame = key == MissionActs.NotStartedKey;
        var act = acts.Act(key);
        var triggers = acts.TriggersOf(key);
        var title = frame ? "Not started by the mission"
            : act != null ? MissionGraph.ActTitle(act, context.SystemName)
            : key;
        var walkthrough = MissionGraph.Walkthrough(triggers.Count, act?.Checkpoints ?? 0);
        var (kind, glyph, help) = KindOf(MissionItemKind.Act, key, MissionSchema.Retail);
        // The fields of an act edit its record in the graph file (section 8.4 of the M6b design). The first act edit
        // writes all proposed acts (decision D679).
        var editable = act != null && context.Editable(new SectionRef(script, new SectionIdentity("Mission", null)));
        var reference = act != null ? new ActRef(script, MissionActs.NicknameOf(act)) : null;
        PageField Link(MissionTrigger t) => new(t.Nickname, t.Condition?.Sentence() ?? "No condition", null,
            new MissionRef(script, MissionItemKind.Trigger, t.Section.Ref))
        {
            Actions = editable ? ActMenu(context, acts, act!, t) : [],
        };
        var actFields = new List<PageField>
        {
            new("Name", title)
            {
                Edit = editable
                    ? new FieldEdit(EditWidget.Text, "Set the name of the act")
                    {
                        Value = title, Make = value => One("Set the name of the act", new SetAct(reference!) { Name = value.Scalar }),
                    }
                    : null,
            },
            new("Walkthrough", walkthrough),
        };
        if (editable)
        {
            actFields.Add(new PageField("Walkthrough text", act!.Text ?? "", "The walkthrough of the act in plain words.")
            {
                Edit = new FieldEdit(EditWidget.Text, "Set the walkthrough text")
                {
                    Value = act.Text ?? "", Make = value => One("Set the walkthrough text", new SetAct(reference!) { Text = value.Scalar }),
                },
            });
        }

        var triggerFields = triggers.Select(Link).ToList();
        if (editable)
        {
            triggerFields.Add(new PageField("Add", "", "Pick a trigger. It moves into this act.")
            {
                Edit = new FieldEdit(EditWidget.AddPicker, "Add a trigger to the act")
                {
                    List = MissionSchema.ListOf(MissionValueKind.Trigger),
                    Scope = script,
                    Add = name => One($"Add {name} to {title}", new SetActMembers(reference!,
                        [.. triggers.Select(t => t.Nickname), name])),
                },
            });
        }

        var sections = new List<PageSection>
        {
            new("act", kind, glyph, help, actFields, []),
            new("triggers", "Triggers", EditorIcons.Bolt, null, triggerFields, []),
            new("checkpoints", "Checkpoints", EditorIcons.Save, null,
                triggers.Where(t => t.ActionsOf("Act_Save").Any()).Select(Link).ToList(), []),
        };
        return new MissionPageModel(title, kind, glyph, help, null, sections, null)
        {
            PageActions = editable ? [new PageAction("Delete", One($"Delete act {title}", new RemoveAct(reference!)))] : [],
            Rename = editable
                ? name => name.Trim().Length == 0 || name.Trim() == title
                    ? null
                    : One("Set the name of the act", new SetAct(reference!) { Name = name.Trim() })
                : null,
        };
    }

    /// <summary>
    /// The row menu of a trigger of an act (section 8.4 of the M6b design): "Move to {act}" for each other act, and
    /// "Remove" for a trigger that the mission does not reach. Each trigger that a start trigger reaches is in an act,
    /// so a removed trigger would go back to the act of the proposal rule (decision D679). A trigger is in one act, so a
    /// move takes it out of this act.
    /// </summary>
    private static IReadOnlyList<PageAction> ActMenu(PageContext context, MissionActs acts, MissionAct act,
        MissionTrigger trigger)
    {
        var script = context.Analysis.Script.Path;
        var result = acts.Acts.Where(other => other != act).Select(other =>
        {
            var title = MissionGraph.ActTitle(other, context.SystemName);
            return new PageAction($"Move to {title}", One($"Move {trigger.Nickname} to {title}",
                new SetActMembers(new ActRef(script, MissionActs.NicknameOf(other)),
                    [.. other.Triggers.Select(t => t.Nickname), trigger.Nickname])));
        }).ToList();
        if (!context.Analysis.Index.Reached.Contains(trigger))
        {
            var title = MissionGraph.ActTitle(act, context.SystemName);
            result.Add(new PageAction("Remove", One($"Remove {trigger.Nickname} from {title}",
                new SetActMembers(new ActRef(script, MissionActs.NicknameOf(act)),
                    act.Triggers.Where(t => t != trigger).Select(t => t.Nickname).ToList()))));
        }

        return result;
    }

    // ---- The prefab pages (sections 10.4 and 10.5).

    /// <summary>The definition of the row of a prefab in the Hierarchy: the key is the prefab as a record names it, for
    /// example <c>builtin:spawnWave</c> or <c>mod:ol_gate</c>. An id without a prefix is a built-in prefab.</summary>
    private static MissionPrefabDefinition? KindOfKey(MissionAnalysis analysis, string? key)
    {
        if (key == null)
            return null;
        if (analysis.Prefabs?.Instances.FirstOrDefault(i => i.PrefabKey.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } instance)
            return instance.Definition;
        return MissionPrefabs.Retail.Definition(PrefabRef.TryParse(key) is { Builtin: true } builtin ? builtin.Id : key);
    }

    /// <summary>The page of a prefab kind (section 10.5 of the M6a design): what it is, what it writes, its fields, its
    /// steps and its uses. A prefab of a mod has the same page.</summary>
    /// <param name="key">The prefab as a record names it (see <see cref="MissionPrefabInstance.PrefabKey"/>).</param>
    private static MissionPageModel PrefabKind(PageContext context, MissionPrefabDefinition definition, string key)
    {
        var script = context.Analysis.Script.Path;
        var prefabKey = PrefabRef.TryParse(key)?.RecordText ?? PrefabRef.BuiltinOf(key).RecordText;
        bool Of(MissionPrefabInstance i) => i.PrefabKey.Equals(prefabKey, StringComparison.OrdinalIgnoreCase);
        var uses = context.Analysis.Prefabs?.Instances.Where(Of).ToList() ?? [];
        var total = context.Result.Analyses.Values.Sum(a => a.Prefabs?.Instances.Count(Of) ?? 0);
        var sections = new List<PageSection>
        {
            new("whatItIs", "What it is", EditorIcons.InfoCircle, null, [], [definition.Help.WhatItIs]),
            new("whatItWrites", "What it writes", EditorIcons.Pen, null, [], [definition.Help.WhatItWrites]),
            new("fields", "Fields", MissionGlyphs.OfPrefab(definition.Id), null, definition.Fields.Select(f => new PageField(
                f.Label, f.List?.Codec switch
                {
                    PrefabListCodec.Lines => $"A list of {f.List.Items}",
                    PrefabListCodec.Triggers => $"A list of {f.List.Items}",
                    _ => "One value",
                }, f.Help)).ToList(), []),
            new("steps", "Steps", EditorIcons.CheckCircle, null,
                definition.Steps.Select(s => new PageField(s.Name, s.Reason)).ToList(), []),
            new("uses", $"Uses ({uses.Count.ToString(CultureInfo.InvariantCulture)})", EditorIcons.Link, null,
                uses.Select(i => new PageField(i.Name, i.Core?.Condition?.Sentence() ?? "", null,
                    new MissionRef(script, MissionItemKind.PrefabInstance, Key: i.Key))).ToList(),
                [total == 1 ? "1 instance in all missions of the browser." : $"{total.ToString(CultureInfo.InvariantCulture)} instances in all missions of the browser."]),
        };
        return new MissionPageModel(definition.Name, "Behavior prefab", MissionGlyphs.OfPrefab(definition.Id), definition.Help,
            null, sections, null);
    }

    // ---- The link pages (section 10.14).

    /// <summary>The page of a cutscene link (section 10.14 of the M6a design): the line that plays a space cutscene. It
    /// shows its completeness, its file, its anchor, the trigger that ends it, and a link that opens it.</summary>
    private static MissionPageModel CutsceneLink(PageContext context, MissionTrigger trigger, MissionLine line)
    {
        var analysis = context.Analysis;
        var schema = MissionSchema.Retail;
        var script = analysis.Script.Path;
        var file = line.Value(0) ?? "";
        var instance = analysis.Prefabs?.Instances.FirstOrDefault(i => i.Definition.Id == "spaceCutscene" && i.Core == trigger);
        var revert = instance?.Role("revert").FirstOrDefault();
        var fields = new List<PageField> { new("Cutscene file", file, line.Name?.Value("file")?.Help, new CutsceneLink(file)) };
        if (line.Name?.Value("anchor") is { } anchorSpec && line.ValueOf(anchorSpec) != null)
            fields.Add(ValueField(context, line, anchorSpec, "Anchor"));
        if (revert != null)
        {
            fields.Add(new PageField("Ends when", revert.Condition?.Sentence() ?? "No condition"));
            fields.Add(new PageField("Camera back", revert.Nickname, null,
                new MissionRef(script, MissionItemKind.Trigger, revert.Section.Ref)));
        }

        fields.Add(new PageField("Open in the Cutscene tab", file, schema.Notes["openCutscene"], new CutsceneLink(file)));
        var problems = analysis.Problems.Where(p => Equals(p.Target, line.Section.Ref) && p.EntryIndex == line.Entry).ToList();
        var sections = new List<PageSection>
        {
            new("cutscene", "Cutscene", EditorIcons.Film, schema.Help["cutsceneLink"], fields, []),
            Lines("addedBy", "Played by", [line], script),
            Problems(analysis, problems, context.ShowBaseGame),
        };
        return new MissionPageModel($"{trigger.Nickname}: {MissionFiles.Display(file)}", "Cutscene link", EditorIcons.Film,
            schema.Help["cutsceneLink"], MissionCompleteness.OfCutsceneLink(analysis, line), sections, trigger.Section.Ref);
    }

    /// <summary>The page of a base scene link (section 10.14 of the M6a design): the file and the repeat. It also lists
    /// the lines that add it, remove it and wait for it.</summary>
    private static MissionPageModel BaseSceneLink(PageContext context, MissionLine line)
    {
        var analysis = context.Analysis;
        var index = analysis.Index;
        var schema = MissionSchema.Retail;
        var script = analysis.Script.Path;
        var file = line.Value(0) ?? "";
        var users = index.Lines.Where(l => l.Trigger != null && l.Value(0) is { } value &&
                                           value.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        var fields = new List<PageField> { new("Scene file", file, line.Name?.Value("file")?.Help) };
        var add = users.FirstOrDefault(l => MissionIndex.Is(l.Key, "Act_AddRTC"));
        if (add?.Name?.Value("repeats") is { } repeats && add.ValueOf(repeats) != null)
            fields.Add(ValueField(context, add, repeats, repeats.Label));
        var sections = new List<PageSection>
        {
            new("baseScene", "Base scene", EditorIcons.Image, schema.Help["baseSceneLink"], fields, []),
            Lines("addedBy", "Added by", users.Where(l => MissionIndex.Is(l.Key, "Act_AddRTC")), script),
            Lines("removedBy", "Removed by", users.Where(l => MissionIndex.Is(l.Key, "Act_RemoveRTC")), script),
            Lines("waitedForBy", "Waited for by", users.Where(l => MissionIndex.Is(l.Key, "Cnd_RTCDone")), script),
        };
        return new MissionPageModel(MissionFiles.Display(file), "Base scene link", EditorIcons.Image,
            schema.Help["baseSceneLink"], MissionCompleteness.OfBaseSceneLink(analysis, line), sections, line.Section.Ref);
    }
}

/// <summary>Helpers of the lines of the page models.</summary>
internal static class MissionLineKinds
{
    /// <summary>The kind of the name of a line: a condition, an action or an order. A line without a schema name counts
    /// by its prefix.</summary>
    public static MissionNameKind? Kind(this MissionLine line) => line.Name?.Kind ??
        (TriggerRules.IsCondition(line.Key) ? MissionNameKind.Condition
            : TriggerRules.IsAction(line.Key) ? MissionNameKind.Action
            : null);
}
