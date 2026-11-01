using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Missions.Editing;

internal sealed partial class MissionDraft
{
    /// <summary>A refusal of the command that runs now.</summary>
    public EditException Refuse(EditError error) => new(error with { Index = error.Index ?? Index });

    /// <summary>The key and the value texts of a line, with the notes of the line.</summary>
    /// <param name="old">The line that the new line replaces, or null.</param>
    public BuiltLine Build(string sectionType, LinePlace place, LineSpec spec, Entry? old = null)
    {
        try
        {
            var line = MissionValues.Build(Schema, sectionType, place, spec, old);
            Note(line);
            return line;
        }
        catch (EditException e)
        {
            throw Refuse(e.Error);
        }
    }

    /// <summary>The specs of the values of a line of a section type.</summary>
    public IReadOnlyList<MissionValueSpec> SpecsOf(string sectionType, string key)
    {
        try
        {
            return MissionValues.Key(Schema, sectionType, LinePlace.Section, key).Specs;
        }
        catch (EditException)
        {
            return [];
        }
    }

    /// <summary>The specs of the values of a line of a section type, with the form of its first value for a key with
    /// forms, for example the type of an objective.</summary>
    public IReadOnlyList<MissionValueSpec> SpecsOf(string sectionType, Entry entry)
    {
        try
        {
            return MissionValues.SpecsOf(Schema, sectionType, entry);
        }
        catch (EditException)
        {
            return [];
        }
    }

    /// <summary>The type of a section with the letter case of the schema, or as given for another type.</summary>
    public string TypeName(string type)
    {
        type = type.Trim();
        if (MissionIndex.Is(type, "NPCShipArch"))
            return Schema.ShipBuild.Type;
        return Schema.Section(type)?.Type ?? type;
    }

    /// <summary>The key entry of a section type, for example <c>nickname</c>, or null for a type without a key, for
    /// example <c>[Mission]</c>.</summary>
    public string? KeyEntryOf(string type) =>
        SectionKeys.For(type, @"DATA\MISSIONS\x.ini") is { Kind: SectionMatchKind.Keyed } match ? match.KeyEntry : null;

    /// <summary>True for the key entry of the type of a section.</summary>
    public bool IsKeyEntry(Section section, string key) =>
        KeyEntryOf(section.Name) is { } entry && entry.Equals(key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refuses the first key line of a section: a rename changes it.</summary>
    public void RefuseKeyLine(Section section, int row)
    {
        if (KeyEntryOf(section.Name) is not { } entry)
            return;
        var first = section.ToList().FindIndex(e => e.Name.Equals(entry, StringComparison.OrdinalIgnoreCase));
        if (first == row)
            throw Refuse(MissionErrors.KeyLineError(entry));
    }

    /// <summary>A trigger name of a line value, after the checks of a patch line.</summary>
    public string TriggerName(string name)
    {
        name = name.Trim();
        try
        {
            MissionValues.CheckText(name);
        }
        catch (EditException e)
        {
            throw Refuse(e.Error);
        }
        if (name.Length == 0)
            throw Refuse(MissionErrors.ValueKindError("", "a trigger name", "The target", "the nickname of a trigger"));
        return name;
    }

    /// <summary>The row of a place in a section: a row from 0 up to the number of lines, or the end for null.</summary>
    public int Place(EditDocument document, int index, int? before, int count)
    {
        if (before is not { } at)
            return count;
        if (at < 0 || at > count)
            throw Refuse(MissionErrors.RowNotFoundError(Owner(document, index), at));
        return at;
    }

    /// <summary>Refuses a row that is not an action of a trigger. The error names the line in its target, so a caller
    /// with two rows can tell which one is not an action.</summary>
    public void RequireAction(EditDocument document, int index, IReadOnlyList<Entry> entries, int row)
    {
        if (row <= TriggerRules.NicknameLine(entries) || !TriggerRules.IsAction(entries[row].Name))
            throw Refuse(MissionErrors.NotAnActionError(Owner(document, index), row) with
            {
                Target = RowOf(document, index, row).Reference,
            });
    }

    // ---- Names ----

    /// <summary>
    /// A new name of a section type in a file: the requested name after the name rules, or a made name
    /// <c>{script stem}_{kind}_{n}</c> (section 4.3 of the M6b design, decision D683).
    /// </summary>
    /// <param name="stem">The first part of a made name, or null for the stem of the file.</param>
    public string NewName(EditDocument document, string type, string? requested, string? stem = null)
    {
        if (requested == null)
        {
            // In prefab mode, a made name is a role, without the name of a file.
            stem ??= IsTemplate(document) ? "role" : System.IO.Path.GetFileNameWithoutExtension(document.Path).ToLowerInvariant();
            var word = MissionNames.MadeWord(type);
            for (var n = 1;; n++)
            {
                var made = $"{stem}_{word}_{n.ToString(CultureInfo.InvariantCulture)}";
                if (made.Length > MissionNames.MaxFor(type))
                    made = $"{word}_{n.ToString(CultureInfo.InvariantCulture)}";
                if (NameProblem(document, -1, type, made) == null)
                    return made;
            }
        }
        var name = requested.Trim();
        if (!MissionNames.FormOk(name, MissionNames.MaxFor(type)))
            throw Refuse(MissionErrors.NameInvalidError(name, MissionNames.MaxFor(type)));
        if (NameProblem(document, -1, type, name) is { } problem)
            throw Refuse(problem);
        return name;
    }

    /// <summary>Checks a new name of the section at an index. A rename back to the name in B, and each inverse, skip the
    /// form rule (review finding RB4).</summary>
    public void CheckRename(EditDocument document, int index, string type, string name, bool skipForm)
    {
        if (!skipForm && !MissionNames.FormOk(name, MissionNames.MaxFor(type)))
            throw Refuse(MissionErrors.NameInvalidError(name, MissionNames.MaxFor(type)));
        if (name.Length == 0)
            throw Refuse(MissionErrors.NameInvalidError(name, MissionNames.MaxFor(type)));
        try
        {
            MissionValues.CheckText(name);
        }
        catch (EditException e)
        {
            throw Refuse(e.Error);
        }
        var section = document.Working[index];
        var old = SectionIdentity.KeyOf(section) ?? TriggerRules.Nickname(section);
        if (document.BaseIdOf(document.IdentityAt(index)) != null && !document.IsWholeFile &&
            string.Equals(old, name, StringComparison.OrdinalIgnoreCase))
            throw Refuse(MissionErrors.NameInvalidError(name, MissionNames.MaxFor(type)) with
            {
                Message = $"'{name}' differs from '{old}' only in letter case. The game finds names without letter " +
                          "case, so a patch cannot change only the letter case. Pick another name.",
            });
        if (NameProblem(document, index, type, name) is { } problem)
            throw Refuse(problem);
    }

    /// <summary>Why a name is not free in its kind in a file, or null (section 4.3 of the M6b design). A trigger name
    /// also needs a game id that no other trigger of the script has (TR4). B counts too, because a patch cannot give a
    /// section the key of another section of B.</summary>
    /// <param name="self">The index of the section that gets the name, or -1 for a new section.</param>
    private EditError? NameProblem(EditDocument document, int self, string type, string name)
    {
        var working = document.Working;
        for (var i = 0; i < working.Count; i++)
        {
            if (i == self || !working[i].Name.Equals(type, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = SectionIdentity.KeyOf(working[i]) ?? TriggerRules.Nickname(working[i]);
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return MissionErrors.NameTakenError(document.Path, MissionNames.KindWord(type), name);
        }
        var selfBase = self >= 0 ? document.BaseIdOf(document.IdentityAt(self)) : null;
        if (!document.IsWholeFile && document.Base.Any(s => s.Name.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                                                            string.Equals(SectionIdentity.KeyOf(s), name, StringComparison.OrdinalIgnoreCase) &&
                                                            !string.Equals(selfBase?.Key, name, StringComparison.OrdinalIgnoreCase)))
            return MissionErrors.NameTakenError(document.Path, MissionNames.KindWord(type), name);
        if (!MissionIndex.Is(type, "Trigger"))
            return null;
        var id = FLHash.CreateID(name);
        for (var i = 0; i < working.Count; i++)
        {
            if (i == self || !MissionIndex.Is(working[i].Name, "Trigger") || TriggerRules.Nickname(working[i]) is not { } other)
                continue;
            if (FLHash.CreateID(other) == id)
                return MissionErrors.NameIdTakenError(name, other);
        }
        return null;
    }

    // ---- Lines ----

    /// <summary>
    /// Puts new lines into the section at an index when they differ from its lines. With <paramref name="inverse"/>, the
    /// command gets the line commands that give the old lines back, and the change of the section is in the report. The
    /// row map of the request gets the change of the lines of a trigger, so the row moves follow each command (final
    /// finding 1 of MCP1-3).
    /// </summary>
    /// <param name="origins">For each new line, its row in the old lines, or -1 for a new line. A command that inserts,
    /// removes or moves a line at a known row gives them, because two same lines can make the compare of the lines pick
    /// the wrong one. Null compares the lines (<see cref="LineDiff.Origins"/>).</param>
    /// <returns>True when the lines changed.</returns>
    public bool SetLines(EditDocument document, int index, IReadOnlyList<Entry> entries, bool inverse = true,
        IReadOnlyList<int>? origins = null)
    {
        var old = document.Working[index].ToList();
        if (LineDiff.Same(old, entries))
            return false;
        var trigger = RowTrigger(document, index);
        SetBody(document, index, entries);
        if (trigger != null)
            rowMap.Changed(trigger, origins ?? LineDiff.Origins(old, entries), old.Count, StartCount);
        if (inverse)
        {
            Changes.Add(RefOf(document, index));
            Inverse(LineDiff.Inverse(ItemOf(document, index), old, entries));
        }
        return true;
    }

    /// <summary>
    /// Removes each line of a state key after the nickname line of a trigger, then puts one new line of the key with a
    /// text at the place of a new state line. A null text only removes the lines. The row map gets the removed rows and
    /// then the row of the new line, so each line between them moves by one row (final findings 3 of MCP1-3).
    /// </summary>
    public void SetStateLine(EditDocument document, int index, string key, string? text)
    {
        var entries = document.Working[index].ToList();
        var nickname = TriggerRules.NicknameLine(entries);
        var origins = Enumerable.Range(0, entries.Count).Where(i => i <= nickname || !MissionIndex.Is(entries[i].Name, key))
            .ToList();
        var kept = origins.Select(i => entries[i]).ToList();
        if (text != null)
        {
            var place = TriggerRules.StatePlace(kept);
            kept.Insert(place, NewEntry(document, index, key, [text], kept.Take(place)));
            origins.Insert(place, -1);
        }
        SetLines(document, index, kept, origins: origins);
    }

    /// <summary>Sets the value texts of one line, with the inverse that gives the old line back.</summary>
    public void SetTexts(EditDocument document, int index, int row, IReadOnlyList<string> texts)
    {
        var entries = document.Working[index].ToList();
        var old = entries[row];
        entries[row] = NewEntry(document, index, old.Name, texts, entries.Take(row));
        if (!SetLines(document, index, entries, inverse: false))
            return;
        var reference = RowOf(document, index, row);
        Changes.Add(reference.Reference);
        // The old line by position, so a retail value that the value checks refuse comes back exactly.
        Inverse(new SetLine(reference, RawLine(old)));
    }

    /// <summary>Sets one named value of a line, or a run of values such as a position. A gap before the value gets its
    /// default or its "none" word.</summary>
    public void SetValue(EditDocument document, int index, int row, string name, MissionValue value)
    {
        var section = document.Working[index];
        var entry = section[row];
        var specs = SpecsOf(section.Name, entry);
        var spec = specs.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                   throw Refuse(MissionErrors.NoSuchValueError(entry.Name, name, specs.Select(s => s.Name)));
        IReadOnlyList<string> converted;
        var notes = new List<string>();
        try
        {
            converted = MissionValues.Convert(Schema, spec, value, notes, entry);
        }
        catch (EditException e)
        {
            throw Refuse(e.Error);
        }
        Notes.AddRange(notes);
        var texts = MissionValues.TextsOf(entry).ToList();
        while (texts.Count < spec.Index)
            texts.Add(Gap(specs, texts.Count));
        if (spec.Rest)
            texts = [.. texts.Take(spec.Index), .. converted];
        else
        {
            for (var i = 0; i < converted.Count; i++)
            {
                if (spec.Index + i < texts.Count)
                    texts[spec.Index + i] = converted[i];
                else
                    texts.Add(converted[i]);
            }
        }
        SetTexts(document, index, row, texts);
    }

    /// <summary>The text of a gap before a later value: the default or the "none" word of the spec at the
    /// index.</summary>
    public string Gap(string sectionType, string key, int at) => Gap(SpecsOf(sectionType, key), at);

    /// <inheritdoc cref="Gap(string, string, int)"/>
    public string Gap(IReadOnlyList<MissionValueSpec> specs, int at)
    {
        var spec = specs.FirstOrDefault(s => s.Covers(at));
        if (spec?.Default != null && spec.Width == 1)
            return spec.Default;
        if (spec?.NoneToken != null)
            return spec.NoneToken;
        throw Refuse(MissionErrors.ValueMissingError(spec?.Label ?? $"Value {at.ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <summary>Sets one value of each of some lines of a section to a text, with the inverse that gives each old line
    /// back.</summary>
    public void ChangeValues(EditDocument document, int index, IReadOnlyList<(int Row, int Value)> values, string text,
        List<MissionCommand> inverse)
    {
        var entries = document.Working[index].ToList();
        var old = entries.ToList();
        foreach (var row in values.Select(v => v.Row).Distinct())
        {
            var texts = MissionValues.TextsOf(entries[row]).ToList();
            foreach (var (_, value) in values.Where(v => v.Row == row))
                texts[value] = text;
            entries[row] = NewEntry(document, index, entries[row].Name, texts, entries.Take(row));
        }
        if (!SetLines(document, index, entries, inverse: false))
            return;
        foreach (var row in values.Select(v => v.Row).Distinct())
        {
            var reference = RowOf(document, index, row);
            Changes.Add(reference.Reference);
            inverse.Add(new SetLine(reference, RawLine(old[row])));
        }
    }

    /// <summary>
    /// Removes lines of sections of a file. The rows of each section go from the last to the first, so each row stays
    /// valid. The inverse gets the inserts of the lines, from the first to the last of each section.
    /// </summary>
    public void RemoveLines(EditDocument document, IReadOnlyList<(int Section, int Row)> lines, List<MissionCommand> inverse)
    {
        foreach (var group in lines.GroupBy(l => l.Section))
        {
            var entries = document.Working[group.Key].ToList();
            var rows = group.Select(l => l.Row).Distinct().OrderBy(r => r).ToList();
            var removed = rows.Select(r => entries[r]).ToList();
            foreach (var row in rows.AsEnumerable().Reverse())
                entries.RemoveAt(row);
            SetLines(document, group.Key, entries, inverse: false, RowMap.OfRemoval(entries.Count + rows.Count, rows));
            var item = ItemOf(document, group.Key);
            for (var i = 0; i < rows.Count; i++)
                inverse.Add(new InsertLine(item, RawLine(removed[i])) { Before = rows[i] });
            Changes.Add(RefOf(document, group.Key));
        }
    }

    /// <summary>Marks lines that name a name that goes as "Missing" (decision D81).</summary>
    public void MarkMissing(EditDocument document, IReadOnlyList<(int Section, int Row)> lines, string name,
        string consequence)
    {
        GoneNames.Add(name);
        foreach (var (section, row) in lines)
            Missing.Add(new ResultMissing(RefOf(document, section), document.Working[section][row].Name, consequence, true));
    }

    /// <summary>Notes a rename of a trigger, so the row moves name the trigger by its new name.</summary>
    public void TriggerRenamed(EditDocument document, string from, string to)
    {
        rowDocuments[document.Path] = document;
        rowMap.Renamed(new TriggerRef(document.Path, from), new TriggerRef(document.Path, to));
    }

    /// <summary>The row map of the request, checked against W now.</summary>
    internal RowMap NetRows()
    {
        rowMap.Check(trigger => rowDocuments.TryGetValue(trigger.Script, out var document) &&
                              TriggerIndex(document, trigger.Name) is >= 0 and var at
            ? document.Working[at].Count
            : null);
        return rowMap;
    }

    /// <summary>The trigger of a section by its name now, or null for a section that is not a trigger.</summary>
    private TriggerRef? RowTrigger(EditDocument document, int index)
    {
        if (!MissionIndex.Is(document.Working[index].Name, "Trigger") ||
            TriggerRules.Nickname(document.Working[index]) is not { } name)
            return null;
        rowDocuments[document.Path] = document;
        return new TriggerRef(document.Path, name);
    }

    /// <summary>The number of lines of a trigger before the request, -1 when the trigger did not exist then, or null
    /// for a document without a state before the request, for example the template of a prefab.</summary>
    private int? StartCount(TriggerRef trigger) =>
        Before.TryGetValue(trigger.Script, out var state)
            ? TriggerIndex(state.Working, trigger.Name) is >= 0 and var at ? state.Working[at].Count : -1
            : null;

    /// <summary>
    /// Moves a line before another row of the section before the move, or to the end. The inverse moves it back. With
    /// <paramref name="actionsOnly"/>, the new place must be an action place.
    /// </summary>
    public void Move(EditDocument document, int index, int row, int? before, bool actionsOnly)
    {
        var entries = document.Working[index].ToList();
        var at = Place(document, index, before, entries.Count);
        var line = entries[row];
        entries.RemoveAt(row);
        var to = at > row ? at - 1 : at;
        if (actionsOnly && !TriggerRules.ActionPlace(entries, to))
            throw Refuse(MissionErrors.BadPlaceError());
        entries.Insert(to, line);
        if (!SetLines(document, index, entries, inverse: false, RowMap.OfMove(entries.Count, row, to)))
            return;
        var moved = RowOf(document, index, to);
        Changes.Add(moved.Reference);
        var back = row < to ? row : row + 1;
        Inverse(new MoveLine(moved) { Before = back >= entries.Count ? null : back });
    }

    // ---- Sections ----

    /// <summary>The raw form of a section: its type and each line with its value texts.</summary>
    public static RawSection RawSection(Section section) => new(section.Name, RawOf(section));

    /// <summary>Puts a section back at an index (see <see cref="EditDocument.InsertRestored"/>), or at the end of the
    /// file by the rule of <see cref="MovesBaseSections"/>.</summary>
    /// <returns>The index of the section in the file.</returns>
    public int InsertRestored(EditDocument document, int at, Section section)
    {
        at = PlaceOfNew(document, at, section);
        document.InsertRestored(at, section);
        if (!IsTemplate(document))
            Scope.Changed(new SectionRef(document.Path, document.IdentityAt(at)), structural: true);
        return at;
    }

    /// <summary>
    /// Removes a section of a mission file. The lines that name it stay and show "Missing", or go with
    /// <see cref="ReferencesMode.Remove"/>. The lines are the lines of the file, and for a ship build also the
    /// <c>[NPC]</c> lines of each script that names it. A name that another section of the type in the file still gives
    /// is not missing.
    /// </summary>
    public void RemoveSection(EditDocument document, int index, ReferencesMode references, DeleteReason why, string? cause)
    {
        var section = document.Working[index];
        var reference = RefOf(document, index);
        var inverse = new List<MissionCommand>();
        var name = SectionIdentity.KeyOf(section) ?? (MissionIndex.Is(section.Name, "Trigger") ? TriggerRules.Nickname(section) : null);
        if (name != null && !document.Working.Where((s, i) => i != index && s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase))
                .Any(s => string.Equals(SectionIdentity.KeyOf(s), name, StringComparison.OrdinalIgnoreCase)))
        {
            var kinds = MissionLinks.KindsOf(section.Name);
            var files = MissionIndex.Is(section.Name, "NPCShipArch")
                ? MissionScripts().Where(d => !ReferenceEquals(d, document) && FindsBuildIn(d, document, name))
                    .Prepend(document).ToList()
                : [document];
            foreach (var file in files)
            {
                var lines = MissionLinks.Naming(Schema, file.Working, kinds, name)
                    .Where(l => !(ReferenceEquals(file, document) && l.Section == index)).Select(l => (l.Section, l.Row))
                    .Distinct().ToList();
                if (ReferenceEquals(file, document))
                    lines = OutsideLines(document, index, lines);
                if (lines.Count == 0)
                    continue;
                if (references == ReferencesMode.Remove)
                    RemoveLines(ReferenceEquals(file, document) ? file : Script(file.Path), lines, inverse);
                else
                    MarkMissing(file, lines, name, "The game ignores this line, because no item has this name now.");
            }
        }
        var removed = Remove(document, index);
        inverse.Insert(0, new RestoreSection(new ScriptRef(document.Path), index, RawSection(removed)));
        Deletes.Add(new ResultDelete(reference, why, cause));
        Inverse(inverse.Count == 1 ? inverse[0] : new CommandGroup(inverse));
    }

    /// <summary>True when a script finds a ship build of a name in a file. The file is the ship build file of the
    /// script. Or it is a shared file, and the ship build file of the script has no build of the name.</summary>
    private bool FindsBuildIn(EditDocument script, EditDocument file, string name)
    {
        var own = Session.ShipBuildFileOf(script.Path);
        if (own != null && VfsPath.Comparer.Equals(own, file.Path))
            return true;
        if (Session.IsGuardedMissionFile(file.Path))
            return false;
        return own == null || Session.OpenDocument(own) is not { State.Exists: true } ownFile ||
               IndexOfKey(ownFile, "NPCShipArch", name) < 0;
    }

    /// <summary>The command that gives a section its old key back, without a change of the lines that name it.</summary>
    public MissionCommand RenameBack(EditDocument document, int index, string oldKey)
    {
        var section = document.Working[index];
        return MissionIndex.Is(section.Name, "Trigger") && TriggerRules.Nickname(section) is { } name
            ? new RenameTrigger(new TriggerRef(document.Path, name), oldKey) { ChangeReferences = false }
            : new RenameItem(ItemOf(document, index), oldKey) { ChangeReferences = false };
    }

    /// <summary>Runs a command inside the command that runs now, and gives the inverses that it recorded.</summary>
    public List<MissionCommand> RunInner(MissionCommand command)
    {
        var count = Inverses.Count;
        var mark = popMark;
        popMark = count;
        try
        {
            command.Run(this);
        }
        finally
        {
            popMark = mark;
        }
        var recorded = Inverses.Skip(count).ToList();
        Inverses.RemoveRange(count, Inverses.Count - count);
        return recorded;
    }

    /// <summary>Runs a command as the command that runs now: its inverse is the inverse of this command.</summary>
    public void RunAsThis(MissionCommand command) => command.Run(this);

    /// <summary>The place in <see cref="Inverses"/> from which <see cref="PopInverse"/> takes the inverses.</summary>
    private int popMark;

    /// <summary>Marks the start of a command of the batch.</summary>
    public void BeginCommand(int index)
    {
        Index = index;
        popMark = Inverses.Count;
    }

    /// <summary>Takes the inverses that the steps of the command recorded since the start of the command or the last
    /// take, so the command can put them into its own inverse.</summary>
    public List<MissionCommand> PopInverse()
    {
        var taken = Inverses.Skip(popMark).ToList();
        Inverses.RemoveRange(popMark, Inverses.Count - popMark);
        return taken;
    }

    /// <summary>The key with the letter case of the schema, and its specs, for a section type.</summary>
    public (string Key, IReadOnlyList<MissionValueSpec> Specs) KeyOf(string sectionType, string key)
    {
        try
        {
            return MissionValues.Key(Schema, sectionType, LinePlace.Section, key);
        }
        catch (EditException e)
        {
            throw Refuse(e.Error);
        }
    }

    /// <summary>
    /// The lines of a section with new lines of one key in the place of its old lines. A key without lines goes at the
    /// end. A ship key of a ship or a formation goes before the first <c>formation</c> or <c>ship</c> line (rule
    /// M09). An empty list removes the key.
    /// </summary>
    /// <param name="key">True for the key line, which only a rename and a copy change.</param>
    public List<Entry> WithKey(EditDocument document, int index, List<Entry> entries, string name,
        IReadOnlyList<LineSpec> lines, bool key = false)
    {
        var type = document.Working[index].Name;
        var built = lines.Select(l => Build(type, LinePlace.Section, l with { Key = name })).ToList();
        if (!key && built.Count > 0 && IsKeyEntry(document.Working[index], built[0].Key))
            throw Refuse(MissionErrors.KeyLineError(built[0].Key));
        var first = entries.FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var result = entries.Where(e => !e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        int at;
        if (first >= 0)
            at = entries.Take(first).Count(e => !e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        else if ((MissionIndex.Is(type, "MsnShip") || MissionIndex.Is(type, "MsnFormation")) && MissionIndex.IsShipKey(name))
        {
            at = result.FindIndex(e => MissionIndex.Is(e.Name, "formation") || MissionIndex.Is(e.Name, "ship"));
            if (at < 0)
                at = result.Count;
        }
        else
            at = result.Count;
        var made = new List<Entry>();
        foreach (var line in built)
            made.Add(NewEntry(document, index, line.Key, line.Texts, result.Take(at).Concat(made)));
        result.InsertRange(at, made);
        return result;
    }

    /// <summary>Adds a section with lines at the end of a file, and reports it.</summary>
    public void AddSectionAt(EditDocument document, string type, IReadOnlyList<LineSpec> lines)
    {
        var entries = new List<Entry>();
        foreach (var spec in lines)
        {
            var line = Build(type, LinePlace.Section, spec);
            entries.Add(NewEntry(document, null, line.Key, line.Texts,
                entries.Count(e => e.Name.Equals(line.Key, StringComparison.OrdinalIgnoreCase))));
        }
        Insert(document, document.Working.Count, NewSection(type, entries));
        Adds.Add(RefOf(document, document.Working.Count - 1));
    }

    /// <summary>The index of the first section of a type whose key is a name, or -1.</summary>
    public static int IndexOfKey(EditDocument document, string type, string name)
    {
        var working = document.Working;
        for (var i = 0; i < working.Count; i++)
        {
            if (working[i].Name.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((SectionIdentity.KeyOf(working[i]) ?? TriggerRules.Nickname(working[i]))?.Trim(), name.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>The section type of a kind word of <c>AddItem</c>, or of a type, or null.</summary>
    public static string? TypeOfKind(string kind) => kind.Trim().ToLowerInvariant().Replace("-", "_") switch
    {
        "formation" or "msnformation" => "MsnFormation",
        "solar" or "msnsolar" => "MsnSolar",
        "loot" or "msnloot" => "MsnLoot",
        "order_list" or "orders" or "objlist" => "ObjList",
        "objective" or "nnobjective" => "NNObjective",
        "conversation" or "dialog" => "Dialog",
        "npc" => "NPC",
        "random_encounter" or "msnrandenc" => "MsnRandEnc",
        "ship_build" or "npcshiparch" => "NPCShipArch",
        "meta_behavior" or "metabehavior" => "MetaBehavior",
        _ => null,
    };

    /// <summary>
    /// The ship build file of a script (the <c>[Mission] npc_ship_file</c> line), to change it, or null when the script
    /// names none. With <paramref name="create"/>, a file that does not exist becomes a new whole file of the active
    /// mod. The file then exists in W, and the inverse of the command deletes it again (see
    /// <see cref="CreatedFiles"/>).
    /// </summary>
    public EditDocument? ShipBuildFile(EditDocument script, bool create)
    {
        if (Session.ShipBuildFileOf(script.Path) is not { } path)
            return null;
        if (Session.OpenDocument(path) is { State.Exists: true })
            return File(path);
        if (!create)
            return null;
        var file = NewFile(NewFilePath(path, allowExisting: true));
        if (!file.State.Exists)
        {
            SetFile(file, [], exists: true);
            CreatedFiles.Add(file.Path);
        }
        return file;
    }

    /// <summary>The ship build files that <see cref="ShipBuildFile"/> made in this batch and that no inverse deletes
    /// yet.</summary>
    private HashSet<string> CreatedFiles { get; } = new(VfsPath.Comparer);

    /// <summary>The inverse command that deletes a ship build file that <see cref="ShipBuildFile"/> made for the command
    /// that runs now, or none. The inverse runs it after it removes the sections of the file.</summary>
    public IEnumerable<MissionCommand> DeleteIfCreated(EditDocument file) =>
        CreatedFiles.Remove(file.Path) ? [new DeleteScript(new ScriptRef(file.Path))] : [];

    /// <summary>The file and the index of a ship build: in the ship build file of the script first, then in the shared
    /// ship build files. True for a build of a shared file. The command only reads the file: a change of a build of a
    /// shared file goes through <see cref="Item"/>, with its confirmation.</summary>
    public (EditDocument File, int Index, bool Shared) FindBuild(EditDocument script, string name)
    {
        if (ShipBuildFile(script, create: false) is { } own && IndexOfKey(own, "NPCShipArch", name) is >= 0 and var at)
            return (own, at, false);
        foreach (var file in SharedBuildFiles())
        {
            if (IndexOfKey(file, "NPCShipArch", name) is >= 0 and var index)
                return (file, index, true);
        }
        throw Refuse(MissionErrors.ItemNotFoundError(script.Path, "ship build", name));
    }

    /// <summary>The number of <c>[NPC]</c> sections of the mission scripts that name a ship build.</summary>
    public int NpcsNaming(string build) =>
        MissionScripts().Sum(d => d.Working.Count(s => MissionIndex.Is(s.Name, "NPC") &&
                                                        MissionIndex.Is(ItemIndex.First(s, "npc_ship_arch"), build)));

    /// <summary>The mission scripts whose <c>[NPC]</c> sections name a ship build, to change them.</summary>
    public List<EditDocument> ScriptsNamingBuild(string build) =>
        MissionScripts().Where(d => d.Working.Any(s => MissionIndex.Is(s.Name, "NPC") &&
                                                        MissionIndex.Is(ItemIndex.First(s, "npc_ship_arch"), build)))
            .Select(d => Script(d.Path)).ToList();

    /// <summary>The documents of the mission scripts of the session: the scripts of the last run of the checks, and each
    /// open script.</summary>
    public IEnumerable<EditDocument> MissionScripts() =>
        Editor.ScriptPaths().Select(Session.OpenDocument).OfType<EditDocument>();

    /// <summary>True when a name of the label rule in a line names an item of a type: a ship before a formation, a
    /// solar and a loot, as the game finds it.</summary>
    public static bool ObjectNamesItem(EditDocument document, (int Section, int Row, int Value) line, string type)
    {
        var name = MissionIndex.Text(document.Working[line.Section][line.Row][line.Value]);
        string[] order = ["MsnShip", "MsnFormation", "MsnSolar", "MsnLoot"];
        foreach (var other in order)
        {
            if (MissionIndex.Is(other, type))
                return true;
            if (IndexOfKey(document, other, name) >= 0)
                return false;
        }
        return true;
    }

    // ---- Files ----

    /// <summary>The normal form of the path of a new mission file, after the checks of a new whole file.</summary>
    /// <param name="allowExisting">True for a path that can name a file that exists, for example a ship build
    /// file.</param>
    /// <param name="field">The field of the command that gives the path, for an error, or null.</param>
    /// <param name="shipFile">True for the path of a ship build file, for the text of an error.</param>
    public string NewFilePath(string path, bool allowExisting = false, string? field = null, bool shipFile = false)
    {
        string normal;
        try
        {
            normal = InstallPaths.Parse(path);
        }
        catch (EditException e)
        {
            throw Refuse(e.Error with { Field = field });
        }
        if (Session.ActiveMod == null)
            throw Refuse(MissionErrors.NotEditableError("There is no active mod. Select a mod to edit."));
        if (!SectionKeys.IsMissionFile(normal) || !normal.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
            Session.DataPath("MISSIONS") is { } missions && !MissionFiles.IsScanned(normal, missions))
            throw Refuse((shipFile ? MissionErrors.BadShipFilePathError(path) : MissionErrors.BadPathError(normal)) with
            {
                Field = field,
            });
        if (!allowExisting && Session.OpenDocument(normal) is { State.Exists: true })
            throw Refuse(MissionErrors.PathInUseError(normal) with { Field = field });
        return normal;
    }

    /// <summary>The document of a new whole file of the active mod, to change it. The session makes the document when it
    /// has none for the path.</summary>
    public EditDocument NewFile(string path)
    {
        Session.NewWholeDocument(path);
        return Edit(path);
    }

    /// <summary>True when a script other than <paramref name="script"/> names the ship build file.</summary>
    public bool OtherScriptNames(string script, string shipFile) =>
        Editor.ScriptPaths().Concat(Touched.Keys)
            .Distinct(VfsPath.Comparer)
            .Where(p => !VfsPath.Comparer.Equals(p, script))
            .Any(p => Session.OpenDocument(p) is { State.Exists: true } && VfsPath.Comparer.Equals(Session.ShipBuildFileOf(p), shipFile));

    // ---- Text ----

    /// <summary>True when another line of the file holds a text value.</summary>
    public static bool OtherLineUses(EditDocument document, int index, int row, string text)
    {
        for (var s = 0; s < document.Working.Count; s++)
        {
            var section = document.Working[s];
            for (var r = 0; r < section.Count; r++)
            {
                if ((s != index || r != row) && section[r].Any(v => v.ToString() == text))
                    return true;
            }
        }
        return false;
    }

    /// <summary>A new key of a text of a line: the script, the item and the key of the line, then a number when the key
    /// is in use (MOD_FORMAT "New items, renames and text").</summary>
    public string NewTextKey(EditDocument document, int index, string lineKey)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(document.Path);
        var section = document.Working[index];
        var owner = SectionIdentity.KeyOf(section) ?? TriggerRules.Nickname(section) ?? section.Name;
        var key = Clean($"{stem}.{owner}.{lineKey}");
        var candidate = key;
        for (var n = 2; Text.Get(StringKind.String, candidate) != null; n++)
            candidate = $"{key}_{n.ToString(CultureInfo.InvariantCulture)}";
        return candidate;
    }

    private static string Clean(string key)
    {
        var builder = new StringBuilder();
        foreach (var c in key.ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return builder.ToString();
    }
}
