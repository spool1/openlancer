using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>The kinds of item references (section 4.0.7 of the M6b design). The comment of each kind gives its
/// form.</summary>
public enum ItemRefKind
{
    /// <summary><c>system:&lt;nickname&gt;</c>, a star system.</summary>
    System,

    /// <summary><c>obj:&lt;nickname&gt;</c>, an object of a system file.</summary>
    Object,

    /// <summary><c>zone:&lt;nickname&gt;</c>, a zone of a system file.</summary>
    Zone,

    /// <summary><c>light:&lt;system&gt;/&lt;nickname&gt;</c>, a light of a system file.</summary>
    Light,

    /// <summary><c>nebula:&lt;zone&gt;</c>, the <c>[Nebula]</c> section of a zone.</summary>
    Nebula,

    /// <summary><c>field:&lt;zone&gt;</c>, the <c>[Asteroids]</c> section of a zone.</summary>
    Field,

    /// <summary><c>base:&lt;nickname&gt;</c>, a base.</summary>
    Base,

    /// <summary><c>list:&lt;list id&gt;/&lt;value&gt;</c>, the section of an entry of a data list.</summary>
    ListEntry,

    /// <summary><c>sec:&lt;install path&gt;#&lt;section&gt;</c>, any section of W.</summary>
    Section,

    /// <summary><c>msn:&lt;install path&gt;</c>, a mission script.</summary>
    Script,

    /// <summary><c>trg:&lt;install path&gt;#&lt;trigger&gt;</c>, a trigger of a mission script.</summary>
    Trigger,

    /// <summary><c>line:&lt;install path&gt;#&lt;section&gt;/&lt;row&gt;</c>, a line of a mission section.</summary>
    Line,

    /// <summary><c>label:&lt;install path&gt;#&lt;label&gt;</c>, a label of a mission script.</summary>
    Label,

    /// <summary><c>inst:&lt;install path&gt;#&lt;id&gt;</c>, a prefab instance of a mission script.</summary>
    Instance,

    /// <summary><c>act:&lt;install path&gt;#&lt;act&gt;</c>, an act of the graph file of a mission script.</summary>
    Act,

    /// <summary><c>prefab:builtin:&lt;id&gt;</c> or <c>prefab:mod:&lt;id&gt;</c>, a behavior prefab.</summary>
    Prefab,

    /// <summary><c>thn:&lt;install path&gt;</c>, a cutscene script.</summary>
    Cutscene,

    /// <summary><c>thn:&lt;install path&gt;#entity/&lt;name&gt;</c>, an entity of a cutscene.</summary>
    CutsceneEntity,

    /// <summary><c>thn:&lt;install path&gt;#event/&lt;index&gt;</c>, an event of a cutscene by its place in the
    /// event list (decision D767 of the MCP1 design).</summary>
    CutsceneEvent,

    /// <summary><c>group:&lt;nickname&gt;</c> or <c>group:&lt;mod id&gt;/&lt;nickname&gt;</c>, a prop group.</summary>
    Group,

    /// <summary><c>asset:&lt;kind&gt;/&lt;id&gt;</c>, an asset of the Assets panel.</summary>
    Asset,
}

/// <summary>
/// An item reference in its parts (see <see cref="ItemRefs"/>). <see cref="ToString"/> gives the text form.
/// </summary>
/// <param name="Kind">The kind of the item.</param>
/// <param name="Name">The name part: a nickname, a value, an id or a name. Empty for a kind without one.</param>
public sealed record ItemReference(ItemRefKind Kind, string Name)
{
    /// <summary>The install path in normal form, or the template of a prefab (<c>prefab:mod:&lt;id&gt;</c>) for a
    /// trigger or a line in prefab mode. Null for a kind without a file.</summary>
    public string? Path { get; init; }

    /// <summary>The first part of a kind with two parts: the system of a light, the list of an entry, the source of a
    /// prefab (<c>builtin</c> or <c>mod</c>), the mod of a prop group or the kind of an asset. Else null.</summary>
    public string? Scope { get; init; }

    /// <summary>The section of a <c>sec:</c> or <c>line:</c> reference, else null.</summary>
    public SectionIdentity? Section { get; init; }

    /// <summary>The row of a <c>line:</c> reference, or the index of a cutscene event, else null.</summary>
    public int? Row { get; init; }

    public override string ToString() => ItemRefs.Format(this);
}

/// <summary>
/// The item references of the command layer (section 4.0.7 of the M6b design, decision D716 of the MCP1 design): one
/// text form for each item, in requests and in results. A result gives the most specific form (see
/// <see cref="Canonical"/>), so a caller can pass it into its next request.
/// </summary>
/// <remarks>
/// <para>An install path in a reference goes through <see cref="InstallPaths.Parse"/>. It can use <c>\</c> or
/// <c>/</c>, and it matches without letter case.</para>
/// <para>The section part is <c>&lt;type&gt;/&lt;key&gt;</c>, or <c>&lt;type&gt;</c> for a section without a key.
/// <c>~&lt;n&gt;</c> follows when the identity repeats: <c>n</c> is the ordinal of <see cref="SectionIdentity"/>, from
/// 0, and the form leaves out <c>~0</c>. A child section puts its parent first, with <c>&gt;</c> between them, for
/// example <c>MBase/ol_base&gt;MRoom/bar</c>.</para>
/// <para>A key can hold these marks. A key that ends with <c>~</c> and digits gets <c>~0</c> too, so the parser reads
/// the last <c>~</c> as the ordinal. A <c>&gt;</c> starts a child only before a child type (see
/// <see cref="Ini.SectionKeys.IsChild"/>), so another <c>&gt;</c> stays in the key.</para>
/// </remarks>
public static class ItemRefs
{
    /// <summary>The detail code of a reference that does not resolve.</summary>
    public const string NotFound = "ReferenceNotFound";

    /// <summary>The detail code of a text that is not a reference.</summary>
    public const string BadReference = "BadReference";

    private static readonly (string Prefix, ItemRefKind Kind)[] Prefixes =
    [
        ("system", ItemRefKind.System), ("obj", ItemRefKind.Object), ("zone", ItemRefKind.Zone),
        ("light", ItemRefKind.Light), ("nebula", ItemRefKind.Nebula), ("field", ItemRefKind.Field),
        ("base", ItemRefKind.Base), ("list", ItemRefKind.ListEntry), ("sec", ItemRefKind.Section),
        ("msn", ItemRefKind.Script), ("trg", ItemRefKind.Trigger), ("line", ItemRefKind.Line),
        ("label", ItemRefKind.Label), ("inst", ItemRefKind.Instance), ("act", ItemRefKind.Act),
        ("prefab", ItemRefKind.Prefab), ("thn", ItemRefKind.Cutscene), ("group", ItemRefKind.Group),
        ("asset", ItemRefKind.Asset),
    ];

    private const string PrefabScript = "prefab:";
    private const string EntityPart = "entity/";
    private const string EventPart = "event/";

    /// <summary>Reads a reference into its parts. It does not look at the edit view.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the text is not a reference, or
    /// its path is not an install path.</exception>
    public static ItemReference Parse(string text)
    {
        var colon = text.IndexOf(':');
        var kind = colon > 0
            ? Prefixes.Where(p => p.Prefix.Equals(text[..colon], StringComparison.OrdinalIgnoreCase))
                .Select(p => (ItemRefKind?)p.Kind).FirstOrDefault()
            : null;
        if (kind == null)
            throw Bad(text, "It does not start with a kind such as obj:, sec: or trg:.");
        var rest = text[(colon + 1)..];
        return kind.Value switch
        {
            ItemRefKind.System or ItemRefKind.Object or ItemRefKind.Zone or ItemRefKind.Nebula or ItemRefKind.Field or
                ItemRefKind.Base => new ItemReference(kind.Value, Name(text, rest)),
            ItemRefKind.Light or ItemRefKind.ListEntry or ItemRefKind.Asset => TwoParts(text, kind.Value, rest, true),
            ItemRefKind.Group => rest.Contains('/') ? TwoParts(text, kind.Value, rest, true)
                : new ItemReference(kind.Value, Name(text, rest)),
            ItemRefKind.Prefab => Prefab(text, rest),
            ItemRefKind.Script => new ItemReference(kind.Value, "") { Path = FilePath(text, rest) },
            ItemRefKind.Cutscene => Cutscene(text, rest),
            ItemRefKind.Section => SectionRefOf(text, rest),
            ItemRefKind.Line => LineOf(text, rest),
            _ => InScript(text, kind.Value, rest),
        };
    }

    /// <summary>The text form of a reference.</summary>
    public static string Format(ItemReference reference)
    {
        var kind = reference.Kind is ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent
            ? ItemRefKind.Cutscene
            : reference.Kind;
        var prefix = Prefixes.First(p => p.Kind == kind).Prefix;
        return reference.Kind switch
        {
            ItemRefKind.Light or ItemRefKind.ListEntry or ItemRefKind.Asset =>
                $"{prefix}:{reference.Scope}/{reference.Name}",
            ItemRefKind.Group => reference.Scope == null ? $"{prefix}:{reference.Name}"
                : $"{prefix}:{reference.Scope}/{reference.Name}",
            ItemRefKind.Prefab => $"{prefix}:{reference.Scope}:{reference.Name}",
            ItemRefKind.Script or ItemRefKind.Cutscene => $"{prefix}:{reference.Path}",
            ItemRefKind.CutsceneEntity => $"{prefix}:{reference.Path}#{EntityPart}{reference.Name}",
            ItemRefKind.CutsceneEvent => $"{prefix}:{reference.Path}#{EventPart}{reference.Row}",
            ItemRefKind.Section => $"{prefix}:{reference.Path}#{SectionText(reference.Section!)}",
            ItemRefKind.Line => $"{prefix}:{reference.Path}#{SectionText(reference.Section!)}/{reference.Row}",
            ItemRefKind.Trigger or ItemRefKind.Label or ItemRefKind.Instance or ItemRefKind.Act =>
                $"{prefix}:{reference.Path}#{reference.Name}",
            _ => $"{prefix}:{reference.Name}",
        };
    }

    /// <summary>The <c>sec:</c> form of a section.</summary>
    public static string Of(SectionRef section) =>
        Format(new ItemReference(ItemRefKind.Section, "") { Path = section.Path, Section = section.Id });

    /// <summary>The section part of a reference, for example <c>MBase/ol_base&gt;MRoom/bar~1</c>.</summary>
    public static string SectionText(SectionIdentity id)
    {
        var part = id.Key == null ? id.Type : $"{id.Type}/{id.Key}";
        // A key that ends with '~' and digits keeps the ordinal 0, so the parser does not read its end as the ordinal.
        if (id.Ordinal > 0 || OrdinalAt(part) != null)
            part += "~" + id.Ordinal.ToString(CultureInfo.InvariantCulture);
        return id.Parent == null ? part : $"{SectionText(id.Parent)}>{part}";
    }

    /// <summary>
    /// The most specific form of a section (section 5.6 of the MCP1 design): <c>obj:</c>, <c>zone:</c>, <c>light:</c>,
    /// <c>nebula:</c> and <c>field:</c> for a section of a system file, <c>system:</c> and <c>base:</c> for a section
    /// of <c>universe.ini</c>, <c>trg:</c> for a <c>[Trigger]</c> of a mission file, else <c>sec:</c>. It also works for
    /// a section that a delete removed.
    /// </summary>
    /// <remarks>A <c>trg:</c> reference names the first trigger with its nickname. So the second trigger with a repeated
    /// nickname keeps the <c>sec:</c> form with its ordinal.</remarks>
    public static string Canonical(EditSession session, SectionRef section)
    {
        var id = section.Id;
        if (id.Key is { } key && id.Parent == null && id.Ordinal == 0)
        {
            if (Ini.SectionKeys.IsMissionFile(section.Path))
            {
                if (id.Type.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
                    return Format(new ItemReference(ItemRefKind.Trigger, key) { Path = section.Path });
            }
            else if (session.IsSystemFile(section.Path))
            {
                var kind = id.Type.ToLowerInvariant() switch
                {
                    "object" => ItemRefKind.Object,
                    "zone" => ItemRefKind.Zone,
                    "nebula" => ItemRefKind.Nebula,
                    "asteroids" => ItemRefKind.Field,
                    "lightsource" => ItemRefKind.Light,
                    _ => (ItemRefKind?)null,
                };
                if (kind == ItemRefKind.Light && session.SystemNicknameOf(section.Path) is { } system)
                    return Format(new ItemReference(ItemRefKind.Light, key) { Scope = system });
                if (kind is ItemRefKind.Nebula or ItemRefKind.Field ||
                    kind is ItemRefKind.Object or ItemRefKind.Zone && ResolvesTo(session, kind.Value, key, section))
                    return Format(new ItemReference(kind.Value, key));
            }
            else if (session.UniversePath is { } universe && IO.VfsPath.Comparer.Equals(universe, section.Path))
            {
                var kind = id.Type.ToLowerInvariant() switch
                {
                    "system" => ItemRefKind.System,
                    "base" => ItemRefKind.Base,
                    _ => (ItemRefKind?)null,
                };
                if (kind is { } k)
                    return Format(new ItemReference(k, key));
            }
        }
        // A section reference has the path in the letter case of the files, so one item has one reference.
        return Of(section with { Path = session.StoredPath(section.Path) });
    }

    /// <summary>
    /// The section of W that a reference names: a reference of the kinds <c>system:</c>, <c>obj:</c>, <c>zone:</c>,
    /// <c>light:</c>, <c>nebula:</c>, <c>field:</c>, <c>base:</c>, <c>list:</c>, <c>sec:</c> or <c>trg:</c>, or the
    /// section of a <c>line:</c>. A trigger with a repeated nickname is the first one.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/> with the closest names of the same kind, or
    /// <see cref="EditErrorKind.InvalidArgument"/> for a kind that names no section.</exception>
    public static SectionRef ResolveSection(EditSession session, ItemReference reference)
    {
        var text = reference.ToString();
        switch (reference.Kind)
        {
            case ItemRefKind.System:
                return session.SystemSection(reference.Name) ?? throw Missing(text, "star system", reference.Name,
                    session.Lists.List("system")?.Entries.Select(e => e.Value) ?? []);
            case ItemRefKind.Object or ItemRefKind.Zone:
            {
                var zone = reference.Kind == ItemRefKind.Zone;
                return session.Nicknames.Find(reference.Name, zone)?.Section ??
                       throw Missing(text, zone ? "zone" : "object", reference.Name,
                           session.Nicknames.All.Where(n => n.IsZone == zone).Select(n => n.Name));
            }
            case ItemRefKind.Light:
            {
                var path = session.SystemPath(reference.Scope!) ?? throw Missing(text, "star system", reference.Scope!,
                    session.Lists.List("system")?.Entries.Select(e => e.Value) ?? []);
                return session.Nicknames.LightTaken(path, reference.Name) ?? throw Missing(text, "light",
                    reference.Name, Keys(session, path, "LightSource"));
            }
            case ItemRefKind.Nebula or ItemRefKind.Field:
            {
                var type = reference.Kind == ItemRefKind.Nebula ? "Nebula" : "Asteroids";
                var zone = session.Nicknames.All.FirstOrDefault(n =>
                    n.IsZone && n.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase));
                var found = zone == null ? null : Existing(session, zone.Section.Path, type, reference.Name);
                return found ?? throw Missing(text, reference.Kind == ItemRefKind.Nebula ? "nebula" : "asteroid field",
                    reference.Name, session.Systems.Files.SelectMany(f => Keys(session, f, type)));
            }
            case ItemRefKind.Base:
                return (session.UniversePath is { } universe ? Existing(session, universe, "Base", reference.Name) : null)
                       ?? throw Missing(text, "base", reference.Name,
                           session.Lists.List("base")?.Entries.Select(e => e.Value) ?? []);
            case ItemRefKind.ListEntry:
            {
                var list = session.Lists.List(reference.Scope!) ?? throw new EditException(new EditError(
                    EditErrorKind.NotFound, NotFound,
                    $"The editor has no data list '{reference.Scope}'. Use the id of a data list, for example base or " +
                    "loadout." + Hint(reference.Scope!, session.Lists.RuleData.Lists.Keys))
                {
                    Target = text,
                });
                return list.Find(reference.Name)?.Section ?? throw Missing(text, "entry of " + reference.Scope,
                    reference.Name, list.Entries.Select(e => e.Value));
            }
            case ItemRefKind.Section or ItemRefKind.Line:
            {
                var target = new SectionRef(reference.Path!, reference.Section!);
                return session.Get(target) != null ? Stored(session, target) : throw Missing(text, "section",
                    SectionText(reference.Section!), session.Sections(reference.Path!).Select(s => SectionText(s.Id)));
            }
            case ItemRefKind.Trigger when !reference.Path!.StartsWith(PrefabScript, StringComparison.Ordinal):
            {
                var path = reference.Path!;
                // The codes and the texts of the mission commands (section 4.7 of the M6b design).
                if (session.OpenDocument(path) == null)
                    throw new EditException(MissionErrors.ScriptNotFoundError(path) with { Target = text });
                return Existing(session, path, "Trigger", reference.Name) ?? throw new EditException(
                    MissionErrors.TriggerNotFoundError(path, reference.Name, Hint(reference.Name, Keys(session, path, "Trigger")))
                        with { Target = text });
            }
            default:
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, BadReference,
                    $"The reference '{text}' names no section of an INI file. Use a reference of a section, for " +
                    "example sec:, obj: or trg:.") { Target = text });
        }
    }

    /// <summary>The install path of a cutscene that a <c>thn:</c> reference names, in the form that the session
    /// stores.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the edit view has no such cutscene.
    /// <see cref="EditErrorKind.InvalidArgument"/>: the reference is not a <c>thn:</c> reference.</exception>
    public static string ResolveCutscene(EditSession session, ItemReference reference)
    {
        if (reference.Kind is not (ItemRefKind.Cutscene or ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent))
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, BadReference,
                $"The reference '{reference}' names no cutscene."));
        return session.CutsceneDocument(reference.Path!)?.Path ?? throw new EditException(new EditError(
            EditErrorKind.NotFound, NotFound, $"The edit view has no cutscene {reference.Path}.")
        {
            Target = reference.ToString(),
        });
    }

    /// <summary>
    /// The entity or the event of a cutscene that a reference names. An entity is the entity that the engine uses for
    /// the name: the last one with the name. An event is the event at the index of the reference. The optional guards
    /// of an event check that it is still the event that the caller read (decision D767 of the MCP1 design).
    /// </summary>
    /// <param name="expectType">The THN name of the type of the event, for example <c>START_SOUND</c>, or null.</param>
    /// <param name="expectTime">The time of the event in seconds, or null.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>, or
    /// <see cref="EditErrorKind.StaleRevision"/> with the detail <c>RowChanged</c> when a guard does not
    /// match.</exception>
    public static ThnRef ResolveCutsceneElement(EditSession session, ItemReference reference, string? expectType = null,
        float? expectTime = null)
    {
        var path = ResolveCutscene(session, reference);
        var model = session.Cutscene(path)!;
        var text = reference.ToString();
        if (reference.Kind == ItemRefKind.CutsceneEntity)
        {
            var entity = model.EntityNamed(reference.Name) ?? throw Missing(text, "entity", reference.Name,
                model.Entities.Select(e => ThnScriptModel.NameOf(e.Table)).OfType<string>());
            return new ThnRef(path, ThnElementKind.Entity, entity.Id);
        }
        if (reference.Kind != ItemRefKind.CutsceneEvent)
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, BadReference,
                $"The reference '{text}' names no entity and no event of a cutscene."));
        var index = reference.Row!.Value;
        if (index >= model.Events.Count)
            throw new EditException(new EditError(EditErrorKind.NotFound, NotFound,
                $"Cutscene {path} has {model.Events.Count} events, so it has no event {index}. Read the cutscene " +
                "again, then try again.") { Target = text });
        var element = model.Events[index];
        var type = ThnSchema.TypeOf(ThnElementKind.Event, element.Table, session.ThnSymbols);
        var time = element.Table.List.Length > 0 && element.Table.List[0] is ThnNumber number ? number.Value : (float?)null;
        if (expectType != null && !string.Equals(type, expectType.Trim(), StringComparison.OrdinalIgnoreCase) ||
            expectTime is { } expected && (time == null || Math.Abs(time.Value - expected) > 1e-4f))
        {
            throw new EditException(new EditError(EditErrorKind.StaleRevision, "RowChanged",
                $"Event {index} of {path} is not a {expectType ?? type} event at {Seconds(expectTime ?? time)} s " +
                "now. The cutscene changed after you read it. Read the cutscene again, then try again.")
            {
                Target = text,
            });
        }
        return new ThnRef(path, ThnElementKind.Event, element.Id);
    }

    /// <summary>The reference of an entity or an event of a cutscene: the name of an entity, or the index of an
    /// event.</summary>
    public static string Of(EditSession session, ThnRef element)
    {
        var model = session.Cutscene(element.Path);
        var found = model?.Element(element.Kind, element.Id);
        if (model == null || found == null)
            return Format(new ItemReference(ItemRefKind.Cutscene, "") { Path = element.Path });
        return element.Kind == ThnElementKind.Entity
            ? Format(new ItemReference(ItemRefKind.CutsceneEntity, ThnScriptModel.NameOf(found.Table) ?? "")
            {
                Path = element.Path,
            })
            : Format(new ItemReference(ItemRefKind.CutsceneEvent, "") { Path = element.Path, Row = model.Events.IndexOf(found) });
    }

    /// <summary>The longest name that gets the closest names. No name of the game data is this long. The distance of
    /// two names costs the product of their lengths, and a request runs on the thread of the session.</summary>
    internal const int MaxHintName = 128;

    /// <summary>The names of a list that are closest to a name, at most three, closest first. A name longer than
    /// <see cref="MaxHintName"/> gets none.</summary>
    internal static IReadOnlyList<string> Closest(string name, IEnumerable<string> candidates)
    {
        if (name.Length > MaxHintName)
            return [];
        var limit = Math.Max(2, name.Length / 3);
        var lower = name.ToLowerInvariant();
        // The distance of two names is at least the difference of their lengths, so such a name is not close.
        return candidates.Where(c => Math.Abs(c.Length - name.Length) <= limit).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (Name: c, Distance: Distance(lower, c.ToLowerInvariant())))
            .Where(c => c.Distance <= limit)
            .OrderBy(c => c.Distance).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3).Select(c => c.Name).ToList();
    }

    private static string Seconds(float? time) => time?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";

    /// <summary>True when the <c>obj:</c> or <c>zone:</c> form of a key resolves to this section, or when W does not
    /// have the section any more, for example after a delete. Another system can have an object with the same nickname,
    /// and then the form names the first one.</summary>
    private static bool ResolvesTo(EditSession session, ItemRefKind kind, string key, SectionRef section)
    {
        if (session.Get(section) == null)
            return true;
        return session.Nicknames.Find(key, kind == ItemRefKind.Zone)?.Section.Equals(section) == true;
    }

    /// <summary>The section of W with a type and a key in a file, in the form that the session stores, or
    /// null.</summary>
    private static SectionRef? Existing(EditSession session, string path, string type, string key)
    {
        var target = new SectionRef(path, new SectionIdentity(type, key));
        return session.Get(target) != null ? Stored(session, target) : null;
    }

    /// <summary>The reference of a section that the session has, with the path and the key as the session stores
    /// them.</summary>
    private static SectionRef Stored(EditSession session, SectionRef target) =>
        session.Sections(target.Path).First(s => s.Equals(target));

    private static IEnumerable<string> Keys(EditSession session, string path, string type) =>
        session.Sections(path).Where(s => s.Id.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && s.Id.Key != null)
            .Select(s => s.Id.Key!);

    private static EditException Missing(string text, string what, string name, IEnumerable<string> candidates) =>
        new(new EditError(EditErrorKind.NotFound, NotFound,
            $"The edit view has no {what} '{name}'. Use a name that the edit view has.{Hint(name, candidates)}")
        {
            Target = text,
        });

    /// <summary>The sentence with the closest names of a name, with a space before it, or an empty text when no name
    /// is close.</summary>
    internal static string Hint(string name, IEnumerable<string> candidates) =>
        Closest(name, candidates) is { Count: > 0 } closest
            ? " The closest names are: " + string.Join(", ", closest) + "."
            : "";

    private static EditException Bad(string text, string reason) =>
        new(new EditError(EditErrorKind.InvalidArgument, BadReference, $"'{text}' is not an item reference. {reason}"));

    private static string Name(string text, string name) =>
        name.Length > 0 ? name : throw Bad(text, "The name after the ':' is empty.");

    private static ItemReference TwoParts(string text, ItemRefKind kind, string rest, bool required)
    {
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            return required ? throw Bad(text, "It needs two parts with a '/' between them.") : new ItemReference(kind, rest);
        return new ItemReference(kind, rest[(slash + 1)..]) { Scope = rest[..slash] };
    }

    private static ItemReference Prefab(string text, string rest)
    {
        var colon = rest.IndexOf(':');
        var source = colon > 0 ? rest[..colon] : "";
        if (source is not ("builtin" or "mod") || colon == rest.Length - 1)
            throw Bad(text, "A prefab reference is prefab:builtin:<id> or prefab:mod:<id>.");
        return new ItemReference(ItemRefKind.Prefab, rest[(colon + 1)..]) { Scope = source };
    }

    private static ItemReference Cutscene(string text, string rest)
    {
        var hash = rest.IndexOf('#');
        if (hash < 0)
            return new ItemReference(ItemRefKind.Cutscene, "") { Path = FilePath(text, rest) };
        var path = FilePath(text, rest[..hash]);
        var item = rest[(hash + 1)..];
        if (item.StartsWith(EntityPart, StringComparison.OrdinalIgnoreCase) && item.Length > EntityPart.Length)
            return new ItemReference(ItemRefKind.CutsceneEntity, item[EntityPart.Length..]) { Path = path };
        if (item.StartsWith(EventPart, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(item[EventPart.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return new ItemReference(ItemRefKind.CutsceneEvent, "") { Path = path, Row = index };
        throw Bad(text, "The part after '#' of a cutscene is entity/<name> or event/<index>.");
    }

    private static ItemReference SectionRefOf(string text, string rest)
    {
        // In prefab mode, the file of a section is the template of a prefab (section 4.0.7 of the M6b design).
        var (path, item) = PathAndItem(text, rest, prefabAllowed: true);
        return new ItemReference(ItemRefKind.Section, "") { Path = path, Section = ParseSection(text, item) };
    }

    private static ItemReference LineOf(string text, string rest)
    {
        var (path, item) = PathAndItem(text, rest, prefabAllowed: true);
        var slash = item.LastIndexOf('/');
        if (slash <= 0 || !int.TryParse(item[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var row))
            throw Bad(text, "A line reference ends with '/' and the row, a whole number from 0.");
        return new ItemReference(ItemRefKind.Line, "") { Path = path, Section = ParseSection(text, item[..slash]), Row = row };
    }

    private static ItemReference InScript(string text, ItemRefKind kind, string rest)
    {
        var (path, item) = PathAndItem(text, rest, prefabAllowed: kind == ItemRefKind.Trigger);
        return new ItemReference(kind, item) { Path = path };
    }

    /// <summary>The script and the item of a reference with <c>#</c>. In prefab mode, the script of a trigger or a line
    /// is the template of a prefab.</summary>
    private static (string Path, string Item) PathAndItem(string text, string rest, bool prefabAllowed)
    {
        var hash = rest.IndexOf('#');
        if (hash < 0 || hash == rest.Length - 1)
            throw Bad(text, "It needs the file, a '#' and the item.");
        var file = rest[..hash];
        if (prefabAllowed && file.StartsWith(PrefabScript, StringComparison.OrdinalIgnoreCase))
        {
            var prefab = Prefab(text, file[PrefabScript.Length..]);
            return ($"{PrefabScript}{prefab.Scope}:{prefab.Name}", rest[(hash + 1)..]);
        }
        return (FilePath(text, file), rest[(hash + 1)..]);
    }

    private static string FilePath(string text, string path)
    {
        try
        {
            return InstallPaths.Parse(path);
        }
        catch (EditException e)
        {
            throw new EditException(e.Error with { Target = text });
        }
    }

    /// <summary>Reads the section part of a reference (see the remarks of <see cref="ItemRefs"/>).</summary>
    private static SectionIdentity ParseSection(string text, string part)
    {
        SectionIdentity? parent = null;
        foreach (var piece in Pieces(part))
        {
            var body = piece;
            var ordinal = 0;
            if (OrdinalAt(body) is var (tilde, n))
            {
                ordinal = n;
                body = body[..tilde];
            }
            var slash = body.IndexOf('/');
            var type = slash < 0 ? body : body[..slash];
            var key = slash < 0 ? null : body[(slash + 1)..];
            if (type.Length == 0 || key is { Length: 0 })
                throw Bad(text, "The section part is <type>/<key> or <type>, for example Good/ol_good or Mission.");
            parent = new SectionIdentity(type, key, parent, ordinal);
        }
        return parent!;
    }

    /// <summary>The parts of a section part, split at each <c>&gt;</c> before a child type. Another <c>&gt;</c> stays
    /// in its part, because it belongs to a key.</summary>
    private static List<string> Pieces(string part)
    {
        var pieces = new List<string>();
        foreach (var piece in part.Split('>'))
        {
            var end = piece.IndexOfAny(['/', '~']);
            if (pieces.Count > 0 && !Ini.SectionKeys.IsChild(end < 0 ? piece : piece[..end]))
                pieces[^1] += ">" + piece;
            else
                pieces.Add(piece);
        }
        return pieces;
    }

    /// <summary>The place of the last <c>~</c> and the ordinal after it, when a text ends with <c>~</c> and digits,
    /// and has another character before the <c>~</c>. Else null.</summary>
    private static (int Tilde, int Ordinal)? OrdinalAt(string text)
    {
        var tilde = text.LastIndexOf('~');
        return tilde > 0 && int.TryParse(text[(tilde + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (tilde, n)
            : null;
    }

    /// <summary>The edit distance of two texts: the fewest inserts, deletes and changes of one character.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
