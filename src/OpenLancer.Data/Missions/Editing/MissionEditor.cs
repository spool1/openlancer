using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The only write path for mission files (section 4 of the M6b design, decision D660). The edit session owns one
/// instance (<see cref="EditSession.Missions"/>). The studio, the completeness fixes, the M3 rename and delete, the
/// tests and the agent tools call it. It makes no ImGui call.
/// </summary>
/// <remarks>
/// <para>The editor keeps a model of each mission script by the state of W (section 7 of the M6b design). The model
/// of a state comes from the cache, so an undo finds the model of its state again.</para>
/// <para>Each request runs on the thread that owns the session (section 4.0.3 of the M6b design).</para>
/// </remarks>
public sealed partial class MissionEditor
{
    private readonly EditSession session;
    private IMissionWorldSource world;
    private readonly MissionModelCache cache = new();
    private readonly Dictionary<string, long> versions = new(VfsPath.Comparer);

    /// <param name="world">Gives the names of the game data that the checks need.</param>
    public MissionEditor(EditSession session, IMissionWorldSource world)
    {
        this.session = session;
        this.world = world;
        seenRevision = session.Revision;
        References = new MissionReferences(session);
        session.Changed += OnHistory;
    }

    /// <summary>The reference provider for the world edits of M3 and of the Database (section 4.10 of the M6b
    /// design).</summary>
    public MissionReferences References { get; }

    /// <summary>The read queries in the vocabulary of the commands (section 4.12 of the M6b design).</summary>
    public MissionQueries Queries => queries ??= new MissionQueries(session, this);

    private MissionQueries? queries;

    /// <summary>The OpenLancer marks of the checks, when the world source is the source of the session. The editor
    /// gives the marks of the engine. Other sources keep their own marks.</summary>
    public IEngineCoverage Coverage
    {
        get => (world as SessionMissionWorld)?.Coverage ?? world.Inputs().Coverage;
        set
        {
            if (world is SessionMissionWorld source)
                source.Coverage = value;
        }
    }

    /// <summary>Gives the editor another world source, for example a fixed world in a test. The models go.</summary>
    internal void UseWorld(IMissionWorldSource source)
    {
        world = source;
        cache.Use(source.Inputs());
    }

    private (object View, IReadOnlyList<string> Paths)? scanned;

    /// <summary>
    /// The install paths of the mission scripts of W: the scripts of the scan of the edit view (decision D535), and each
    /// script that the session opened, for example a new script. A script that W deletes is not in the list.
    /// </summary>
    public IReadOnlyList<string> ScriptPaths()
    {
        if (scanned is not var (view, paths) || !ReferenceEquals(view, session.EditView))
        {
            var files = session.EditView.CreateFileSystem();
            paths = session.DataPath("MISSIONS") is { } missions ? MissionFiles.Scan(files, missions) : [];
            scanned = (session.EditView, paths);
        }
        // A result gives the path as the session stores it (section 4.0.7 of the M6b design).
        return paths.Concat(session.OpenMissionScripts()).Distinct(VfsPath.Comparer)
            .Select(p => session.OpenDocument(p)).OfType<EditDocument>()
            .Where(d => d is { HoldsScript: true, State.Exists: true }).Select(d => d.Path).ToList();
    }

    /// <summary>True when the mission editor is the only write path of a file (decision D660): a mission script, or the
    /// ship build file of a script. The raw INI editors send such a file to <see cref="RawMissionEdit"/>.</summary>
    /// <param name="path">An install path, with <c>\</c> or <c>/</c>, in any letter case.</param>
    public bool IsMissionFile(string path) =>
        InstallPaths.TryParse(path) is { } normal && session.IsGuardedMissionFile(normal);

    /// <summary>Why the user cannot change a mission file, or null: the session takes no edits, or the file is a whole
    /// file of another mod.</summary>
    public string? ReadOnlyReason(string path) =>
        session.ReadOnlyReason ??
        (InstallPaths.TryParse(path) is { } normal && session.OpenDocument(normal) is { } document ? document.ReadOnlyReason : null);

    /// <summary>
    /// Why the user cannot change a section of a mission file, or null (section 8.1 of the M6b design): the session
    /// takes no edits, the file is a whole file of another mod, or a patch cannot change the section, for example a
    /// repeated nickname of a lower layer (decision D20). A member of a linked prefab instance takes no line edits
    /// either. The pages show the reason and use the read-only state of their widgets.
    /// </summary>
    public string? ReadOnlyReason(SectionRef section)
    {
        if (ReadOnlyReason(section.Path) is { } reason)
            return reason;
        if (InstallPaths.TryParse(section.Path) is not { } normal || session.OpenDocument(normal) is not { } document ||
            document.IndexOf(section.Id) is not (>= 0 and var index))
            return null;
        if (document.ReadOnlyReasonAt(index) is { } why)
            return why.Length == 0 ? why : char.ToUpperInvariant(why[0]) + why[1..];
        return LinkedInstanceOf(section) is { } linked ? PrefabLinks.MemberReason(PrefabLinks.PrefabNameOf(linked), linked.Record.Id) : null;
    }

    /// <summary>The linked prefab instance whose member is a section of a mission script, or null (section 5.1 of the M6b
    /// design). A removed member is not a member.</summary>
    public LinkedInstance? LinkedInstanceOf(SectionRef section)
    {
        if (InstallPaths.TryParse(section.Path) is not { } normal || !SectionKeys.IsMissionFile(normal) ||
            Graph(normal).Instances.Count == 0)
            return null;
        return Model(normal).Analysis.Prefabs?.Instances.FirstOrDefault(i =>
            i.Linked?.Sections.Values.Any(s => s.Ref.Equals(section)) == true)?.Linked;
    }

    /// <summary>
    /// True for a shared ship build file of the missions: a file of ship builds directly in <c>DATA\MISSIONS</c> that no
    /// script names. Random encounters and bases can use its builds too, so a change of such a build needs a
    /// confirmation (review finding RB2 of the M6b design).
    /// </summary>
    /// <param name="path">An install path, in any letter case.</param>
    public bool IsSharedBuildFile(string path) =>
        InstallPaths.TryParse(path) is { } normal &&
        MissionDraft.IsSharedBuildPath(session, normal, session.IsGuardedMissionFile);

    /// <summary>
    /// The lines of a section of W that the active mod sets: each line that differs from the same line of B, by its key
    /// and its place among the lines of that key. All lines of a section that B does not have, and all lines of a whole
    /// file of the active mod, count. The pages show the blue bar at these lines (section 8.1 of the M6b design, UI rule
    /// 7). Empty without an active mod or for a section that W does not have.
    /// </summary>
    /// <returns>The indexes of the lines in the section of W.</returns>
    public IReadOnlySet<int> ChangedLines(SectionRef section)
    {
        if (session.ActiveMod == null || InstallPaths.TryParse(section.Path) is not { } normal ||
            session.OpenDocument(normal) is not { } document || document.IndexOf(section.Id) is not (>= 0 and var index))
            return new HashSet<int>();
        var working = document.Working[index];
        var id = document.IdentityAt(index);
        if (document.IsWholeFile || document.BaseSection(id) is not { } lower)
            return Enumerable.Range(0, working.Count).ToHashSet();
        var before = SectionComparer.ByName(lower);
        var changed = new HashSet<int>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var row = 0; row < working.Count; row++)
        {
            var entry = working[row];
            var occurrence = seen.GetValueOrDefault(entry.Name);
            seen[entry.Name] = occurrence + 1;
            if (before.GetValueOrDefault(entry.Name) is not { } lines || occurrence >= lines.Count ||
                !SectionComparer.SameLines([lines[occurrence]], [entry], document.IsBinary))
                changed.Add(row);
        }
        return changed;
    }

    /// <summary>
    /// The lines of B of a section whose base changed after the edit of the active mod (section 6.3 of the M6b design,
    /// decision D429): the <c>ol.base</c> hash that the patch writes for the section differs from the hash of B now. Each
    /// line is "key = values". Null when the base did not change, or for a section that B does not have. The page then
    /// shows the banner with "Compare", "Keep my lines" and "Use the new base".
    /// </summary>
    public IReadOnlyList<string>? ChangedBase(SectionRef section)
    {
        if (session.ActiveMod == null || InstallPaths.TryParse(section.Path) is not { } normal ||
            session.OpenDocument(normal) is not { IsWholeFile: false } document ||
            document.IndexOf(section.Id) is not (>= 0 and var index))
            return null;
        var id = document.IdentityAt(index);
        if (document.BaseSection(id) is not { } lower || document.WrittenBaseHash(id) is not { } written ||
            SectionHash.Same(written, SectionHash.Of(lower)))
            return null;
        return lower.Select(LineText).ToList();
    }

    /// <summary>A line as "key = values", with the values as the game reads them.</summary>
    public static string LineText(LibreLancer.Data.Ini.Entry entry) =>
        entry.Count == 0 ? $"{entry.Name} =" : $"{entry.Name} = {string.Join(", ", MissionValues.TextsOf(entry))}";

    /// <summary>The model cache, for the tests.</summary>
    internal MissionModelCache Cache => cache;

    /// <summary>
    /// The current model of a script: W, the index, the problems, the prefab instances, the graph file and the version.
    /// </summary>
    /// <param name="script">The install path of the script, with <c>\</c> or <c>/</c>, in any letter case.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the edit view has no such mission
    /// script.</exception>
    public MissionModel Model(string script)
    {
        var document = Script(script);
        return ModelOf(document, document.State);
    }

    /// <summary>
    /// The analysis of a script now, with its linked prefab instances. For a script <c>prefab:mod:&lt;id&gt;</c>, it is
    /// the analysis of the template of the prefab in the role form, as prefab mode shows it (section 8.6 of the M6b
    /// design). The commands take both forms of a script, so a caller that makes commands from the triggers of a script
    /// reads them here.
    /// </summary>
    /// <returns>Null when the edit view has no such script, or when no mod has the prefab or its file has a
    /// problem. A command on such a script gives the error.</returns>
    public MissionAnalysis? AnalysisOf(string script)
    {
        if (MissionRefs.IsPrefab(script))
            return PrefabRef.TryParse(script) is { } prefab ? PrefabMode(prefab)?.Analysis : null;
        return InstallPaths.TryParse(script) is { } path &&
               session.OpenDocument(path) is { HoldsScript: true, State.Exists: true } document
            ? ModelOf(document, document.State).Analysis
            : null;
    }

    /// <summary>
    /// The mission errors of the save gate (section 6.10 of the M6b design): the errors of each script that the active
    /// mod changes. When the active mod removes or renames a name of the game data, also the errors of each other
    /// script, so a line of any layer that names such a name blocks the save (decision D81). The origin rule of D551
    /// makes each problem of a lower mod or of the base game a warning, so each error here is an error of the active
    /// mod.
    /// </summary>
    internal IEnumerable<Diagnostic> SaveErrors(string activeMod)
    {
        var scripts = ScriptPaths();
        if (scripts.Count == 0)
            return [];
        var removes = world.Inputs().BaseWorld is { } names && names.RemovesNamesOf(activeMod);
        return scripts.Where(p => removes || session.OpenDocument(p) is { } document && document.ChangesBase(document.State))
            .SelectMany(p => Model(p).Problems.Where(d => d.Severity == DiagnosticSeverity.Error)).ToList();
    }

    /// <summary>The model of a state of a script, from the cache or a new analysis, with the linked prefab instances of
    /// the graph file (section 5 of the M6b design).</summary>
    /// <param name="names">The names of the game data, or null for the names now.</param>
    /// <param name="modText">Gives the text of an editor file of the active mod, or null for W now. The commit check of
    /// a batch gives the texts before the batch, so the problems before the batch have the records before it.</param>
    internal MissionModel ModelOf(EditDocument document, DocumentState state, MissionInputs? names = null,
        Func<string, string?>? modText = null)
    {
        var snapshot = names ?? world.Inputs();
        cache.Use(snapshot);
        var path = document.Path;
        var analysis = cache.Find(path, state);
        if (analysis == null)
        {
            analysis = Analyze(snapshot, document, state);
            cache.Put(path, state, analysis);
        }
        var graphText = modText != null ? modText(GraphFile.PathOf(path)) : GraphFileText(path);
        return new MissionModel(path, Version(path), Linked(analysis, modText))
        {
            GraphFile = graphText,
            Graph = modText != null ? GraphFile.Read(graphText, out _) : GraphOf(path, graphText),
            ReadOnlyReason = session.ReadOnlyReason ?? document.ReadOnlyReason,
        };
    }

    // The last graph file that the editor read for each script, by its text (review finding 8 of M6b-2).
    private readonly Dictionary<string, (string? Text, GraphFile File)> graphs = new(VfsPath.Comparer);

    /// <summary>The records of the text of the graph file of a script. The same text gives the same record, so a view
    /// that keys on the record builds again only after a change of the file. Each script has its own entry, so a read
    /// of another script does not make a new record.</summary>
    private GraphFile GraphOf(string path, string? text)
    {
        if (graphs.TryGetValue(path, out var known) && string.Equals(known.Text, text, StringComparison.Ordinal))
            return known.File;
        var file = GraphFile.Read(text, out _);
        graphs[path] = (text, file);
        return file;
    }

    /// <summary>
    /// The records of the graph file of a script in W (section 6.8 of the M6b design): the node places, the acts and
    /// the other records. <see cref="GraphFile.Empty"/> when the active mod has no graph file for the script.
    /// </summary>
    /// <param name="script">The install path of the script, in any letter case.</param>
    public GraphFile Graph(string script) =>
        InstallPaths.TryParse(script) is { } path ? GraphOf(path, GraphFileText(path)) : GraphFile.Empty;

    /// <summary>The problems of the graph file of a script: the lines that the editor cannot read.</summary>
    public IReadOnlyList<string> GraphProblems(string script)
    {
        if (InstallPaths.TryParse(script) is not { } path)
            return [];
        GraphFile.Read(GraphFileText(path), out var problems);
        return problems;
    }

    /// <summary>
    /// The snapshot of a full run of the mission checks for the worker of the editor: the names of the last snapshot,
    /// a new generation, and the overlay of W of each mission file that the session changed (section 7 of the M6b
    /// design).
    /// </summary>
    public MissionInputs RunInputs() =>
        (world.Inputs() with { Generation = session.NextMissionGeneration() }).WithOverlay(session.MissionOverlay());

    /// <summary>
    /// Takes the analyses of a full run into the model cache. A run result replaces the entries of the states that it
    /// used. A run of another snapshot of the names gives nothing.
    /// </summary>
    /// <param name="inputs">The inputs of the run, from <see cref="RunInputs"/>.</param>
    public void TakeRun(MissionInputs inputs, MissionRunResult result)
    {
        if (cache.Inputs == null || !ReferenceEquals(cache.Inputs.World, inputs.World))
            return;
        foreach (var (path, analysis) in result.Analyses)
        {
            if (inputs.Overlay.GetValueOrDefault(path)?.State is { } state)
                cache.Put(path, state, analysis);
            else if (session.OpenedDocument(path) is { } document && ReferenceEquals(document.State, document.Initial))
                cache.Put(path, document.State, analysis);
        }
    }

    /// <summary>The version of a script (section 4.2 of the M6b design). It starts at 1 and grows when the history
    /// changes W of the script: a commit, an undo, a redo or a restore.</summary>
    /// <param name="script">The install path of the script, in any letter case.</param>
    public long Version(string script) =>
        versions.GetValueOrDefault(InstallPaths.TryParse(script) ?? script, 1);

    /// <summary>The document of a mission script of the edit view.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the text is not an install path
    /// (decision D763 of the MCP1 design). <see cref="EditErrorKind.NotFound"/> with the detail
    /// <see cref="MissionErrors.ScriptNotFound"/>: the edit view has no such script.</exception>
    internal EditDocument Script(string script)
    {
        var path = InstallPaths.Parse(script);
        if (session.OpenDocument(path) is { HoldsScript: true, State.Exists: true } document)
            return document;
        throw new EditException(MissionErrors.ScriptNotFoundError(path));
    }

    /// <summary>The analysis of a state of a script. A state that the session did not change since the open or the
    /// last save is the file of the edit view, so its raw text keeps the comments of the file.</summary>
    private MissionAnalysis Analyze(MissionInputs snapshot, EditDocument document, DocumentState state)
    {
        var path = document.Path;
        if (ReferenceEquals(state, document.Initial) && state.Exists &&
            MissionRun.Analyze(snapshot, MissionRun.Info(snapshot, path), cache.BaseRun) is { } saved)
            return saved;
        var overlay = new Dictionary<string, MissionOverlayFile>(session.MissionOverlay(), VfsPath.Comparer)
        {
            [path] = session.OverlayOf(document, state),
        };
        return MissionRun.AnalyzeScript(snapshot.WithOverlay(overlay), path, state.Working, document.IsBinary,
            cache.BaseRun);
    }

    /// <summary>The text of the graph file of a script in the active mod, or null.</summary>
    private string? GraphFileText(string path) => GraphFileText(path, saved: false);

    /// <param name="saved">True for the text at the last save.</param>
    private string? GraphFileText(string path, bool saved)
    {
        try
        {
            return saved ? session.ModFileSavedText(GraphFile.PathOf(path)) : session.ModFileText(GraphFile.PathOf(path));
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The overrides of a linked instance that the last save does not have (section 5.6 of the M6b design): the fields
    /// whose override differs from the saved record, and the roles of the removed members that the saved record does
    /// not have. The Inspector gives such an override the blue bar beside the violet bar (UI rule 7).
    /// </summary>
    public (IReadOnlySet<string> Fields, IReadOnlySet<string> Members) UnsavedOverrides(InstanceRef instance)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        if (InstallPaths.TryParse(instance.Script) is not { } path || Graph(path).Instance(instance.Id) is not { } record)
            return (fields, members);
        var saved = GraphFile.Read(GraphFileText(path, saved: true), out _).Instance(instance.Id);
        foreach (var value in record.Overrides)
        {
            if (saved?.Overrides.FirstOrDefault(o => o.Field == value.Field) is not { } before || !before.Equals(value))
                fields.Add(value.Field);
        }
        members.UnionWith(record.Removed.Where(role => saved?.Removed.Contains(role) != true));
        return (fields, members);
    }
}
