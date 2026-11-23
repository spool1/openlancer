using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing.Bases;

public static partial class BasePlans
{
    /// <summary>The lines of a character that a copy keeps (owner decision K5 of the bases design). The other lines of a
    /// retail character, for example its rumors, its bribes and its knowledge, name the story and other bases.</summary>
    private static readonly string[] CharacterLines =
        ["body", "head", "lefthand", "righthand", "accessory", "base_appr", "individual_name", "affiliation", "voice", "room"];

    /// <summary>The lines of a <c>[BaseFaction]</c> that a new base leaves out (owner decision K6 of the bases design):
    /// a new base offers no missions.</summary>
    private static readonly string[] MissionLines = ["mission_type", "offers_missions"];

    /// <summary>The words of the title of a character after the name of the base, by the role of its fixture in
    /// lowercase. The four roles of the retail fixtures are bartender, trader, Equipment and ShipDealer.</summary>
    private static readonly Dictionary<string, string> RoleWords = new()
    {
        ["bartender"] = "Bartender",
        ["trader"] = "Commodity Trader",
        ["equipment"] = "Equipment Dealer",
        ["shipdealer"] = "Ship Dealer",
    };

    /// <summary>The commands of the bar data of a new base, the sections that they add (the <c>[MBase]</c> first), the
    /// new characters, or a refusal.</summary>
    private sealed record BarPlan(List<EditCommand> Commands, List<SectionRef> Adds, List<SectionRef> Characters,
        Refusal? Refusal = null);

    /// <summary>
    /// The <c>[MBase]</c> of a new base and its children (see <see cref="Create"/>), or null when the template has no
    /// <c>[MBase]</c>. The new characters get the nicknames <c>&lt;base&gt;_&lt;role&gt;</c> for a fixture and
    /// <c>&lt;base&gt;_npc_&lt;n&gt;</c> for the bar crowd. The fixture lines and the <c>npc</c> lines name them. A
    /// <c>npc</c> line or a fixture line of a character that the base does not copy goes. The block is one copy of
    /// <see cref="SectionCopies.CopyTo"/>, which puts the children under the new <c>[MBase]</c>, gives the characters
    /// their new nicknames and copies the texts of the mods.
    /// </summary>
    /// <remarks>
    /// A retail name text of a character has three lines: the faction, the title and the name of the person. An example
    /// is "Junkers", "Rochester Bartender" and "Winston Harris". So each copied fixture character gets its own text of
    /// the mod. Its lines are its faction, the name of the new base with the word of its role, and the name of the
    /// person. A character of the crowd keeps its text.
    /// </remarks>
    private static BarPlan? MBaseCommands(EditSession session, BaseSpec spec, BaseTemplate template,
        IReadOnlyList<TemplateRoom> kept, string nickname, SectionCopies texts, List<Diagnostic> warnings)
    {
        if (template.MBase is not { } mbase || session.Bases.MBasesPath is not { } path)
            return null;
        if (session.OpenDocument(path) is { ReadOnlyReason: { } reason })
            return new BarPlan([], [], [], new Refusal(path, $"{SceneSections.FileName(path)}: {reason}"));
        var rooms = kept.Select(r => r.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in template.Fixtures.Where(f => rooms.Contains(f.Room)))
            roles.TryAdd(fixture.Character, fixture.Role);

        // The new nickname of each character that the base copies, by its old nickname.
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copied = new List<(TemplateCharacter Character, string? Role)>();
        var crowd = 0;
        foreach (var character in template.Characters)
        {
            var fixture = roles.TryGetValue(character.Nickname, out var role);
            var keep = spec.Characters switch
            {
                CharacterCopy.Template => fixture || character.Room != null && rooms.Contains(character.Room),
                CharacterCopy.Dealers => fixture,
                _ => false,
            };
            if (!keep || names.ContainsKey(character.Nickname))
                continue;
            names[character.Nickname] = fixture
                ? FreeName(names.Values, $"{nickname}_{(role ?? "fixture").ToLowerInvariant()}")
                : $"{nickname}_npc_{++crowd}";
            copied.Add((character, fixture ? role : null));
        }

        var mbaseId = new SectionIdentity("MBase", nickname);
        var faction = spec.LocalFaction?.Trim() is { Length: > 0 } given ? given : null;
        var lines = new List<(string, string)> { ("nickname", nickname) };
        if (faction != null)
            lines.Add(("local_faction", faction));
        if (ScenePlans.Entries(lines, session.IsBinaryFile(path), out var error) is not { } entries)
            return new BarPlan([], [], [], new Refusal(path, error!));
        // The faction of the request takes the place of the faction of the template.
        entries.AddRange(mbase.Where(e => SceneSections.Is(e.Name, "local_faction") && faction == null ||
                                          SceneSections.Is(e.Name, "diff")).Select(Line));

        // A line that names a character stays only when the base copies the character. The copy renames it.
        bool Named(Entry e, string key) =>
            !SceneSections.Is(e.Name, key) || e.Count > 0 && names.ContainsKey(e[0].ToString().Trim());
        var copies = new List<SectionCopy> { new(SectionEdits.Build("MBase", entries)) };
        if (template.Vendor is { } vendor)
            copies.Add(new SectionCopy(vendor));
        copies.AddRange(template.Factions.Select(row => new SectionCopy(Only(row, e =>
            !MissionLines.Contains(e.Name, StringComparer.OrdinalIgnoreCase) && Named(e, "npc")))));
        foreach (var (character, role) in copied)
        {
            var key = names[character.Nickname];
            var source = character.Section;
            // The new text is not in the text of the mod before the command runs, so the copy keeps its placeholder.
            if (role != null && NameText(session, spec, template, character, role, warnings) is { } name &&
                texts.New(StringKind.String, name, new SectionIdentity("GF_NPC", key, mbaseId), "individual_name") is
                    { } text)
                source = SectionEdits.WithLines(source, "individual_name", [[text]]);
            copies.Add(new SectionCopy(source, key, CharacterLines));
        }
        copies.AddRange(template.RoomParts
            .Where(r => BaseTemplate.Last(r, "nickname") is { } n && rooms.Contains(n))
            .Select(room => new SectionCopy(Only(room, e => Named(e, "fixture")))));

        var adds = new List<SectionRef> { new(path, mbaseId) };
        var ordinals = new Dictionary<SectionIdentity, int>();
        foreach (var copy in copies.Skip(1))
        {
            var key = copy.Key ?? BaseTemplate.Last(copy.Source, "nickname");
            var first = new SectionIdentity(copy.Source.Name, key, mbaseId);
            var ordinal = ordinals.GetValueOrDefault(first);
            ordinals[first] = ordinal + 1;
            adds.Add(new SectionRef(path, first with { Ordinal = ordinal }));
        }
        return new BarPlan([texts.CopyTo(path, copies, newFile: false)], adds,
            adds.Where(a => SceneSections.Is(a.Id.Type, "GF_NPC")).ToList());
    }

    /// <summary>
    /// The name text of a copied fixture character, with three lines. The first line is the name of the faction of its
    /// <c>affiliation</c>. The second line is the title: the name of the new base and the word of the role. The third
    /// line is the name of the person: the third line of the text of the template, else its last line. Without a name
    /// of the faction, a text of the template with three lines gives the first line. Else the text has no faction line.
    /// Null when the character has no <c>individual_name</c>, or when the plan cannot read its text. The character then
    /// keeps the line of the template, and the second case gives a warning.
    /// </summary>
    private static string? NameText(EditSession session, BaseSpec spec, BaseTemplate template,
        TemplateCharacter character, string? role, List<Diagnostic> warnings)
    {
        if (BaseTemplate.Last(character.Section, "individual_name") == null)
            return null;
        if (TextOf(session, spec, character.Ref, "individual_name") is not { } text)
        {
            warnings.Add(Diagnostic.Warning(character.Ref.Path,
                $"The editor cannot read the name of the character {character.Nickname} of {template.Nickname}. So its " +
                $"copy keeps that name text, and the game shows the title of {template.Nickname} for it.") with
            {
                Target = character.Ref,
            });
            return null;
        }
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var faction = BaseTemplate.Last(character.Section, "affiliation") is { } affiliation &&
                      session.Lists.List("faction")?.Find(affiliation)?.Section is { } group
            ? TextOf(session, spec, group, "ids_name")
            : null;
        faction ??= lines.Length >= 3 ? lines[0] : null;
        var title = role != null && RoleWords.TryGetValue(role.ToLowerInvariant(), out var word)
            ? $"{spec.Name.Trim()} {word}"
            : spec.Name.Trim();
        var person = lines.Length >= 3 ? lines[2] : lines[^1];
        return string.Join('\n', new[] { faction, title, person }.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    /// <summary>
    /// The text of a text key of a section of W, or null. The text comes from the host (see
    /// <see cref="BaseSpec.Text"/>), else from a placeholder or an id of a text of a mod.
    /// </summary>
    private static string? TextOf(EditSession session, BaseSpec spec, SectionRef section, string key)
    {
        if (spec.Text?.Invoke(section, key) is { Length: > 0 } text)
            return text;
        if (session.Get(section) is not { } view || SceneSections.Last(view, key) is not { } value)
            return null;
        if (StringPlaceholder.TryParse(value, out var placeholder))
            return session.Text(placeholder) is { Length: > 0 } modText ? modText : null;
        return int.TryParse(value, out var id) && session.ModTextOf(id) is { Text.Length: > 0 } found ? found.Text : null;
    }

    /// <summary>A name that no name of the list has, without letter case: the stem, or the stem with <c>_2</c>,
    /// <c>_3</c> and on.</summary>
    private static string FreeName(IEnumerable<string> names, string stem)
    {
        var taken = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem))
            return stem;
        for (var n = 2; ; n++)
        {
            if (!taken.Contains($"{stem}_{n}"))
                return $"{stem}_{n}";
        }
    }
}
