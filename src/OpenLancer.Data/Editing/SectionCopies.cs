using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>A text of a mod that a line value names: its kind, its text, and the value that names it, for example
/// <c>$str:li01_dock</c> or <c>#458754</c>.</summary>
internal sealed record CopiedText(StringKind Kind, string Text, string Source);

/// <summary>One section that <see cref="SectionCopies.CopyTo"/> copies.</summary>
/// <param name="Source">The section to copy, for example a section of W or of a template file. The copy does not change
/// it.</param>
/// <param name="Key">The key of the copy, or null to keep the key of the source. A section without a key can keep the
/// key. So can a name that is unique only in its file, such as a light of a system file.</param>
/// <param name="Keep">The names of the lines that the copy keeps, without letter case, or null for each line. The copy
/// always keeps the line of the key.</param>
internal sealed record SectionCopy(Section Source, string? Key = null, IReadOnlyCollection<string>? Keep = null);

/// <summary>
/// The texts of a mod that the copies of sections get, and the commands that add them to the text of the active mod.
/// New from template (<see cref="ItemTemplates"/>) and Duplicate in the Scene (milestone M3) use it. So do the draft and
/// the placement of a prop group (milestone M23), and the copies of <see cref="CopyTo"/>. So a copy of a section gets
/// its text in one way.
/// </summary>
/// <param name="problem">Gets why a line of a copy cannot get its text.</param>
internal sealed class SectionCopies(EditSession session, Action<string> problem)
{
    private readonly ILookup<StringPlaceholder, SectionRef?> uses = session.TextUses();

    /// <summary>The new placeholder of each copied text, by the value that named the text: <c>$str:key</c> of a
    /// placeholder, <c>#id</c> of an id of a text of a mod, or a text of a prop group.</summary>
    private readonly Dictionary<string, StringPlaceholder> copied = [];

    public List<EditCommand> Commands { get; } = [];

    /// <summary>
    /// Value i of a line of a copy. The first value of a text key that holds a text placeholder of the active
    /// mod, or the id of a text of a mod, becomes the placeholder of a copy of the text. Each other value stays.
    /// </summary>
    /// <param name="copy">The copy, for the key of the text.</param>
    public ValueBase Copy(Entry entry, int i, SectionIdentity copy) => Copy(entry.Section.Name, entry.Name, i, entry[i], copy);

    /// <summary>Value i of a line of a key of a section type (see <see cref="Copy(Entry, int, SectionIdentity)"/>).</summary>
    public ValueBase Copy(string type, string name, int i, ValueBase value, SectionIdentity copy) =>
        TextOf(session, type, name, i, value, problem) is { } text ? Add(text, copy, name) ?? value : value;

    /// <summary>
    /// The text of a mod that value i of a line names: a text placeholder of the active mod, or the id of a text of a mod.
    /// Null for another value, for the id of a text of the base data, and for a placeholder without text, which the save
    /// reports.
    /// </summary>
    /// <param name="problem">Gets why the value names no text, for an id of a mod without a text.</param>
    public static CopiedText? TextOf(EditSession session, string type, string name, int i, ValueBase value,
        Action<string> problem)
    {
        if (i != 0 || SchemaCatalog.TextKind(type, name) == null)
            return null;
        if (value is StringValue && StringPlaceholder.TryParse(value.ToString(), out var placeholder))
            return session.Text(placeholder) is { Length: > 0 } text ? new CopiedText(placeholder.Kind, text, placeholder.ToString())
                : null;
        if (value is not Int32Value || !value.TryToInt32(out var id) || !session.IsModTextId(id))
            return null;
        if (session.ModTextOf(id) is not { Text.Length: > 0 } found)
        {
            problem($"the '{name}' line of [{type}] holds {id}, which is in the id range of the text of the mods, but no " +
                    "mod has a text with this id.");
            return null;
        }
        return new CopiedText(found.Kind, found.Text, "#" + id);
    }

    /// <summary>
    /// Adds a copy of a text to the text of the active mod, with a key that the copy gives and that no section, text or
    /// other copy has. A text that this object copied before keeps its first copy. Null when the copy gives no valid
    /// key: the problem then tells why.
    /// </summary>
    /// <param name="copy">The copy, for the key of the text.</param>
    /// <param name="name">The key of the line, for example <c>ids_name</c>.</param>
    public ValueBase? Add(CopiedText text, SectionIdentity copy, string name)
    {
        if (!copied.TryGetValue(text.Source, out var result))
        {
            string key;
            try
            {
                key = ItemEdits.NewTextKeys(copy, name).First(k => Free(new StringPlaceholder(text.Kind, k)));
            }
            catch (ArgumentException e)
            {
                problem(e.Message);
                return null;
            }
            result = new StringPlaceholder(text.Kind, key);
            copied[text.Source] = result;
            Commands.Add(EditCommand.SetText(text.Kind, key, text.Text));
        }
        return new StringValue(result.ToString());
    }

    /// <summary>
    /// Adds a new text to the text of the active mod for a line of a new section, with a key that the section gives
    /// and that no section, text or copy has (see <see cref="Add"/>). So the new texts and the copied texts of one
    /// plan get their keys in one way and do not collide. Null when the section gives no valid key: the problem then
    /// tells why.
    /// </summary>
    /// <param name="section">The new section, for the key of the text.</param>
    /// <param name="name">The key of the line, for example <c>strid_name</c>.</param>
    public ValueBase? New(StringKind kind, string text, SectionIdentity section, string name) =>
        Add(new CopiedText(kind, text, $"new:{section.Type}:{section.Key}:{name}"), section, name);

    /// <summary>
    /// The command that copies sections into a file, for example the header sections of a system into a new system
    /// file. With <paramref name="newFile"/>, it makes the new INI file of the folder of the active mod
    /// with the copies in their order (see <see cref="EditCommand.NewFile"/>). Else it adds the copies at the end of the
    /// existing file (see <see cref="EditCommand.AddSection"/>). There, a child copy goes under the last other copy
    /// before it, or under <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// <para>Each line of a copy keeps its values, with three changes. The line of the key gets the key of the copy. A
    /// value that is the old key of another copy of the call names the new key of that copy. An example is the
    /// <c>parent</c> of a copied child object. A text of a mod gets a copy with a new key (see
    /// <see cref="Copy(string, string, int, ValueBase, SectionIdentity)"/>), and <see cref="Commands"/> gets the command
    /// of the text. A text id of the base game stays.</para>
    /// <para>A copy in a binary file writes each bool string in lowercase (see <see cref="Stored"/>).</para>
    /// </remarks>
    /// <param name="path">The install path of the file.</param>
    /// <param name="parent">The parent of the child copies at the start of the list, in the existing file.</param>
    /// <exception cref="ArgumentException">A copy gives a key to a section type without a key. Or two copies give one
    /// old key two new keys, or a child copy of an existing file has no parent.</exception>
    public EditCommand CopyTo(string path, IReadOnlyList<SectionCopy> copies, bool newFile, SectionRef? parent = null)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in copies)
        {
            if (copy.Key is not { } key || KeyOf(copy.Source) is not { } old || old.Equals(key, StringComparison.Ordinal))
                continue;
            if (renames.TryGetValue(old, out var known) && !known.Equals(key, StringComparison.Ordinal))
                throw new ArgumentException($"Two copies give the key '{old}' the new keys '{known}' and '{key}'. A line " +
                                            $"that names '{old}' cannot name both.", nameof(copies));
            renames[old] = key;
        }

        var binary = !newFile && session.IsBinaryFile(path);
        var sections = new List<SectionEntries>(copies.Count);
        var parents = new List<SectionRef?>(copies.Count);
        var owner = parent;
        var ownerKey = parent?.Id.Key;
        foreach (var copy in copies)
        {
            var type = copy.Source.Name;
            var keyEntry = SectionKeys.For(type).KeyEntry;
            if (copy.Key != null && keyEntry == null)
                throw new ArgumentException($"A [{type}] section has no key, so its copy cannot get the key '{copy.Key}'.",
                    nameof(copies));
            var key = copy.Key ?? KeyOf(copy.Source);
            var child = SectionKeys.IsChild(type);
            // A child has no key of its own, so the key of a copied text comes from the key of its parent.
            var identity = new SectionIdentity(type, child && key == null ? ownerKey : key);
            var entries = copy.Source
                .Where(e => keyEntry != null && Is(e.Name, keyEntry) || copy.Keep == null || copy.Keep.Any(k => Is(k, e.Name)))
                .Select(e => new EntryView(e.Name, e.Select((_, i) =>
                    Stored(Rewrite(e, i, keyEntry, copy.Key, renames, identity), binary)).ToList()))
                .ToList();
            if (copy.Key != null && !entries.Any(e => Is(e.Name, keyEntry!)))
                entries.Insert(0, new EntryView(keyEntry!, [new StringValue(copy.Key)]));
            sections.Add(new SectionEntries(type, entries));
            if (child)
            {
                if (!newFile && owner == null)
                    throw new ArgumentException($"The copy of a [{type}] section needs a parent section.", nameof(parent));
                parents.Add(owner);
                continue;
            }
            parents.Add(null);
            owner = new SectionRef(path, new SectionIdentity(type, key));
            ownerKey = key;
        }

        if (newFile)
            return EditCommand.NewFile(path, sections);
        var adds = sections.Select((s, i) => EditCommand.AddSection(path, s.Type, s.Entries, parents[i])).ToList();
        return EditCommand.Group(
            $"Copy {Plural.Count(copies.Count, "section")} to {Scene.SceneSections.FileName(path)}", adds);
    }

    /// <summary>Value i of a line of a copy of <see cref="CopyTo"/>.</summary>
    private ValueBase Rewrite(Entry entry, int i, string? keyEntry, string? key, Dictionary<string, string> renames,
        SectionIdentity copy)
    {
        var isKey = keyEntry != null && Is(entry.Name, keyEntry);
        if (isKey && i == 0 && key != null)
            return new StringValue(key);
        if (!isKey && renames.TryGetValue(entry[i].ToString() ?? "", out var renamed))
            return new StringValue(renamed);
        return Copy(entry.Section.Name, entry.Name, i, entry[i], copy);
    }

    /// <summary>The key of a section: the first value of its line of the key entry, or null.</summary>
    private static string? KeyOf(Section section) =>
        SectionKeys.For(section.Name).KeyEntry is { } name &&
        section.FirstOrDefault(e => Is(e.Name, name) && e.Count > 0) is { } line
            ? line[0].ToString()
            : null;

    /// <summary>
    /// A value of a new line as a file stores it. A binary file stores a new boolean as lowercase text (see
    /// <c>BinaryValueTypes.Store</c>). So each bool string of a copy in a binary file becomes <c>true</c> or
    /// <c>false</c>. The engine reads both forms as the same boolean. Each other value stays.
    /// </summary>
    /// <param name="binary">True for a value of a binary file.</param>
    public static ValueBase Stored(ValueBase value, bool binary) =>
        binary && value is StringValue && bool.TryParse(value.ToString(), out var truth)
            ? new StringValue(truth ? "true" : "false")
            : value;

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when no section uses the key, the mod has no text with it, and no other copy has it.</summary>
    private bool Free(StringPlaceholder placeholder) =>
        !uses.Contains(placeholder) && session.Text(placeholder) == null && !copied.ContainsValue(placeholder);
}
