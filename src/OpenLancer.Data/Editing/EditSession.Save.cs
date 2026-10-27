using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>The text of the active mod at the open or at the last save.</summary>
    private ModText savedText;

    /// <summary>The hash of the active mod's strings.json at the open or at the last save, or null when it had
    /// none.</summary>
    private string? textHash;

    /// <summary>
    /// The text that a save writes now for an INI file that the session opened. The text is the patch of the file, or
    /// the text of a whole file of the mod. Null when a save does not write the file, or cannot write it. A patch
    /// without sections gives an empty text, because the save then deletes the patch file.
    /// </summary>
    /// <param name="path">An install path in normal form.</param>
    internal string? SaveText(string path)
    {
        if (!documents.TryGetValue(path, out var document))
            return null;
        if (document.IsWholeFile)
            return document.ChangedWholeFile() is { Bytes: { } bytes } ? IniDocument.TextEncoding.GetString(bytes) : null;
        return document.ChangedPatch() is { Succeeded: true } patch ? patch.Text : null;
    }

    /// <summary>
    /// Writes the edits as patch files and the text as strings.json of the active mod. The save writes all files
    /// or none (review finding F1).
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>For each file that a command changed, the writer makes the patch and checks it. A file whose patch
    /// does not change keeps its text (rule P2). A patch without sections deletes its file (P4). A cutscene gives a
    /// THN patch, or its whole file when the active mod has one. The save evaluates the text of each cutscene and
    /// checks that it gives W. It writes no whole cutscene file into a mod in the OpenLancer repo (decision
    /// D70).</item>
    /// <item>Any problem stops the save: a patch that the writer cannot write, a text placeholder that the text of
    /// the mod does not have (S8), a patch line of a text key that holds the id of a text of a mod as a number (S8),
    /// a strings.json with comments (F13). When the text changes, the save checks the
    /// placeholders of all patches and whole INI files of the mod, because strings.json gives the text of each.</item>
    /// <item>A change of the lower layer since the open stops the save (F11): the base data, a lower mod, or the mod
    /// list of the profile. The session writes the recovery file, and the profile must open again. A whole mission file
    /// and an editor file of the mod do not depend on the lower layer, so a change of their path in a lower mod does
    /// not stop the save.</item>
    /// <item>A file of the active mod that changed outside the editor stops the save, unless
    /// <paramref name="overwrite"/> is true (P7).</item>
    /// <item><see cref="SaveTransaction"/> writes the files (P5, P6). Then W becomes what the saved files give
    /// (F14, P10), the edit view is built again (P11), and the history is saved.</item>
    /// </list>
    /// </remarks>
    /// <param name="overwrite">True to replace the files of the active mod that changed outside the editor.</param>
    /// <exception cref="InvalidOperationException">A transaction or a batch is open.</exception>
    public SaveResult Save(bool overwrite = false) => Save(overwrite, null);

    /// <param name="afterSteps">Called with k after k moves and deletes of the save transaction, from k = 0 at its
    /// commit point. The fault tests use it.</param>
    internal SaveResult Save(bool overwrite, Action<int>? afterSteps)
    {
        ThrowIfBusy();
        if (readOnlyReason != null)
            return NotSaved([Diagnostic.Error(Profile.FilePath, readOnlyReason)]);
        var active = ActiveMod!;
        var problems = new List<Diagnostic>();
        bool recovered;
        Mod activeNow;
        ModProfile profileNow;
        try
        {
            // A save of this session that stopped after its commit point is decided, so it completes first.
            recovered = SaveTransaction.Recover(active.Folder, problems);
            activeNow = Mod.Load(active.Folder);
            profileNow = ModProfile.Load(Profile.FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ModLoadException)
        {
            problems.Add(Diagnostic.Error(active.Folder, e.Message));
            return NotSaved(problems);
        }
        if (recovered)
        {
            // The completed save changed the files of the mod.
            ActiveMod = activeNow;
            EditView = BuildEditView(activeNow);
            CacheRevision++;
        }

        // The save gate runs on each save, also when no file changes, so that Play never starts with an error.
        problems.AddRange(SaveProblems());
        if (problems.Any(IsError))
            return NotSaved(problems);

        var plan = Plan(active, activeNow, problems);
        if (problems.Any(IsError))
            return NotSaved(problems);

        var writes = plan.Where(p => !p.SameAsDisk).ToList();
        if (writes.Count > 0)
        {
            // An editor file of the mod and a whole mission file of the mod do not depend on the lower layer.
            var stale = inputs.Changed(plan.Where(p => p.Source is EditDocument { IsWholeFile: false } or ThnDocument)
                .Select(p => p.Source!.Path), profileNow, active);
            if (stale.Count > 0)
                return Stale(stale, problems);
        }

        var changedOnDisk = writes.Where(p => ContentHash.OfFile(p.Change.Path) != p.KnownHash).Select(p => p.Change.Path).ToList();
        if (changedOnDisk.Count > 0 && !overwrite)
        {
            problems.AddRange(changedOnDisk.Select(path => Diagnostic.Error(path,
                "Something outside the editor changed this file after the editor read it. Save again with overwrite " +
                "to replace it.")));
            return NotSaved(problems, changedOnDisk);
        }

        Exception? incomplete = null;
        if (writes.Count > 0)
        {
            try
            {
                incomplete = SaveTransaction.Commit(active.Folder, writes.Select(p => p.Change).ToList(), problems, afterSteps);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problems.Add(Diagnostic.Error(active.Folder, "The save wrote nothing: " + e.Message));
                return NotSaved(problems);
            }
            if (incomplete != null)
                problems.Add(Diagnostic.Warning(active.Folder,
                    $"A file did not move into place: {incomplete.Message} The save journal holds the save. The " +
                    "editor moves the other files at the next save, or when the profile opens again."));
        }
        AfterSave(plan, problems);
        return new SaveResult(true,
            writes.Where(p => p.Change.Content != null).Select(p => p.Change.Path).ToList(),
            writes.Where(p => p.Change.Content == null).Select(p => p.Change.Path).ToList(),
            problems, []) { Incomplete = incomplete != null };
    }

    /// <summary>
    /// The errors of the save gate (section 11.3 of the M21 design), and the base-game warnings of the key rule. A save
    /// writes nothing while it has an error. An edit with an error stays a draft in W and in the recovery file, and
    /// never reaches the mod files.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>The sections that the active mod adds or changes, in the files that a command changed and in the files of
    /// its patches: the errors of the field checks (<see cref="Check(SectionRef)"/>, decision D110) and of the list
    /// checks (<see cref="CheckLists"/>). A section that the editor cannot change is not checked.</item>
    /// <item>Each line of any layer under a closed rule that names an entry that the active mod removed (decision
    /// D81), or that the active mod made unusable without a delete (decision D82, as the user decided it on
    /// 2026-10-18). For example, the active mod removes the points of a curve, and a retail light names the curve.
    /// See <see cref="DataLists.ReferrerErrors"/>.</item>
    /// <item>The mission errors of the active mod (section 6.10 of the M6b design): the errors of each script that it
    /// changes, and of each script that names a name that it removes or renames. A mission problem of a lower mod or of
    /// the base game is a warning by the origin rule (decision D551), so it does not block. The Console shows the
    /// warnings of the scripts.</item>
    /// <item>The errors of the format checks and of the content checks that the active mod adds (milestone M11, see
    /// <see cref="TargetProblems"/>). They run for a tier-1 mod that names a retail target. For example, a good with the
    /// price 0 crashes the retail game.</item>
    /// <item>The checks of the mission patches on the linked game of each retail target: the errors of rule T01 and the
    /// warnings of rule T02 (section 6.12 of the M6b design, see <see cref="TargetGame"/>).</item>
    /// <item>Each FLHash collision of a hash list that the active mod adds a name to.</item>
    /// <item>The errors of the component checks (layer 3, milestone M3): a zone of the active mod whose nickname another
    /// zone of any system has, also by FLHash. Retail drops one of the two zones.</item>
    /// <item>The errors of the checks of each cutscene that the active mod changes, that the lower cutscene does not
    /// have, and each whole cutscene file of the active mod that does not evaluate (decision D137 of the M8b design,
    /// see <see cref="CutsceneErrors"/>). The game crashes when it plays such a cutscene.</item>
    /// </list>
    /// <para>Each list error tells what the game does with the data, for example "The game stops loading with this
    /// value."</para>
    /// <para>A field-check error or a missing required key that the base game has too does not block the save. The list
    /// has it as a warning with the origin base game (the key rule, decision D508 of the console filter design, see
    /// <see cref="IsBaseGameProblem(Diagnostic)"/>). All other errors block.</para>
    /// </remarks>
    public IReadOnlyList<Diagnostic> SaveProblems()
    {
        if (ActiveMod is not { } active)
            return [];
        var problems = new List<Diagnostic>();
        // The place of a cutscene error tells two cutscenes with the same message apart.
        var seen = new HashSet<(SectionRef?, string?, string, ThnLocation?)>();
        void Add(Diagnostic problem)
        {
            if (problem.Severity == DiagnosticSeverity.Error &&
                seen.Add((problem.Target, problem.Key?.ToLowerInvariant(), problem.Message, problem.Cutscene)))
                problems.Add(problem);
        }

        // An error of the base game becomes a warning by the key rule (decision D508 of the console filter design).
        void AddChecked(SectionRef target, EditDocument document, int index, Diagnostic problem, ListProblem? list)
        {
            if (problem.Severity == DiagnosticSeverity.Error && IsBaseGameProblem(target, document, index, problem, list))
            {
                var warning = BaseGameWarning(problem);
                if (seen.Add((warning.Target, warning.Key?.ToLowerInvariant(), warning.Message, null)))
                    problems.Add(warning);
            }
            else
                Add(problem);
        }

        var paths = touched.Concat(active.Patches.Keys).Select(p => VfsPath.Normalize(p) ?? p)
            .Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).Distinct(VfsPath.Comparer);
        foreach (var path in paths)
        {
            if (OpenDocument(path) is not { ReadOnlyReason: null } document)
                continue;
            for (var i = 0; i < document.Working.Count; i++)
            {
                var id = document.IdentityAt(i);
                if (document.BaseSection(id) is { } lower && ReferenceEquals(lower, document.Working[i]) ||
                    document.ReadOnlyReasonAt(i) != null)
                    continue;
                var target = new SectionRef(document.Path, id);
                if (SchemaOf(target) is { } schema)
                {
                    foreach (var problem in Check(target, schema, document, i))
                        AddChecked(target, document, i, problem, null);
                }
                foreach (var problem in Lists.Problems(target))
                {
                    if (problem.Kind == ListProblemKind.RequiredMissing)
                        AddChecked(target, document, i, problem.Diagnostic, problem);
                    else
                        Add(problem.Diagnostic);
                }
            }
        }

        foreach (var problem in Lists.ReferrerErrors())
            Add(problem.Diagnostic);

        // The mission errors come before the target checks of M11. Rule M01 has the text and the place of the trigger
        // check of M11, so the gate keeps the mission problem with its rule and its origin (decision D556).
        foreach (var problem in Missions.SaveErrors(active.Id))
            Add(problem);

        // The format checks and the content checks of the retail targets (milestone M11).
        foreach (var problem in TargetProblems())
            Add(problem);

        // The mission patches on the game of each retail target (section 6.12 of the M6b design).
        foreach (var problem in MissionTargetProblems(active))
        {
            if (problem.Severity == DiagnosticSeverity.Error)
                Add(problem);
            else if (seen.Add((problem.Target, problem.Key?.ToLowerInvariant(), problem.Message, null)))
                problems.Add(problem);
        }

        // Layer 3: a zone nickname that another zone of any system has, also by FLHash (milestone M3).
        foreach (var problem in Scene.ComponentChecks.SaveErrors(this))
            Add(problem);

        var changed = ActiveChangedPaths().ToHashSet(VfsPath.Comparer);
        foreach (var definition in Lists.RuleData.Lists.Values.Where(d => d is { Hash: true, Source.Role: not null }))
        {
            if (!DataFiles.Paths(definition.Source!.Role!).Any(changed.Contains) ||
                Lists.List(definition.Id) is not { Collisions.Count: > 0 } list)
                continue;
            // An entry of a whole file of the active mod gives a warning only (review finding R22).
            foreach (var entry in list.Collisions.SelectMany(g => g).Select(list.Find).OfType<ListEntry>()
                         .Where(e => e is { Origin: ValueOrigin.ActiveMod, Section: not null }))
            {
                foreach (var problem in Lists.Problems(entry.Section!).Where(p => p.Kind == ListProblemKind.HashCollision))
                    Add(problem.Diagnostic);
            }
        }

        foreach (var problem in CutsceneGateErrors(active))
            Add(problem);
        return problems;
    }

    /// <summary>
    /// The files that the edits change, with their new content. Adds a problem for each file that the save cannot
    /// write.
    /// </summary>
    /// <param name="activeNow">The active mod as it is on disk now.</param>
    private List<PlannedFile> Plan(Mod active, Mod activeNow, List<Diagnostic> problems)
    {
        var plan = new List<PlannedFile>();
        foreach (var key in touchedModFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            var modFile = modFiles[key];
            if (!modFile.HasChanges)
                continue;
            var target = Path.Combine(active.Folder, modFile.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
            if (!PathRules.IsInside(target, active.Folder))
            {
                problems.Add(Diagnostic.Error(target, $"The file {target} is not in the folder {active.Folder}."));
                continue;
            }
            plan.Add(new PlannedFile(new FileChange(target, modFile.Bytes), modFile.SourceHash, modFile));
        }
        foreach (var path in touched.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (cutscenes.TryGetValue(path, out var cutscene))
            {
                if (PlanCutscene(cutscene, active, activeNow, problems) is { } planned)
                    plan.Add(planned);
                continue;
            }
            var document = documents[path];
            if (document.IsWholeFile)
            {
                if (PlanWholeFile(document, active, activeNow, problems) is { } whole)
                    plan.Add(whole);
                continue;
            }
            var patch = document.ChangedPatch();
            if (patch == null)
                continue;
            if (!patch.Succeeded)
            {
                problems.AddRange(patch.Problems);
                continue;
            }
            problems.AddRange(patch.Notes);
            problems.AddRange(MissingText(document));
            problems.AddRange(FrozenTextIds(document.Path, patch));
            if (activeNow.Files.ContainsKey(path))
            {
                problems.Add(Diagnostic.Error(path, $"The active mod now replaces {path} with a whole file. Open the " +
                                                    "profile again."));
                continue;
            }
            var patches = Path.Combine(active.Folder, Mod.PatchesFolder);
            var target = activeNow.Patches.GetValueOrDefault(path) ?? Path.Combine(patches, LocalPath(path));
            if (!PathRules.IsInside(target, patches))
            {
                problems.Add(Diagnostic.Error(path, $"The patch file {target} is not in the folder {patches}."));
                continue;
            }
            plan.Add(new PlannedFile(new FileChange(target, patch.Sections.Count == 0 ? null : patch.ToBytes()),
                document.SourceHash, document));
        }
        if (!text.SameAs(savedText))
        {
            // The new text is the text of each placeholder of the mod, also in the files that the save keeps.
            var written = plan.Select(p => p.Source!.Path).ToHashSet(VfsPath.Comparer);
            problems.AddRange(MissingText(activeNow, written));
            var path = Path.Combine(active.Folder, Mod.StringsFile);
            try
            {
                plan.Add(new PlannedFile(new FileChange(path, text.ToFile(path)), textHash, null));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add(Diagnostic.Error(path, "The editor cannot write the text into this file: " + e.Message));
            }
        }
        return plan;
    }

    /// <summary>
    /// The file that the edits of a cutscene change: the THN patch, or the whole file when the active mod replaces the
    /// cutscene with one. Null when the file does not change, or when the save cannot write it. A problem then tells
    /// why.
    /// </summary>
    /// <param name="activeNow">The active mod as it is on disk now.</param>
    private PlannedFile? PlanCutscene(ThnDocument cutscene, Mod active, Mod activeNow, List<Diagnostic> problems)
    {
        var path = cutscene.Path;
        if (cutscene.ChangedFile() is not { } file)
            return null;
        if (cutscene.IsWholeFile && WholeCutsceneInRepo(active) is { } inRepo)
        {
            problems.Add(Diagnostic.Error(path, inRepo));
            return null;
        }
        if ((file.Problem ?? cutscene.SaveProblem(file)) is { } problem)
        {
            problems.Add(Diagnostic.Error(path, problem));
            return null;
        }
        var (folder, files, other) = cutscene.IsWholeFile
            ? (Mod.FilesFolder, activeNow.Files, activeNow.Patches)
            : (Mod.PatchesFolder, activeNow.Patches, activeNow.Files);
        if (other.ContainsKey(path))
        {
            problems.Add(Diagnostic.Error(path, $"The active mod now has another kind of file for {path}. Open the " +
                                                "profile again."));
            return null;
        }
        var root = Path.Combine(active.Folder, folder);
        var target = files.GetValueOrDefault(path) ?? Path.Combine(root, LocalPath(path));
        if (!PathRules.IsInside(target, root))
        {
            problems.Add(Diagnostic.Error(path, $"The file {target} is not in the folder {root}."));
            return null;
        }
        if (cutscene.DropsComments && file.Text != null)
            problems.Add(Diagnostic.Warning(target, "The save writes this THN patch in canonical form. It keeps the " +
                                                    "comments at the top of the file and drops the other comments."));
        return new PlannedFile(new FileChange(target, file.ToBytes()), cutscene.SourceHash, cutscene);
    }

    /// <summary>
    /// The whole file that the edits of a document of the kind "whole file" give (section 6.7 of the M6b design), or
    /// null when the file does not change or the save cannot write it. A problem then tells why. The save writes no
    /// whole mission file into a mod in the OpenLancer repo (decision D70).
    /// </summary>
    /// <param name="activeNow">The active mod as it is on disk now.</param>
    private PlannedFile? PlanWholeFile(EditDocument document, Mod active, Mod activeNow, List<Diagnostic> problems)
    {
        var path = document.Path;
        if (document.ChangedWholeFile() is not { } file)
            return null;
        if (file.Bytes != null && WholeMissionFileInRepo(active) is { } inRepo)
        {
            problems.Add(Diagnostic.Error(path, inRepo));
            return null;
        }
        if (file.Bytes != null)
            problems.AddRange(MissingText(document));
        var root = Path.Combine(active.Folder, Mod.FilesFolder);
        // A new script of the mod (CreateScript) has no file yet (decision D680).
        if (!activeNow.Files.TryGetValue(path, out var target) && !document.Initial.Exists && file.Bytes != null &&
            !activeNow.Patches.ContainsKey(path))
            target = Path.Combine(root, LocalPath(path));
        if (target == null)
        {
            problems.Add(Diagnostic.Error(path, $"The active mod has no whole file for {path} now. Open the profile " +
                                                "again."));
            return null;
        }
        if (!PathRules.IsInside(target, root))
        {
            problems.Add(Diagnostic.Error(path, $"The file {target} is not in the folder {root}."));
            return null;
        }
        if (file.Note != null)
            problems.Add(Diagnostic.Warning(target, file.Note));
        return new PlannedFile(new FileChange(target, file.Bytes), document.SourceHash, document);
    }

    /// <summary>Why the editor writes no whole mission file into a mod, or null: the mod is in the OpenLancer repo
    /// (decision D70, section 6.7 of the M6b design).</summary>
    internal static string? WholeMissionFileInRepo(Mod mod) => WholeFileInRepo(mod, "mission");

    /// <summary>Why the editor writes no whole cutscene file into a mod, or null: the mod is in the OpenLancer repo
    /// (decision D70).</summary>
    internal static string? WholeCutsceneInRepo(Mod mod) => WholeFileInRepo(mod, "cutscene");

    private static string? WholeFileInRepo(Mod mod, string kind) => RepoCheck.RepoRoot(mod.Folder) is { } repo
        ? $"The mod '{mod.Id}' is in the OpenLancer repo {repo}. The editor writes no whole {kind} file into it, " +
          "because the content scan cannot tell such a file from a copy of a retail script (decision D70)."
        : null;

    /// <summary>
    /// Stops a save because the lower layer changed (F11), and keeps the unsaved edits in the recovery file.
    /// </summary>
    /// <param name="sources">What changed: install paths, <see cref="LayerInputs.TextInputs"/> or the profile file.</param>
    private SaveResult Stale(List<string> sources, List<Diagnostic> problems)
    {
        problems.AddRange(sources.Select(source => Diagnostic.Error(source,
            "The base data, a lower mod or the mod list of the profile changed after the profile opened, so the " +
            "edits apply to old data. Open the profile again.")));
        var file = RecoveryFile.PathOf(ActiveMod!.Folder);
        var notKept = WriteRecovery();
        problems.AddRange(notKept);
        problems.Add(notKept.Count == 0
            ? Diagnostic.Warning(file, "The editor kept the unsaved edits in this file. The next open of the profile " +
                                       "offers to restore them.")
            : Diagnostic.Error(file, "The editor could not keep all unsaved edits in this file. When the profile " +
                                     "opens again, the edits that are not in the file are lost."));
        return NotSaved(problems);
    }

    /// <summary>Makes the saved state the state of the session (rules P10 and P11).</summary>
    private void AfterSave(List<PlannedFile> plan, List<Diagnostic> problems)
    {
        var active = ActiveMod!;
        try
        {
            ActiveMod = Mod.Load(active.Folder);
        }
        catch (ModLoadException e)
        {
            problems.Add(Diagnostic.Warning(active.Folder, "The saved mod does not load: " + e.Message));
        }
        foreach (var file in plan)
        {
            // W becomes what the saved file gives, so it equals the state that a reload gives (F14).
            switch (file.Source)
            {
                case null:
                    textHash = file.Change.Content == null ? null : ContentHash.Of(file.Change.Content);
                    break;
                case EditDocument document:
                    var diagnostics = new List<Diagnostic>();
                    documents[document.Path] = document.Reopen(active.Id, file.Change.Path, file.Change.Content, diagnostics);
                    problems.AddRange(diagnostics.Where(IsError).Select(d => d with { Severity = DiagnosticSeverity.Warning }));
                    break;
                case ThnDocument cutscene:
                    cutscenes[cutscene.Path] = cutscene.Reopen(file.Change.Content, file.Change.Path);
                    break;
                case ModFileDocument modFile:
                    modFiles[modFile.Path] = modFile.Reopen(file.Change.Content);
                    break;
            }
        }
        savedText = text;
        history.MarkSaved();
        CacheRevision++;
        // The reopened documents start at their saved states.
        worldStates.Clear();

        if (plan.Any(p => !p.SameAsDisk))
        {
            var before = EditView;
            EditView = BuildEditView(ActiveMod!);
            problems.AddRange(Diagnostic.NewIn(before.Diagnostics, EditView.Diagnostics).Where(IsError));
        }
        if (!HasRecovery)
        {
            try
            {
                DeleteRecoveryFile();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add(Diagnostic.Warning(RecoveryFile.PathOf(active.Folder), e.Message));
            }
        }
    }

    /// <summary>The text placeholders of W that the text of the session does not have (rule S8).</summary>
    private IEnumerable<Diagnostic> MissingText(EditDocument document)
    {
        for (var i = 0; i < document.Working.Count; i++)
        {
            foreach (var placeholder in document.Working[i].SelectMany(MissingPlaceholders))
            {
                var id = document.IdentityAt(i);
                yield return Diagnostic.Error(document.Path, $"{id}: {MissingTextMessage(placeholder)}") with
                {
                    Target = new SectionRef(document.Path, id),
                };
            }
        }
    }

    /// <summary>
    /// The text placeholders that the text of the session does not have, in the patches and the whole INI files of the
    /// active mod on disk.
    /// </summary>
    /// <param name="mod">The active mod as it is on disk now.</param>
    /// <param name="written">The install paths whose patch the save writes. They are not read from the disk.</param>
    private IEnumerable<Diagnostic> MissingText(Mod mod, IReadOnlySet<string> written)
    {
        foreach (var (file, section, entry) in ResolvedLines(mod, written.Contains))
        {
            foreach (var placeholder in MissingPlaceholders(entry))
            {
                var line = entry.Line > 0 ? $", line {entry.Line}" : "";
                yield return Diagnostic.Error(file, $"[{section.Name}]{line}: {MissingTextMessage(placeholder)}");
            }
        }
    }

    /// <summary>The text placeholders of the values of a line that the text of the session does not have.</summary>
    private IEnumerable<StringPlaceholder> MissingPlaceholders(Entry entry) =>
        Placeholders(entry).Where(p => text.Get(p.Kind, p.Key) == null);

    /// <summary>
    /// The lines of a patch that hold the id of a text of a mod as a number (rule S8). The merge gives the id from the
    /// position of the text in the generated text DLL, so the line shows other text when a mod adds text.
    /// </summary>
    private IEnumerable<Diagnostic> FrozenTextIds(string path, PatchText patch)
    {
        foreach (var written in patch.Sections)
        {
            var bytes = IniDocument.TextEncoding.GetBytes(string.Join("\n", written.Lines));
            foreach (var section in IniDocument.Parse(path, bytes).Sections)
            {
                foreach (var entry in section.Where(e => SchemaCatalog.TextKind(section.Name, e.Name) != null))
                {
                    if (entry.Count > 0 && entry[0] is Int32Value && entry[0].TryToInt32(out var id) && IsModTextId(id))
                    {
                        var message = $"{written.Id}: '{entry.Name} = {id}' holds the id of a text of a mod, which " +
                                      "changes when a mod adds text. Copy this text into the mod.";
                        yield return Diagnostic.Error(path, message) with { Target = new SectionRef(path, written.Id) };
                    }
                }
            }
        }
    }

    private string MissingTextMessage(StringPlaceholder placeholder) =>
        $"'{placeholder}' is not in the strings.json of mod '{ActiveMod!.Id}'. Add the text, or change the value.";

    /// <summary>
    /// The path of a new patch file, relative to the patches folder: the install path with the letter case of the
    /// file in the last lower mod that has it, or else in the base folder (rule P8).
    /// </summary>
    private string LocalPath(string installPath)
    {
        foreach (var mod in lower.Reverse())
        {
            if (mod.Patches.TryGetValue(installPath, out var patch))
                return Path.GetRelativePath(Path.Combine(mod.Folder, Mod.PatchesFolder), patch);
            if (mod.Files.TryGetValue(installPath, out var file))
                return Path.GetRelativePath(Path.Combine(mod.Folder, Mod.FilesFolder), file);
        }
        return PathRules.WithDiskCase(Profile.BaseFolder, installPath).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static SaveResult NotSaved(List<Diagnostic> problems, List<string>? changedOnDisk = null) =>
        new(false, [], [], problems, changedOnDisk ?? []);

    private static bool IsError(Diagnostic d) => d.Severity == DiagnosticSeverity.Error;

    /// <summary>One file of a save.</summary>
    /// <param name="KnownHash">The hash of the file when the session read it, or null when it did not exist.</param>
    /// <param name="Source">The INI document, the cutscene or the editor file, or null for strings.json.</param>
    private sealed record PlannedFile(FileChange Change, string? KnownHash, IEditFile? Source)
    {
        /// <summary>True when the file on disk already has the new content, so it needs no write.</summary>
        public bool SameAsDisk { get; } = Change.Content == null
            ? !File.Exists(Change.Path)
            : File.Exists(Change.Path) && File.ReadAllBytes(Change.Path).AsSpan().SequenceEqual(Change.Content);
    }
}
