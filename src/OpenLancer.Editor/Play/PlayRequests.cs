using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Dialogs;

namespace OpenLancer.Editor.Play;

/// <summary>The game of a play copy that Stop can stop (milestone M11). <see cref="GamePlay"/> is it, and the tests give
/// a fake.</summary>
internal interface IGameStop
{
    /// <summary>True while the game of a play copy runs.</summary>
    bool GameRuns { get; }

    /// <summary>Stops the game of the play copy, or ends it at once when it does not exit.</summary>
    void StopGame();
}

/// <summary>What a stop request stopped.</summary>
internal enum PlayStopped
{
    /// <summary>No game ran.</summary>
    Nothing,

    /// <summary>The game of Play in OpenLancer.</summary>
    OpenLancer,

    /// <summary>The game of a play copy.</summary>
    Game,
}

/// <summary>
/// The result of a play request of the shared command layer (section 7.2 of the M6c design). A play request changes no
/// data of the mod, so the result has no revision and no history entry.
/// </summary>
/// <param name="Play">The Play status of OpenLancer when the request ended.</param>
internal sealed record PlayRequestResult(PlayStatus Play)
{
    /// <summary>Why the play did not start, or null when it started. The kinds are <c>refused</c>,
    /// <c>save_blocked</c> and <c>changed_on_disk</c> (section 7.4 of the M6c design).</summary>
    public EditError? Error { get; init; }

    /// <summary>The start plan of a mission play, or null for another play or a plan that the play did not make.</summary>
    public StartPlan? Plan { get; init; }

    /// <summary>True when the play started, or when the running mission play starts again at the new start.</summary>
    public bool Started => Error == null;

    /// <summary>A request that did not start a play.</summary>
    public static PlayRequestResult Refused(PlayStatus play, string detail, string message,
        EditErrorKind kind = EditErrorKind.Refused) => new(play) { Error = new EditError(kind, detail, message) };
}

/// <summary>What the save of Play does for a play request (user decision of 2026-10-20 of the M6c design).</summary>
internal enum PlaySaveRule
{
    /// <summary>Play saves the edits of the active mod first, as F5 does. A request of the user, and a request of an
    /// agent that the user or the agent setting allowed to save.</summary>
    Save,

    /// <summary>A request of an agent that nobody allowed to save, because the active mod had no unsaved edits. When the
    /// active mod has unsaved edits at the save of Play, the request gives refused, and Play does not save.</summary>
    NoEdits,

    /// <summary>Play does not save. The game uses the saved files, for example for Restart the mission.</summary>
    None,
}

/// <summary>What the agent play rule reads of the editor (user decision of 2026-10-20 of the M6c design).</summary>
internal interface IAgentPlayHost
{
    /// <summary>The id of the active mod while it has unsaved edits, else null.</summary>
    string? UnsavedMod { get; }

    /// <summary>The agent setting "Auto-save and continue when an MCP agent starts Play". It is off by default.</summary>
    bool AutoSaveAgentPlay { get; }

    /// <summary>Shows a question with buttons.</summary>
    /// <param name="cancel">What Escape and the close button of the title bar do.</param>
    void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel);

    /// <summary>Closes the question of <see cref="Ask"/> with these buttons as Cancel does, when it still shows. It can
    /// run on each thread. The close runs on the UI thread, and it runs the cancel action of the question.</summary>
    void Withdraw(IReadOnlyList<Choice> choices);

    /// <summary>The agent play request of the editor that asked the user and did not end. Each kind of agent play shares
    /// it, because one dialog shows the question.</summary>
    AgentPlayRule AgentPlays { get; }
}

/// <summary>
/// The play requests of the shared command layer that each kind of play shares (section 7.2 of the M6c design, decision
/// D758). <see cref="Stop"/> is the one stop of Shift+F5, the Stop button, the menu item and the agent tool
/// <c>play_stop</c>, for each kind of play. <see cref="WithAgentRule"/> is the rule of a play request of an agent.
/// </summary>
internal static class PlayRequests
{
    /// <summary>The title of the question of the agent play rule.</summary>
    public const string AgentPlayTitle = "Agent Play";

    /// <summary>The button of the question that saves the active mod and starts the play.</summary>
    public const string SaveAndContinue = "Save and continue";

    /// <summary>The button of the question that does not start the play.</summary>
    public const string CancelLabel = "Cancel";

    /// <summary>The reason of the refusal when the user does not save (user decision of 2026-10-20).</summary>
    public const string NotSavedReason = "The user did not save. Play did not start.";

    /// <summary>The detail code of the refusal when the user does not save.</summary>
    public const string NotSavedDetail = "NotSaved";

    /// <summary>The detail code of an agent request while an earlier agent request that asked the user did not
    /// end.</summary>
    public const string AgentWaitsDetail = "AgentPlayWaits";

    /// <summary>The refusal of an agent request while the question of another agent request is open.</summary>
    public const string AgentWaitsReason = "The user did not answer an earlier agent play request yet, so this request " +
                                           "did not start. Wait for the result of the earlier request.";

    /// <summary>The refusal of an agent request after the user clicked "Save and continue" for an earlier agent request,
    /// until Play gives the result of the earlier request.</summary>
    public const string AgentRunsReason = "An earlier agent play request did not end yet, so this request did not " +
                                          "start. Wait for the result of the earlier request.";

    /// <summary>The question of the agent play rule (user decision of 2026-10-20 of the M6c design).</summary>
    public static string AgentPlayQuestion(string mod) =>
        $"An agent wants to start Play. Mod '{mod}' has unsaved edits. Save them and continue?";

    /// <summary>The refusal of an agent request whose active mod got unsaved edits after the request, before the save
    /// of Play. Nobody allowed the save of these edits.</summary>
    public static string EditsWhileWaiting(string mod) =>
        $"Mod '{mod}' got unsaved edits while the agent play request waited. Play did not save them and did not " +
        "start. Send the request again, so that the editor asks the user.";

    /// <summary>Stops the game of Play in OpenLancer when one runs, else the game of the play copy. A game that does not
    /// respond ends at once (Force stop).</summary>
    public static PlayStopped Stop(PlayController play, IGameStop games)
    {
        if (play.Status != PlayStatus.Idle)
        {
            play.Stop();
            return PlayStopped.OpenLancer;
        }

        if (games.GameRuns)
        {
            games.StopGame();
            return PlayStopped.Game;
        }

        return PlayStopped.Nothing;
    }

    /// <summary>True when a play request asks the user before it starts: a request of an agent while the active mod has
    /// unsaved edits and the agent setting is off.</summary>
    public static bool AsksUser(IAgentPlayHost host, EditOrigin origin) =>
        origin == EditOrigin.Agent && host.UnsavedMod != null && !host.AutoSaveAgentPlay;

    /// <summary>
    /// Runs a play request with the agent play rule (user decision of 2026-10-20 of the M6c design). A request of an
    /// agent never saves on its own. When the active mod has unsaved edits, the editor asks the user first: "Save and
    /// continue" starts the play, and Play saves first, as F5 does. "Cancel", Escape, the close button and a dialog that
    /// takes the place of the question give the refusal <see cref="NotSavedReason"/>. Cancel is the default button, so
    /// Enter does not save. The agent setting "Auto-save and continue when an MCP agent starts Play" skips the question.
    /// A request without unsaved edits starts at once, and Play then refuses to save edits that come later
    /// (<see cref="PlaySaveRule.NoEdits"/>). Each request of the user (F5 and Ctrl+F5) starts at once and saves.
    /// </summary>
    /// <param name="status">Gives the Play status for a refusal.</param>
    /// <param name="start">Starts the play on the UI thread with the save rule of the request. The question calls it
    /// from its button, so it also runs on the UI thread.</param>
    /// <param name="cancel">Closes the question as Cancel does, for example at the time limit of an agent call. After
    /// it, "Save and continue" does not start the play, also when the click comes before the close.</param>
    public static Task<PlayRequestResult> WithAgentRule(IAgentPlayHost host, EditOrigin origin, Func<PlayStatus> status,
        Func<PlaySaveRule, Task<PlayRequestResult>> start, CancellationToken cancel = default)
    {
        if (origin != EditOrigin.Agent || host.AutoSaveAgentPlay)
        {
            return start(PlaySaveRule.Save);
        }

        if (host.UnsavedMod is not { } mod)
        {
            return start(PlaySaveRule.NoEdits);
        }

        var done = new TaskCompletionSource<PlayRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<Choice> choices =
        [
            // The cancel can come on another thread while the close of the question waits for the UI thread.
            new Choice(SaveAndContinue, () =>
            {
                if (cancel.IsCancellationRequested)
                    Refuse();
                else
                    Forward(start(PlaySaveRule.Save), done);
            }),
            new Choice(CancelLabel, Refuse) { Default = true },
        ];
        host.Ask(AgentPlayTitle, AgentPlayQuestion(mod), choices, Refuse);
        if (cancel.CanBeCanceled)
        {
            var withdraw = cancel.Register(() => host.Withdraw(choices));
            done.Task.ContinueWith(_ => withdraw.Dispose(), TaskScheduler.Default);
        }

        return done.Task;

        void Refuse() => done.TrySetResult(PlayRequestResult.Refused(status(), NotSavedDetail, NotSavedReason));
    }

    /// <summary>Gives the result of a task to a completion source, also an exception.</summary>
    private static void Forward(Task<PlayRequestResult> from, TaskCompletionSource<PlayRequestResult> to) =>
        from.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                to.TrySetResult(task.Result);
            }
            else if (task.Exception is { } failure)
            {
                to.TrySetException(failure.InnerExceptions);
            }
            else
            {
                to.TrySetCanceled();
            }
        }, TaskScheduler.Default);
}

/// <summary>
/// The agent play request of one editor that asked the user and did not end (user decision of 2026-10-20 of the M6c
/// design). One dialog shows the question, so the agent tools <c>play</c> and <c>mission_play</c> share it. Until that
/// request ends, a new agent request gets <c>busy</c>. The members run on the UI thread.
/// </summary>
internal sealed class AgentPlayRule
{
    // The last agent request that asked the user, or null.
    private Asked? asking;

    /// <summary>The refusal <c>busy</c> of a new agent request while an earlier agent request that asked the user did
    /// not end, or null.</summary>
    /// <param name="status">The Play status for the refusal.</param>
    public PlayRequestResult? Busy(PlayStatus status) =>
        asking is { Result.IsCompleted: false } earlier
            ? PlayRequestResult.Refused(status, PlayRequests.AgentWaitsDetail,
                earlier.Answered ? PlayRequests.AgentRunsReason : PlayRequests.AgentWaitsReason, EditErrorKind.Busy)
            : null;

    /// <summary>Runs a play request with the agent play rule (see <see cref="PlayRequests.WithAgentRule"/>) and keeps a
    /// request that asks the user until it ends.</summary>
    /// <param name="afterAnswer">Why the request cannot start after "Save and continue", or null. The user can start
    /// another play while the question is open, so the checks of the request run again after the answer.</param>
    public Task<PlayRequestResult> Run(IAgentPlayHost host, EditOrigin origin, Func<PlayStatus> status,
        Func<PlayRequestResult?> afterAnswer, Func<PlaySaveRule, Task<PlayRequestResult>> start,
        CancellationToken cancel = default)
    {
        var asked = PlayRequests.AsksUser(host, origin) ? new Asked() : null;
        var result = PlayRequests.WithAgentRule(host, origin, status, save =>
        {
            if (asked == null)
            {
                return start(save);
            }

            asked.Answered = true;
            return afterAnswer() is { } late ? Task.FromResult(late) : start(save);
        }, cancel);
        if (asked != null)
        {
            asked.Result = result;
            asking = asked;
        }

        return result;
    }

    /// <summary>An agent request that asked the user.</summary>
    private sealed class Asked
    {
        /// <summary>The result of the request. It completes when Play gives the result, which can be after the
        /// answer.</summary>
        public Task<PlayRequestResult>? Result { get; set; }

        /// <summary>True after the user clicked "Save and continue".</summary>
        public bool Answered { get; set; }
    }
}
