using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;

namespace OpenLancer.Data.Missions;

/// <summary>A lens of the mission graph (section 6.5 of the M6a design). A lens filters and groups the nodes. It does
/// not change data, and it does not change the order of the nodes.</summary>
public enum GraphLens
{
    /// <summary>All triggers, with the prefab frames collapsed. The default lens.</summary>
    Beats,

    /// <summary>All triggers, with all frames open.</summary>
    Logic,

    Battle,
    Conversation,
    Cutscene,
    Objectives,
}

/// <summary>What a node of the graph stands for.</summary>
public enum GraphNodeKind
{
    /// <summary>One trigger.</summary>
    Trigger,

    /// <summary>A collapsed prefab frame.</summary>
    Prefab,

    /// <summary>A collapsed act frame, or the collapsed frame "Not started by the mission".</summary>
    Act,

    /// <summary>A trigger name that the script does not have.</summary>
    Missing,
}

/// <summary>The kind of a frame of the graph (section 6.4 of the M6a design).</summary>
public enum GraphFrameKind
{
    Act,

    /// <summary>The frame "Not started by the mission".</summary>
    NotStarted,

    Prefab,
}

/// <summary>The kind of a pin of a node (section 6.2 of the M6a design).</summary>
public enum GraphPinKind
{
    // Input pins, on the left.
    Start,
    Stop,
    Load,

    /// <summary>The input of a trigger whose condition watches another trigger.</summary>
    Watches,

    // Output pins, on the right.
    /// <summary>The pin of one line with a trigger value, at the lines level.</summary>
    Row,

    /// <summary>The pin of the "Starts" chip: all start lines of the node.</summary>
    Starts,

    /// <summary>The pin of the "Stops" chip: all stop lines of the node.</summary>
    Stops,

    /// <summary>The pin of the "Checkpoint" chip: all checkpoint lines of the node.</summary>
    Saves,

    /// <summary>The state of a trigger that a condition watches, in the header.</summary>
    State,

    /// <summary>The pin of the row "Show {n} more lines": the lines with a trigger value that the limit of rows
    /// hides.</summary>
    More,
}

/// <summary>The detail of a node in one frame of the graph view (decision D554).</summary>
public enum GraphDetail
{
    /// <summary>The full node of its level.</summary>
    Full,

    /// <summary>The header only, at the full node size, because the zoom is below 60 percent.</summary>
    Header,

    /// <summary>An empty box of the full node size with its pins, because the node is outside the view.</summary>
    Stub,
}

/// <summary>One pin of a node.</summary>
/// <param name="Id">The id of the pin in the node editor.</param>
/// <param name="Slot">The line of the node where the pin sits: 0 is the header, 1 is the first body line.</param>
/// <param name="Line">The line of a <see cref="GraphPinKind.Row"/> pin, or null.</param>
public sealed record GraphPin(int Id, GraphPinKind Kind, int Slot, MissionLine? Line = null)
{
    /// <summary>True for an input pin, on the left of the node.</summary>
    public bool IsInput => Kind is GraphPinKind.Start or GraphPinKind.Stop or GraphPinKind.Load or GraphPinKind.Watches;

    /// <summary>The tooltip of the pin (section 13.1 of the M6a design).</summary>
    public string Tooltip => Kind switch
    {
        GraphPinKind.Start => "Start: triggers that start this trigger.",
        GraphPinKind.Stop => "Stop: triggers that stop it.",
        GraphPinKind.Load => "Load: checkpoints that continue at it after a load.",
        GraphPinKind.Watches => "Watches: the trigger whose state the condition of this trigger waits for.",
        GraphPinKind.State => "State: conditions that watch the state of this trigger.",
        GraphPinKind.Starts => "The triggers that this trigger starts.",
        GraphPinKind.Stops => "The triggers that this trigger stops.",
        GraphPinKind.Saves => "The trigger where the mission continues after a load.",
        GraphPinKind.More => "The lines that the row \"Show more lines\" hides.",
        _ => "The trigger that this line names.",
    };
}

/// <summary>The kind of a row of a node at the lines level.</summary>
public enum GraphRowKind
{
    /// <summary>A line before the nickname. The game ignores it (TR2).</summary>
    BeforeNickname,

    /// <summary>The condition that the game uses.</summary>
    Condition,

    /// <summary>A condition before the last one. The game ignores it (TR1).</summary>
    IgnoredCondition,

    Action,
}

/// <summary>A chip of a row that names another item of the mission (section 6.2 of the M6a design).</summary>
/// <param name="Kind">The kind of the item, for example "Ship" or "Space cutscene".</param>
/// <param name="Section">The section of the item, or null for a label, a file or an object of a system.</param>
public sealed record GraphRowLink(string Kind, string Name, MissionSection? Section)
{
    /// <summary>The text of the chip, for example "Ship pirate_1".</summary>
    public string Text => $"{Kind} {Name}";
}

/// <summary>One row of a node at the lines level.</summary>
/// <param name="Text">The plain name and the values as short text.</param>
/// <param name="Mark">The OpenLancer mark, for example "OpenLancer: no effect", or null.</param>
/// <param name="Problems">The problems of the line that show.</param>
public sealed record GraphRow(GraphRowKind Kind, MissionLine Line, string Text, string? Mark, GraphPin? Pin,
    IReadOnlyList<GraphRowLink> Links, IReadOnlyList<Diagnostic> Problems)
{
    /// <summary>The tooltip of the OpenLancer mark, or null without a mark.</summary>
    public string? MarkTip => Mark != null ? OpenLancerGap.MarkTooltip(Line.Mark) : null;
}

/// <summary>A chip of the summary level: a group of actions with its count, or a flow chip of a line.</summary>
/// <param name="Group">The id of the action group, or null for a flow chip.</param>
/// <param name="Tooltip">One line for each action line: the plain name and the targets.</param>
/// <param name="Warning">True for a chip with the warning glyph, for example "Starts and stops".</param>
public sealed record GraphChip(string? Group, string Text, string Tooltip, bool Warning = false, GraphPin? Pin = null);

/// <summary>A block of the graph: a node, or a frame with its blocks.</summary>
public abstract class GraphBlock
{
    private protected GraphBlock(string key, int id, int order)
    {
        Key = key;
        Id = id;
        Order = order;
    }

    /// <summary>A stable key of the block, unique in the graph, for example <c>t:12</c>.</summary>
    public string Key { get; }

    /// <summary>The id of the node in the node editor.</summary>
    public int Id { get; }

    /// <summary>The place of the block in file order: the lowest section index of its triggers.</summary>
    public int Order { get; }

    /// <summary>The frame that holds the block, or null for a top block.</summary>
    public GraphFrame? Parent { get; internal set; }

    public override string ToString() => Key;
}

/// <summary>
/// One node of the graph (section 6.2 of the M6a design): a trigger, a collapsed frame or a missing trigger. The node has
/// a fixed width and a height from its line count, so the layout needs no text measure.
/// </summary>
public sealed class GraphNode : GraphBlock
{
    private readonly List<GraphPin> pins = [];

    internal GraphNode(string key, int id, int order, GraphNodeKind kind, string title) : base(key, id, order)
    {
        Kind = kind;
        Title = title;
    }

    public GraphNodeKind Kind { get; }

    /// <summary>The nickname of a trigger, the name of a collapsed frame, or "Missing: {name}".</summary>
    public string Title { get; }

    /// <summary>The trigger name of a missing node, as the first line that names it writes it, or null.</summary>
    public string? MissingName { get; internal init; }

    /// <summary>The trigger of a trigger node, or null.</summary>
    public MissionTrigger? Trigger { get; internal init; }

    /// <summary>The frame that a collapsed node stands for, or null.</summary>
    public GraphFrame? Frame { get; internal init; }

    /// <summary>The triggers that the node stands for: one for a trigger node, the triggers of a collapsed frame.</summary>
    public IReadOnlyList<MissionTrigger> Triggers { get; internal init; } = [];

    /// <summary>True for a node at the lines level.</summary>
    public bool Lines { get; internal init; }

    public Vector2 Size { get; internal set; }

    /// <summary>The id of the event group of the condition, for example <c>combat</c>, or null.</summary>
    public string? EventGroup { get; internal init; }

    /// <summary>The event sentence of the condition, the walkthrough line of an act, or the text of a missing
    /// node.</summary>
    public string Sentence { get; internal init; } = "";

    /// <summary>The chips of the header: "Start", "Repeats" and the system of the trigger.</summary>
    public IReadOnlyList<string> HeaderChips { get; internal init; } = [];

    /// <summary>True when nothing starts the trigger, and it is not a start trigger or a checkpoint target.</summary>
    public bool NeverStarts { get; internal init; }

    /// <summary>True for a trigger with a condition and no actions.</summary>
    public bool WatchedState { get; internal init; }

    /// <summary>The problems of the node that show, in file order.</summary>
    public IReadOnlyList<Diagnostic> Problems { get; internal init; } = [];

    public int Errors => Problems.Count(p => p.Severity == DiagnosticSeverity.Error && p.Origin?.IsBaseGame != true);

    public int Warnings => Problems.Count(p => p.Severity == DiagnosticSeverity.Warning && p.Origin?.IsBaseGame != true);

    /// <summary>The base-game problems that show muted, with the lock glyph.</summary>
    public int Muted => Problems.Count(p => p.Origin?.IsBaseGame == true);

    /// <summary>The chips of the summary level, in group order, then the flow chips.</summary>
    public IReadOnlyList<GraphChip> Chips { get; internal init; } = [];

    /// <summary>The rows of the lines level that show.</summary>
    public IReadOnlyList<GraphRow> Rows { get; internal init; } = [];

    /// <summary>The number of rows that the limit of 20 rows hides. The last row then says "Show {n} more
    /// lines".</summary>
    public int HiddenRows { get; internal init; }

    /// <summary>The third line of a summary node with an OpenLancer mark, or null.</summary>
    public string? TargetLine { get; internal init; }

    /// <summary>The named fields of a collapsed prefab in short form, for example "Ships: 3".</summary>
    public IReadOnlyList<string> Fields { get; internal init; } = [];

    /// <summary>The tooltip of each field of <see cref="Fields"/>: its label and each value on its own line (section
    /// 6.4 of the M6a design).</summary>
    public IReadOnlyList<string> FieldTips { get; internal init; } = [];

    /// <summary>The completeness line of a collapsed prefab with a missing step, or null.</summary>
    public string? Completeness { get; internal init; }

    /// <summary>True when the lens fades the node to 25 percent opacity.</summary>
    public bool Faded { get; internal set; }

    /// <summary>The input and output pins of the node.</summary>
    public IReadOnlyList<GraphPin> Pins => pins;

    /// <summary>The pin of a kind, or null.</summary>
    public GraphPin? Pin(GraphPinKind kind) => pins.FirstOrDefault(p => p.Kind == kind);

    /// <summary>The number of body lines of the node.</summary>
    public int BodyLines { get; internal set; }

    internal void Add(GraphPin pin) => pins.Add(pin);
}

/// <summary>A frame of the graph: an act, the frame "Not started by the mission", or a prefab instance (section 6.4 of the
/// M6a design).</summary>
public sealed class GraphFrame : GraphBlock
{
    private readonly List<GraphBlock> blocks = [];

    internal GraphFrame(string key, int id, int order, GraphFrameKind kind, string title, string detail)
        : base(key, id, order)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
    }

    public GraphFrameKind Kind { get; }

    /// <summary>The title, for example "Act 2: New York" or "Spawn wave".</summary>
    public string Title { get; }

    /// <summary>The line under the title: the walkthrough line of an act, or the name of a prefab instance.</summary>
    public string Detail { get; }

    /// <summary>The act of an act frame, or null.</summary>
    public MissionAct? Act { get; internal init; }

    /// <summary>The prefab instance of a prefab frame, or null.</summary>
    public MissionPrefabInstance? Instance { get; internal init; }

    /// <summary>True when the frame shows as one node (<see cref="Node"/>).</summary>
    public bool Collapsed { get; internal set; }

    /// <summary>The node of the collapsed frame. The graph makes it also for an open frame, so the links can find
    /// it.</summary>
    public GraphNode Node { get; internal set; } = null!;

    /// <summary>The blocks in the frame, in file order.</summary>
    public IReadOnlyList<GraphBlock> Blocks => blocks;

    /// <summary>The triggers in the frame and in its prefab frames, in file order.</summary>
    public IReadOnlyList<MissionTrigger> Triggers { get; internal set; } = [];

    internal void Add(GraphBlock block)
    {
        block.Parent = this;
        blocks.Add(block);
    }
}

/// <summary>A link of the graph between an output pin and an input pin (section 6.3 of the M6a design).</summary>
/// <param name="Sources">The lines of the links that the link stands for. A summary pin or a collapsed node stands for
/// several lines.</param>
/// <param name="State">The chip at the target pin of a watch link, for example "Is active", or null.</param>
public sealed record GraphLink(int Id, MissionLinkKind Kind, GraphNode From, GraphPin FromPin, GraphNode To,
    GraphPin ToPin, IReadOnlyList<MissionLink> Sources, string? State);

/// <summary>One item of the side list of the Cutscene and Objectives lenses: a line in path order from the start
/// trigger.</summary>
/// <param name="TextIds">The text ids of the line, and of the objective that the line names. A view shows their
/// texts, for example the title or the log line.</param>
public sealed record GraphSideItem(string Text, MissionTrigger Trigger, MissionLine Line, IReadOnlyList<string> TextIds);

/// <summary>The stable ids of the node editor for the keys of a graph. A view keeps one map for each mission, so a node
/// keeps its id when the graph is built again.</summary>
public sealed class GraphIds
{
    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);

    /// <summary>The id of a key. A new key gets the next id, from 1.</summary>
    public int Of(string key)
    {
        if (!ids.TryGetValue(key, out var id))
            ids[key] = id = ids.Count + 1;
        return id;
    }
}

/// <summary>What the graph shows (sections 6.1 and 6.5 of the M6a design).</summary>
/// <param name="ShowLines">True for the lines level of each trigger node ("Show lines").</param>
/// <param name="Collapse">The frames that the user collapsed or opened, by frame key. A frame without an entry has the
/// default of the lens.</param>
/// <param name="LinesOf">The trigger nodes that show their lines also at the summary level, by node key.</param>
/// <param name="AllRowsOf">The trigger nodes that show all rows, without the limit of 20 rows, by node key.</param>
/// <param name="ShowBaseGame">True while the base-game messages show.</param>
/// <param name="HidesOpenLancer">True when the active mod has a targets list without OpenLancer. The OpenLancer marks
/// then do not show (UI rule 4).</param>
/// <param name="SystemName">Gives the name of a system for the act titles, or null.</param>
public sealed record GraphOptions(GraphLens Lens = GraphLens.Beats, bool ShowLines = false,
    IReadOnlyDictionary<string, bool>? Collapse = null, IReadOnlySet<string>? LinesOf = null,
    IReadOnlySet<string>? AllRowsOf = null, bool ShowBaseGame = false, bool HidesOpenLancer = false,
    Func<string, string?>? SystemName = null, GraphIds? Ids = null)
{
    /// <summary>The acts of the graph file (decision D679), or null for the acts of the analysis.</summary>
    public MissionActs? Acts { get; init; }
}

/// <summary>
/// The model of the mission graph (section 6 of the M6a design). It has one node for each trigger, the act frames, the
/// prefab frames and the frame "Not started by the mission". It also has the pins, the links and the lens. The model
/// makes no ImGui call and reads no window, so the tests and later an agent server (roadmap row MCP1) read it. The
/// graph view draws it, and <see cref="GraphLayout"/> places it.
/// </summary>
public sealed class MissionGraph
{
    /// <summary>The width of a node at the summary level, in points.</summary>
    public const float SummaryWidth = 260;

    /// <summary>The width of a node at the lines level, in points.</summary>
    public const float LinesWidth = 340;

    /// <summary>The width of a missing node, in points.</summary>
    public const float MissingWidth = 200;

    /// <summary>The height of the header of a node, in points.</summary>
    public const float HeaderHeight = 26;

    /// <summary>The height of one body line of a node, in points.</summary>
    public const float LineHeight = 20;

    /// <summary>The space under the last line of a node, in points.</summary>
    public const float BottomPadding = 6;

    /// <summary>A node shows at most this many rows. The last row then opens all rows.</summary>
    public const int MaxRows = 20;

    /// <summary>The opacity of a faded node.</summary>
    public const float FadeAlpha = 0.25f;

    /// <summary>The prefab frames that stay open in the Battle lens.</summary>
    private static readonly HashSet<string> BattlePrefabs = ["spawnWave", "ambush", "destroyTargets", "escort", "patrol"];

    private static readonly HashSet<string> CutsceneActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Act_CallThorn", "Act_StaticCam", "Act_RevertCam", "Act_AddRTC", "Act_RemoveRTC", "Act_AddAmbient",
        "Act_RemoveAmbient",
    };

    /// <summary>The lines of the side list of the Cutscene lens: the space cutscenes and the base scenes.</summary>
    private static readonly HashSet<string> CutsceneListActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Act_CallThorn", "Act_AddRTC",
    };

    private readonly Dictionary<MissionTrigger, GraphNode> triggerNodes = [];
    private readonly Dictionary<string, GraphBlock> byKey = new(StringComparer.Ordinal);
    private readonly List<GraphNode> nodes = [];
    private readonly List<GraphFrame> frames = [];
    private readonly List<GraphLink> links = [];

    private MissionGraph(MissionAnalysis analysis, GraphOptions options)
    {
        Analysis = analysis;
        Options = options;
    }

    public MissionAnalysis Analysis { get; }

    public GraphOptions Options { get; }

    public MissionIndex Index => Analysis.Index;

    /// <summary>The top frames: the acts in act order, then the frame "Not started by the mission".</summary>
    public IReadOnlyList<GraphFrame> Acts { get; private set; } = [];

    /// <summary>All frames, the open and the collapsed ones.</summary>
    public IReadOnlyList<GraphFrame> Frames => frames;

    /// <summary>The nodes that show: the trigger nodes outside collapsed frames, the nodes of the collapsed frames and
    /// the missing nodes.</summary>
    public IReadOnlyList<GraphNode> Nodes => nodes;

    /// <summary>The links between the nodes that show.</summary>
    public IReadOnlyList<GraphLink> Links => links;

    /// <summary>The lines of the side list of the Cutscene or Objectives lens, in path order from the start trigger.
    /// Empty in the other lenses.</summary>
    public IReadOnlyList<GraphSideItem> SideList { get; private set; } = [];

    /// <summary>The block of a key, or null.</summary>
    public GraphBlock? Block(string key) => byKey.GetValueOrDefault(key);

    /// <summary>The node that shows a trigger: its trigger node, or the node of the collapsed frame that holds it. Null
    /// for a trigger that is not in the graph.</summary>
    public GraphNode? NodeOf(MissionTrigger trigger)
    {
        if (!triggerNodes.TryGetValue(trigger, out var node))
            return null;
        GraphNode shown = node;
        for (var frame = node.Parent; frame != null; frame = frame.Parent)
        {
            if (frame.Collapsed)
                shown = frame.Node;
        }
        return shown;
    }

    /// <summary>The trigger node of a trigger, also inside a collapsed frame, or null.</summary>
    public GraphNode? TriggerNode(MissionTrigger trigger) => triggerNodes.GetValueOrDefault(trigger);

    /// <summary>The key of the node of a trigger.</summary>
    public static string KeyOf(MissionTrigger trigger) => "t:" + trigger.Section.Index.ToString(CultureInfo.InvariantCulture);

    /// <summary>True when a frame starts collapsed in a lens (sections 6.4 and 6.5 of the M6a design).</summary>
    public static bool DefaultCollapsed(GraphFrameKind kind, string? prefab, GraphLens lens) => kind switch
    {
        GraphFrameKind.NotStarted => lens != GraphLens.Logic,
        GraphFrameKind.Act => false,
        _ => lens switch
        {
            GraphLens.Logic => false,
            GraphLens.Battle => prefab == null || !BattlePrefabs.Contains(prefab),
            GraphLens.Conversation => prefab != "conversation",
            _ => true,
        },
    };

    /// <summary>
    /// The detail of a node in a frame of the view (decision D554). A node in the view is the full node at a zoom of 60
    /// percent or more, and the header only below that zoom. A node outside the view is a stub.
    /// </summary>
    /// <param name="scale">The zoom of the view: 1 at 100 percent.</param>
    public static GraphDetail DetailOf(Vector2 min, Vector2 max, Vector2 viewMin, Vector2 viewMax, float scale)
    {
        var inView = min.X <= viewMax.X && max.X >= viewMin.X && min.Y <= viewMax.Y && max.Y >= viewMin.Y;
        if (!inView)
            return GraphDetail.Stub;
        return scale >= 0.6f ? GraphDetail.Full : GraphDetail.Header;
    }

    /// <summary>The text of the state chip of a watch link (section 13.2 of the M6a design).</summary>
    public static string? StateText(string? state) => state?.Trim().ToUpperInvariant() switch
    {
        "ON" or "ACTIVE" => "Is active",
        "OFF" => "Is not active",
        "INACTIVE" => "Is inactive",
        "COMPLETE" => "Has run",
        _ => null,
    };

    /// <summary>The acts of the graph: the acts of the options, of the analysis, or the proposed acts.</summary>
    public MissionActs ActsOf() => Options.Acts ?? Analysis.Acts ?? MissionActs.Propose(Index);

    /// <summary>Builds the graph of an analysis.</summary>
    public static MissionGraph Build(MissionAnalysis analysis, GraphOptions? options = null)
    {
        var graph = new MissionGraph(analysis, options ?? new GraphOptions());
        graph.ids = graph.Options.Ids ?? new GraphIds();
        graph.problems = BaseGameMessages.Shown(analysis.Problems, graph.Options.ShowBaseGame)
            .Where(p => p.Target != null).GroupBy(p => p.Target!).ToDictionary(g => g.Key, g => g.ToList());
        graph.BuildFrames();
        graph.BuildNodes();
        graph.BuildLinks();
        graph.ApplyLens();
        graph.SideList = graph.BuildSideList();
        return graph;
    }

    private GraphIds ids = null!;

    // The problems that show, by section.
    private Dictionary<SectionRef, List<Diagnostic>> problems = [];

    private int Id(string key) => ids.Of(key);

    /// <summary>The frames and the trigger nodes in them.</summary>
    private void BuildFrames()
    {
        var acts = ActsOf();
        var triggers = Index.Triggers;
        var top = new List<GraphFrame>();
        var actFrames = new Dictionary<MissionAct, GraphFrame>();
        foreach (var act in acts.Acts)
        {
            var key = MissionActs.KeyOf(act);
            var frame = new GraphFrame(key, Id(key), act.Triggers.Count > 0 ? act.Triggers.Min(t => t.Section.Index) : 0,
                GraphFrameKind.Act, ActTitle(act, Options.SystemName), Walkthrough(act.Triggers.Count, act.Checkpoints))
            {
                Act = act,
            };
            actFrames[act] = frame;
            top.Add(frame);
        }
        // The game never runs a duplicate trigger (TR4), so it is in the frame "Not started by the mission" too.
        var notStarted = acts.NotStarted;
        var notStartedFrame = new GraphFrame(MissionActs.NotStartedKey, Id(MissionActs.NotStartedKey), int.MaxValue,
            GraphFrameKind.NotStarted, NotStartedTitle(notStarted.Count),
            "No start trigger and no checkpoint leads to these triggers.");
        if (notStarted.Count > 0)
            top.Add(notStartedFrame);

        GraphFrame Home(MissionTrigger trigger) => acts.ActOf(trigger) is { } act ? actFrames[act] : notStartedFrame;

        // A prefab frame holds the triggers that its instance claims. It is in the frame of its core trigger, or of
        // its first claimed trigger.
        var claimedBy = new Dictionary<MissionTrigger, GraphFrame>();
        var prefabFrames = new List<(GraphFrame Home, GraphFrame Frame)>();
        foreach (var instance in Analysis.Prefabs?.Instances ?? [])
        {
            if (instance.Claimed.Count == 0)
                continue;
            var anchor = instance.Core is { } core && instance.Claimed.Contains(core) ? core : instance.Claimed[0];
            var key = "p:" + instance.Key;
            // An instance of a prefab that no mod has shows "Missing prefab", as Unity does (decision D673).
            var title = instance.Linked is { Asset: null } ? $"{MissingPrefab}: {instance.Definition.Name}" : instance.Definition.Name;
            var frame = new GraphFrame(key, Id(key), instance.Claimed.Min(t => t.Section.Index), GraphFrameKind.Prefab,
                title, instance.Name) { Instance = instance };
            prefabFrames.Add((Home(anchor), frame));
            foreach (var trigger in instance.Claimed)
                claimedBy.TryAdd(trigger, frame);
        }

        // The blocks of each frame in file order: the prefab frames and the plain triggers.
        frames.AddRange(top);
        frames.AddRange(prefabFrames.Select(p => p.Frame));
        var blocks = frames.ToDictionary(f => f, _ => new List<GraphBlock>());
        foreach (var (home, frame) in prefabFrames)
            blocks[home].Add(frame);
        foreach (var trigger in triggers)
        {
            var node = TriggerNode(trigger, problems.GetValueOrDefault(trigger.Section.Ref) ?? []);
            triggerNodes[trigger] = node;
            byKey[node.Key] = node;
            blocks[claimedBy.TryGetValue(trigger, out var prefab) ? prefab : Home(trigger)].Add(node);
        }
        foreach (var frame in frames)
        {
            foreach (var block in blocks[frame].OrderBy(b => b.Order))
                frame.Add(block);
            byKey[frame.Key] = frame;
        }
        foreach (var frame in frames)
            frame.Triggers = Descendants(frame).OfType<GraphNode>().Select(n => n.Trigger).OfType<MissionTrigger>()
                .OrderBy(t => t.Section.Index).ToList();
        foreach (var frame in frames)
        {
            var collapsed = Options.Collapse?.TryGetValue(frame.Key, out var value) == true
                ? value
                : DefaultCollapsed(frame.Kind, frame.Instance?.Definition.Id, Options.Lens);
            frame.Collapsed = collapsed;
        }
        Acts = top;
    }

    /// <summary>The blocks of a frame and of its frames, depth first.</summary>
    private static IEnumerable<GraphBlock> Descendants(GraphFrame frame)
    {
        foreach (var block in frame.Blocks)
        {
            yield return block;
            if (block is GraphFrame inner)
            {
                foreach (var child in Descendants(inner))
                    yield return child;
            }
        }
    }

    /// <summary>The title of an act: "Act {n}: {system name}", or "Act {n}" for an act without a system (section 6.4
    /// of the M6a design). A system without a name gives its nickname.</summary>
    /// <param name="systemName">Gives the name of a system, or null.</param>
    public static string ActTitle(MissionAct act, Func<string, string?>? systemName)
    {
        if (act.Name is { Length: > 0 } actName)
            return actName;
        var number = act.Number.ToString(CultureInfo.InvariantCulture);
        if (act.System is not { } system)
            return $"Act {number}";
        return $"Act {number}: {(systemName?.Invoke(system) is { Length: > 0 } name ? name : system)}";
    }

    /// <summary>The label of an instance of a prefab that no mod has (decision D673): the frame, the Hierarchy row and
    /// the page show it.</summary>
    public const string MissingPrefab = "Missing prefab";

    /// <summary>The title of the frame "Not started by the mission" with its count of triggers.</summary>
    public static string NotStartedTitle(int count) =>
        $"Not started by the mission ({count.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The walkthrough line of an act: the count of its triggers and of its checkpoints.</summary>
    public static string Walkthrough(int triggers, int checkpoints) =>
        $"{Count(triggers, "trigger", "triggers")}, {Count(checkpoints, "checkpoint", "checkpoints")}";

    private static string Count(int n, string one, string many) =>
        n == 1 ? $"1 {one}" : $"{n.ToString(CultureInfo.InvariantCulture)} {many}";

    /// <summary>True when a frame or one of its parents is collapsed.</summary>
    private static bool Hidden(GraphBlock block)
    {
        for (var frame = block.Parent; frame != null; frame = frame.Parent)
        {
            if (frame.Collapsed)
                return true;
        }
        return false;
    }

    /// <summary>The nodes that show, and the nodes of the frames.</summary>
    private void BuildNodes()
    {
        foreach (var node in triggerNodes.Values.OrderBy(n => n.Order))
        {
            if (!Hidden(node))
                nodes.Add(node);
        }
        foreach (var frame in frames)
        {
            frame.Node = FrameNode(frame);
            if (frame.Collapsed && !Hidden(frame))
                nodes.Add(frame.Node);
        }
    }

    /// <summary>The trigger node with its header, body and pins.</summary>
    private GraphNode TriggerNode(MissionTrigger trigger, IReadOnlyList<Diagnostic> nodeProblems)
    {
        var key = KeyOf(trigger);
        var lines = Options.ShowLines || Options.LinesOf?.Contains(key) == true;
        var chips = new List<string>();
        if (trigger.StartsActive)
            chips.Add("Start");
        if (trigger.Repeats)
            chips.Add("Repeats");
        if (trigger.SystemHint is { } hint)
            chips.Add(hint);
        var condition = trigger.Condition;
        var marks = trigger.Rows.Where(r => r.Mark != EngineMark.Runs).ToList();
        string? targetLine = null;
        if (!Options.HidesOpenLancer && marks.Count > 0)
        {
            targetLine = marks.Any(m => m.Mark == EngineMark.CannotLoad)
                ? "OpenLancer cannot load this trigger"
                : marks.Count == 1 ? "1 line does nothing in OpenLancer"
                : $"{marks.Count.ToString(CultureInfo.InvariantCulture)} lines do nothing in OpenLancer";
        }

        // The output pins on the right: the state in the header, then the pins of the chips or of the rows.
        var right = 1;
        var hidden = 0;
        IReadOnlyList<GraphRow> rows = lines
            ? Rows(trigger, nodeProblems, Options.AllRowsOf?.Contains(key) == true, out hidden)
            : [];
        var node = new GraphNode(key, Id(key), trigger.Section.Index, GraphNodeKind.Trigger, trigger.Nickname)
        {
            Trigger = trigger,
            Triggers = [trigger],
            Lines = lines,
            EventGroup = condition?.Name?.Group,
            Sentence = condition?.Sentence() ?? "No condition",
            HeaderChips = chips,
            NeverStarts = !trigger.IsDuplicate && Index.NothingStarts(trigger),
            WatchedState = condition != null && trigger.Actions.Count == 0,
            Problems = nodeProblems,
            Chips = lines ? [] : Chips(trigger, ref right),
            Rows = rows,
            HiddenRows = hidden,
            TargetLine = lines ? null : targetLine,
        };

        // The input pins on the left, from the header down.
        var left = 0;
        node.Add(new GraphPin(Id(key + "|in:start"), GraphPinKind.Start, left++));
        node.Add(new GraphPin(Id(key + "|in:stop"), GraphPinKind.Stop, left++));
        if (Index.LinksTo(trigger).Any(l => l.Kind == MissionLinkKind.Load))
            node.Add(new GraphPin(Id(key + "|in:load"), GraphPinKind.Load, left++));
        if (condition != null && MissionIndex.Is(condition.Key, "Cnd_WatchTrigger"))
            node.Add(new GraphPin(Id(key + "|in:watch"), GraphPinKind.Watches, left++));

        if (Index.LinksTo(trigger).Any(l => l.Kind == MissionLinkKind.Watch))
            node.Add(new GraphPin(Id(key + "|out:state"), GraphPinKind.State, 0));
        if (lines)
        {
            foreach (var row in node.Rows.Where(r => r.Pin != null))
                node.Add(row.Pin!);
            node.BodyLines = node.Rows.Count + (node.HiddenRows > 0 ? 1 : 0);
            if (node.HiddenRows > 0)
                node.Add(new GraphPin(Id(key + "|out:more"), GraphPinKind.More, node.BodyLines));
        }
        else
        {
            foreach (var chip in node.Chips.Where(c => c.Pin != null))
                node.Add(chip.Pin!);
            node.BodyLines = targetLine != null ? 3 : 2;
        }
        node.Size = new Vector2(lines ? LinesWidth : SummaryWidth, Height(node.BodyLines, left - 1, right - 1));
        return node;
    }

    /// <summary>The height of a node from its body lines and its pin slots.</summary>
    private static float Height(int bodyLines, int leftSlots, int rightSlots) =>
        HeaderHeight + Math.Max(bodyLines, Math.Max(leftSlots, rightSlots)) * LineHeight + BottomPadding;

    /// <summary>The chips of the summary level (section 6.2 of the M6a design). They are one chip for each action
    /// group, in group order, then the flow chips of restart, cancel and self start.</summary>
    /// <param name="slot">The next free slot of the output pins. Each chip with a pin takes the next slot.</param>
    private List<GraphChip> Chips(MissionTrigger trigger, ref int slot)
    {
        var schema = Index.Schema;
        var chips = new List<GraphChip>();
        foreach (var group in trigger.Actions.GroupBy(a => a.Name?.Group ?? "")
                     .OrderBy(g => schema.Group(g.Key)?.Order ?? int.MaxValue))
        {
            var info = schema.Group(group.Key);
            var lines = group.ToList();
            var name = info?.Chip ?? "Other";
            var tooltip = string.Join("\n", lines.Select(ActionText));
            var pinKind = group.Key switch
            {
                "starts" => GraphPinKind.Starts,
                "stops" => GraphPinKind.Stops,
                "checkpoint" => GraphPinKind.Saves,
                _ => (GraphPinKind?)null,
            };
            var key = KeyOf(trigger);
            if (group.Key == "starts")
                tooltip += "\n\nThe game starts these triggers after all actions of this trigger. The order comes from " +
                           "the trigger names, not from the line order.";
            chips.Add(new GraphChip(group.Key, $"{name} {lines.Count.ToString(CultureInfo.InvariantCulture)}", tooltip,
                Pin: pinKind is { } kind ? new GraphPin(Id($"{key}|out:{kind}"), kind, slot++) : null));
        }
        foreach (var link in Index.Restarts.Where(l => l.Source == trigger))
            chips.Add(new GraphChip(null, $"Restarts {link.TargetName}",
                $"This trigger stops {link.TargetName} and then starts it again. {link.TargetName} starts from the beginning."));
        foreach (var link in Index.Cancels.Where(l => l.Source == trigger))
            chips.Add(new GraphChip(null, $"Starts and stops {link.TargetName}",
                $"This trigger starts {link.TargetName} and then stops it. The game does not start {link.TargetName}.",
                Warning: true));
        if (Index.SelfStarts.Contains(trigger))
            chips.Add(new GraphChip(null, "Starts itself", "This trigger starts itself again."));
        return chips;
    }

    /// <summary>The plain name and the targets of an action line, for a tooltip.</summary>
    public static string ActionText(MissionLine line)
    {
        if (MissionIndex.Is(line.Key, "Act_ChangeState"))
            return MissionIndex.Is(line.Value(0), "SUCCEED") ? "End mission: success" : "End mission: fail";
        return LineText(line);
    }

    /// <summary>The rows of the lines level: the lines before the nickname, the conditions, then the actions in file
    /// order. At most 20 rows show unless the node shows all rows.</summary>
    private List<GraphRow> Rows(MissionTrigger trigger, IReadOnlyList<Diagnostic> problems, bool all, out int hidden)
    {
        var rows = new List<GraphRow>();
        var lines = Index.LinesOf(trigger.Section);
        foreach (var entry in trigger.LinesBeforeNickname)
            rows.Add(Row(GraphRowKind.BeforeNickname, lines[entry], problems, rows.Count + 1));
        foreach (var condition in trigger.Conditions)
            rows.Add(Row(condition == trigger.Condition ? GraphRowKind.Condition : GraphRowKind.IgnoredCondition,
                condition, problems, rows.Count + 1));
        foreach (var action in trigger.Actions)
            rows.Add(Row(GraphRowKind.Action, action, problems, rows.Count + 1));
        hidden = all || rows.Count <= MaxRows ? 0 : rows.Count - (MaxRows - 1);
        return hidden > 0 ? rows.Take(MaxRows - 1).ToList() : rows;
    }

    /// <param name="slot">The body line of the row in the node, from 1.</param>
    private GraphRow Row(GraphRowKind kind, MissionLine line, IReadOnlyList<Diagnostic> problems, int slot)
    {
        var text = kind == GraphRowKind.Action ? ActionText(line) : LineText(line);
        var mark = Options.HidesOpenLancer || line.Mark == EngineMark.Runs ? null
            : $"OpenLancer: {OpenLancerGap.MarkText(line.Mark)}";
        GraphPin? pin = null;
        if (kind == GraphRowKind.Action && TriggerTarget(line) != null)
            pin = new GraphPin(Id($"{KeyOf(line.Trigger!)}|row:{line.Entry.ToString(CultureInfo.InvariantCulture)}"),
                GraphPinKind.Row, slot, line);
        return new GraphRow(kind, line, text, mark, pin, RowLinks(line),
            problems.Where(p => p.EntryIndex == line.Entry).ToList());
    }

    /// <summary>The plain name (<see cref="MissionLine.Plain"/>) and the values of a line, for example "Repeats: true".
    /// A name without values in the schema, for example Cnd_True, shows its plain name only, because its value is only a
    /// placeholder.</summary>
    public static string LineText(MissionLine line)
    {
        var plain = line.Plain;
        if (line.Name is { Values.Count: 0 })
            return plain;
        var values = Enumerable.Range(0, line.Count).Select(line.Value).OfType<string>().ToList();
        return values.Count == 0 ? plain : $"{plain}: {string.Join(", ", values)}";
    }

    /// <summary>The trigger name of a start, stop or checkpoint line, or null.</summary>
    private static string? TriggerTarget(MissionLine line) =>
        line.Key.ToLowerInvariant() is "act_acttrig" or "act_deacttrig" or "act_save" ? line.Value(0) : null;

    /// <summary>The link chips of a row: the items of the mission that it names.</summary>
    private IReadOnlyList<GraphRowLink> RowLinks(MissionLine line)
    {
        var result = new List<GraphRowLink>();
        foreach (var reference in line.References.Where(r => r.State == MissionReferenceState.Found))
        {
            var kind = reference.Section?.Kind switch
            {
                MissionSectionKind.Ship => "Ship",
                MissionSectionKind.Formation => "Formation",
                MissionSectionKind.Solar => "Solar",
                MissionSectionKind.Loot => "Loot",
                MissionSectionKind.OrderList => "Order list",
                MissionSectionKind.Objective => "Objective",
                MissionSectionKind.Conversation => "Conversation",
                MissionSectionKind.RandomEncounter => "Random encounter",
                _ => reference.Kind switch
                {
                    MissionValueKind.SpaceCutscene => "Space cutscene",
                    MissionValueKind.BaseScene => "Base scene",
                    MissionValueKind.Object when Index.Labels.ContainsKey(reference.Name) => "Label",
                    _ => null,
                },
            };
            if (kind != null && !result.Any(r => r.Kind == kind && MissionIndex.Is(r.Name, reference.Name)))
                result.Add(new GraphRowLink(kind, reference.Name, reference.Section));
        }
        return result;
    }

    /// <summary>The node of a frame: the collapsed form, with the pins of the links that cross the frame.</summary>
    private GraphNode FrameNode(GraphFrame frame)
    {
        var inside = frame.Triggers.ToHashSet();
        var nodeProblems = frame.Triggers.SelectMany(t => problems.GetValueOrDefault(t.Section.Ref) ?? []).ToList();
        var key = frame.Key + "|node";
        IReadOnlyList<string> fields = [];
        IReadOnlyList<string> tips = [];
        string? completeness = null;
        var kind = frame.Kind == GraphFrameKind.Prefab ? GraphNodeKind.Prefab : GraphNodeKind.Act;
        if (frame.Instance is { } instance)
        {
            var values = instance.Definition.Fields.Select(f => (Field: f, Values: instance.Values(f))).ToList();
            fields = values.Select(v =>
            {
                var value = v.Field.List != null ? v.Values.Count.ToString(CultureInfo.InvariantCulture)
                    : v.Values.Count == 0 ? "None"
                    : v.Values.Count == 1 ? v.Values[0].Text
                    : $"{v.Values[0].Text} (+{(v.Values.Count - 1).ToString(CultureInfo.InvariantCulture)})";
                return $"{v.Field.Label}: {value}";
            }).ToList();
            // The tooltip of a field gives each value on its own line, so a list field names each item.
            tips = values.Select(v => v.Values.Count == 0
                ? $"{v.Field.Label}: None"
                : $"{v.Field.Label}:\n{string.Join("\n", v.Values.Select(x => x.Text))}").ToList();
            var report = MissionCompleteness.OfInstance(Analysis, instance);
            if (!report.Complete)
                completeness = report.Summary;
        }

        var node = new GraphNode(key, Id(key), frame.Order, kind, frame.Title)
        {
            Frame = frame,
            Triggers = frame.Triggers,
            Sentence = frame.Detail,
            Fields = fields,
            FieldTips = tips,
            Completeness = completeness,
            Problems = nodeProblems,
        };
        node.Parent = frame.Parent;
        var left = 0;
        var incoming = Index.Links.Where(l => l.Target != null && inside.Contains(l.Target) && !inside.Contains(l.Source))
            .Select(l => l.Kind).ToHashSet();
        var outgoing = Index.Links.Where(l => inside.Contains(l.Source) && (l.Target == null || !inside.Contains(l.Target)))
            .Select(l => l.Kind).ToHashSet();
        var watchedFrom = Index.Links.Any(l => l.Kind == MissionLinkKind.Watch && l.Target != null &&
                                               inside.Contains(l.Target) && !inside.Contains(l.Source));
        foreach (var (link, pin) in new[]
                 {
                     (MissionLinkKind.Start, GraphPinKind.Start), (MissionLinkKind.Stop, GraphPinKind.Stop),
                     (MissionLinkKind.Load, GraphPinKind.Load),
                 })
        {
            if (incoming.Contains(link))
                node.Add(new GraphPin(Id($"{key}|in:{pin}"), pin, left++));
        }
        if (outgoing.Contains(MissionLinkKind.Watch))
            node.Add(new GraphPin(Id($"{key}|in:watch"), GraphPinKind.Watches, left++));
        var right = 1;
        if (watchedFrom)
            node.Add(new GraphPin(Id($"{key}|out:state"), GraphPinKind.State, 0));
        foreach (var (link, pin) in new[]
                 {
                     (MissionLinkKind.Start, GraphPinKind.Starts), (MissionLinkKind.Stop, GraphPinKind.Stops),
                     (MissionLinkKind.Load, GraphPinKind.Saves),
                 })
        {
            if (outgoing.Contains(link))
                node.Add(new GraphPin(Id($"{key}|out:{pin}"), pin, right++));
        }
        node.BodyLines = Math.Max(1, fields.Count + (completeness != null ? 1 : 0) + (kind == GraphNodeKind.Act ? 1 : 0));
        node.Size = new Vector2(SummaryWidth, Height(node.BodyLines, left - 1, right - 1));
        return node;
    }

    /// <summary>The links between the nodes that show (section 6.3 of the M6a design).</summary>
    private void BuildLinks()
    {
        var missing = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<(int From, int To), (GraphNode From, GraphPin FromPin, GraphNode To, GraphPin ToPin,
            MissionLinkKind Kind, List<MissionLink> Sources, string? State)>();
        var order = new List<(int, int)>();
        // In prefab mode, a value of a template can be a slot, for example {port:done}. The compile gives it the triggers
        // of the port or of the field, so the link ends at its output pin, with no "Missing" node (section 8.6 of the M6b
        // design).
        var template = Editing.MissionRefs.IsPrefab(Analysis.Script.Path);
        foreach (var link in Index.Links)
        {
            // A trigger that starts itself gives no link. Its row chip shows instead.
            if (link.Kind == MissionLinkKind.Start && link.Target == link.Source)
                continue;
            if (template && link.Target == null && Prefabs.PrefabSlot.Of(link.TargetName).Count > 0)
                continue;
            GraphNode from, to;
            GraphPin? fromPin, toPin;
            if (link.Kind == MissionLinkKind.Watch)
            {
                to = NodeOf(link.Source)!;
                from = link.Target != null ? NodeOf(link.Target)! : Missing(missing, link);
                fromPin = from.Pin(GraphPinKind.State);
                toPin = to.Pin(GraphPinKind.Watches);
            }
            else
            {
                from = NodeOf(link.Source)!;
                to = link.Target != null ? NodeOf(link.Target)! : Missing(missing, link);
                fromPin = OutputPin(from, link);
                toPin = to.Pin(link.Kind switch
                {
                    MissionLinkKind.Stop => GraphPinKind.Stop,
                    MissionLinkKind.Load => GraphPinKind.Load,
                    _ => GraphPinKind.Start,
                });
            }
            if (from == null || to == null || from == to || fromPin == null || toPin == null)
                continue;
            var key = (fromPin.Id, toPin.Id);
            if (!merged.TryGetValue(key, out var entry))
            {
                entry = (from, fromPin, to, toPin, link.Kind, [], link.Kind == MissionLinkKind.Watch ? StateText(link.State) : null);
                merged[key] = entry;
                order.Add(key);
            }
            entry.Sources.Add(link);
        }
        foreach (var key in order)
        {
            var (from, fromPin, to, toPin, kind, sources, state) = merged[key];
            links.Add(new GraphLink(Id($"link:{fromPin.Id}>{toPin.Id}"), kind, from, fromPin, to, toPin, sources, state));
        }
    }

    /// <summary>The output pin of a start, stop or checkpoint link at a node.</summary>
    private static GraphPin? OutputPin(GraphNode node, MissionLink link)
    {
        // At the lines level, a line that the limit of rows hides starts at the row "Show {n} more lines".
        if (node.Kind == GraphNodeKind.Trigger && node.Lines)
            return node.Pins.FirstOrDefault(p => p.Kind == GraphPinKind.Row && p.Line == link.Line) ??
                   node.Pin(GraphPinKind.More);
        return node.Pin(GroupPin(link.Kind));
    }

    private static GraphPinKind GroupPin(MissionLinkKind kind) => kind switch
    {
        MissionLinkKind.Stop => GraphPinKind.Stops,
        MissionLinkKind.Load => GraphPinKind.Saves,
        _ => GraphPinKind.Starts,
    };

    /// <summary>The node "Missing: {name}" of a trigger name that the script does not have, with the pin of a link.
    /// The node has the message of rule M05 for the first line that names it (section 6.3 of the M6a design).</summary>
    private GraphNode Missing(Dictionary<string, GraphNode> missing, MissionLink link)
    {
        var name = link.TargetName;
        var kind = link.Kind;
        if (!missing.TryGetValue(name.Trim(), out var node))
        {
            var key = "m:" + name.Trim().ToLowerInvariant();
            var message = Analysis.Problems.FirstOrDefault(p => p.Rule == "M05" && Equals(p.Target, link.Line.Section.Ref) &&
                                                                p.EntryIndex == link.Line.Entry)?.Message;
            node = new GraphNode(key, Id(key), int.MaxValue, GraphNodeKind.Missing, $"Missing: {name.Trim()}")
            {
                Sentence = message ?? "This script has no trigger with this name. The game ignores the line.",
                BodyLines = 1,
                MissingName = name.Trim(),
            };
            missing[name.Trim()] = node;
            nodes.Add(node);
            byKey[key] = node;
        }
        var pinKind = kind switch
        {
            MissionLinkKind.Stop => GraphPinKind.Stop,
            MissionLinkKind.Load => GraphPinKind.Load,
            MissionLinkKind.Watch => GraphPinKind.State,
            _ => GraphPinKind.Start,
        };
        if (node.Pin(pinKind) == null)
            node.Add(new GraphPin(Id($"{node.Key}|{pinKind}"), pinKind, pinKind == GraphPinKind.State ? 0
                : node.Pins.Count(p => p.IsInput)));
        node.Size = new Vector2(MissingWidth, Height(1, node.Pins.Count(p => p.IsInput) - 1, 0));
        return node;
    }

    /// <summary>The fade of the lens (section 6.5 of the M6a design). A collapsed node fades when all its triggers fade.
    /// A missing node never fades.</summary>
    private void ApplyLens()
    {
        Func<MissionTrigger, bool>? keeps = Options.Lens switch
        {
            GraphLens.Battle => t => t.Actions.Any(a => a.Name?.Group is "spawn" or "orders" or "sides" or "area") ||
                                     t.Condition?.Name?.Group == "combat",
            GraphLens.Conversation => t => t.Actions.Any(a => a.Name?.Group == "comms") ||
                                           t.Condition?.Name?.Group == "talk",
            GraphLens.Cutscene => t => t.Actions.Any(a => CutsceneActions.Contains(a.Key)),
            GraphLens.Objectives => t => t.Actions.Any(a => a.Name?.Group == "objective"),
            _ => null,
        };
        if (keeps == null)
            return;
        foreach (var node in nodes.Where(n => n.Kind != GraphNodeKind.Missing))
            node.Faded = node.Triggers.Count > 0 && !node.Triggers.Any(keeps);
    }

    /// <summary>The side list of the Cutscene and Objectives lenses, in path order from the start triggers over Start
    /// and Load links. The Cutscene lens lists the space cutscenes and the base scenes. The Objectives lens lists the
    /// objective and text lines.</summary>
    private IReadOnlyList<GraphSideItem> BuildSideList()
    {
        Func<MissionLine, bool>? lists = Options.Lens switch
        {
            GraphLens.Cutscene => l => CutsceneListActions.Contains(l.Key),
            GraphLens.Objectives => l => l.Name?.Group == "objective",
            _ => null,
        };
        if (lists == null)
            return [];
        var result = new List<GraphSideItem>();
        foreach (var trigger in PathOrder(Index))
        {
            foreach (var line in trigger.Actions.Where(lists))
                result.Add(new GraphSideItem(ActionText(line), trigger, line, SideTextIds(line)));
        }
        return result;
    }

    /// <summary>The text ids of a line: its values of the kind text id. Then come the text ids of the <c>type</c> line
    /// of each objective that it names.</summary>
    private static IReadOnlyList<string> SideTextIds(MissionLine line)
    {
        var ids = line.Specs.Where(s => s.Kind == MissionValueKind.TextId).Select(line.ValueOf).OfType<string>().ToList();
        foreach (var reference in line.References)
        {
            if (reference.Section is { Kind: MissionSectionKind.Objective } objective &&
                objective.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "type")) is { } type)
            {
                ids.AddRange(MissionIndex.ObjectiveTextIds(type));
            }
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The triggers that the start triggers reach, in breadth-first order over Start and Load links in file
    /// order.</summary>
    public static IReadOnlyList<MissionTrigger> PathOrder(MissionIndex index)
    {
        var order = new List<MissionTrigger>();
        var seen = new HashSet<MissionTrigger>();
        var queue = new Queue<MissionTrigger>();
        foreach (var start in index.StartTriggers)
        {
            if (seen.Add(start))
                queue.Enqueue(start);
        }
        var outgoing = index.Links.Where(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Load && l.Target != null)
            .GroupBy(l => l.Source).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Line.Entry).Select(l => l.Target!).ToList());
        while (queue.Count > 0)
        {
            var trigger = queue.Dequeue();
            order.Add(trigger);
            foreach (var next in outgoing.GetValueOrDefault(trigger) ?? [])
            {
                if (seen.Add(next))
                    queue.Enqueue(next);
            }
        }
        return order;
    }
}
