using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
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

    /// <summary>The mission play request of the shared command layer (<see cref="MissionPlay.Request"/>). A cancel
    /// closes the question of the agent play rule as Cancel does.</summary>
    Task<PlayRequestResult> PlayMission(MissionPlayRequest request, CancellationToken cancel);

    /// <summary>The live trace query of the shared command layer (<see cref="MissionWorkspace.LiveTrace"/>).</summary>
    LiveTraceResult LiveTrace(int play, long since);
}

/// <summary>
/// The Play of the editor for the agent tools <c>play</c> and <c>play_stop</c> (section 7.6 of the MCP1 design,
/// decision D739), and <c>mission_play</c> and <c>mission_play_trace</c> (section 7.4). A play request of an agent runs
/// the checks of the Play menu or of the mission play request, and the agent play rule of the user decision of
/// 2026-10-20: it never saves on its own, and with unsaved edits the editor asks the user first. While a game runs, the
/// request gives <c>refused</c>, and the agent stops the game with <c>play_stop</c> first. The Play menu closes the
/// running game instead. A mission play of an agent starts a running mission play again at its start, as F5 does.
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
        return Wait(result, withdraw, done => new PlayRequestView(StatusOf(done.Play)));
    }

    public Func<CancellationToken, Task<MissionPlayView>> StartMission(StartPlanRequest request)
    {
        var play = new MissionPlayRequest(request.Script, request.Start)
        {
            Origin = EditOrigin.Agent,
            Place = request.Place,
            PlaceBase = request.PlaceBase,
        };
        var withdraw = new CancellationTokenSource();
        var result = editor.PlayMission(play, withdraw.Token);
        // The mission play request refuses before it asks the user: another play runs, an earlier agent request did
        // not end, the script or the trigger does not resolve, or the plan refuses. The session part gives such a
        // refusal, so the Console shows it, as for play.
        if (result.IsCompletedSuccessfully && result.Result.Error is { } refused)
        {
            withdraw.Dispose();
            throw new EditException(refused with { Field = refused.Field ?? FieldOf(refused.Detail) });
        }
        return Wait(result, withdraw, done => MissionView(play, done));
    }

    public MissionTraceView Trace(int play, long since, int limit)
    {
        var trace = editor.LiveTrace(play, since);
        var script = trace.Script;
        var changes = trace.Changes.Take(limit).Select(c => new MissionTraceChange(c.Sequence, c.Time,
            script == null ? c.Trigger : new TriggerRef(script, c.Trigger).ToString(), c.Change)).ToList();
        return new MissionTraceView(trace.Play, changes, trace.Changes.Count > limit, StatusOf(trace.Status))
        {
            Mission = trace.Mission,
            Script = script == null ? null : new ScriptRef(script).ToString(),
            Dropped = trace.Dropped,
            GameRuns = trace.GameRuns,
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

    /// <summary>The argument of a refusal of the mission play request, or null: a script or a trigger of the start that
    /// does not resolve.</summary>
    private static string? FieldOf(string detail) => detail switch
    {
        MissionErrors.ScriptNotFound => "script",
        MissionErrors.TriggerNotFound => "start",
        _ => null,
    };

    /// <summary>The wait for the result of a play request, as the tail of the call. The end of the wait by its time
    /// limit or by a cancel of the client cancels <paramref name="withdraw"/>, so the open question closes as Cancel
    /// does, and a later click of the user does not start the play.</summary>
    private static Func<CancellationToken, Task<T>> Wait<T>(Task<PlayRequestResult> result,
        CancellationTokenSource withdraw, Func<PlayRequestResult, T> view) => async cancel =>
    {
        try
        {
            var done = await result.WaitAsync(cancel).ConfigureAwait(false);
            return done.Error is { } error ? throw new EditException(error) : view(done);
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

    /// <summary>The result of a mission play that started: the start of the plan of the play. Play makes the plan after
    /// its save, so the start is the mission start when the saved script does not have the trigger of the
    /// request.</summary>
    private static MissionPlayView MissionView(MissionPlayRequest request, PlayRequestResult done)
    {
        var plan = done.Plan;
        var start = plan?.Start ?? request.Start;
        var kind = plan?.Kind ?? (start.IsMissionStart ? StartKind.MissionStart : StartKind.Approximate);
        return new MissionPlayView(StatusOf(done.Play), new ScriptRef(request.Script).ToString(),
            RunJson.StartText(request.Script, start), kind, plan?.StartName ?? StartPlan.NameOf(kind, start))
        {
            Warnings = plan?.Warnings ?? [],
        };
    }

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
