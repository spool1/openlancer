using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Prefabs;

/// <summary>A field of a linked instance with its value now: the override, the per-instance value or the default of
/// the prefab.</summary>
/// <param name="Override">True when the instance overrides the default of the prefab (section 5.6 of the M6b
/// design). A per-instance field never shows as an override.</param>
public sealed record LinkedField(PrefabFieldDef Field, IReadOnlyList<PrefabRow> Rows, bool Override)
{
    /// <summary>The value as one text: each row in its text form, with "; " between the rows, or "None".</summary>
    public string Text => Rows.Count == 0 ? "None" : string.Join("; ", Rows.Select(r => r.Format()));
}

/// <summary>
/// A linked prefab instance of a script (sections 5.1 and 5.5 of the M6b design): its record, its prefab, the value of
/// each field, its member sections, and the state of rules P01 to P03.
/// </summary>
public sealed class LinkedInstance
{
    internal LinkedInstance(InstanceRecord record, PrefabAsset? asset, IReadOnlyList<LinkedField> fields,
        IReadOnlyDictionary<string, MissionSection> sections)
    {
        Record = record;
        Asset = asset;
        Fields = fields;
        Sections = sections;
    }

    public InstanceRecord Record { get; }

    /// <summary>The prefab of the record, or null when no mod has it (rule P02).</summary>
    public PrefabAsset? Asset { get; }

    /// <summary>The template of the prefab, or null when no mod has it or its file has a problem.</summary>
    public PrefabTemplate? Template => Asset?.Template;

    /// <summary>The fields of the template with their values. Empty without a template.</summary>
    public IReadOnlyList<LinkedField> Fields { get; }

    /// <summary>The member section of each role that W has.</summary>
    public IReadOnlyDictionary<string, MissionSection> Sections { get; }

    /// <summary>True when the lines of the members differ from the last compile: a hand edit (rule P01).</summary>
    public bool HandEdited { get; init; }

    /// <summary>True when the prefab changed after the last compile (rule P03).</summary>
    public bool Outdated { get; init; }

    /// <summary>True when the editor cannot compile the instance: no mod has its prefab, or the file of the prefab has a
    /// problem (rule P02).</summary>
    public bool Missing => Template == null;

    /// <summary>The number of overrides: the fields that differ from the prefab and the removed members.</summary>
    public int OverrideCount => Fields.Count(f => f.Override) + Record.Removed.Count;

    /// <summary>The field of an id, or null.</summary>
    public LinkedField? Field(string id) => Fields.FirstOrDefault(f => f.Field.Id == id);

    /// <summary>The input of the compile of the record.</summary>
    public static PrefabInput InputOf(InstanceRecord record, PrefabTemplate template) => new(record.Id)
    {
        Values = record.Values.Concat(record.Overrides)
            .Where(v => template.Field(v.Field) != null)
            .GroupBy(v => v.Field)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PrefabRow>)g.First().Rows
                .Select(r => PrefabRow.Parse(template.Field(g.Key)!.Kind, r)).ToList()),
        Ports = record.Ports.GroupBy(p => p.Port).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Trigger).ToList()),
        Members = record.Members.GroupBy(m => m.Role).ToDictionary(g => g.Key, g => g.First().Nickname),
        Removed = record.Removed.ToHashSet(),
    };

    /// <summary>The values of the fields of a record and a template.</summary>
    internal static IReadOnlyList<LinkedField> FieldsOf(InstanceRecord record, PrefabTemplate template) =>
        template.Fields.Select(field =>
        {
            var own = field.PerInstance
                ? record.Values.FirstOrDefault(v => v.Field == field.Id)
                : record.Overrides.FirstOrDefault(v => v.Field == field.Id);
            return new LinkedField(field,
                own != null ? own.Rows.Select(r => PrefabRow.Parse(field.Kind, r)).ToList() : field.Default,
                !field.PerInstance && own != null);
        }).ToList();
}

/// <summary>
/// The linked prefab instances of a script (sections 5.1, 5.5 and 6.10 of the M6b design): the records of the graph file
/// with their prefabs and member sections, the matched instances of the other triggers, and the warnings P01, P02 and
/// P03. It also gives the links of the members through the outputs of their prefabs (section 5.3).
/// </summary>
public static class PrefabLinks
{
    /// <summary>The text of P01 (section 6.10 of the M6b design).</summary>
    public static string HandEditMessage(string instance) =>
        $"The lines of prefab instance '{instance}' differ from its prefab. A change of the prefab does not change this " +
        "instance. Unpack the instance, or compile it again.";

    /// <summary>The info text of an override (section 8.11 of the M6b design, UI rule 10). Each override has the info
    /// icon with it.</summary>
    public static readonly HelpText OverrideHelp = new("Override",
        "A field of this instance that differs from the prefab. Only this instance has the value.",
        "The lines of this instance. The prefab does not change until you apply the value.");

    /// <summary>The note of a compile that replaced a hand edit of the lines of an instance (rule P01).</summary>
    public static string ReplacedHandEditNote(string instance, string script) =>
        $"The lines of prefab instance '{instance}' in {script} differed from its prefab. The compile replaced them with " +
        "the lines of the prefab. Undo restores them.";

    /// <summary>The text of P02.</summary>
    public static string MissingMessage(string instance, string prefab) =>
        $"Prefab instance '{instance}' names prefab '{prefab}', which no mod has. The triggers stay as they are. Unpack " +
        "the instance, or add the prefab again.";

    /// <summary>The text of P02 for a prefab whose file has a problem.</summary>
    public static string BrokenMessage(string instance, string prefab) =>
        $"Prefab instance '{instance}' names prefab '{prefab}', whose file has a problem. The triggers stay as they are. " +
        "Correct the prefab file, or unpack the instance.";

    /// <summary>Why a member section of a linked instance does not take line edits (sections 8.1 and 8.11 of the M6b
    /// design, decision D660). A delete of the member is a removed-member override (decision D676 c).</summary>
    public static string MemberReason(string prefab, string instance) =>
        $"This section comes from prefab '{prefab}' (instance '{instance}'). Change a field of the instance, open the " +
        "prefab, or unpack the instance.";

    /// <summary>The tooltip of a line of a member of a linked instance in a raw INI view (section 8.11 of the M6b
    /// design).</summary>
    public static string LineNote(string prefab) => $"This line comes from prefab '{prefab}'.";

    /// <summary>Why a raw INI edit cannot change a line of a member of a linked instance (section 8.8 of the M6b
    /// design).</summary>
    public static string LineEditMessage(string prefab) =>
        $"{LineNote(prefab)} Change a field of the instance, open the prefab, or unpack the instance.";

    /// <summary>The name of the prefab of a linked instance: the name of its asset, or the id in its record when no mod
    /// has the prefab.</summary>
    public static string PrefabNameOf(LinkedInstance linked) => linked.Asset?.Name ?? PrefabName(linked.Record.Prefab);

    /// <summary>
    /// The notes of the lines of the member sections of the linked instances in a raw text (section 8.8 of the M6b
    /// design): the header and each line of a member get the note "This line comes from prefab '{name}'.". The raw
    /// editor shows these lines dim, with the note as the tooltip. A raw edit of such a line stops.
    /// </summary>
    /// <param name="text">The raw text of the whole script, or of the INI block of a page.</param>
    public static IEnumerable<(int Line, string Note)> MemberLines(RawText text, MissionAnalysis analysis)
    {
        foreach (var instance in analysis.Prefabs?.Instances ?? [])
        {
            if (instance.Linked is not { } linked)
                continue;
            var note = LineNote(PrefabNameOf(linked));
            foreach (var section in linked.Sections.Values)
            {
                if (text.SectionOf(section.Ref.Id) is not (>= 0 and var k))
                    continue;
                yield return (text.HeaderLine(k), note);
                for (var entry = 0; entry < text.EntryCount(k); entry++)
                    yield return (text.EntryLine(k, entry), note);
            }
        }
    }

    /// <summary>The text of P03.</summary>
    public static string OutdatedMessage(string prefab, string instance) =>
        $"Prefab '{prefab}' changed after the last compile of instance '{instance}'. The instance does not have the " +
        "changes. Compile the instance again.";

    /// <summary>The name of the step of a linked instance with the rules P01 to P03.</summary>
    public const string StepName = "Same lines as the prefab";

    /// <summary>Why the step of a linked instance is necessary.</summary>
    public const string StepReason = "A change of the prefab reaches only the instances that have the lines of the prefab.";

    /// <summary>The rules of the linked instances.</summary>
    public static readonly IReadOnlyList<string> Rules = ["P01", "P02", "P03"];

    /// <summary>
    /// The analysis of a script with its linked instances: each record of the graph file is a linked instance, the
    /// matcher leaves out its sections, and the warnings P01 to P03 join the problems. A problem of a record has the
    /// origin of the active mod, because the graph file is a file of that mod.
    /// </summary>
    /// <param name="resolve">Gives the prefab of the text of a record, or null when no mod has it.</param>
    /// <param name="activeMod">The active mod, or null.</param>
    public static MissionAnalysis Apply(MissionAnalysis analysis, GraphFile graph, Func<string, PrefabAsset?> resolve,
        string? activeMod)
    {
        analysis = analysis.Matched ?? analysis;
        if (graph.Instances.Count == 0)
            return analysis;
        var index = analysis.Index;
        var linked = new List<MissionPrefabInstance>();
        var sectionsOfLinks = new HashSet<MissionSection>();
        var problems = new List<Diagnostic>();
        foreach (var record in graph.Instances)
        {
            var asset = resolve(record.Prefab);
            var template = asset?.Template;
            var sections = new Dictionary<string, MissionSection>();
            foreach (var member in record.Members.Where(m => !record.Removed.Contains(m.Role)))
            {
                if (Find(index.Document, template?.Section(member.Role)?.Type, member.Nickname) is { } section &&
                    !sectionsOfLinks.Contains(section))
                    sections.TryAdd(member.Role, section);
            }
            var present = record.Members.Where(m => !record.Removed.Contains(m.Role)).ToList();
            var handEdited = record.LinesHash != null &&
                             (present.Any(m => !sections.ContainsKey(m.Role)) ||
                              PrefabCompiler.LinesHash(present.Select(m => sections[m.Role].Section)) != record.LinesHash);
            var outdated = template != null && record.PrefabHash != null && record.PrefabHash != template.ContentHash;
            var instance = Instance(index, record, asset, sections);
            var link = new LinkedInstance(record, asset, template != null ? LinkedInstance.FieldsOf(record, template) : [], sections)
            {
                HandEdited = handEdited,
                Outdated = outdated,
            };
            var made = new MissionPrefabInstance(instance.Definition, instance.Core, instance.CoreSection, instance.Members,
                instance.Claimed) { Linked = link };
            linked.Add(made);
            sectionsOfLinks.UnionWith(sections.Values);
            var target = sections.Values.OrderBy(s => s.Index).FirstOrDefault();
            if (template == null)
                problems.Add(Problem("P02", target, record.Id, asset == null
                    ? MissingMessage(record.Id, PrefabName(record.Prefab))
                    : BrokenMessage(record.Id, asset.Name)));
            else if (handEdited)
                problems.Add(Problem("P01", target, record.Id, HandEditMessage(record.Id)));
            if (outdated && !handEdited)
                problems.Add(Problem("P03", target, record.Id, OutdatedMessage(asset!.Name, record.Id)));
        }
        var matched = MissionPrefabs.Retail.Match(index, sectionsOfLinks);
        var instances = linked.Concat(matched.Instances).ToList();
        var claims = new Dictionary<MissionTrigger, MissionPrefabInstance>();
        foreach (var instance in instances)
        {
            foreach (var trigger in instance.Claimed)
                claims.TryAdd(trigger, instance);
        }
        var origin = activeMod != null ? MessageOrigin.Mod(activeMod, ModEvidence.File) : MessageOrigin.SomeMod;
        return analysis with
        {
            Prefabs = new MissionPrefabResult(instances, claims),
            Problems = [.. analysis.Problems, .. problems.Select(p => p with { Origin = origin })],
            Matched = analysis,
        };
    }

    /// <summary>The section of a type and a nickname, without letter case. Without a type, a trigger comes first, then
    /// another section.</summary>
    internal static MissionSection? Find(MissionDocument document, string? type, string nickname)
    {
        bool Named(MissionSection s) => s.Kind == MissionSectionKind.Trigger
            ? string.Equals(TriggerRules.Nickname(s.Section), nickname, StringComparison.OrdinalIgnoreCase)
            : string.Equals(s.Nickname, nickname, StringComparison.OrdinalIgnoreCase);
        if (type != null)
            return document.Sections.FirstOrDefault(s => MissionIndex.Is(s.Type, type) && Named(s));
        return document.OfKind(MissionSectionKind.Trigger).FirstOrDefault(Named) ??
               document.Sections.FirstOrDefault(s => s.Kind != MissionSectionKind.Trigger && Named(s));
    }

    /// <summary>The name of the prefab of a record text: its id.</summary>
    private static string PrefabName(string prefab) => PrefabRef.TryParse(prefab)?.Id ?? prefab;

    private static Diagnostic Problem(string rule, MissionSection? target, string instance, string message) =>
        new(DiagnosticSeverity.Warning, MissionChecks.Source, message)
        {
            Target = target?.Ref,
            Rule = rule,
            Subject = $"{rule}|{instance}",
        };

    /// <summary>The parts of the instance of a record: its definition, its core, its members and its claims.</summary>
    private static (MissionPrefabDefinition Definition, MissionTrigger? Core, MissionSection? CoreSection,
        IReadOnlyDictionary<string, IReadOnlyList<MissionTrigger>> Members, IReadOnlyList<MissionTrigger> Claimed)
        Instance(MissionIndex index, InstanceRecord record, PrefabAsset? asset, IReadOnlyDictionary<string, MissionSection> sections)
    {
        var bySection = index.Triggers.ToDictionary(t => t.Section);
        var ordered = record.Members.Where(m => sections.ContainsKey(m.Role)).Select(m => (m.Role, Section: sections[m.Role])).ToList();
        var members = new Dictionary<string, IReadOnlyList<MissionTrigger>>();
        var claimed = new List<MissionTrigger>();
        foreach (var (role, section) in ordered)
        {
            if (!bySection.TryGetValue(section, out var trigger))
                continue;
            members[role] = [trigger];
            claimed.Add(trigger);
        }
        var core = claimed.FirstOrDefault();
        var coreSection = ordered.Select(o => o.Section).FirstOrDefault();
        return (DefinitionOf(record, asset), core, coreSection, members, claimed);
    }

    /// <summary>
    /// The prefab definition of a linked instance: the definition of the matcher for a built-in prefab, so the steps of
    /// the prefab work. A prefab asset of a mod gets a definition from its template: its name and its help, and the steps
    /// of its built-in source when the template has the roles of those steps.
    /// </summary>
    internal static MissionPrefabDefinition DefinitionOf(InstanceRecord record, PrefabAsset? asset)
    {
        var prefab = PrefabRef.TryParse(record.Prefab);
        var template = asset?.Template;
        if (prefab is { Builtin: true } && MissionPrefabs.Retail.Definition(prefab.Id) is { } builtin)
            return builtin;
        var name = asset?.Name ?? PrefabName(record.Prefab);
        var help = template?.HelpText ?? new HelpText(name,
            "A behavior prefab that no mod has now. The instance keeps its lines.",
            "The sections of the instance. The game does not read the prefab.");
        var roles = template?.Sections.Select(s => s.Role).ToList() ?? record.Members.Select(m => m.Role).ToList();
        var source = template?.VariantOf is { } variant && PrefabRef.TryParse(variant) is { Builtin: true } from
            ? MissionPrefabs.Retail.Definition(from.Id)
            : null;
        var steps = source?.Steps.Where(s => s.Requires is null or "core" || roles.Contains(s.Requires)).ToList() ?? [];
        return new MissionPrefabDefinition(prefab?.Id ?? record.Prefab, name, template?.Kind ?? MissionPrefabKind.Beat, 0, help,
            new PrefabCore(), roles.Select(r => new PrefabMemberRule(r, r, "linked", false, 0)).ToList(), [], steps,
            true, roles.FirstOrDefault() ?? "core");
    }

    // ---- The outputs of the members (section 5.3 of the M6b design) ----

    /// <summary>The reference, the linked instance and the role of a member trigger of the script of an analysis, or
    /// null for a trigger that is not a member of a linked instance. A removed member is not a member.</summary>
    public static (InstanceRef Instance, LinkedInstance Linked, string Role)? MemberOf(MissionAnalysis analysis,
        MissionTrigger trigger)
    {
        if (analysis.Prefabs?.Of(trigger) is not { Linked: { } linked })
            return null;
        var role = linked.Record.Members.FirstOrDefault(m => !linked.Record.Removed.Contains(m.Role) &&
                                                             m.Nickname.Equals(trigger.Nickname, StringComparison.OrdinalIgnoreCase))?.Role;
        return role == null ? null : (new InstanceRef(analysis.Script.Path, linked.Record.Id), linked, role);
    }

    /// <summary>The port of a role of a template whose lines have the key of a link kind: <c>Act_ActTrig</c> for a start,
    /// <c>Act_DeactTrig</c> for a stop. Null when the role has no such port.</summary>
    public static PrefabPort? PortOf(PrefabTemplate template, string role, LinkKind kind)
    {
        var key = kind == LinkKind.Stop ? "Act_DeactTrig" : "Act_ActTrig";
        return template.Ports.FirstOrDefault(p => p.Role == role && template.Section(role)?.Lines
            .Any(l => MissionIndex.Is(l.Key, key) && PrefabSlot.Of(l.Value).Any(slot => slot.Port == p.Id)) == true);
    }

    /// <summary>The triggers of a port of a record, in order.</summary>
    public static List<string> LinksOf(InstanceRecord record, string port) =>
        record.Ports.Where(p => p.Port == port).Select(p => p.Trigger).ToList();

    /// <summary>
    /// The command of a new link from a trigger to a target, for the link gesture of the mission graph and for the
    /// trigger tools of an agent. A plain trigger gets <see cref="LinkTriggers"/>. The lines of a member of a linked
    /// instance come from its prefab, so the member links through the output of its role: <see cref="SetPrefabPort"/>
    /// with the target added. Null for a member without such an output, and for a restart of a member.
    /// </summary>
    /// <param name="analysis">The analysis of the script of the trigger, with its linked instances.</param>
    public static MissionCommand? LinkCommand(MissionAnalysis analysis, MissionTrigger source, string target, LinkKind kind)
    {
        if (MemberOf(analysis, source) is not var (instance, linked, role))
            return new LinkTriggers(RefOf(source), target, kind);
        return kind != LinkKind.Restart && linked.Template is { } template && PortOf(template, role, kind) is { } port
            ? new SetPrefabPort(instance, port.Id, [.. LinksOf(linked.Record, port.Id), target])
            : null;
    }

    /// <summary>
    /// The command that removes the link from a trigger to a target. A member of a linked instance whose output starts or
    /// stops the target gets <see cref="SetPrefabPort"/> without the target, as the gesture "Remove link" of the mission
    /// graph does. Each other link gets <see cref="UnlinkTriggers"/>. That command refuses a line of a member with the
    /// reason.
    /// </summary>
    /// <param name="analysis">The analysis of the script of the trigger, with its linked instances.</param>
    public static MissionCommand UnlinkCommand(MissionAnalysis analysis, MissionTrigger source, string target, LinkKind kind)
    {
        if (kind != LinkKind.Restart && MemberOf(analysis, source) is var (instance, linked, role) &&
            linked.Template is { } template && PortOf(template, role, kind) is { } port)
        {
            var links = LinksOf(linked.Record, port.Id);
            if (links.RemoveAll(t => t.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
                return new SetPrefabPort(instance, port.Id, links);
        }
        return new UnlinkTriggers(RefOf(source), target, kind);
    }

    /// <summary>The reference of a trigger by its nickname.</summary>
    private static TriggerRef RefOf(MissionTrigger trigger) => new(trigger.Section.Ref.Path, trigger.Nickname);
}
