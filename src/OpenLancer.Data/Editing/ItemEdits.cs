using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using LibreLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>Edits of an item that change more than one section: a rename, and the text of an item.</summary>
public static class ItemEdits
{
    /// <summary>
    /// Gives an item that the active mod adds a new nickname, and changes each line that names the item in the
    /// sections that the active mod can change (review finding F12): the <c>ship</c> of a hull good, the
    /// <c>hull</c> of a package, the <c>equipment</c> and the <c>addon</c> lines of a good, the
    /// <c>projectile_archetype</c> of a gun and the <c>marketgood</c> lines of a market. Equipment and its good keep
    /// the same nickname, because the engine finds the equipment of a good by that nickname.
    /// </summary>
    /// <remarks>The edit is one entry of the edit history. Run it at once: it holds the lines that it changes, so it
    /// fails when it runs after the edit view changed.</remarks>
    /// <param name="item">A section of an item file with a nickname. The active mod adds it, and also each section
    /// that the edit renames with it, for example the good of equipment (see
    /// <see cref="EditCommand.RenameAdded"/>).</param>
    /// <param name="warnings">Gets a warning for each mission line that names the item and that the edit cannot change,
    /// for example a line of a section that the active mod cannot patch. The save stops at such a line (rule M23), so
    /// the caller shows the warnings at once. Null drops them.</param>
    /// <exception cref="ArgumentException">The section is not a section of an item file with a nickname. Or the lower
    /// layer has a section that the edit renames. Or the new nickname differs from the old one only in letter case, or
    /// it cannot be the value of a patch line.</exception>
    /// <exception cref="InvalidOperationException">Another section of the same kind of file has the new
    /// nickname.</exception>
    public static EditCommand Rename(EditSession session, SectionRef item, string newNickname,
        List<Diagnostic>? warnings = null) => Rename(session, item, newNickname, warnings, null);

    /// <summary>
    /// Gives an item that the active mod adds a new nickname, as
    /// <see cref="Rename(EditSession, SectionRef, string, List{Diagnostic})"/> does.
    /// </summary>
    /// <param name="changes">Gets each section that the edit changes and keeps, with its identity after the edit: the
    /// sections that it renames and the sections whose lines name the item. Null drops them.</param>
    internal static EditCommand Rename(EditSession session, SectionRef item, string newNickname,
        List<Diagnostic>? warnings, List<SectionRef>? changes)
    {
        var index = session.Items;
        var kind = index.KindOf(item.Path);
        var oldNickname = item.Id.Key;
        if (kind == null || oldNickname == null || index.Section(item) == null)
            throw new ArgumentException($"{item} is not a section of an item file with a nickname.", nameof(item));
        if (string.Equals(oldNickname, newNickname, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(EditCommand.SameKeyReason(item.Id), nameof(newNickname));
        var renamed = new List<(SectionRef Section, ItemFileKind Kind)> { (item, kind.Value) };
        if (kind == ItemFileKind.Equipment)
            renamed.AddRange(index.Related(item, ItemRelation.Good).Select(g => (g, ItemFileKind.Goods)));
        else if (kind == ItemFileKind.Goods)
            renamed.AddRange(index.Named(ItemFileKind.Equipment, oldNickname).Select(e => (e, ItemFileKind.Equipment)));
        // RenameAdded refuses a section of the lower layer when it runs. This check refuses it before, so the plan of a
        // command can refuse it.
        if (renamed.FirstOrDefault(r => session.InBase(r.Section)).Section is { } lower)
            throw new ArgumentException(EditCommand.OnlyAddedReason(lower.Id), nameof(item));
        foreach (var (section, sectionKind) in renamed)
        {
            if (index.Named(sectionKind, newNickname).Any(s => !s.Equals(section)))
                throw new InvalidOperationException(
                    $"Another section of the {sectionKind.ToString().ToLowerInvariant()} files has the nickname {newNickname}.");
        }

        var commands = renamed.Select(r => EditCommand.RenameAdded(r.Section, newNickname)).ToList();
        // A section that the edit renames has another identity when its links change.
        var moved = renamed.ToDictionary(r => r.Section, r => r.Section with { Id = r.Section.Id with { Key = newNickname } });
        var relinked = new List<SectionRef>();
        foreach (var (section, name) in renamed.SelectMany(r => index.ReferencesTo(r.Kind, oldNickname)).Distinct())
        {
            // The active mod cannot change the line. It is a line of a lower layer that names a nickname that only
            // the active mod adds.
            if (session.Get(section)?.ReadOnlyReason != null)
                continue;
            var lines = index.Section(section)!.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(e => Relinked(e, oldNickname, newNickname))
                .ToList();
            commands.Add(EditCommand.SetLines(moved.GetValueOrDefault(section, section), name, lines));
            relinked.Add(section);
        }
        // The mission lines that name the item follow the rename in the same entry (decision D668).
        var missionKinds = renamed.Select(r => r.Kind switch
        {
            ItemFileKind.Ships => (Missions.MissionValueKind?)Missions.MissionValueKind.ShipArch,
            ItemFileKind.Goods => Missions.MissionValueKind.Good,
            ItemFileKind.Equipment => Missions.MissionValueKind.Equipment,
            _ => null,
        }).OfType<Missions.MissionValueKind>().Distinct().ToList();
        if (missionKinds.Count > 0 &&
            session.Missions.References.Rename(missionKinds, oldNickname, newNickname, warnings ?? []) is { } mission)
            commands.Add(mission);
        // The new nickname goes into the key lines and the links, so each of their files must take it.
        foreach (var binary in renamed.Select(r => r.Section).Concat(relinked).Select(index.IsBinary).Distinct())
        {
            if (PatchFormatter.KeyError(newNickname, binary) is { } error)
                throw new ArgumentException(error, nameof(newNickname));
        }
        changes?.AddRange(renamed.Select(r => moved[r.Section])
            .Concat(relinked.Select(s => moved.GetValueOrDefault(s, s))).Distinct());
        return EditCommand.Group($"Rename {oldNickname} to {newNickname}", commands, session.Snapshot());
    }

    /// <summary>The values of a line, with the new nickname in the first value when it names the old one.</summary>
    private static IReadOnlyList<ValueBase> Relinked(Entry line, string oldNickname, string newNickname) =>
        line.Select((value, i) => i == 0 && value.ToString().Equals(oldNickname, StringComparison.OrdinalIgnoreCase)
            ? new StringValue(newNickname)
            : value).ToList();

    /// <summary>
    /// The other sections of the item of a section that show the same text for a text key: their line of the name
    /// has the same text id or the same placeholder. For example, a retail gun and its good have the same
    /// <c>ids_name</c>, but the Inspector shows the name of the gun and a shop shows the name of the good (review
    /// finding F12).
    /// </summary>
    public static IReadOnlyList<SectionRef> SharedText(EditSession session, SectionRef section, string name)
    {
        var value = TextValue(session.Get(section), name);
        if (value == null)
            return [];
        return session.Items.Parts(section)
            .Where(p => !p.Equals(section) && TextValue(session.Get(p), name) == value)
            .ToList();
    }

    /// <summary>
    /// Sets the text of a text key of a section, for example <c>ids_name</c>: the edit puts the text into the
    /// strings.json of the active mod, and sets the line of the section and of the <paramref name="also"/> sections to
    /// the placeholder of the text. The text of no other section changes.
    /// </summary>
    /// <remarks>
    /// <para>The key of the text is the key of the placeholder in the line of the section, when the line has a
    /// placeholder of the right kind and no other section uses it. Otherwise it is a new key (see
    /// <see cref="NewTextKeys"/>) that no other section uses and that is not the key of an unused text of the mod.
    /// So two sections share a text only when <paramref name="also"/> says so, for example a gun and its good, which
    /// have the same nickname.</para>
    /// <para>Run the edit at once: it fails when it runs after the edit view or the text changed.</para>
    /// </remarks>
    /// <param name="text">The text. For an infocard, RDL XML (see <see cref="ToInfocard"/>).</param>
    /// <param name="also">Other sections that get the same placeholder in their line of the name, for example the
    /// sections of <see cref="SharedText"/>.</param>
    /// <exception cref="ArgumentException">The name holds no text id, the edit view has no such section, or the key
    /// of the text is not valid.</exception>
    public static EditCommand SetText(EditSession session, SectionRef section, string name, string text,
        IReadOnlyList<SectionRef>? also = null)
    {
        var kind = SchemaCatalog.TextKind(section.Id.Type, name) ??
                   throw new ArgumentException($"'{name}' does not hold the id of a text.", nameof(name));
        var view = session.Get(section) ??
                   throw new ArgumentException($"The edit view has no section {section}.", nameof(section));
        var targets = new[] { section }.Concat(also ?? []).Distinct().ToList();
        var owners = targets.Select(t => t with { Path = VfsPath.Normalize(t.Path) ?? t.Path }).ToHashSet();
        var uses = session.TextUses();
        // A key is free when no other section uses it. The key of an unused text of the mod stays as it is.
        bool Free(string key)
        {
            var users = uses[new StringPlaceholder(kind, key)].ToList();
            return users.All(u => u != null && owners.Contains(u)) &&
                   (users.Count > 0 || session.Text(new StringPlaceholder(kind, key)) == null);
        }

        var current = StringPlaceholder.TryParse(TextValue(view, name) ?? "", out var placeholder) && placeholder.Kind == kind
            ? placeholder.Key
            : null;
        var key = current != null && Free(current) ? current : NewTextKeys(section.Id, name).First(Free);
        var result = new StringPlaceholder(kind, key).ToString();
        var commands = new List<EditCommand> { EditCommand.SetText(kind, key, text) };
        foreach (var target in targets)
        {
            if (TextValue(session.Get(target), name) != result)
                commands.Add(EditCommand.SetLines(target, name, [[new StringValue(result)]]));
        }
        return EditCommand.Group($"Set {name} of {section.Id.Key}", commands, session.Snapshot());
    }

    /// <summary>
    /// Makes an infocard from plain text: each line of the text becomes a paragraph of RDL XML, the format of
    /// Freelancer infocards.
    /// </summary>
    public static string ToInfocard(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split('\n')
            .Select(p => p.Length == 0 ? "<PARA/>" : $"<TEXT>{SecurityElement.Escape(p)}</TEXT><PARA/>");
        return $"<?xml version=\"1.0\" encoding=\"UTF-16\"?><RDL><PUSH/>{string.Concat(paragraphs)}<POP/></RDL>";
    }

    /// <summary>
    /// The keys for a new text of a text key of a section, in the order of preference: the nickname in lowercase for
    /// <c>ids_name</c> and <c>strid_name</c>, and the nickname with a suffix for the other keys, for example
    /// <c>li_gun01.info</c> for <c>ids_info</c>. Then the same key with the section type, for example
    /// <c>li_gun01.good</c>, then with a number, for example <c>li_gun01.good_2</c>. The list does not end.
    /// </summary>
    /// <exception cref="ArgumentException">The section has no nickname, or the nickname gives no valid key.</exception>
    internal static IEnumerable<string> NewTextKeys(SectionIdentity section, string name)
    {
        var nickname = section.Key?.ToLowerInvariant() ??
                       throw new ArgumentException($"{section} has no nickname for the key of its text.", nameof(section));
        var lower = name.ToLowerInvariant();
        var suffix = lower is "ids_name" or "strid_name" ? ""
            : lower.StartsWith("ids_", StringComparison.Ordinal) ? "." + lower[4..]
            : lower.StartsWith("strid_", StringComparison.Ordinal) ? "." + lower[6..]
            : "." + lower;
        if (!ModStrings.IsValidKey(nickname + suffix))
            throw new ArgumentException($"The text key '{nickname + suffix}' must use letters, digits, '_', '-' or '.'.",
                nameof(section));
        var typed = $"{nickname}.{section.Type.ToLowerInvariant()}";
        // A section type that gives no valid key adds only the number.
        var stem = ModStrings.IsValidKey(typed + suffix) ? typed : nickname;
        return Keys();

        IEnumerable<string> Keys()
        {
            yield return nickname + suffix;
            if (stem != nickname)
                yield return stem + suffix;
            for (var n = 2; ; n++)
                yield return $"{stem}_{n}{suffix}";
        }
    }

    /// <summary>
    /// The value of the first line of a text key as text, when it refers to a text: a placeholder, or a text id
    /// above 0. Otherwise null.
    /// </summary>
    private static string? TextValue(SectionView? view, string name)
    {
        if (view?.Lines(name) is not [{ Values: [var value, ..] }, ..])
            return null;
        var text = value.ToString();
        return StringPlaceholder.TryParse(text, out _) || value.TryToInt32(out var id) && id > 0 ? text : null;
    }
}
