using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The cutscene part of the result of an agent call, and of each step of <c>edit_batch</c> (section 5.6 of the MCP1
/// design, decision D767). It gives the <c>thn:</c> references of the scripts and of the elements that the cutscene
/// plans of the call add, change and delete. It also gives the cutscene entities that another plan changes, for
/// example the templates that follow the rename of a ship (see <see cref="EditPlan.CutsceneChanges"/>).
/// </summary>
/// <remarks>
/// <para>An event reference holds the index of the event, and each add, delete and reorder can change the index. So the
/// references come from the net change of the call, not from the parts of the batch:</para>
/// <list type="bullet">
/// <item>An add and a change give the reference in W after the call. A delete gives the reference in W before the
/// call, because the element is not in W after it.</item>
/// <item>An element that the call adds and then deletes is not in the result. An element that the call adds is not a
/// change. An element of a new script of the call is a change of nothing, so only its add counts.</item>
/// <item>A plan changes the elements that it names, and each other element whose table its run changes. An example is
/// a rename of an entity, which also changes the events that name the entity.</item>
/// <item>A change counts only when the element differs from W before the call. An entity with another name than
/// before the call is a rename, from its reference before the call to its reference after the call.</item>
/// <item>The select is the last add that W after the call still has, as the editor selects a new item (decision
/// D249).</item>
/// </list>
/// <para>A step gives its references as they are after the step, and counts from W before the step.</para>
/// </remarks>
internal sealed class CutsceneResults(EditSession session)
{
    /// <summary>The run of one cutscene plan.</summary>
    /// <param name="Part">The index of the part of the plan in the parts of the batch.</param>
    /// <param name="NamesScript">True for a plan that names no element, for example a new script or a new
    /// duration. It changes the script as a whole.</param>
    private sealed record Run(string Path, int Part, IReadOnlyList<ThnRef> Added, IReadOnlyList<ThnRef> Changed,
        IReadOnlyList<ThnRef> Deleted, bool NamesScript);

    /// <summary>The references of a net change, and the part of the batch that gave the select.</summary>
    private sealed record Refs(IReadOnlyList<string> Adds, IReadOnlyList<ResultRename> Renames,
        IReadOnlyList<string> Changes, IReadOnlyList<ResultDelete> Deletes, string? Select, int SelectPart)
    {
        public bool IsEmpty => Adds.Count == 0 && Renames.Count == 0 && Changes.Count == 0 && Deletes.Count == 0;
    }

    private readonly List<Run> runs = [];

    /// <summary>W of each script before the call, or null for a script that did not exist then.</summary>
    private readonly Dictionary<string, ThnScriptModel?> callScripts = new(VfsPath.Comparer);

    /// <summary>W of each script before the step, as for <see cref="callScripts"/>.</summary>
    private readonly Dictionary<string, ThnScriptModel?> stepScripts = new(VfsPath.Comparer);

    private int stepStart;

    /// <summary>W of the script of the plan that runs now, before its run.</summary>
    private ThnScriptModel? planBefore;

    /// <summary>The references of the call, made after its last command, while the batch is still open.</summary>
    private Refs? call;

    /// <summary>Starts a step: the references of a step count from the scripts as they are now.</summary>
    public void StartStep()
    {
        stepStart = runs.Count;
        stepScripts.Clear();
    }

    /// <summary>W of a script before the step, or null for a script that the step did not touch or that did not exist
    /// before the step.</summary>
    public ThnScriptModel? StepScript(string path) => stepScripts.GetValueOrDefault(path);

    /// <summary>Notes W of the script of a plan before the plan runs, at the first touch of the script in the call
    /// and in the step. W cannot change, so a reference keeps it.</summary>
    public void Before(CutscenePlan plan) => planBefore = Note(plan.Path);

    /// <summary>Notes W of each script of the cutscene entities that a plan that is not a cutscene plan changes (see
    /// <see cref="EditPlan.CutsceneChanges"/>), before the plan runs, as <see cref="Before(CutscenePlan)"/>
    /// does.</summary>
    public void Before(EditPlan plan)
    {
        foreach (var path in plan.CutsceneChanges.Select(c => c.Path).Distinct(VfsPath.Comparer))
            Note(path);
    }

    /// <summary>Notes W of a script at the first touch in the call and in the step, and gives W now.</summary>
    private ThnScriptModel? Note(string path)
    {
        var now = session.Cutscene(path);
        callScripts.TryAdd(path, now);
        stepScripts.TryAdd(path, now);
        return now;
    }

    /// <summary>Notes the elements of a plan right after it ran. The new elements exist only now (see
    /// <see cref="CutscenePlan.Added"/>).</summary>
    /// <param name="part">The index of the part of the plan in the parts of the batch.</param>
    public void After(CutscenePlan plan, int part)
    {
        var added = plan.Added(session);
        var namesScript = added.Count == 0 && plan.Changed.Count == 0 && plan.Deleted.Count == 0;
        var changed = namesScript
            ? plan.Changed
            : plan.Changed.Concat(Differ(plan.Path, planBefore, session.Cutscene(plan.Path))).Distinct().ToList();
        runs.Add(new Run(plan.Path, part, added, changed, plan.Deleted, namesScript));
    }

    /// <summary>Notes the cutscene entities that a plan that is not a cutscene plan changed, right after it ran. An
    /// example is the template of a COMPOUND that follows the rename of a ship. The result gives each such entity in
    /// its changes, as for a cutscene plan.</summary>
    /// <param name="part">The index of the part of the plan in the parts of the batch.</param>
    public void After(EditPlan plan, int part)
    {
        foreach (var script in plan.CutsceneChanges.GroupBy(c => c.Path, VfsPath.Comparer))
            runs.Add(new Run(script.Key, part, [], script.Distinct().ToList(), [], false));
    }

    /// <summary>The elements of a script whose table differs between two versions of W. An element that one version
    /// does not have is an add or a delete, not a change.</summary>
    private static IEnumerable<ThnRef> Differ(string path, ThnScriptModel? was, ThnScriptModel? now)
    {
        if (was == null || now == null || ReferenceEquals(was, now))
            yield break;
        foreach (var kind in new[] { ThnElementKind.Entity, ThnElementKind.Event })
        {
            var earlier = was.Elements(kind).ToDictionary(e => e.Id, e => e.Table);
            foreach (var element in now.Elements(kind))
            {
                if (earlier.TryGetValue(element.Id, out var table) && !ReferenceEquals(table, element.Table) &&
                    !Equals(table, element.Table))
                    yield return new ThnRef(path, kind, element.Id);
            }
        }
    }

    /// <summary>Makes the references of the call. Call it after the last command and before the preview or the commit,
    /// because a preview puts back W before the call.</summary>
    public void EndCall() => call = Net(runs, callScripts);

    /// <summary>The result of the step with its cutscene references.</summary>
    /// <param name="parts">The parts of the batch.</param>
    /// <param name="partStart">The index of the first part of the step.</param>
    public EditResult Step(EditResult result, IReadOnlyList<EditResult> parts, int partStart) =>
        With(result, Net(runs.Skip(stepStart).ToList(), stepScripts), parts, partStart);

    /// <summary>The result of the call with its cutscene references, after the net result of the other parts.</summary>
    public EditResult Call(EditResult result, IReadOnlyList<EditResult> parts) =>
        With(result, call ?? Net(runs, callScripts), parts, 0);

    /// <summary>A result with the cutscene references after its own references. The cutscene select replaces the
    /// select of the other parts when it comes from a later part. A result without cutscene references stays as it
    /// is.</summary>
    private static EditResult With(EditResult result, Refs refs, IReadOnlyList<EditResult> parts, int partStart)
    {
        if (refs.IsEmpty)
            return result;
        var otherSelect = -1;
        for (var i = parts.Count - 1; i >= partStart; i--)
        {
            if (parts[i].Select != null)
            {
                otherSelect = i;
                break;
            }
        }
        return result with
        {
            Adds = [.. result.Adds, .. refs.Adds],
            Renames = [.. result.Renames, .. refs.Renames],
            Changes = [.. result.Changes, .. refs.Changes],
            Deletes = [.. result.Deletes, .. refs.Deletes],
            Select = refs.Select != null && refs.SelectPart > otherSelect ? refs.Select : result.Select,
        };
    }

    /// <summary>The net references of runs, with W of each script before the runs (see the remarks of the
    /// class).</summary>
    private Refs Net(IReadOnlyList<Run> list, IReadOnlyDictionary<string, ThnScriptModel?> before)
    {
        var added = new List<ThnRef>();
        var changed = new List<ThnRef>();
        var deleted = new List<(ThnRef Element, ThnRef? Cause)>();
        var newScripts = new List<string>();
        var changedScripts = new List<string>();
        object? select = null;
        var selectPart = -1;
        foreach (var run in list)
        {
            // A delete of an entity also deletes the events that name it, so these events go with the entity.
            var entity = run.Deleted.FirstOrDefault(d => d.Kind == ThnElementKind.Entity);
            foreach (var element in run.Deleted)
            {
                if (Equals(select, element))
                    select = null;
                changed.Remove(element);
                if (added.Remove(element) || deleted.Any(d => d.Element == element))
                    continue;
                deleted.Add((element, element.Kind == ThnElementKind.Event ? entity : null));
            }
            foreach (var element in run.Added)
            {
                if (!added.Contains(element))
                    added.Add(element);
                (select, selectPart) = (element, run.Part);
            }
            foreach (var element in run.Changed.Where(c => !added.Contains(c) && !changed.Contains(c)))
                changed.Add(element);
            if (!run.NamesScript)
                continue;
            if (before.GetValueOrDefault(run.Path) == null)
            {
                if (!newScripts.Contains(run.Path, VfsPath.Comparer))
                    newScripts.Add(run.Path);
                (select, selectPart) = (run.Path, run.Part);
            }
            else if (!changedScripts.Contains(run.Path, VfsPath.Comparer))
            {
                changedScripts.Add(run.Path);
            }
        }

        bool IsNew(string path) => newScripts.Contains(path, VfsPath.Comparer);
        ThnElement? After(ThnRef element) => session.Cutscene(element.Path)?.Element(element.Kind, element.Id);
        ThnElement? Earlier(ThnRef element) => before.GetValueOrDefault(element.Path)?.Element(element.Kind, element.Id);
        string? Selected() => select switch
        {
            string path when session.Cutscene(path) != null => CutsceneQueries.Ref(path),
            ThnRef element when After(element) != null => ItemRefs.Of(session, element),
            _ => null,
        };

        var adds = newScripts.Where(p => session.Cutscene(p) != null).Select(CutsceneQueries.Ref)
            .Concat(added.Where(a => After(a) != null).Select(a => ItemRefs.Of(session, a)))
            .Distinct().ToList();
        var netChanged = changed.Where(c => !IsNew(c.Path) && After(c) is { } now && Earlier(c) is { } was &&
                                            !Equals(now.Table, was.Table)).ToList();
        bool Renamed(ThnRef c) => c.Kind == ThnElementKind.Entity &&
                                  ThnScriptModel.NameOf(Earlier(c)!.Table) != ThnScriptModel.NameOf(After(c)!.Table);
        var renames = netChanged.Where(Renamed)
            .Select(c => new ResultRename(ItemRefs.Of(before.GetValueOrDefault(c.Path), c), ItemRefs.Of(session, c)))
            .ToList();
        var changes = changedScripts
            .Where(p => session.Cutscene(p) is { } now && !ReferenceEquals(now, before.GetValueOrDefault(p)))
            .Select(CutsceneQueries.Ref)
            .Concat(netChanged.Where(c => !Renamed(c)).Select(c => ItemRefs.Of(session, c)))
            .Distinct().ToList();
        // An event that goes with its entity gives the targets line that names the entity as its cause.
        string? CauseOf(ThnRef? entity) =>
            entity != null && Earlier(entity) is { } was && ThnScriptModel.NameOf(was.Table) is { } name
                ? $"targets = {name}"
                : null;
        var deletes = deleted.Where(d => Earlier(d.Element) != null)
            .Select(d => (d.Element, Cause: CauseOf(d.Cause)))
            .Select(d => new ResultDelete(ItemRefs.Of(before.GetValueOrDefault(d.Element.Path), d.Element),
                d.Cause == null ? DeleteReason.Target : DeleteReason.Cascade, d.Cause))
            .ToList();
        return new Refs(adds, renames, changes, deletes, Selected(), selectPart);
    }
}
