using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

/// <summary>An immutable state of an <see cref="IEditFile"/>. The edit history keeps old states to undo edits.</summary>
internal interface IFileState;

/// <summary>
/// A file of a session whose state the edit history keeps: an INI file (see <see cref="EditDocument"/>), a cutscene
/// script (see <see cref="ThnDocument"/>) or an editor file of the mod (see <see cref="ModFileDocument"/>). So one
/// entry of the history can change each kind.
/// </summary>
internal interface IEditFile
{
    /// <summary>The key of the file in the session: the install path in <see cref="VfsPath.Normalize"/> form, or the
    /// key of an editor file of the mod (see <see cref="ModFileDocument"/>).</summary>
    string Path { get; }

    /// <summary>The state now. <see cref="Restore"/> puts back a state that this property gave.</summary>
    IFileState State { get; }

    /// <summary>Puts back a state that <see cref="State"/> gave, to undo or redo an edit.</summary>
    /// <exception cref="InvalidCastException">The state is a state of another kind of file.</exception>
    void Restore(IFileState state);
}

/// <summary>
/// The states before and after one entry of the edit history: of each file that the entry changed, and of the text
/// of the mod.
/// </summary>
/// <remarks>
/// The change set records the state of a file before the first change to it, and the state of each file at the end.
/// To undo the entry, the session puts back the states before. To redo it, the session puts back the states after.
/// A state holds W and the pins, so an undo also gives back the pins that the edit removed.
/// </remarks>
internal sealed class ChangeSet(string label, EditOrigin origin = EditOrigin.User)
{
    private readonly Dictionary<string, (IFileState Before, IFileState After)> files = new(VfsPath.Comparer);
    private readonly List<SectionRef> sections = [];
    private readonly List<string> cutscenes = [];
    private readonly List<(SectionRef From, SectionRef To)> renames = [];
    private (ModText Before, ModText After)? text;

    /// <summary>The label of the entry. A batch sets it at its commit, because its label can change until
    /// then.</summary>
    public string Label { get; set; } = label;

    /// <summary>Who made the entry.</summary>
    public EditOrigin Origin { get; } = origin;

    /// <summary>The keys of the files that the entry changed: install paths, and the keys of the editor files of the
    /// mod.</summary>
    public IReadOnlyCollection<string> Paths => files.Keys;

    /// <summary>True when a command of the entry set the text of the mod.</summary>
    public bool ChangesText => text != null;

    /// <summary>True when the change set recorded no file and no text.</summary>
    public bool IsEmpty => files.Count == 0 && text == null;

    /// <summary>The number of different sections that the commands of the change set changed, added or
    /// removed.</summary>
    public int SectionCount => sections.Distinct().Count();

    /// <summary>Each section that got a new key, with its old and its new reference, in order.</summary>
    public IReadOnlyList<(SectionRef From, SectionRef To)> Renames => renames;

    /// <summary>
    /// True when a state after the change differs from its state before. Before <see cref="Finish"/>, it is false.
    /// </summary>
    public bool ChangesState =>
        files.Values.Any(s => !s.Before.Equals(s.After)) || text is { } t && !t.Before.SameAs(t.After);

    /// <summary>What the entry changes, for the change and its redo. Call <see cref="Finish"/> first, so the change
    /// knows the states after.</summary>
    public EditChange Change =>
        new(sections.Distinct().ToList(), Structural, ChangesText, cutscenes.Distinct(VfsPath.Comparer).ToList())
        {
            Renames = renames.ToList(),
            ModFiles = files.Keys.Where(ModFileDocument.IsKey).Select(k => k[ModFileDocument.KeyPrefix.Length..])
                .Order(StringComparer.OrdinalIgnoreCase).ToList(),
            // A file that a command only read through the scope has the same state before and after.
            Files = files.Where(f => !ModFileDocument.IsKey(f.Key) && !cutscenes.Contains(f.Key, VfsPath.Comparer) &&
                                     f.Key.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                                     !f.Value.Before.Equals(f.Value.After))
                .Select(f => f.Key).Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };

    /// <summary>What an undo of the entry changes: the same sections, and each rename in the other direction, in
    /// reverse order.</summary>
    public EditChange UndoChange => Change with
    {
        Renames = renames.AsEnumerable().Reverse().Select(r => (r.To, r.From)).ToList(),
    };

    private bool Structural { get; set; }

    /// <summary>Records the state of a file before the first change to it.</summary>
    public void Capture(IEditFile file) => files.TryAdd(file.Path, (file.State, file.State));

    /// <summary>Records the text before the first change to it.</summary>
    public void CaptureText(ModText current) => text ??= (current, current);

    public void Note(SectionRef section, bool structural)
    {
        sections.Add(section);
        Structural |= structural;
    }

    /// <summary>Records that a section got a new key.</summary>
    public void NoteRename(SectionRef from, SectionRef to) => renames.Add((from, to));

    /// <summary>Records a change of a cutscene script.</summary>
    /// <param name="path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
    public void NoteCutscene(string path) => cutscenes.Add(path);

    /// <summary>Records a change of sections that the change set does not list one by one.</summary>
    public void NoteStructural() => Structural = true;

    /// <summary>
    /// Records the states after the change. A file whose state after has the content of its state before gets its state
    /// before again. So commands that cancel each other change no state (section 4.5 of the M6b design, item 6).
    /// </summary>
    /// <param name="file">Gives the file of an install path.</param>
    public void Finish(Func<string, IEditFile> file, ModText currentText)
    {
        foreach (var path in files.Keys.ToList())
        {
            var before = files[path].Before;
            var edited = file(path);
            var after = edited.State;
            if (!ReferenceEquals(before, after) && before is DocumentState old && after is DocumentState now &&
                old.SameAs(now))
            {
                edited.Restore(before);
                after = before;
            }
            files[path] = (before, after);
        }
        if (text is { } t)
            text = (t.Before, currentText);
    }

    /// <summary>Puts back the states before the change.</summary>
    /// <returns>The text before the change, or null when the change did not change the text.</returns>
    public ModText? Undo(Func<string, IEditFile> file)
    {
        foreach (var (path, states) in files)
            file(path).Restore(states.Before);
        return text?.Before;
    }

    /// <summary>Puts back the states after the change.</summary>
    /// <returns>The text after the change, or null when the change did not change the text.</returns>
    public ModText? Redo(Func<string, IEditFile> file)
    {
        foreach (var (path, states) in files)
            file(path).Restore(states.After);
        return text?.After;
    }
}

/// <summary>
/// The undo and redo stacks of a session. Each entry has an id. The id of the top entry at the last save is the
/// saved id, so the session knows when it has unsaved edits.
/// </summary>
/// <remarks>
/// The ids never repeat. So when an undo goes below the saved entry and a new edit clears the redo stack, the saved
/// id is gone, and the session stays dirty until the next save.
/// </remarks>
internal sealed class EditHistory
{
    /// <summary>The id of the state that the session opened with.</summary>
    private const long OpenId = 0;

    private readonly List<Entry> undo = [];
    private readonly List<Entry> redo = [];
    private long nextId = OpenId + 1;
    private long savedId = OpenId;

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public string? UndoLabel => CanUndo ? undo[^1].Change.Label : null;

    public string? RedoLabel => CanRedo ? redo[^1].Change.Label : null;

    /// <summary>Who made the top entry, or null when there is nothing to undo.</summary>
    public EditOrigin? UndoOrigin => CanUndo ? undo[^1].Change.Origin : null;

    /// <summary>Who made the top entry of the redo stack, or null when there is nothing to redo.</summary>
    public EditOrigin? RedoOrigin => CanRedo ? redo[^1].Change.Origin : null;

    /// <summary>Who undid the top entry of the redo stack the last time, or null when there is nothing to redo. The
    /// agent redoes only an entry that it undid itself (decision D709 of the MCP1 design).</summary>
    public EditOrigin? RedoUndoneBy => CanRedo ? redo[^1].UndoneBy : null;

    /// <summary>True when the top entry is not the entry of the last save.</summary>
    public bool IsDirty => TopId != savedId;

    private long TopId => CanUndo ? undo[^1].Id : OpenId;

    /// <summary>Adds an entry on top. The redo stack goes.</summary>
    public void Push(ChangeSet change)
    {
        undo.Add(new Entry(nextId++, change, null));
        redo.Clear();
    }

    /// <summary>Moves the top entry to the redo stack and returns it.</summary>
    /// <param name="by">Who undoes the entry.</param>
    public ChangeSet Undo(EditOrigin by)
    {
        var entry = Pop(undo);
        redo.Add(entry with { UndoneBy = by });
        return entry.Change;
    }

    /// <summary>Moves the top entry of the redo stack back on top and returns it.</summary>
    public ChangeSet Redo()
    {
        var entry = Pop(redo);
        undo.Add(entry);
        return entry.Change;
    }

    /// <summary>Makes the top entry the entry of the last save.</summary>
    public void MarkSaved() => savedId = TopId;

    private static Entry Pop(List<Entry> from)
    {
        if (from.Count == 0)
            throw new InvalidOperationException("There is no entry to move.");
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);
        return entry;
    }

    /// <param name="UndoneBy">Who undid the entry the last time, or null when no undo moved it.</param>
    private sealed record Entry(long Id, ChangeSet Change, EditOrigin? UndoneBy);
}
