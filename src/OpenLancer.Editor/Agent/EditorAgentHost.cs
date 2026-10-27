using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
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
}

/// <summary>
/// The editor host of the agent tools (section 5.4 of the MCP1 design): the session of the open profile, shared with
/// the user, the Console, the view, and the target checks in an oltool process. Each member runs on the UI thread,
/// in the session part of a call.
/// </summary>
internal sealed class EditorAgentHost(IAgentEditor editor) : AgentHost
{
    public override AgentHostKind Kind => AgentHostKind.Editor;

    public override EditSession? Session => editor.Edits;

    /// <summary>The reason of the open profile, or the reason that no profile is open. The edits of a mod in the
    /// OpenLancer repo wait for the allowance of the user (decision D738). The agent gets the repo-mod text of section
    /// 10 of the MCP1 design, which tells where the user allows the edits.</summary>
    public override string? NoEditReason =>
        editor.Edits == null ? AgentTexts.Error("no_profile")
        : editor.EditReason ?? (editor.RepoReason is { } repo ? $"{repo} {AgentTexts.Error("repo_mod_editor")}" : null);

    public override AgentLimits Limits => editor.Settings.Limits;

    public override IReadOnlySet<ToolGroup> HiddenGroups => editor.Settings.HiddenToolGroups;

    public override AgentView? View => editor.View;

    public override IAgentViews Views => editor.Views;

    public override string NameText(int ids) => editor.Name(ids);

    public override string InfoText(int ids) => editor.InfoText(ids);

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
