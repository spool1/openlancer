using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>An archetype that a template of Create can make, for the picker.</summary>
/// <param name="Nickname">The nickname of the <c>[Solar]</c>.</param>
/// <param name="Type">Its <c>type</c> in lowercase.</param>
/// <param name="Entry">The entry of the list of the solar archetypes.</param>
public sealed record TemplateArchetype(string Nickname, string Type, ListEntry Entry);

/// <summary>
/// The plans of the actions on the items of a system in the Scene (milestone M3): Create, Duplicate, Delete and Rename of
/// objects, zones and lights, and Delete of nebulae and asteroid fields. Each plan gives one command, which runs as one
/// entry of the edit history. A check never refuses an edit command: a plan refuses only what would leave data that the
/// editor cannot show or fix.
/// </summary>
/// <remarks>
/// <para>A delete follows Unity (decision D81): the children go with the object, a reference gets a safe fix in the same
/// entry when the rule data gives one, another reference shows "Missing", and a reference whose rule refuses the delete
/// refuses it, as RequireComponent does. One Undo restores everything.</para>
/// <para>A rename changes each line that the reverse index finds, the gate locks of <c>InitialWorld.ini</c> by their hash,
/// and a section of the lower layer through <c>ol.rename</c>, so it keeps its place (decisions D9, D12, D231 and
/// D257).</para>
/// <para>Jump gates, jump holes, airlock gates and trade lane rings stay out of Create, Duplicate and Delete until
/// milestone M27 (decision D253). A rename of them works.</para>
/// <para>The asset browser of milestone M23 adds a static prop archetype with its object (<c>ScenePlans.Props.cs</c>)
/// and places prop groups (<see cref="Assets.PropGroups"/>). Create, the prop archetype, Duplicate and the prop groups
/// share the object builder (<see cref="NewObject"/>) and the copy builder (<see cref="CopyLines"/>). So the editor
/// has one way to write a new object and one way to copy a section (review finding R9 of the M23 design).</para>
/// </remarks>
public static partial class ScenePlans
{
    /// <summary>The value of <c>pos</c> of a new item: whole numbers (decision D234).</summary>
    internal static string PositionText(Vector3 position) => string.Join(", ",
        new[] { position.X, position.Y, position.Z }.Select(v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture)));

    /// <summary>The templates of objects of the rule data, in the order of the Create menu, with their labels.</summary>
    public static IReadOnlyList<(SceneTemplate Template, string Label)> ObjectTemplates(EditSession session) =>
        session.Lists.RuleData.SceneTemplates
            .Select(t => (Template: TemplateOf(t.Id), t.Label))
            .Where(t => t.Template != null)
            .Select(t => (t.Template!.Value, t.Label))
            .ToList();

    /// <summary>
    /// The archetypes of a template of objects, from the list of the solar archetypes of W: the archetypes whose type the
    /// template holds, and for a station only the archetypes with a <c>docking_sphere</c> line. A connection type is in no
    /// template (decision D253).
    /// </summary>
    public static IReadOnlyList<TemplateArchetype> Archetypes(EditSession session, SceneTemplate template)
    {
        if (Definition(session, template) is not { } definition || session.Lists.List("solar_archetype") is not { } list)
            return [];
        var result = new List<TemplateArchetype>();
        foreach (var entry in list.Entries)
        {
            if (entry.Section == null || session.Get(entry.Section) is not { } view ||
                SceneSections.Last(view, "type")?.ToLowerInvariant() is not { } type || !definition.Types.Contains(type) ||
                definition.Needs != null && view.Lines(definition.Needs).Count == 0)
                continue;
            result.Add(new TemplateArchetype(entry.Value, type, entry));
        }
        return result;
    }

    /// <summary>True when the template asks for a base after the archetype (decision D263): the station. Each archetype
    /// of <see cref="Archetypes"/> has the line that the template needs, so the Create menu asks for each of them.</summary>
    public static bool AsksForBase(EditSession session, SceneTemplate template) =>
        Definition(session, template)?.Then == "base";

    /// <summary>
    /// True when the template asks for a base after the archetype, and the archetype has the line that the template
    /// needs (decision D277 of the M23 design). A base needs a docking sphere. So a station archetype without one gets
    /// no base step.
    /// </summary>
    public static bool AsksForBase(EditSession session, SceneTemplate template, string archetype) =>
        Definition(session, template) is { Then: "base" } definition &&
        (definition.Needs == null || session.Lists.List("solar_archetype")?.Find(archetype)?.Section is { } solar &&
            session.Get(solar)?.Lines(definition.Needs).Count > 0);

    /// <summary>
    /// The template of Create whose types hold the type of an archetype of the list of the solar archetypes (decision
    /// D277 of the M23 design). Null for an archetype outside the list, for a connection type (decision D253) and for a
    /// type in no template. Unlike <see cref="Archetypes"/>, the <c>needs</c> line of a template does not count. So a
    /// station archetype without a docking sphere has the station template.
    /// </summary>
    public static SceneTemplate? TemplateFor(EditSession session, string archetype) =>
        TypeOf(session, archetype) is { } type
            ? session.Lists.RuleData.SceneTemplates.Where(t => t.Types.Contains(type)).Select(t => TemplateOf(t.Id))
                .FirstOrDefault(t => t != null)
            : null;

    /// <summary>The <c>[Solar] type</c> of an archetype of the list in lowercase, or null.</summary>
    internal static string? TypeOf(EditSession session, string archetype) =>
        session.Lists.List("solar_archetype")?.Find(archetype)?.Section is { } solar && session.Get(solar) is { } view
            ? SceneSections.Last(view, "type")?.ToLowerInvariant()
            : null;

    /// <summary>The bases of the list of the bases: the bases of the system first, then the others, each in list
    /// order (review finding R24).</summary>
    public static IReadOnlyList<ListEntry> Bases(EditSession session, string system)
    {
        if (session.Lists.List("base") is not { } list)
            return [];
        bool InSystem(ListEntry e) => e.Section != null && session.Get(e.Section) is { } view &&
                                      string.Equals(SceneSections.Last(view, "system"), system, StringComparison.OrdinalIgnoreCase);
        return list.Entries.Where(InSystem).Concat(list.Entries.Where(e => !InSystem(e))).ToList();
    }

    /// <summary>
    /// Plans a new object, zone or light in a system (decisions D233 to D235, D250 and D263).
    /// </summary>
    /// <param name="system">The nickname of the system.</param>
    /// <param name="archetype">The archetype of an object template, or null for a zone or a light.</param>
    /// <param name="position">The position. The plan rounds each value to a whole number.</param>
    /// <param name="parent">The object that gets the new object as a child (<c>parent</c>), or null. Only an object
    /// template takes it.</param>
    /// <param name="dockWith">The base of a station, or null.</param>
    /// <param name="shape">The shape of a zone.</param>
    public static ScenePlan Create(EditSession session, string system, SceneTemplate template, string? archetype,
        Vector3 position, SectionRef? parent, string? dockWith = null,
        LibreLancer.Data.Schema.Universe.ZoneShape shape = LibreLancer.Data.Schema.Universe.ZoneShape.SPHERE)
    {
        var plan = new ScenePlan(SceneAction.Create, "Create");
        var refusals = new List<Diagnostic>();
        if (SystemFileProblem(session, system, out var path) is { } fileProblem)
            return Refused(plan, fileProblem);
        void Refuse(string message) => refusals.Add(Diagnostic.Error(path, message));

        var type = template switch
        {
            SceneTemplate.Zone => SceneSections.ZoneType,
            SceneTemplate.Light => SceneSections.LightType,
            _ => SceneSections.ObjectType,
        };
        if (type == SceneSections.ObjectType)
        {
            if (archetype == null)
                Refuse("Pick an archetype for the new object.");
            else if (TemplateFor(session, archetype) != template)
                Refuse(TemplateProblem(session, archetype, template));
        }
        if (refusals.Count > 0)
            return Refused(plan, [.. refusals]);

        var stem = $"{system}_{TemplateId(template)}".ToLowerInvariant();
        string? error;
        if (type == SceneSections.ObjectType)
        {
            if (NewObject(session, path, stem, position, archetype!, parent, dockWith, out error) is not { } made)
                return Refused(plan, Diagnostic.Error(path, error!));
            return Created(session, plan, path, type, made.Nickname, made.Entries);
        }

        var binary = session.IsBinaryFile(path);
        var nickname = type == SceneSections.LightType
            ? session.Nicknames.FirstFree(stem, 1, taken: n => session.Nicknames.LightTaken(path, n) != null)
            : session.Nicknames.FirstFree(stem, 1);
        var lines = new List<(string Name, string Text)> { ("nickname", nickname), ("pos", PositionText(position)) };
        switch (template)
        {
            case SceneTemplate.Zone:
                lines.Add(("shape", shape.ToString()));
                lines.Add(("size", shape switch
                {
                    LibreLancer.Data.Schema.Universe.ZoneShape.ELLIPSOID => "2000, 1000, 2000",
                    LibreLancer.Data.Schema.Universe.ZoneShape.BOX => "2000, 2000, 2000",
                    LibreLancer.Data.Schema.Universe.ZoneShape.CYLINDER => "1000, 2000",
                    LibreLancer.Data.Schema.Universe.ZoneShape.RING => "2000, 1000, 200",
                    _ => "1000",
                }));
                break;
            case SceneTemplate.Light:
                lines.Add(("color", "255, 255, 255"));
                lines.Add(("range", "5000"));
                lines.Add(("type", "POINT"));
                lines.Add(("attenuation", "1, 0, 0.000001"));
                break;
        }
        if (Entries(lines, binary, out error) is not { } entries)
            return Refused(plan, Diagnostic.Error(path, error!));
        return Created(session, plan, path, type, nickname, entries);
    }

    /// <summary>Gives a Create plan its new section and its command.</summary>
    private static ScenePlan Created(EditSession session, ScenePlan plan, string path, string type, string nickname,
        List<EntryView> entries)
    {
        var added = new SectionRef(path, new SectionIdentity(type, nickname));
        plan.Label = $"Create {nickname}";
        plan.Adds = [added];
        plan.SelectAfter = added;
        plan.Warnings = DanglingWarnings(session, nickname, path);
        plan.Command = EditCommand.Group(plan.Label, [EditCommand.AddSection(path, type, entries)], session.Snapshot());
        return plan;
    }

    /// <summary>Why an archetype is not an archetype of a template: a connection type (decision D253), a type in no
    /// template (section 5.2 of the M23 design), or another template.</summary>
    internal static string TemplateProblem(EditSession session, string archetype, SceneTemplate template)
    {
        var type = TypeOf(session, archetype);
        if (type != null && session.Lists.RuleData.ConnectionTypes.Contains(type))
            return $"The archetype {archetype} is a jump gate, a jump hole or a trade lane ring. The editor adds connections " +
                   "in milestone M27.";
        if (type != null && !session.Lists.RuleData.SceneTemplates.Any(t => t.Types.Contains(type)))
            return NoTemplateReason(type);
        return $"The archetype {archetype} is not an archetype of the template {template}.";
    }

    /// <summary>Why an archetype of a type in no template of Create cannot spawn (section 5.2 of the M23
    /// design).</summary>
    internal static string NoTemplateReason(string type) => $"The type {type} is in no Create template of the rule data.";

    /// <summary>
    /// Why a new item cannot go into the file of a system, or null (review finding R9 of the M23 design): the system has
    /// no file in the edit view, or the file is read-only.
    /// </summary>
    /// <param name="path">The install path of the system file, or an empty text when the system has none.</param>
    internal static Diagnostic? SystemFileProblem(EditSession session, string system, out string path)
    {
        path = session.SystemPath(system) ?? "";
        if (path.Length == 0 || session.OpenDocument(path) is not { } document)
            return Diagnostic.Error(system, $"The system '{system}' has no file in the edit view.");
        return document.ReadOnlyReason is { } reason
            ? Diagnostic.Error(path, $"{SceneSections.FileName(path)}: {reason}")
            : null;
    }

    /// <summary>
    /// The object builder (review finding R9 of the M23 design): the nickname and the lines of a new object of a system
    /// file. The lines are <c>nickname</c>, <c>pos</c> with whole numbers (decision D234) and <c>archetype</c>. Then come
    /// <c>parent</c> and <c>dock_with</c> when the caller gives them. The nickname is the first free
    /// <c>&lt;stem&gt;_&lt;n&gt;</c> (decision D235). Create and the static prop archetype use it.
    /// </summary>
    /// <param name="stem">The stem of the nickname, for example <c>li01_station</c>.</param>
    /// <param name="parent">The object that gets the new object as a child, or null.</param>
    /// <param name="dockWith">The base of a station, or null.</param>
    /// <returns>The nickname and the lines, or null when the file cannot hold a line. The error then tells why.</returns>
    internal static (string Nickname, List<EntryView> Entries)? NewObject(EditSession session, string path, string stem,
        Vector3 position, string archetype, SectionRef? parent, string? dockWith, out string? error)
    {
        var nickname = session.Nicknames.FirstFree(stem, 1);
        var lines = new List<(string Name, string Text)>
        {
            ("nickname", nickname), ("pos", PositionText(position)), ("archetype", archetype),
        };
        if (parent?.Id.Key is { } parentKey)
            lines.Add(("parent", parentKey));
        if (dockWith != null)
            lines.Add(("dock_with", dockWith));
        return Entries(lines, session.IsBinaryFile(path), out error) is { } entries ? (nickname, entries) : null;
    }

    /// <summary>
    /// Plans copies of items with the next free nicknames (decisions D241, D259, D260 and D262): each target with its
    /// <c>parent</c> children, and their children, at the same place. The copies go at the end of the file, the parent
    /// before its children, so the launch object of a base does not change. A copy leaves out the <c>base</c> line.
    /// </summary>
    public static ScenePlan Duplicate(EditSession session, IReadOnlyList<SectionRef> targets)
    {
        var plan = new ScenePlan(SceneAction.Duplicate, "Duplicate");
        var refusals = new List<Diagnostic>();
        var set = new List<SectionRef>();
        foreach (var target in targets)
            Collect(session, target, set);
        foreach (var section in set)
        {
            if (session.Get(section) is not { } view || SceneSections.KindOf(session, section) is not { } kind)
            {
                refusals.Add(NotAnItem(section, "copy"));
                continue;
            }
            if (SceneSections.ConnectionKind(session, view) is { } connection)
                refusals.Add(Diagnostic.Error(section.Path, SceneSections.ConnectionMessage("copy", section, view, connection))
                    with { Target = section });
            else if (kind is SceneKind.Nebula or SceneKind.Asteroids || kind == SceneKind.Zone && HoldsVolume(session, section))
                refusals.Add(Diagnostic.Error(section.Path, $"{SceneSections.Label(kind, section)}: the editor copies " +
                                                            "nebulae and asteroid fields in milestone M26.") with { Target = section });
            else if (session.OpenDocument(section.Path)?.ReadOnlyReason is { } reason)
                refusals.Add(Diagnostic.Error(section.Path, $"{section.Id}: {reason}") with { Target = section });
        }
        if (refusals.Count > 0)
            return Refused(plan, [.. refusals]);

        // The new nickname of each copy, and the names that the plan takes.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<SectionRef, string>();
        foreach (var section in set)
        {
            var old = section.Id.Key!;
            var name = SceneSections.KindOf(session, section) == SceneKind.Light
                ? session.Nicknames.CopyName(old, reserved,
                    n => reserved.Contains(n) || session.Nicknames.LightTaken(section.Path, n) != null)
                : session.Nicknames.CopyName(old, reserved);
            reserved.Add(name);
            names[section] = name;
        }

        var warnings = new List<Diagnostic>();
        var commands = new List<EditCommand>();
        var adds = new List<SectionRef>();
        var problems = new List<string>();
        var texts = new SectionCopies(session, problems.Add);
        foreach (var section in set)
        {
            var source = session.WorkingSection(section)!;
            var name = names[section];
            var binary = session.IsBinaryFile(section.Path);
            var copy = new SectionIdentity(source.Name, name);
            var (objects, zones) = CopiedNames(set.Where(s => VfsPath.Comparer.Equals(s.Path, section.Path)), names);
            var entries = CopyLines(source.Name, Lines(source), "nickname", "nickname", name, objects, zones,
                keepOutside: true, (key, i, value) => texts.Copy(source.Name, key, i, value, copy),
                // A base has one object in space (decision D259).
                (_, _) => warnings.Add(Diagnostic.Warning(section.Path, $"{name}: the copy has no 'base' line. A base has " +
                                                                        $"one object in space, and {section.Id.Key} stays " +
                                                                        "that object.") with { Target = section }));
            commands.Add(EditCommand.AddSection(section.Path, source.Name, entries));
            adds.Add(new SectionRef(section.Path, copy));
            if (PatchFormatter.KeyError(name, binary) is { } keyError)
                problems.Add(keyError);
            warnings.AddRange(DanglingWarnings(session, name, section.Path));
        }
        if (problems.Count > 0)
            return Refused(plan, problems.Select(p => Diagnostic.Error(set[0].Path, p)).ToArray());
        commands.AddRange(texts.Commands);
        plan.Label = $"Duplicate {targets[0].Id.Key}" + (set.Count > 1 ? $" and {set.Count - 1} more" : "");
        plan.Adds = adds;
        plan.SelectAfter = adds[set.IndexOf(targets[0])];
        plan.Warnings = warnings;
        plan.Command = EditCommand.Group(plan.Label, commands, session.Snapshot());
        return plan;
    }

    /// <summary>
    /// Plans a delete of items (decisions D81, D236, D237 and D238). The targets go with their child sections. The
    /// sections that a rule with <c>onDelete: cascade</c> names go too, again and again: the <c>parent</c> children of an
    /// object, and the nebula, the field and the loot zones of a zone. A line of a rule with <c>clear</c> or
    /// <c>clearHole</c> goes in the same entry. A rule with <c>refuse</c> refuses the delete. Each other line shows
    /// "Missing". A delete of a nebula or a field removes only its section, and its zone stays.
    /// </summary>
    public static ScenePlan Delete(EditSession session, IReadOnlyList<SectionRef> targets)
    {
        var plan = new ScenePlan(SceneAction.Delete, "Delete");
        var refusals = new List<Diagnostic>();
        foreach (var target in targets)
        {
            if (session.Get(target) is not { } view || SceneSections.KindOf(session, target) is null)
                refusals.Add(NotAnItem(target, "delete"));
            else if (SceneSections.ConnectionKind(session, view) is { } connection)
                refusals.Add(Diagnostic.Error(target.Path, SceneSections.ConnectionMessage("delete", target, view, connection))
                    with { Target = target });
            else if (view.ReadOnlyReason is { } reason)
                refusals.Add(Diagnostic.Error(target.Path, $"{target.Id}: {reason}") with { Target = target });
        }
        if (refusals.Count > 0)
            return Refused(plan, [.. refusals]);

        var deletes = new List<PlannedDelete>();
        var inSet = new HashSet<SectionRef>();
        var queue = new Queue<(SectionRef Section, DeleteReason Why, string? Cause)>();
        foreach (var target in targets.Distinct())
            queue.Enqueue((target, DeleteReason.Target, null));
        var fixes = new Dictionary<SectionRef, List<(int First, int Count, string Key, string Text)>>();
        var holes = new HashSet<(SectionRef, string)>();
        var missing = new List<MissingReference>();
        while (queue.TryDequeue(out var next))
        {
            var (section, why, cause) = next;
            if (!inSet.Add(section) || session.Get(section) is not { } view)
                continue;
            if (why == DeleteReason.Cascade)
            {
                if (SceneSections.ConnectionKind(session, view) is { } connection)
                {
                    refusals.Add(Diagnostic.Error(section.Path, $"{section.Id.Key} goes with {cause}, but " +
                        SceneSections.ConnectionMessage("delete", section, view, connection)) with { Target = section });
                    continue;
                }
                if (view.ReadOnlyReason is { } reason)
                {
                    refusals.Add(Diagnostic.Error(section.Path, $"{section.Id} goes with {cause}, but the editor cannot " +
                                                                $"delete it: {reason}") with { Target = section });
                    continue;
                }
            }
            deletes.Add(new PlannedDelete(section, session.ChildCount(section), why, cause));
            foreach (var (list, name, scope) in NamesOf(session, section))
            {
                // Another section that stays still gives the name.
                if (session.Sections(section.Path).Any(s => !inSet.Contains(s) && SceneSections.Is(s.Id.Type, section.Id.Type) &&
                                                            string.Equals(s.Id.Key, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                foreach (var reference in session.Lists.ReferencesTo(list, name, scope))
                {
                    if (inSet.Contains(reference.Section))
                        continue;
                    var referrer = session.Get(reference.Section);
                    switch (reference.Rule.OnDelete)
                    {
                        case DeleteAction.Cascade:
                            queue.Enqueue((reference.Section, DeleteReason.Cascade, $"{reference.Key} = {name}"));
                            break;
                        case DeleteAction.Refuse:
                            refusals.Add(Diagnostic.Error(reference.Section.Path,
                                $"{reference.Section.Id} names {name} in its '{reference.Key}' line. " +
                                $"{reference.Rule.ConsequenceText} Remove that line first, or keep {name}.") with
                            {
                                Target = reference.Section, Key = reference.Key,
                            });
                            break;
                        case DeleteAction.Clear or DeleteAction.ClearHole when referrer?.ReadOnlyReason is { } reason:
                            missing.Add(new MissingReference(reference, $"The editor cannot change this line: {reason}",
                                reference.Rule.Closed));
                            break;
                        case DeleteAction.Clear:
                            if (LineIndex(referrer!, reference.Key, reference.Line) is >= 0 and var at)
                                AddFix(fixes, reference.Section, at, 1, reference.Key,
                                    $"{SceneSections.FileName(reference.Section.Path)}: the '{reference.Key}' line of " +
                                    $"{reference.Section.Id.Key ?? reference.Section.Id.ToString()}");
                            break;
                        case DeleteAction.ClearHole:
                            // A hole of a file with another owner system that has the zone still serves that system. One
                            // section can name the zone more than one time, and its runs go once.
                            if (OtherOwnerHasZone(session, reference.Section.Path, section.Path, name) ||
                                !holes.Add((reference.Section, name.ToLowerInvariant())))
                                break;
                            foreach (var (first, count) in ExclusionHoles.Runs(session.WorkingSection(reference.Section)!, name))
                                AddFix(fixes, reference.Section, first, count, reference.Key,
                                    $"{SceneSections.FileName(reference.Section.Path)}: the hole of {name} ({count} " +
                                    (count == 1 ? "line)" : "lines)"));
                            break;
                        default:
                            missing.Add(new MissingReference(reference, reference.Rule.ConsequenceText, reference.Rule.Closed));
                            break;
                    }
                }
            }
        }
        if (refusals.Count > 0)
            return Refused(plan, refusals.DistinctBy(r => r.Message).ToArray());

        // A fix or a "Missing" line of a section that goes with the delete does not apply.
        foreach (var gone in fixes.Keys.Where(inSet.Contains).ToList())
            fixes.Remove(gone);
        missing.RemoveAll(m => inSet.Contains(m.Reference.Section));

        var warnings = new List<Diagnostic>();
        var mentions = new List<NamedMention>();
        foreach (var delete in deletes)
        {
            var kind = SceneSections.KindOf(session, delete.Section);
            if (kind is not null && SceneSections.IsNamed(kind.Value) && delete.Section.Id.Key is { } key)
                mentions.AddRange(session.StoryMentions(key).Select(m => new NamedMention(key, m.Path, m.Count)));
            if (kind == SceneKind.Object)
                warnings.AddRange(BaseWarnings(session, delete.Section, inSet));
            foreach (var mod in session.LaterModsThatChangeSection(delete.Section))
                warnings.Add(Diagnostic.Warning(delete.Section.Path, $"The mod {mod} loads later and changes " +
                                                                     $"{delete.Section.Id}. Its patch cannot find the section " +
                                                                     "after the delete.") with { Target = delete.Section });
        }

        var commands = new List<EditCommand>();
        // The fixes of each section remove their lines from the last to the first, so each index stays valid.
        foreach (var (section, ranges) in fixes)
        {
            foreach (var (first, count, _, _) in ranges.DistinctBy(r => r.First).OrderByDescending(r => r.First))
                commands.Add(EditCommand.RemoveEntries(section, first, count));
        }
        // The deletes of each file go from the last section to the first, so an ordinal of a repeated key does not
        // change before its delete.
        foreach (var file in deletes.GroupBy(d => d.Section.Path, VfsPath.Comparer))
        {
            var order = session.Sections(file.Key).ToList();
            foreach (var delete in file.OrderByDescending(d => order.IndexOf(d.Section)))
                commands.Add(EditCommand.DeleteSection(delete.Section));
        }
        var head = targets[0];
        plan.Label = $"Delete {SceneSections.Label(SceneSections.KindOf(session, head)!.Value, head)}" +
                     (deletes.Count > 1 ? $" and {deletes.Count - 1} more" : "");
        plan.Deletes = deletes;
        plan.Fixes = fixes.SelectMany(f => f.Value.Select(r => new PlannedFix(f.Key, r.Key, r.Text))).ToList();
        plan.Missing = missing;
        // Two objects that go can give the same base warning.
        warnings = warnings.DistinctBy(w => w.Message).ToList();
        plan.Warnings = warnings;
        plan.Mentions = mentions;
        // The mission lines that name a deleted object stay and show "Missing" (decision D668).
        plan.MissionMissing = session.Missions.References.Missing(Missions.Editing.MissionReferences.ObjectKinds,
            deletes.Where(d => SceneSections.KindOf(session, d.Section) == SceneKind.Object && d.Section.Id.Key != null)
                .Select(d => d.Section.Id.Key!));
        plan.NeedsConfirmation = deletes.Any(d => d.Why == DeleteReason.Cascade) || plan.Fixes.Count > 0 ||
                                 missing.Count > 0 || warnings.Count > 0 || mentions.Count > 0 ||
                                 plan.MissionMissing.Count > 0;
        plan.Command = EditCommand.Group(plan.Label, commands, session.Snapshot());
        return plan;
    }

    /// <summary>
    /// Plans a new nickname for an object, a zone or a light, also of the lower layer (decisions D12, D231, D232, D257 and
    /// D264). A nebula or a field gives its zone a new nickname, because the zone is the key of its section.
    /// </summary>
    public static ScenePlan Rename(EditSession session, SectionRef target, string newNickname)
    {
        var plan = new ScenePlan(SceneAction.Rename, "Rename");
        var item = ItemOf(session, target);
        var name = newNickname.Trim();
        if (Refusal(session, item, name) is { } refusal)
            return Refused(plan, refusal);
        var kind = SceneSections.KindOf(session, item)!.Value;
        var old = item.Id.Key!;
        var warnings = new List<Diagnostic>();
        var values = new List<EditCommand>();
        var keyed = new List<EditCommand>();
        if (SceneSections.ListOf(kind) is { } list)
        {
            foreach (var reference in session.Lists.ReferencesTo(list, old, item.Path))
            {
                if (reference.Section.Equals(item))
                    continue;
                var referrer = session.Get(reference.Section);
                if (referrer?.ReadOnlyReason is { } reason)
                {
                    warnings.Add(Diagnostic.Warning(reference.Section.Path,
                        $"{reference.Section.Id}: the editor cannot change the '{reference.Key}' line: {reason} The line names " +
                        "the old nickname" + (reference.Rule.Closed ? ", and the save stops until you fix it." : ".")) with
                    {
                        Target = reference.Section, Key = reference.Key,
                    });
                    continue;
                }
                if (PatchFormatter.KeyError(name, session.IsBinaryFile(reference.Section.Path)) is { } keyError)
                    return Refused(plan, Diagnostic.Error(reference.Section.Path, keyError));
                // A line that is the key of its own section renames that section, for example the zone of a nebula.
                if (reference.Index == 0 && EditSession.KeyNameOf(reference.Section.Id.Type) is { } keyName &&
                    SceneSections.Is(keyName, reference.Key))
                    keyed.Add(EditCommand.Rename(reference.Section, name));
                else
                    values.Add(EditCommand.SetValueAt(reference.Section, reference.Key, reference.Line, reference.Index,
                        new StringValue(name)));
            }
        }
        if (kind == SceneKind.Object)
        {
            // The gate locks of InitialWorld.ini hold the FLHash of the nickname (decision D257).
            foreach (var gateLock in GateLocks.Of(session, old))
            {
                if (session.Get(gateLock.Section)?.ReadOnlyReason is { } reason)
                    warnings.Add(Diagnostic.Warning(gateLock.Section.Path,
                        $"{SceneSections.FileName(gateLock.Section.Path)} locks this gate at the start of a new game by its " +
                        $"old hash. The editor cannot change that line: {reason}") with { Target = gateLock.Section });
                else
                    values.Add(EditCommand.SetValueAt(gateLock.Section, gateLock.Key, gateLock.Line, gateLock.Index,
                        GateLocks.ValueOf(name)));
            }
        }

        var mentions = new List<NamedMention>();
        if (SceneSections.IsNamed(kind))
        {
            foreach (var mention in session.StoryMentions(old))
            {
                if (SceneSections.FileName(mention.Path).Equals("KnowledgeMap.ini", StringComparison.OrdinalIgnoreCase))
                    warnings.Add(Diagnostic.Warning(mention.Path, $"KnowledgeMap.ini names {old}. After the rename, a text " +
                                                                  "does not reveal this item on the navigation map."));
                else
                    mentions.Add(new NamedMention(old, mention.Path, mention.Count));
            }
        }
        foreach (var mod in session.LaterModsThatChangeSection(item))
            warnings.Add(Diagnostic.Warning(item.Path, $"The mod {mod} loads later and changes {item.Id}. Its patch cannot " +
                                                       "find the section after the rename.") with { Target = item });
        if (kind == SceneKind.Zone)
        {
            foreach (var word in new[] { "Tradelane", "vignette" })
            {
                if (old.Contains(word, StringComparison.Ordinal) != name.Contains(word, StringComparison.Ordinal))
                    warnings.Add(Diagnostic.Warning(item.Path, $"Retail uses the word '{word}' in a zone nickname. A zone " +
                                                               "with 'Tradelane' can be a trade lane zone, and a zone with " +
                                                               "'vignette' is lawful. The rename changes this in retail.")
                        with { Target = item });
            }
        }
        if (SceneSections.IsNamed(kind))
            warnings.AddRange(DanglingWarnings(session, name, item.Path));
        // The mission lines that name the object follow the rename in the same entry (decision D668). The plan gives
        // their triggers as changes, so a question names the mission scripts that the rename changes.
        var triggers = new List<SectionRef>();
        var mission = kind == SceneKind.Object
            ? session.Missions.References.Rename(Missions.Editing.MissionReferences.ObjectKinds, old, name, warnings,
                triggers)
            : null;
        plan.Changes = triggers;

        var renamed = item with { Id = item.Id with { Key = name } };
        plan.Label = $"Rename {old} to {name}";
        plan.Warnings = warnings;
        plan.Mentions = mentions;
        plan.SelectAfter = renamed;
        plan.NeedsConfirmation = warnings.Count > 0 || mentions.Count > 0;
        // The order of review finding R21: no command changes a reference that a later command uses.
        plan.Command = EditCommand.Group(plan.Label,
            [.. values, .. keyed, EditCommand.Rename(item, name), .. mission == null ? [] : new[] { mission }],
            session.Snapshot());
        return plan;
    }

    /// <summary>
    /// Why an action cannot run on a section, from the checks that need no plan, or null: the section is not an item of a
    /// system file, it is a connection object (decision D253), it is read-only, or a duplicate of a nebula or a field
    /// (decision D260). The menus show it as the tooltip of a disabled item. A plan can still refuse for another reason.
    /// </summary>
    public static string? Unavailable(EditSession session, SceneAction action, SectionRef section)
    {
        if (action == SceneAction.Create)
            return null;
        var verb = action switch
        {
            SceneAction.Duplicate => "copy",
            SceneAction.Delete => "delete",
            _ => "rename",
        };
        if (session.Get(section) is not { } view || SceneSections.KindOf(session, section) is not { } kind)
            return NotAnItem(section, verb).Message;
        if (session.ReadOnlyReason is { } closed)
            return closed;
        switch (action)
        {
            case SceneAction.Rename:
                var item = ItemOf(session, section);
                if (session.Get(item) is not { } itemView)
                    return $"{section.Id}: the zone of this item is not in the system file.";
                if (itemView.ReadOnlyReason is { } reason)
                    return $"{item.Id}: {reason}";
                return session.InBase(item) && session.ChildCount(item) > 0
                    ? $"{item.Id}: the editor cannot rename a section of the lower layer with child sections."
                    : null;
            case SceneAction.Duplicate:
                if (SceneSections.ConnectionKind(session, view) is { } copied)
                    return SceneSections.ConnectionMessage("copy", section, view, copied);
                if (kind is SceneKind.Nebula or SceneKind.Asteroids || kind == SceneKind.Zone && HoldsVolume(session, section))
                    return $"{SceneSections.Label(kind, section)}: the editor copies nebulae and asteroid fields in milestone M26.";
                return session.OpenDocument(section.Path)?.ReadOnlyReason;
            default:
                if (SceneSections.ConnectionKind(session, view) is { } deleted)
                    return SceneSections.ConnectionMessage("delete", section, view, deleted);
                return view.ReadOnlyReason;
        }
    }

    /// <summary>Why a rename to a nickname cannot run, or null. The Hierarchy shows it while the user types.</summary>
    public static string? RenameProblem(EditSession session, SectionRef target, string newNickname) =>
        Refusal(session, ItemOf(session, target), newNickname.Trim())?.Message;

    /// <summary>The section that a rename changes: the zone of a nebula or a field, else the section. Its key is the
    /// nickname that the rename field starts with.</summary>
    public static SectionRef RenameTarget(EditSession session, SectionRef target) => ItemOf(session, target);

    /// <summary>The section that a rename changes: the zone of a nebula or a field, else the section.</summary>
    private static SectionRef ItemOf(EditSession session, SectionRef target) =>
        SceneSections.KindOf(session, target) is SceneKind.Nebula or SceneKind.Asteroids && target.Id.Key is { } zone &&
        session.Sections(target.Path).FirstOrDefault(s => SceneSections.Is(s.Id.Type, SceneSections.ZoneType) &&
                                                         string.Equals(s.Id.Key, zone, StringComparison.OrdinalIgnoreCase))
            is { } section
            ? section
            : target;

    /// <summary>Why a rename cannot run, or null (step 1 of section 12 of the M3 design).</summary>
    private static Diagnostic? Refusal(EditSession session, SectionRef item, string name)
    {
        Diagnostic Error(string message) => Diagnostic.Error(item.Path, message) with { Target = item };
        // A nickname that another section has gives the rule of the command layer, so an agent gets name_taken.
        Diagnostic Taken(string message) => Error(message) with { Rule = Missions.Editing.MissionErrors.NameTaken };
        if (SceneSections.KindOf(session, item) is not { } kind || kind is SceneKind.Nebula or SceneKind.Asteroids ||
            session.Get(item) is not { } view || item.Id.Key is not { } old)
            return Error($"{item.Id}: the Scene can rename only an object, a zone or a light of a system file.");
        if (view.ReadOnlyReason is { } reason)
            return Error($"{item.Id}: {reason}");
        if (session.InBase(item) && session.ChildCount(item) > 0)
            return Error($"{item.Id}: the editor cannot rename a section of the lower layer with child sections.");
        if (name.Length == 0)
            return Error("Type a nickname.");
        if (name.Equals(old, StringComparison.OrdinalIgnoreCase))
            return Error(name == old
                ? "Type a new nickname."
                : "The new nickname differs only in letter case. A patch cannot change only the letter case of a key.");
        if (PatchFormatter.KeyError(name, session.IsBinaryFile(item.Path)) is { } keyError)
            return Error(keyError);
        if (kind == SceneKind.Light)
        {
            if (session.Nicknames.LightTaken(item.Path, name, item) is not null)
                return Taken($"Another light of {SceneSections.FileName(item.Path)} has the nickname {name}.");
        }
        else if (session.Nicknames.Taken(name, item) is { } taken)
            return Taken(TakenMessage(session, name, taken));
        var before = session.OpenDocument(item.Path)?.BaseIdOf(item.Id);
        if (!string.Equals(before?.Key, name, StringComparison.OrdinalIgnoreCase) &&
            session.BaseHasKey(item.Path, item.Id.Type, name))
            return Taken($"A section of the lower layer of {SceneSections.FileName(item.Path)} has the nickname {name}. A " +
                         "patch would then find two sections with it.");
        return null;
    }

    /// <summary>The message of a nickname that another object or zone has (decisions D240 and D265).</summary>
    private static string TakenMessage(EditSession session, string name, SceneName taken)
    {
        var what = taken.IsZone ? "zone" : "object";
        var system = session.SystemNicknameOf(taken.Section.Path) ?? SceneSections.FileName(taken.Section.Path);
        return string.Equals(taken.Name, name, StringComparison.OrdinalIgnoreCase)
            ? $"The {what} {taken.Name} in {system} has the nickname {name}. Give the item another nickname."
            : $"The {what} {taken.Name} in {system} has the FLHash of {name}. Give the item another nickname.";
    }

    /// <summary>The warning of a new name that dangling lines name (review finding R29).</summary>
    internal static IReadOnlyList<Diagnostic> DanglingWarnings(EditSession session, string name, string path)
    {
        var dangling = session.Nicknames.Dangling(name, path);
        return dangling.Count == 0
            ? []
            : [Diagnostic.Warning(path, $"{dangling.Count} {(dangling.Count == 1 ? "line names" : "lines name")} {name}, but no " +
                                        "item has this name. After the edit, they name this item.")];
    }

    /// <summary>
    /// The base warnings of an object that goes (decision D254): its system loses the last object that docks with a base
    /// of the system, or the last object with <c>base</c> of a base of the system.
    /// </summary>
    private static IEnumerable<Diagnostic> BaseWarnings(EditSession session, SectionRef section, IReadOnlySet<SectionRef> gone)
    {
        var view = session.Get(section)!;
        foreach (var (key, message) in new[]
                 {
                     ("dock_with", "No object in {0} docks with {1}. The game puts the player at the center of {0} after a launch."),
                     ("base", "No object in {0} has 'base = {1}'. The navigation map does not list the base, and its rooms " +
                              "show no planet."),
                 })
        {
            if (SceneSections.Last(view, key) is not { } baseName ||
                session.Lists.List("base")?.Find(baseName)?.Section is not { } baseSection ||
                session.Get(baseSection) is not { } baseView || SceneSections.Last(baseView, "system") is not { } system ||
                session.SystemPath(system) is not { } systemPath || !VfsPath.Comparer.Equals(systemPath, section.Path))
                continue;
            var stays = session.Lists.ReferencesTo("base", baseName)
                .Any(r => SceneSections.Is(r.Key, key) && VfsPath.Comparer.Equals(r.Section.Path, systemPath) &&
                          !gone.Contains(r.Section));
            if (!stays)
                yield return Diagnostic.Warning(section.Path, string.Format(CultureInfo.InvariantCulture, message, system, baseName))
                    with { Target = baseSection };
        }
    }

    /// <summary>The names that a section gives to the lists, with the scope of each.</summary>
    private static IEnumerable<(string List, string Name, string? Scope)> NamesOf(EditSession session, SectionRef section)
    {
        var working = session.WorkingSection(section)!;
        foreach (var definition in session.Lists.SourcesOf(section.Path, working.Name))
        {
            var source = definition.Source!;
            foreach (var name in DataLists.Names(working, source))
                yield return (definition.Id, name, source.Scoped ? section.Path : null);
        }
    }

    /// <summary>True when a system file other than <paramref name="system"/> owns the nebula or field file and has a zone
    /// with the nickname.</summary>
    private static bool OtherOwnerHasZone(EditSession session, string file, string system, string zone) =>
        session.DataFiles.OwnersOf(file)
            .Where(o => !VfsPath.Comparer.Equals(o, system))
            .Any(o => session.Sections(o).Any(s => SceneSections.Is(s.Id.Type, SceneSections.ZoneType) &&
                                                   string.Equals(s.Id.Key, zone, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The index among all lines of a section of the line <paramref name="line"/> of a key, or -1.</summary>
    private static int LineIndex(SectionView view, string key, int line)
    {
        var n = 0;
        for (var i = 0; i < view.Entries.Count; i++)
        {
            if (SceneSections.Is(view.Entries[i].Name, key) && n++ == line)
                return i;
        }
        return -1;
    }

    private static void AddFix(Dictionary<SectionRef, List<(int, int, string, string)>> fixes, SectionRef section, int first,
        int count, string key, string text)
    {
        if (!fixes.TryGetValue(section, out var list))
            fixes[section] = list = [];
        list.Add((first, count, key, text));
    }

    /// <summary>Why the copy builder leaves out a line (see <see cref="CopyLines"/>).</summary>
    internal enum LeftOutReason
    {
        /// <summary>The <c>base</c> line of an object: a base has one object in space (decision D259).</summary>
        Base,

        /// <summary>A <c>parent</c> or a <c>ring</c> line that names an item that the copy does not take.</summary>
        Outside,
    }

    /// <summary>The lines of a section as line views, in order.</summary>
    internal static IEnumerable<EntryView> Lines(Section section) => section.Select(e => new EntryView(e.Name, e.ToArray()));

    /// <summary>The new names of the copied objects and zones, by their old names without letter case.</summary>
    internal static (Dictionary<string, string> Objects, Dictionary<string, string> Zones) CopiedNames(
        IEnumerable<SectionRef> sections, IReadOnlyDictionary<SectionRef, string> names)
    {
        var objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var zones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            if (section.Id.Key is not { } key || !names.TryGetValue(section, out var name))
                continue;
            if (SceneSections.Is(section.Id.Type, SceneSections.ObjectType))
                objects.TryAdd(key, name);
            else if (SceneSections.Is(section.Id.Type, SceneSections.ZoneType))
                zones.TryAdd(key, name);
        }
        return (objects, zones);
    }

    /// <summary>
    /// The copy builder (review finding R9 of the M23 design): the lines of a copy of an object, a zone or a light.
    /// Duplicate, the draft of a prop group and its placement use it. So each copy follows one set of line rules:
    /// <list type="bullet">
    /// <item>The key line <paramref name="from"/> becomes the line <paramref name="to"/> with the new name. For example,
    /// <c>nickname</c> becomes <c>member</c> in a prop group.</item>
    /// <item>A copy of an object has no <c>base</c> line (decision D259): a base has one object in space.</item>
    /// <item>Value 0 of <c>parent</c> and of <c>ring</c> of an object gets the new name of a copied object or zone. A
    /// line that names an item outside the copy keeps its name when <paramref name="keepOutside"/> is true. Else the
    /// line goes (decision D286).</item>
    /// <item>Each other value goes through <paramref name="value"/>, for example for a copy of its text.</item>
    /// </list>
    /// </summary>
    /// <param name="objects">The new names of the copied objects, by their old names.</param>
    /// <param name="zones">The new names of the copied zones, by their old names.</param>
    /// <param name="leftOut">Gets each line that the copy leaves out, with the reason.</param>
    internal static List<EntryView> CopyLines(string type, IEnumerable<EntryView> source, string from, string to,
        string name, IReadOnlyDictionary<string, string> objects, IReadOnlyDictionary<string, string> zones,
        bool keepOutside, Func<string, int, ValueBase, ValueBase> value, Action<EntryView, LeftOutReason> leftOut)
    {
        var isObject = SceneSections.Is(type, SceneSections.ObjectType);
        var result = new List<EntryView>();
        foreach (var entry in source)
        {
            if (SceneSections.Is(entry.Name, from))
            {
                result.Add(new EntryView(to, [new StringValue(name), .. entry.Values.Skip(1)]));
                continue;
            }
            if (isObject && SceneSections.Is(entry.Name, "base"))
            {
                leftOut(entry, LeftOutReason.Base);
                continue;
            }
            var names = !isObject ? null
                : SceneSections.Is(entry.Name, "parent") ? objects
                : SceneSections.Is(entry.Name, "ring") ? zones
                : null;
            var values = new List<ValueBase>(entry.Values.Count);
            if (names != null && entry.Values.Count > 0)
            {
                if (names.TryGetValue(entry.Values[0].ToString().Trim(), out var copied))
                    values.Add(new StringValue(copied));
                else if (keepOutside)
                    values.Add(entry.Values[0]);
                else
                {
                    leftOut(entry, LeftOutReason.Outside);
                    continue;
                }
            }
            for (var i = values.Count; i < entry.Values.Count; i++)
                values.Add(value(entry.Name, i, entry.Values[i]));
            result.Add(new EntryView(entry.Name, values));
        }
        return result;
    }

    /// <summary>The set of a duplicate: the section, and each object of the file whose <c>parent</c> names an object of
    /// the set, again and again. The draft of a prop group takes the same set.</summary>
    internal static void Collect(EditSession session, SectionRef section, List<SectionRef> set)
    {
        if (set.Contains(section))
            return;
        set.Add(section);
        if (!SceneSections.Is(section.Id.Type, SceneSections.ObjectType) || section.Id.Key is not { } key)
            return;
        foreach (var child in session.Sections(section.Path))
        {
            if (SceneSections.Is(child.Id.Type, SceneSections.ObjectType) && session.Get(child) is { } view &&
                string.Equals(SceneSections.Last(view, "parent"), key, StringComparison.OrdinalIgnoreCase))
                Collect(session, child, set);
        }
    }

    /// <summary>True when a <c>[Nebula]</c> or an <c>[Asteroids]</c> section of the file names the zone.</summary>
    internal static bool HoldsVolume(EditSession session, SectionRef zone) =>
        session.Sections(zone.Path).Any(s => (SceneSections.Is(s.Id.Type, "Nebula") || SceneSections.Is(s.Id.Type, "Asteroids")) &&
                                             string.Equals(s.Id.Key, zone.Id.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The lines of a new section from their text, as the merge reads a patch line.</summary>
    internal static List<EntryView>? Entries(IEnumerable<(string Name, string Text)> lines, bool binary, out string? error)
    {
        var result = new List<EntryView>();
        foreach (var (name, text) in lines)
        {
            if (!PatchFormatter.TryReadLine(name, text, null, binary, out var values, out error))
                return null;
            result.Add(new EntryView(name, values));
        }
        error = null;
        return result;
    }

    private static Diagnostic NotAnItem(SectionRef section, string verb) =>
        Diagnostic.Error(section.Path, $"{section.Id}: the Scene can {verb} only an object, a zone, a light, a nebula or an " +
                                       "asteroid field of a system file.") with { Target = section };

    internal static ScenePlan Refused(ScenePlan plan, params Diagnostic[] refusals)
    {
        plan.Refusals = refusals;
        plan.Command = null;
        return plan;
    }

    private static SceneTemplateDefinition? Definition(EditSession session, SceneTemplate template) =>
        session.Lists.RuleData.SceneTemplates.FirstOrDefault(t => t.Id == TemplateId(template));

    private static string TemplateId(SceneTemplate template) => template.ToString().ToLowerInvariant();

    internal static SceneTemplate? TemplateOf(string id) =>
        Enum.TryParse<SceneTemplate>(id, true, out var template) && template is not (SceneTemplate.Zone or SceneTemplate.Light)
            ? template
            : null;
}
