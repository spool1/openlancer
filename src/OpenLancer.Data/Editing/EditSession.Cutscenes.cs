using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private readonly Dictionary<string, ThnDocument> cutscenes = new(VfsPath.Comparer);

    /// <summary>Why a cutscene path that the session tried to open has no document.</summary>
    private readonly Dictionary<string, string> cutsceneProblems = new(VfsPath.Comparer);

    /// <summary>The errors of <see cref="CutsceneErrors"/> of each cutscene, with what gave them.</summary>
    private readonly Dictionary<string, CutsceneCheckState> cutsceneErrors = new(VfsPath.Comparer);

    /// <summary>The files of the lower layer, made at the first use.</summary>
    private FileSystem? lowerFiles;

    /// <summary>The INI lines of the edit view that name cutscenes, for the edit view that gave them.</summary>
    private (ModOverlay View, ThnReferences References)? editViewReferences;

    /// <summary>The INI files that a command changed, and their lines of W that name cutscenes, for the
    /// <see cref="CacheRevision"/> that gave them.</summary>
    private (int Revision, HashSet<string> Files, ThnReferences References)? changedIniReferences;

    /// <summary>The items of <see cref="BrokenCutsceneItems"/>, for the <see cref="CacheRevision"/> that gave
    /// them.</summary>
    private (int Revision, IReadOnlyList<(ThnItemKind Kind, string Name)> Items)? brokenCutsceneItems;

    /// <summary>The cutscenes of <see cref="ReferrerCutscenes"/>, for the <see cref="CacheRevision"/> that gave
    /// them.</summary>
    private (int Revision, IReadOnlyList<string> Paths)? referrerCutscenes;

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
    /// design). A name problem does not crash the game, so its message does not start with it.</summary>
    public const string CutsceneCrash = "The game crashes when it plays this cutscene.";

    /// <summary>
    /// The problems of the checks of a cutscene (<see cref="ThnChecks.Check"/>) that W has and B does not have, with
    /// the game data of W (decision D137 and section 12.1 of the M8b design, decisions D96 and D120 of the M21 design).
    /// The save gate and Play refuse them.
    /// </summary>
    /// <remarks>
    /// <para>An error makes the engine throw when it reads the script or builds the scene, and no code of the game
    /// catches it, so the game crashes. Its message starts with <see cref="CutsceneCrash"/>. A name problem is a template
    /// that names no item, where the scene shows less, for example an entity without a model. Its message tells what the
    /// scene does. Both kinds block the save, because a value of the active mod outside a closed list is an error.</para>
    /// <para>The game data comes from the data lists of W (see <see cref="SessionThnData"/>), so an unsaved INI edit
    /// counts, for example a new costume. B gets the same game data without the answers that the active mod causes
    /// (<see cref="SessionThnData.Lower"/>). So a retail value that is not in the lists gives the same problem in B, and
    /// stays out of the result. A costume that the active mod deletes or renames gives no problem in B, so its problem
    /// in W stays in the result. Each problem keeps the place of the check. B does not give the problems, so each
    /// problem has the origin of the active mod.</para>
    /// <para>The errors of W and B compare by their messages (<see cref="Diagnostic.NewIn"/>). The retail data has a
    /// name problem, so the name problems compare by their elements (see <see cref="NewNameProblems"/>).</para>
    /// <para>The result is cached. The cache checks W or B again when it changes, and both when the game data gives
    /// another answer. The Problems section of the Cutscene page reads the result in each frame.</para>
    /// <para>Empty when the session has no W of the cutscene.</para>
    /// </remarks>
    public IReadOnlyList<Diagnostic> CutsceneErrors(string path)
    {
        if (CutsceneDocument(path) is not { } document)
            return [];
        var known = cutsceneErrors.GetValueOrDefault(document.Path);
        // The game data is the same when nothing changed since the last call, or when the lists still give each answer
        // that the checks read.
        var data = known != null && (known.Revision == CacheRevision || known.Data.Unchanged()) ? known.Data : null;
        if (data != null)
        {
            known!.Revision = CacheRevision;
            if (ReferenceEquals(known.Working, document.Working) && ReferenceEquals(known.Base, document.Base))
                return known.Errors;
        }
        var lower = data != null && ReferenceEquals(known!.Base, document.Base) ? known.Lower : null;
        data ??= new SessionThnData(this, document.Path);
        lower ??= document.Base == null
            ? new ThnCheckResult([], [])
            : ThnChecks.Check(document.Path, document.Base.ToGlobals(), thn, data.Lower);
        var working = ThnChecks.Check(document.Path, document.Working.ToGlobals(), thn, data);
        // The lower layer does not give these problems, so the active mod causes them: its patch, its whole file or its
        // edits.
        var origin = ActiveMod is { } active ? MessageOrigin.Mod(active.Id, ModEvidence.ActiveMod) : null;
        var crashes = Diagnostic.NewIn(lower.Errors, working.Errors)
            .Select(e => e with { Message = $"{CutsceneCrash} {e.Message}" });
        var errors = crashes.Concat(NewNameProblems(document.Working, lower.NameProblems, working.NameProblems))
            .Select(e => e with { Severity = DiagnosticSeverity.Error, Origin = e.Origin ?? origin })
            .ToList();
        var checks = working.Errors.Select(e => e with { Message = $"{CutsceneCrash} {e.Message}" })
            .Concat(working.NameProblems).ToList();
        cutsceneErrors[document.Path] = new CutsceneCheckState(document.Working, document.Base, data, lower, errors, checks)
        {
            Revision = CacheRevision,
        };
        return errors;
    }

    /// <summary>
    /// All problems of the checks of W of a cutscene with the game data of W, before the compare with B (see
    /// <see cref="CutsceneErrors"/>). So the result also has the problems that B has, for example a retail template that
    /// the lists do not have. An error starts with <see cref="CutsceneCrash"/>, as in <see cref="CutsceneErrors"/>. The
    /// Inspector shows these problems at their fields, and the color of a field tells if the save gate refuses the
    /// problem. The result shares the cache of <see cref="CutsceneErrors"/>. Empty when the session has no W of the
    /// cutscene.
    /// </summary>
    public IReadOnlyList<Diagnostic> CutsceneChecks(string path)
    {
        CutsceneErrors(path);
        return CutsceneDocument(path) is { } document && cutsceneErrors.GetValueOrDefault(document.Path) is { } known
            ? known.Checks
            : [];
    }

    /// <summary>
    /// The name problems of W that B does not have. A message names its entity, and the retail data has a name problem:
    /// the PSYS "ring_2" of <c>Intro_waterplanet.thn</c> names the effect "ring", which no effect file has. A compare of
    /// the messages would make such a problem new after a rename of its entity. So a problem of an element of W that
    /// comes from B is new only when its element in B has no problem with the same subject: the path of the value and
    /// the text (<see cref="Diagnostic.Subject"/>). A problem of an element without an origin, for example in a whole
    /// file of the active mod, compares by its message.
    /// </summary>
    private static IEnumerable<Diagnostic> NewNameProblems(ThnScriptModel working, IReadOnlyList<Diagnostic> lower,
        IReadOnlyList<Diagnostic> current)
    {
        var left = lower.ToList();
        foreach (var problem in current)
        {
            var (kind, origin) = problem.Cutscene is { Kind: { } k, Index: >= 0 and var index } &&
                                 index < working.Elements(k).Count
                ? (k, working.Elements(k)[index].Origin)
                : (default(ThnElementKind?), -1);
            var match = origin >= 0
                ? left.FindIndex(p => p.Cutscene is { } at && at.Kind == kind && at.Index == origin && p.Subject == problem.Subject)
                : left.FindIndex(p => p.Message == problem.Message);
            if (match >= 0)
                left.RemoveAt(match);
            else
                yield return problem;
        }
    }

    /// <summary>The errors of a cutscene, with the W, the B and the game data that gave them, the problems of B, and
    /// all problems of W (see <see cref="CutsceneChecks"/>).</summary>
    private sealed record CutsceneCheckState(ThnScriptModel Working, ThnScriptModel? Base, SessionThnData Data,
        ThnCheckResult Lower, IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Checks)
    {
        /// <summary>The <see cref="CacheRevision"/> of the last call that found the game data the same.</summary>
        public int Revision { get; set; }
    }

    /// <summary>The INI lines of the edit view that name cutscenes. The scan reads each INI file of the edit view, so it
    /// runs once for each edit view, when a check first needs it. The edit view changes only with a save.</summary>
    internal ThnReferences EditViewReferences
    {
        get
        {
            if (editViewReferences is not { } known || !ReferenceEquals(known.View, EditView))
            {
                known = (EditView, ThnReferences.Scan(EditView.CreateFileSystem()));
                editViewReferences = known;
            }
            return known.References;
        }
    }

    /// <summary>
    /// True when the game plays a script only in space, as W gives it (see <see cref="ThnReferences.OnlyInSpace(string)"/>).
    /// The lines of W replace the lines of the edit view in each INI file that a command changed. So an unsaved
    /// <c>Act_CallThorn</c> line counts at once, and the save gate gives the same result in one save and in two saves.
    /// The session reads the changed files once for each <see cref="CacheRevision"/>.
    /// </summary>
    internal bool PlaysOnlyInSpace(string path)
    {
        var (files, changed) = ChangedIniReferences();
        return ThnReferences.OnlyInSpace(EditViewReferences.UsedBy(path).Where(r => !files.Contains(r.File))
            .Concat(changed.UsedBy(path)));
    }

    /// <summary>
    /// The INI files that a command changed, and their lines of W that name cutscenes. The files of the open transaction
    /// or the open batch count too. A transaction or a batch adds its files to the files that a command changed only at
    /// its commit. The commit checks of a batch run before the commit. So the result of the first agent INI edit of a
    /// session gives the same problems as the save gate.
    /// A file that W does not have, for example after the undo of a new file, has no lines.
    /// </summary>
    private (HashSet<string> Files, ThnReferences References) ChangedIniReferences()
    {
        if (changedIniReferences is not { } known || known.Revision != CacheRevision)
        {
            var files = touched.Concat(OpenPaths).Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(VfsPath.Comparer);
            var references = ThnReferences.Read(files.Order(StringComparer.OrdinalIgnoreCase).Select(file =>
                (file, documents.GetValueOrDefault(file) is { State.Exists: true } document
                    ? document.Working
                    : (IReadOnlyList<Section>)[])));
            known = (CacheRevision, files, references);
            changedIniReferences = known;
        }
        return (known.Files, known.References);
    }

    /// <summary>
    /// The template and clip items whose answer the active mod makes other than Ready (see
    /// <see cref="SessionThnData.AnswerOf"/>). The active mod removes such an item, makes it unusable or breaks its
    /// model. Such an item can give an error in a cutscene that the active mod does not change.
    /// </summary>
    /// <remarks>
    /// <para>Only a change of the active mod gives such an answer. So the candidates are these names:</para>
    /// <list type="bullet">
    /// <item>The names that the active mod removes from the lists of the templates
    /// (<see cref="DataLists.Removed"/>).</item>
    /// <item>The entries of these lists that the search of the referrer errors visits
    /// (<see cref="DataLists.WalkedEntries"/>).</item>
    /// <item>The values of a values list, for example the rooms of petalDB, that the lower layer has and W does not
    /// have.</item>
    /// <item>The clips that the lower layer has and W does not have.</item>
    /// </list>
    /// <para>A candidate counts only when its answer for a kind of its list is not Ready and the active mod causes it.
    /// So a change that keeps each answer gives no item, for example new hit points for each ship.</para>
    /// <para>The session keeps the result for each <see cref="CacheRevision"/>. A save changes the number too. Empty
    /// without an active mod.</para>
    /// </remarks>
    internal IReadOnlyList<(ThnItemKind Kind, string Name)> BrokenCutsceneItems()
    {
        if (brokenCutsceneItems is { } known && known.Revision == CacheRevision)
            return known.Items;
        var items = new List<(ThnItemKind Kind, string Name)>();
        if (ActiveMod != null)
        {
            var seen = new HashSet<(ThnItemKind, string)>();
            void Consider(string list, string name)
            {
                foreach (var kind in ThnTemplateRules.CheckedKinds)
                {
                    if (ThnTemplateRules.ListsOf(kind).Contains(list, StringComparer.OrdinalIgnoreCase) &&
                        seen.Add((kind, name.ToLowerInvariant())) &&
                        SessionThnData.AnswerOf(this, kind, name) is
                            { State: not (null or ThnItemState.Ready), ByActiveMod: true })
                        items.Add((kind, name));
                }
            }

            foreach (var entry in Lists.Removed())
                Consider(entry.List, entry.Value);
            foreach (var (list, name) in Lists.WalkedEntries())
                Consider(list, name);
            foreach (var list in ThnTemplateRules.CheckedKinds.SelectMany(ThnTemplateRules.ListsOf)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var value in Lists.RemovedValues(list))
                    Consider(list, value);
            }
            foreach (var clip in Lists.RemovedClips())
                Consider(ThnTemplateRules.ListsOf(ThnItemKind.Animation)[0], clip);
        }
        brokenCutsceneItems = (CacheRevision, items);
        return items;
    }

    /// <summary>
    /// The cutscenes of the edit view that hold the name of an item of <see cref="BrokenCutsceneItems"/> as a word. They
    /// are install paths in normal form, in path order. A cutscene that the active mod does not change has the same
    /// script in W and in B. So only an answer that the active mod causes can give it an error (see
    /// <see cref="CutsceneErrors"/>), and the result holds each such cutscene with an error. A match is not always a use
    /// of the item, so the caller checks each cutscene.
    /// </summary>
    /// <remarks>
    /// <para>The search uses the word index of the story mentions: one build for each edit view, in a worker task. The
    /// call waits for the build. The session keeps the result for each <see cref="CacheRevision"/>. Without an active
    /// mod or without a broken item, the result is empty and the call does not start the build.</para>
    /// <para>The index finds a name only when a file holds it as one word. So it does not find a template that a text
    /// script makes from parts.</para>
    /// <para>The Problems section of the Cutscene page reads <see cref="CutsceneErrors"/> in each frame. So
    /// <see cref="CutsceneErrors"/> and <see cref="CutsceneChecks"/> do not call this method.</para>
    /// </remarks>
    internal IReadOnlyList<string> ReferrerCutscenes()
    {
        if (referrerCutscenes is { } known && known.Revision == CacheRevision)
            return known.Paths;
        IReadOnlyList<string> paths = [];
        if (BrokenCutsceneItems() is { Count: > 0 } items)
        {
            ReferrerLookups++;
            paths = items.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(Mentions.Cutscenes)
                .Distinct(VfsPath.Comparer)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        referrerCutscenes = (CacheRevision, paths);
        return paths;
    }

    /// <summary>The number of searches of <see cref="ReferrerCutscenes"/> in the word index. A test reads it to check
    /// the cache.</summary>
    internal int ReferrerLookups { get; private set; }

    /// <summary>The install paths of the cutscenes that the active mod changes, in normal form: the files that a
    /// command changed, and the patches and the whole files of the mod.</summary>
    private IEnumerable<string> ChangedCutscenePaths(Mod active) =>
        touched.Concat(active.Patches.Keys).Concat(active.Files.Keys).Select(p => VfsPath.Normalize(p) ?? p)
            .Where(p => p.EndsWith(".thn", StringComparison.OrdinalIgnoreCase)).Distinct(VfsPath.Comparer);

    /// <summary>
    /// The cutscenes whose errors the save gate gives (see <see cref="CutsceneGateErrors"/>): each cutscene that the
    /// active mod changes and that the editor can change, as install paths in normal form. Empty without an active mod.
    /// An agent batch that changes an INI file compares their errors before and after the batch.
    /// </summary>
    internal IReadOnlyList<string> GateCutscenes() =>
        ActiveMod is { } active
            ? ChangedCutscenePaths(active)
                .Where(p => TryCutscene(p, out var document) && document is { ReadOnlyReason: null, State.Exists: true })
                .ToList()
            : [];

    /// <summary>
    /// The cutscene errors of the save gate: the errors of each cutscene that the active mod changes (see
    /// <see cref="CutsceneErrors"/>), in the files that a command changed and in the patches and whole files of the
    /// mod. A cutscene that the editor cannot change, or a new cutscene that does not exist now, is not checked. A whole
    /// file of the active mod that does not evaluate is an error too: the engine reads it with the same Thorn VM and
    /// throws (ResourceLoadExtensions.cs:48-49, RoomGameplay.cs:851).
    /// </summary>
    private IEnumerable<Diagnostic> CutsceneGateErrors(Mod active)
    {
        foreach (var path in ChangedCutscenePaths(active))
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
    internal FileSystem LowerFiles => lowerFiles ??= lowerView.CreateFileSystem();

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
