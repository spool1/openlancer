using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions.Run;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>One row of the Missions browser (query <c>ListScripts</c>).</summary>
/// <param name="Ref">The <c>msn:</c> reference of the script.</param>
/// <param name="Slot">The story slot that loads the script, for example <c>M02</c>, or null.</param>
/// <param name="ModId">The last mod that changes or replaces the script, or null.</param>
/// <param name="Errors">The errors that show: the errors of the active mod.</param>
/// <param name="Warnings">The warnings that show. A problem of the base game does not count.</param>
public sealed record MissionScriptRow(string Ref, string? Slot, MissionScriptState State, string? ModId, int Triggers,
    int Errors, int Warnings, long Version);

/// <summary>An act of a mission: its triggers in file order.</summary>
public sealed record MissionActView(int Number, string? System, IReadOnlyList<string> Triggers);

/// <summary>A prefab instance of a script: its <c>inst:</c> reference, its prefab and its core trigger.</summary>
public sealed record MissionInstanceRow(string Ref, string Prefab, string Name, string? Core);

/// <summary>A ship, a formation, a solar, a loot or an NPC of a script, with its system or null.</summary>
public sealed record MissionCastRow(string Ref, MissionSectionKind Kind, string? System);

/// <summary>The summary of a script (query <c>GetMission</c>).</summary>
public sealed record MissionOverview(string Ref, long Version)
{
    public string? Slot { get; init; }

    public MissionScriptState State { get; init; }

    /// <summary>Why the editor cannot change the script, or null.</summary>
    public string? ReadOnlyReason { get; init; }

    /// <summary>The triggers that start with the mission.</summary>
    public IReadOnlyList<string> StartTriggers { get; init; } = [];

    public IReadOnlyList<MissionActView> Acts { get; init; } = [];

    /// <summary>The triggers that no start trigger reaches.</summary>
    public IReadOnlyList<string> NotStarted { get; init; } = [];

    public IReadOnlyList<MissionInstanceRow> Instances { get; init; } = [];

    public IReadOnlyList<MissionCastRow> Cast { get; init; } = [];

    /// <summary>The problems that show (see <see cref="MissionQueries.GetProblems"/>).</summary>
    public IReadOnlyList<Problem> Problems { get; init; } = [];
}

/// <summary>A name in a line and how it resolves.</summary>
/// <param name="Value">The index of the value in the line.</param>
/// <param name="Ref">The reference of the item of the script that the name resolves to, or null.</param>
public sealed record MissionNameView(int Value, string Name, MissionValueKind Kind, MissionReferenceState State, string? Ref);

/// <summary>A line with its row, its key, its plain name, its named values, its names and its problems.</summary>
/// <param name="Ref">The <c>line:</c> reference.</param>
/// <param name="Plain">The plain name of the key, or null for a key that the schema does not know.</param>
/// <param name="Values">The text of each named value that the line has.</param>
/// <param name="Texts">The value texts by position, as the game reads them.</param>
public sealed record MissionLineView(string Ref, int Row, string Key, string? Plain, IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Texts, IReadOnlyList<MissionNameView> Names, IReadOnlyList<Problem> Problems);

/// <summary>A link between two triggers: the line and the trigger at the other end.</summary>
/// <param name="Line">The <c>line:</c> reference of the line that makes the link.</param>
/// <param name="Trigger">The reference of the trigger at the other end, or its name when the script has no such
/// trigger.</param>
public sealed record MissionLinkView(MissionLinkKind Kind, string Line, string Trigger);

/// <summary>A trigger (query <c>GetTrigger</c>).</summary>
public sealed record MissionTriggerView(string Ref, long Version, string Nickname)
{
    public bool StartsWithMission { get; init; }

    public bool Repeats { get; init; }

    /// <summary>The system hint of the trigger, or null. The game does not read it.</summary>
    public string? System { get; init; }

    /// <summary>The sentence of the condition that the game uses, or null without a condition.</summary>
    public string? Event { get; init; }

    /// <summary>The <c>inst:</c> reference of the prefab instance that has the trigger, or null.</summary>
    public string? Instance { get; init; }

    public IReadOnlyList<MissionLinkView> LinksIn { get; init; } = [];

    public IReadOnlyList<MissionLinkView> LinksOut { get; init; } = [];

    /// <summary>Each line of the section, the lines before the nickname too.</summary>
    public IReadOnlyList<MissionLineView> Lines { get; init; } = [];
}

/// <summary>An item of a script, or the members of a label (query <c>GetItem</c>).</summary>
/// <param name="Users">The <c>line:</c> references of the lines that name the item.</param>
public sealed record MissionItemView(string Ref, long Version, IReadOnlyList<MissionLineView> Lines,
    IReadOnlyList<string> Users)
{
    /// <summary>The section type, or null for a label.</summary>
    public string? Type { get; init; }

    /// <summary>The references of the sections of a label.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];
}

/// <summary>A line that names an item (query <c>FindReferences</c>).</summary>
/// <param name="Line">The <c>line:</c> reference.</param>
/// <param name="Value">The index of the value in the line.</param>
public sealed record MissionReferenceView(string Line, string Key, int Value);

/// <summary>A value of a name of the palette.</summary>
/// <param name="Words">The words of a word set of the value, or empty.</param>
/// <param name="Default">The text that a command writes in a gap, or null.</param>
public sealed record MissionValueView(string Name, string Label, MissionValueKind Kind, int Index, int Width, bool Optional,
    bool Rest, string? Default, IReadOnlyList<string> Words, string? Help);

/// <summary>A condition, an action or an order of the palette (query <c>ListPalette</c>).</summary>
/// <param name="BaseCount">The number of lines of the name in the scripts of the base game without mods.</param>
/// <param name="NoRetailExample">True when no retail script has the name (rule M28).</param>
/// <param name="Checked">False when no reader checked the meaning of the name.</param>
public sealed record MissionPaletteName(string Name, string Plain, MissionNameKind Kind, string Group,
    IReadOnlyList<MissionValueView> Values, EngineMark Mark, bool NoRetailExample, int BaseCount, bool Checked);

/// <summary>A field of a prefab: what <c>PlacePrefabInstance</c> and <c>SetPrefabField</c> take.</summary>
/// <param name="List">True for a list field.</param>
public sealed record MissionPrefabFieldView(string Id, string Label, string Help, bool List)
{
    /// <summary>The kind of the field in the template file, for example <c>condition</c> or <c>formation</c>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>True when each instance sets the value itself. It is never an override.</summary>
    public bool PerInstance { get; init; }

    /// <summary>The default rows in their text form.</summary>
    public IReadOnlyList<string> Default { get; init; } = [];

    /// <summary>True when a value field can have no value.</summary>
    public bool Optional { get; init; }

    /// <summary>The columns of a list field, in the order of their values in a row. Other fields have none.</summary>
    public IReadOnlyList<MissionPrefabColumnView> Columns { get; init; } = [];

    /// <summary>The keys that a condition field or a lines field takes. Empty takes each condition or each
    /// action.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];
}

/// <summary>A column of a list field of a prefab: its id in a row, its plain label and its value kind, for example
/// <c>position</c>.</summary>
public sealed record MissionPrefabColumnView(string Id, string Label, string Kind);

/// <summary>A prefab of the library (query <c>ListPrefabs</c>): a built-in prefab or an asset of a mod (section 5.2 of
/// the M6b design).</summary>
/// <param name="Ref">The <c>prefab:builtin:</c> or <c>prefab:mod:</c> reference.</param>
/// <param name="Roles">The roles of the template sections.</param>
public sealed record MissionPrefabView(string Ref, string Name, MissionPrefabKind Kind, string Help,
    IReadOnlyList<MissionPrefabFieldView> Fields, IReadOnlyList<string> Roles, IReadOnlyList<string> Steps)
{
    /// <summary>The mod of an asset, or null for a built-in prefab.</summary>
    public string? ModId { get; init; }

    /// <summary>True for a prefab that cannot change: a built-in prefab or an asset of a lower mod.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The ids of the ports.</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];

    /// <summary>The problems of the file of an asset. An asset with a problem does not place.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>A step of the completeness panel of an instance.</summary>
public sealed record MissionStepView(string Name, CompletenessState State, string Reason);

/// <summary>A prefab instance with its fields, its members and its steps (query <c>GetPrefabInstance</c>). A matched
/// instance has no overrides.</summary>
/// <param name="Fields">The value texts of each field.</param>
/// <param name="Members">The trigger references of each role.</param>
public sealed record MissionInstanceView(string Ref, long Version, string Prefab, string Name,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields, IReadOnlyDictionary<string, IReadOnlyList<string>> Members,
    IReadOnlyList<MissionStepView> Steps)
{
    public string? Core { get; init; }

    /// <summary>True for a linked instance with a record (section 5.1 of the M6b design).</summary>
    public bool Linked { get; init; }

    /// <summary>The fields of a linked instance that override the prefab.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>The roles of the removed members of a linked instance (decision D676 c).</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];
}

/// <summary>
/// The read queries of the mission layer (section 4.12 of the M6b design). Each result names items with the reference
/// strings of section 4.0.7, so a read feeds a command. The query tools of MCP1 call them. They run on the thread of the
/// session, as the commands do.
/// </summary>
/// <remarks>A query that does not resolve its reference throws an <see cref="EditException"/> with
/// <see cref="EditErrorKind.NotFound"/>, as a command does.</remarks>
public sealed class MissionQueries
{
    private readonly EditSession session;
    private readonly MissionEditor editor;
    private Dictionary<string, int>? baseCounts;

    internal MissionQueries(EditSession session, MissionEditor editor)
    {
        this.session = session;
        this.editor = editor;
    }

    /// <summary>The rows of the Missions browser: each script of W with its slot, state, counts and version.</summary>
    public IReadOnlyList<MissionScriptRow> ListScripts() =>
    [
        .. editor.ScriptPaths().Select(path =>
        {
            var model = editor.Model(path);
            var shown = Shown(model.Problems).ToList();
            var info = model.Analysis.Script;
            return new MissionScriptRow(new ScriptRef(path).ToString(), info.Slot?.Slot, info.State, info.ModId,
                model.Index.Triggers.Count, shown.Count(p => p.Severity == DiagnosticSeverity.Error),
                shown.Count(p => p.Severity == DiagnosticSeverity.Warning), model.Version);
        }),
    ];

    /// <summary>The summary of a script: start triggers, acts, prefab instances, cast and problems.</summary>
    public MissionOverview GetMission(ScriptRef script)
    {
        var model = editor.Model(script.Path);
        var path = model.Script;
        var index = model.Index;
        var cast = model.Document.Sections
            .Where(s => s.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation or MissionSectionKind.Solar or
                MissionSectionKind.Loot or MissionSectionKind.Npc)
            .Select(s => new MissionCastRow(Ref(s.Ref), s.Kind, index.SystemOf(s))).ToList();
        return new MissionOverview(new ScriptRef(path).ToString(), model.Version)
        {
            Slot = model.Analysis.Script.Slot?.Slot,
            State = model.Analysis.Script.State,
            ReadOnlyReason = model.ReadOnlyReason,
            StartTriggers = [.. index.StartTriggers.Select(Ref)],
            Acts = [.. (model.Acts?.Acts ?? []).Select(a => new MissionActView(a.Number, a.System, [.. a.Triggers.Select(Ref)]))],
            NotStarted = [.. (model.Acts?.NotStarted ?? []).Select(Ref)],
            Instances = [.. (model.Prefabs?.Instances ?? []).Select(i => new MissionInstanceRow(InstanceRef(path, i),
                i.Definition.Id, i.Name, i.Core == null ? null : Ref(i.Core)))],
            Cast = cast,
            Problems = GetProblems(script),
        };
    }

    /// <summary>A trigger: the nickname, the state lines, the event sentence, the links in and out, and each line with
    /// its row, key, plain name, named values, names and problems.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the script has no such trigger.</exception>
    public MissionTriggerView GetTrigger(TriggerRef reference)
    {
        var model = editor.Model(reference.Script);
        var trigger = model.Index.Trigger(reference.Name) ??
                      throw new EditException(MissionErrors.TriggerNotFoundError(model.Script, reference.Name));
        var index = model.Index;
        return new MissionTriggerView(Ref(trigger), model.Version, trigger.Nickname)
        {
            StartsWithMission = trigger.StartsActive,
            Repeats = trigger.Repeats,
            System = trigger.SystemHint,
            Event = trigger.Condition?.Sentence(),
            Instance = model.Prefabs?.Of(trigger) is { } instance ? InstanceRef(model.Script, instance) : null,
            LinksIn = [.. index.LinksTo(trigger).Select(l => new MissionLinkView(l.Kind, LineRef(l.Line), Ref(l.Source)))],
            LinksOut = [.. index.Links.Where(l => l.Source == trigger)
                .Select(l => new MissionLinkView(l.Kind, LineRef(l.Line), l.Target != null ? Ref(l.Target) : l.TargetName))],
            Lines = Lines(model, trigger.Section),
        };
    }

    /// <summary>The lines of an item with their named values, its names and the lines that name it.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the file has no such item.</exception>
    public MissionItemView GetItem(ItemRef reference)
    {
        if (MissionIndex.Is(reference.Section.Type, "Trigger") && reference.Section.Key is { } nickname)
        {
            var trigger = GetTrigger(new TriggerRef(reference.Script, nickname));
            return new MissionItemView(trigger.Ref, trigger.Version, trigger.Lines,
                [.. trigger.LinksIn.Select(l => l.Line)]) { Type = "Trigger" };
        }
        var path = InstallPaths.TryParse(reference.Script) ?? reference.Script;
        var users = FindReferences(reference.ToString()).Select(r => r.Line).ToList();
        if (MissionIndex.Is(reference.Section.Type, "NPCShipArch"))
        {
            // A ship build is in the ship build file, which has no index. Its lines come from W.
            var document = session.OpenDocument(path) is { } file && file.IndexOf(reference.Section) is var i and >= 0
                ? (File: file, Index: i)
                : throw new EditException(NotFound(path, reference.Section));
            var ships = new SectionRef(document.File.Path, reference.Section);
            return new MissionItemView(Ref(ships), editor.Version(document.File.Path),
                PlainLines(ships, document.File.Working[document.Index], MissionSchema.Retail.ShipBuild), users)
            {
                Type = reference.Section.Type,
            };
        }
        var model = editor.Model(path);
        var section = model.Document.Find(new SectionRef(model.Script, reference.Section)) ??
                      throw new EditException(NotFound(model.Script, reference.Section));
        return new MissionItemView(Ref(section.Ref), model.Version, Lines(model, section), users) { Type = section.Type };
    }

    private static EditError NotFound(string script, SectionIdentity section) =>
        MissionErrors.ItemNotFoundError(script, MissionNames.KindWord(section.Type), section.Key ?? section.Type);

    /// <summary>The members of a label and the lines that name it.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the script has no such label.</exception>
    public MissionItemView GetItem(LabelRef reference)
    {
        var model = editor.Model(reference.Script);
        if (!model.Index.Labels.TryGetValue(reference.Label, out var members))
            throw new EditException(new EditError(EditErrorKind.NotFound, MissionErrors.ItemNotFound,
                $"Script {model.Script} has no label '{reference.Label}'. Pick a label of the script."));
        return new MissionItemView(new LabelRef(model.Script, reference.Label).ToString(), model.Version, [],
            [.. FindReferences(reference.ToString()).Select(r => r.Line)])
        {
            Members = [.. members.Select(m => Ref(m.Ref))],
        };
    }

    /// <summary>
    /// Each line that names an item, in all scripts. A trigger, an item or a label of a script gives the lines of its
    /// script. A ship build gives the lines of each script. A name of the game data (<c>obj:</c>, <c>system:</c>,
    /// <c>base:</c> or <c>list:</c>) gives the lines of each script that name it.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the reference names no item that a
    /// mission line can name. <see cref="EditErrorKind.NotFound"/>: the script does not exist.</exception>
    public IReadOnlyList<MissionReferenceView> FindReferences(string reference)
    {
        var parsed = ItemRefs.Parse(reference);
        switch (parsed.Kind)
        {
            case ItemRefKind.Trigger or ItemRefKind.Section when parsed.Path is { } path:
            {
                var item = ItemRef.Parse(reference);
                var kinds = MissionLinks.KindsOf(item.Section.Type);
                if (MissionIndex.Is(item.Section.Type, "NPCShipArch") && item.Section.Key is { } build)
                    return World(kinds, build);
                var model = editor.Model(path);
                return Script(model, kinds, item.Section.Key ?? "");
            }
            case ItemRefKind.Label when parsed.Path is { } path:
                return Script(editor.Model(path), [MissionValueKind.Object], parsed.Name);
            case ItemRefKind.Object:
                return World(MissionReferences.ObjectKinds, parsed.Name);
            case ItemRefKind.System:
                return World([MissionValueKind.System], parsed.Name);
            case ItemRefKind.Base:
                return World([MissionValueKind.Base], parsed.Name);
            case ItemRefKind.ListEntry when parsed.Scope is { } list && EditSession.MissionKindOfList(list) is { } kind:
                return World([kind], parsed.Name);
            default:
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
                    $"{reference} names no item that a mission line can name. Use a trg:, sec:, label:, obj:, system:, " +
                    "base: or list: reference."));
        }
    }

    /// <summary>The conditions, actions and orders with their groups, plain names, values, word sets, OpenLancer marks,
    /// "no retail example" marks and counts in the base game.</summary>
    public IReadOnlyList<MissionPaletteName> ListPalette()
    {
        var schema = MissionSchema.Retail;
        var counts = BaseCounts();
        var coverage = editor.Coverage;
        return
        [
            .. schema.Conditions.Concat(schema.Actions).Concat(schema.Directives).Select(n => new MissionPaletteName(n.Name,
                n.Plain, n.Kind, n.Group, [.. n.Values.Select(v => ValueView(v, schema))], coverage.Of(n.Name),
                n.NoRetailExample, counts.GetValueOrDefault(n.Name), n.Checked)),
        ];
    }

    /// <summary>The prefabs of the library: the built-in prefabs, then the assets of the active mod and of the lower
    /// mods, with their fields, roles, ports and steps (section 5.2 of the M6b design).</summary>
    public IReadOnlyList<MissionPrefabView> ListPrefabs() =>
    [
        .. editor.Prefabs().Select(asset =>
        {
            var template = asset.Template;
            var definition = asset.Ref.Builtin ? MissionPrefabs.Retail.Definition(asset.Ref.Id) : null;
            return new MissionPrefabView(asset.Ref.ToString(), asset.Name, template?.Kind ?? MissionPrefabKind.Beat,
                template?.HelpText.WhatItIs ?? "",
                [.. (template?.Fields ?? []).Select(f => new MissionPrefabFieldView(f.Id, f.Label, f.Help, f.IsList)
                {
                    Kind = f.KindText, PerInstance = f.PerInstance, Default = [.. f.Default.Select(r => r.Format())],
                    Optional = f.Optional,
                    Columns = [.. f.Columns.Select(c => new MissionPrefabColumnView(c.Id, c.Label, Prefabs.PrefabFieldDef.KindName(c.Kind)))],
                    Keys = f.Keys,
                })],
                [.. (template?.Sections ?? []).Select(s => s.Role)], [.. (definition?.Steps ?? []).Select(s => s.Name)])
            {
                ModId = asset.ModId,
                ReadOnly = !asset.Active,
                Ports = [.. (template?.Ports ?? []).Select(p => p.Id)],
                Problems = [.. asset.Problems.Select(p => p.ToString())],
            };
        }),
    ];

    /// <summary>A prefab instance: the values of its fields, its members and its completeness steps.</summary>
    /// <param name="reference">The <c>inst:</c> reference: the script, the prefab id and the core trigger.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the script has no such
    /// instance.</exception>
    public MissionInstanceView GetPrefabInstance(string reference)
    {
        var parsed = ItemRefs.Parse(reference);
        if (parsed is not { Kind: ItemRefKind.Instance, Path: { } path })
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
                $"{reference} is not a prefab instance. Use an inst: reference, for example " +
                @"inst:DATA\MISSIONS\M02\m02.ini#spawnWave/ol_wave."));
        var model = editor.Model(path);
        var instance = model.Prefabs?.Instances.FirstOrDefault(i =>
                           string.Equals(i.Id, parsed.Name, StringComparison.OrdinalIgnoreCase)) ??
                       throw new EditException(new EditError(EditErrorKind.NotFound, MissionErrors.ItemNotFound,
                           $"Script {model.Script} has no prefab instance '{parsed.Name}'. Read the script, then pick an " +
                           "instance."));
        var steps = MissionCompleteness.OfInstance(model.Analysis, instance).Steps
            .Select(s => new MissionStepView(s.Name, s.State, s.Reason)).ToList();
        if (instance.Linked is { } linked)
        {
            return new MissionInstanceView(InstanceRef(model.Script, instance), model.Version, "prefab:" + linked.Record.Prefab,
                instance.Name,
                linked.Fields.ToDictionary(f => f.Field.Id, f => (IReadOnlyList<string>)[.. f.Rows.Select(r => r.Format())]),
                linked.Sections.ToDictionary(m => m.Key, m => (IReadOnlyList<string>)[Ref(m.Value.Ref)]),
                steps)
            {
                Core = instance.Core == null ? null : Ref(instance.Core),
                Linked = true,
                Overrides = [.. linked.Fields.Where(f => f.Override).Select(f => f.Field.Id)],
                Removed = linked.Record.Removed,
            };
        }
        return new MissionInstanceView(InstanceRef(model.Script, instance), model.Version, instance.Definition.Id, instance.Name,
            instance.Definition.Fields.ToDictionary(f => f.Id, f => (IReadOnlyList<string>)[.. instance.Values(f).Select(v => v.Text)]),
            instance.Members.ToDictionary(m => m.Key, m => (IReadOnlyList<string>)[.. m.Value.Select(Ref)]),
            steps)
        {
            Core = instance.Core == null ? null : Ref(instance.Core),
        };
    }

    /// <summary>
    /// The dry run query (section 7.2 of the M6c design): folds the inputs of a request over the model of a script now
    /// and gives the steps with their changes and the full state after the last step. The query keeps no state: the
    /// caller gives all inputs each time. It changes nothing.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the script, a trigger or a system does not
    /// resolve. <see cref="EditErrorKind.InvalidArgument"/>: a line matches two waiting lines or no condition of the
    /// script. <see cref="EditErrorKind.Limit"/>: the request has too many inputs or heartbeats.</exception>
    public DryRunView DryRun(DryRunRequest request, DryRunLimits? limits = null)
    {
        RunJson.CheckLimits(request.Inputs, limits ?? DryRunLimits.Default);
        var model = editor.Model(request.Script);
        var state = Run.DryRun.Fold(model.Index, request.Inputs, request.Timers, strict: true);
        return RunJson.View(model.Script, model.Version, state, request.SinceStep);
    }

    /// <summary>
    /// The start plan query (section 7.2 of the M6c design): the plan of a start of a script now, as the Play start page
    /// shows it. A plan that cannot start is data, with its reason. It changes nothing.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the script or the trigger of the start does
    /// not resolve.</exception>
    public StartPlanView StartPlan(StartPlanRequest request)
    {
        var model = editor.Model(request.Script);
        if (request.Start.Trigger is { } name && model.Index.Trigger(name) is not { IsDuplicate: false })
            throw new EditException(MissionErrors.TriggerNotFoundError(model.Script, name) with { Field = "start" });
        var plan = Run.StartPlan.Make(model.Index, model.Analysis.Script.Slot, request.Start, request.Place,
            request.PlaceBase);
        return RunJson.View(plan, model.Version);
    }

    /// <summary>The problems of a script that show, with their origin, rule and reference. A problem of the base game
    /// shows only on request, as in the Console (decision D551).</summary>
    /// <param name="baseGame">True to give the problems of the base game too.</param>
    public IReadOnlyList<Problem> GetProblems(ScriptRef script, bool baseGame = false)
    {
        var model = editor.Model(script.Path);
        return [.. (baseGame ? model.Problems : Shown(model.Problems)).Select(p => ProblemOf(session, model.Script, p))];
    }

    /// <summary>The problem of a diagnostic of a script, with the reference of its line or its item.</summary>
    internal static Problem ProblemOf(EditSession session, string script, Diagnostic problem)
    {
        string? reference = null;
        if (problem.Target is { } target)
            reference = problem.EntryIndex >= 0
                ? new RowRef(new ItemRef(target.Path, target.Id), problem.EntryIndex).Reference
                : ItemRefs.Canonical(session, target);
        return Problem.From(problem, reference ?? new ScriptRef(script).ToString());
    }

    private static IEnumerable<Diagnostic> Shown(IEnumerable<Diagnostic> problems) =>
        problems.Where(p => p.Origin?.Kind != OriginKind.BaseGame);

    private IReadOnlyList<MissionReferenceView> Script(MissionModel model, IEnumerable<MissionValueKind> kinds, string name)
    {
        var seen = new HashSet<(MissionLine, int)>();
        return
        [
            .. kinds.SelectMany(k => model.Index.ReferencesTo(k, name))
                .Where(r => seen.Add((r.Line, r.ValueIndex)))
                .OrderBy(r => r.Line.Section.Index).ThenBy(r => r.Line.Entry)
                .Select(r => new MissionReferenceView(LineRef(r.Line), r.Line.Key, r.ValueIndex)),
        ];
    }

    private IReadOnlyList<MissionReferenceView> World(IReadOnlyCollection<MissionValueKind> kinds, string name) =>
    [
        .. editor.References.Uses(kinds, name).Select(u =>
            new MissionReferenceView(new RowRef(new ItemRef(u.Section.Path, u.Section.Id), u.Row).Reference, u.Key, u.Value)),
    ];

    private IReadOnlyList<MissionLineView> Lines(MissionModel model, MissionSection section)
    {
        var problems = model.Problems.Where(p => p.Target == section.Ref && p.EntryIndex >= 0).ToList();
        var lines = model.Index.LinesOf(section).ToDictionary(l => l.Entry);
        var result = new List<MissionLineView>();
        for (var row = 0; row < section.Section.Count; row++)
        {
            var entry = section.Section[row];
            lines.TryGetValue(row, out var line);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var spec in line?.Specs ?? [])
            {
                if (line!.ValueOf(spec) is { } text)
                    values[spec.Name] = text;
            }
            result.Add(new MissionLineView(new RowRef(new ItemRef(section.Ref.Path, section.Ref.Id), row).Reference, row,
                entry.Name, line?.Name?.Plain ?? line?.KeySpec?.Label, values, MissionValues.TextsOf(entry),
                [.. (line?.References ?? []).Select(r => new MissionNameView(r.ValueIndex, r.Name, r.Kind, r.State,
                    r.Section == null ? null : Ref(r.Section.Ref)))],
                [.. problems.Where(p => p.EntryIndex == row).Select(p => ProblemOf(session, model.Script, p))]));
        }
        return result;
    }

    /// <summary>The lines of a section outside the scripts, with the named values of the keys of its type.</summary>
    private static IReadOnlyList<MissionLineView> PlainLines(SectionRef reference, LibreLancer.Data.Ini.Section section,
        MissionSectionSpec spec) =>
    [
        .. section.Select((entry, row) =>
        {
            var key = spec.Key(entry.Name);
            var texts = MissionValues.TextsOf(entry);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in key?.ValueSpecs ?? [])
            {
                var parts = texts.Skip(value.Index).Take(Math.Min(value.End, texts.Count) - value.Index)
                    .Where(t => t.Trim().Length > 0).ToList();
                if (parts.Count > 0)
                    values[value.Name] = string.Join(", ", parts);
            }
            return new MissionLineView(new RowRef(new ItemRef(reference.Path, reference.Id), row).Reference, row, entry.Name,
                key?.Label, values, texts, [], []);
        }),
    ];

    private Dictionary<string, int> BaseCounts()
    {
        if (baseCounts != null)
            return baseCounts;
        baseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in editor.ScriptPaths())
        {
            if (session.OpenDocument(path) is not { IsWholeFile: false } document)
                continue;
            foreach (var entry in document.Base.SelectMany(s => s))
                baseCounts[entry.Name] = baseCounts.GetValueOrDefault(entry.Name) + 1;
        }
        return baseCounts;
    }

    private static MissionValueView ValueView(MissionValueSpec spec, MissionSchema schema) =>
        new(spec.Name, spec.Label, spec.Kind, spec.Index, spec.Width, spec.Optional, spec.Rest, spec.Default,
            [.. new[] { spec.Tokens, spec.Also }.OfType<string>()
                .SelectMany(id => schema.Tokens.TryGetValue(id, out var set) ? set.Values : [])],
            spec.Help);

    private string Ref(SectionRef section) => ItemRefs.Canonical(session, section);

    private string Ref(MissionTrigger trigger) => Ref(trigger.Section.Ref);

    private static string LineRef(MissionLine line) =>
        new RowRef(new ItemRef(line.Section.Ref.Path, line.Section.Ref.Id), line.Entry).Reference;

    private static string InstanceRef(string script, MissionPrefabInstance instance) =>
        new Editing.InstanceRef(script, instance.Id).ToString();
}
