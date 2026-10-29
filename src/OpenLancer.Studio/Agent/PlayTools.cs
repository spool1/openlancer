using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Data.Editing.Commands;
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

/// <summary>
/// The Play of the editor for the play tools (section 7.6 of the MCP1 design, decision D739). Only the editor host has
/// it. Each member runs on the UI thread, in the session part of a call.
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

    /// <summary>Stops the game of Play in OpenLancer when one runs, else the game of a play copy, as Shift+F5
    /// does.</summary>
    PlayStopView Stop();
}

/// <summary>
/// The tools <c>play</c> and <c>play_stop</c> of section 7.6 of the MCP1 design. Only the editor host has them. The
/// handler checks the arguments and makes the start. The Play of the editor then runs the checks of the Play menu and
/// the agent play rule.
/// </summary>
internal static class PlayTools
{
    /// <summary>The words of the argument <c>start</c>.</summary>
    public static readonly IReadOnlyList<string> Starts = ["space", "base"];

    /// <summary>The arguments of a start in space. A start at a base takes none of them.</summary>
    private static readonly string[] SpaceArguments = ["system", "position", "orientation"];

    public static ToolStep Play(ToolCall call)
    {
        var play = PlayOf(call);
        var start = StartOf(call.Args);
        // Without an open profile, play gives the error of each tool that reads the session (section 10 of the MCP1
        // design). The checks of the Play menu give the other reasons.
        _ = call.Session;
        var wait = play.Start(start);
        return ToolStep.Then(async cancel => ToolResult.Of(await wait(cancel).ConfigureAwait(false)));
    }

    public static ToolStep Stop(ToolCall call) => ToolResult.Of(PlayOf(call).Stop());

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
