using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Validation;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>What the editor host reads from the editor window. The window gives it, and the tests give a
/// fake.</summary>
internal interface IAgentEditor
{
    /// <summary>The edit session of the open profile, or null while no profile is open.</summary>
    EditSession? Edits { get; }

    /// <summary>Why the session of the open profile takes no edits now, or null when it takes them or no profile is
    /// open. Examples are the lock and a profile in a game folder.</summary>
    string? EditReason { get; }

    /// <summary>The reason of <see cref="OpenLancer.Data.Editing.RepoMods.Reason"/> while the user did not allow the
    /// edits of the active mod for this session, else null (decision D738).</summary>
    string? RepoReason { get; }

    /// <summary>The text of a string id of the game data of the open profile, or an empty text.</summary>
    string Name(int ids);

    /// <summary>The plain text of an infocard id of the game data of the open profile, or an empty text.</summary>
    string InfoText(int ids);

    /// <summary>The agent settings of the editor.</summary>
    AgentSettings Settings { get; }

    /// <summary>The messages of the Console.</summary>
    ConsoleLog Log { get; }

    /// <summary>What the editor shows now: the open system, mission and cutscene.</summary>
    AgentView View { get; }

    /// <summary>The views of the editor for the view tools: the open of a view, the selection and the images.</summary>
    IAgentViews Views { get; }

    /// <summary>The Play of the editor for the play tools.</summary>
    IAgentPlay Play { get; }

    /// <summary>Saves as File &gt; Save does: the gate, the report in the Console, the reload and the validation. When
    /// files of the mod changed outside the editor, the dialog of the editor asks the user.</summary>
    SaveOutcome Save();

    /// <summary>The state of the last validation of the session, or null.</summary>
    ValidationState? Validation { get; }

    /// <summary>Runs on the UI thread when a validation of the session gives its result.</summary>
    event Action<ValidationState>? Validated;

    /// <summary>The game links of the editor for an export and the publish checklist of a mod.</summary>
    AgentGames Games(ModManifest manifest);

    /// <summary>Why an export cannot start now, or null: an export of the Export dialog or of the agent runs.</summary>
    string? ExportBlock { get; }

    /// <summary>True while the user pauses the agent edits (decision D734). The Agent menu and the status bar set
    /// it.</summary>
    bool Paused { get; }

    /// <summary>Follow mode of the agent edits (decision D719).</summary>
    AgentFollow Follow { get; }
}

/// <summary>
/// The editor host of the agent tools (section 5.4 of the MCP1 design): the session of the open profile, shared with
/// the user, the Console, the view, and the target checks in an oltool process. Each member runs on the UI thread,
/// in the session part of a call.
/// </summary>
/// <param name="exports">The export of the agent that runs now. The controller keeps it for all starts of the server,
/// or null for an own one.</param>
internal sealed class EditorAgentHost(IAgentEditor editor, AgentExports? exports = null) : AgentHost
{
    public override AgentHostKind Kind => AgentHostKind.Editor;

    public override AgentExports Exports { get; } = exports ?? new AgentExports();

    /// <summary>An export of the Export dialog or of the agent runs (review finding R12 of MCP1-2).</summary>
    public override string? ExportBusy => editor.ExportBlock ?? base.ExportBusy;

    public override EditSession? Session => editor.Edits;

    /// <summary>The reason of the open profile, or the reason that no profile is open. The edits of a mod in the
    /// OpenLancer repo wait for the allowance of the user (decision D738). The agent gets the repo-mod text of section
    /// 10 of the MCP1 design, which tells where the user allows the edits.</summary>
    public override string? NoEditReason =>
        editor.Edits == null ? AgentTexts.Error("no_profile")
        : editor.EditReason ?? (editor.RepoReason is { } repo ? $"{repo} {AgentTexts.Error("repo_mod_editor")}" : null);

    public override AgentLimits Limits => editor.Settings.Limits;

    /// <summary>Pause of the Agent menu and of the status bar (decision D734).</summary>
    public override bool Paused => editor.Paused;

    public override IReadOnlySet<ToolGroup> HiddenGroups => editor.Settings.HiddenToolGroups;

    public override AgentView? View => editor.View;

    public override IAgentViews Views => editor.Views;

    public override IAgentPlay Play => editor.Play;

    public override string NameText(int ids) => editor.Name(ids);

    /// <summary>The game links of the editor, as the Export dialog and the Mods window use them.</summary>
    public override AgentGames Games(ModManifest manifest) => editor.Games(manifest);

    /// <summary>
    /// The save of the editor (section 7.6 of the MCP1 design): the gate, the report in the Console, the reload and the
    /// validation. A save that changed files has the validation as the tail of the call. The tail waits for the result
    /// of the validation of its save, or of a later save that replaced it.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/>: the save did not start. The Console tells
    /// why.</exception>
    public override AgentSave Save()
    {
        var outcome = editor.Save();
        if (outcome.Result is not { } result)
            throw new EditException(new EditError(EditErrorKind.Refused, "SaveNotStarted", AgentTexts.Error("save_not_started")));
        var save = new AgentSave(result, outcome.Number, outcome.Failure == SaveFailure.Blocked);
        return result is { Saved: true, Incomplete: false } && outcome.Number > 0
            ? save with { Validation = ValidationOf(outcome.Number) }
            : save;
    }

    /// <summary>The wait for the result of the validation of a save. A validation that cannot start gives its result
    /// at once, during the save, so the wait also reads the state of the last validation. The tail does not touch the
    /// session.</summary>
    private Func<CancellationToken, Task<SaveValidation>> ValidationOf(int number)
    {
        var done = new TaskCompletionSource<SaveValidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<ValidationState> handler = null!;
        handler = state =>
        {
            // A later save stops the validation of an earlier save, and its validation replaces it.
            if (state.Save < number || state.Result is not { } result)
                return;
            done.TrySetResult(new SaveValidation(result.Save, result.Problems, result.Failure));
            editor.Validated -= handler;
        };
        editor.Validated += handler;
        if (editor.Validation is { Running: false } last)
            handler(last);
        return async cancel =>
        {
            try
            {
                return await done.Task.WaitAsync(cancel).ConfigureAwait(false);
            }
            finally
            {
                // The removal of a handler is safe on each thread.
                editor.Validated -= handler;
            }
        };
    }

    public override string InfoText(int ids) => editor.InfoText(ids);

    /// <summary>
    /// The Console entry of an applied edit (section 8.5 of the MCP1 design). It is an Info entry with the source "Agent"
    /// and the label of the request. It links to the first item that the edit adds, renames or changes. The entry has the
    /// origin OpenLancer, as each message of the editor has. So the toggle of the base-game messages never hides it
    /// (decision D718).
    /// </summary>
    /// <remarks>Follow mode then opens and frames the item of the edit (decision D719).</remarks>
    public override void Edited(string tool, EditResult result) => AfterChange(() =>
    {
        editor.Log.Add(ConsoleSeverity.Info, AgentController.Source, Sentence(result.Label ?? result.UndoLabel ?? tool),
            FirstItem(result));
        editor.Follow.Edited(result);
    });

    /// <summary>The Console entry of a refused call of a tool that changes something: a Warning "{tool}:
    /// {message}".</summary>
    public override void Refused(string tool, EditError error) =>
        editor.Log.Add(ConsoleSeverity.Warning, AgentController.Source, $"{tool}: {error.Message}",
            error.Target is { } target ? SectionOf(target) : null);

    /// <summary>The Console entry of an undo or a redo of the agent, for example "Undo Agent: Create Zone_1.". The undo
    /// does not run the selection rule of an undo of the user. Only follow mode selects the item of an undone delete
    /// again.</summary>
    public override void Stepped(string tool, string label) => AfterChange(() =>
    {
        editor.Log.Add(ConsoleSeverity.Info, AgentController.Source, Sentence($"{(tool == "redo" ? "Redo" : "Undo")} {label}"));
        editor.Follow.Stepped(tool, label);
    });

    /// <summary>The start of the Console entry of a fault in what the editor shows of an agent change.</summary>
    internal const string ShowFailedText = "The editor could not show the agent change:";

    /// <summary>
    /// Runs what the editor shows of an agent change: the Console entry and follow mode. The change is in the history when
    /// this runs. So a fault here must not give the agent an error that tells that the tool changed nothing. The Console
    /// gets the fault as an Error entry, and the call keeps its result (review finding of MCP1-2, step 8).
    /// </summary>
    private void AfterChange(Action show)
    {
        try
        {
            show();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            editor.Log.Add(ConsoleSeverity.Error, AgentController.Source, $"{ShowFailedText} {e.Message}");
        }
    }

    /// <summary>A label as a sentence with a full stop.</summary>
    private static string Sentence(string label) => label.TrimEnd().TrimEnd('.') + ".";

    /// <summary>The section of the first item of a result that the edit view has: a new item, a renamed item, then a
    /// changed item. A delete links to nothing, because its items are gone.</summary>
    private SectionRef? FirstItem(EditResult result) =>
        result.Adds.Concat(result.Renames.Select(r => r.To)).Concat(result.Changes).Select(SectionOf)
            .FirstOrDefault(s => s != null);

    /// <summary>The section of an item reference of a result. It is null when the reference names no section of the edit
    /// view now, for example a prop group.</summary>
    private SectionRef? SectionOf(string reference)
    {
        if (Session is not { } session)
            return null;
        try
        {
            return ItemRefs.ResolveSection(session, ItemRefs.Parse(reference));
        }
        catch (Exception e) when (e is EditException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The entries of the Console, oldest first. The base-game entries show only on request, as in the Console
    /// (decision D519). An engine line that waits for its origin does not show yet.</summary>
    public override IReadOnlyList<AgentConsoleEntry> Console(ConsoleFilter filter)
    {
        var lowest = filter.Severity switch
        {
            AgentSeverity.Error => ConsoleSeverity.Error,
            AgentSeverity.Warning => ConsoleSeverity.Warning,
            _ => ConsoleSeverity.Info,
        };
        var session = Session;
        return editor.Log.Entries
            .Where(e => e.Severity >= lowest && (filter.Since == null || e.Sequence > filter.Since))
            .Where(e => filter.Source == null || e.Source.Equals(filter.Source, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Origin.Kind != OriginKind.Pending && (filter.BaseGame || !e.Origin.IsBaseGame))
            .Select(e => new AgentConsoleEntry(e.Sequence, e.Time.ToUniversalTime(), Severity(e.Severity), e.Source, e.Message,
                e.Target is { } target && session != null ? ItemRefs.Canonical(session, target) : null, e.Origin.Kind))
            .ToList();
    }

    /// <summary>
    /// The target check in an oltool process, as the tail of the call: the check merges all mods of the profile on the
    /// files of the game, which takes seconds, and the engine log of a data load is process-wide (see
    /// <see cref="Editing.ToolProcess"/>). The game folder is the linked game of the target.
    /// </summary>
    public override Func<CancellationToken, Task<TargetCheckResult>> CheckTarget(string target)
    {
        var session = Session!;
        var arguments = new List<string> { "validate", session.Profile.FilePath, "--target", target, "--json" };
        if (session.TargetGame?.Invoke(target) is { } game)
        {
            arguments.Add("--game");
            arguments.Add(game.Root);
        }
        return token => RunCheck(ProgramPaths.Oltool, arguments, token);
    }

    /// <summary>Runs <c>oltool validate --target</c> and gives its problems. A cancel of the call stops the
    /// process.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotAvailable"/>: the check could not run (exit code
    /// 2).</exception>
    internal static async Task<TargetCheckResult> RunCheck(string tool, IReadOnlyList<string> arguments, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{tool} did not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            var errors = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            if (process.ExitCode == 2)
                throw new EditException(CheckQueries.TargetCheckError((await errors.ConfigureAwait(false)).Trim()));
            var problems = ValidationJson.Read(await output.ConfigureAwait(false));
            return new TargetCheckResult(problems.Select(p => Problem.From(p, null)).ToList(), []);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static AgentSeverity Severity(ConsoleSeverity severity) => severity switch
    {
        ConsoleSeverity.Error => AgentSeverity.Error,
        ConsoleSeverity.Warning => AgentSeverity.Warning,
        _ => AgentSeverity.Info,
    };
}
