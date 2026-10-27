using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>
/// An edit of the edit view, or of the text of the active mod. <see cref="EditSession.Execute"/> runs a command as one
/// entry of the edit history, and an <see cref="EditTransaction"/> runs its commands as one entry.
/// </summary>
/// <remarks>
/// <para>A command never changes a section or an entry in place. It builds new sections and puts them into W, so the
/// history can put the old ones back.</para>
/// <para>A command that cannot run throws, and changes nothing: an <see cref="ArgumentException"/> when an argument
/// is not valid, and an <see cref="InvalidOperationException"/> when the edit view does not allow the edit.</para>
/// </remarks>
public abstract class EditCommand
{
    private protected EditCommand(string label) => Label = label;

    /// <summary>The name of the edit, for the Undo and Redo menu items.</summary>
    internal string Label { get; }

    /// <summary>
    /// Sets the lines of one name. The new lines take the place of the first line of the name, or go at the end of the
    /// section, as the merge puts the lines of a patch. An empty list removes the name.
    /// </summary>
    /// <param name="lines">The values of each new line.</param>
    public static EditCommand SetLines(SectionRef target, string name, IReadOnlyList<IReadOnlyList<ValueBase>> lines)
    {
        CheckName(name);
        return new SetLinesCommand($"Set {name}", target, name, lines);
    }

    /// <summary>
    /// Sets the lines of one name from the text of their values, for example <c>1, 2.5, li_gun01</c> for one line.
    /// Each line gets the values that a patch line with that text gives through the merge (see
    /// <c>docs/MOD_FORMAT.md</c>, "Value types in binary files"). So the edit view shows the values that a save and a
    /// reload give. The lines go where <see cref="SetLines"/> puts them.
    /// </summary>
    /// <remarks>A line text that a patch line cannot hold makes the command throw an <see cref="ArgumentException"/>
    /// when it runs.</remarks>
    /// <param name="lines">The value text of each new line. An empty list removes the name. An empty text gives a
    /// line without values, for example <c>separable =</c>.</param>
    public static EditCommand SetLinesFromText(SectionRef target, string name, IReadOnlyList<string> lines)
    {
        CheckName(name);
        return new SetLineTextsCommand($"Set {name}", target, name, lines);
    }

    /// <summary>Removes all lines of one name.</summary>
    public static EditCommand RemoveName(SectionRef target, string name)
    {
        CheckName(name);
        return new SetLinesCommand($"Remove {name}", target, name, []);
    }

    /// <summary>
    /// Puts back the lines of one name, or of the whole section when <paramref name="name"/> is null, as the lower
    /// layer has them: the base data with the mods that load before the active mod. A save then writes no line for
    /// them. A section that is as the lower layer has it does not change.
    /// </summary>
    /// <remarks>The command throws an <see cref="InvalidOperationException"/> when it runs on a section that the
    /// lower layer does not have, because the active mod adds it.</remarks>
    public static EditCommand Revert(SectionRef target, string? name = null)
    {
        if (name != null)
            CheckName(name);
        return new RevertCommand(name == null ? $"Revert {target.Id}" : $"Revert {name}", target, name);
    }

    /// <summary>
    /// Adds a section to the file at <paramref name="path"/>. A section goes at the end of the file. A child section
    /// (see <c>docs/MOD_FORMAT.md</c>) goes after the child sections of its parent.
    /// </summary>
    /// <param name="parent">The parent of a child section. Only a child section has one.</param>
    public static EditCommand AddSection(string path, string type, IReadOnlyList<EntryView> entries,
        SectionRef? parent = null)
    {
        if (!PatchFormatter.TryFormatHeader(type, out _, out var error))
            throw new ArgumentException(error, nameof(type));
        foreach (var entry in entries)
            CheckName(entry.Name);
        return new AddSectionCommand($"Add [{type}]", path, type, entries, parent);
    }

    /// <summary>Deletes a section, and its child sections.</summary>
    public static EditCommand DeleteSection(SectionRef target) => new DeleteSectionCommand($"Delete {target.Id}", target);

    /// <summary>
    /// Gives a section a new key. The new key must differ from the old key in more than letter case, and no other
    /// section of the type in W can have it. A section of the lower layer gets the new key through <c>ol.rename</c> and
    /// keeps its place (milestone M3). Then the new key must also differ from the key of each other section of the type
    /// in the lower layer, also of a section that W deleted. A rename back to the key of the lower layer ends the rename.
    /// A section of the lower layer with child sections, and a child section of the lower layer, cannot get a new key.
    /// </summary>
    public static EditCommand Rename(SectionRef target, string newKey)
    {
        if (string.IsNullOrWhiteSpace(newKey))
            throw new ArgumentException("The new key cannot be empty.", nameof(newKey));
        return new RenameCommand($"Rename {target.Id} to {newKey}", target, newKey, onlyAdded: false);
    }

    /// <summary>
    /// Gives a section that the active mod adds a new key, as <see cref="Rename"/> does. The command throws an
    /// <see cref="InvalidOperationException"/> when it runs on a section of the lower layer. The Database renames its
    /// items with it, because a patch cannot change the lines of a lower <c>[BaseGood]</c> that name an item.
    /// </summary>
    internal static EditCommand RenameAdded(SectionRef target, string newKey)
    {
        if (string.IsNullOrWhiteSpace(newKey))
            throw new ArgumentException("The new key cannot be empty.", nameof(newKey));
        return new RenameCommand($"Rename {target.Id} to {newKey}", target, newKey, onlyAdded: true);
    }

    /// <summary>Why <see cref="RenameAdded"/> cannot give a section of the lower layer a new key.</summary>
    internal static string OnlyAddedReason(SectionIdentity id) =>
        $"{id}: the editor can change the key only of a section that the active mod adds.";

    /// <summary>Why a rename cannot give a section a key that differs from its old key only in letter case.</summary>
    internal static string SameKeyReason(SectionIdentity id) =>
        $"{id}: the new key must differ from the old key in more than letter case.";

    /// <summary>
    /// Sets one value of one line in place: value <paramref name="index"/> of the line <paramref name="line"/> (from 0)
    /// of a name. The other lines keep their order, so a line of a group of lines that belong together stays with its
    /// group. The command throws an <see cref="ArgumentException"/> when it runs on a section without such a value.
    /// </summary>
    internal static EditCommand SetValueAt(SectionRef target, string name, int line, int index, ValueBase value)
    {
        CheckName(name);
        return new SetValueCommand($"Set {name}", target, name, line, index, value);
    }

    /// <summary>
    /// Sets the lines of a group of names as one block, in the order of <paramref name="lines"/>. The block takes the
    /// place of the first line of any of the names, or goes at the end of the section. The other lines keep their
    /// order. An example is the encounter lines of a zone with their faction lines (see
    /// <see cref="SectionKeys.LinkedGroups"/>). The engine gives each faction line to the encounter line before it. So
    /// one name alone cannot keep the order. An empty block removes the names.
    /// </summary>
    /// <param name="names">The names that the block replaces. Each line of the block has one of them.</param>
    /// <param name="label">The label of the edit.</param>
    /// <exception cref="ArgumentException">A name is not valid, or a line of the block has another name.</exception>
    internal static EditCommand SetLineBlock(SectionRef target, IReadOnlyList<string> names, IReadOnlyList<EntryView> lines,
        string label)
    {
        if (names.Count == 0)
            throw new ArgumentException("Give one or more names.", nameof(names));
        foreach (var name in names)
            CheckName(name);
        if (lines.FirstOrDefault(l => !names.Contains(l.Name, StringComparer.OrdinalIgnoreCase)) is { } other)
            throw new ArgumentException($"The line '{other.Name}' is not a line of the block.", nameof(lines));
        return new SetLineBlockCommand(label, target, names, lines);
    }

    /// <summary>
    /// Removes <paramref name="count"/> lines of a section from the line at <paramref name="first"/> (from 0, among all
    /// lines of the section). The other lines keep their order. The command throws an <see cref="ArgumentException"/>
    /// when it runs on a section without these lines, and an <see cref="InvalidOperationException"/> when the lines
    /// hold the key line.
    /// </summary>
    internal static EditCommand RemoveEntries(SectionRef target, int first, int count) =>
        new RemoveEntriesCommand($"Remove lines of {target.Id}", target, first, count);

    /// <summary>Sets the text of a key in the active mod's strings.json, or removes it when the text is null.</summary>
    public static EditCommand SetText(StringKind kind, string key, string? text)
    {
        if (!ModStrings.IsValidKey(key))
            throw new ArgumentException($"The key '{key}' must use letters, digits, '_', '-' or '.'.", nameof(key));
        if (text is { Length: 0 })
            throw new ArgumentException("The text cannot be empty. Use null to remove it.", nameof(text));
        return new SetTextCommand($"Set text {key}", kind, key, text);
    }

    /// <summary>
    /// Sets the text of an editor file of the active mod: a graph file or a mission prefab (section 6.8 of the M6b
    /// design). Null makes it a file that does not exist, so a save deletes it. The command throws an
    /// <see cref="ArgumentException"/> when it runs with a path outside the folders of the editor files.
    /// </summary>
    /// <param name="relativePath">The path relative to the mod folder, for example
    /// <c>graphs\DATA\MISSIONS\M02\m02.ini</c>.</param>
    internal static EditCommand SetModFile(string relativePath, string? text) =>
        new SetModFileCommand($"Set {relativePath}", relativePath, text);

    /// <summary>Runs commands in order, as one edit.</summary>
    public static EditCommand Group(string label, IReadOnlyList<EditCommand> commands) => new GroupCommand(label, commands, null);

    /// <summary>
    /// Runs commands in order, as one edit, only while <paramref name="current"/> is true. An edit whose commands hold
    /// values that it read from the edit view uses it, so that it never runs on a newer state and undoes the changes in
    /// between (see <see cref="EditSession.Snapshot"/>).
    /// </summary>
    internal static EditCommand Group(string label, IReadOnlyList<EditCommand> commands, Func<bool> current) =>
        new GroupCommand(label, commands, current);

    internal abstract void Run(EditSession.CommandScope scope);

    private static void CheckName(string name)
    {
        if (PatchFormatter.NameError(name) is { } error)
            throw new ArgumentException(error, nameof(name));
    }

    /// <summary>The key entry name of a section, or null when its type has no key.</summary>
    private static string? KeyName(Section section) =>
        SectionKeys.For(section.Name) is { Kind: SectionMatchKind.Keyed } match ? match.KeyEntry : null;

    private sealed class SetModFileCommand(string label, string relativePath, string? text) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope) => scope.ModFile(relativePath).SetText(text);
    }

    private sealed class SetLinesCommand(string label, SectionRef target, string name,
        IReadOnlyList<IReadOnlyList<ValueBase>> lines) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var old = document.Working[index];
            if (string.Equals(KeyName(old), name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{name}' is the key of {target.Id}. Use Rename to change it.");
            document.Replace(index, SectionEdits.WithLines(old, name, lines), [name]);
            scope.Changed(target, structural: false);
        }
    }

    private sealed class SetLineTextsCommand(string label, SectionRef target, string name, IReadOnlyList<string> lines)
        : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var values = ReadLines(document, index, name, lines, out var error) ??
                         throw new ArgumentException(error, nameof(lines));
            new SetLinesCommand(Label, target, name, values).Run(scope);
        }
    }

    /// <summary>
    /// The values of the lines of a name of a section from their value texts, as <see cref="SetLinesFromText"/> gives
    /// them when it runs. Null, with the reason, when a patch line cannot hold a text.
    /// </summary>
    internal static List<IReadOnlyList<ValueBase>>? ReadLines(EditDocument document, int index, string name,
        IReadOnlyList<string> lines, out string? error)
    {
        // The merge gives the n-th patch line of a name the type rules of the n-th line of that name in B.
        var replaced = document.BaseSection(document.IdentityAt(index))?.Where(e => Is(e, name)).ToList() ?? [];
        var values = new List<IReadOnlyList<ValueBase>>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!PatchFormatter.TryReadLine(name, lines[i], i < replaced.Count ? replaced[i] : null, document.IsBinary,
                    out var line, out error))
                return null;
            values.Add(line);
        }
        error = null;
        return values;
    }

    private sealed class RevertCommand(string label, SectionRef target, string? name) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var id = document.IdentityAt(index);
            var lower = document.BaseSection(id) ??
                        throw new InvalidOperationException($"{target.Id}: the active mod adds this section, so the " +
                                                            "lower layer has no lines for it.");
            var old = document.Working[index];
            if (name != null)
            {
                // The lines of a group follow each other in the order of B. One name alone cannot keep that order.
                if (SectionKeys.LinkedGroups(old.Name).Any(g => g.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"{target.Id}: '{name}' belongs to a group of lines that " +
                                                        "belong together. Revert the whole section.");
                var lowerLines = lower.Where(e => Is(e, name)).ToList();
                if (document.Pins(id).Contains(name) ||
                    !SectionComparer.SameLines(lowerLines, old.Where(e => Is(e, name)).ToList(), document.IsBinary))
                {
                    var lines = lowerLines.Select(e => (IReadOnlyList<ValueBase>)e.ToList()).ToList();
                    document.Replace(index, SectionEdits.WithLines(old, name, lines), [name]);
                }
            }
            else if (!ReferenceEquals(old, lower))
            {
                // W shares the sections of B that no patch and no edit changed, so the section of B can go back into W.
                // The names lose their pins.
                document.Replace(index, lower, old.Concat(lower).Select(e => e.Name).ToList());
            }
            // A revert of a section that the active mod renames gives it its key of B again (milestone M3). The scene
            // then keeps its item, as for a rename.
            var reverted = new SectionRef(document.Path, document.IdentityAt(index));
            if (reverted.Id.Equals(id))
            {
                scope.Changed(target, structural: false);
                return;
            }
            scope.Changed(target, structural: true);
            scope.Changed(reverted, structural: true);
            scope.Renamed(target, reverted);
        }
    }

    private static bool Is(Entry entry, string name) => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private sealed class AddSectionCommand(string label, string path, string type, IReadOnlyList<EntryView> entries,
        SectionRef? parent) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var document = scope.Document(path);
            int at;
            if (SectionKeys.IsChild(type))
            {
                if (parent == null)
                    throw new InvalidOperationException($"A [{type}] section needs a parent section.");
                var (parentDocument, p) = scope.Find(parent);
                if (!ReferenceEquals(parentDocument, document))
                    throw new InvalidOperationException("The parent section must be in the same file.");
                if (SectionKeys.IsChild(document.Working[p].Name))
                    throw new InvalidOperationException($"{parent.Id} is a child section. It cannot be a parent.");
                at = p + 1 + SectionKeys.ChildCount(document.Working, p);
            }
            else
            {
                if (parent != null)
                    throw new InvalidOperationException($"Only a child section has a parent. [{type}] is not a child section.");
                at = document.Working.Count;
            }
            document.Insert(at, SectionEdits.Build(type, entries));
            scope.Changed(new SectionRef(document.Path, document.IdentityAt(at)), structural: true);
        }
    }

    private sealed class DeleteSectionCommand(string label, SectionRef target) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var children = SectionKeys.IsChild(document.Working[index].Name)
                ? 0
                : SectionKeys.ChildCount(document.Working, index);
            document.Remove(index, 1 + children);
            scope.Changed(target, structural: true);
        }
    }

    /// <param name="onlyAdded">True to refuse a section of the lower layer.</param>
    private sealed class RenameCommand(string label, SectionRef target, string newKey, bool onlyAdded) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var section = document.Working[index];
            var keyName = KeyName(section) ??
                          throw new InvalidOperationException($"A [{section.Name}] section has no key to change.");
            if (document.BaseIdOf(document.IdentityAt(index)) is { } before)
            {
                if (onlyAdded)
                    throw new InvalidOperationException(OnlyAddedReason(target.Id));
                if (SectionKeys.IsChild(section.Name))
                    throw new InvalidOperationException(
                        $"{target.Id}: a patch cannot give a child section of the lower layer a new key.");
                // The identities of the child sections hold the identity of their parent (review finding R20 of M3).
                if (SectionKeys.ChildCount(document.Working, index) > 0)
                    throw new InvalidOperationException(
                        $"{target.Id}: the editor cannot give a section of the lower layer with child sections a new key.");
                // The merge would find two sections with the key, also when W deleted the other one.
                if (!string.Equals(before.Key, newKey, StringComparison.OrdinalIgnoreCase) &&
                    document.Base.Any(s => s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(SectionIdentity.KeyOf(s), newKey, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"Another [{section.Name}] section of the lower layer has {keyName} = {newKey}.");
            }
            if (string.Equals(SectionIdentity.KeyOf(section), newKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(SameKeyReason(target.Id));
            if (document.Working.Any(s => s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(SectionIdentity.KeyOf(s), newKey, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Another [{section.Name}] section has {keyName} = {newKey}.");
            document.Replace(index, SectionEdits.WithLines(section, keyName, [[new StringValue(newKey)]]), [keyName]);
            var renamed = new SectionRef(document.Path, document.IdentityAt(index));
            scope.Changed(target, structural: true);
            scope.Changed(renamed, structural: true);
            scope.Renamed(target, renamed);
        }
    }

    private sealed class SetValueCommand(string label, SectionRef target, string name, int line, int index,
        ValueBase value) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, i) = scope.Find(target);
            var old = document.Working[i];
            if (string.Equals(KeyName(old), name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{name}' is the key of {target.Id}. Use Rename to change it.");
            var changed = SectionEdits.WithValue(old, name, line, index, value) ??
                          throw new ArgumentException($"{target.Id} has no value {index + 1} in line {line + 1} of '{name}'.");
            document.Replace(i, changed, [name]);
            scope.Changed(target, structural: false);
        }
    }

    private sealed class SetLineBlockCommand(string label, SectionRef target, IReadOnlyList<string> names,
        IReadOnlyList<EntryView> lines) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index) = scope.Find(target);
            var old = document.Working[index];
            if (KeyName(old) is { } key && names.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{key}' is the key of {target.Id}. Use Rename to change it.");
            document.Replace(index, SectionEdits.WithBlock(old, names, lines), names);
            scope.Changed(target, structural: false);
        }
    }

    private sealed class RemoveEntriesCommand(string label, SectionRef target, int first, int count) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, i) = scope.Find(target);
            var old = document.Working[i];
            if (first < 0 || count < 1 || first + count > old.Count)
                throw new ArgumentException($"{target.Id} has no lines {first + 1} to {first + count}.");
            var removed = old.Skip(first).Take(count).ToList();
            if (KeyName(old) is { } key && removed.Any(e => Is(e, key)))
                throw new InvalidOperationException($"{target.Id}: the lines hold the key line. Delete the section instead.");
            document.Replace(i, SectionEdits.Without(old, first, count),
                removed.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            scope.Changed(target, structural: false);
        }
    }

    private sealed class SetTextCommand(string label, StringKind kind, string key, string? text) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope) => scope.Text = scope.Text.With(kind, key, text);
    }

    private sealed class GroupCommand(string label, IReadOnlyList<EditCommand> commands, Func<bool>? current)
        : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            if (current?.Invoke() == false)
                throw new InvalidOperationException($"{Label}: the edit view changed after the edit was made. Make the edit again.");
            foreach (var command in commands)
                command.Run(scope);
        }
    }
}

/// <summary>Builds the new sections of the edit commands.</summary>
internal static class SectionEdits
{
    /// <summary>
    /// A copy of <paramref name="old"/> in which <paramref name="lines"/> take the place of the lines of
    /// <paramref name="name"/>: at the first line of the name, or at the end. The lines keep the letter case of the
    /// name in the section.
    /// </summary>
    public static Section WithLines(Section old, string name, IReadOnlyList<IReadOnlyList<ValueBase>> lines)
    {
        var spelling = old.FirstOrDefault(e => Is(e, name))?.Name ?? name;
        var result = new Section(old.Name, old.Count) { File = old.File, Line = old.Line };
        var placed = false;
        foreach (var entry in old)
        {
            if (!Is(entry, name))
                result.Add(Line(result, entry.Name, entry, entry.Line));
            else if (!placed)
            {
                Add(result, spelling, lines);
                placed = true;
            }
        }
        if (!placed)
            Add(result, spelling, lines);
        return result;
    }

    /// <summary>
    /// A copy of <paramref name="old"/> in which <paramref name="lines"/> take the place of the lines of each of
    /// <paramref name="names"/>, as one block. The block goes to the first line of any of the names, or to the end.
    /// </summary>
    public static Section WithBlock(Section old, IReadOnlyList<string> names, IReadOnlyList<EntryView> lines)
    {
        var result = new Section(old.Name, old.Count) { File = old.File, Line = old.Line };
        var placed = false;
        foreach (var entry in old)
        {
            if (!names.Any(n => Is(entry, n)))
                result.Add(Line(result, entry.Name, entry, entry.Line));
            else if (!placed)
            {
                foreach (var line in lines)
                    result.Add(Line(result, line.Name, line.Values));
                placed = true;
            }
        }
        if (!placed)
        {
            foreach (var line in lines)
                result.Add(Line(result, line.Name, line.Values));
        }
        return result;
    }

    /// <summary>
    /// A copy of <paramref name="old"/> in which value <paramref name="index"/> of the line <paramref name="line"/> of
    /// <paramref name="name"/> is <paramref name="value"/>, or null when the section has no such value.
    /// </summary>
    public static Section? WithValue(Section old, string name, int line, int index, ValueBase value)
    {
        var result = new Section(old.Name, old.Count) { File = old.File, Line = old.Line };
        var n = 0;
        var done = false;
        foreach (var entry in old)
        {
            if (Is(entry, name) && n++ == line && index >= 0 && index < entry.Count)
            {
                result.Add(Line(result, entry.Name, entry.Select((v, i) => i == index ? value : v), entry.Line));
                done = true;
            }
            else
                result.Add(Line(result, entry.Name, entry, entry.Line));
        }
        return done ? result : null;
    }

    /// <summary>A copy of <paramref name="old"/> without <paramref name="count"/> lines from the line at
    /// <paramref name="first"/>.</summary>
    public static Section Without(Section old, int first, int count)
    {
        var result = new Section(old.Name, old.Count) { File = old.File, Line = old.Line };
        for (var i = 0; i < old.Count; i++)
        {
            if (i < first || i >= first + count)
                result.Add(Line(result, old[i].Name, old[i], old[i].Line));
        }
        return result;
    }

    public static Section Build(string type, IEnumerable<EntryView> entries)
    {
        var result = new Section(type);
        foreach (var entry in entries)
            result.Add(Line(result, entry.Name, entry.Values));
        return result;
    }

    private static void Add(Section section, string name, IReadOnlyList<IReadOnlyList<ValueBase>> lines)
    {
        foreach (var values in lines)
            section.Add(Line(section, name, values));
    }

    /// <summary>A new entry of <paramref name="section"/>. It shares the values, because a value cannot change.</summary>
    /// <param name="line">The line number in the file that the entry comes from, for messages, or -1.</param>
    private static Entry Line(Section section, string name, IEnumerable<ValueBase> values, int line = -1)
    {
        var entry = new Entry(section, name) { Line = line };
        foreach (var value in values)
            entry.Add(value);
        return entry;
    }

    private static bool Is(Entry entry, string name) => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}
