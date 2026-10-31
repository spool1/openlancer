using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The draft of a batch of mission commands (section 4.5 of the M6b design): W now, with the commands before the one
/// that runs. It resolves the identities, checks the sections, makes the edits in the scope of the session, and keeps
/// the inverse and the report of each command.
/// </summary>
/// <remarks>The draft lives for one run of a <see cref="MissionPlanCommand"/>. A refusal throws an
/// <see cref="EditException"/>, and the session puts back the states before the batch.</remarks>
internal sealed partial class MissionDraft(MissionEditor editor, EditSession.CommandScope scope)
{
    public MissionEditor Editor { get; } = editor;

    /// <summary>The scope of the session that the plan command runs in.</summary>
    internal EditSession.CommandScope Scope => scope;

    public EditSession Session => scope.Session;

    public MissionSchema Schema => MissionSchema.Retail;

    /// <summary>Who makes the batch.</summary>
    public EditOrigin Origin => scope.Origin;

    /// <summary>The index of the command that runs now.</summary>
    public int Index { get; set; }

    // The report of the batch.
    public List<string> Adds { get; } = [];
    public List<string> Changes { get; } = [];
    public List<ResultDelete> Deletes { get; } = [];
    public List<ResultRename> Renames { get; } = [];
    public List<ResultMissing> Missing { get; } = [];
    public List<string> Notes { get; } = [];
    public List<Problem> Warnings { get; } = [];

    /// <summary>The rows of the trigger lines that the commands moved, and the document of each trigger.</summary>
    private readonly RowMap rowMap = new();

    private readonly Dictionary<string, EditDocument> rowDocuments = new(VfsPath.Comparer);

    /// <summary>The net row moves of the request: each line whose row changed, once, from its row before the request
    /// to its row after it (final finding 1 of MCP1-3).</summary>
    public IReadOnlyList<RowMove> RowMoves => NetRows().Moves;
    public string? Select { get; set; }

    /// <summary>The names that a delete or a rename of the batch takes away, by the kind of their lines. A "Missing"
    /// line that names such a name does not count in strict mode (section 4.0.5 of the M6b design).</summary>
    public HashSet<string> GoneNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The errors of the confirmations that the batch needs: a shared ship build, or for an agent a delete or
    /// a rename that leaves "Missing" lines.</summary>
    public List<EditError> Confirmations { get; } = [];

    /// <summary>The inverse of each command, in the order of the commands.</summary>
    public List<MissionCommand> Inverses { get; } = [];

    /// <summary>The state of each file before the first change of the batch.</summary>
    public Dictionary<string, DocumentState> Before { get; } = new(VfsPath.Comparer);

    /// <summary>The documents that the batch touched, by install path.</summary>
    public Dictionary<string, EditDocument> Touched { get; } = new(VfsPath.Comparer);

    // ---- Resolution ----

    /// <summary>The document of a mission script, to change it.</summary>
    /// <exception cref="EditException">The edit view has no such script, or it is read-only.</exception>
    public EditDocument Script(ScriptRef script) => Script(script.Path);

    /// <inheritdoc cref="Script(ScriptRef)"/>
    /// <remarks>In prefab mode, the script <c>prefab:mod:&lt;id&gt;</c> is the template of the asset in the role form
    /// (section 5.7 of the M6b design).</remarks>
    public EditDocument Script(string path)
    {
        if (MissionRefs.IsPrefab(path))
            return Template(path);
        var document = Session.OpenDocument(path);
        if (document is not { HoldsScript: true, State.Exists: true })
            throw new EditException(MissionErrors.ScriptNotFoundError(path) with { Index = Index });
        return Edit(document.Path);
    }

    /// <summary>
    /// The document of a mission file, to change it: a script, or the ship build file of a script (section 4.1 of the
    /// M6b design). The shared files under <c>DATA\MISSIONS</c>, for example the pilot files and the encounters, are
    /// world data. The commands only read them.
    /// </summary>
    /// <exception cref="EditException">The edit view has no such file, the file is world data, or it is
    /// read-only.</exception>
    public EditDocument File(string path)
    {
        if (MissionRefs.IsPrefab(path))
            return Template(path);
        if (Session.OpenDocument(path) is not { State.Exists: true } document || !SectionKeys.IsMissionFile(document.Path))
            throw new EditException(MissionErrors.ScriptNotFoundError(path) with { Index = Index });
        if (!IsMissionFile(document.Path))
            throw new EditException(MissionErrors.WorldFileError(document.Path) with { Index = Index });
        return Edit(document.Path);
    }

    /// <summary>True for a mission file: a script or the ship build file of a script, also of a script that an earlier
    /// command of the batch changed. A file directly in <c>DATA\MISSIONS</c> is never the ship build file of a script,
    /// as for the guard of the session. So a command cannot point a script at a world file to change that file (final
    /// finding 5 of MCP1-3).</summary>
    private bool IsMissionFile(string path) =>
        Session.IsGuardedMissionFile(path) ||
        !MissionFiles.IsDirectlyIn(path, Session.DataPath("MISSIONS")) &&
        Touched.Values.Any(d => d.HoldsScript && VfsPath.Comparer.Equals(Session.ShipBuildFileOf(d.Path), path));

    /// <summary>True for a shared ship build file of the missions (see <see cref="SharedBuildFiles"/>).</summary>
    public bool IsSharedBuildFile(string path) =>
        Session.OpenDocument(path) is { } document && SharedBuildFiles().Any(f => ReferenceEquals(f, document));

    /// <summary>The shared ship build files of the missions: the files of the role <c>mission_ships</c> directly in
    /// <c>DATA\MISSIONS</c> that no script names. Encounters and bases use their builds too (review finding
    /// RB2).</summary>
    public IEnumerable<EditDocument> SharedBuildFiles() =>
        Session.MissionShipPaths.Where(p => IsSharedBuildPath(Session, p, IsMissionFile))
            .Select(Session.OpenDocument).OfType<EditDocument>().Where(d => d.State.Exists);

    /// <summary>True for the path of a shared ship build file: a file of the role <c>mission_ships</c> directly in
    /// <c>DATA\MISSIONS</c> that is not a mission file.</summary>
    /// <param name="path">A normal install path.</param>
    /// <param name="isMissionFile">True for a script, the ship build file of a script or an editor file.</param>
    internal static bool IsSharedBuildPath(EditSession session, string path, Func<string, bool> isMissionFile) =>
        session.MissionShipPaths.Any(p => VfsPath.Comparer.Equals(p, path)) &&
        MissionFiles.IsDirectlyIn(path, session.DataPath("MISSIONS")) && !isMissionFile(path);

    /// <summary>The document of a path through the scope, with its state before the batch.</summary>
    private EditDocument Edit(string path)
    {
        EditDocument document;
        try
        {
            document = scope.Document(path);
        }
        catch (InvalidOperationException e)
        {
            throw new EditException((Session.ReadOnlyReason is { } reason
                ? MissionErrors.NotEditableError(reason)
                : MissionErrors.ReadOnlySectionError(path, e.Message)) with { Index = Index });
        }
        catch (ArgumentException)
        {
            throw new EditException(MissionErrors.ScriptNotFoundError(path) with { Index = Index });
        }
        if (Before.TryAdd(document.Path, document.State))
            Touched[document.Path] = document;
        return document;
    }

    /// <summary>The index of the first trigger with a nickname, without letter case (TR4), or -1.</summary>
    public static int TriggerIndex(EditDocument document, string name) => TriggerIndex(document.Working, name);

    /// <inheritdoc cref="TriggerIndex(EditDocument, string)"/>
    private static int TriggerIndex(IReadOnlyList<Section> working, string name)
    {
        for (var i = 0; i < working.Count; i++)
        {
            if (working[i].Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(TriggerRules.Nickname(working[i]), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>The script and the index of a trigger.</summary>
    /// <exception cref="EditException"><c>TriggerNotFound</c>.</exception>
    public (EditDocument Document, int Index) Trigger(TriggerRef trigger)
    {
        var document = Script(trigger.Script);
        var index = TriggerIndex(document, trigger.Name);
        if (index < 0)
            throw new EditException(MissionErrors.TriggerNotFoundError(document.Path, trigger.Name,
                ItemRefs.Hint(trigger.Name, TriggerNames(document))) with { Index = Index, Target = trigger.ToString() });
        return (document, index);
    }

    /// <summary>The file and the index of an item. A trigger also resolves by its first nickname line.</summary>
    /// <param name="lines">True for a command that changes only the lines of the item. Such a command can also change
    /// a ship build of a shared ship build file, after a confirmation (<c>SharedBuildChange</c>, review finding RB2). The
    /// inverse of "Shared" of <see cref="SetShipLoadout"/> needs it.</param>
    /// <exception cref="EditException"><c>ItemNotFound</c> or <c>TriggerNotFound</c>.</exception>
    public (EditDocument Document, int Index) Item(ItemRef item, bool lines = false)
    {
        var document = lines && SharedBuild(item) is { } shared ? shared : File(item.Script);
        var index = document.IndexOf(item.Section);
        if (index < 0 && MissionIndex.Is(item.Section.Type, "Trigger") && item.Section is { Key: { } name, Ordinal: 0 })
            index = TriggerIndex(document, name);
        if (index >= 0)
            return (document, index);
        var kind = MissionNames.KindWord(item.Section.Type);
        var key = item.Section.Key ?? item.Section.ToString();
        var names = document.Working.Where(s => s.Name.Equals(item.Section.Type, StringComparison.OrdinalIgnoreCase))
            .Select(SectionIdentity.KeyOf).OfType<string>();
        throw new EditException((MissionIndex.Is(item.Section.Type, "Trigger")
            ? MissionErrors.TriggerNotFoundError(document.Path, key, ItemRefs.Hint(key, names))
            : MissionErrors.ItemNotFoundError(document.Path, kind, key, ItemRefs.Hint(key, names))) with
        {
            Index = Index, Target = item.ToString(),
        });
    }

    /// <summary>The shared ship build file of a ship build, to change it, with the confirmation of the change. Null
    /// when the item is not a ship build of a shared ship build file.</summary>
    private EditDocument? SharedBuild(ItemRef item)
    {
        if (!MissionIndex.Is(item.Section.Type, "NPCShipArch") ||
            Session.OpenDocument(item.Script) is not { State.Exists: true } document ||
            !SharedBuildFiles().Any(f => ReferenceEquals(f, document)))
            return null;
        Confirmations.Add(MissionErrors.SharedBuildChangeError(item.Section.Key ?? item.Section.ToString()));
        return Edit(document.Path);
    }

    /// <summary>The file, the section and the line of a row, after its guards. A line of a ship build of a shared
    /// ship build file needs a confirmation (see <see cref="Item"/>).</summary>
    /// <exception cref="EditException"><c>RowNotFound</c> or <c>RowChanged</c>.</exception>
    public (EditDocument Document, int Index, int Row) Row(RowRef row)
    {
        var (document, index) = Item(row.Item, lines: true);
        var section = document.Working[index];
        var owner = Owner(document, index);
        if (row.Row < 0 || row.Row >= section.Count)
            throw new EditException(MissionErrors.RowNotFoundError(owner, row.Row) with
            {
                Index = Index, Target = row.Reference,
            });
        var entry = section[row.Row];
        var keyChanged = row.ExpectKey != null && !entry.Name.Equals(row.ExpectKey.Trim(), StringComparison.OrdinalIgnoreCase);
        var valuesChanged = row.ExpectValues != null && !MissionValues.TextsOf(entry).Select(t => t.Trim())
            .SequenceEqual(row.ExpectValues.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        if (keyChanged || valuesChanged)
        {
            var expected = row.ExpectKey ?? entry.Name;
            if (row.ExpectValues != null)
                expected += " = " + string.Join(", ", row.ExpectValues);
            throw new EditException(MissionErrors.RowChangedError(owner, row.Row, expected) with
            {
                Index = Index, Target = row.Reference,
            });
        }
        return (document, index, row.Row);
    }

    /// <summary>The row of a line that must be in a given section, or null for the end of the section.</summary>
    /// <exception cref="EditException">The row is in another section, or its guards do not match.</exception>
    public int? RowIn(RowRef? row, EditDocument document, int index)
    {
        if (row == null)
            return null;
        var (d, i, r) = Row(row);
        if (!ReferenceEquals(d, document) || i != index)
            throw new EditException(MissionErrors.NotAnItemError(row.Reference, $"a line of {Owner(document, index)}") with
            {
                Index = Index,
            });
        return r;
    }

    /// <summary>Refuses a section that a patch cannot change (section 4.6 of the M6b design), and a member of a linked
    /// prefab instance (decision D676).</summary>
    /// <param name="members">True for a delete: a delete of a member is a removed-member override (decision D676 c), so
    /// it passes.</param>
    /// <exception cref="EditException"><c>RepeatedNickname</c>, <c>ReadOnlySection</c> or <c>PrefabMember</c>.</exception>
    public void Editable(EditDocument document, int index, bool members = false)
    {
        if (!members)
            RefuseMember(document, index);
        if (document.ReadOnlyReasonAt(index) is not { } reason)
            return;
        var id = document.IdentityAt(index);
        var section = document.Working[index];
        var key = SectionIdentity.KeyOf(section) ?? TriggerRules.Nickname(section);
        if (key != null && document.Base.Count(s => s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase) &&
                                                    string.Equals(SectionIdentity.KeyOf(s), key, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new EditException(MissionErrors.RepeatedNicknameError(section.Name, key) with
            {
                Index = Index, Target = RefOf(document, index),
            });
        throw new EditException(MissionErrors.ReadOnlySectionError(id.ToString(), Capital(reason)) with
        {
            Index = Index, Target = RefOf(document, index),
        });
    }

    // ---- Edits ----

    /// <summary>Puts a new section in place of the section at an index. A new identity of a section is a rename.</summary>
    /// <param name="names">The keys of the lines that the edit sets or removes.</param>
    public void Replace(EditDocument document, int index, Section section, IEnumerable<string> names)
    {
        var before = new SectionRef(document.Path, document.IdentityAt(index));
        document.Replace(index, section, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var after = new SectionRef(document.Path, document.IdentityAt(index));
        // The role form of a template is not a file of the session. The write of the asset records the change.
        if (IsTemplate(document))
            return;
        if (before.Equals(after) && string.Equals(before.Id.Key, after.Id.Key, StringComparison.Ordinal))
        {
            scope.Changed(before, structural: false);
            return;
        }
        scope.Changed(before, structural: true);
        scope.Changed(after, structural: true);
        if (!before.Equals(after))
            scope.Renamed(before, after);
    }

    /// <summary>Puts a new body into the section at an index, with the lines of the body.</summary>
    public void SetBody(EditDocument document, int index, IReadOnlyList<Entry> entries)
    {
        var old = document.Working[index];
        var section = WithEntries(old, entries);
        Replace(document, index, section, old.Select(e => e.Name).Concat(entries.Select(e => e.Name)));
    }

    /// <summary>Puts a new section into a file at a place, or at the end of the file by the rule of
    /// <see cref="MovesBaseSections"/>.</summary>
    /// <returns>The index of the section in the file.</returns>
    public int Insert(EditDocument document, int at, Section section)
    {
        at = PlaceOfNew(document, at, section);
        document.Insert(at, section);
        if (!IsTemplate(document))
            scope.Changed(new SectionRef(document.Path, document.IdentityAt(at)), structural: true);
        return at;
    }

    /// <summary>The place of a new section: the place, or the end of the file with a note when the section there would
    /// make the patch move the sections of B after it.</summary>
    private int PlaceOfNew(EditDocument document, int at, Section section)
    {
        var after = Enumerable.Range(at, Math.Max(0, document.Working.Count - at)).Select(document.IdentityAt);
        if (!MovesBaseSections(document, section, after))
            return at;
        if (NewPlaceNote(section.Name) is var note && !Notes.Contains(note))
            Notes.Add(note);
        return document.Working.Count;
    }

    /// <summary>
    /// True when a new section at a place of a patched mission file makes the patch move the sections of B after it
    /// (section 6.6 of the M6b design). The merge adds a new section at the end of the file, and the game finds a
    /// trigger and the other items by their name, not by their place. So such a section goes to the end of the file. A
    /// ship or a formation section keeps its place, because the writer gives it <c>ol.before</c> (rule M08). A section
    /// that B has, for example a deleted section that comes back, keeps its place too. A whole file keeps each place.
    /// </summary>
    /// <param name="after">The sections of the file after the place, by their identity.</param>
    internal static bool MovesBaseSections(EditDocument document, Section section, IEnumerable<SectionIdentity> after)
    {
        if (document.IsWholeFile || MissionIndex.Is(section.Name, "MsnShip") || MissionIndex.Is(section.Name, "MsnFormation") ||
            SectionIdentity.KeyOf(section) is not { } key ||
            document.Base.Any(s => s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(SectionIdentity.KeyOf(s), key, StringComparison.OrdinalIgnoreCase)))
            return false;
        return after.Any(id => document.BaseIdOf(id) != null);
    }

    /// <summary>The note of a new section that goes to the end of its file.</summary>
    public static string NewPlaceNote(string type) =>
        $"The place of a [{type}] section in the file has no effect in the game. A patch adds a new section at the end " +
        "of the file, so the editor put it there.";

    /// <summary>Removes the section at an index, and gives it. The delete of a member of a linked prefab instance
    /// becomes a removed-member override of its record (decision D676 c). The caller handles the lines that name the
    /// section with <see cref="OutsideLines"/> first.</summary>
    public Section Remove(EditDocument document, int index)
    {
        var member = MemberDeleteAt(document, index);
        var old = document.Working[index];
        var reference = new SectionRef(document.Path, document.IdentityAt(index));
        document.Remove(index, 1);
        if (!IsTemplate(document))
            scope.Changed(reference, structural: true);
        if (member != null)
            MemberRemoved(document, member);
        return old;
    }

    /// <summary>Gives the patch of a section another <c>ol.base</c> hash (see
    /// <see cref="EditDocument.SetBaseHash"/>).</summary>
    public void SetBaseHash(EditDocument document, int index, string hash)
    {
        document.SetBaseHash(document.IdentityAt(index), hash);
        scope.Changed(new SectionRef(document.Path, document.IdentityAt(index)), structural: false);
    }

    /// <summary>Sets the state of a whole file: its sections, and whether it exists (section 6.7 of the M6b
    /// design).</summary>
    public void SetFile(EditDocument document, IReadOnlyList<Section> sections, bool exists)
    {
        document.Restore(document.State with
        {
            Working = System.Collections.Immutable.ImmutableList.CreateRange(sections),
            Exists = exists,
        });
        scope.NoteStructural();
    }

    /// <summary>The text of the active mod, to read or to change.</summary>
    public ModText Text
    {
        get => scope.Text;
        set => scope.Text = value;
    }

    /// <summary>A copy of a section with other entries.</summary>
    public static Section WithEntries(Section old, IEnumerable<Entry> entries)
    {
        var result = new Section(old.Name) { File = old.File, Line = old.Line };
        foreach (var entry in entries)
            result.Add(Copy(result, entry));
        return result;
    }

    /// <summary>A new section of a type with entries.</summary>
    public static Section NewSection(string type, IEnumerable<Entry> entries)
    {
        var result = new Section(type);
        foreach (var entry in entries)
            result.Add(Copy(result, entry));
        return result;
    }

    /// <summary>A copy of an entry for another section. It shares the values, because a value cannot change.</summary>
    public static Entry Copy(Section section, Entry entry)
    {
        var copy = new Entry(section, entry.Name) { Line = entry.Line };
        foreach (var value in entry)
            copy.Add(value);
        return copy;
    }

    /// <summary>
    /// A new line of a section from its key and value texts. In a binary patched file, each value takes the type of
    /// the line of B with the same key and the same occurrence, as the merge does (section 4.3 of the M6b design).
    /// </summary>
    /// <param name="entries">The lines of the section before the place of the new line, in the new order. The
    /// occurrence of the key counts in them.</param>
    public Entry NewEntry(EditDocument document, int index, string key, IReadOnlyList<string> texts,
        IEnumerable<Entry> entries)
    {
        var occurrence = entries.Count(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        return NewEntry(document, index < 0 ? null : document.BaseSection(document.IdentityAt(index)), key, texts, occurrence);
    }

    /// <summary>A new line with the type guide of a section of B.</summary>
    public Entry NewEntry(EditDocument document, Section? baseSection, string key, IReadOnlyList<string> texts, int occurrence)
    {
        var replaced = baseSection?.Where(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Skip(occurrence)
            .FirstOrDefault();
        try
        {
            return MissionValues.Entry(new Section("Line"), key, texts, replaced, document.IsBinary && !document.IsWholeFile);
        }
        catch (EditException e)
        {
            throw new EditException(e.Error with { Index = Index });
        }
    }

    /// <summary>
    /// The lines of a section from raw entries (key and value texts), with the type guide of B by occurrence. The
    /// inverses and the raw INI editor give such entries.
    /// </summary>
    public List<Entry> Entries(EditDocument document, Section? baseSection, IReadOnlyList<RawEntry> raw)
    {
        var result = new List<Entry>();
        foreach (var entry in raw)
        {
            if (PatchFormatter.NameError(entry.Key) is { } error)
                throw new EditException(MissionErrors.UnknownKeyError(entry.Key) with { Message = error, Index = Index });
            foreach (var text in entry.Values)
            {
                try
                {
                    MissionValues.CheckText(text);
                }
                catch (EditException e)
                {
                    throw new EditException(e.Error with { Index = Index });
                }
            }
            var occurrence = result.Count(e => e.Name.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));
            result.Add(NewEntry(document, baseSection, entry.Key, entry.Values, occurrence));
        }
        return result;
    }

    // ---- Report ----

    /// <summary>The reference of a section in the form of a result.</summary>
    public string RefOf(EditDocument document, int index) =>
        ItemRefs.Canonical(Session, new SectionRef(document.Path, document.IdentityAt(index)));

    /// <summary>The item of a section, for a command.</summary>
    public static ItemRef ItemOf(EditDocument document, int index) => new(document.Path, document.IdentityAt(index));

    /// <summary>The row of a line, with the key of the line as its guard.</summary>
    public static RowRef RowOf(EditDocument document, int index, int row) =>
        new(ItemOf(document, index), row) { ExpectKey = document.Working[index][row].Name };

    /// <summary>The raw entries of a section: each line with its value texts.</summary>
    public static IReadOnlyList<RawEntry> RawOf(Section section) =>
        section.Select(e => new RawEntry(e.Name, MissionValues.TextsOf(e))).ToList();

    /// <summary>A line as a spec with its value texts by position, so that it comes back exactly.</summary>
    public static LineSpec RawLine(Entry entry) => new(entry.Name) { Texts = MissionValues.TextsOf(entry) };

    /// <summary>The name of the owner of a line in a message: "trigger 'x'" or the section.</summary>
    public static string Owner(EditDocument document, int index)
    {
        var section = document.Working[index];
        return MissionIndex.Is(section.Name, "Trigger") && TriggerRules.Nickname(section) is { } name
            ? $"Trigger '{name}'"
            : document.IdentityAt(index).ToString();
    }

    /// <summary>The nicknames of the triggers of a file.</summary>
    public static IEnumerable<string> TriggerNames(EditDocument document) =>
        document.Working.Where(s => MissionIndex.Is(s.Name, "Trigger")).Select(TriggerRules.Nickname).OfType<string>();

    /// <summary>Adds the notes of a line.</summary>
    public void Note(BuiltLine line) => Notes.AddRange(line.Notes);

    /// <summary>Records the inverse of the command that runs now.</summary>
    public void Inverse(MissionCommand inverse) => Inverses.Add(inverse);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>A line with its value texts by position, as a file holds it. The inverses use it to write a line
/// exactly.</summary>
public sealed record RawEntry(string Key, IReadOnlyList<string> Values)
{
    public bool Equals(RawEntry? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) &&
        Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Key.ToLowerInvariant(), Values.Count);

    public override string ToString() => Values.Count == 0 ? $"{Key} =" : $"{Key} = {string.Join(", ", Values)}";
}
