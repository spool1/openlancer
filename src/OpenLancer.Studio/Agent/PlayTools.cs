using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Studio.Agent;

/// <summary>The state of Play in OpenLancer, as the play tools give it (section 7.6 of the MCP1 design).</summary>
public enum AgentPlayStatus
{
    /// <summary>No game of Play in OpenLancer runs.</summary>
    Idle,

    /// <summary>The game loads its data, or it starts the play after the start command.</summary>
    Starting,

    /// <summary>The game waits for the validation of its save.</summary>
    Validating,

    /// <summary>The player is in the game.</summary>
    Running,

    /// <summary>The game loads the files of a save again and continues at the place of the player.</summary>
    Reloading,

    /// <summary>The game did not load its data or end a reload in time.</summary>
    NotResponding,

    /// <summary>The editor stopped the game and waits for its exit.</summary>
    Stopping,
}

/// <summary>What <c>play_stop</c> stopped.</summary>
public enum AgentPlayStopped
{
    /// <summary>No game ran.</summary>
    Nothing,

    /// <summary>The game of Play in OpenLancer.</summary>
    [JsonStringEnumMemberName("openlancer")]
    OpenLancer,

    /// <summary>The game of a play copy (Play in the game).</summary>
    Game,
}

/// <summary>The result of <c>play</c>.</summary>
/// <param name="Play">The state of Play when the request ended.</param>
public sealed record PlayRequestView(AgentPlayStatus Play);

/// <summary>The result of <c>play_stop</c>.</summary>
/// <param name="Stopped">The game that the stop stopped, or nothing.</param>
/// <param name="Play">The state of Play after the stop.</param>
public sealed record PlayStopView(AgentPlayStopped Stopped, AgentPlayStatus Play);

/// <summary>The result of <c>mission_play</c>: the state of Play and the start that the game plays.</summary>
/// <param name="Play">The state of Play when the request ended.</param>
/// <param name="Script">The <c>msn:</c> reference of the script.</param>
/// <param name="Start"><c>mission_start</c>, or the <c>trg:</c> reference of the trigger of the start. When the saved
/// script does not have the trigger of the request, the game plays the mission start.</param>
/// <param name="Kind">The kind of the start of the plan of the play.</param>
/// <param name="StartName">The name of the start in the editor, for example "Checkpoint my_restore".</param>
public sealed record MissionPlayView(AgentPlayStatus Play, string Script, string Start, StartKind Kind,
    string StartName)
{
    /// <summary>The warnings of the plan of the play.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The result of <c>play_capture</c>, beside its image.</summary>
/// <param name="Width">The width of the image in pixels.</param>
/// <param name="Height">The height of the image in pixels.</param>
/// <param name="Play">The state of Play in OpenLancer when the game gave the image.</param>
public sealed record PlayCaptureView(int Width, int Height, AgentPlayStatus Play);

/// <summary>A change of a trigger in the live trace of a mission play.</summary>
/// <param name="Sequence">The number of the change in its play, from 1.</param>
/// <param name="Time">The time of the change since the game loaded the mission, in seconds.</param>
/// <param name="Trigger">The <c>trg:</c> reference of the trigger, or its nickname when the editor does not know the
/// script of the mission.</param>
public sealed record MissionTraceChange(long Sequence, double Time, string Trigger, MissionChangeKind Change);

/// <summary>The result of <c>mission_play_trace</c>: one page of the live trace of the mission of the OpenLancer game
/// (section 7.4 of the MCP1 design).</summary>
/// <param name="Play">The number of the play of the changes, or 0 before the first mission event.</param>
/// <param name="Changes">The changes of the play after the sequence number of the request, oldest first.</param>
/// <param name="More">True when the play has more changes than the page gives.</param>
/// <param name="Status">The state of Play in OpenLancer.</param>
public sealed record MissionTraceView(int Play, IReadOnlyList<MissionTraceChange> Changes, bool More,
    AgentPlayStatus Status)
{
    /// <summary>The name of the mission of the game, for example <c>Mission_02</c>, or null.</summary>
    public string? Mission { get; init; }

    /// <summary>The <c>msn:</c> reference of the script of the mission, or null.</summary>
    public string? Script { get; init; }

    /// <summary>The changes that the game dropped in the play because its queue was full.</summary>
    public int Dropped { get; init; }

    /// <summary>True while the game of a play copy runs. That game has no channel to the editor.</summary>
    public bool GameRuns { get; init; }
}

/// <summary>
/// The Play of the editor for the play tools (section 7.6 of the MCP1 design, decision D739) and the mission play tools
/// of section 7.4. Only the editor host has it. Each member runs on the UI thread, in the session part of a call.
/// </summary>
public interface IAgentPlay
{
    /// <summary>
    /// Asks for a play at a start, with the checks of the Play menu and the agent play rule (user decision of
    /// 2026-10-20 of the MCP1 design). A play request of an agent never saves on its own. With unsaved edits, the editor
    /// asks the user first, unless the user turned on the agent setting of the automatic save.
    /// </summary>
    /// <returns>The wait for the result of the request, as the tail of the call. A cancel of the wait closes an open
    /// question of the user as Cancel does, so a later click cannot start the play. The wait throws an
    /// <see cref="EditException"/> when the play did not start.</returns>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/> or <see cref="EditErrorKind.Busy"/>: the
    /// play cannot start now, for example because another play runs.</exception>
    Func<CancellationToken, Task<PlayRequestView>> Start(PlayStart start);

    /// <summary>
    /// Asks for a mission play of a start of a script (the mission play request of section 7.2 of the M6c design), with
    /// the agent play rule. The request plays the start of the request and never changes the play start of the user.
    /// While a mission play runs, the game starts again at the new start.
    /// </summary>
    /// <returns>The wait for the result of the request, as for <see cref="Start"/>.</returns>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/>, <see cref="EditErrorKind.Busy"/> or
    /// <see cref="EditErrorKind.NotFound"/>: the play cannot start, for example because another play runs or the plan of
    /// the start refuses.</exception>
    Func<CancellationToken, Task<MissionPlayView>> StartMission(StartPlanRequest request);

    /// <summary>One page of the live trace of the mission of the OpenLancer game: the changes of the current play after
    /// a sequence number. A request with another play number gets the changes of the current play from its
    /// start.</summary>
    /// <param name="play">The play number of the last result of the caller, or 0.</param>
    /// <param name="since">The last sequence number that the caller has, or 0.</param>
    /// <param name="limit">The most changes of the page.</param>
    MissionTraceView Trace(int play, long since, int limit);

    /// <summary>
    /// Asks the game of Play in OpenLancer for an image of a frame after a delay (shared step SH9). The image has at most
    /// the size of the arguments and keeps the shape of the game window.
    /// </summary>
    /// <returns>The wait for the image, as the tail of the call. It throws an <see cref="EditException"/> when the game
    /// gives no image.</returns>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/>: no game of Play in OpenLancer runs, also
    /// while only the game of a play copy runs.</exception>
    Func<CancellationToken, Task<(PlayCaptureView View, ToolImage Image)>> Capture(TimeSpan delay, int maxWidth,
        int maxHeight);

    /// <summary>Stops the game of Play in OpenLancer when one runs, else the game of a play copy, as Shift+F5
    /// does.</summary>
    PlayStopView Stop();
}

/// <summary>
/// The tools <c>play</c> and <c>play_stop</c> of section 7.6 of the MCP1 design, <c>mission_play</c> and
/// <c>mission_play_trace</c> of section 7.4, and <c>play_capture</c> (shared step SH9). Only the editor host has them.
/// The handler checks the arguments and makes the start. The Play of the editor then runs the checks of the Play menu or
/// of the mission play request, and the agent play rule.
/// </summary>
internal static class PlayTools
{
    /// <summary>The words of the argument <c>start</c>.</summary>
    public static readonly IReadOnlyList<string> Starts = ["space", "base"];

    /// <summary>The most seconds of the delay of <c>play_capture</c>.</summary>
    public const int MaxCaptureDelay = 60;

    /// <summary>The arguments of a start in space. A start at a base takes none of them.</summary>
    private static readonly string[] SpaceArguments = ["system", "position", "orientation"];

    public static ToolStep Play(ToolCall call)
    {
        var play = PlayOf(call);
        var start = StartOf(call.Args);
        // Without an open profile, play gives the error of each tool that reads the session (section 10 of the MCP1
        // design). The checks of the Play menu give the other reasons.
        _ = call.Session;
        // The checks of the Play menu read the loaded game data. A system or a base of the last save waits for the reload.
        if (start is PlayStart.AtBase dock)
            ViewTools.RefuseWhileReloading(call, ItemRefKind.Base, dock.Base, "base");
        else if (start is PlayStart.InSpace space)
            ViewTools.RefuseWhileReloading(call, ItemRefKind.System, space.System, "system");
        var wait = play.Start(start);
        return ToolStep.Then(async cancel => ToolResult.Of(await wait(cancel).ConfigureAwait(false)));
    }

    public static ToolStep Stop(ToolCall call) => ToolResult.Of(PlayOf(call).Stop());

    /// <summary>An image of the game of Play in OpenLancer after <c>delay</c> seconds. The image has at most the size of
    /// <c>width</c> and <c>height</c>, by default the render limit, and keeps the shape of the game window.</summary>
    public static ToolStep Capture(ToolCall call)
    {
        var play = PlayOf(call);
        var (width, height) = ViewTools.Size(call);
        var delay = TimeSpan.FromSeconds(call.Args.Number("delay") ?? 0);
        return ViewTools.Image(play.Capture(delay, width, height));
    }

    /// <summary>
    /// The mission play of an agent. The reader of <c>mission_start_plan</c> reads the start, so one request plans and
    /// plays the same start. The tool is in the group Play, and its script is an item of the group Missions. So it also
    /// refuses while the user hides the group Missions (the item rule of decision D725).
    /// </summary>
    public static ToolStep MissionPlay(ToolCall call)
    {
        var play = PlayOf(call);
        var request = MissionQueryTools.StartRequest(call.Args);
        _ = call.Session;
        if (call.Host.HiddenGroups.Contains(ToolGroup.Missions))
        {
            throw new EditException(AgentTools.GroupHidden(ToolGroup.Missions) with
            {
                Target = new ScriptRef(request.Script).ToString(),
            });
        }
        var wait = play.StartMission(request);
        return ToolStep.Then(async cancel => ToolResult.Of(await wait(cancel).ConfigureAwait(false)));
    }

    /// <summary>One page of the live trace. The page has the default rows of the limits, or the rows of
    /// <c>limit</c>.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: <c>since</c> without
    /// <c>play</c>.</exception>
    public static ToolStep MissionTrace(ToolCall call)
    {
        var args = call.Args;
        var number = (int)Math.Min(args.Whole("play") ?? 0, int.MaxValue);
        var since = args.Whole("since") ?? 0;
        // A sequence number belongs to one play. The live trace gives all changes of the current play for another play
        // number, so a page call without play would give the first page again.
        if (since > 0 && number == 0)
            throw ToolInput.Bad("since", AgentTexts.Error("trace_since_play"));
        return ToolResult.Of(PlayOf(call).Trace(number, since, call.RowLimit()));
    }

    /// <summary>The start of the arguments: a point with a heading in a system, or a base.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: an argument of the other kind of
    /// start, a missing argument, or a reference of another kind.</exception>
    private static PlayStart StartOf(ToolArgs args)
    {
        if (args.Text("start") == "base")
        {
            foreach (var name in SpaceArguments)
            {
                if (args.Has(name))
                    throw ToolInput.Bad(name, AgentTexts.Error("play_space_only", ("name", name)));
            }
            return new PlayStart.AtBase(Nickname(args, "base", ItemRefKind.Base));
        }
        if (args.Has("base"))
            throw ToolInput.Bad("base", AgentTexts.Error("play_base_only"));
        var system = Nickname(args, "system", ItemRefKind.System);
        var position = args.Point("position") ?? throw Missing("position");
        // The heading is in degrees, as the rotate key of an object holds it. The ship flies along its -Z axis.
        var orientation = MathHelper.MatrixFromEulerDegrees(args.Point("orientation") ?? Vector3.Zero).ExtractRotation();
        return new PlayStart.InSpace(system, position, orientation);
    }

    /// <summary>The nickname of an argument that takes a nickname or a reference of one kind.</summary>
    private static string Nickname(ToolArgs args, string name, ItemRefKind kind)
    {
        var text = args.Text(name) ?? throw Missing(name);
        // No nickname holds a ':' (section 5.6 of the MCP1 design), so a text with one is a reference.
        if (!text.Contains(':'))
            return text;
        var reference = ItemRefs.Parse(text);
        return reference.Kind == kind
            ? reference.Name
            : throw ToolInput.Bad(name, AgentTexts.Error("play_ref", ("name", name), ("ref", text)));
    }

    private static EditException Missing(string name) =>
        ToolInput.Bad(name, AgentTexts.Error("argument_missing", ("name", name)));

    /// <summary>The Play of the editor. The tool table gives <c>not_available</c> in a host without a window, before
    /// the handler runs.</summary>
    private static IAgentPlay PlayOf(ToolCall call) => call.Host.Play ?? throw new EditException(new EditError(
        EditErrorKind.NotAvailable, "EditorOnly", AgentTexts.Error("editor_only", ("tool", call.Tool))));
}
