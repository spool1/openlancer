using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Missions;

/// <summary>One change of the live trace, with its sequence number in its play.</summary>
/// <param name="Sequence">The number of the change in its play, from 1.</param>
/// <param name="Time">The time of the change since the game loaded the mission, in seconds.</param>
internal sealed record LiveChange(long Sequence, double Time, string Trigger, MissionChangeKind Change);

/// <summary>The result of the live trace query of the shared command layer (section 7.2 of the M6c design).</summary>
/// <param name="Play">The number of the play of the changes, or 0 before the first mission event.</param>
/// <param name="Changes">The changes of the play after the sequence number of the request. With a script, each change
/// names its trigger as the session names it now (RF19 of the MCP1 design).</param>
/// <param name="Status">The Play status of OpenLancer.</param>
internal sealed record LiveTraceResult(int Play, IReadOnlyList<LiveChange> Changes, PlayStatus Status)
{
    /// <summary>The name of the mission of the game, for example <c>Mission_02</c>, or null.</summary>
    public string? Mission { get; init; }

    /// <summary>The install path of the script of the mission, or null for a mission without a story slot.</summary>
    public string? Script { get; init; }

    /// <summary>The changes that the game dropped in the play because its queue was full.</summary>
    public int Dropped { get; init; }

    /// <summary>True while the game of a play copy runs. The real game has no channel to the editor, so the trace then
    /// gives only the status.</summary>
    public bool GameRuns { get; init; }
}

/// <summary>
/// The live trace of the mission of the OpenLancer game (sections 4.5, 6.3 and 7.2 of the M6c design): the trigger
/// changes of the mission events of Play, as the steps of one trace for the overlay and the Run log. A step holds the
/// changes of one time. The play number grows at each start of a game and at each restart of a mission play, and at each
/// other load of a mission in the game, for example a reload. After the exit of the game, the trace stays with the time
/// of the exit until the next play or until the modder clears it. The class makes no ImGui call, so the tests check it.
/// </summary>
internal sealed class MissionLive(Func<double> clock)
{
    private readonly List<LiveChange> changes = [];
    private readonly HashSet<string> stalled = new(StringComparer.OrdinalIgnoreCase);

    // The trigger renames of the session since the game loaded the mission of the current play, in order, also by Undo
    // and Redo.
    private readonly List<(TriggerRef From, TriggerRef To)> renames = [];

    private double loadedAt;
    private long sequence;

    // True after a new play started and before its first reset. The game can still send the changes of the earlier
    // play, so the trace takes no change until the reset.
    private bool waitsForReset;

    // The time of the clock when the game exited, or null while a game runs.
    private double? endedAt;

    /// <summary>The number of the play, from 1, or 0 before the first play.</summary>
    public int Play { get; private set; }

    /// <summary>The name of the mission of the game, for example <c>Mission_02</c>, or null before the first event of a
    /// play and after <see cref="Clear"/>.</summary>
    public string? Mission { get; private set; }

    /// <summary>The steps of the live trace.</summary>
    public MissionTrace Trace { get; private set; } = new(live: true);

    /// <summary>The changes of the current play since the last <see cref="Clear"/>, in order.</summary>
    public IReadOnlyList<LiveChange> Changes => changes;

    /// <summary>The setup triggers of the test layer of the current play. The Run log shows them as "Play setup:
    /// {trigger}".</summary>
    public IReadOnlySet<string> Setup { get; private set; } = new HashSet<string>();

    /// <summary>The changes that the game dropped in the current play because its queue was full.</summary>
    public int Dropped { get; private set; }

    /// <summary>A number that grows with each change of the trace. A view that keeps a model of the trace makes it
    /// again when the number changes.</summary>
    public int Version { get; private set; }

    /// <summary>The time of the last mission event, in seconds of the clock. The flash of a run starts then.</summary>
    public double ChangedAt { get; private set; } = double.NegativeInfinity;

    /// <summary>The time now in the live trace: the seconds since the game loaded the mission, by the clock of the
    /// editor. After the exit of the game, the time of the exit.</summary>
    public double Now => (endedAt ?? clock()) - loadedAt;

    /// <summary>
    /// A new game started, or the game starts its mission play again (section 7.2 of the M6c design). The play number
    /// grows, and the trace of the earlier play goes, also after a play from the camera or at a base. The trace then
    /// takes the changes of the first reset of the game.
    /// </summary>
    public void Started()
    {
        Play++;
        renames.Clear();
        Mission = null;
        NewTrace();
        Setup = new HashSet<string>();
        waitsForReset = true;
        endedAt = null;
    }

    /// <summary>The game exited. The clock of the trace stops, so a trigger that waited at the exit does not wait
    /// longer.</summary>
    public void Ended()
    {
        if (endedAt != null)
            return;
        endedAt = clock();
        waitsForReset = false;
        Version++;
    }

    /// <summary>
    /// Takes a mission event of the game. A reset starts a new play: the trace of the play before it goes. The first
    /// reset after <see cref="Started"/> belongs to that play, so the play number stays. After <see cref="Clear"/>, the
    /// next event shows the mission again, and the sequence numbers of the play go on.
    /// </summary>
    /// <param name="setup">The setup triggers of the test layer of the play that runs. A reset keeps them for the
    /// play.</param>
    public void Take(PlayEvent.MissionChanges e, IReadOnlySet<string>? setup = null)
    {
        if (waitsForReset && !e.Reset)
            return;
        if (e.Reset || Play == 0)
        {
            // Another load of the mission, for example a reload after a save. The game then has the names of the
            // session.
            if (!waitsForReset)
            {
                Play++;
                renames.Clear();
            }
            waitsForReset = false;
            NewTrace();
            Setup = setup ?? new HashSet<string>();
        }

        Mission = e.Mission;
        var notes = new List<TraceChange>();
        if (e.Dropped > 0)
        {
            Dropped += e.Dropped;
            notes.Add(TraceChange.Dropped(e.Dropped));
        }

        foreach (var group in e.Changes.GroupBy(c => c.Ms))
        {
            var time = group.Key / 1000.0;
            var rows = new List<TraceChange>(notes);
            notes.Clear();
            foreach (var change in group)
            {
                changes.Add(new LiveChange(++sequence, time, change.Trigger, change.Change));
                rows.Add(new TraceChange(change.Change switch
                {
                    MissionChangeKind.Started => TraceChangeKind.Started,
                    MissionChangeKind.Ran => TraceChangeKind.Ran,
                    _ => TraceChangeKind.Stopped,
                }, change.Trigger));
            }

            Trace.Append(new TraceStep(Trace.Steps.Count + 1, "", rows) { Time = time });
        }

        if (notes.Count > 0)
        {
            Trace.Append(new TraceStep(Trace.Steps.Count + 1, "", notes) { Time = Now });
        }

        Version++;
        ChangedAt = clock();
    }

    /// <summary>Removes the trace ("Clear"). The play number and the sequence numbers stay, so the live trace query
    /// does not give a number twice in one play. The renames stay, because the game keeps its names.</summary>
    public void Clear()
    {
        Mission = null;
        Trace = new MissionTrace(live: true);
        changes.Clear();
        stalled.Clear();
        Dropped = 0;
        Version++;
        ChangedAt = double.NegativeInfinity;
    }

    /// <summary>
    /// Records the trigger renames of a change of the history, also an Undo or a Redo of a rename (RF19 of the MCP1
    /// design). The game keeps the names of its load, so the live trace query moves each name of the game through these
    /// renames. Play saves before it starts the game, so at the load the game has the names of the session.
    /// </summary>
    public void Renamed(MissionScriptsChange change) =>
        renames.AddRange(change.Renames.Where(r => VfsPath.Comparer.Equals(r.From.Script, r.To.Script)));

    /// <summary>The triggers that wait for the stall time of <see cref="MissionTrace"/> now and did not stall before in
    /// this play (section 6.3 of the M6c design). Each trigger comes once in a play.</summary>
    public IReadOnlyList<string> NewStalls()
    {
        var now = Now;
        var result = new List<string>();
        foreach (var (name, state) in Trace.States)
        {
            if (Trace.StallMinutes(state, now) != null && stalled.Add(name))
                result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// The changes of the live trace query (section 7.2 of the M6c design): the changes of the current play after a
    /// sequence number. A request with another play number gets all changes of the current play.
    /// </summary>
    public IReadOnlyList<LiveChange> Since(int play, long since) =>
        play == Play ? changes.Where(c => c.Sequence > since).ToList() : changes.ToList();

    /// <summary>
    /// The result of the live trace query of the shared command layer (section 7.2 of the M6c design): the play number,
    /// the changes of the play after a sequence number, and the Play status. While only the game of a play copy runs,
    /// the result has no changes, because the real game has no channel to the editor. With a script, each change names
    /// its trigger as the session names it now, so a reference of the change finds the trigger after a rename.
    /// </summary>
    /// <param name="play">The play number of the last result of the caller, or 0.</param>
    /// <param name="since">The last sequence number that the caller has, or 0.</param>
    /// <param name="status">The Play status of OpenLancer.</param>
    /// <param name="gameRuns">True while the game of a play copy runs.</param>
    /// <param name="script">The install path of the script of the mission of the trace, or null.</param>
    public LiveTraceResult Result(int play, long since, PlayStatus status, bool gameRuns, string? script) =>
        new(Play, gameRuns && status == PlayStatus.Idle ? [] : NamesNow(Since(play, since), script), status)
        {
            Mission = Mission,
            Script = script,
            Dropped = Dropped,
            GameRuns = gameRuns,
        };

    /// <summary>The changes with the names of their triggers now in a script.</summary>
    private IReadOnlyList<LiveChange> NamesNow(IReadOnlyList<LiveChange> list, string? script) =>
        script == null || renames.Count == 0
            ? list
            : list.Select(c => c with { Trigger = NameNow(script, c.Trigger) }).ToList();

    /// <summary>The name in the session now of a trigger of a script that the game names.</summary>
    private string NameNow(string script, string trigger)
    {
        foreach (var (from, to) in renames)
        {
            if (VfsPath.Comparer.Equals(from.Script, script) && MissionIndex.Is(trigger, from.Name))
                trigger = to.Name;
        }
        return trigger;
    }

    /// <summary>Starts an empty trace for a new play: no change, no stall, and the time from now.</summary>
    private void NewTrace()
    {
        Trace = new MissionTrace(live: true);
        changes.Clear();
        stalled.Clear();
        Dropped = 0;
        sequence = 0;
        loadedAt = clock();
        endedAt = null;
        Version++;
        ChangedAt = double.NegativeInfinity;
    }
}

/// <summary>The Console notes of the live view (sections 6.5 and 8.4 of the M6c design). The trigger changes go to the
/// Run log only.</summary>
internal static class LiveNotes
{
    /// <summary>The end notes of the triggers that ran in a mission event: "{slot} ends with success at trigger
    /// '{trigger}'." or "{slot} ends with fail at trigger '{trigger}'."</summary>
    public static IEnumerable<string> Ends(MissionAnalysis analysis, PlayEvent.MissionChanges changes)
    {
        var slot = SlotOf(analysis);
        foreach (var change in changes.Changes.Where(c => c.Change == MissionChangeKind.Ran))
        {
            if (analysis.Index.Trigger(change.Trigger) is not { } trigger ||
                trigger.ActionsOf("Act_ChangeState").FirstOrDefault()?.Value(0) is not { } state)
                continue;
            if (MissionIndex.Is(state, "SUCCEED"))
                yield return $"{slot} ends with success at trigger '{trigger.Nickname}'.";
            else if (MissionIndex.Is(state, "FAIL"))
                yield return $"{slot} ends with fail at trigger '{trigger.Nickname}'.";
        }
    }

    /// <summary>The stall note of a trigger that waits for 10 minutes. A condition without effect in OpenLancer gets
    /// its own text, because the trigger never runs here.</summary>
    public static string Stall(MissionAnalysis analysis, MissionTrigger trigger) =>
        trigger.Condition?.Mark == EngineMark.NoEffect
            ? $"Trigger '{trigger.Nickname}' of {SlotOf(analysis)} waits for 10 minutes. Its condition has no effect in " +
              "OpenLancer, so it never runs here. The retail game runs it."
            : $"Trigger '{trigger.Nickname}' of {SlotOf(analysis)} waits for 10 minutes. Look in the graph for the event " +
              "that it waits for.";

    /// <summary>The origin of a stall note (decision D551 of the M6a design): the base game when no mod changes the
    /// script, else the last mod that changes it. So the base-game toggle hides the stall notes of unchanged retail
    /// missions.</summary>
    public static MessageOrigin StallOrigin(MissionScriptInfo script) =>
        script is { State: not MissionScriptState.BaseGame, ModId: { } mod }
            ? MessageOrigin.Mod(mod, ModEvidence.File)
            : MessageOrigin.BaseGame;

    /// <summary>The story slot of a script, or its file name.</summary>
    private static string SlotOf(MissionAnalysis analysis) =>
        analysis.Script.Slot?.Slot ?? MissionFiles.Display(analysis.Script.Path);
}
