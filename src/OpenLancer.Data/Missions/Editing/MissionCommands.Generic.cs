using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>Inserts any line into a section of a mission file, except a second key line (section 4.9 of the M6b
/// design). The raw INI editor and the inverses use it.</summary>
public sealed record InsertLine(ItemRef Section, LineSpec Line) : MissionCommand
{
    /// <summary>The row that the line goes before, or null for the end.</summary>
    public int? Before { get; init; }

    protected override string Describe() => $"Add {Line.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Section, lines: true);
        d.Editable(document, index);
        var section = document.Working[index];
        var line = d.Build(section.Name, LinePlace.Section, Line, null);
        if (d.IsKeyEntry(section, line.Key) && section.Any(e => e.Name.Equals(line.Key, StringComparison.OrdinalIgnoreCase)))
            throw d.Refuse(MissionErrors.KeyLineError(line.Key));
        var entries = section.ToList();
        var at = d.Place(document, index, Before, entries.Count);
        entries.Insert(at, d.NewEntry(document, index, line.Key, line.Texts, entries.Take(at)));
        d.SetLines(document, index, entries, inverse: false, RowMap.OfInsert(entries.Count - 1, at));
        var row = MissionDraft.RowOf(document, index, at);
        d.Adds.Add(row.Reference);
        d.Inverse(new RemoveLine(row));
    }
}

/// <summary>Removes any line of a section of a mission file, except the first key line.</summary>
public sealed record RemoveLine(RowRef Row) : MissionCommand
{
    protected override string Describe() => "Remove line";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        var section = document.Working[index];
        d.RefuseKeyLine(section, row);
        var entries = section.ToList();
        var old = entries[row];
        entries.RemoveAt(row);
        d.SetLines(document, index, entries, inverse: false, RowMap.OfRemoval(entries.Count + 1, [row]));
        d.Inverse(new InsertLine(MissionDraft.ItemOf(document, index), MissionDraft.RawLine(old)) { Before = row });
    }
}

/// <summary>Moves any line of a section before another row, or to the end. The first key line cannot move.</summary>
public sealed record MoveLine(RowRef Row) : MissionCommand
{
    /// <summary>The row before the move that the line goes before, or null for the end.</summary>
    public int? Before { get; init; }

    protected override string Describe() => "Move line";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        d.RefuseKeyLine(document.Working[index], row);
        d.Move(document, index, row, Before, actionsOnly: false);
    }
}

/// <summary>Sets one whole line of a section, also with a new key. The first key line cannot change: use a
/// rename.</summary>
public sealed record SetLine(RowRef Row, LineSpec Line) : MissionCommand
{
    protected override string Describe() => $"Set {Line.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        var section = document.Working[index];
        d.RefuseKeyLine(section, row);
        var line = d.Build(section.Name, LinePlace.Section, Line, section[row]);
        if (d.IsKeyEntry(section, line.Key))
            throw d.Refuse(MissionErrors.KeyLineError(line.Key));
        var entries = section.ToList();
        var old = entries[row];
        entries[row] = d.NewEntry(document, index, line.Key, line.Texts, entries.Take(row));
        if (!d.SetLines(document, index, entries, inverse: false))
            return;
        var reference = MissionDraft.RowOf(document, index, row);
        d.Changes.Add(reference.Reference);
        d.Inverse(new SetLine(reference, MissionDraft.RawLine(old)));
    }
}

/// <summary>Sets one value of a line: a value of the schema by its name, or a value by its index from 0.</summary>
public sealed record SetLineValue(RowRef Row, MissionValue Value) : MissionCommand
{
    /// <summary>The name of the value in the schema, or null when <see cref="Index"/> gives the value.</summary>
    public string? Name { get; init; }

    /// <summary>The index of the value in the line, from 0, when <see cref="Name"/> is null.</summary>
    public int? Index { get; init; }

    protected override string Describe() => $"Set {Name ?? "value"}";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        d.RefuseKeyLine(document.Working[index], row);
        if (Name != null)
        {
            d.SetValue(document, index, row, Name, Value);
            return;
        }
        var at = Index ?? throw d.Refuse(new EditError(EditErrorKind.InvalidArgument, MissionErrors.NoSuchValue,
            "Give the name of the value or its index."));
        var entries = document.Working[index].ToList();
        var texts = MissionValues.TextsOf(entries[row]).ToList();
        if (at < 0 || at > texts.Count)
            throw d.Refuse(new EditError(EditErrorKind.InvalidArgument, MissionErrors.NoSuchValue,
                $"Line {row} of {MissionDraft.Owner(document, index)} has {texts.Count} values, so value {at} cannot " +
                "change. Read the line again, then try again."));
        var text = Value.Scalar ?? throw d.Refuse(MissionErrors.ValueKindError(Value.ToString(), "one value", "The value",
            "one value"));
        MissionValues.CheckText(text);
        if (at == texts.Count)
            texts.Add(text);
        else
            texts[at] = text;
        d.SetTexts(document, index, row, texts);
    }
}

/// <summary>Adds a section of a mission type to a mission file: a script or a ship build file. A section of a keyed type
/// without its key line gets a made name.</summary>
public sealed record AddSection(ScriptRef Script, string Type, IReadOnlyList<LineSpec> Lines) : MissionCommand
{
    /// <summary>The section that the new section goes before, or null for the end of the file.</summary>
    public ItemRef? Before { get; init; }

    protected override string Describe() => $"Add [{Type}]";

    internal override void Run(MissionDraft d)
    {
        var document = d.File(Script.Path);
        if (!PatchFormatter.TryFormatHeader(Type.Trim(), out _, out var headerError))
            throw d.Refuse(MissionErrors.NotAKeyError(Type, "mission file") with { Message = headerError });
        var type = d.TypeName(Type.Trim());
        var built = Lines.Select(l => d.Build(type, LinePlace.Section, l, null)).ToList();
        var entries = new List<Entry>();
        var keyEntry = d.KeyEntryOf(type);
        var named = false;
        if (keyEntry != null && !built.Any(b => b.Key.Equals(keyEntry, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(d.NewEntry(document, null, keyEntry, [d.NewName(document, type, null)], 0));
            named = true;
        }
        foreach (var line in built)
        {
            // The first key line names the section, also after a line before it, as a pasted trigger can have (TR2).
            if (keyEntry != null && !named && line.Key.Equals(keyEntry, StringComparison.OrdinalIgnoreCase) &&
                line.Texts.Count > 0)
            {
                d.NewName(document, type, line.Texts[0]);
                named = true;
            }
            entries.Add(d.NewEntry(document, null, line.Key, line.Texts,
                entries.Count(e => e.Name.Equals(line.Key, StringComparison.OrdinalIgnoreCase))));
        }
        var at = Before == null ? document.Working.Count : d.Item(Before).Index;
        at = d.Insert(document, at, MissionDraft.NewSection(type, entries));
        var reference = d.RefOf(document, at);
        d.Adds.Add(reference);
        d.Select = reference;
        d.Inverse(new RemoveSection(MissionDraft.ItemOf(document, at)));
    }
}

/// <summary>Removes a section of a mission file. Lines that name it stay and show "Missing".</summary>
public sealed record RemoveSection(ItemRef Section) : MissionCommand
{
    protected override string Describe() => $"Delete {Section.Section}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Section);
        d.Editable(document, index, members: true);
        d.RemoveSection(document, index, ReferencesMode.Keep, OpenLancer.Data.Editing.Scene.DeleteReason.Target, null);
    }
}

/// <summary>Puts a section with exact entries at an index of a file. The inverses and the raw INI editor use it.</summary>
/// <param name="Index">The index of the section in the file. A larger index puts the section at the end.</param>
public sealed record RestoreSection(ScriptRef Script, int Index, RawSection Section) : MissionCommand
{
    protected override string Describe() => $"Restore [{Section.Type}]";

    internal override void Run(MissionDraft d)
    {
        var document = d.File(Script.Path);
        var type = d.TypeName(Section.Type);
        var entries = d.Entries(document, null, Section.Entries);
        var section = MissionDraft.NewSection(type, entries);
        if (SectionIdentity.KeyOf(section) is { } key && document.Working.Any(s =>
                s.Name.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(SectionIdentity.KeyOf(s), key, StringComparison.OrdinalIgnoreCase)) &&
            !document.IsWholeFile)
            throw d.Refuse(MissionErrors.NameTakenError(document.Path, MissionNames.KindWord(type), key));
        var at = Math.Clamp(Index, 0, document.Working.Count);
        // A deleted section of B that comes back with the lines of B is the section of B again, so the writer writes
        // no change for it.
        at = d.InsertRestored(document, at, section);
        var reference = d.RefOf(document, at);
        d.Adds.Add(reference);
        d.Inverse(new RemoveSection(MissionDraft.ItemOf(document, at)));
    }
}

/// <summary>Sets all entries of a section, also lines before the nickname. The first nickname cannot change: use a
/// rename. The raw INI editor and the inverses use it.</summary>
public sealed record ReplaceSectionBody(ItemRef Section, IReadOnlyList<RawEntry> Entries) : MissionCommand
{
    protected override string Describe() => $"Edit {Section.Section}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Section, lines: true);
        d.Editable(document, index);
        var old = document.Working[index];
        var entries = d.Entries(document, document.BaseSection(document.IdentityAt(index)), Entries);
        var keyEntry = d.KeyEntryOf(old.Name);
        if (keyEntry != null)
        {
            var before = old.FirstOrDefault(e => e.Name.Equals(keyEntry, StringComparison.OrdinalIgnoreCase));
            var after = entries.FirstOrDefault(e => e.Name.Equals(keyEntry, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(before is { Count: > 0 } ? MissionIndex.Text(before[0]) : null,
                    after is { Count: > 0 } ? MissionIndex.Text(after[0]) : null, StringComparison.Ordinal))
                throw d.Refuse(MissionErrors.KeyLineError(keyEntry));
        }
        if (!d.SetLines(document, index, entries, inverse: false))
            return;
        d.Changes.Add(d.RefOf(document, index));
        d.Inverse(new ReplaceSectionBody(MissionDraft.ItemOf(document, index), MissionDraft.RawOf(old)));
    }
}

/// <summary>Puts back the lines of B of a section, or of one key of it (<c>EditCommand.Revert</c>). A section that the
/// active mod adds refuses.</summary>
public sealed record RevertSection(ItemRef Section) : MissionCommand
{
    /// <summary>The key whose lines go back, or null for the whole section.</summary>
    public string? Name { get; init; }

    protected override string Describe() => Name == null ? $"Revert {Section.Section}" : $"Revert {Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Section);
        d.Editable(document, index);
        var id = document.IdentityAt(index);
        var lower = document.BaseSection(id) ?? throw d.Refuse(MissionErrors.NotInBaseError(id.ToString()));
        var old = document.Working[index];
        if (Name == null)
        {
            if (ReferenceEquals(old, lower))
                return;
            var before = d.RefOf(document, index);
            d.Replace(document, index, lower, old.Concat(lower).Select(e => e.Name));
            var after = MissionDraft.ItemOf(document, index);
            d.Changes.Add(d.RefOf(document, index));
            if (!document.IdentityAt(index).Equals(id))
            {
                d.Renames.Add(new ResultRename(before, d.RefOf(document, index)));
                if (MissionIndex.Is(id.Type, "Trigger") && id.Key != null &&
                    TriggerRules.Nickname(document.Working[index]) is { } name)
                    d.TriggerRenamed(document, id.Key, name);
                d.Inverse(new CommandGroup(
                [
                    d.RenameBack(document, index, id.Key!),
                    new ReplaceSectionBody(new ItemRef(document.Path, id), MissionDraft.RawOf(old)),
                ]));
            }
            else
                d.Inverse(new ReplaceSectionBody(after, MissionDraft.RawOf(old)));
            return;
        }
        var entries = old.Where(e => !e.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var lines = lower.Where(e => e.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var at = old.ToList().FindIndex(e => e.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));
        entries.InsertRange(at < 0 ? entries.Count : at, lines);
        d.SetLines(document, index, entries);
    }
}

/// <summary>Writes a section again with <c>ol.base</c> of the current B, after the base changed (decision D429, section
/// 6.3 of the M6b design). W does not change.</summary>
public sealed record AcceptBase(ItemRef Section) : MissionCommand
{
    /// <summary>The hash to write, or null for the hash of B now. The inverse gives the old hash with it.</summary>
    public string? Hash { get; init; }

    protected override string Describe() => $"Keep my lines of {Section.Section}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Section);
        d.Editable(document, index);
        var id = document.IdentityAt(index);
        var old = document.WrittenBaseHash(id);
        var hash = Hash?.Trim().ToLowerInvariant() ?? (document.BaseSection(id) is { } lower ? SectionHash.Of(lower) : null);
        if (old == null || hash == null || SectionHash.Same(old, hash))
            throw d.Refuse(MissionErrors.NoBaseDifferenceError(id.ToString()));
        if (!SectionHash.IsHash(hash))
            throw d.Refuse(MissionErrors.ValueKindError(hash, "a hash", "The hash", "16 hex digits"));
        d.SetBaseHash(document, index, hash);
        d.Changes.Add(d.RefOf(document, index));
        d.Inverse(new AcceptBase(MissionDraft.ItemOf(document, index)) { Hash = old });
    }
}

/// <summary>
/// Runs commands in order as one step. The inverses use it. A body guard names a section and the engine-form hash of
/// its body (decision D666): when the body differs, the group refuses with <c>SectionChanged</c> (section 4.8 of the
/// M6b design).
/// </summary>
public sealed record CommandGroup(IReadOnlyList<MissionCommand> Commands) : MissionCommand
{
    /// <summary>The body guards: the reference of each section and the hash that it must have.</summary>
    public IReadOnlyDictionary<string, string> ExpectBodies { get; init; } = new Dictionary<string, string>();

    protected override string Describe() => Commands.Count == 1 ? Commands[0].HistoryLabel : "Mission edits";

    internal override void Run(MissionDraft d)
    {
        // An inverse with body guards changes back the sections that its edit left as they are now. So it can change
        // the lines of a member of a linked prefab instance, but only of a member that a guard names (section 4.8 of the
        // M6b design).
        var guarded = new List<string>();
        foreach (var (reference, hash) in ExpectBodies)
        {
            var item = ItemRef.Parse(reference);
            var document = d.Session.OpenDocument(item.Script);
            var index = document?.IndexOf(item.Section) ?? -1;
            if (index < 0 && document != null && MissionIndex.Is(item.Section.Type, "Trigger") && item.Section.Key is { } name)
                index = MissionDraft.TriggerIndex(document, name);
            if (document == null || index < 0 || !string.Equals(SectionHash.Of(document.Working[index]), hash,
                    StringComparison.OrdinalIgnoreCase))
                throw d.Refuse(MissionErrors.SectionChangedError(item.Section.ToString()) with { Target = reference });
            if (d.GuardedSections.Add(d.RefOf(document, index)))
                guarded.Add(d.RefOf(document, index));
        }
        var inverses = new List<MissionCommand>();
        try
        {
            foreach (var command in Commands)
                inverses.AddRange(d.RunInner(command));
        }
        finally
        {
            d.GuardedSections.ExceptWith(guarded);
        }
        inverses.Reverse();
        if (inverses.Count > 0)
            d.Inverse(inverses.Count == 1 ? inverses[0] : new CommandGroup(inverses));
    }
}

/// <summary>Sets a text of the active mod in <c>strings.json</c>, or removes it with null. The inverse of
/// <see cref="SetLineText"/> uses it.</summary>
public sealed record SetText(string Key) : MissionCommand
{
    public string? Text { get; init; }

    protected override string Describe() => $"Set text {Key}";

    internal override void Run(MissionDraft d)
    {
        if (!ModStrings.IsValidKey(Key))
            throw d.Refuse(MissionErrors.NotTextIdError("The key") with
            {
                Message = $"'{Key}' is not a text key. Use letters, digits, '_', '-' or '.'.",
            });
        var old = d.Text.Get(StringKind.String, Key);
        if (string.Equals(old, Text, StringComparison.Ordinal))
            return;
        d.Text = d.Text.With(StringKind.String, Key, string.IsNullOrEmpty(Text) ? null : Text);
        d.Inverse(new SetText(Key) { Text = old });
    }
}
