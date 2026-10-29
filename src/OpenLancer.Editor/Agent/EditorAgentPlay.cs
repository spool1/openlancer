using System;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>What the play tools of the agent read of the editor. The window gives it, and the tests give a fake. Each
/// member runs on the UI thread.</summary>
internal interface IAgentPlayEditor : IAgentPlayHost
{
    /// <summary>The Play status of OpenLancer.</summary>
    PlayStatus PlayStatus { get; }

    /// <summary>True while the game of a play copy runs (Play in the game).</summary>
    bool GameRuns { get; }

    /// <summary>Why Play cannot start at a start now, or null: the checks of the Play menu
    /// (<see cref="PlayChecks.StartBlock"/>).</summary>
    string? StartBlock(PlayStart start);

    /// <summary>Asks Play for a play of an agent at a start, with the ship of the Play menu
    /// (<see cref="PlayController.Request"/>).</summary>
    Task<PlayRequestResult> Request(PlayStart start, PlaySaveRule save);

    /// <summary>The stop of Shift+F5 (<see cref="PlayRequests.Stop"/>).</summary>
    PlayStopped Stop();
}

/// <summary>
/// The Play of the editor for the agent tools <c>play</c> and <c>play_stop</c> (section 7.6 of the MCP1 design, decision
/// D739). A play request of an agent runs the checks of the Play menu and the agent play rule of the user decision of
/// 2026-10-20: it never saves on its own, and with unsaved edits the editor asks the user first. While a game runs, the
/// request gives <c>refused</c>, and the agent stops the game with <c>play_stop</c> first. The Play menu closes the
/// running game instead.
/// </summary>
internal sealed class EditorAgentPlay(IAgentPlayEditor editor) : IAgentPlay
{
    /// <summary>The detail code of a request while a game runs.</summary>
    public const string OtherPlayDetail = "OtherPlay";

    /// <summary>The refusal of a request while a game runs.</summary>
    public const string OtherPlayReason = "A game of Play runs, so Play did not start. Call play_stop first.";

    /// <summary>The detail code of a start that the checks of the Play menu refuse.</summary>
    public const string StartBlockedDetail = "StartBlocked";

    public Func<CancellationToken, Task<PlayRequestView>> Start(PlayStart start)
    {
        if (editor.AgentPlays.Busy(editor.PlayStatus) is { Error: { } busy })
            throw new EditException(busy);
        if (Refusal(start) is { Error: { } refused })
            throw new EditException(refused);
        // The end of the wait by its time limit or by a cancel of the client closes an open question. The session part
        // has no token, so the wait cancels this source.
        var withdraw = new CancellationTokenSource();
        var result = editor.AgentPlays.Run(editor, EditOrigin.Agent, () => editor.PlayStatus, () => Refusal(start),
            save => editor.Request(start, save), withdraw.Token);
        return async cancel =>
        {
            try
            {
                var done = await result.WaitAsync(cancel).ConfigureAwait(false);
                return done.Error is { } error ? throw new EditException(error) : new PlayRequestView(StatusOf(done.Play));
            }
            catch (OperationCanceledException)
            {
                // No agent waits for the play now, so a later click of the user must not start it.
                withdraw.Cancel();
                throw;
            }
            finally
            {
                withdraw.Dispose();
            }
        };
    }

    public PlayStopView Stop()
    {
        var stopped = editor.Stop() switch
        {
            PlayStopped.OpenLancer => AgentPlayStopped.OpenLancer,
            PlayStopped.Game => AgentPlayStopped.Game,
            _ => AgentPlayStopped.Nothing,
        };
        return new PlayStopView(stopped, StatusOf(editor.PlayStatus));
    }

    /// <summary>The state of Play for the agent.</summary>
    internal static AgentPlayStatus StatusOf(PlayStatus status) => status switch
    {
        PlayStatus.Idle => AgentPlayStatus.Idle,
        PlayStatus.Starting => AgentPlayStatus.Starting,
        PlayStatus.Validating => AgentPlayStatus.Validating,
        PlayStatus.Running => AgentPlayStatus.Running,
        PlayStatus.Reloading => AgentPlayStatus.Reloading,
        PlayStatus.NotResponding => AgentPlayStatus.NotResponding,
        PlayStatus.Stopping => AgentPlayStatus.Stopping,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>Why the request cannot start, or null: a game runs, or the checks of the Play menu refuse the start.
    /// The checks run again after the answer of the user, because the user can start another play or close the profile
    /// while the question is open.</summary>
    private PlayRequestResult? Refusal(PlayStart start)
    {
        var status = editor.PlayStatus;
        if (status != PlayStatus.Idle || editor.GameRuns)
            return PlayRequestResult.Refused(status, OtherPlayDetail, OtherPlayReason);
        return editor.StartBlock(start) is { } block ? PlayRequestResult.Refused(status, StartBlockedDetail, block) : null;
    }
}
