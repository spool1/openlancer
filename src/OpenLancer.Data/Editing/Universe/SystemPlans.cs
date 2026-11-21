using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>A part of a system that a new system copies from its template (see
/// <see cref="SystemPlans.Create"/>).</summary>
public enum SystemPart
{
    /// <summary>The <c>[Archetype]</c> preload list.</summary>
    Preload,

    /// <summary>The <c>[TexturePanels]</c> section.</summary>
    Panels,

    /// <summary>The <c>[Dust]</c> section.</summary>
    Dust,

    /// <summary>The <c>[Music]</c> section.</summary>
    Music,

    /// <summary>The <c>[Ambient]</c> section.</summary>
    Ambient,

    /// <summary>The <c>[Background]</c> section.</summary>
    Background,

    /// <summary>Each <c>[LightSource]</c> section.</summary>
    Lights,

    /// <summary>Each <c>[EncounterParameters]</c> section.</summary>
    Encounters,
}

/// <summary>The sun of a new system. A value that is null comes from the first sun of the template.</summary>
/// <param name="Archetype">The solar archetype of the sun. Its <c>type</c> must be <c>SUN</c>.</param>
/// <param name="Star">The star of the sun (<c>star</c>).</param>
/// <param name="Name">The name of the sun.</param>
public sealed record SunRow(string? Archetype = null, string? Star = null, string? Name = null);

/// <summary>The options of a new system (see <see cref="SystemPlans.Create"/>).</summary>
public sealed record SystemOptions
{
    /// <summary>True shows the system on the nav map of a new game (<c>visit = 1</c>). False shows it after the first
    /// visit.</summary>
    public bool OnMapAtStart { get; init; }

    /// <summary>The zoom of the system map (<c>NavMapScale</c>), or null for the value of the template.</summary>
    public float? NavMapScale { get; init; }

    /// <summary>The parts of the template to copy, or null for all parts.</summary>
    public IReadOnlySet<SystemPart>? Copy { get; init; }

    /// <summary>The faction of the system (<c>local_faction</c>), or null for the faction of the template.</summary>
    public string? LocalFaction { get; init; }

    /// <summary>The sun, or null for a copy of the first sun of the template.</summary>
    public SunRow? Sun { get; init; }
}

/// <summary>
/// The plans of the star systems: a new system and a rename of a system that the active mod adds. Each plan gives one
/// command, which runs as one entry of the edit history, also when it changes several files and the text of the mod.
/// The agent tools and the hand tools call the same plans.
/// </summary>
/// <remarks>
/// <para>Retail facts: the game reads the system file at the <c>file</c> line of a <c>[system]</c>, with the nickname
/// that the section set before that line. The id of a system is the FLHash of its nickname, and the game drops a
/// second system with the same id. The retail route writer keeps 15 characters of a system nickname. The nav map shows
/// a system at <c>pos</c> on a grid of 0 to 15, and only with bit 0 of its visit flags. A system without
/// <c>msg_id_prefix</c> gets one line in the game log, and NPC speech that names the system is silent.</para>
/// <para>A new system file goes to <c>DATA\MODS\&lt;mod id&gt;\SYSTEMS\&lt;Nick&gt;\&lt;Nick&gt;.ini</c> (see
/// <see cref="ContentPaths.NewSystemFile"/>). Its <c>[system]</c> names it relative to the folder of
/// <c>universe.ini</c>, with a <c>..</c> part.</para>
/// </remarks>
public static class SystemPlans
{
    /// <summary>The detail code of a place outside the nav map grid.</summary>
    public const string MapPosRange = "MapPosRange";

    /// <summary>The detail code of a template system without a file in the edit view.</summary>
    public const string LikeNoFile = "LikeNoFile";

    /// <summary>The detail code of a template system without a sun, when the call gives no sun.</summary>
    public const string LikeNoSun = "LikeNoSun";

    /// <summary>The detail code of a sun archetype of another type than <c>SUN</c>.</summary>
    public const string SunType = "SunType";

    /// <summary>The detail code of a system of the game or of a lower mod, which the editor does not rename or
    /// delete.</summary>
    public const string GameSystem = "GameSystem";

    /// <summary>The largest place on each axis of the nav map grid.</summary>
    public const int MapMax = 15;

    /// <summary>The rule of a system nickname: a letter, then letters, digits or <c>_</c>, 2 to 15 characters. The
    /// retail route writer keeps 15 characters of a nickname.</summary>
    private static readonly Regex NicknameRule = new("^[A-Za-z][A-Za-z0-9_]{1,14}$", RegexOptions.CultureInvariant);

    /// <summary>The header sections that a new system copies, in the order of the new file, with their parts. The
    /// <c>[SystemInfo]</c> has no part, because the new system always copies it.</summary>
    private static readonly (string Type, SystemPart? Part)[] HeaderSections =
    [
        ("SystemInfo", null), ("Archetype", SystemPart.Preload), ("TexturePanels", SystemPart.Panels),
        ("Dust", SystemPart.Dust), ("Music", SystemPart.Music), ("Ambient", SystemPart.Ambient),
        ("Background", SystemPart.Background), ("LightSource", SystemPart.Lights),
        ("EncounterParameters", SystemPart.Encounters),
    ];

    /// <summary>The lines of the first sun of the template that the new sun keeps.</summary>
    private static readonly string[] SunLines = ["ambient_color", "archetype", "star", "atmosphere_range", "burn_color", "ids_info"];

    /// <summary>
    /// Plans a new star system of the active mod, as one entry of the edit history. The system copies the background,
    /// the music, the dust, the ambient color, the lights, the faction, the encounter parameters and the sun of a
    /// template system.
    /// </summary>
    /// <remarks>
    /// <para>The entry adds the <c>[system]</c> to <c>universe.ini</c> with the lines <c>nickname</c>, <c>file</c>,
    /// <c>pos</c>, <c>visit</c>, <c>strid_name</c>, <c>ids_info</c> and <c>NavMapScale</c>, in this order, and without
    /// <c>msg_id_prefix</c>. A new system has no voice lines for its name. The entry makes the new system file with
    /// the header sections of the template, a sun at the center and the death zone of the sun. The sun copies the
    /// first sun of the template, with a numeric <c>ids_info</c> and a new name. The zone copies the death zone of that
    /// sun, or it takes the defaults of the rule data. So the first save does not find a system without objects. The
    /// names go into the text of the active mod.</para>
    /// </remarks>
    /// <param name="nickname">The nickname of the new system.</param>
    /// <param name="like">The nickname of the template system.</param>
    /// <param name="mapPos">The place on the nav map grid, 0 to 15 on each axis.</param>
    /// <param name="name">The name of the system on the nav map.</param>
    /// <param name="infocard">The plain text of the infocard, or null or empty for the name.</param>
    public static UniversePlan Create(EditSession session, string nickname, string like, (int X, int Y) mapPos,
        string name, string? infocard, SystemOptions? options = null)
    {
        options ??= new SystemOptions();
        nickname = nickname.Trim();
        var plan = new UniversePlan($"Create system {nickname}");
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (session.ActiveMod is not { } active)
            return plan.Refuse("", "The profile has no active mod.");
        if (session.UniversePath is not { } universe || session.OpenDocument(universe) is not { State.Exists: true } universeFile)
            return plan.Refuse("", "freelancer.ini names no universe.ini, so the game has no star systems.");
        if (universeFile.ReadOnlyReason is { } universeReason)
            return plan.Refuse(universe, $"{SceneSections.FileName(universe)}: {universeReason}");
        if (NicknameProblem(nickname) is { } nicknameProblem)
            return plan.Refuse(universe, nicknameProblem, detail: MissionErrors.NameInvalid);
        if (TakenBy(session, nickname, null) is { } other)
            return plan.Refuse(universe, $"The system nickname {nickname} is taken: {other.Id.Key} has the same nickname " +
                                         "or the same FLHash.", other, MissionErrors.NameTaken);
        var path = ContentPaths.NewSystemFile(active.Id, nickname);
        if (ContentPaths.Taken(session, path))
            return plan.Refuse(path, $"The file {path} exists already in the game data, in a lower mod or in the active " +
                                     "mod. Pick another nickname.", detail: MissionErrors.PathInUse);
        if (mapPos.X is < 0 or > MapMax || mapPos.Y is < 0 or > MapMax)
            return plan.Refuse(universe, $"The place on the nav map needs two whole numbers from 0 to {MapMax}. Other " +
                                         "places are off the nav map.", detail: MapPosRange);
        if (options.NavMapScale is { } given && (!float.IsFinite(given) || given <= 0))
            return plan.Refuse(universe, "The zoom of the system map needs a number above 0.",
                detail: MissionErrors.FieldValue);
        if (session.SystemSection(like) is not { } likeSystem || session.SystemPath(like) is not { } likePath ||
            session.OpenDocument(likePath) is not { State.Exists: true })
            return plan.Refuse(universe, $"The system {like} has no file in the edit view. Pick another system.",
                detail: LikeNoFile);
        var likeSections = session.Sections(likePath);
        var likeSun = likeSections.FirstOrDefault(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) &&
                                                       session.Get(s) is { } view &&
                                                       SceneSections.Last(view, "archetype") is { } archetype &&
                                                       ScenePlans.TypeOf(session, archetype) == "sun");
        var row = options.Sun;
        if (likeSun == null && (row?.Archetype == null || row.Star == null))
            return plan.Refuse(likePath, $"The system {like} has no sun. Give the archetype and the star of the sun, or " +
                                         "pick another system.", detail: LikeNoSun);
        if (row?.Archetype is { } sunArchetype && ScenePlans.TypeOf(session, sunArchetype) is var sunType &&
            sunType != "sun")
            return plan.Refuse(universe, (sunType == null
                ? $"No solar archetype has the name {sunArchetype}."
                : $"The archetype {sunArchetype} is a {sunType.ToUpperInvariant()}.") +
                " The sun needs an archetype of the type SUN.", detail: SunType);

        var problems = new List<string>();
        var texts = new SectionCopies(session, problems.Add);
        var systemId = new SectionIdentity("system", nickname);
        var systemName = texts.New(StringKind.String, name, systemId, "strid_name");
        var systemInfo = texts.New(StringKind.Infocard,
            ItemEdits.ToInfocard(string.IsNullOrWhiteSpace(infocard) ? name : infocard), systemId, "ids_info");
        var lines = new List<(string Name, string Text)>
        {
            ("nickname", nickname),
            ("file", ContentPaths.UniverseValue(session, path)!),
            ("pos", $"{mapPos.X}, {mapPos.Y}"),
            ("visit", options.OnMapAtStart ? "1" : "0"),
        };
        if (systemName != null)
            lines.Add(("strid_name", systemName.ToString()!));
        if (systemInfo != null)
            lines.Add(("ids_info", systemInfo.ToString()!));
        var scale = options.NavMapScale?.ToString("R", CultureInfo.InvariantCulture) ??
                    session.Get(likeSystem)?.Lines("NavMapScale").LastOrDefault()?.Values.FirstOrDefault()?.ToString();
        if (scale != null)
            lines.Add(("NavMapScale", scale));
        if (ScenePlans.Entries(lines, session.IsBinaryFile(universe), out var lineError) is not { } systemEntries)
            return plan.Refuse(universe, lineError!);

        // The new file: the header sections of the template, the sun and its death zone.
        var copies = HeaderSections
            .Where(h => h.Part is not { } part || options.Copy?.Contains(part) != false)
            .SelectMany(h => likeSections.Where(s => SceneSections.Is(s.Id.Type, h.Type)))
            .Select(s => session.WorkingSection(s)!)
            .Select(s => SceneSections.Is(s.Name, "SystemInfo") && options.LocalFaction is { } faction
                ? WithLine(s, "local_faction", new StringValue(faction.Trim()))
                : s)
            .ToList();
        if (options.LocalFaction is { } newFaction && !copies.Any(s => SceneSections.Is(s.Name, "SystemInfo")))
            copies.Insert(0, Build("SystemInfo", [("local_faction", [new StringValue(newFaction.Trim())])]));

        var sunName = FreeName(session, $"{nickname}_sun");
        var sunSection = likeSun == null ? null : session.WorkingSection(likeSun);
        var sunId = new SectionIdentity(SceneSections.ObjectType, sunName);
        var sunText = texts.New(StringKind.String, row?.Name is { Length: > 0 } rowName ? rowName : $"{name} Sun", sunId,
            "ids_name");
        var sunLines = new List<(string, IReadOnlyList<ValueBase>)> { ("nickname", [new StringValue(sunName)]) };
        if (sunText != null)
            sunLines.Add(("ids_name", [sunText]));
        sunLines.Add(("pos", Center));
        foreach (var line in sunSection?.Where(e => e.Count > 0 && SunLines.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                     .DistinctBy(e => e.Name.ToLowerInvariant()) ?? [])
            sunLines.Add((line.Name, Replaced(line, row) ?? line.ToArray()));
        foreach (var (key, value) in new[] { ("archetype", row?.Archetype), ("star", row?.Star) })
        {
            if (value != null && !sunLines.Any(l => SceneSections.Is(l.Item1, key)))
                sunLines.Add((key, [new StringValue(value.Trim())]));
        }
        copies.Add(Build(SceneSections.ObjectType, sunLines));

        var zoneName = FreeName(session, $"zone_{nickname}_sun_death");
        if (DeathZone(session, likeSections, likeSun, sunSection) is { } likeZone)
        {
            copies.Add(Build(SceneSections.ZoneType,
            [
                ("nickname", [new StringValue(zoneName)]), ("pos", Center),
                .. likeZone.Where(e => !SceneSections.Is(e.Name, "nickname") && !SceneSections.Is(e.Name, "pos"))
                    .Select(e => (e.Name, (IReadOnlyList<ValueBase>)e.ToArray())),
            ]));
        }
        else if (session.Lists.RuleData.Builders.SunZone is { } rule)
        {
            var range = sunLines.FirstOrDefault(l => SceneSections.Is(l.Item1, "atmosphere_range")).Item2?.FirstOrDefault();
            var radius = rule.RadiusFor(range != null && range.TryToSingle(out var r) ? r : (float?)null);
            copies.Add(Build(SceneSections.ZoneType,
            [
                ("nickname", [new StringValue(zoneName)]), ("pos", Center), ("shape", [new StringValue("SPHERE")]),
                ("size", [new StringValue(radius.ToString("R", CultureInfo.InvariantCulture))]),
                .. rule.Lines.Select(l => (l.Key, (IReadOnlyList<ValueBase>)[new StringValue(l.Value!)])),
            ]));
        }

        EditCommand file;
        try
        {
            file = texts.CopyTo(path, copies.Select(s => new SectionCopy(s)).ToList(), newFile: true);
        }
        catch (ArgumentException e)
        {
            return plan.Refuse(path, ErrorText.Of(e));
        }
        if (problems.Count > 0)
            return plan.Refuse(path, problems[0]);

        var system = new SectionRef(universe, systemId);
        var sun = new SectionRef(path, sunId);
        plan.System = system;
        plan.File = path;
        plan.Sun = sun;
        plan.SelectAfter = system;
        var zone = copies[^1] is var last && SceneSections.Is(last.Name, SceneSections.ZoneType)
            ? new SectionRef(path, new SectionIdentity(SceneSections.ZoneType, zoneName))
            : null;
        plan.Adds = zone == null ? [system, sun] : [system, sun, zone];
        plan.Warnings =
        [
            Diagnostic.Warning(universe, $"No jump gate or jump hole leads to {nickname} yet.") with { Target = system },
        ];
        plan.Command = EditCommand.Group(plan.Label,
            [EditCommand.AddSection(universe, "system", systemEntries), file, .. texts.Commands], session.Snapshot());
        return plan;
    }

    /// <summary>
    /// Plans a new nickname for a system that the active mod adds, as one entry of the edit history. The entry changes
    /// the <c>[system]</c>, value 0 of each <c>goto</c> that names the system, the <c>system</c> line of each
    /// <c>[Base]</c> and the mission lines that name the system. The file of the system keeps its path.
    /// </summary>
    /// <remarks>A save game in the system does not find it after the rename, so the plan always warns. It asks before
    /// it runs when it changes a file other than <c>universe.ini</c> and the file of the system, when another file
    /// names the old nickname as text, or when it has another warning.</remarks>
    public static UniversePlan Rename(EditSession session, string system, string newNickname)
    {
        var name = newNickname.Trim();
        var plan = new UniversePlan($"Rename {system} to {name}");
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (session.SystemSection(system) is not { } item || session.Get(item) is not { } view)
            return plan.Refuse(session.UniversePath ?? "", $"No [system] has the nickname {system}, or more than one has " +
                                                           "it.", detail: MissionErrors.ItemNotFound);
        var old = item.Id.Key!;
        plan.Label = $"Rename {old} to {name}";
        if (!view.IsNew)
            return plan.Refuse(item.Path, $"{old} is a system of the game or of a lower mod. This version renames only " +
                                          "systems that the active mod adds.", item, GameSystem);
        if (view.ReadOnlyReason is { } reason)
            return plan.Refuse(item.Path, $"{old}: {reason}", item);
        if (string.Equals(old, name, StringComparison.OrdinalIgnoreCase))
            return plan.Refuse(item.Path, EditCommand.SameKeyReason(item.Id), item, MissionErrors.NameInvalid);
        if (NicknameProblem(name) is { } nicknameProblem)
            return plan.Refuse(item.Path, nicknameProblem, item, MissionErrors.NameInvalid);
        if (TakenBy(session, name, item) is { } other)
            return plan.Refuse(item.Path, $"The system nickname {name} is taken: {other.Id.Key} has the same nickname or " +
                                          "the same FLHash.", other, MissionErrors.NameTaken);

        var path = session.SystemPath(old);
        var warnings = new List<Diagnostic>();
        var values = new List<EditCommand>();
        var changes = new List<SectionRef>();
        foreach (var reference in session.Lists.ReferencesTo("system", old))
        {
            if (session.Get(reference.Section)?.ReadOnlyReason is { } lineReason)
            {
                warnings.Add(Diagnostic.Warning(reference.Section.Path,
                    $"{reference.Section.Id}: the editor cannot change the '{reference.Key}' line: {lineReason} The line " +
                    "names the old nickname" + (reference.Rule.Closed ? ", and the save stops until you fix it." : ".")) with
                {
                    Target = reference.Section, Key = reference.Key,
                });
                continue;
            }
            values.Add(EditCommand.SetValueAt(reference.Section, reference.Key, reference.Line, reference.Index,
                new StringValue(name)));
            if (!changes.Contains(reference.Section))
                changes.Add(reference.Section);
        }
        var mentions = session.StoryMentions(old)
            .Where(m => !VfsPath.Comparer.Equals(m.Path, item.Path))
            .Select(m => new NamedMention(old, m.Path, m.Count))
            .ToList();
        foreach (var mod in session.LaterModsThatChangeSection(item))
            warnings.Add(Diagnostic.Warning(item.Path, $"The mod {mod} loads later and changes {item.Id}. Its patch cannot " +
                                                       "find the section after the rename.") with { Target = item });
        var missionSections = new List<SectionRef>();
        var mission = session.Missions.References.Rename([MissionValueKind.System], old, name, warnings, missionSections);
        changes.AddRange(missionSections);
        var others = warnings.Count > 0 || mentions.Count > 0 || missionSections.Count > 0 ||
                     changes.Any(c => !VfsPath.Comparer.Equals(c.Path, item.Path) &&
                                      (path == null || !VfsPath.Comparer.Equals(c.Path, path)));

        var renamed = item with { Id = item.Id with { Key = name } };
        plan.System = renamed;
        plan.File = path;
        plan.Changes = changes;
        plan.Mentions = mentions;
        plan.SelectAfter = renamed;
        plan.Warnings =
        [
            Diagnostic.Warning(item.Path, $"A save game in {old} does not find the system after the rename." +
                                          (path == null ? "" : $" The system file keeps its path {path}.")) with
            {
                Target = renamed,
            },
            .. warnings,
        ];
        plan.NeedsConfirmation = others;
        plan.Command = EditCommand.Group(plan.Label,
            [.. values, EditCommand.RenameAdded(item, name), .. mission == null ? [] : new[] { mission }],
            session.Snapshot());
        return plan;
    }

    /// <summary>Why a text cannot be the nickname of a system, or null.</summary>
    internal static string? NicknameProblem(string nickname) => NicknameRule.IsMatch(nickname)
        ? null
        : $"The system nickname '{nickname}' is not valid. Use 2 to 15 letters, digits or '_', and start with a letter. " +
          "The game keeps only 15 characters of a system nickname in its route tables.";

    /// <summary>The first <c>[system]</c> of <c>universe.ini</c> other than <paramref name="except"/> whose nickname
    /// has the FLHash of a nickname, or null. Two nicknames that differ only in letter case have one FLHash.</summary>
    internal static SectionRef? TakenBy(EditSession session, string nickname, SectionRef? except)
    {
        if (session.UniversePath is not { } universe)
            return null;
        var hash = FLHash.CreateID(nickname);
        return session.Sections(universe).FirstOrDefault(s => SceneSections.Is(s.Id.Type, "system") && s.Id.Key != null &&
                                                              !s.Equals(except) && FLHash.CreateID(s.Id.Key) == hash);
    }

    /// <summary>The value of <c>pos</c> at the center of a system.</summary>
    private static IReadOnlyList<ValueBase> Center => [new Int32Value(0), new Int32Value(0), new Int32Value(0)];

    /// <summary>A nickname of an object or a zone that no object or zone of any system has: the name, or the first
    /// free <c>&lt;name&gt;_&lt;n&gt;</c>.</summary>
    private static string FreeName(EditSession session, string name) =>
        session.Nicknames.Taken(name) == null ? name : session.Nicknames.FirstFree(name, 1);

    /// <summary>The values of a kept line of the template sun, with the archetype or the star of the sun row, or
    /// null to keep the values.</summary>
    private static IReadOnlyList<ValueBase>? Replaced(Entry line, SunRow? row)
    {
        var value = SceneSections.Is(line.Name, "archetype") ? row?.Archetype
            : SceneSections.Is(line.Name, "star") ? row?.Star
            : null;
        return value == null ? null : [new StringValue(value.Trim())];
    }

    /// <summary>
    /// The death zone of the sun of the template: the first <c>SPHERE</c> zone of its file with a <c>damage</c> line at
    /// the position of the sun, or null. 27 of the 42 first suns of the vanilla 1.1 system files have one.
    /// </summary>
    private static Section? DeathZone(EditSession session, IReadOnlyList<SectionRef> sections, SectionRef? sun,
        Section? sunSection)
    {
        if (sun == null || sunSection == null || Position(sunSection) is not { } center)
            return null;
        return sections.Where(s => SceneSections.Is(s.Id.Type, SceneSections.ZoneType))
            .Select(s => session.WorkingSection(s)!)
            .FirstOrDefault(z => z.Any(e => SceneSections.Is(e.Name, "damage")) &&
                                 z.LastOrDefault(e => SceneSections.Is(e.Name, "shape") && e.Count > 0)?[0].ToString()
                                     ?.Trim().Equals("SPHERE", StringComparison.OrdinalIgnoreCase) == true &&
                                 Position(z) is { } at && System.Numerics.Vector3.Distance(at, center) < 1);
    }

    /// <summary>The value of the last <c>pos</c> line of a section, or null.</summary>
    private static System.Numerics.Vector3? Position(Section section) =>
        section.LastOrDefault(e => SceneSections.Is(e.Name, "pos") && e.Count >= 3) is { } pos &&
        pos[0].TryToSingle(out var x) && pos[1].TryToSingle(out var y) && pos[2].TryToSingle(out var z)
            ? new System.Numerics.Vector3(x, y, z)
            : null;

    /// <summary>A copy of a section with one line set: the first line of the key gets the value, or the section gets a
    /// new line at its end.</summary>
    private static Section WithLine(Section source, string key, ValueBase value)
    {
        var lines = source.Select(e => (e.Name, (IReadOnlyList<ValueBase>)e.ToArray())).ToList();
        var at = lines.FindIndex(l => SceneSections.Is(l.Name, key));
        if (at >= 0)
            lines[at] = (lines[at].Name, [value]);
        else
            lines.Add((key, [value]));
        return Build(source.Name, lines);
    }

    /// <summary>A new section of a type with lines.</summary>
    private static Section Build(string type, IEnumerable<(string Name, IReadOnlyList<ValueBase> Values)> lines)
    {
        var section = new Section(type);
        foreach (var (name, values) in lines)
        {
            var entry = new Entry(section, name);
            foreach (var value in values)
                entry.Add(value);
            section.Add(entry);
        }
        return section;
    }
}
