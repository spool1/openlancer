using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private readonly Dictionary<string, ThnDocument> cutscenes = new(VfsPath.Comparer);

    /// <summary>Why a cutscene path that the session tried to open has no document.</summary>
    private readonly Dictionary<string, string> cutsceneProblems = new(VfsPath.Comparer);

    /// <summary>The errors of <see cref="CutsceneErrors"/>, for the W and the B that gave them, with the errors of B. B
    /// changes only with a save, so an edit checks only W again.</summary>
    private readonly Dictionary<string, (ThnScriptModel Working, ThnScriptModel? Base, IReadOnlyList<Diagnostic> Lower,
        IReadOnlyList<Diagnostic> Errors)> cutsceneErrors = new(VfsPath.Comparer);

    /// <summary>The files of the lower layer, made at the first use.</summary>
    private FileSystem? lowerFiles;

    /// <summary>The install paths of the cutscene scripts that the session opened, in normal form. A new cutscene that
    /// does not exist now, before New cutscene or after an undo of it, is not in the list.</summary>
    public IReadOnlyCollection<string> OpenCutscenes =>
        cutscenes.Where(c => c.Value.State.Exists).Select(c => c.Key).ToList();

    /// <summary>
    /// The install paths of the cutscenes of W that the active mod adds and that the lower layer does not have: the new
    /// cutscenes of New cutscene, before and after their save, and the whole files of the active mod that the session
    /// opened. The Cutscenes tab and the list of the cutscenes show the new cutscenes before their save.
    /// </summary>
    public IReadOnlyList<string> NewCutscenes =>
        cutscenes.Values.Where(d => d is { Kind: ThnFileKind.NewHere, State.Exists: true }).Select(d => d.Path).ToList();

    /// <summary>
    /// W of a cutscene script (<c>.thn</c>): the cutscene of the lower layer with the patch of the active mod and the
    /// edits, or the whole file of the active mod with the edits. The session opens the cutscene at the first use. The
    /// edit view stays as the files on disk give it.
    /// </summary>
    /// <returns>W, or null when the edit view has no such cutscene or the editor cannot read it.
    /// <see cref="CutsceneReadOnlyReason(string)"/> then tells why.</returns>
    public ThnScriptModel? Cutscene(string path) => CutsceneDocument(path)?.Working;

    /// <summary>B: the cutscene as the lower layer gives it, or null when the lower layer does not have it or the
    /// session cannot open the cutscene.</summary>
    public ThnScriptModel? LowerCutscene(string path) => CutsceneDocument(path)?.Base;

    /// <summary>Where W of a cutscene comes from, or null when the session cannot open the cutscene.</summary>
    public ThnFileKind? CutsceneKind(string path) => CutsceneDocument(path)?.Kind;

    /// <summary>
    /// Why the editor cannot change a cutscene, or null: the session is read-only, the edit view has no such cutscene,
    /// the editor cannot read it, or the file of the active mod for it has an error.
    /// </summary>
    public string? CutsceneReadOnlyReason(string path) =>
        ReadOnlyReason ?? (TryCutscene(path, out var document)
            ? document.State.Exists ? document.ReadOnlyReason : NoCutscene(path)
            : CutsceneProblem(path));

    /// <summary>Why the editor cannot change an element of a cutscene with an edit other than a delete, or null. A
    /// later entity with the same name makes an entity read-only, because the engine does not use it.</summary>
    public string? CutsceneReadOnlyReason(ThnRef element) =>
        CutsceneReadOnlyReason(element.Path) ?? CutsceneDocument(element.Path)!.ReadOnlyReasonOf(element.Kind, element.Id);

    /// <summary>
    /// Where the edit view gets a cutscene from, without opening it: the kind of the file of the active mod, as
    /// <see cref="CutsceneKind"/> gives it after an open, and true when a lower mod adds or changes the cutscene.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not an install path.</exception>
    public ThnFileSource CutsceneSource(string path)
    {
        var normal = InstallPaths.TryParse(path) ?? throw new ArgumentException($"{path} is not an install path.", nameof(path));
        var lowerMods = lower.Any(m => m.Files.ContainsKey(normal) || m.Patches.ContainsKey(normal));
        ThnFileKind kind;
        // A new cutscene that is not saved yet is new here too.
        if (ActiveMod != null && (ActiveMod.Files.ContainsKey(normal) ||
                                  cutscenes.GetValueOrDefault(normal) is { Kind: ThnFileKind.NewHere, State.Exists: true }))
            kind = LowerFiles.FileExists(normal) ? ThnFileKind.WholeFileHere : ThnFileKind.NewHere;
        else
            kind = ActiveMod != null && ActiveMod.Patches.ContainsKey(normal) ? ThnFileKind.PatchedHere : ThnFileKind.Lower;
        return new ThnFileSource(kind, lowerMods);
    }

    /// <summary>True when the session opened a cutscene and its W differs from the state of the open or of the last
    /// save, so a save would write it.</summary>
    public bool HasUnsavedCutsceneEdits(string path) =>
        VfsPath.Normalize(path) is { } normal && cutscenes.TryGetValue(normal, out var document) &&
        document.State != document.Initial;

    /// <summary>The document of a cutscene, or null when the session cannot open it or the cutscene is a new cutscene
    /// that does not exist now.</summary>
    internal ThnDocument? CutsceneDocument(string path) =>
        TryCutscene(path, out var document) && document.State.Exists ? document : null;

    /// <summary>
    /// The install path of a cutscene in the form that the session stores: the letter case of its file, with <c>\</c>
    /// between the parts. A <see cref="ThnRef"/> compares its path with letter case, so a view that names the elements
    /// of a script uses this form, as the results of the commands do. Null when the session cannot open the cutscene.
    /// </summary>
    public string? CutscenePath(string path) => CutsceneDocument(path)?.Path;

    /// <summary>
    /// The install path of a new cutscene of the active mod: <c>DATA\MODS\&lt;mod id&gt;\CUTSCENES\&lt;name&gt;.thn</c>
    /// (decision D67). Null without an active mod. <see cref="ThnEdits.NewCutsceneRefusal"/> checks the name.
    /// </summary>
    public string? NewCutscenePath(string name) =>
        ActiveMod is { } mod ? $@"{NewCutsceneFolder(mod.Id)}\{name}.thn" : null;

    /// <summary>The folder of the new cutscenes of a mod, as an install path.</summary>
    internal static string NewCutsceneFolder(string modId) => $@"DATA\MODS\{modId}\CUTSCENES";

    /// <summary>True when the edit view or W has a file at an install path, in any letter case.</summary>
    internal bool HasCutsceneFile(string normal) =>
        cutscenes.GetValueOrDefault(normal) is { } document
            ? document.State.Exists || ActiveMod?.Files.ContainsKey(normal) == true
            : LowerFiles.FileExists(normal) || ActiveMod != null &&
              (ActiveMod.Files.ContainsKey(normal) || ActiveMod.Patches.ContainsKey(normal));

    /// <summary>The start of the message of an error of a cutscene in the save gate (decision D137 of the M8b
    /// design).</summary>
    public const string CutsceneCrash = "The game crashes when it plays this cutscene.";

    /// <summary>
    /// The errors of the checks of a cutscene (<see cref="ThnChecks.Errors"/>) that W has and B does not have (decision
    /// D137 of the M8b design). Such a value makes the engine throw when it reads the script or builds the scene, and no
    /// code of the game catches it, so the game crashes. The save gate and Play refuse them. Each error keeps the place
    /// of the check, and its message starts with <see cref="CutsceneCrash"/>. B does not give the errors, so each error
    /// has the origin of the active mod. The checks run without game data, so they do not check templates and costumes.
    /// Empty when the session has no W of the cutscene.
    /// </summary>
    public IReadOnlyList<Diagnostic> CutsceneErrors(string path)
    {
        if (CutsceneDocument(path) is not { } document)
            return [];
        var cached = cutsceneErrors.TryGetValue(document.Path, out var known) && ReferenceEquals(known.Base, document.Base);
        if (cached && ReferenceEquals(known.Working, document.Working))
            return known.Errors;
        var lower = cached ? known.Lower
            : document.Base == null ? [] : ThnChecks.Errors(document.Path, document.Base.ToGlobals(), thn);
        // The lower layer does not give these errors, so the active mod causes them: its patch, its whole file or its edits.
        var origin = ActiveMod is { } active ? MessageOrigin.Mod(active.Id, ModEvidence.ActiveMod) : null;
        var errors = Diagnostic.NewIn(lower, ThnChecks.Errors(document.Path, document.Working.ToGlobals(), thn))
            .Select(e => e with
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"{CutsceneCrash} {e.Message}",
                Origin = e.Origin ?? origin,
            })
            .ToList();
        cutsceneErrors[document.Path] = (document.Working, document.Base, lower, errors);
        return errors;
    }

    /// <summary>
    /// The cutscene errors of the save gate: the errors of each cutscene that the active mod changes (see
    /// <see cref="CutsceneErrors"/>), in the files that a command changed and in the patches and whole files of the
    /// mod. A cutscene that the editor cannot change, or a new cutscene that does not exist now, is not checked. A whole
    /// file of the active mod that does not evaluate is an error too: the engine reads it with the same Thorn VM and
    /// throws (ResourceLoadExtensions.cs:48-49, RoomGameplay.cs:851).
    /// </summary>
    private IEnumerable<Diagnostic> CutsceneGateErrors(Mod active)
    {
        var paths = touched.Concat(active.Patches.Keys).Concat(active.Files.Keys).Select(p => VfsPath.Normalize(p) ?? p)
            .Where(p => p.EndsWith(".thn", StringComparison.OrdinalIgnoreCase)).Distinct(VfsPath.Comparer);
        foreach (var path in paths)
        {
            if (TryCutscene(path, out var document))
            {
                if (document is { ReadOnlyReason: null, State.Exists: true })
                {
                    foreach (var error in CutsceneErrors(path))
                        yield return error;
                }
            }
            else if (active.Files.ContainsKey(path))
            {
                yield return Diagnostic.Error(path, $"{CutsceneCrash} {CutsceneProblem(path)}") with
                {
                    Cutscene = new ThnLocation(path),
                    Origin = MessageOrigin.Mod(active.Id, ModEvidence.ActiveMod),
                };
            }
        }
    }

    /// <summary>The documents of the new cutscenes that the session has, also the absent ones. The list of the
    /// cutscenes reads their states.</summary>
    internal IEnumerable<ThnDocument> NewCutsceneDocuments => cutscenes.Values.Where(d => d.Kind == ThnFileKind.NewHere);

    /// <summary>
    /// The layer that gives a file of the edit view or a new cutscene of W, for an entry of a files list: the active mod
    /// for its whole file and for a new cutscene that is not saved yet, else the last lower mod with a whole file at the
    /// path, else the base data. A patch does not give the file.
    /// </summary>
    internal (ValueOrigin Origin, string? ModId, bool Unsaved) FileOrigin(string path)
    {
        var normal = VfsPath.Normalize(path) ?? path;
        if (ActiveMod is { } active)
        {
            if (active.Files.ContainsKey(normal))
                return (ValueOrigin.ActiveMod, active.Id, false);
            if (cutscenes.GetValueOrDefault(normal) is { Kind: ThnFileKind.NewHere, State.Exists: true })
                return (ValueOrigin.ActiveMod, active.Id, true);
        }
        return lower.LastOrDefault(m => m.Files.ContainsKey(normal)) is { } mod
            ? (ValueOrigin.LowerMod, mod.Id, false)
            : (ValueOrigin.Base, null, false);
    }

    /// <summary>The names of the THN environment, with their group and number.</summary>
    internal IReadOnlyDictionary<string, ThnSymbolInfo> ThnSymbols => thn.Symbols;

    /// <summary>The evaluator that runs the cutscene scripts of the session, for a Lua value text of an edit.</summary>
    internal IThnEvaluator ThnEvaluator => thn;

    /// <summary>The files of the lower layer, made at the first use.</summary>
    private FileSystem LowerFiles => lowerFiles ??= lowerView.CreateFileSystem();

    /// <summary>Why the session cannot open a cutscene.</summary>
    internal string CutsceneProblem(string path) =>
        (VfsPath.Normalize(path) is { } normal ? cutsceneProblems.GetValueOrDefault(normal) : null) ?? NoCutscene(path);

    private static string NoCutscene(string path) => $"The edit view has no cutscene {path}.";

    /// <summary>
    /// The document of a new cutscene at a path: the document that the session has, or a new absent document (see
    /// <see cref="ThnDocument.New"/>). A new document removes the kept problem of the path, and the session takes the
    /// fingerprint of the path, as at the open of a cutscene. Else a look-up of the path before New cutscene, for
    /// example by the refusal check, would keep the problem, and the session would give no W after the command.
    /// </summary>
    private ThnDocument NewCutsceneDocument(string normal)
    {
        if (TryCutscene(normal, out var document))
            return document;
        cutsceneProblems.Remove(normal);
        inputs.Remember(normal, lower);
        document = ThnDocument.New(normal, thn);
        cutscenes[normal] = document;
        return document;
    }

    /// <summary>The document of a cutscene. Opens it at the first use. A path that does not open keeps its problem, so
    /// the session reads it once.</summary>
    private bool TryCutscene(string path, [NotNullWhen(true)] out ThnDocument? document)
    {
        document = null;
        // An install path is relative to the game folder, so the session refuses a drive, a ':' and a rooted path
        // (decision D763 of the MCP1 design).
        var normal = InstallPaths.TryParse(path);
        if (normal == null || !normal.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
            return false;
        if (cutscenes.TryGetValue(normal, out document))
            return true;
        if (cutsceneProblems.ContainsKey(normal))
            return false;
        // The session keeps the path in the letter case of the files, as for an INI file. So the first request does not
        // decide the letter case of the results, of the edited paths and of the recovery file.
        normal = LetterCaseOfFiles(normal);
        // The fingerprint first: a change after it shows at the save.
        inputs.Remember(normal, lower);
        byte[]? bytes;
        try
        {
            bytes = LowerFiles.FileExists(normal) ? LowerFiles.ReadAllBytes(normal) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            cutsceneProblems[normal] = e.Message;
            return false;
        }
        document = ThnDocument.Open(normal, bytes, ActiveMod, thn, out var problem);
        if (document == null)
        {
            cutsceneProblems[normal] = problem!;
            return false;
        }
        cutscenes[normal] = document;
        return true;
    }
}
