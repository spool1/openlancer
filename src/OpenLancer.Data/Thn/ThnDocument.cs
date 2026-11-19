using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Thn;

/// <summary>Where the working copy W of a cutscene comes from, and what a save writes for it.</summary>
public enum ThnFileKind
{
    /// <summary>The active mod has no file for the cutscene. W starts as the cutscene of the lower layer, and a save
    /// writes a THN patch.</summary>
    Lower,

    /// <summary>The active mod has a THN patch for the cutscene. A save writes the patch again.</summary>
    PatchedHere,

    /// <summary>The active mod replaces the cutscene of the lower layer with a whole file. A save writes the whole
    /// file.</summary>
    WholeFileHere,

    /// <summary>The active mod has a whole file at a path that the lower layer does not have. A save writes the whole
    /// file.</summary>
    NewHere,
}

/// <summary>Where the edit view gets a cutscene from (see <see cref="EditSession.CutsceneSource"/>).</summary>
/// <param name="Kind">The kind of the file of the active mod for the cutscene.</param>
/// <param name="ChangedByLowerMod">True when a mod that loads before the active mod adds or changes the
/// cutscene.</param>
public readonly record struct ThnFileSource(ThnFileKind Kind, bool ChangedByLowerMod);

/// <summary>An element of a cutscene of an edit session. The id stays the same through edits, undo, redo and saves,
/// so a selection survives them.</summary>
/// <param name="Path">The install path of the cutscene.</param>
public sealed record ThnRef(string Path, ThnElementKind Kind, ThnElementId Id);

/// <summary>
/// A value that the loaded patch sets or removes, and that no edit touched since. A save writes it again, also when its
/// value equals the lower value, so the mod keeps its value when a lower mod changes it (see
/// <see cref="ThnPatch.Make"/>).
/// </summary>
/// <remarks>An edit of a path removes the pins at the path and in its value. A pin of a record that holds the path
/// stays, so the patch keeps the whole record with the edited value in it. A revert of the path splits such a pin into
/// the pins of the other values of the record (see <see cref="ThnElementPaths.Beside"/>). So the path follows the lower
/// value, and the other values stay in the patch.</remarks>
/// <param name="Element">The element, or null for the global <see cref="Path"/>. The only such global is
/// <c>duration</c>.</param>
internal readonly record struct ThnPin(ThnElementId? Element, ThnPath Path)
{
    /// <summary>The pin of the <c>duration</c> global.</summary>
    public static readonly ThnPin Duration = new(null, new ThnPath(ThnScriptModel.DurationGlobal));
}

/// <summary>The state of the working copy of a cutscene: W and the pins. The state cannot change, so the edit history
/// keeps old states to undo edits.</summary>
internal sealed record ThnDocumentState(ThnScriptModel Working, ImmutableHashSet<ThnPin> Pins) : IFileState
{
    /// <summary>The state of a new cutscene before New cutscene, and after an undo of it: no script, and an empty
    /// W (decision D134 of the M8b design).</summary>
    public static readonly ThnDocumentState Absent =
        new(new ThnScriptModel(ThnTable.Empty, 0, [], []), []) { Exists = false };

    /// <summary>False for the state of a new cutscene that the user did not make yet, or whose New cutscene an undo
    /// took back. The session then gives no W for the cutscene, and a save deletes its file.</summary>
    public bool Exists { get; init; } = true;
}

/// <summary>The text that a save writes for a cutscene, or why the save cannot write it.</summary>
/// <param name="Text">The ASCII text of the patch or of the whole file, or null to delete the patch file.</param>
/// <param name="Problem">Why the editor cannot write the file, or null.</param>
internal sealed record ThnFileText(string? Text, string? Problem)
{
    public byte[]? ToBytes() => Text == null ? null : Encoding.ASCII.GetBytes(Text);
}

/// <summary>
/// One cutscene script in the editor: B, the script as the lower mods leave it, and W, the working copy that the edits
/// change.
/// </summary>
/// <remarks>
/// <para>W starts as B with the THN patch of the active mod applied, or as the whole file of the active mod. An
/// element of W finds its element of B by its <see cref="ThnElement.Origin"/>, so a save writes the patch that changes
/// B into W. The elements of a whole file have no origin, and a save writes the whole file.</para>
/// <para>The paths that the loaded patch sets or removes are pins (see <see cref="ThnPin"/>). An edit of a path
/// removes its pin. The patch always deletes and adds the elements that W deletes and adds, so they need no pin.</para>
/// <para>The document is read-only when the patch of the active mod has an error, because a save would lose the
/// entries that do not apply, and when the active mod has a whole file and a patch for the cutscene.</para>
/// <para>An entity whose name a later entity repeats is read-only too. The engine uses the last entity with a name
/// (ThnScript.cs:132-136), so it does not use the earlier one. Delete is the only edit of it (decision D64).</para>
/// </remarks>
internal sealed class ThnDocument : IEditFile
{
    public const string HiddenEntityMessage = "The engine uses the last entity with this name, so it does not use this " +
                                          "entity (ThnScript.cs:132-136). Delete is the only edit of it.";

    private readonly IThnEvaluator thn;

    /// <summary>Gives the ids of B, of the loaded elements and of new elements.</summary>
    private readonly Func<ThnElementId> ids;

    /// <summary>The comment block of the patch of the active mod (see <see cref="ThnPatch.Header"/>).</summary>
    private readonly string header;

    private ThnDocumentState state;
    private ThnFileText? initialText;

    /// <summary>The entities that a later entity with the same name hides, for the W in which they were found.</summary>
    private (ThnScriptModel Working, IReadOnlySet<ThnElementId> Ids)? hidden;

    private ThnDocument(string path, ThnScriptModel? b, ThnFileKind kind, ThnDocumentState state, string header,
        bool dropsComments, string? readOnlyReason, string? sourceHash, IThnEvaluator thn, Func<ThnElementId> ids)
    {
        Path = path;
        Base = b;
        Kind = kind;
        this.state = state;
        Initial = state;
        this.header = header;
        DropsComments = dropsComments;
        ReadOnlyReason = readOnlyReason;
        SourceHash = sourceHash;
        this.thn = thn;
        this.ids = ids;
    }

    /// <summary>The install path, in <see cref="VfsPath.Normalize"/> form.</summary>
    public string Path { get; }

    /// <summary>B: the cutscene as the lower layer gives it, or null when the lower layer does not have it.</summary>
    public ThnScriptModel? Base { get; }

    public ThnFileKind Kind { get; }

    /// <summary>True when a save writes a whole file, not a patch.</summary>
    public bool IsWholeFile => Kind is ThnFileKind.WholeFileHere or ThnFileKind.NewHere;

    /// <summary>The state now. <see cref="Restore"/> puts back a state that this property gave.</summary>
    public ThnDocumentState State => state;

    IFileState IEditFile.State => state;

    /// <summary>The state when the document opened.</summary>
    public ThnDocumentState Initial { get; }

    /// <summary>W.</summary>
    public ThnScriptModel Working => state.Working;

    /// <summary>Why the document cannot be edited, or null.</summary>
    public string? ReadOnlyReason { get; }

    /// <summary>The SHA-256 of the file of the active mod that a save replaces (the patch or the whole file), in
    /// lowercase hex, or null when the active mod had no file for the cutscene.</summary>
    public string? SourceHash { get; }

    /// <summary>True when the patch of the active mod has comments after its comment block. A save writes the patch in
    /// canonical form, which keeps only the comment block (decision D63).</summary>
    public bool DropsComments { get; }

    /// <summary>The names of the THN environment.</summary>
    public IReadOnlyDictionary<string, ThnSymbolInfo> Symbols => thn.Symbols;

    /// <summary>Opens a cutscene for editing.</summary>
    /// <param name="path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
    /// <param name="lower">The bytes of the cutscene as the lower layer gives it, or null when the lower layer does
    /// not have it.</param>
    /// <param name="activeMod">The mod that the edits go to, or null.</param>
    /// <param name="problem">Why the document does not open, or null.</param>
    /// <returns>The document, or null when the edit view has no cutscene at the path, or when the editor cannot read
    /// it.</returns>
    public static ThnDocument? Open(string path, byte[]? lower, Mod? activeMod, IThnEvaluator thn, out string? problem)
    {
        var ids = ThnElementId.Counter();
        ThnScriptModel? b = null;
        if (lower != null)
        {
            try
            {
                b = ThnScriptModel.From(thn.Evaluate(lower, path), ids);
            }
            catch (Exception e) when (e is ThnEvaluationException or FormatException)
            {
                problem = $"The editor cannot read the cutscene as the lower mods leave it: {e.Message}";
                return null;
            }
        }
        (string File, byte[] Bytes)? whole = null;
        (string File, byte[] Bytes)? patch = null;
        try
        {
            if (activeMod != null && activeMod.Files.TryGetValue(path, out var file))
                whole = (file, SharedFile.ReadAllBytes(file));
            if (activeMod != null && activeMod.Patches.TryGetValue(path, out var patchFile))
                patch = (patchFile, SharedFile.ReadAllBytes(patchFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = e.Message;
            return null;
        }
        return Load(path, b, whole, patch, thn, ids, ids, out problem);
    }

    /// <summary>
    /// The document of a new cutscene of the active mod, at a path that the edit view does not have (decision D134 of
    /// the M8b design). It starts absent: no file, no B and an empty W, and <see cref="ThnEdits.NewCutscene"/> makes the
    /// script. A save of an absent document writes nothing, or deletes the file of an earlier save.
    /// </summary>
    /// <param name="path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
    public static ThnDocument New(string path, IThnEvaluator thn) => New(path, thn, ThnElementId.Counter());

    private static ThnDocument New(string path, IThnEvaluator thn, Func<ThnElementId> ids) =>
        new(path, null, ThnFileKind.NewHere, ThnDocumentState.Absent, "", false, null, null, thn, ids);

    /// <summary>
    /// The document that a saved file of the active mod gives, with the same B. The editor uses it after a save, so
    /// that W is what a reload of the saved files gives (rule F14). The elements that the load makes keep the ids of
    /// the elements of W in their order, so a selection survives a save. A new cutscene without content gives the
    /// absent document, for example after a save that deleted its file.
    /// </summary>
    /// <param name="content">The bytes of the patch or of the whole file, or null when the active mod has no patch
    /// now.</param>
    /// <param name="file">The file, for messages.</param>
    /// <exception cref="InvalidOperationException">The content gives no document, for example a whole file that does
    /// not evaluate. A save checks its content first, so this does not occur after a save.</exception>
    public ThnDocument Reopen(byte[]? content, string file)
    {
        if (content == null && Kind == ThnFileKind.NewHere)
            return New(Path, thn, ids);
        (string File, byte[] Bytes)? source = content == null ? null : (file, content);
        return Load(Path, Base, IsWholeFile ? source : null, IsWholeFile ? null : source, thn, ids, ReusedIds(), out var problem)
               ?? throw new InvalidOperationException(problem);
    }

    /// <summary>Puts back a state that <see cref="State"/> gave, to undo or redo an edit.</summary>
    public void Restore(ThnDocumentState restored) => state = restored;

    void IEditFile.Restore(IFileState restored) => Restore((ThnDocumentState)restored);

    /// <summary>Puts a new W in place.</summary>
    /// <param name="unpinned">Selects the pins that go, or null for none.</param>
    /// <param name="pinned">The pins that come in addition, or null for none.</param>
    public void Change(ThnScriptModel working, Func<ThnPin, bool>? unpinned = null, IReadOnlyCollection<ThnPin>? pinned = null)
    {
        var pins = state.Pins;
        if (unpinned != null && pins.Where(unpinned).ToList() is { Count: > 0 } gone)
            pins = pins.Except(gone);
        if (pinned is { Count: > 0 })
            pins = pins.Union(pinned);
        state = new ThnDocumentState(working, pins);
    }

    /// <summary>A new id for a new element.</summary>
    public ThnElementId NewId() => ids();

    /// <summary>The index of an element in its list of W, or -1.</summary>
    public int IndexOf(ThnElementKind kind, ThnElementId id) => Working.Elements(kind).FindIndex(e => e.Id == id);

    /// <summary>The table of the element of B that an element of W comes from, or null when the element has no origin:
    /// a new element, or an element of a whole file.</summary>
    public ThnTable? LowerTable(ThnElementKind kind, ThnElement element) =>
        element.Origin < 0 ? null : Base!.Elements(kind)[element.Origin].Table;

    /// <summary>Why an edit other than a delete cannot change an element, or null.</summary>
    public string? ReadOnlyReasonOf(ThnElementKind kind, ThnElementId id) =>
        ReadOnlyReason ?? (kind == ThnElementKind.Entity && IsHidden(id) ? HiddenEntityMessage : null);

    /// <summary>True when a later entity of W with the same name hides an entity, so the engine does not make
    /// it.</summary>
    internal bool IsHidden(ThnElementId entity) => Hidden().Contains(entity);

    /// <summary>
    /// Why a patch cannot change or delete an element, or null. A patch finds a lower element by its key, so a lower
    /// element without a key cannot change: an event without a time, a known type or a list of text targets.
    /// </summary>
    public string? PatchProblem(ThnElementKind kind, ThnElement element)
    {
        if (IsWholeFile || LowerTable(kind, element) is not { } lower)
            return null;
        return ThnKeys.Signature(kind, ThnKeys.Match(kind, lower), Symbols) == null
            ? $"A patch cannot change this {ThnPatch.Singular(kind)} of the lower cutscene, because it has no key: a " +
              "time, a type that the engine knows, and a list of text targets."
            : null;
    }

    /// <summary>
    /// The text that a save writes, or null when the edits do not change the file: W has its initial state, or its
    /// text equals the text of the initial state. Then the file keeps its bytes (rule P2). The text of a patch without
    /// entries is null, so the save deletes the patch file.
    /// </summary>
    public ThnFileText? ChangedFile()
    {
        if (ReadOnlyReason != null || state == Initial)
            return null;
        // An absent new cutscene deletes the file of an earlier save, and else changes nothing.
        if (!state.Exists)
            return SourceHash == null ? null : new ThnFileText(null, null);
        var text = Text(state);
        initialText ??= Text(Initial);
        return text.Problem == null && initialText.Problem == null && text.Text == initialText.Text ? null : text;
    }

    /// <summary>
    /// The save check: evaluates the text of a save, applies a patch to B again, and compares the canonical text of the
    /// result with the canonical text of W. The texts are equal only when the trees are equal bit for bit, also in the
    /// sign of 0, the key order and the order of the elements.
    /// </summary>
    /// <returns>Null when the check passes, or else the problem.</returns>
    public string? SaveProblem(ThnFileText text)
    {
        const string start = "The saved text does not give the edited cutscene";
        // An absent new cutscene has no file.
        if (!state.Exists)
            return text.Text == null ? null : $"{start}: the cutscene does not exist, so its file has no text.";
        var globals = Working.ToGlobals();
        ThnScriptModel? result;
        try
        {
            var bytes = text.ToBytes();
            if (IsWholeFile)
                result = bytes == null ? null : ThnScriptModel.From(thn.Evaluate(bytes, Path), ThnElementId.Counter());
            else if (bytes == null)
                result = Base;
            else
            {
                var applied = ThnPatch.Read(bytes, Path, thn).Apply(Base!, Symbols, ThnElementId.Counter());
                if (applied.Model == null)
                    return $"{start}: {applied.Errors[0]}";
                result = applied.Model;
            }
            if (result != null && ThnWriter.Write(result.ToGlobals()) == ThnWriter.Write(globals))
                return null;
        }
        catch (Exception e) when (e is ArgumentException or ThnEvaluationException or ThnPatchException or FormatException)
        {
            return $"{start}: {e.Message}";
        }
        var at = result == null ? null : ThnValues.FirstDifference(globals, result.ToGlobals());
        return at switch
        {
            null => $"{start}. A number differs in its float bits.",
            "" => $"{start}. The globals differ.",
            _ => $"{start}. The first difference is at {at}.",
        };
    }

    /// <summary>Reads a cutscene: the whole file of the active mod or else B, then the patch of the active mod.</summary>
    /// <param name="ids">The id source of the document.</param>
    /// <param name="loadIds">Gives the ids of the elements that the load makes.</param>
    private static ThnDocument? Load(string path, ThnScriptModel? b, (string File, byte[] Bytes)? whole,
        (string File, byte[] Bytes)? patch, IThnEvaluator thn, Func<ThnElementId> ids, Func<ThnElementId> loadIds,
        out string? problem)
    {
        problem = null;
        ThnScriptModel start;
        if (whole is { } wholeFile)
        {
            try
            {
                start = WithoutOrigins(ThnScriptModel.From(thn.Evaluate(wholeFile.Bytes, wholeFile.File), loadIds));
            }
            catch (Exception e) when (e is ThnEvaluationException or FormatException)
            {
                problem = $"The editor cannot read the cutscene file of the active mod: {e.Message}";
                return null;
            }
        }
        else if (b != null)
            start = b;
        else
        {
            problem = patch == null ? $"The edit view has no cutscene {path}." : $"The file to patch, {path}, does not exist.";
            return null;
        }

        var working = start;
        var pins = ImmutableHashSet<ThnPin>.Empty;
        var patchHeader = "";
        var dropsComments = false;
        var reason = whole != null && patch != null
            ? "The active mod has a whole file and a THN patch for this cutscene. Remove one of them, then open the profile again."
            : null;
        if (patch is { } patchFile)
        {
            var text = Encoding.ASCII.GetString(patchFile.Bytes);
            patchHeader = ThnPatch.HeaderOf(text);
            dropsComments = ThnPatch.HasInnerComments(text);
            IReadOnlyList<string> errors;
            try
            {
                var result = ThnPatch.Read(patchFile.Bytes, patchFile.File, thn).Apply(start, thn.Symbols, loadIds);
                errors = result.Errors;
                if (result.Model != null)
                {
                    working = result.Model;
                    pins = Pins(result);
                }
            }
            catch (ThnPatchException e)
            {
                errors = e.Problems;
            }
            if (errors.Count > 0)
                reason ??= $"The THN patch of the active mod has an error, so the editor cannot apply it: {errors[0]} " +
                           "Correct the file, then open the profile again.";
        }
        var kind = whole != null ? b != null ? ThnFileKind.WholeFileHere : ThnFileKind.NewHere
            : patch != null ? ThnFileKind.PatchedHere : ThnFileKind.Lower;
        var sourceHash = (whole ?? patch) is { } source ? ContentHash.Of(source.Bytes) : null;
        return new ThnDocument(path, b, kind, new ThnDocumentState(working, pins), patchHeader, dropsComments, reason,
            sourceHash, thn, ids);
    }

    /// <summary>The pins of a patch that applied: each path that it sets or removes, and the duration.</summary>
    private static ImmutableHashSet<ThnPin> Pins(ThnPatchResult result)
    {
        var pins = result.Changes.Where(c => c.Path != null).Select(c => new ThnPin(c.Element, c.Path!.Value));
        return [.. result.SetsDuration ? pins.Append(ThnPin.Duration) : pins];
    }

    /// <summary>The model with the origin -1 for each element, as for the elements of a whole file.</summary>
    internal static ThnScriptModel WithoutOrigins(ThnScriptModel model) => model with
    {
        Entities = [.. model.Entities.Select(e => e with { Origin = -1 })],
        Events = [.. model.Events.Select(e => e with { Origin = -1 })],
    };

    /// <summary>
    /// Gives the ids of the elements of W that a load makes again, in the order in which the load asks for them, and
    /// then new ids. A patch makes the new entities and then the new events of W. A whole file makes all elements.
    /// </summary>
    private Func<ThnElementId> ReusedIds()
    {
        var made = Working.Entities.Concat(Working.Events).Where(e => e.Origin < 0).Select(e => e.Id).ToList();
        var next = 0;
        return () => next < made.Count ? made[next++] : ids();
    }

    private ThnFileText Text(ThnDocumentState s)
    {
        if (!s.Exists)
            return new ThnFileText(null, null);
        try
        {
            if (IsWholeFile)
                return new ThnFileText(ThnWriter.Write(s.Working.ToGlobals()), null);
            var patch = ThnPatch.Make(Base!, s.Working, Symbols, header, s.Pins);
            return new ThnFileText(patch.IsEmpty ? null : patch.Write(), null);
        }
        catch (ArgumentException e)
        {
            return new ThnFileText(null, $"The editor cannot write this cutscene: {e.Message}");
        }
    }

    /// <summary>The entities of W that a later entity with the same name hides. Names compare without letter case, as
    /// the engine compares them (ThnScript.cs:107).</summary>
    private IReadOnlySet<ThnElementId> Hidden()
    {
        if (hidden is { } known && ReferenceEquals(known.Working, state.Working))
            return known.Ids;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<ThnElementId>();
        for (var i = Working.Entities.Count - 1; i >= 0; i--)
        {
            if (Working.Entities[i].Table[ThnKeys.EntityName] is ThnText name && !names.Add(name.Value))
                result.Add(Working.Entities[i].Id);
        }
        hidden = (state.Working, result);
        return result;
    }
}
