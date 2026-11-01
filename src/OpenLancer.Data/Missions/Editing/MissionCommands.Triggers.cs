using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>Adds a trigger at the end of a script (section 4.9 of the M6b design). Without a nickname, the layer makes
/// one (decision D683).</summary>
public sealed record AddTrigger(ScriptRef Script) : MissionCommand
{
    public string? Nickname { get; init; }

    /// <summary>The condition, or null for a trigger without a condition (rule M01).</summary>
    public LineSpec? Condition { get; init; }

    public IReadOnlyList<LineSpec> Actions { get; init; } = [];

    /// <summary>True writes <c>InitState = ACTIVE</c>: the trigger starts with the mission.</summary>
    public bool StartsWithMission { get; init; }

    /// <summary>True writes <c>repeatable = true</c>.</summary>
    public bool Repeats { get; init; }

    /// <summary>The system hint of the trigger, or null. The game does not read it (TR3).</summary>
    public string? System { get; init; }

    /// <summary>The place of the node in the graph, or null for the automatic layout (section 6.8 of the M6b
    /// design).</summary>
    public GraphPoint? Place { get; init; }

    /// <summary>The act that the trigger joins, or null. The first act edit writes all proposed acts (decision
    /// D679).</summary>
    public ActRef? Act { get; init; }

    protected override string Describe() => $"Add trigger {Nickname}".TrimEnd();

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Script);
        var name = d.NewName(document, "Trigger", Nickname);
        var entries = new List<Entry>();
        void Add(string key, IReadOnlyList<string> texts) => entries.Add(d.NewEntry(document, null, key, texts, 0));
        Add("nickname", [name]);
        if (System != null)
        {
            MissionValues.CheckText(System.Trim());
            Add("system", [System.Trim()]);
        }
        if (StartsWithMission)
            Add("InitState", ["ACTIVE"]);
        if (Condition != null)
        {
            var condition = d.Build("Trigger", LinePlace.Condition, Condition);
            Add(condition.Key, condition.Texts);
        }
        if (Repeats)
            Add("repeatable", ["true"]);
        foreach (var action in Actions)
        {
            var line = d.Build("Trigger", LinePlace.Action, action);
            Add(line.Key, line.Texts);
        }
        d.Insert(document, document.Working.Count, MissionDraft.NewSection("Trigger", entries));
        var trigger = new TriggerRef(document.Path, name);
        d.Adds.Add(trigger.ToString());
        d.Select = trigger.ToString();
        d.Inverse(GraphRecords(d, document, name) is { } acts ? new CommandGroup([new RemoveTrigger(trigger), acts])
            : new RemoveTrigger(trigger));
    }

    /// <summary>Writes the node place and the act of the new trigger into the graph file. Returns the inverse of the
    /// acts that the first act edit wrote, or null.</summary>
    private MissionCommand? GraphRecords(MissionDraft d, EditDocument document, string name)
    {
        if (Place == null && Act == null)
            return null;
        var graph = d.Graph(document);
        MissionCommand? inverse = null;
        if (Place is { } place)
            graph = graph with { Nodes = [.. graph.Nodes, new GraphNodePlace(name, (float)Math.Round(place.X), (float)Math.Round(place.Y))] };
        if (Act is { } act)
        {
            // The first act edit proposes the acts with the new trigger, so a trigger that the mission reaches can be in a
            // proposed act already. A trigger is in one act, so it leaves that act first.
            var acts = d.ActsForEdit(document, graph, out var written)
                .Select(a => a with { Members = a.Members.Where(m => !m.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() })
                .ToList();
            var at = d.ActIndex(document, acts, act);
            acts[at] = acts[at] with { Members = [.. acts[at].Members, name] };
            graph = graph with { Acts = acts };
            if (written)
                inverse = new CommandGroup(acts.Select(a => (MissionCommand)new RemoveAct(new ActRef(document.Path, a.Nickname))).ToList());
        }
        d.SetGraph(document, graph);
        return inverse;
    }
}

/// <summary>A point of the canvas of the mission graph, in points.</summary>
public sealed record GraphPoint(double X, double Y);

/// <summary>Removes a trigger. Each line that names it stays and shows "Missing", or goes with
/// <see cref="ReferencesMode.Remove"/> (section 4.10 of the M6b design).</summary>
public sealed record RemoveTrigger(TriggerRef Trigger) : MissionCommand
{
    public ReferencesMode References { get; init; } = ReferencesMode.Keep;

    protected override string Describe() => $"Delete trigger {Trigger.Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index, members: true);
        var name = TriggerRules.Nickname(document.Working[index])!;
        var reference = d.RefOf(document, index);
        var inverse = new List<MissionCommand>();
        // A member of a linked prefab instance: the compile of the instance handles the lines of its other members.
        var lines = d.OutsideLines(document, index, MissionLinks.Naming(d.Schema, document.Working, [MissionValueKind.Trigger], name)
            .Where(l => l.Section != index).Select(l => (l.Section, l.Row)).Distinct());
        if (References == ReferencesMode.Remove)
            d.RemoveLines(document, lines, inverse);
        else
            d.MarkMissing(document, lines, name, "The game ignores this line, because the script has no such trigger.");
        var position = index;
        var live = MissionDraft.TriggerNames(document).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = d.Remove(document, index);
        inverse.Insert(0, new RestoreSection(new ScriptRef(document.Path), position, MissionDraft.RawSection(removed)));
        // The node place and the act place of the trigger go with it (section 4.9 of the M6b design). The inverse puts
        // the node back at its place in the file, and gives the act its members that the script has.
        var graph = d.Graph(document);
        if (graph.Place(name) is { } place)
            inverse.Add(new MoveNodes(new ScriptRef(document.Path), [new NodePlace(place.Trigger, place.X, place.Y)
            {
                Index = graph.Nodes.ToList().IndexOf(place),
            }]));
        if (graph.ActOf(name) is { } act)
            inverse.Add(new SetActMembers(new ActRef(document.Path, act.Nickname), act.Members.Where(live.Contains).ToList()));
        if (graph.Place(name) != null || graph.ActOf(name) != null)
            d.SetGraph(document, graph);
        d.Deletes.Add(new ResultDelete(reference, OpenLancer.Data.Editing.Scene.DeleteReason.Target, null));
        d.Inverse(inverse.Count == 1 ? inverse[0] : new CommandGroup(inverse));
    }
}

/// <summary>Gives a trigger a new nickname. With <see cref="ChangeReferences"/>, true by default, each line of the script
/// that names the trigger changes too (section 4.10 of the M6b design).</summary>
public sealed record RenameTrigger(TriggerRef Trigger, string NewName) : MissionCommand
{
    /// <inheritdoc cref="RenameItem.ChangeReferences"/>
    [System.ComponentModel.DefaultValue(true)]
    public bool ChangeReferences { get; init; } = true;

    protected override string Describe() => $"Rename {Trigger.Name} to {NewName}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index);
        var section = document.Working[index];
        var old = TriggerRules.Nickname(section)!;
        var name = NewName.Trim();
        if (string.Equals(old, name, StringComparison.Ordinal))
            return;
        var baseName = document.BaseSection(document.IdentityAt(index)) is { } b ? TriggerRules.Nickname(b) : null;
        // A rename back to the name of B, and an inverse, skip the form rule (section 4.3 of the M6b design). An inverse
        // gives the name back without a change of the lines, as RenameItem does.
        d.CheckRename(document, index, "Trigger", name, skipForm: !ChangeReferences ||
                                                                 string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase));
        var before = d.RefOf(document, index);
        var inverse = new List<MissionCommand>();
        var lines = MissionLinks.Naming(d.Schema, document.Working, [MissionValueKind.Trigger], old);
        if (ChangeReferences)
        {
            // The lines of other sections first, so the index of the trigger stays valid.
            foreach (var group in lines.GroupBy(l => l.Section).Where(g => g.Key != index))
                d.ChangeValues(document, group.Key, group.Select(l => (l.Row, l.Value)).ToList(), name, inverse);
        }
        else
            d.MarkMissing(document, lines.Where(l => l.Section != index).Select(l => (l.Section, l.Row)).Distinct().ToList(),
                old, "The game ignores this line, because the script has no trigger with this name now.");
        // The trigger itself: its nickname line and its own lines that name it.
        var entries = section.ToList();
        var nickname = TriggerRules.NicknameLine(entries);
        var own = ChangeReferences ? lines.Where(l => l.Section == index).ToList() : [];
        var changed = new List<Entry>();
        for (var r = 0; r < entries.Count; r++)
        {
            var values = own.Where(l => l.Row == r).Select(l => l.Value).ToHashSet();
            if (r == nickname)
                values.Add(0);
            if (values.Count == 0)
            {
                changed.Add(entries[r]);
                continue;
            }
            var texts = MissionValues.TextsOf(entries[r]).ToList();
            foreach (var v in values)
                texts[v] = name;
            changed.Add(d.NewEntry(document, index, entries[r].Name, texts, changed));
        }
        d.SetBody(document, index, changed);
        // The node place, the act place and the records of the graph file follow the trigger, also without a change of
        // the lines that name it. A record of the new name named a trigger that the script did not have. It goes, so
        // one trigger has one node record and one act.
        var graph = d.Graph(document);
        var caseOnly = old.Equals(name, StringComparison.OrdinalIgnoreCase);
        bool Stale(string trigger) => !caseOnly && trigger.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (graph.Place(old) != null || graph.ActOf(old) != null || graph.Place(name) != null || graph.ActOf(name) != null)
        {
            string Renamed(string trigger) => trigger.Equals(old, StringComparison.OrdinalIgnoreCase) ? name : trigger;
            d.SetGraph(document, graph with
            {
                Nodes = graph.Nodes.Where(n => !Stale(n.Trigger)).Select(n => n with { Trigger = Renamed(n.Trigger) }).ToList(),
                Acts = graph.Acts.Select(a => a with { Members = a.Members.Where(m => !Stale(m)).Select(Renamed).ToList() })
                    .ToList(),
            });
        }

        if (ChangeReferences)
            d.RenameInRecords(document, [MissionValueKind.Trigger], old, name);
        if (MissionDraft.IsTemplateDocument(document))
            d.RenameRole(document, old, name);
        var renamed = new TriggerRef(document.Path, name);
        // The inverse gives the own lines that named the trigger their old text first, then the lines of the other
        // sections in reverse order, then the old nickname. So the rename back finds no line that names the new name
        // only because of the rename, and reports no false "Missing" line.
        var back = own.Select(l => l.Row).Distinct().Where(r => r != nickname)
            .Select(row => (MissionCommand)new SetLine(new RowRef(renamed.Item, row) { ExpectKey = entries[row].Name },
                MissionDraft.RawLine(entries[row])))
            .Concat(inverse.AsEnumerable().Reverse())
            .Append(new RenameTrigger(renamed, old) { ChangeReferences = false })
            .ToList();
        d.Renames.Add(new ResultRename(before, d.RefOf(document, index)));
        d.TriggerRenamed(document, old, name);
        d.GoneNames.Add(old);
        d.Select = d.RefOf(document, index);
        d.Inverse(back.Count == 1 ? back[0] : new CommandGroup(back));
    }
}

/// <summary>Copies a trigger to the end of the script with a new nickname. Lines that name the original do not change.
/// A line of the copy that names the original names the copy, as a paste of the trigger does and as Unity duplicates
/// the references inside a copied object.</summary>
public sealed record DuplicateTrigger(TriggerRef Trigger) : MissionCommand
{
    public string? NewName { get; init; }

    /// <summary>The place of the node of the copy in the graph, or null for the automatic layout. The gesture gives a
    /// place beside the original.</summary>
    public GraphPoint? Place { get; init; }

    protected override string Describe() => $"Duplicate trigger {Trigger.Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        // A copy of a member of a linked prefab instance refuses: duplicate the instance (section 4.9 of the M6b design).
        d.RefuseMember(document, index);
        var section = document.Working[index];
        var name = d.NewName(document, "Trigger", NewName);
        var nickname = TriggerRules.NicknameLine(section);
        var self = MissionLinks.Naming(d.Schema, [section], [MissionValueKind.Trigger], TriggerRules.Nickname(section)!)
            .Where(l => l.Row != nickname).ToList();
        var entries = new List<Entry>();
        for (var r = 0; r < section.Count; r++)
        {
            var values = self.Where(l => l.Row == r).Select(l => l.Value).ToList();
            if (r == nickname)
                values.Add(0);
            if (values.Count == 0)
            {
                entries.Add(section[r]);
                continue;
            }
            var texts = MissionValues.TextsOf(section[r]).ToList();
            foreach (var value in values)
                texts[value] = name;
            entries.Add(d.NewEntry(document, null, section[r].Name, texts,
                entries.Count(e => e.Name.Equals(section[r].Name, StringComparison.OrdinalIgnoreCase))));
        }
        d.Insert(document, document.Working.Count, MissionDraft.NewSection(section.Name, entries));
        if (Place is { } place)
        {
            var graph = d.Graph(document);
            d.SetGraph(document, graph with
            {
                Nodes = [.. graph.Nodes, new GraphNodePlace(name, (float)Math.Round(place.X), (float)Math.Round(place.Y))],
            });
        }

        var copy = new TriggerRef(document.Path, name);
        d.Adds.Add(copy.ToString());
        d.Select = copy.ToString();
        d.Inverse(new RemoveTrigger(copy));
    }
}

/// <summary>Makes a trigger start with the mission, or not: <c>InitState = ACTIVE</c> after the nickname line and the
/// state lines that follow it.</summary>
public sealed record SetTriggerStart(TriggerRef Trigger, bool Starts) : MissionCommand
{
    protected override string Describe() => Starts ? $"Start {Trigger.Name} with the mission" : $"Do not start {Trigger.Name} with the mission";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index);
        d.SetStateLine(document, index, "InitState", Starts ? "ACTIVE" : null);
    }
}

/// <summary>Makes a trigger repeat, or not: <c>repeatable = true</c> at the place of a new state line. "No" removes each
/// <c>repeatable</c> line after the nickname line, also one after an action (TR10).</summary>
public sealed record SetTriggerRepeats(TriggerRef Trigger, bool Repeats) : MissionCommand
{
    protected override string Describe() => Repeats ? $"Repeat {Trigger.Name}" : $"Do not repeat {Trigger.Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index);
        d.SetStateLine(document, index, "repeatable", Repeats ? "true" : null);
    }
}

/// <summary>Sets the system hint of a trigger: the <c>system</c> line after the nickname line. The game does not read
/// it (TR3). Null removes the line.</summary>
public sealed record SetTriggerSystem(TriggerRef Trigger) : MissionCommand
{
    public string? System { get; init; }

    protected override string Describe() => $"Set the system of {Trigger.Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        var nickname = TriggerRules.NicknameLine(entries);
        var at = entries.FindIndex(nickname + 1, e => MissionIndex.Is(e.Name, "system"));
        if (System is not { } system)
        {
            if (at >= 0)
                entries.RemoveAt(at);
        }
        else
        {
            MissionValues.CheckText(system.Trim());
            if (at >= 0)
                entries[at] = d.NewEntry(document, index, entries[at].Name, [system.Trim()], entries.Take(at));
            else
            {
                var place = TriggerRules.StatePlace(entries);
                entries.Insert(place, d.NewEntry(document, index, "system", [system.Trim()], entries.Take(place)));
            }
        }
        d.SetLines(document, index, entries);
    }
}

/// <summary>Sets the condition of a trigger in the place of the effective condition (TR1). A trigger without a
/// condition gets the line after the state lines.</summary>
public sealed record SetCondition(TriggerRef Trigger, LineSpec Line) : MissionCommand
{
    protected override string Describe() => $"Set the condition of {Trigger.Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index);
        var line = d.Build("Trigger", LinePlace.Condition, Line, null);
        var entries = document.Working[index].ToList();
        var replaced = TriggerRules.EffectiveCondition(entries) is >= 0 and var effective ? entries[effective] : null;
        var at = TriggerRules.ConditionPlace(entries);
        if (replaced == null)
            entries.Insert(at, d.NewEntry(document, index, line.Key, line.Texts, entries.Take(at)));
        else
            entries[at] = d.NewEntry(document, index, line.Key, line.Texts, entries.Take(at));
        if (!d.SetLines(document, index, entries, inverse: false))
            return;
        var row = MissionDraft.RowOf(document, index, at);
        d.Changes.Add(row.Reference);
        d.Inverse(replaced != null
            ? new SetCondition(Trigger, MissionDraft.RawLine(replaced))
            : new RemoveLine(row));
    }
}

/// <summary>Sets one named value of the effective condition of a trigger.</summary>
public sealed record SetConditionValue(TriggerRef Trigger, string Name, MissionValue Value) : MissionCommand
{
    protected override string Describe() => $"Set {Name} of the condition of {Trigger.Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index);
        var at = TriggerRules.EffectiveCondition(document.Working[index].ToList());
        if (at < 0)
            throw d.Refuse(MissionErrors.NoConditionError(Trigger.Name));
        d.SetValue(document, index, at, Name, Value);
    }
}

/// <summary>Adds an action line to a trigger, before a row or at the end. A place before the nickname line or before the
/// condition is not allowed.</summary>
public sealed record AddAction(TriggerRef Trigger, LineSpec Line) : MissionCommand
{
    /// <summary>The row that the action goes before, or null for the end.</summary>
    public int? Before { get; init; }

    protected override string Describe() => $"Add {Line.Key} to {Trigger.Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Trigger);
        d.Editable(document, index);
        var line = d.Build("Trigger", LinePlace.Action, Line, null);
        var entries = document.Working[index].ToList();
        var at = d.Place(document, index, Before, entries.Count);
        if (!TriggerRules.ActionPlace(entries, at))
            throw d.Refuse(MissionErrors.BadPlaceError());
        entries.Insert(at, d.NewEntry(document, index, line.Key, line.Texts, entries.Take(at)));
        d.SetLines(document, index, entries, inverse: false, RowMap.OfInsert(entries.Count - 1, at));
        var row = MissionDraft.RowOf(document, index, at);
        d.Adds.Add(row.Reference);
        d.Inverse(new RemoveAction(row));
    }
}

/// <summary>Removes an action line of a trigger.</summary>
public sealed record RemoveAction(RowRef Row) : MissionCommand
{
    protected override string Describe() => "Remove action";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        d.RequireAction(document, index, entries, row);
        var old = entries[row];
        entries.RemoveAt(row);
        d.SetLines(document, index, entries, inverse: false, RowMap.OfRemoval(entries.Count + 1, [row]));
        d.Inverse(new InsertLine(MissionDraft.ItemOf(document, index), MissionDraft.RawLine(old)) { Before = row });
    }
}

/// <summary>Moves an action line of a trigger before another row, or to the end.</summary>
public sealed record ReorderAction(RowRef Row) : MissionCommand
{
    /// <summary>The row in the trigger before the move that the action goes before, or null for the end.</summary>
    public int? Before { get; init; }

    protected override string Describe() => "Move action";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        d.RequireAction(document, index, entries, row);
        if (Before is { } before && before < entries.Count)
            d.RequireAction(document, index, entries, before);
        d.Move(document, index, row, Before, actionsOnly: true);
    }
}

/// <summary>Sets one named value of an action line, or a run of values such as a position.</summary>
public sealed record SetActionValue(RowRef Row, string Name, MissionValue Value) : MissionCommand
{
    protected override string Describe() => $"Set {Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        d.RequireAction(document, index, document.Working[index].ToList(), row);
        d.SetValue(document, index, row, Name, Value);
    }
}

/// <summary>Sets a whole action line, also with a new key.</summary>
public sealed record SetActionLine(RowRef Row, LineSpec Line) : MissionCommand
{
    protected override string Describe() => $"Set {Line.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        d.RequireAction(document, index, entries, row);
        var line = d.Build("Trigger", LinePlace.Action, Line, entries[row]);
        var old = entries[row];
        entries[row] = d.NewEntry(document, index, line.Key, line.Texts, entries.Take(row));
        if (!d.SetLines(document, index, entries, inverse: false))
            return;
        var reference = MissionDraft.RowOf(document, index, row);
        d.Changes.Add(reference.Reference);
        d.Inverse(new SetActionLine(reference, MissionDraft.RawLine(old)));
    }
}

/// <summary>Links two triggers (section 4.9 of the M6b design): Start writes <c>Act_ActTrig</c>, Stop writes
/// <c>Act_DeactTrig</c>, and Restart writes both in the order of TR9.</summary>
/// <param name="Target">The nickname of the target trigger in the same script. A name that does not resolve gives the
/// problem M05.</param>
public sealed record LinkTriggers(TriggerRef Source, string Target, LinkKind LinkKind) : MissionCommand
{
    /// <summary>The row that the lines go before, or null for the end.</summary>
    public int? Before { get; init; }

    protected override string Describe() => $"Link {Source.Name} to {Target}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Source);
        d.Editable(document, index);
        var target = d.TriggerName(Target);
        var keys = LinkKind switch
        {
            LinkKind.Start => new[] { "Act_ActTrig" },
            LinkKind.Stop => ["Act_DeactTrig"],
            _ => ["Act_DeactTrig", "Act_ActTrig"],
        };
        var entries = document.Working[index].ToList();
        var at = d.Place(document, index, Before, entries.Count);
        if (!TriggerRules.ActionPlace(entries, at))
            throw d.Refuse(MissionErrors.BadPlaceError());
        var rows = new List<int>();
        foreach (var key in keys)
        {
            entries.Insert(at, d.NewEntry(document, index, key, [target], entries.Take(at)));
            rows.Add(at++);
        }
        d.SetLines(document, index, entries, inverse: false, RowMap.OfInsert(entries.Count - rows.Count, rows[0], rows.Count));
        foreach (var row in rows)
            d.Adds.Add(MissionDraft.RowOf(document, index, row).Reference);
        var inverse = rows.AsEnumerable().Reverse()
            .Select(r => (MissionCommand)new RemoveLine(MissionDraft.RowOf(document, index, r))).ToList();
        d.Inverse(inverse.Count == 1 ? inverse[0] : new CommandGroup(inverse));
    }
}

/// <summary>Removes the lines of a link: the <c>Act_ActTrig</c> lines of Start, the <c>Act_DeactTrig</c> lines of Stop,
/// and both of Restart.</summary>
public sealed record UnlinkTriggers(TriggerRef Source, string Target, LinkKind LinkKind) : MissionCommand
{
    protected override string Describe() => $"Unlink {Source.Name} from {Target}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Trigger(Source);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        var nickname = TriggerRules.NicknameLine(entries);
        var keys = LinkKind switch
        {
            LinkKind.Start => new[] { "Act_ActTrig" },
            LinkKind.Stop => ["Act_DeactTrig"],
            _ => ["Act_DeactTrig", "Act_ActTrig"],
        };
        var rows = Enumerable.Range(0, entries.Count)
            .Where(r => r > nickname && keys.Any(k => MissionIndex.Is(entries[r].Name, k)) && entries[r].Count > 0 &&
                        MissionIndex.Text(entries[r][0]).Equals(Target.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
            throw d.Refuse(MissionErrors.LinkNotFoundError(Source.Name, Target, LinkKind.ToString().ToLowerInvariant()));
        var inverse = new List<MissionCommand>();
        d.RemoveLines(document, rows.Select(r => (index, r)).ToList(), inverse);
        d.Inverse(inverse.Count == 1 ? inverse[0] : new CommandGroup(inverse));
    }
}

/// <summary>Gives a line that names a trigger another trigger: an <c>Act_ActTrig</c>, <c>Act_DeactTrig</c>,
/// <c>Act_Save</c> or <c>Cnd_WatchTrigger</c> line.</summary>
public sealed record RetargetLink(RowRef Row, string Target) : MissionCommand
{
    private static readonly string[] Keys = ["Act_ActTrig", "Act_DeactTrig", "Act_Save", "Cnd_WatchTrigger"];

    protected override string Describe() => $"Link to {Target}";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        if (!Keys.Any(k => MissionIndex.Is(entries[row].Name, k)) || entries[row].Count == 0)
            throw d.Refuse(MissionErrors.NotALinkError(MissionDraft.Owner(document, index), row));
        var target = d.TriggerName(Target);
        var old = MissionValues.TextsOf(entries[row]);
        if (string.Equals(old[0], target, StringComparison.Ordinal))
            return;
        var texts = old.ToList();
        texts[0] = target;
        entries[row] = d.NewEntry(document, index, entries[row].Name, texts, entries.Take(row));
        d.SetLines(document, index, entries, inverse: false);
        var reference = MissionDraft.RowOf(document, index, row);
        d.Changes.Add(reference.Reference);
        d.Inverse(new RetargetLink(reference, old[0]));
    }
}

/// <summary>Puts a new trigger on a Start link (section 4.9 of the M6b design): a new trigger with the condition and
/// <c>Act_ActTrig</c> of the old target, and the row of the link then names the new trigger.</summary>
public sealed record InsertTriggerOnLink(RowRef Row, LineSpec Condition) : MissionCommand
{
    public string? Nickname { get; init; }

    /// <summary>The place of the node of the new trigger in the graph, or null for the automatic layout. The gesture
    /// gives a place between the two triggers of the link (section 8.10 of the M6b design).</summary>
    public GraphPoint? Place { get; init; }

    protected override string Describe() => "Insert trigger";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        var entry = document.Working[index][row];
        if (!MissionIndex.Is(entry.Name, "Act_ActTrig") || entry.Count == 0)
            throw d.Refuse(MissionErrors.NotALinkError(MissionDraft.Owner(document, index), row) with
            {
                Message = $"Line {row} of {MissionDraft.Owner(document, index)} is not a Start link. Pick an Act_ActTrig line.",
            });
        var target = MissionIndex.Text(entry[0]);
        var add = new AddTrigger(new ScriptRef(document.Path))
        {
            Nickname = Nickname,
            Condition = Condition,
            Actions = [LineSpec.Raw("Act_ActTrig", target)],
            Place = Place,
        };
        var first = d.RunInner(add);
        var name = TriggerRules.Nickname(document.Working[^1])!;
        var second = d.RunInner(new RetargetLink(Row with { ExpectKey = null, ExpectValues = null }, name));
        d.Inverse(new CommandGroup([.. second, .. first]));
    }
}
