using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The value of a field of a prefab for a command: the field id and its rows in their text form (see
/// <see cref="PrefabRow"/>), for example <c>Cnd_Timer = 5</c> for a condition or <c>ol_form</c> for a value.</summary>
public sealed record PrefabFieldInput(string Field, IReadOnlyList<string> Rows)
{
    public bool Equals(PrefabFieldInput? other) =>
        other is not null && Field == other.Field && Rows.SequenceEqual(other.Rows, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Field, Rows.Count);
}

/// <summary>Places a linked instance of a prefab in a script (section 5.1 of the M6b design): the compiled sections at
/// the end of the script, and the <c>[Instance]</c> record in the graph file. Without an id, the layer makes one. The
/// template of a prefab refuses, because a prefab does not go into a prefab.</summary>
public sealed record PlacePrefabInstance(ScriptRef Script, PrefabRef Prefab) : MissionCommand
{
    /// <summary>The id of the instance, or null for a made id.</summary>
    public string? Id { get; init; }

    /// <summary>The values of the fields. A field without a value takes the default of the prefab.</summary>
    public IReadOnlyList<PrefabFieldInput> Values { get; init; } = [];

    /// <summary>The outside triggers that the ports start.</summary>
    public IReadOnlyList<InstancePort> Ports { get; init; } = [];

    /// <summary>The roles of the removed-member overrides of the new instance (decision D676 c). A duplicate of an
    /// instance keeps them, as Unity Duplicate keeps each override.</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>The place of the first member trigger in the graph, or null for the automatic layout.</summary>
    public GraphPoint? Place { get; init; }

    /// <summary>The act that the member triggers join, or null.</summary>
    public ActRef? Act { get; init; }

    /// <summary>The space between the nodes of two member triggers of a new instance, in canvas points.</summary>
    private const float NodeGap = MissionGraph.SummaryWidth + 60;

    protected override string Describe() => $"Place prefab {Prefab.Id}";

    internal override void Run(MissionDraft d)
    {
        // A template has no graph file, so it cannot keep the record of an instance.
        if (MissionRefs.IsPrefab(Script.Path))
            throw d.Refuse(MissionErrors.PrefabInPrefabError(Script.Path));
        var document = d.Script(Script);
        var asset = d.Prefab(Prefab);
        var template = d.TemplateOf(asset);
        var graph = d.Graph(document);
        var id = Id?.Trim() ?? d.NewInstanceId(document, template);
        if (!MissionNames.FormOk(id, MissionNames.MaxName))
            throw d.Refuse(MissionErrors.NameInvalidError(id, MissionNames.MaxName));
        if (graph.Instance(id) != null)
            throw d.Refuse(MissionErrors.NameTakenError(document.Path, "prefab instance", id));
        var record = d.RecordOf(new InstanceRecord(id, Prefab.RecordText), template, Values, Ports);
        var removed = Removed.Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
        foreach (var role in removed.Where(r => template.Section(r) == null))
            throw d.Refuse(MissionErrors.RoleNotFoundError(template.Name, role, template.Sections.Select(s => s.Role)));
        if (removed.Count > 0)
            record = record with
            {
                // A removed member keeps its made name, so Revert gives the section that name.
                Members = removed.Select(r => new InstanceMember(r, PrefabCompiler.MadeName(template.Section(r)!, id))).ToList(),
                Removed = removed,
            };
        var reference = new InstanceRef(document.Path, id);
        d.InstanceOps++;
        try
        {
            // The steps of the compile are not the inverse: the inverse removes the whole instance.
            d.Capture(() => d.Compile(document, record, template));
        }
        finally
        {
            d.InstanceOps--;
        }
        record = d.Graph(document).Instance(id)!;
        var present = record.Members.Where(m => !record.Removed.Contains(m.Role)).ToList();
        var triggers = present.Where(m => template.Section(m.Role)?.Type is "Trigger").Select(m => m.Nickname).ToList();
        MissionCommand? acts = null;
        if (Place != null || Act != null)
        {
            graph = d.Graph(document);
            if (Place is { } place)
                graph = graph with
                {
                    Nodes = [.. graph.Nodes, .. triggers.Select((t, i) => new GraphNodePlace(t, (float)Math.Round(place.X + i * NodeGap),
                        (float)Math.Round(place.Y)))],
                };
            if (Act is { } act)
            {
                var list = d.ActsForEdit(document, graph, out var written)
                    .Select(a => a with { Members = a.Members.Where(m => !triggers.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList() })
                    .ToList();
                var at = d.ActIndex(document, list, act);
                list[at] = list[at] with { Members = [.. list[at].Members, .. triggers] };
                graph = graph with { Acts = list };
                if (written)
                    acts = new CommandGroup(list.Select(a => (MissionCommand)new RemoveAct(new ActRef(document.Path, a.Nickname))).ToList());
            }
            d.SetGraph(document, graph);
        }
        d.Adds.Add(reference.ToString());
        d.Adds.AddRange(present.Select(m => MissionDraft.MemberIndex(document, template.Section(m.Role)?.Type, m.Nickname))
            .Where(i => i >= 0).Select(i => d.RefOf(document, i)));
        d.Select = reference.ToString();
        MissionCommand remove = new RemovePrefabInstance(reference);
        d.Inverse(acts != null ? new CommandGroup([remove, acts]) : remove);
    }
}

/// <summary>Sets a field of a prefab instance (sections 5.6 and 5.8 of the M6b design). A linked instance gets an
/// override, or a per-instance value, and compiles. A matched instance writes the lines that the field reads.</summary>
/// <param name="Rows">The rows of the value in their text form (see <see cref="PrefabRow"/>).</param>
public sealed record SetPrefabField(InstanceRef Instance, string Field, IReadOnlyList<string> Rows) : MissionCommand
{
    protected override string Describe() => $"Set {Field} of {Instance.Id}";

    internal override void Run(MissionDraft d)
    {
        if (Instance.IsMatched)
        {
            d.WriteMatchedField(Instance, Field, Rows);
            return;
        }
        var (document, record) = d.Linked(Instance);
        var template = d.TemplateOf(d.Prefab(d.PrefabOf(record)));
        var field = d.FieldOf(template, Field);
        var rows = d.Rows(field, Rows);
        var updated = d.WithValue(record, field, rows);
        if (updated.Equals(record))
            return;
        d.Recompile(document, record, updated, template);
    }
}

/// <summary>Sets the outside triggers that a port of a linked instance starts, and compiles the instance (section 5.3
/// of the M6b design).</summary>
public sealed record SetPrefabPort(InstanceRef Instance, string Port, IReadOnlyList<string> Triggers) : MissionCommand
{
    protected override string Describe() => $"Set {Port} of {Instance.Id}";

    internal override void Run(MissionDraft d)
    {
        var (document, record) = d.Linked(Instance);
        var template = d.TemplateOf(d.Prefab(d.PrefabOf(record)));
        if (template.Port(Port) == null)
            throw d.Refuse(MissionErrors.PortNotFoundError(template.Name, Port, template.Ports.Select(p => p.Id)));
        var triggers = Triggers.Select(d.TriggerName).ToList();
        var first = record.Ports.ToList().FindIndex(p => p.Port == Port);
        var ports = record.Ports.Where(p => p.Port != Port).ToList();
        ports.InsertRange(first < 0 ? ports.Count : first, triggers.Select(t => new InstancePort(Port, t)));
        var updated = record with { Ports = ports };
        if (updated.Equals(record))
            return;
        d.Recompile(document, record, updated, template);
    }
}

/// <summary>Removes overrides of a linked instance, and compiles it (section 5.6 of the M6b design). The field takes
/// the value of the prefab again, and a removed member comes back. Without a field and a member, all overrides
/// go.</summary>
public sealed record RevertOverride(InstanceRef Instance) : MissionCommand
{
    /// <summary>The field of the override, or null.</summary>
    public string? Field { get; init; }

    /// <summary>The role of a removed member (decision D676 c), or null.</summary>
    public string? Member { get; init; }

    protected override string Describe() => Field != null ? $"Revert {Field} of {Instance.Id}"
        : Member != null ? $"Revert the removed {Member} of {Instance.Id}"
        : $"Revert all of {Instance.Id}";

    internal override void Run(MissionDraft d)
    {
        var (document, record) = d.Linked(Instance);
        var template = d.TemplateOf(d.Prefab(d.PrefabOf(record)));
        var updated = d.WithoutOverrides(record, Field, Member);
        d.Recompile(document, record, updated, template);
    }
}

/// <summary>
/// Makes overrides of a linked instance the defaults of its prefab (section 5.6 of the M6b design): the prefab gets the
/// value, the override goes, and each linked instance of the prefab in the active mod compiles again in the same undo
/// entry. A removed member goes from the prefab. A built-in prefab and a prefab of a lower mod refuse.
/// </summary>
public sealed record ApplyOverrideToPrefab(InstanceRef Instance) : MissionCommand
{
    /// <summary>The field of the override, or null.</summary>
    public string? Field { get; init; }

    /// <summary>The role of a removed member, or null.</summary>
    public string? Member { get; init; }

    protected override string Describe() => Field != null ? $"Apply {Field} of {Instance.Id} to the prefab"
        : $"Apply the overrides of {Instance.Id} to the prefab";

    internal override void Run(MissionDraft d)
    {
        var (document, record) = d.Linked(Instance);
        var prefab = d.PrefabOf(record);
        var asset = d.ActivePrefab(prefab);
        var template = d.TemplateOf(asset);
        var updated = d.WithoutOverrides(record, Field, Member);
        var fields = LinkedInstance.FieldsOf(record, template)
            .Where(f => f.Override && (Field == null && Member == null || f.Field.Id == Field)).ToList();
        var roles = record.Removed.Where(r => Field == null && Member == null || r == Member).ToList();
        // A removed member goes from the prefab with the lines that name it, as the compile of the instance left them out.
        var changed = (template with
        {
            Fields = template.Fields.Select(f => fields.FirstOrDefault(o => o.Field.Id == f.Id) is { } value
                ? f with { Default = value.Rows }
                : f).ToList(),
        }).WithoutRoles(roles);
        d.SetPrefabText(prefab.Id, changed.Write());
        d.Inverse(new RestorePrefabAsset(prefab) { Text = asset.Text });
        d.Recompile(document, record, updated with { Removed = updated.Removed.Where(r => !roles.Contains(r)).ToList() }, changed);
        var (instances, _) = d.Propagate(prefab, changed, (document.Path, record.Id));
        d.PropagationNote(asset.Name, instances + 1,
            d.PropagatedScripts.Append(document.Path).Distinct(OpenLancer.Data.IO.VfsPath.Comparer).Count());
        d.Changes.Add(prefab.ToString());
    }
}

/// <summary>Compiles a linked instance again from its prefab and its fields (section 5.4 of the M6b design). A hand edit
/// of its lines goes (rule P01), and the instance gets the changes of its prefab (rule P03).</summary>
public sealed record CompilePrefabInstance(InstanceRef Instance) : MissionCommand
{
    protected override string Describe() => $"Compile {Instance.Id}";

    internal override void Run(MissionDraft d)
    {
        var (document, record) = d.Linked(Instance);
        d.Compile(document, record, d.TemplateOf(d.Prefab(d.PrefabOf(record))));
    }
}

/// <summary>Removes the record of a linked instance (section 5.9 of the M6b design). The member sections stay as plain
/// sections, and the matcher can find them again, as Unity "Unpack Prefab" does.</summary>
public sealed record UnpackPrefabInstance(InstanceRef Instance) : MissionCommand
{
    protected override string Describe() => $"Unpack {Instance.Id}";

    internal override void Run(MissionDraft d)
    {
        var (document, record) = d.Linked(Instance);
        var at = d.Graph(document).Instances.ToList().IndexOf(record);
        d.SetRecord(document, record.Id, null);
        d.Deletes.Add(new ResultDelete(new InstanceRef(document.Path, record.Id).ToString(), DeleteReason.Target, null));
        // The selection stays on the triggers: the first member, as Unity keeps the selection on an unpacked object.
        var template = d.Editor.Prefab(record.Prefab)?.Template;
        if (record.Members.Where(m => !record.Removed.Contains(m.Role))
                .Select(m => MissionDraft.MemberIndex(document, template?.Section(m.Role)?.Type, m.Nickname))
                .Where(i => i >= 0).Select(i => (int?)i).FirstOrDefault() is { } first)
            d.Select = d.RefOf(document, first);
        d.Inverse(new RestoreInstanceRecord(new ScriptRef(document.Path), record) { Index = at });
    }
}

/// <summary>Removes a linked instance: its member sections and its record (section 4.9 of the M6b design). Each line
/// outside the instance that names a member stays and shows "Missing", or goes with
/// <see cref="ReferencesMode.Remove"/>.</summary>
public sealed record RemovePrefabInstance(InstanceRef Instance) : MissionCommand
{
    public ReferencesMode References { get; init; } = ReferencesMode.Keep;

    protected override string Describe() => $"Delete instance {Instance.Id}";

    internal override void Run(MissionDraft d)
    {
        var (document, record) = d.Linked(Instance);
        d.RemoveInstance(document, record, References);
    }
}

/// <summary>Puts a record of a linked instance into the graph file of a script, in place of the record with its id, or
/// at an index. The inverses use it.</summary>
public sealed record RestoreInstanceRecord(ScriptRef Script, InstanceRecord Record) : MissionCommand
{
    /// <summary>The index of a new record in the list of the records, or null for the end.</summary>
    public int? Index { get; init; }

    protected override string Describe() => $"Restore instance {Record.Id}";

    internal override void Run(MissionDraft d)
    {
        var document = d.Script(Script);
        var records = d.Graph(document).Instances.ToList();
        var at = records.FindIndex(i => i.Id.Equals(Record.Id, StringComparison.OrdinalIgnoreCase));
        var old = at >= 0 ? records[at] : null;
        if (Record.Equals(old))
            return;
        d.SetRecord(document, Record.Id, Record, Index);
        var reference = new InstanceRef(document.Path, Record.Id);
        d.Changes.Add(reference.ToString());
        d.Inverse(old == null ? new UnpackPrefabInstance(reference) : new RestoreInstanceRecord(Script, old) { Index = at });
    }
}

/// <summary>
/// Makes a prefab asset of the active mod, <c>prefabs\missions\&lt;id&gt;.ini</c> (sections 5.2 and 5.9 of the M6b
/// design): from a template text, as a variant of a prefab, from a linked instance with its overrides as the defaults
/// ("Make a variant with this value"), or from a matched instance ("Save as prefab"). The source instance becomes a
/// linked instance of the new asset.
/// </summary>
public sealed record CreatePrefabAsset(string Id) : MissionCommand
{
    /// <summary>The name in the library, or null for the name of the source.</summary>
    public string? Name { get; init; }

    /// <summary>The whole text of the template. The inverse of a delete uses it.</summary>
    public string? Text { get; init; }

    /// <summary>The prefab that the new asset copies (a variant, decision D672), or null.</summary>
    public PrefabRef? VariantOf { get; init; }

    /// <summary>The instance that becomes a linked instance of the new asset, or null.</summary>
    public InstanceRef? Source { get; init; }

    /// <summary>For a linked source: the fields whose values become the defaults of the variant, or null for all its
    /// overrides.</summary>
    public IReadOnlyList<string>? Fields { get; init; }

    protected override string Describe() => $"Make prefab {Name ?? Id}";

    internal override void Run(MissionDraft d)
    {
        var id = Id.Trim();
        if (PrefabAsset.FileNameProblem(id) is { } problem)
            throw d.Refuse(MissionErrors.PrefabFileNameError(problem));
        // An asset of a lower mod with the id counts too: a new asset would hide it (review finding 6 of M6b-3).
        if (d.Editor.Prefab(PrefabRef.ModOf(id)) is { } taken)
            throw d.Refuse(MissionErrors.PrefabIdTakenError(id, taken.ModId, taken.Active));
        var prefab = PrefabRef.ModOf(id);
        if (Text != null)
        {
            d.SetPrefabText(id, Text);
            d.Inverse(new RemovePrefabAsset(prefab));
        }
        else if (Source is { IsMatched: true } matched)
            d.SaveAsPrefab(matched, id, Name);
        else if (Source is { } source)
        {
            var (document, record) = d.Linked(source);
            var asset = d.Prefab(d.PrefabOf(record));
            var template = d.TemplateOf(asset);
            var baked = LinkedInstance.FieldsOf(record, template)
                .Where(f => f.Override && (Fields == null || Fields.Contains(f.Field.Id))).ToList();
            var variant = template with
            {
                Id = id,
                Name = NameOf(d, template),
                VariantOf = record.Prefab,
                Fields = template.Fields.Select(f => baked.FirstOrDefault(b => b.Field.Id == f.Id) is { } value
                    ? f with { Default = value.Rows }
                    : f).ToList(),
            };
            d.SetPrefabText(id, variant.Write());
            d.Inverse(new RemovePrefabAsset(prefab));
            var relinked = record with
            {
                Prefab = prefab.RecordText,
                Overrides = record.Overrides.Where(o => baked.All(b => b.Field.Id != o.Field)).ToList(),
            };
            d.Recompile(document, record, relinked, variant);
            // The selection stays on the instance, which now uses the variant.
            d.Select = source.ToString();
        }
        else
        {
            var from = VariantOf ?? throw d.Refuse(new EditError(EditErrorKind.InvalidArgument, EditPlan.PlanRefused,
                "A new prefab needs a source: a template text, a prefab to copy, or an instance."));
            var asset = d.Prefab(from);
            var template = d.TemplateOf(asset);
            d.SetPrefabText(id, (template with
            {
                Id = id,
                Name = NameOf(d, template),
                VariantOf = from.RecordText,
            }).Write());
            d.Inverse(new RemovePrefabAsset(prefab));
        }
        d.Adds.Add(prefab.ToString());
        d.Select ??= prefab.ToString();
    }

    /// <summary>The name of a variant: the given name, or the name of the source with "variant" and a number when the
    /// library has the name (UI rule 9).</summary>
    private string NameOf(MissionDraft d, PrefabTemplate source) =>
        Name?.Trim() is { Length: > 0 } name ? name : d.Editor.FreePrefabName($"{source.Name} variant");
}

/// <summary>Removes a prefab asset of the active mod (section 4.9 of the M6b design, decision D673). Each linked
/// instance keeps its lines and gets P02, as Unity shows a missing prefab. A confirmation comes first when instances use
/// the asset. One undo brings the asset back.</summary>
public sealed record RemovePrefabAsset(PrefabRef Prefab) : MissionCommand
{
    protected override string Describe() => $"Delete prefab {Prefab.Id}";

    internal override void Run(MissionDraft d)
    {
        var asset = d.ActivePrefab(Prefab);
        var count = d.Editor.InstancesOf(Prefab).Count();
        if (count > 0)
            d.Confirmations.Add(MissionErrors.PrefabInUseError(asset.Name, count));
        d.SetPrefabText(Prefab.Id, null);
        d.Deletes.Add(new ResultDelete(Prefab.ToString(), DeleteReason.Target, null));
        // The inverse writes the old text back, also for an asset that hides an asset of a lower mod.
        d.Inverse(new RestorePrefabAsset(Prefab) { Text = asset.Text });
    }
}

/// <summary>Gives a prefab asset of the active mod a new file name, id and name (section 8.3 of the M6b design). Each
/// instance record of the active mod that names it follows. The hash of the prefab does not change, so no instance
/// needs a compile. With the same id, only the name in the library changes.</summary>
public sealed record RenamePrefabAsset(PrefabRef Prefab, string NewId) : MissionCommand
{
    /// <summary>The new name in the library, or null to keep the name.</summary>
    public string? NewName { get; init; }

    protected override string Describe() => NewId.Trim().Equals(Prefab.Id, StringComparison.Ordinal)
        ? $"Rename prefab {Prefab.Id} to '{NewName}'"
        : $"Rename prefab {Prefab.Id} to {NewId}";

    internal override void Run(MissionDraft d)
    {
        var asset = d.ActivePrefab(Prefab);
        var template = d.TemplateOf(asset);
        var id = NewId.Trim();
        var sameFile = id.Equals(Prefab.Id, StringComparison.Ordinal);
        var name = NewName?.Trim() is { Length: > 0 } given ? given : template.Name;
        if (sameFile && name == template.Name)
            return;
        var renamed = PrefabRef.ModOf(id);
        if (!sameFile)
        {
            // The file name is the id. A file system can ignore letter case, so a change of the letter case only would
            // keep the old file name.
            if (id.Equals(Prefab.Id, StringComparison.OrdinalIgnoreCase))
                throw d.Refuse(MissionErrors.PrefabFileNameError(
                    $"'{id}' differs from '{Prefab.Id}' only in letter case. Pick another file name."));
            if (PrefabAsset.FileNameProblem(id) is { } problem)
                throw d.Refuse(MissionErrors.PrefabFileNameError(problem));
            if (d.Editor.Prefab(renamed) is { } taken)
                throw d.Refuse(MissionErrors.PrefabIdTakenError(id, taken.ModId, taken.Active));
        }
        var records = d.Editor.InstancesOf(Prefab).ToList();
        if (!sameFile)
            d.SetPrefabText(Prefab.Id, null);
        d.SetPrefabText(id, (template with { Id = id, Name = name }).Write());
        // The inverse writes the old text back, then removes the new file, then gives each record its old prefab. So it
        // does not check the rule of a new file name, and an old id with capitals comes back as it was.
        var inverse = new List<MissionCommand> { new RestorePrefabAsset(Prefab) { Text = asset.Text } };
        if (!sameFile)
        {
            inverse.Add(new RestorePrefabAsset(renamed) { Text = null });
            foreach (var (script, record) in records)
                inverse.AddRange(d.RunInner(new RestoreInstanceRecord(new ScriptRef(script), record with { Prefab = renamed.RecordText })));
            d.Renames.Add(new ResultRename(Prefab.ToString(), renamed.ToString()));
        }
        d.Changes.Add(renamed.ToString());
        d.Inverse(new CommandGroup(inverse));
    }
}

/// <summary>Sets the whole text of a prefab asset of the active mod, or deletes the file with null. It does not compile
/// an instance. The inverses of the prefab commands use it to give the old text back.</summary>
public sealed record RestorePrefabAsset(PrefabRef Prefab) : MissionCommand
{
    /// <summary>The text of the file, or null for no file.</summary>
    public string? Text { get; init; }

    protected override string Describe() => $"Restore prefab {Prefab.Id}";

    internal override void Run(MissionDraft d)
    {
        if (Prefab.Builtin)
            throw d.Refuse(MissionErrors.BuiltInPrefabError(Prefab.Id));
        var old = d.Editor.Prefab(Prefab) is { Active: true } asset ? asset.Text : null;
        if (string.Equals(old, Text, StringComparison.Ordinal))
            return;
        d.SetPrefabText(Prefab.Id, Text);
        d.Changes.Add(Prefab.ToString());
        d.Inverse(new RestorePrefabAsset(Prefab) { Text = old });
    }
}

/// <summary>The helpers of the prefab commands in the draft.</summary>
internal sealed partial class MissionDraft
{
    /// <summary>The scripts that a propagation of this batch compiled.</summary>
    public HashSet<string> PropagatedScripts { get; } = new(OpenLancer.Data.IO.VfsPath.Comparer);

    /// <summary>Runs steps and gives the inverses that they recorded. The inverses do not go to the command that runs
    /// now.</summary>
    public List<MissionCommand> Capture(Action steps)
    {
        var count = Inverses.Count;
        steps();
        var recorded = Inverses.Skip(count).ToList();
        Inverses.RemoveRange(count, Inverses.Count - count);
        return recorded;
    }

    /// <summary>The prefab of a record.</summary>
    /// <exception cref="EditException"><c>PrefabNotFound</c> for a record text that is not a prefab.</exception>
    public PrefabRef PrefabOf(InstanceRecord record) =>
        PrefabRef.TryParse(record.Prefab) ?? throw Refuse(MissionErrors.PrefabNotFoundError(record.Prefab));

    /// <summary>The field of an id of a template.</summary>
    /// <exception cref="EditException"><c>FieldNotFound</c>.</exception>
    public PrefabFieldDef FieldOf(PrefabTemplate template, string id) =>
        template.Field(id.Trim()) ?? throw Refuse(MissionErrors.FieldNotFoundError(template.Name, id, template.Fields.Select(f => f.Id)));

    /// <summary>The rows of a value of a field from their text form, after the checks of the field.</summary>
    /// <exception cref="EditException"><c>FieldValue</c>.</exception>
    public IReadOnlyList<PrefabRow> Rows(PrefabFieldDef field, IReadOnlyList<string> texts)
    {
        var rows = texts.Select(t => PrefabRow.Parse(field.Kind, t)).ToList();
        if (PrefabValues.ValueProblem(field, rows) is { } problem)
            throw Refuse(MissionErrors.FieldValueError(field.Label, problem));
        return rows;
    }

    /// <summary>A record with a new value of a field: a per-instance value, an override, or no override when the value
    /// is the default of the prefab.</summary>
    public InstanceRecord WithValue(InstanceRecord record, PrefabFieldDef field, IReadOnlyList<PrefabRow> rows)
    {
        var value = new InstanceValue(field.Id, rows.Select(r => r.Format()).ToList());
        if (field.PerInstance)
            return record with { Values = Put(record.Values, value) };
        return rows.SequenceEqual(field.Default)
            ? record with { Overrides = record.Overrides.Where(o => o.Field != field.Id).ToList() }
            : record with { Overrides = Put(record.Overrides, value) };
    }

    private static IReadOnlyList<InstanceValue> Put(IReadOnlyList<InstanceValue> values, InstanceValue value)
    {
        var list = values.ToList();
        var at = list.FindIndex(v => v.Field == value.Field);
        if (at >= 0)
            list[at] = value;
        else
            list.Add(value);
        return list;
    }

    /// <summary>A record without the override of a field, without a removed member, or without all overrides.</summary>
    /// <exception cref="EditException"><c>NoOverride</c>.</exception>
    public InstanceRecord WithoutOverrides(InstanceRecord record, string? field, string? member)
    {
        if (field != null)
            return record.Overrides.Any(o => o.Field == field)
                ? record with { Overrides = record.Overrides.Where(o => o.Field != field).ToList() }
                : throw Refuse(MissionErrors.NoOverrideError(field, record.Id));
        if (member != null)
            return record.Removed.Contains(member)
                ? record with { Removed = record.Removed.Where(r => r != member).ToList() }
                : throw Refuse(MissionErrors.NoOverrideError(member, record.Id));
        return record.Overrides.Count + record.Removed.Count > 0
            ? record with { Overrides = [], Removed = [] }
            : throw Refuse(MissionErrors.NoOverrideError("(all)", record.Id) with
            {
                Message = $"Prefab instance '{record.Id}' has no override.",
            });
    }

    /// <summary>The record of a new instance: the values of the fields and the links of the ports, after their checks.
    /// A per-instance field holds its value, also the default. Another field holds an override only when its value
    /// differs from the default.</summary>
    /// <exception cref="EditException"><c>FieldNotFound</c>, <c>FieldValue</c> or <c>PortNotFound</c>.</exception>
    public InstanceRecord RecordOf(InstanceRecord record, PrefabTemplate template, IReadOnlyList<PrefabFieldInput> values,
        IReadOnlyList<InstancePort> ports)
    {
        foreach (var field in template.Fields.Where(f => f.PerInstance))
            record = WithValue(record, field, field.Default);
        foreach (var input in values)
        {
            var field = FieldOf(template, input.Field);
            record = WithValue(record, field, Rows(field, input.Rows));
        }
        foreach (var port in ports)
        {
            if (template.Port(port.Port) == null)
                throw Refuse(MissionErrors.PortNotFoundError(template.Name, port.Port, template.Ports.Select(p => p.Id)));
            TriggerName(port.Trigger);
        }
        return record with { Ports = ports.Select(p => p with { Trigger = p.Trigger.Trim() }).ToList() };
    }

    /// <summary>A made id of a new instance: the id of the prefab in lowercase and a number, free in the records of the
    /// script, with free names for its members.</summary>
    public string NewInstanceId(EditDocument script, PrefabTemplate template)
    {
        var graph = Graph(script);
        return PrefabCompiler.NewInstanceId(template, id => graph.Instance(id) != null,
            (type, name) => NameProblem(script, -1, type, name) != null);
    }

    /// <summary>
    /// Removes a linked instance: its member sections, its record, and the node places and the act places of its member
    /// triggers. A line outside the instance that names a member stays and shows "Missing", or goes with
    /// <see cref="ReferencesMode.Remove"/>. The inverse gives all of it back in its place.
    /// </summary>
    public void RemoveInstance(EditDocument script, InstanceRecord record, ReferencesMode references)
    {
        var template = Editor.Prefab(record.Prefab)?.Template;
        var members = record.Members.Where(m => !record.Removed.Contains(m.Role))
            .Select(m => (Member: m, Index: MemberIndex(script, template?.Section(m.Role)?.Type, m.Nickname)))
            .Where(m => m.Index >= 0).OrderBy(m => m.Index).ToList();
        var indexes = members.Select(m => m.Index).ToHashSet();
        var inverse = new List<MissionCommand>();
        InstanceOps++;
        try
        {
            foreach (var (member, at) in members)
            {
                var section = script.Working[at];
                var lines = MissionLinks.Naming(Schema, script.Working, MissionLinks.KindsOf(section.Name), member.Nickname)
                    .Where(l => !indexes.Contains(l.Section)).Select(l => (l.Section, l.Row)).Distinct().ToList();
                if (lines.Count == 0)
                    continue;
                if (references == ReferencesMode.Remove)
                    RemoveLines(script, lines, inverse);
                else
                    MarkMissing(script, lines, member.Nickname, "The game ignores this line, because the item is gone.");
            }
            var graph = Graph(script);
            var triggers = members.Where(m => MissionIndex.Is(script.Working[m.Index].Name, "Trigger"))
                .Select(m => m.Member.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var path = new ScriptRef(script.Path);
            var restore = new List<MissionCommand>();
            foreach (var (member, at) in members)
            {
                Deletes.Add(new ResultDelete(RefOf(script, at), DeleteReason.Cascade, new InstanceRef(script.Path, record.Id).ToString()));
                restore.Add(new RestoreSection(path, at, RawSection(script.Working[at])));
            }
            foreach (var (_, at) in members.AsEnumerable().Reverse())
                Remove(script, at);
            var nodes = graph.Nodes.Select((n, i) => (Node: n, Index: i)).Where(n => triggers.Contains(n.Node.Trigger)).ToList();
            var acts = graph.Acts.Where(a => a.Members.Any(triggers.Contains)).ToList();
            var recordAt = graph.Instances.ToList().IndexOf(record);
            SetGraph(script, graph with
            {
                Nodes = graph.Nodes.Where(n => !triggers.Contains(n.Trigger)).ToList(),
                Acts = graph.Acts.Select(a => a with { Members = a.Members.Where(m => !triggers.Contains(m)).ToList() }).ToList(),
                Instances = graph.Instances.Where(i => !ReferenceEquals(i, record)).ToList(),
            });
            restore.Add(new RestoreInstanceRecord(path, record) { Index = recordAt });
            if (nodes.Count > 0)
                restore.Add(new MoveNodes(path, nodes.Select(n => new NodePlace(n.Node.Trigger, n.Node.X, n.Node.Y) { Index = n.Index })
                    .ToList()));
            restore.AddRange(acts.Select(a => new SetActMembers(new ActRef(script.Path, a.Nickname), a.Members)));
            // The lines that the delete removed come back after the sections that they name.
            restore.AddRange(inverse);
            Deletes.Add(new ResultDelete(new InstanceRef(script.Path, record.Id).ToString(), DeleteReason.Target, null));
            Inverse(restore.Count == 1 ? restore[0] : new CommandGroup(restore));
        }
        finally
        {
            InstanceOps--;
        }
    }

    /// <summary>The Console note of a propagation (section 8.6 of the M6b design).</summary>
    public void PropagationNote(string prefab, int instances, int scripts) =>
        Notes.Add($"Prefab '{prefab}' changed {MissionChecks.Count(instances, "instance", "instances")} in " +
                  $"{MissionChecks.Count(scripts, "mission", "missions")}.");

    /// <summary>
    /// Compiles a linked instance after a command changed its record: a field, a port, an override or its prefab. A hand
    /// edit of its lines goes, and a note says so (rule P01). Propagation skips such an instance, but a change of the
    /// instance itself compiles it.
    /// </summary>
    /// <param name="before">The record before the command.</param>
    public InstanceRecord Recompile(EditDocument script, InstanceRecord before, InstanceRecord updated, PrefabTemplate template)
    {
        if (HandEdited(script, before))
            Notes.Add(PrefabLinks.ReplacedHandEditNote(before.Id, script.Path));
        return Compile(script, updated, template);
    }
}
