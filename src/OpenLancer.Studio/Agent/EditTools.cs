using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The result of an edit tool that ran mission commands, and of a step of <c>edit_batch</c>: the result of the command
/// layer with the inverse and the row moves of the mission commands (section 4.4 of the M6b design, section 7.4 of the
/// MCP1 design). Without mission commands, the inverse and the row moves are null, so the JSON leaves them out.
/// </summary>
internal sealed record EditToolResult : EditResult
{
    public EditToolResult(EditResult result) : base(result)
    {
    }

    /// <summary>The mission command that reverts the mission commands of the call after later edits, or null. The
    /// agent gives it to <c>mission_edit</c>.</summary>
    public MissionCommand? Inverse { get; init; }

    /// <summary>The lines whose row index the mission commands changed, or null without mission commands.</summary>
    public IReadOnlyList<RowMove>? RowMoves { get; init; }
}

/// <summary>
/// The batch of one edit call of an agent, or of one <c>edit_batch</c> call with all its steps (section 8.3 of the MCP1
/// design). Each command of the call runs its plan in the batch. So all commands give one entry of the edit history,
/// with the edit origin Agent.
/// </summary>
internal sealed class EditScope
{
    private readonly EditChecks checks;

    /// <summary>The index in the parts of the batch of the first part of the step.</summary>
    private int stepStart;

    private readonly List<string> stepConfirmations = [];

    /// <summary>The inverse and the row map of each mission request of the call, in order.</summary>
    private readonly List<(MissionCommand? Inverse, RowMap? Rows)> missionRuns = [];

    /// <summary>The index in <see cref="missionRuns"/> of the first mission request of the step.</summary>
    private int stepMissionStart;

    internal EditScope(EditSession session, EditBatch batch, AgentLimits limits)
    {
        Session = session;
        Batch = batch;
        checks = new EditChecks(session, batch, limits.EditSections);
        batch.AddCommitCheck(checks);
    }

    public EditSession Session { get; }

    public EditBatch Batch { get; }

    /// <summary>Why the commands of the step until now need <c>confirm: true</c>, one text for each plan that
    /// asks.</summary>
    public IReadOnlyList<string> Confirmations => stepConfirmations;

    /// <summary>Starts a step: the parts and the confirmations of the next commands belong to it. A call that is not a
    /// step of <c>edit_batch</c> has one step.</summary>
    internal void StartStep()
    {
        stepStart = Batch.Parts.Count;
        stepMissionStart = missionRuns.Count;
        stepConfirmations.Clear();
    }

    /// <summary>The result of the commands of the step until now. A step with mission commands gives their inverse and
    /// their row moves. So each mission step of <c>edit_batch</c> keeps its own inverse.</summary>
    internal EditResult StepResult()
    {
        var step = Batch.Parts.Skip(stepStart).ToList();
        return WithMission(EditTools.Shaped(EditResult.Merge(EditStatus.Applied, step), step),
            missionRuns.Skip(stepMissionStart).ToList());
    }

    /// <summary>The net result of all commands of the call. Its adds, renames and changes come from the parts of the
    /// batch in their order (see <see cref="EditTools.Shaped"/>). A call with mission commands gives their inverse and
    /// their row moves when the call applied or is a preview.</summary>
    internal EditResult CallResult(EditResult batchResult) =>
        WithMission(EditTools.Shaped(batchResult, Batch.Parts),
            batchResult.Status is EditStatus.Applied or EditStatus.Previewed ? missionRuns : null);

    /// <summary>
    /// Runs a mission request in the batch of the call, on top of the commands before it (section 7.4 of the MCP1
    /// design). The request runs in strict mode (decision D681 of M6b), so the commit check of the mission editor
    /// refuses the batch when it adds an error. The batch gives the edit origin Agent and the label, so this call sets
    /// neither. The confirmations of the request join the confirmations of the step, as the confirmations of a plan do.
    /// </summary>
    /// <param name="field">The argument of the commands, for an error that names a command, for example
    /// <c>commands</c>. An error of a command names <c>commands[i]</c>.</param>
    /// <exception cref="EditException">The request refuses. The mission editor cancels the batch, so nothing
    /// changed.</exception>
    public MissionEditResult RunMission(MissionBatch batch, string field) =>
        RunMission(batch, error => error.Index is { } i ? $"{field}[{i}]" : field);

    /// <inheritdoc cref="RunMission(MissionBatch, string)"/>
    /// <param name="fieldOf">The argument of an error that names no argument, for example the argument of the
    /// command at the index of the error. A tool that makes its commands from its arguments uses it.</param>
    public MissionEditResult RunMission(MissionBatch batch, Func<EditError, string> fieldOf)
    {
        var result = Session.Missions.Apply(batch with
        {
            Origin = EditOrigin.Agent,
            Options = batch.Options with { RejectNewErrors = true },
            Confirm = true,
        });
        if (result.Status == EditStatus.Refused)
        {
            var error = result.Errors[0];
            throw new EditException(error with { Field = error.Field ?? fieldOf(error) });
        }
        missionRuns.Add((result.Inverse, result.Rows));
        stepConfirmations.AddRange(result.Asked.Select(a => a.Message).Distinct());
        return result;
    }

    /// <summary>A result with the inverse and the row moves of mission requests, or the result as it is without
    /// mission requests. The row moves are the net moves of all the requests: each line once, with its row before the
    /// first request and its row after the last request (final finding 1 of MCP1-3).</summary>
    /// <param name="runs">The mission requests, or null for a result that gives no inverse, for example
    /// <c>no_change</c>.</param>
    private static EditResult WithMission(EditResult result, IReadOnlyList<(MissionCommand? Inverse, RowMap? Rows)>? runs)
    {
        if (runs is not { Count: > 0 })
            return result;
        // Later requests revert first. A group of the inverses runs each inverse with its own body guards.
        var inverses = runs.Select(r => r.Inverse).OfType<MissionCommand>().Reverse().ToList();
        return new EditToolResult(result)
        {
            Inverse = inverses.Count switch
            {
                0 => null,
                1 => inverses[0],
                _ => new CommandGroup(inverses),
            },
            RowMoves = runs.Select(r => r.Rows).OfType<RowMap>().Aggregate(new RowMap(), (all, next) => all.Then(next)).Moves,
        };
    }

    /// <summary>
    /// Runs the session command of a plan in the batch, on top of the commands before it. Then it reports the part of
    /// the result that the plan gives. A plan that refuses throws its refusal, and the caller cancels the batch.
    /// </summary>
    /// <param name="targets">The sections that the request names, for the problems before and after the
    /// edit.</param>
    /// <param name="asks">False for a plan that the request needs and that the agent cannot confirm. An example is the
    /// rename of a new item to the nickname of its request. Its warnings stay in the result, but the call does not ask
    /// for <c>confirm: true</c>.</param>
    /// <exception cref="EditException">The plan refuses, or its session command does not apply. A command that does not
    /// apply ends the batch, and the states before the batch come back.</exception>
    public void Run(EditPlan plan, IEnumerable<SectionRef>? targets = null, bool asks = true)
    {
        if (!plan.CanRun)
            throw new EditException(EditTools.RefusalOf(Session, plan));
        checks.Before(plan, targets ?? []);
        Batch.Label ??= plan.Label;
        try
        {
            Batch.Run(plan.Command!);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, ErrorText.Of(e)));
        }
        checks.After(plan);
        var part = plan.Report(Session);
        Report(asks ? part : part with { NeedsConfirmation = false });
        if (asks && EditTools.ConfirmationOf(Session, plan) is { } reason)
            stepConfirmations.Add(reason);
    }


    /// <summary>Adds a part of the result, for example the warnings of a text that other sections share.</summary>
    public void Report(EditResult part) => Batch.Report(part);
}

/// <summary>
/// The shared edit call of the agent tools (section 12.4, items 2 and 3 of the MCP1 design). Each edit tool calls
/// <see cref="Apply"/> with the commands of the request. The call runs these checks in order. The tool table checks
/// Pause. Then come the reason why the host takes no edits (<c>read_only</c>) and the revision guard of
/// <c>if_revision</c>. Then the call opens a batch with the edit origin Agent, or joins the batch of
/// <c>edit_batch</c>. It runs the commands, and gives a preview or commits.
/// </summary>
/// <remarks>
/// <para>A call with no net change gives <c>no_change</c> and no history entry. Some plans give
/// <c>needs_confirmation</c> until the call has <c>confirm: true</c> (decision D712 of the MCP1 design). These are a
/// plan that the editor asks about, and a delete or a rename that leaves "Missing" lines (section 4.0.5 of the M6b
/// design).</para>
/// <para>The call cancels its batch at each exception, so no batch stays open. An open batch would block each edit,
/// undo and save of the user too (review finding R9 of MCP1-2).</para>
/// </remarks>
internal static class EditTools
{
    /// <summary>The detail code of a plan that needs <c>confirm: true</c>.</summary>
    public const string ConfirmChanges = "ConfirmChanges";

    /// <summary>The detail code of a reference of a mission item that a tool does not change. The mission tools and the
    /// generic edit tools change mission items.</summary>
    public const string MissionItem = "MissionItem";

    /// <summary>The detail code of a reference of a cutscene before the cutscene tools.</summary>
    public const string CutsceneEdits = "CutsceneEdits";

    /// <summary>The most references that the text of a confirmation names for each kind of change.</summary>
    private const int ShownRefs = 3;

    public static readonly ToolParam Preview = new FlagParam("preview");

    public static readonly ToolParam IfRevision = new TextParam("if_revision", false, null, null);

    public static readonly ToolParam WithPatch = new FlagParam("with_patch");

    public static readonly ToolParam Confirm = new FlagParam("confirm");

    /// <summary>The arguments of an edit tool: its own arguments, then the common arguments of section 7.0 of the MCP1
    /// design. These are <c>preview</c>, <c>if_revision</c> and <c>with_patch</c>, and <c>confirm</c> for a tool that
    /// can ask.</summary>
    public static ToolInput Input(bool confirm, params ToolParam[] own) =>
        new([.. own, Preview, IfRevision, WithPatch, .. confirm ? new[] { Confirm } : []]);

    /// <summary>
    /// Runs the commands of an edit call. A call of the agent gets its own batch, and a step of <c>edit_batch</c> joins
    /// the batch of the call. The result of a step is the part of its commands, and the batch commits after its last
    /// step.
    /// </summary>
    /// <param name="body">Runs the commands of the request in the scope.</param>
    /// <exception cref="EditException">A check or a command refuses, or the result is too large. Nothing
    /// changed.</exception>
    public static ToolResult Apply(ToolCall call, Action<EditScope> body)
    {
        var args = call.Args;
        if (call.Scope is { } batch)
        {
            // edit_batch checks Pause and the reason why the host takes no edits for all steps.
            foreach (var name in new[] { "preview", "with_patch" }.Where(args.Has))
                throw ToolInput.Bad(name, AgentTexts.Error("step_preview", ("name", name)));
            Guard(call);
            batch.StartStep();
            body(batch);
            if (batch.Confirmations.Count > 0 && !args.Flag("confirm"))
                throw new EditException(NeedsConfirmation(batch.Confirmations));
            return Output(batch.StepResult());
        }
        var preview = args.Flag("preview");
        var withPatch = args.Flag("with_patch");
        if (withPatch && !preview)
            throw ToolInput.Bad("with_patch", AgentTexts.Error("with_patch_preview"));
        ReadOnlyGuard(call);
        Guard(call);
        return Commit(call, null, preview, withPatch, args.Flag("confirm"), body, Output);
    }

    /// <summary>The result of an edit tool in the JSON form of its type, so the result of mission commands gives its
    /// inverse and its row moves (see <see cref="EditToolResult"/>).</summary>
    internal static ToolResult Output(EditResult result) => ToolResult.Of<object>(result);

    /// <summary>The versions of the mission scripts among the files of a result, by the path that the session stores
    /// (section 4.2 of the M6b design). So each call that changes a script gives its version, also a rename of an
    /// object that changes the mission lines that name it.</summary>
    internal static IReadOnlyDictionary<string, long> VersionsOf(EditSession session, IEnumerable<string> files)
    {
        var versions = new Dictionary<string, long>(OpenLancer.Data.IO.VfsPath.Comparer);
        foreach (var file in files)
        {
            if (session.Missions.ScriptPathOf(file) is { } script)
                versions[script] = session.Missions.Version(script);
        }
        return versions;
    }

    /// <summary>
    /// Opens a batch with the edit origin Agent, runs the commands, and gives a preview or commits. A batch that needs a
    /// confirmation without <paramref name="confirm"/> cancels and refuses. A preview gives
    /// <c>needs_confirmation: true</c> in its result instead, with the text of each confirmation as a warning.
    /// </summary>
    /// <remarks>The batch commits only when the result of the tool fits the size limit of a result (section 8.4 of the
    /// MCP1 design). The queue replaces a larger result with the error <c>limit</c>. That error must not hide an edit
    /// that applied, so a larger edit cancels with <c>limit</c> before its commit.</remarks>
    /// <param name="label">The label of the request, or null for the label of the first command.</param>
    /// <param name="output">Makes the result of the tool from the result of the batch, for example with the result of
    /// each step of <c>edit_batch</c>.</param>
    /// <exception cref="EditException">A check or a command refuses, a commit check refuses, or the result is too
    /// large. Nothing changed.</exception>
    internal static ToolResult Commit(ToolCall call, string? label, bool preview, bool withPatch, bool confirm,
        Action<EditScope> body, Func<EditResult, ToolResult> output)
    {
        var session = call.Session;
        EditBatch batch;
        try
        {
            batch = session.BeginBatch(label, EditOrigin.Agent);
        }
        catch (InvalidOperationException e)
        {
            throw new EditException(new EditError(EditErrorKind.Busy, MissionErrors.Busy, e.Message));
        }
        using (batch)
        {
            var scope = new EditScope(session, batch, call.Limits);
            scope.StartStep();
            body(scope);
            var confirmations = scope.Confirmations;
            if (confirmations.Count > 0 && !confirm && !preview)
                throw new EditException(NeedsConfirmation(confirmations));
            var patch = withPatch ? batch.PatchTexts() : null;
            EditResult Final(EditResult result)
            {
                var shaped = scope.CallResult(result);
                return shaped with
                {
                    // A call with no net change changed no item, also when its plans name items.
                    Changes = result.Status == EditStatus.NoChange ? [] : shaped.Changes,
                    NeedsConfirmation = result.NeedsConfirmation || confirmations.Count > 0,
                    // A preview tells why the edit needs confirm: true, as the preview of the mission studio does
                    // (section 4.0.1 of the M6b design). So the agent can ask the user before it calls again.
                    Warnings = preview && confirmations.Count > 0
                        ? [.. shaped.Warnings, .. confirmations.Select(c => new Problem(DiagnosticSeverity.Warning, c))]
                        : shaped.Warnings,
                    Patch = patch ?? result.Patch,
                    // After a commit, the versions of the scripts are the new versions. A preview gives the versions
                    // now, because it changes no version (decision D764).
                    Versions = VersionsOf(session, result.Files),
                    Summary = result.Status switch
                    {
                        EditStatus.Applied => AgentTexts.Error("edit_applied", ("label", result.UndoLabel ?? "")),
                        EditStatus.Previewed => AgentTexts.Error("edit_previewed"),
                        _ => AgentTexts.Error("edit_no_change"),
                    },
                };
            }

            ToolResult Fitted(EditResult result) => EditTools.Fitted(call, output(result));

            var finished = batch.Finish();
            if (finished.Status == EditStatus.Refused)
                throw new EditException(finished.Errors[0]);
            if (preview)
                return Fitted(Final(batch.Preview()));
            // The revision and the script versions after the commit are not known yet. The longest numbers give the
            // longest text, so the result of the commit is never larger than this result. The result counts the
            // inverse of the mission commands too.
            var longest = Final(finished with { UndoLabel = batch.UndoLabel, Revision = long.MaxValue });
            Fitted(longest with { Versions = longest.Versions.ToDictionary(v => v.Key, _ => long.MaxValue) });
            var result = Final(batch.Commit());
            if (result.Status == EditStatus.Applied)
                call.Host.Edited(call.Tool, result);
            return output(result);
        }
    }

    /// <summary>Refuses a result that is larger than the size limit of a result (section 8.4 of the MCP1 design). An
    /// edit calls it before its change, because the queue replaces a larger result with the error <c>limit</c>, and
    /// that error must not hide a change that applied.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/>: the change did not run.</exception>
    internal static ToolResult Fitted(ToolCall call, ToolResult text)
    {
        if (text.SizeAboveLimit(call.Limits) is { } size)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, ToolResult.ResultSize,
                AgentTexts.Error("limit_edit_result", ("size", size), ("max", call.Limits.ResultKilobytes))));
        }
        return text;
    }

    /// <summary>
    /// The net result of the commands of a call, so that each reference of the result resolves after the call (section
    /// 5.6 of the MCP1 design). A step can add an item and a later step can change, rename or delete it. These are the
    /// rules:
    /// <list type="bullet">
    /// <item>An item that the call adds is an add with its last reference. The changes and the renames leave it out.
    /// An example is a new object with the nickname of its request.</item>
    /// <item>An item that the call adds and then deletes is not in the result, because it did not exist before the
    /// call.</item>
    /// <item>Two renames of one item are one rename from its first to its last reference. A rename back to the first
    /// reference is no rename.</item>
    /// <item>A change gives the last reference of the item. An item that the call deletes is not a change.</item>
    /// <item>A delete of an item that the call renamed gives the reference before the call, and no rename.</item>
    /// </list>
    /// </summary>
    /// <remarks>The walk reads the parts in order. A rename changes an add only when the add came before it. So an
    /// existing item that a step renames stays a rename. This is also true when a later step adds an item with its old
    /// nickname (review finding of MCP1-2, step 4). In a part, the renames and the deletes name the items before the
    /// part, and the adds and the changes name the items after it. So the renames and the deletes apply first.</remarks>
    /// <param name="result">The merged result of the parts, with the lists of the other kinds.</param>
    /// <param name="parts">The parts of the commands, in order.</param>
    internal static EditResult Shaped(EditResult result, IReadOnlyList<EditResult> parts)
    {
        var adds = new List<string>();
        var renames = new List<ResultRename>();
        var changes = new List<string>();
        var deletes = new List<ResultDelete>();
        foreach (var part in parts)
        {
            foreach (var rename in part.Renames)
            {
                Replace(changes, rename.From, rename.To);
                if (Replace(adds, rename.From, rename.To))
                    continue;
                var at = renames.FindIndex(r => r.To == rename.From);
                if (at < 0)
                    renames.Add(rename);
                else if (renames[at].From == rename.To)
                    renames.RemoveAt(at);
                else
                    renames[at] = renames[at] with { To = rename.To };
            }
            foreach (var delete in part.Deletes)
            {
                changes.Remove(delete.Ref);
                if (adds.Remove(delete.Ref))
                    continue;
                var at = renames.FindIndex(r => r.To == delete.Ref);
                if (at < 0)
                {
                    deletes.Add(delete);
                    continue;
                }
                deletes.Add(delete with { Ref = renames[at].From });
                renames.RemoveAt(at);
            }
            adds.AddRange(part.Adds.Where(add => !adds.Contains(add)));
            changes.AddRange(part.Changes.Where(change => !adds.Contains(change) && !changes.Contains(change)));
        }
        return result with { Adds = adds, Renames = renames, Changes = changes, Deletes = deletes };
    }

    /// <summary>Replaces a reference in a list with its new reference. Gives false when the list does not have
    /// it.</summary>
    private static bool Replace(List<string> refs, string from, string to)
    {
        var at = refs.IndexOf(from);
        if (at < 0)
            return false;
        refs[at] = to;
        return true;
    }

    /// <summary>
    /// The section of a reference argument of an edit tool that changes sections of INI files that are not mission
    /// files, for example a scene tool or a builder. A reference of a mission item gives <c>invalid_argument</c> with
    /// the detail <see cref="MissionItem"/>: the mission tools and the generic edit tools change mission items (see
    /// <see cref="ItemEditTools.Target"/>). A reference of a cutscene gives <c>not_available</c> until the cutscene
    /// tools (MCP1-4). An item of a tool group that the user hides gives <c>not_available</c> too (decision D725).
    /// </summary>
    /// <param name="field">The argument, for the error.</param>
    /// <exception cref="EditException">The reference is not valid, does not resolve, or names an item that the tool
    /// does not change.</exception>
    public static SectionRef Section(ToolCall call, string text, string field) => At(field, () =>
    {
        var session = call.Session;
        var reference = ItemRefs.Parse(text);
        if (IsCutscene(reference.Kind))
            throw CutsceneLater(text);
        if (IsMission(reference))
            throw MissionItemRefusal(call, text);
        var section = ItemRefs.ResolveSection(session, reference);
        if (session.Missions.IsMissionFile(section.Path))
            throw MissionItemRefusal(call, text);
        if (session.Missions.IsSharedBuildFile(section.Path))
            throw WorldFileRefusal(call, section, text);
        RequireGroup(call, section, text);
        return section;
    });

    /// <summary>True for a reference that names an item of a mission script or of the template of a prefab, or a
    /// prefab: <c>msn:</c>, <c>trg:</c>, <c>line:</c>, <c>label:</c>, <c>inst:</c>, <c>act:</c>, <c>prefab:</c>, and a
    /// <c>sec:</c> reference of a template in prefab mode.</summary>
    internal static bool IsMission(ItemReference reference) =>
        reference.Kind is ItemRefKind.Script or ItemRefKind.Trigger or ItemRefKind.Line or ItemRefKind.Label or
            ItemRefKind.Instance or ItemRefKind.Act or ItemRefKind.Prefab ||
        reference is { Kind: ItemRefKind.Section, Path: { } path } && MissionRefs.IsPrefab(path);

    /// <summary>True for a reference that names a cutscene or an element of a cutscene.</summary>
    internal static bool IsCutscene(ItemRefKind kind) =>
        kind is ItemRefKind.Cutscene or ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent;

    /// <summary>Refuses an item of a tool group that the user hides (decision D725).</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotAvailable"/> with the detail
    /// <c>GroupHidden</c>.</exception>
    internal static void RequireGroup(ToolCall call, SectionRef section, string text)
    {
        if (GroupOf(call.Session, section) is { } group && call.Host.HiddenGroups.Contains(group))
            throw new EditException(AgentTools.GroupHidden(group) with { Target = text });
    }

    /// <summary>The tool group of a section, or null for a section of no group, for example a loadout. A section of a
    /// system file and the <c>[system]</c> sections of <c>universe.ini</c> are in the group Scene. A section of a file
    /// under <c>DATA\MISSIONS</c> is in the group Missions.</summary>
    internal static ToolGroup? GroupOf(EditSession session, SectionRef section) =>
        SectionKeys.IsMissionFile(section.Path) ? ToolGroup.Missions
        : session.IsSystemFile(section.Path) ||
          session.UniversePath is { } universe && OpenLancer.Data.IO.VfsPath.Comparer.Equals(universe, section.Path) &&
          section.Id.Type.Equals("system", StringComparison.OrdinalIgnoreCase)
            ? ToolGroup.Scene
            : null;

    /// <summary>The error of a plan that refuses: the kind of its first refusal, with the reasons of all refusals. A
    /// refusal of a nickname that another section has gives <c>name_taken</c>, and a value that a field cannot hold
    /// gives <c>invalid_value</c>.</summary>
    internal static EditError RefusalOf(EditSession session, EditPlan plan)
    {
        var errors = plan.Report(session).Errors;
        var first = errors[0];
        var kind = first.Detail switch
        {
            MissionErrors.NameTaken => EditErrorKind.NameTaken,
            MissionErrors.ItemNotFound => EditErrorKind.NotFound,
            MissionErrors.FieldValue => EditErrorKind.InvalidValue,
            MissionErrors.NotTextId => EditErrorKind.InvalidArgument,
            _ => EditErrorKind.Refused,
        };
        return first with { Kind = kind, Message = string.Join(" ", errors.Select(e => e.Message).Distinct()) };
    }

    /// <summary>
    /// Why a plan needs <c>confirm: true</c> from an agent, or null. The editor asks about the plan (decisions D238 and
    /// D242), or the plan leaves "Missing" lines, also mission lines (section 4.0.5 of the M6b design, item 6). The
    /// text names what the plan changes beside its targets.
    /// </summary>
    internal static string? ConfirmationOf(EditSession session, EditPlan plan)
    {
        if (!plan.NeedsConfirmation && plan.Missing.Count == 0 && plan.MissionMissing.Count == 0)
            return null;
        string Ref(SectionRef section) => ItemRefs.Canonical(session, section);
        var parts = new List<string> { AgentTexts.Error("confirm_intro", ("label", plan.Label)) };
        var cascade = plan.Deletes.Where(d => d.Why == DeleteReason.Cascade)
            .Select(d => Ref(d.Section)).ToList();
        if (cascade.Count > 0)
            parts.Add(AgentTexts.Error("confirm_cascade", ("refs", Shown(cascade))));
        if (plan.Fixes.Count > 0)
            parts.Add(AgentTexts.Error("confirm_fixes", ("refs", Shown(plan.Fixes.Select(f => Ref(f.Section)).Distinct().ToList()))));
        // A rename also changes the mission lines that name the item. The question names their sections and scripts.
        var missions = plan.Changes.Where(c => SectionKeys.IsMissionFile(c.Path)).ToList();
        if (missions.Count > 0)
        {
            var files = Shown(missions.Select(m => m.Path).Distinct(OpenLancer.Data.IO.VfsPath.Comparer).ToList());
            parts.Add(AgentTexts.Error(missions.Count == 1 ? "confirm_mission" : "confirm_missions",
                ("count", missions.Count), ("files", files)));
        }
        var missing = plan.Missing.Select(m => Ref(m.Reference.Section)).Concat(plan.MissionMissing.Select(m => m.Ref))
            .Distinct().ToList();
        if (missing.Count > 0)
            parts.Add(AgentTexts.Error("confirm_missing", ("refs", Shown(missing))));
        if (plan.Mentions.Count > 0)
            parts.Add(AgentTexts.Error("confirm_mentions", ("files", Shown(plan.Mentions.Select(m => m.Path).Distinct().ToList()))));
        parts.AddRange(plan.Warnings.Select(w => w.Message).Distinct());
        return string.Join(" ", parts);
    }

    /// <summary>The error of a call whose plans need <c>confirm: true</c>: the reason of each plan, and how to
    /// confirm.</summary>
    internal static EditError NeedsConfirmation(IEnumerable<string> reasons) =>
        new(EditErrorKind.NeedsConfirmation, ConfirmChanges,
            string.Join(" ", reasons.Append(AgentTexts.Error("confirm_end"))));

    /// <summary>Refuses an edit while the host takes no edits, with the reason of the host (section 8.2 of the MCP1
    /// design). The reason is the reason of the session, for example the lock or a profile in a game folder. It can
    /// also be the rule of the mods in the OpenLancer repo. Without an open profile, the edit gives
    /// <c>not_available</c>, as each other tool that reads the profile does.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotAvailable"/> without a profile, else
    /// <see cref="EditErrorKind.ReadOnly"/>.</exception>
    internal static void ReadOnlyGuard(ToolCall call)
    {
        _ = call.Session;
        if (call.Host.NoEditReason is { } reason)
            throw new EditException(new EditError(EditErrorKind.ReadOnly, MissionErrors.NotEditable, reason));
    }

    /// <summary>The refusal of a reference of a cutscene: the edit tools of this version do not change
    /// cutscenes.</summary>
    internal static EditException CutsceneLater(string reference) =>
        new(new EditError(EditErrorKind.NotAvailable, CutsceneEdits, AgentTexts.Error("edit_cutscenes_later"))
        {
            Target = reference,
        });

    /// <summary>The refusal of a reference of a mission item in a tool that does not change mission items. The text
    /// names the tools that change them.</summary>
    internal static EditException MissionItemRefusal(ToolCall call, string reference) =>
        new(new EditError(EditErrorKind.InvalidArgument, MissionItem,
            AgentTexts.Error("edit_mission_item", ("tool", call.Tool))) { Target = reference });

    /// <summary>
    /// The refusal of a section of a file under <c>DATA\MISSIONS</c> that is not a mission script and not the ship build
    /// file of a script, for a tool that does not change it. A shared ship build file gives the detail
    /// <c>SharedBuildChange</c>: random encounters can use its builds, so <c>mission_edit</c> changes the lines of its
    /// builds after a confirmation, and the text tells it. A <c>line:</c> or <c>trg:</c> reference of a pilot file or
    /// another world file gives the detail <c>WorldFile</c>, and the text names the tools that change its sections. The
    /// generic tools change a section of such a file with the commands of the Inspector (final finding 7 of MCP1-3).
    /// </summary>
    internal static EditException WorldFileRefusal(ToolCall call, SectionRef section, string reference) =>
        new(call.Session.Missions.IsSharedBuildFile(section.Path)
            ? new EditError(EditErrorKind.Refused, MissionErrors.SharedBuildChange,
                AgentTexts.Error("edit_mission_shared_build", ("path", section.Path), ("tool", call.Tool))) { Target = reference }
            : new EditError(EditErrorKind.Refused, MissionErrors.WorldFile,
                AgentTexts.Error("edit_mission_world_file", ("path", section.Path))) { Target = reference });

    /// <summary>The revision guard of <c>if_revision</c> (decision D723 of the MCP1 design). Inside a batch, the
    /// revision is the revision before the batch, because the batch changes it only at its commit.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a text that is not a whole
    /// number, and <see cref="EditErrorKind.StaleRevision"/> for a revision that changed.</exception>
    internal static void Guard(ToolCall call)
    {
        if (call.Args.Text("if_revision") is not { } text)
            return;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            throw ToolInput.Bad("if_revision", AgentTexts.Error("if_revision_bad"));
        if (call.Session.RevisionGuard(revision) is { } stale)
            throw new EditException(stale with { Field = "if_revision" });
    }

    /// <summary>Runs a part of a request. Each error of the part that names no argument gets the argument of the part.
    /// For example, a refusal of the rename to the nickname of a new item names the argument <c>nickname</c>.</summary>
    /// <exception cref="EditException">The part refuses.</exception>
    public static T At<T>(string field, Func<T> part)
    {
        try
        {
            return part();
        }
        catch (EditException e) when (e.Error.Field == null)
        {
            throw new EditException(e.Error with { Field = field });
        }
    }

    /// <inheritdoc cref="At{T}"/>
    public static void At(string field, Action part) => At(field, () =>
    {
        part();
        return 0;
    });

    /// <summary>The first references of a list, and the number of the others.</summary>
    private static string Shown(IReadOnlyList<string> refs) =>
        refs.Count <= ShownRefs
            ? string.Join(", ", refs)
            : string.Join(", ", refs.Take(ShownRefs)) + AgentTexts.Error("confirm_more", ("count", refs.Count - ShownRefs));
}

/// <summary>
/// The commit checks of an agent batch. They check the limit of the sections of one call. They also give the problems
/// that the batch adds and removes (section 4.0.5 of the M6b design, item 2). The problems are the field checks and the
/// list checks of the sections that the commands name, before and after the batch. They are data, not errors. So the
/// edit applies, as an edit of the user applies.
/// </summary>
/// <remarks>
/// <para>The problems of a section that the batch removes do not count as fixed. A renamed section compares its
/// problems with the problems of the section before the rename.</para>
/// <para>The checks skip the sections of the mission scripts and of their ship build files. The commit check of the
/// mission editor gives their problems, and it compares them with the problems before the batch (section 4.5 of the M6b
/// design). These checks note a section of a mission command only at a rename, without its problems before. So with a
/// field check or a list check of a mission section, they would give each problem of a renamed trigger as new. Today
/// no such check exists, so the skip changes no result. It also keeps these checks from reading each mission section
/// that a call changes.</para>
/// </remarks>
internal sealed class EditChecks(EditSession session, EditBatch batch, int sectionLimit) : IBatchCheck
{
    private readonly Dictionary<SectionRef, IReadOnlyList<Diagnostic>> before = [];
    private readonly HashSet<SectionRef> added = [];
    private readonly List<SectionRef> touched = [];
    private readonly HashSet<SectionRef> seen = [];

    /// <summary>Notes the sections of a plan before its command runs, and their problems now.</summary>
    /// <param name="targets">The sections that the request names.</param>
    public void Before(EditPlan plan, IEnumerable<SectionRef> targets)
    {
        var sections = targets.Concat(plan.Changes).Concat(plan.Deletes.Select(d => d.Section))
            .Concat(plan.Fixes.Select(f => f.Section)).Concat(plan.Missing.Select(m => m.Reference.Section));
        foreach (var section in sections)
        {
            if (!Touch(section))
                continue;
            if (!added.Contains(section) && !before.ContainsKey(section) && session.Get(section) != null)
                before[section] = AgentRows.DiagnosticsOf(session, section);
        }
    }

    /// <summary>Notes the sections that a plan added and selects, after its command ran.</summary>
    public void After(EditPlan plan)
    {
        foreach (var section in plan.Adds)
        {
            added.Add(section);
            Touch(section);
        }
        if (plan.SelectAfter is { } selected)
            Touch(selected);
    }

    public EditResult Check(EditBatch checkedBatch)
    {
        var count = batch.SectionCount;
        if (count > sectionLimit)
        {
            return new EditResult(EditStatus.Applied)
            {
                Errors =
                [
                    new EditError(EditErrorKind.Limit, "EditSections",
                        AgentTexts.Error("limit_sections", ("count", count), ("max", sectionLimit))),
                ],
            };
        }
        // A section that a command renamed has its problems before under its old reference.
        var renamedFrom = new Dictionary<SectionRef, SectionRef>();
        foreach (var (from, to) in batch.Renames)
        {
            renamedFrom[to] = renamedFrom.Remove(from, out var first) ? first : from;
            Touch(to);
        }
        var newProblems = new List<Problem>();
        var fixedProblems = new List<Problem>();
        foreach (var section in touched)
        {
            if (session.Get(section) == null)
                continue;
            var after = AgentRows.DiagnosticsOf(session, section);
            var was = before.GetValueOrDefault(section) ?? [];
            string? newKey = null;
            string? oldKey = null;
            if (!before.ContainsKey(section) && renamedFrom.TryGetValue(section, out var old) &&
                before.TryGetValue(old, out var oldProblems))
            {
                was = oldProblems;
                (newKey, oldKey) = (section.Id.Key, old.Id.Key);
            }
            var left = was.Select(Key).ToList();
            foreach (var problem in after)
            {
                var key = Key(problem);
                // A message can name the section, so the compare reads the new key as the old key.
                if (newKey != null && oldKey != null)
                    key = (key.Severity, key.Key, key.Rule, key.Message.Replace(newKey, oldKey, StringComparison.OrdinalIgnoreCase));
                if (!left.Remove(key))
                    newProblems.Add(AgentRows.ProblemOf(session, problem));
            }
            foreach (var problem in was.Where(p => left.Remove(Key(p))))
                fixedProblems.Add(AgentRows.ProblemOf(session, problem));
        }
        return new EditResult(EditStatus.Applied) { NewProblems = newProblems, FixedProblems = fixedProblems };
    }

    /// <summary>Notes a section that the batch touches. Gives false for a section of a mission file, which the checks
    /// skip.</summary>
    private bool Touch(SectionRef section)
    {
        if (session.IsGuardedMissionFile(section.Path))
            return false;
        if (seen.Add(section))
            touched.Add(section);
        return true;
    }

    private static (DiagnosticSeverity Severity, string? Key, string? Rule, string Message) Key(Diagnostic problem) =>
        (problem.Severity, problem.Key, problem.Rule, problem.Message);
}
