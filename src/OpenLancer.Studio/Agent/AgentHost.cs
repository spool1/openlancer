using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The limits of one agent call (section 8.4 of the MCP1 design). They are settings of the editor, with these defaults
/// (decision D724).
/// </summary>
public sealed record AgentLimits
{
    /// <summary>The rows of a list result when the call gives no <c>limit</c>.</summary>
    public int ListRows { get; init; } = 50;

    /// <summary>The most rows of one list result. A larger <c>limit</c> gives the error <c>limit</c>.</summary>
    public int MaxListRows { get; init; } = 200;

    /// <summary>The most text of one result, in kilobytes. A larger result gives the error <c>limit</c>.</summary>
    public int ResultKilobytes { get; init; } = 256;

    /// <summary>The most calls that the host takes in one second. The context of the model and the frame time of the
    /// editor need it.</summary>
    public int CallsPerSecond { get; init; } = 10;

    /// <summary>How long a call waits for the thread of the session, in seconds. Then it gives <c>busy</c>.</summary>
    public int WaitSeconds { get; init; } = 30;

    /// <summary>How long the tail of a call can run, in seconds. Then it gives <c>limit</c>.</summary>
    public int TailSeconds { get; init; } = 60;

    /// <summary>The largest body of an HTTP request, in megabytes. A larger body gets 413.</summary>
    public int RequestMegabytes { get; init; } = 4;

    /// <summary>The most pixels on each side of an image of <c>render_scene</c> and <c>render_cutscene</c>. A larger
    /// width or height gives the error <c>limit</c>, because a large image takes GPU time and memory in the frame of the
    /// editor. A larger panel of <c>capture_panel</c> gives a smaller image.</summary>
    public int RenderPixels { get; init; } = 2048;

    /// <summary>The most items that one call of <c>delete_items</c> or <c>duplicate_items</c> names. A larger change is
    /// not one gesture.</summary>
    public int EditItems { get; init; } = 200;

    /// <summary>The most sections that one edit call changes, adds or removes. A call that changes more refuses with
    /// the error <c>limit</c> and changes nothing.</summary>
    public int EditSections { get; init; } = 2000;

    /// <summary>The most steps of one <c>edit_batch</c> call. A batch runs in one frame of the editor.</summary>
    public int BatchSteps { get; init; } = 100;

    /// <summary>The most legs of one route of <c>scene_place_patrol_route</c>. A retail route has at most 12
    /// legs.</summary>
    public int RouteLegs { get; init; } = 50;

    /// <summary>The allowed range of <see cref="MaxListRows"/>. <see cref="ListRows"/> goes from 1 to
    /// <see cref="MaxListRows"/>.</summary>
    public static readonly (int Min, int Max) MaxListRowsRange = (1, 1000);

    /// <summary>The allowed range of <see cref="ResultKilobytes"/>.</summary>
    public static readonly (int Min, int Max) ResultKilobytesRange = (16, 4096);

    /// <summary>The allowed range of <see cref="CallsPerSecond"/>.</summary>
    public static readonly (int Min, int Max) CallsPerSecondRange = (1, 100);

    /// <summary>The allowed range of <see cref="WaitSeconds"/>.</summary>
    public static readonly (int Min, int Max) WaitSecondsRange = (1, 600);

    /// <summary>The allowed range of <see cref="TailSeconds"/>.</summary>
    public static readonly (int Min, int Max) TailSecondsRange = (1, 3600);

    /// <summary>The allowed range of <see cref="RequestMegabytes"/>.</summary>
    public static readonly (int Min, int Max) RequestMegabytesRange = (1, 64);

    /// <summary>The allowed range of <see cref="RenderPixels"/>.</summary>
    /// <remarks>Decision D722 of the MCP1 design allows at most 2048 pixels on each side.</remarks>
    public static readonly (int Min, int Max) RenderPixelsRange = (256, 2048);

    /// <summary>The allowed range of <see cref="EditItems"/>.</summary>
    public static readonly (int Min, int Max) EditItemsRange = (1, 1000);

    /// <summary>The allowed range of <see cref="EditSections"/>.</summary>
    public static readonly (int Min, int Max) EditSectionsRange = (1, 20000);

    /// <summary>The allowed range of <see cref="BatchSteps"/>.</summary>
    public static readonly (int Min, int Max) BatchStepsRange = (1, 1000);

    /// <summary>The allowed range of <see cref="RouteLegs"/>.</summary>
    public static readonly (int Min, int Max) RouteLegsRange = (1, 500);

    /// <summary>The limits with each value in its allowed range: from the smallest useful value to the largest value
    /// that keeps the editor responsive and the result small enough for a model. The settings file can hold any
    /// value.</summary>
    public AgentLimits Clamped()
    {
        var maxRows = Clamp(MaxListRows, MaxListRowsRange);
        return new AgentLimits
        {
            ListRows = Math.Clamp(ListRows, 1, maxRows),
            MaxListRows = maxRows,
            ResultKilobytes = Clamp(ResultKilobytes, ResultKilobytesRange),
            CallsPerSecond = Clamp(CallsPerSecond, CallsPerSecondRange),
            WaitSeconds = Clamp(WaitSeconds, WaitSecondsRange),
            TailSeconds = Clamp(TailSeconds, TailSecondsRange),
            RequestMegabytes = Clamp(RequestMegabytes, RequestMegabytesRange),
            RenderPixels = Clamp(RenderPixels, RenderPixelsRange),
            EditItems = Clamp(EditItems, EditItemsRange),
            EditSections = Clamp(EditSections, EditSectionsRange),
            BatchSteps = Clamp(BatchSteps, BatchStepsRange),
            RouteLegs = Clamp(RouteLegs, RouteLegsRange),
        };
    }

    private static int Clamp(int value, (int Min, int Max) range) => Math.Clamp(value, range.Min, range.Max);
}

/// <summary>What the editor shows now: the open system, mission and cutscene, as item references.</summary>
public sealed record AgentView(string? System, string? Mission, string? Cutscene);

/// <summary>The severity of an entry of the Console (tool <c>get_console</c>).</summary>
public enum AgentSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One entry of the Console of the editor, for the tool <c>get_console</c>.</summary>
/// <param name="Sequence">The number of the entry in the order of arrival. <c>since</c> takes it.</param>
/// <param name="Ref">The item reference of the target of the entry, or null.</param>
public sealed record AgentConsoleEntry(long Sequence, DateTime Time, AgentSeverity Severity, string Source, string Message,
    string? Ref, OpenLancer.Data.OriginKind Origin);

/// <summary>The filter of <see cref="AgentHost.Console"/>.</summary>
/// <param name="Source">Only the entries of this source, without letter case, or null.</param>
/// <param name="Severity">The lowest severity.</param>
/// <param name="BaseGame">True to give the entries of the base game too (decision D519).</param>
/// <param name="Since">Only entries after this sequence number, or null.</param>
public sealed record ConsoleFilter(string? Source, AgentSeverity Severity, bool BaseGame, long? Since);

/// <summary>The problems of the target checks of a profile (see <see cref="AgentHost.CheckTarget"/>).</summary>
/// <param name="Notes">Notes of the check, for example a target game that the profile does not link.</param>
public sealed record TargetCheckResult(IReadOnlyList<Problem> Problems, IReadOnlyList<string> Notes);

/// <summary>
/// The program that serves the tools: the editor or <c>oltool mcp serve</c> (section 5.4 of the MCP1 design). The tool
/// handlers read the session and the host state through it, so a handler runs the same way in both hosts. Each member
/// is read on the thread that owns the session.
/// </summary>
public abstract class AgentHost
{
    public abstract AgentHostKind Kind { get; }

    /// <summary>The session of the open profile, or null while no profile is open.</summary>
    public abstract EditSession? Session { get; }

    /// <summary>The text of a string id of the game data, or an empty text when the id is unknown. The names of the
    /// items come from it (see <see cref="ItemTexts"/>).</summary>
    public abstract string NameText(int ids);

    /// <summary>The plain text of an infocard id of the game data, or an empty text when the id is unknown.</summary>
    public abstract string InfoText(int ids);

    /// <summary>Why the host takes no edit now, or null: the reason of the session, and the rule of the mods in the
    /// OpenLancer repo (decision D738).</summary>
    public abstract string? NoEditReason { get; }

    /// <summary>The limits of the calls.</summary>
    public abstract AgentLimits Limits { get; }

    /// <summary>The groups that the user hides (decision D725).</summary>
    public virtual IReadOnlySet<ToolGroup> HiddenGroups => new HashSet<ToolGroup>();

    /// <summary>
    /// The tools of the host, in the order of the tool table. A host that only reads has only the read-only tools
    /// (section 6.2 of the MCP1 design), and the other hosts have all tools. <c>tools/list</c> gives this set, and a
    /// call of another tool is a protocol error, as a call of an unknown tool is. The set does not change while the
    /// host runs, so each thread can read it.
    /// </summary>
    public virtual IReadOnlyList<StudioTool> Tools => AgentTools.All;

    /// <summary>Tells the host that an edit call of the agent changed the data (section 8.5 of the MCP1 design). It runs
    /// after the commit, on the thread that owns the session. A preview and a call without a change do not call
    /// it.</summary>
    /// <param name="tool">The name of the tool, for example <c>delete_items</c>.</param>
    /// <param name="result">The result of the commit, with the label of the history entry.</param>
    public virtual void Edited(string tool, EditResult result)
    {
    }

    /// <summary>Tells the host that a call of a tool that changes data gave an error, for example
    /// <c>needs_confirmation</c> (section 8.5 of the MCP1 design). A preview and a query do not call it.</summary>
    public virtual void Refused(string tool, EditError error)
    {
    }

    /// <summary>The saves of this host that changed files.</summary>
    private int saves;

    /// <summary>
    /// Saves the edits of the active mod (tool <c>save</c>, section 7.6 of the MCP1 design), on the thread that owns the
    /// session. This is the save of the host without a window: the save gate, then <see cref="EditSession.Save(bool)"/>.
    /// The editor host runs the save of the editor instead: the gate, the report in the Console, the reload and the
    /// validation.
    /// </summary>
    /// <exception cref="InvalidOperationException">A batch or a transaction is open.</exception>
    public virtual AgentSave Save()
    {
        var session = Session!;
        if (session.GateRefusal() is { } refused)
            return new AgentSave(refused, 0, Blocked: true);
        var result = session.Save();
        return new AgentSave(result, result.Saved && result.Written.Count + result.Deleted.Count > 0 ? ++saves : 0, false);
    }

    /// <summary>True while the user pauses the agent edits (decision D734).</summary>
    public virtual bool Paused => false;

    /// <summary>What the editor shows now, or null in a host without a window.</summary>
    public virtual AgentView? View => null;

    /// <summary>The views of the editor for the view tools (section 7.7 of the MCP1 design), or null in a host without a
    /// window.</summary>
    public virtual IAgentViews? Views => null;

    /// <summary>The entries of the Console that match the filter, oldest first, or null in a host without a
    /// Console.</summary>
    public virtual IReadOnlyList<AgentConsoleEntry>? Console(ConsoleFilter filter) => null;

    /// <summary>
    /// The target checks of <c>oltool validate --target</c>: all mods of the profile merged on the files of the target
    /// game, as the saved files give them. The headless host runs them in its process, and the editor host runs them
    /// in an oltool process as the tail of the call (section 7.1 of the MCP1 design). The returned function is the tail.
    /// It does not touch the session.
    /// </summary>
    /// <param name="target">The id of a retail target, for example <c>vanilla-1.1</c>.</param>
    /// <exception cref="EditException">The check cannot run. The tail can throw it too.</exception>
    public abstract Func<CancellationToken, Task<TargetCheckResult>> CheckTarget(string target);

    /// <summary>The groups that the tool table has and the user does not hide, in table order.</summary>
    public IReadOnlyList<ToolGroup> ShownGroups =>
        AgentTools.All.Select(t => t.Group).Distinct().Where(g => !HiddenGroups.Contains(g)).ToList();
}
