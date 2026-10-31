using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

public sealed partial class MissionEditor
{
    /// <summary>The commit check of the open batch, or null.</summary>
    private (EditBatch Batch, BatchCheck Check)? current;

    /// <summary>The revision at the last change of the history that the editor saw.</summary>
    private long seenRevision;

    /// <summary>The result of each request, after its commit (section 4.4 of the M6b design).</summary>
    public event Action<MissionEditResult>? Applied;

    /// <summary>Each change of a mission script by the history: a request, an undo, a redo or a restore. It comes from
    /// <see cref="EditSession.Changed"/>.</summary>
    public event Action<MissionScriptsChange>? ScriptsChanged;

    /// <summary>Runs one command in the open batch of the caller, or in its own batch (section 4.0.4 of the M6b
    /// design).</summary>
    public MissionEditResult Apply(MissionCommand command, MissionEditOptions? options = null) =>
        Apply(new MissionBatch(command.HistoryLabel, [command]) { Options = options ?? MissionEditOptions.Default });

    /// <summary>
    /// Runs a batch in the open batch of the caller, or in its own batch (section 4.5 of the M6b design). A refusal of
    /// any command cancels the whole batch: nothing changes, and no history entry exists. In the batch of a caller, the
    /// caller commits, and the commit check of the layer runs then.
    /// </summary>
    public MissionEditResult Apply(MissionBatch batch) => Run(batch, preview: false);

    /// <summary>A preview: runs a batch, reports the result, and puts back the state before. No history entry. The
    /// revision and the script versions stay.</summary>
    public MissionEditResult Preview(MissionBatch batch) => Run(batch, preview: true);

    private MissionEditResult Run(MissionBatch batch, bool preview)
    {
        var open = session.OpenBatch;
        // A refusal in any step cancels the whole batch of the caller (section 4.0.4 of the M6b design, item 8).
        MissionEditResult Refuse(IReadOnlyList<EditError> errors)
        {
            open?.Cancel();
            return Refused(batch, errors);
        }

        if ((StateRefusal() ?? Refusal(batch)) is { } refusal)
            return Refuse([refusal]);
        if (open != null && preview)
            return Refuse([MissionErrors.BusyError() with
            {
                Message = "A batch is open, so the editor cannot show a preview now. Complete the batch, then try again.",
            }]);
        EditBatch editBatch;
        try
        {
            editBatch = open ?? session.BeginBatch(batch.EntryLabel, batch.Origin);
        }
        catch (ArgumentException e)
        {
            return Refused(batch, [new EditError(EditErrorKind.InvalidArgument, EditPlan.PlanRefused, e.Message)]);
        }
        catch (InvalidOperationException e)
        {
            return Refused(batch, [MissionErrors.NotEditableError(e.Message)]);
        }
        var check = CheckOf(editBatch);
        check.Join(batch.Options);
        var plan = new MissionPlanCommand(batch.EntryLabel, this, batch.Commands);
        try
        {
            editBatch.Run(plan);
        }
        catch (EditException e)
        {
            return Refused(batch, [e.Error]);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return Refused(batch, [new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, e.Message)
            {
                Index = plan.FailedIndex >= 0 ? plan.FailedIndex : null,
            }]);
        }
        var draft = plan.Draft!;
        check.Add(draft, draft.ModTextBefore);
        var confirmations = Confirmations(draft, editBatch.Origin);
        if (confirmations.Count > 0 && !batch.Confirm && !preview)
        {
            editBatch.Cancel();
            return MissionEditResult.From(Part(draft)) with
            {
                Status = EditStatus.Refused,
                Label = batch.EntryLabel,
                Errors = confirmations,
                NeedsConfirmation = true,
                Revision = session.Revision,
                Summary = confirmations[0].Message,
            };
        }
        var inverse = InverseOf(draft);
        editBatch.Report(Part(draft));
        if (open != null)
        {
            // The caller commits. The layer gives its event after that commit.
            void Committed(EditResult result)
            {
                editBatch.Committed -= Committed;
                if (result.Status == EditStatus.Applied)
                    Applied?.Invoke(Final(result, draft, inverse));
            }
            editBatch.Committed += Committed;
            // With Confirm, the caller asks for the confirmations of the plan, so the result gives them as data.
            return MissionEditResult.From(Part(draft)) with
            {
                Label = batch.EntryLabel,
                Inverse = inverse,
                RowMoves = draft.RowMoves,
                Rows = draft.NetRows(),
                Revision = session.Revision,
                Asked = confirmations,
            };
        }
        var final = Final(preview ? editBatch.Preview() : editBatch.Commit(), draft, inverse);
        // A preview reports the result of the request (section 4.0.1 of the M6b design). So a preview of a batch that
        // needs a confirmation tells it, and why, and does not refuse.
        if (preview && confirmations.Count > 0 && !batch.Confirm && final.Status == EditStatus.Previewed)
            final = final with
            {
                NeedsConfirmation = true,
                Warnings = [.. final.Warnings, .. confirmations.Select(c => new Problem(DiagnosticSeverity.Warning, c.Message)
                {
                    Ref = c.Target,
                })],
            };
        if (final.Status == EditStatus.Applied)
            Applied?.Invoke(final);
        return final;
    }

    /// <summary>The confirmations that a batch needs (section 4.0.5 of the M6b design): those of its plan. An agent
    /// also confirms a delete or a rename that leaves "Missing" lines (decision D712 of the MCP1 design). The origin is
    /// the origin of the history entry, so an agent step in the batch of a caller asks too.</summary>
    private static List<EditError> Confirmations(MissionDraft draft, EditOrigin origin)
    {
        var result = draft.Confirmations.DistinctBy(c => (c.Detail, c.Message)).ToList();
        if (origin == EditOrigin.Agent && draft.Missing.Count > 0)
            result.Add(MissionErrors.MissingLinesError(draft.Missing.Count));
        return result;
    }

    /// <summary>The refusals of the state of the session before a batch runs: the session takes no edits, or a
    /// transaction is open (section 4.5 of the M6b design).</summary>
    private EditError? StateRefusal() =>
        session.ReadOnlyReason is { } reason ? MissionErrors.NotEditableError(reason)
        : session.InTransaction ? MissionErrors.BusyError()
        : null;

    /// <summary>The refusals of the guards of a batch: the expected revision or an expected script version differs
    /// (section 4.5 of the M6b design). A key of the versions that is not an install path refuses with
    /// <c>invalid_argument</c>, and a script that the edit view does not have with <c>not_found</c> (decision D763 of the
    /// MCP1 design).</summary>
    internal EditError? Refusal(MissionBatch batch)
    {
        if (session.RevisionGuard(batch.IfRevision) is { } stale)
            return stale;
        foreach (var (script, expected) in batch.ExpectVersions)
        {
            if (InstallPaths.Problem(script) is { } problem)
                return new EditError(EditErrorKind.InvalidArgument, InstallPaths.NotInstallPath, problem)
                {
                    Field = "expect_versions",
                };
            var path = InstallPaths.Parse(script);
            if (session.OpenDocument(path) is not { HoldsScript: true } document)
                return MissionErrors.ScriptNotFoundError(path) with { Field = "expect_versions" };
            if (Version(document.Path) != expected)
                return MissionErrors.ScriptChangedError(document.Path) with { Field = "expect_versions" };
        }
        return null;
    }

    private MissionEditResult Refused(MissionBatch batch, IReadOnlyList<EditError> errors) =>
        new(EditStatus.Refused)
        {
            Label = batch.EntryLabel,
            Errors = errors,
            Revision = session.Revision,
            Summary = errors.Count > 0 ? errors[0].Message : null,
        };

    /// <summary>The mission result of a commit or a preview, with the inverse and the versions of the touched
    /// scripts.</summary>
    private MissionEditResult Final(EditResult result, MissionDraft draft, MissionCommand? inverse)
    {
        var scripts = draft.Touched.Values.Where(IsScript).Select(d => d.Path);
        return MissionEditResult.From(result) with
        {
            Inverse = result.Status is EditStatus.Applied or EditStatus.Previewed ? inverse : null,
            RowMoves = result.Status is EditStatus.Applied or EditStatus.Previewed ? draft.RowMoves : [],
            Versions = scripts.ToDictionary(p => p, Version, VfsPath.Comparer),
            Summary = result.Status switch
            {
                EditStatus.Applied => $"The editor applied '{result.Label}'.",
                EditStatus.Previewed => $"The preview of '{result.Label}' changed nothing.",
                EditStatus.NoChange => $"'{result.Label}' changed nothing.",
                _ => result.Errors.Count > 0 ? result.Errors[0].Message : null,
            },
        };
    }

    /// <summary>The part of the result of the plan: the adds, changes, deletes, renames, "Missing" lines, notes and
    /// the selection.</summary>
    private static EditResult Part(MissionDraft draft) => new(EditStatus.Applied)
    {
        Adds = draft.Adds.Distinct().ToList(),
        Changes = draft.Changes.Distinct().Where(c => !draft.Adds.Contains(c)).ToList(),
        Deletes = draft.Deletes,
        Renames = draft.Renames,
        Missing = draft.Missing,
        Notes = draft.Notes.Distinct().ToList(),
        Warnings = draft.Warnings,
        Select = draft.Select,
    };

    /// <summary>
    /// The inverse of a batch (section 4.8 of the M6b design): a group of the inverses in reverse order, with a body
    /// guard for each section that the batch changed or added. The guard is the engine-form hash of the section in W
    /// after the batch (decision D666). The inverse holds no script version, so an edit of another section does not
    /// stop it.
    /// </summary>
    internal MissionCommand? InverseOf(MissionDraft draft)
    {
        if (draft.Inverses.Count == 0)
            return null;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, before) in draft.Before)
        {
            var document = draft.Touched[path];
            if (!document.State.Exists)
                continue;
            var old = new HashSet<LibreLancer.Data.Ini.Section>(before.Working, ReferenceEqualityComparer.Instance);
            for (var i = 0; i < document.Working.Count; i++)
            {
                if (!old.Contains(document.Working[i]))
                    bodies[ItemRefs.Canonical(session, new SectionRef(path, document.IdentityAt(i)))] =
                        SectionHash.Of(document.Working[i]);
            }
        }
        // A group of one command without body guards runs as its commands, so the inverse holds them directly.
        var commands = draft.Inverses.AsEnumerable().Reverse()
            .SelectMany(c => c is CommandGroup { ExpectBodies.Count: 0 } group ? group.Commands : [c]).ToList();
        return new CommandGroup(commands) { ExpectBodies = bodies };
    }

    private BatchCheck CheckOf(EditBatch batch)
    {
        if (current is var (known, check) && ReferenceEquals(known, batch) && batch.IsOpen)
            return check;
        var made = new BatchCheck(this, WorldNow());
        batch.AddCommitCheck(made);
        current = (batch, made);
        return made;
    }

    /// <summary>True for a document that is a mission script now, or that was one in this session. A new script has
    /// no sections after an undo of its create or after its delete, but its version still changes.</summary>
    private bool IsScript(EditDocument document) => document.HoldsScript || versions.ContainsKey(document.Path);

    /// <summary>The path of a file as the session stores it, when the file is a mission script now or was one in this
    /// session, else null. A result gives the versions of such files (section 4.2 of the M6b design).</summary>
    /// <param name="path">An install path in normal form, for example a path of <see cref="EditResult.Files"/>.</param>
    internal string? ScriptPathOf(string path) =>
        session.OpenedDocument(path) is { } document && IsScript(document) ? document.Path : null;

    /// <summary>Gives the new versions and the trigger renames of each change of the history (section 4.2 of the M6b
    /// design). A step of a transaction, a preview and a cancel do not change the revision, so they change no
    /// version.</summary>
    private void OnHistory(EditChange change)
    {
        if (session.Revision == seenRevision)
            return;
        seenRevision = session.Revision;
        var changed = new Dictionary<string, long>(VfsPath.Comparer);
        foreach (var path in change.Files)
        {
            if (session.OpenedDocument(path) is { } document && IsScript(document))
            {
                versions[document.Path] = Version(document.Path) + 1;
                changed[document.Path] = versions[document.Path];
            }
        }
        if (changed.Count == 0)
            return;
        var renames = change.Renames
            .Where(r => MissionIndex.Is(r.From.Id.Type, "Trigger") && r.From.Id.Key != null && r.To.Id.Key != null)
            .Select(r => (new TriggerRef(r.From.Path, r.From.Id.Key!), new TriggerRef(r.To.Path, r.To.Id.Key!)))
            .ToList();
        ScriptsChanged?.Invoke(new MissionScriptsChange(changed, renames));
    }

    /// <summary>The names of the game data now, for the problems before a batch or a transaction.</summary>
    internal MissionInputs WorldNow() => world.Inputs();

    /// <summary>
    /// The commit check of the mission part of a batch (section 4.5 of the M6b design): it builds the model of each
    /// touched script once, compares the problems with the problems before the batch by their subject with counts, and
    /// refuses a batch that adds an error in strict mode. A "Missing" line of a delete or a rename does not count.
    /// </summary>
    /// <param name="before">The names of the game data before the batch. The problems before the batch use them, so a
    /// step that changes a file of the world does not make the editor analyze the states before again.</param>
    internal sealed class BatchCheck(MissionEditor editor, MissionInputs before) : IBatchCheck
    {
        private readonly Dictionary<string, (EditDocument Document, DocumentState Before, IReadOnlyList<Diagnostic> Old)>
            scripts = new(VfsPath.Comparer);

        private readonly HashSet<string> gone = new(StringComparer.OrdinalIgnoreCase);
        private MissionEditOptions options = MissionEditOptions.Default;

        /// <summary>The state before the batch of each mission file that is not a script, for example the ship build
        /// file of a script. The problems of a script before the batch read these states (final finding 14 of
        /// MCP1-3).</summary>
        private readonly Dictionary<string, DocumentState> filesBefore = new(VfsPath.Comparer);

        public void Join(MissionEditOptions joined) => options = new MissionEditOptions(
            options.RejectNewErrors || joined.RejectNewErrors, options.RejectNewWarnings || joined.RejectNewWarnings);

        /// <param name="modText">Gives the text of each editor file of the active mod before the batch, so the problems
        /// before the batch have the prefab records and the prefabs before it.</param>
        public void Add(MissionDraft draft, Func<string, string?> modText)
        {
            var files = new List<string>();
            foreach (var (path, state) in draft.Before)
            {
                if (!editor.IsScript(draft.Touched[path]) && SectionKeys.IsMissionFile(path) && filesBefore.TryAdd(path, state))
                    files.Add(path);
            }
            // The problems of a script before the batch read its ship build file before the batch.
            var others = filesBefore.Count > 0 ? filesBefore : null;
            foreach (var (path, state) in draft.Before)
            {
                var document = draft.Touched[path];
                if (!editor.IsScript(document) || scripts.ContainsKey(path))
                    continue;
                var old = state.Exists ? editor.ModelOf(document, state, before, modText, others).Problems : [];
                scripts[path] = (document, state, old);
            }
            // A change of the ship build file of a script changes the problems of the script.
            foreach (var path in editor.ScriptPaths())
            {
                if (scripts.ContainsKey(path) || editor.session.ShipBuildFileOf(path) is not { } shipFile ||
                    !files.Contains(shipFile, VfsPath.Comparer) ||
                    editor.session.OpenDocument(path) is not { State.Exists: true } document || !editor.IsScript(document))
                    continue;
                scripts[document.Path] = (document, document.State,
                    editor.ModelOf(document, document.State, before, modText, others).Problems);
            }
            // A change of a graph file or of a prefab asset changes the prefab instances of a script without a change of
            // its lines, for example an unpack or a delete of a prefab (rules P01 to P03).
            foreach (var file in draft.ModBefore.Keys)
            {
                IEnumerable<string> touched = file.StartsWith(@"graphs\", StringComparison.OrdinalIgnoreCase)
                    ? [file[@"graphs\".Length..]]
                    : file.StartsWith(PrefabAsset.Folder, StringComparison.OrdinalIgnoreCase)
                        ? editor.InstancesOf(PrefabRef.ModOf(System.IO.Path.GetFileNameWithoutExtension(file))).Select(i => i.Script)
                        : [];
                foreach (var path in touched)
                {
                    if (editor.session.OpenDocument(path) is not { } document || !editor.IsScript(document) ||
                        scripts.ContainsKey(document.Path) || !document.State.Exists)
                        continue;
                    scripts[document.Path] = (document, document.State,
                        editor.ModelOf(document, document.State, before, modText).Problems);
                }
            }
            gone.UnionWith(draft.GoneNames);
        }

        public EditResult Check(EditBatch batch) => Check();

        /// <summary>The new and the fixed problems of the touched scripts, and the errors of strict mode.</summary>
        public EditResult Check()
        {
            var added = new List<Problem>();
            var fixedProblems = new List<Problem>();
            var refusing = new List<Problem>();
            var exempted = new Dictionary<(string, SectionIdentity, string), int>();
            foreach (var (path, (document, _, old)) in scripts)
            {
                var now = document.State.Exists ? editor.ModelOf(document, document.State).Problems : [];
                var (plus, minus) = Compare(old, now);
                added.AddRange(plus.Select(p => ProblemOf(path, p)));
                fixedProblems.AddRange(minus.Select(p => ProblemOf(path, p)));
                refusing.AddRange(plus.Where(p => Counts(path, p, exempted)).Select(p => ProblemOf(path, p)));
            }
            var errors = refusing.Count > 0
                ? [MissionErrors.NewErrorsError(refusing.Count(p => p.Severity == DiagnosticSeverity.Error),
                    refusing.Count(p => p.Severity == DiagnosticSeverity.Warning), refusing[0].Message)]
                : new List<EditError>();
            return new EditResult(EditStatus.Applied)
            {
                NewProblems = added,
                FixedProblems = fixedProblems,
                Errors = errors,
            };
        }

        /// <summary>True when a new problem refuses the batch in strict mode.</summary>
        /// <param name="exempted">The number of "Missing" lines that the check let through, by script, section and
        /// line.</param>
        private bool Counts(string path, Diagnostic problem, Dictionary<(string, SectionIdentity, string), int> exempted)
        {
            var refuses = problem.Severity == DiagnosticSeverity.Error && options.RejectNewErrors ||
                          problem.Severity == DiagnosticSeverity.Warning && options.RejectNewWarnings;
            if (!refuses)
                return false;
            // A "Missing" line of a delete or a rename is a line that was there before the batch and that names a name
            // that the batch took away (decision D81). A new line that names such a name counts.
            return !(problem.Rule is "M05" or "M06" or "M21" or "M22" or "M23" && problem.Subject is { } subject &&
                     gone.Any(n => subject.EndsWith("|" + n, StringComparison.OrdinalIgnoreCase)) &&
                     WasThere(path, problem, exempted));
        }

        /// <summary>True when the line of a problem was in its section before the batch. The section had more lines with
        /// the same key and values than the check let through until now.</summary>
        private bool WasThere(string path, Diagnostic problem, Dictionary<(string, SectionIdentity, string), int> exempted)
        {
            var (document, state, _) = scripts[path];
            if (problem.Target is not { } target || problem.EntryIndex < 0 ||
                document.IndexOf(target.Id) is not (>= 0 and var at) || problem.EntryIndex >= document.Working[at].Count)
                return false;
            var line = LineDiff.Text(document.Working[at][problem.EntryIndex]);
            var old = Array.IndexOf(SectionIdentity.Identify(state.Working), target.Id);
            var before = old < 0 ? 0 : state.Working[old].Count(e => LineDiff.Text(e) == line);
            var key = (path, target.Id, line);
            var used = exempted.GetValueOrDefault(key);
            if (used >= before)
                return false;
            exempted[key] = used + 1;
            return true;
        }

        /// <summary>The problems that are new and the problems that went, by their subject with counts, as
        /// <see cref="MissionOrigins"/> compares them (review finding RB26).</summary>
        private static (List<Diagnostic> Added, List<Diagnostic> Fixed) Compare(IReadOnlyList<Diagnostic> before,
            IReadOnlyList<Diagnostic> after) => (Minus(after, before), Minus(before, after));

        /// <summary>The problems of a list that the other list does not have, by subject with counts.</summary>
        private static List<Diagnostic> Minus(IReadOnlyList<Diagnostic> list, IReadOnlyList<Diagnostic> other)
        {
            static string Key(Diagnostic d) => d.Subject ?? d.Message;
            var counts = other.CountBy(Key).ToDictionary(c => c.Key, c => c.Value);
            var result = new List<Diagnostic>();
            foreach (var problem in list)
            {
                var key = Key(problem);
                if (counts.TryGetValue(key, out var n) && n > 0)
                    counts[key] = n - 1;
                else
                    result.Add(problem);
            }
            return result;
        }

        private Problem ProblemOf(string path, Diagnostic problem) => MissionQueries.ProblemOf(editor.session, path, problem);
    }
}
