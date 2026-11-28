using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The groups of the Timeline and of the Hierarchy in cutscene mode, in their order.</summary>
internal enum TimelineGroupKind
{
    Cameras,
    Characters,
    Props,
    Lights,
    Effects,
    Sounds,
    Markers,
    Paths,
    Scene,
    Other,

    /// <summary>The event targets that name no entity of the script, for example an entity of the set script.</summary>
    Missing,
}

/// <summary>One event of the open script, as the Timeline shows it.</summary>
/// <param name="Index">The index of the event in the events of W.</param>
/// <param name="Time">The start, in seconds.</param>
/// <param name="Length"><c>props.duration</c>, or 0 for an event without a length. An event of a type without a length
/// (see <see cref="ThnSchema.HasLength"/>) has 0 too, also when it stores <c>props.duration</c>. So the Timeline draws it as
/// a diamond without edge handles, because a length change of such an event refuses.</param>
/// <param name="Type">The type as a THN name, for example <c>SET_CAMERA</c>, or the value of the file when the engine
/// does not know it.</param>
/// <param name="Kind">The type as the engine reads it, or null.</param>
/// <param name="Targets">The names of the targets.</param>
/// <param name="Keys">The times of the points of <c>param_curve</c> in the bar.</param>
/// <param name="AfterEnd">For an event that starts after the end of the script, the gap and what the game does with
/// it (see <see cref="TimelineModel.GapText"/>). Else null.</param>
/// <param name="Ignored">Why the game does not run the event in this context, or null.</param>
internal sealed record TimelineEvent(ThnRef Ref, int Index, float Time, float Length, string Type, EventTypes? Kind,
    IReadOnlyList<string> Targets, IReadOnlyList<float> Keys, string? AfterEnd, string? Ignored)
{
    /// <summary>The sub-row of the event in its row, from 0 (decision D131). A diamond and a cut of a monitor lane are
    /// on sub-row 0.</summary>
    public int SubRow { get; init; }

    /// <summary>True for an event with a length, which the Timeline draws as a bar. Else it draws a diamond.</summary>
    public bool IsBar => Length > 0;

    /// <summary>The end of the bar, or the time of a diamond.</summary>
    public float End => Time + Length;

    /// <summary>The second target, for example the camera of a cut or the target of an attachment, or null.</summary>
    public string? SecondTarget => Targets.Count > 1 ? Targets[1] : null;
}

/// <summary>A SET_CAMERA event on a monitor lane, and its shot.</summary>
/// <param name="Camera">The camera of the shot: the second target.</param>
/// <param name="End">The end of the shot: the next cut of the lane, or the end of the script.</param>
internal sealed record TimelineCut(TimelineEvent Event, string Camera, float End);

/// <summary>The camera cuts of one monitor.</summary>
/// <param name="Monitor">The MONITOR entity, or null when no entity of the script has the name.</param>
/// <param name="Name">The name of the monitor, as the SET_CAMERA events give it.</param>
/// <param name="Main">True for the monitor that the engine uses.</param>
/// <param name="Applies">True when the engine applies the cuts of the lane.</param>
/// <param name="Note">What the engine does with the cuts of the lane.</param>
/// <param name="Cuts">The cuts, in time order.</param>
internal sealed record TimelineLane(ThnRef? Monitor, string Name, bool Main, bool Applies, string Note,
    IReadOnlyList<TimelineCut> Cuts);

/// <summary>One track of the Timeline: an entity and the events whose first target it is.</summary>
/// <param name="Entity">The entity, or null for a target name that no entity of the script has.</param>
/// <param name="Name">The name of the entity or of the target.</param>
/// <param name="Kind">The kind of the entity, for the glyph and the color.</param>
/// <param name="Events">The events of the row, in the order of the file.</param>
/// <param name="Mark">An information mark of the row, or null.</param>
internal sealed record TimelineRow(ThnRef? Entity, string Name, ObjectKind Kind, IReadOnlyList<TimelineEvent> Events,
    string? Mark)
{
    /// <summary>The count of the sub-rows of the row, 1 or more. A row gets more than one sub-row when its bars
    /// overlap, so that no bar hides another bar (decision D131).</summary>
    public int SubRows { get; init; } = 1;
}

/// <summary>One group of the Timeline: the monitor lanes of the cameras, and the rows of one entity type.</summary>
internal sealed record TimelineGroup(TimelineGroupKind Id, string Label, ObjectKind Kind,
    IReadOnlyList<TimelineLane> Lanes, IReadOnlyList<TimelineRow> Rows);

/// <summary>One visible line of the Timeline: the header of a group, a monitor lane, or one sub-row of a row.</summary>
/// <param name="SubRow">The sub-row of a row, from 0. A header and a lane have 0.</param>
internal readonly record struct TimelineLine(TimelineGroup Group, TimelineLane? Lane, TimelineRow? Row, int SubRow = 0)
{
    public bool IsHeader => Lane == null && Row == null;

    /// <summary>The events of the line: the cuts of a lane, or the events of the sub-row of a row.</summary>
    public IEnumerable<TimelineEvent> Events
    {
        get
        {
            var subRow = SubRow;
            return Lane?.Cuts.Select(c => c.Event) ?? Row?.Events.Where(e => e.SubRow == subRow) ?? [];
        }
    }
}

/// <summary>The diamonds of a row that overlap on the screen. The Timeline draws them as one mark with a count.</summary>
/// <param name="Time">The time of the first diamond, where the mark draws.</param>
/// <param name="Events">The diamonds of the mark, in the order of the file.</param>
internal sealed record DiamondMark(float Time, IReadOnlyList<TimelineEvent> Events);

/// <summary>
/// The Timeline of one cutscene, without ImGui (section 7.3 of the M8 design): the groups and their rows, the monitor
/// lanes with the camera cuts, the bars and diamonds of the events, and the hit tests. It reads W of the cutscene, so it
/// shows the unsaved edits.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The engine sorts the monitors by <c>userprops.Priority</c> and uses only the first one
/// (ThnScriptInstance.cs:309-312, SetCameraEvent.cs:24-28). The other lanes show their cuts grey.</item>
/// <item>An event is on the row of its first target. The engine finds an entity by its name without letter case, and
/// it uses the last entity with a name (ThnScript.cs:107, :132-136).</item>
/// <item>A camera that only an ignored monitor cuts to gets an information mark, unless another event names it. For
/// example, a START_SPATIAL_PROP_ANIM can move a camera of the main monitor to the pose of such a camera.</item>
/// </list>
/// </remarks>
internal sealed class TimelineModel
{
    /// <summary>The information mark of a camera that only an ignored monitor cuts to.</summary>
    public const string IgnoredMonitorMark =
        "Only an ignored monitor uses this camera, and no other event names it. The game never looks through it.";

    /// <summary>The information mark of an entity that a later entity with the same name hides.</summary>
    public const string HiddenEntityMark =
        "A later entity has the same name, and the engine uses that one (ThnScript.cs:132-136).";

    private const string SetScriptReason =
        "The game makes the entities of a set script and runs none of its events (Cutscene.cs:279-283).";

    private const string FidgetReason = "A fidget runs only its START_MOTION events (Cutscene.cs:229-241).";

    private const string MainLaneNote =
        "The engine uses this monitor: the first monitor by userprops.Priority (ThnScriptInstance.cs:309-312).";

    private const string IgnoredLaneNote =
        "Ignored by the engine: it uses only the first monitor by userprops.Priority, so it does not apply these cuts " +
        "(SetCameraEvent.cs:24-28).";

    private const string UnknownLaneNote =
        "No entity of this script has this name. The engine looks for the monitor in the scene and cuts (SetCameraEvent.cs:16-30).";

    private const string NotMonitorLaneNote =
        "This entity is not a MONITOR, so the engine does not cut (SetCameraEvent.cs:19-23).";

    /// <summary>The name of the row of the events without a target.</summary>
    public const string NoTarget = "(no target)";

    private const string EntityNameKey = "entity_name";

    /// <summary>At most this many key ticks for each event, for a curve with a short period in a long event.</summary>
    private const int MaxKeys = 256;

    /// <summary>The frame rate of the frame rule of an event after the end.</summary>
    private const double FramesPerSecond = 60;

    private static readonly (TimelineGroupKind Id, string Label, ObjectKind Kind)[] GroupInfo =
    [
        (TimelineGroupKind.Cameras, "Cameras", ObjectKinds.Thn.Camera),
        (TimelineGroupKind.Characters, "Characters", ObjectKinds.Thn.Character),
        (TimelineGroupKind.Props, "Props", ObjectKinds.Thn.Prop),
        (TimelineGroupKind.Lights, "Lights", ObjectKinds.Thn.Light),
        (TimelineGroupKind.Effects, "Effects", ObjectKinds.Thn.Effect),
        (TimelineGroupKind.Sounds, "Sounds", ObjectKinds.Thn.Sound),
        (TimelineGroupKind.Markers, "Markers", ObjectKinds.Thn.Marker),
        (TimelineGroupKind.Paths, "Paths", ObjectKinds.Thn.Path),
        (TimelineGroupKind.Scene, "Scene", ObjectKinds.Thn.Scene),
        (TimelineGroupKind.Other, "Other", ObjectKinds.Thn.Other),
        (TimelineGroupKind.Missing, "Not in this script", ObjectKinds.Thn.Missing),
    ];

    private readonly Dictionary<ThnElementId, TimelineEvent> eventsById;
    private readonly Dictionary<ThnElementId, ObjectKind> entityKinds;
    private readonly Dictionary<string, (TimelineGroup Group, int Index)> rowsByName;

    // The bars of the last model that packed its sub-rows: this model, or for a pinned build the packed model before
    // the gesture. The next packing keeps the sub-row of each bar that did not change since.
    private readonly IReadOnlyDictionary<ThnElementId, PackedBar> packed;

    private TimelineModel(string path, float duration, IReadOnlyList<TimelineGroup> groups,
        IReadOnlyList<TimelineEvent> events, Dictionary<ThnElementId, ObjectKind> entityKinds,
        IReadOnlyDictionary<ThnElementId, PackedBar>? packed)
    {
        Path = path;
        Duration = duration;
        Groups = groups;
        Events = events;
        this.entityKinds = entityKinds;
        eventsById = events.ToDictionary(e => e.Ref.Id);
        Lanes = groups.SelectMany(g => g.Lanes).ToList();
        End = Math.Max(duration, events.Count == 0 ? 0 : events.Max(e => e.End));
        this.packed = packed ?? events.Where(e => e.IsBar)
            .ToDictionary(e => e.Ref.Id, e => new PackedBar(e.Time, e.Length, e.SubRow));
        rowsByName = new Dictionary<string, (TimelineGroup, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            for (var i = 0; i < group.Rows.Count; i++)
            {
                // The engine uses the last entity with a name, so an earlier entity with the name has no events.
                if (group.Rows[i].Mark != HiddenEntityMark)
                    rowsByName[group.Rows[i].Name] = (group, i);
            }
        }
    }

    /// <summary>The install path of the cutscene.</summary>
    public string Path { get; }

    /// <summary>The <c>duration</c> global of W.</summary>
    public float Duration { get; }

    /// <summary>The end of the time range: the duration, or the end of the last event when an event ends later.</summary>
    public float End { get; }

    /// <summary>The groups that have lanes or rows, in the order of <see cref="TimelineGroupKind"/>.</summary>
    public IReadOnlyList<TimelineGroup> Groups { get; }

    /// <summary>The time that the build took to give the bars their sub-rows (see <see cref="Pack"/>).</summary>
    public TimeSpan PackTime { get; private init; }

    /// <summary>The monitor lanes, the main lane first.</summary>
    public IReadOnlyList<TimelineLane> Lanes { get; }

    /// <summary>All events, in the order of W.</summary>
    public IReadOnlyList<TimelineEvent> Events { get; }

    /// <summary>The lane of the main monitor, or null when the script has no monitor.</summary>
    public TimelineLane? MainLane => Lanes.FirstOrDefault(l => l.Main);

    /// <summary>The cameras with the information mark of <see cref="IgnoredMonitorMark"/>.</summary>
    public IEnumerable<TimelineRow> MarkedCameras =>
        Groups.Where(g => g.Id == TimelineGroupKind.Cameras).SelectMany(g => g.Rows)
            .Where(r => r.Mark == IgnoredMonitorMark);

    /// <summary>The counts of the Timeline for the log line of an open.</summary>
    public string Summary
    {
        get
        {
            var rows = Groups.Sum(g => g.Rows.Count);
            var withEvents = Groups.Sum(g => g.Rows.Count(r => r.Events.Count > 0));
            var cuts = Lanes.Sum(l => l.Cuts.Count);
            var mainCuts = MainLane?.Cuts.Count ?? 0;
            var marked = MarkedCameras.Select(r => r.Name).ToList();
            return $"Timeline: {Count(rows, "row")} ({withEvents} with events) in {Count(Groups.Count, "group")}, " +
                   $"{Count(Lanes.Count, "monitor lane")}, {Count(cuts, "cut")} ({mainCuts} on the main monitor), " +
                   $"{Count(Events.Count, "event")} ({Events.Count(e => e.AfterEnd != null)} after the end, " +
                   $"{Events.Count(e => e.Ignored != null)} that the game does not run here). Cameras that only an " +
                   $"ignored monitor uses: {(marked.Count == 0 ? "none" : string.Join(", ", marked))}.";
        }
    }

    /// <summary>Makes the Timeline of W.</summary>
    /// <param name="path">The install path of the cutscene.</param>
    /// <param name="symbols">The THN names, for the types that the file gives as names.</param>
    /// <param name="play">How the preview context plays the script.</param>
    /// <param name="previous">The model of the same cutscene before the change, or null for a new open. A bar that did
    /// not change keeps its sub-row (see <see cref="Pack"/>).</param>
    /// <param name="pinSubRows">True while a gesture is open (<see cref="EditorWindow.IsEditing"/>): each event keeps
    /// its sub-row of <paramref name="previous"/>, also when bars overlap, so no bar jumps during a drag. The first
    /// build after the gesture packs again.</param>
    public static TimelineModel Build(string path, ThnScriptModel working,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols, ScriptPlay play, TimelineModel? previous = null,
        bool pinSubRows = false)
    {
        var names = new SymbolNames(symbols);
        var duration = working.Duration;

        // The entities as the engine keeps them: one for each name, the last one, at the place of the first one
        // (ThnScript.cs:130-137).
        var engine = new Dictionary<string, ThnElement>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var entity in working.Entities)
        {
            var name = EntityName(entity.Table);
            if (!engine.ContainsKey(name))
                order.Add(name);
            engine[name] = entity;
        }

        var types = working.Entities.ToDictionary(e => e.Id, e => EntityType(e.Table, names));
        var monitors = order.Select(n => engine[n]).Where(e => types[e.Id] == EntityTypes.Monitor)
            .OrderBy(e => Priority(e.Table), StringComparer.Ordinal).ToList();
        var mainMonitor = monitors.Count > 0 ? EntityName(monitors[0].Table) : null;

        // The events, with the lane of each cut.
        var events = new List<TimelineEvent>(working.Events.Count);
        var laneOf = new Dictionary<ThnElementId, string>();
        for (var i = 0; i < working.Events.Count; i++)
        {
            var element = working.Events[i];
            var ev = Event(path, element, i, duration, names, play);
            if (ev.Kind == EventTypes.SetCamera && ev.Targets.Count >= 2)
            {
                laneOf[element.Id] = ev.Targets[0];
                if (ev.Ignored == null && LaneNote(ev.Targets[0], engine, types, mainMonitor) is { Applies: false } lane)
                    ev = ev with { Ignored = lane.Note };
            }

            events.Add(ev);
        }

        var lanes = BuildLanes(path, events, laneOf, monitors, engine, types, mainMonitor, duration);
        var pin = pinSubRows ? previous : null;
        var memory = new SubRowMemory(pin?.eventsById, previous?.packed);
        var rows = BuildRows(path, working, events, laneOf, engine, types, lanes, memory);
        var groups = new List<TimelineGroup>();
        foreach (var (id, label, kind) in GroupInfo)
        {
            var groupLanes = id == TimelineGroupKind.Cameras ? lanes : [];
            var groupRows = rows.Where(r => r.Group == id).Select(r => r.Row).ToList();
            if (groupLanes.Count > 0 || groupRows.Count > 0)
                groups.Add(new TimelineGroup(id, label, kind, groupLanes, groupRows));
        }

        var kinds = working.Entities.ToDictionary(e => e.Id, e => KindOf(types[e.Id]));
        return new TimelineModel(path, duration, groups, events, kinds, pin?.packed) { PackTime = memory.Time };
    }

    /// <summary>
    /// The visible lines: the header of each group, then its lanes and rows when it is open. With
    /// <paramref name="onlyWithEvents"/>, a row without events does not show, and a group without lanes or shown rows
    /// does not show.
    /// </summary>
    public IReadOnlyList<TimelineLine> Lines(IReadOnlySet<TimelineGroupKind> collapsed, bool onlyWithEvents)
    {
        var lines = new List<TimelineLine>();
        foreach (var group in Groups)
        {
            var rows = onlyWithEvents ? group.Rows.Where(r => r.Events.Count > 0).ToList() : group.Rows;
            if (group.Lanes.Count == 0 && rows.Count == 0)
                continue;
            lines.Add(new TimelineLine(group, null, null));
            if (collapsed.Contains(group.Id))
                continue;
            lines.AddRange(group.Lanes.Select(l => new TimelineLine(group, l, null)));
            foreach (var row in rows)
            {
                for (var subRow = 0; subRow < row.SubRows; subRow++)
                    lines.Add(new TimelineLine(group, null, row, subRow));
            }
        }

        return lines;
    }

    /// <summary>
    /// The event of a line under a time, or null. A diamond and a cut take <paramref name="tolerance"/> seconds on each
    /// side, and a bar takes it at its ends. The Timeline draws the diamonds over the bars and a later event over an
    /// earlier one, so the hit test takes the diamonds first and then the latest event. On a lane, a time in a shot
    /// gives the cut of the shot. On a row, the hit test reads only the events of the sub-row of the line.
    /// </summary>
    public static TimelineEvent? HitTest(TimelineLine line, double time, double tolerance)
    {
        if (line.Lane is { } lane)
        {
            for (var i = lane.Cuts.Count - 1; i >= 0; i--)
            {
                if (Math.Abs(time - lane.Cuts[i].Event.Time) <= tolerance)
                    return lane.Cuts[i].Event;
            }

            for (var i = lane.Cuts.Count - 1; i >= 0; i--)
            {
                if (time >= lane.Cuts[i].Event.Time && time < lane.Cuts[i].End)
                    return lane.Cuts[i].Event;
            }

            return null;
        }

        if (line.Row == null)
            return null;
        var events = line.Events.ToList();
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var ev = events[i];
            if (!ev.IsBar && Math.Abs(time - ev.Time) <= tolerance)
                return ev;
        }

        for (var i = events.Count - 1; i >= 0; i--)
        {
            var ev = events[i];
            if (ev.IsBar && time >= ev.Time - tolerance && time <= ev.End + tolerance)
                return ev;
        }

        return null;
    }

    /// <summary>
    /// The events of a line that a box from <paramref name="from"/> to <paramref name="to"/> seconds touches, for a box
    /// select: a bar that overlaps the range, and a diamond or a cut within <paramref name="tolerance"/> of it. A diamond
    /// brings the other diamonds of its mark (see <see cref="Diamonds"/>), so a box never takes only a part of a mark.
    /// </summary>
    /// <param name="markWidth">The width of a diamond in seconds at the zoom of the view.</param>
    public static IEnumerable<TimelineEvent> EventsIn(TimelineLine line, double from, double to, double tolerance,
        double markWidth = 0)
    {
        var (low, high) = (Math.Min(from, to), Math.Max(from, to));
        var picked = line.Events.Where(e => e.IsBar
            ? e.Time <= high && e.End >= low
            : e.Time >= low - tolerance && e.Time <= high + tolerance).ToList();
        if (line.Row == null)
            return picked;
        var ids = picked.Select(e => e.Ref.Id).ToHashSet();
        var marks = Diamonds(line, markWidth).Where(m => m.Events.Any(e => ids.Contains(e.Ref.Id)));
        return picked.Concat(marks.SelectMany(m => m.Events)).DistinctBy(e => e.Ref.Id).ToList();
    }

    /// <summary>
    /// The diamonds of a row line as marks. Diamonds at one time make one mark. A diamond that is closer than
    /// <paramref name="width"/> seconds to the diamond before it joins the mark of that diamond. So at the zoom of the
    /// view, no diamond hides another diamond. The marks are in time order. A lane gives no marks.
    /// </summary>
    /// <param name="width">The width of a diamond in seconds at the zoom of the view.</param>
    public static IReadOnlyList<DiamondMark> Diamonds(TimelineLine line, double width)
    {
        if (line.Row == null)
            return [];
        var groups = new List<List<TimelineEvent>>();
        var last = 0f;
        foreach (var ev in line.Events.Where(e => !e.IsBar).OrderBy(e => e.Time).ThenBy(e => e.Index))
        {
            if (groups.Count > 0 && (ev.Time == last || ev.Time - last < width))
                groups[^1].Add(ev);
            else
                groups.Add([ev]);
            last = ev.Time;
        }

        // The events of each mark in the order of the file, so that a click takes the last one, as the hit test does.
        return groups.Select(g => new DiamondMark(g[0].Time, g.OrderBy(e => e.Index).ToList())).ToList();
    }

    /// <summary>The event with an id, or null.</summary>
    public TimelineEvent? Event(ThnElementId id) => eventsById.GetValueOrDefault(id);

    /// <summary>The kind of an entity of W, or null when W has no entity with the id.</summary>
    public ObjectKind? KindOf(ThnElementId entity) => entityKinds.GetValueOrDefault(entity);

    /// <summary>The group and the index of the row of an entity name, without letter case, or null.</summary>
    public (TimelineGroup Group, int Index)? RowOf(string name) =>
        rowsByName.TryGetValue(name, out var row) ? row : null;

    /// <summary>
    /// The text of an event that starts after the end of the script. An event runs in a frame when its time is at or
    /// before the time of that frame, and the last frame can pass the end by one step (ThnScriptInstance.cs:319-349).
    /// So the text tells whether a steady 60 frames per second runs it. The preview clock stops at the end, so the
    /// preview does not run it.
    /// </summary>
    public static string GapText(float time, float duration)
    {
        var gap = (double)time - duration;
        var milliseconds = Math.Round(gap * 1000);
        var gapText = gap >= 1 ? $"{Invariant(gap, "0.###")} s"
            : milliseconds < 1 ? "less than 1 ms"
            : $"{Invariant(milliseconds, "0")} ms";
        // The last update starts at the last step at or before the end, and it ends one step later.
        var lastFrame = (Math.Floor(duration * FramesPerSecond + 1e-6) + 1) / FramesPerSecond;
        var runs = time <= lastFrame;
        return $"Starts {gapText} after the end. The game runs it only when the last frame passes this time, so it " +
               $"depends on the frame rate. At a steady 60 frames per second, the last frame ends at " +
               $"{Invariant(lastFrame, "0.000")} s, so the game {(runs ? "runs" : "does not run")} it. The preview " +
               "stops at the end, so it does not run it.";
    }

    /// <summary>The tooltip of an event: its type and times, its targets, the keys, and the notes.</summary>
    public static string Describe(TimelineEvent ev)
    {
        var lines = new List<string>
        {
            ev.IsBar
                ? $"{ev.Type} at {Invariant(ev.Time, "0.000")} s, {Invariant(ev.Length, "0.000")} s long, to {Invariant(ev.End, "0.000")} s"
                : $"{ev.Type} at {Invariant(ev.Time, "0.000")} s",
            ev.Targets.Count == 0 ? "No target" : $"Targets: {string.Join(", ", ev.Targets)}",
        };
        if (ev.Keys.Count > 0)
            lines.Add($"{Count(ev.Keys.Count, "key")} of param_curve");
        if (ev.AfterEnd != null)
            lines.Add(ev.AfterEnd);
        if (ev.Ignored != null)
            lines.Add(ev.Ignored);
        return string.Join("\n", lines);
    }

    /// <summary>The name of an entity, or the empty name when it has no text name.</summary>
    internal static string EntityName(ThnTable entity) => entity[EntityNameKey] is ThnText name ? name.Value : "";

    private static TimelineEvent Event(string path, ThnElement element, int index, float duration, SymbolNames names,
        ScriptPlay play)
    {
        var list = element.Table.List;
        var time = list.Length > 0 && list[0] is ThnNumber t ? t.Value : 0;
        var (type, kind) = list.Length > 1 ? names.EventType(list[1]) : ("(no type)", null);
        var targets = list.Length > 2 && list[2] is ThnTable { IsRecord: false } targetList
            ? targetList.List.Select(v => v is ThnText text ? text.Value : ThnWriter.Value(v)).ToList()
            : [];
        var props = list.Length > 3 ? list[3] as ThnTable : null;
        var length = ThnSchema.HasLength(type) ? TimelineClip.LengthOf(element.Table) : 0;
        string? ignored = play switch
        {
            ScriptPlay.Set => SetScriptReason,
            ScriptPlay.Fidget when kind != EventTypes.StartMotion => FidgetReason,
            _ => null,
        };
        return new TimelineEvent(new ThnRef(path, ThnElementKind.Event, element.Id), index, time, length, type, kind,
            targets, Keys(props, time, length), time > duration ? GapText(time, duration) : null, ignored);
    }

    /// <summary>
    /// The times of the points of <c>param_curve</c> in a bar, each time once, in order. A point has its time as a
    /// fraction of the event length. When the event has <c>pcurve_period</c> milliseconds, the time is a fraction of the
    /// period, and the curve repeats (ParameterCurve.cs:105-131).
    /// </summary>
    private static IReadOnlyList<float> Keys(ThnTable? props, float time, float length)
    {
        if (length <= 0 || props?["param_curve"] is not ThnTable curve || curve["points"] is not ThnTable points)
            return [];
        var fractions = points.List.OfType<ThnTable>().Where(p => p.List.Length > 0 && p.List[0] is ThnNumber)
            .Select(p => Math.Clamp(((ThnNumber)p.List[0]).Value, 0f, 1f)).ToList();
        var period = props["pcurve_period"] is ThnNumber p && p.Value / 1000 >= float.Epsilon ? p.Value / 1000 : length;
        var keys = new SortedSet<float>();
        for (var repeat = 0; keys.Count < MaxKeys && repeat * period <= length; repeat++)
        {
            foreach (var fraction in fractions)
            {
                var at = (repeat + fraction) * period;
                if (at <= length && keys.Count < MaxKeys)
                    keys.Add(time + at);
            }
        }

        return [.. keys];
    }

    /// <summary>
    /// The lanes: one for each MONITOR entity, by <c>userprops.Priority</c>, then one for each other name that a
    /// SET_CAMERA gives as its monitor. The sort is stable, as the sort of the engine is for a few monitors.
    /// </summary>
    private static List<TimelineLane> BuildLanes(string path, List<TimelineEvent> events, Dictionary<ThnElementId, string> laneOf,
        List<ThnElement> monitors, Dictionary<string, ThnElement> engine, Dictionary<ThnElementId, EntityTypes?> types,
        string? mainMonitor, float duration)
    {
        var names = monitors.Select(m => EntityName(m.Table)).ToList();
        foreach (var name in laneOf.Values)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        var lanes = new List<TimelineLane>();
        foreach (var name in names)
        {
            var cuts = events.Where(e => laneOf.TryGetValue(e.Ref.Id, out var lane) &&
                                         lane.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Time).ToList();
            var shots = cuts.Select((c, i) => new TimelineCut(c, c.Targets[1],
                i + 1 < cuts.Count ? cuts[i + 1].Time : Math.Max(duration, c.Time))).ToList();
            var (applies, note) = LaneNote(name, engine, types, mainMonitor);
            var monitor = engine.TryGetValue(name, out var entity) && types[entity.Id] == EntityTypes.Monitor
                ? new ThnRef(path, ThnElementKind.Entity, entity.Id)
                : null;
            lanes.Add(new TimelineLane(monitor, monitor != null ? EntityName(entity!.Table) : name,
                mainMonitor != null && name.Equals(mainMonitor, StringComparison.OrdinalIgnoreCase), applies, note,
                shots));
        }

        return lanes;
    }

    /// <summary>Whether the engine applies the cuts on a monitor name, and why (SetCameraEvent.cs:16-30).</summary>
    private static (bool Applies, string Note) LaneNote(string name, Dictionary<string, ThnElement> engine,
        Dictionary<ThnElementId, EntityTypes?> types, string? mainMonitor)
    {
        if (!engine.TryGetValue(name, out var entity))
            return (true, UnknownLaneNote);
        if (types[entity.Id] != EntityTypes.Monitor)
            return (false, NotMonitorLaneNote);
        return name.Equals(mainMonitor, StringComparison.OrdinalIgnoreCase) ? (true, MainLaneNote) : (false, IgnoredLaneNote);
    }

    /// <summary>
    /// The rows: one for each entity in the order of the file, without the monitors that only have cuts, then one for
    /// each other first target. An event goes to the row of the last entity with the name of its first target. The
    /// events of each row get their sub-rows (see <see cref="Pack"/>), and <paramref name="events"/> gets the events
    /// with their sub-rows.
    /// </summary>
    private static List<(TimelineGroupKind Group, TimelineRow Row)> BuildRows(string path, ThnScriptModel working,
        List<TimelineEvent> events, Dictionary<ThnElementId, string> laneOf, Dictionary<string, ThnElement> engine,
        Dictionary<ThnElementId, EntityTypes?> types, List<TimelineLane> lanes, SubRowMemory memory)
    {
        var byEntity = new Dictionary<ThnElementId, List<TimelineEvent>>();
        var missing = new Dictionary<string, List<TimelineEvent>>(StringComparer.OrdinalIgnoreCase);
        var missingOrder = new List<string>();
        foreach (var ev in events.Where(e => !laneOf.ContainsKey(e.Ref.Id)))
        {
            var target = ev.Targets.Count > 0 ? ev.Targets[0] : NoTarget;
            if (ev.Targets.Count > 0 && engine.TryGetValue(target, out var entity))
            {
                if (!byEntity.TryGetValue(entity.Id, out var list))
                    byEntity[entity.Id] = list = [];
                list.Add(ev);
                continue;
            }

            if (!missing.ContainsKey(target))
            {
                missing[target] = [];
                missingOrder.Add(target);
            }

            missing[target].Add(ev);
        }

        var start = Stopwatch.GetTimestamp();
        var subRows = new Dictionary<List<TimelineEvent>, int>();
        var withSubRow = new Dictionary<ThnElementId, TimelineEvent>();
        foreach (var list in byEntity.Values.Concat(missing.Values))
            subRows[list] = Pack(list, memory, withSubRow);
        for (var i = 0; i < events.Count; i++)
        {
            if (withSubRow.TryGetValue(events[i].Ref.Id, out var packed))
                events[i] = packed;
        }

        memory.Time = Stopwatch.GetElapsedTime(start);

        // The cameras that some event other than a cut names, and the cameras of each kind of lane.
        var named = new HashSet<string>(events.Where(e => !laneOf.ContainsKey(e.Ref.Id)).SelectMany(e => e.Targets),
            StringComparer.OrdinalIgnoreCase);
        var appliedCameras = new HashSet<string>(lanes.Where(l => l.Applies).SelectMany(l => l.Cuts).Select(c => c.Camera),
            StringComparer.OrdinalIgnoreCase);
        var ignoredCameras = new HashSet<string>(lanes.Where(l => !l.Applies).SelectMany(l => l.Cuts).Select(c => c.Camera),
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<(TimelineGroupKind, TimelineRow)>();
        foreach (var entity in working.Entities)
        {
            var name = EntityName(entity.Table);
            var type = types[entity.Id];
            var rowEvents = byEntity.GetValueOrDefault(entity.Id) ?? [];
            if (type == EntityTypes.Monitor && rowEvents.Count == 0)
                continue;
            string? mark = null;
            if (!ReferenceEquals(engine[name], entity))
                mark = HiddenEntityMark;
            else if (type == EntityTypes.Camera && ignoredCameras.Contains(name) && !appliedCameras.Contains(name) &&
                     !named.Contains(name))
                mark = IgnoredMonitorMark;
            rows.Add((GroupOf(type), new TimelineRow(new ThnRef(path, ThnElementKind.Entity, entity.Id), name,
                KindOf(type), rowEvents, mark) { SubRows = subRows.GetValueOrDefault(rowEvents, 1) }));
        }

        foreach (var name in missingOrder)
        {
            rows.Add((TimelineGroupKind.Missing, new TimelineRow(null, name, ObjectKinds.Thn.Missing, missing[name],
                name == NoTarget
                    ? "These events have no target."
                    : "No entity of this script has this name. The event works only when another script of the scene, " +
                      "for example the set script of the room, makes the entity.") { SubRows = subRows[missing[name]] }));
        }

        return rows;
    }

    /// <summary>
    /// Gives each bar of a row a sub-row, so that no two bars of one sub-row overlap (decision D131). Two bars overlap
    /// when one starts before the other ends, so touching bars share a sub-row. A diamond stays on sub-row 0.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>First, each bar that the last packed model has with the same time and length keeps its sub-row, unless a
    /// bar that kept its sub-row before it overlaps it. So an edit, an undo or a redo moves only the bars that it
    /// changes. A pass in time order alone is not enough: an edited bar that now starts earlier would keep its sub-row
    /// and push an unchanged bar away.</item>
    /// <item>Then each other bar, in the order of its time and then of the file, takes the lowest sub-row where no bar
    /// overlaps it.</item>
    /// <item>The count of the sub-rows ends at the highest sub-row with a bar, so empty sub-rows at the bottom go
    /// away.</item>
    /// </list>
    /// <para>With a pin (an open gesture), each event of the previous model keeps its sub-row instead, also when bars
    /// overlap.</para>
    /// </remarks>
    /// <param name="rowEvents">The events of the row, in the order of the file. Each bar gets its sub-row in the
    /// list.</param>
    /// <param name="withSubRow">Gets each bar whose sub-row is not 0, by its id.</param>
    /// <returns>The count of the sub-rows of the row, 1 or more.</returns>
    private static int Pack(List<TimelineEvent> rowEvents, SubRowMemory memory,
        Dictionary<ThnElementId, TimelineEvent> withSubRow)
    {
        var bars = rowEvents.Where(e => e.IsBar).OrderBy(e => e.Time).ThenBy(e => e.Index).ToList();
        if (bars.Count == 0)
            return 1;
        var placed = new List<List<TimelineEvent>>();
        var subRowOf = new Dictionary<ThnElementId, int>();

        void Place(TimelineEvent bar, int subRow)
        {
            while (placed.Count <= subRow)
                placed.Add([]);
            placed[subRow].Add(bar);
            subRowOf[bar.Ref.Id] = subRow;
        }

        bool Free(TimelineEvent bar, int subRow) =>
            subRow >= placed.Count || !placed[subRow].Any(o => o.Time < bar.End && bar.Time < o.End);

        var rest = new List<TimelineEvent>();
        foreach (var bar in bars)
        {
            if (memory.Pinned is { } pinned)
            {
                if (pinned.TryGetValue(bar.Ref.Id, out var old))
                    Place(bar, old.SubRow);
                else
                    rest.Add(bar);
            }
            else if (memory.Packed?.GetValueOrDefault(bar.Ref.Id) is { } known && known.Time == bar.Time &&
                     known.Length == bar.Length && Free(bar, known.SubRow))
            {
                Place(bar, known.SubRow);
            }
            else
            {
                rest.Add(bar);
            }
        }

        foreach (var bar in rest)
        {
            var subRow = 0;
            while (!Free(bar, subRow))
                subRow++;
            Place(bar, subRow);
        }

        for (var i = 0; i < rowEvents.Count; i++)
        {
            if (subRowOf.TryGetValue(rowEvents[i].Ref.Id, out var subRow) && subRow != 0)
            {
                rowEvents[i] = rowEvents[i] with { SubRow = subRow };
                withSubRow[rowEvents[i].Ref.Id] = rowEvents[i];
            }
        }

        return subRowOf.Values.Max() + 1;
    }

    /// <summary>The <c>Priority</c> of a monitor, as the engine reads it: the userprops key without letter case, the last
    /// one when two keys differ only in case, from the record part of the table, whatever its list part holds
    /// (ThnScript.cs:305-355). An entity without it has the empty text.</summary>
    private static string Priority(ThnTable entity)
    {
        if (entity["userprops"] is not ThnTable props)
            return "";
        var value = props.Record.LastOrDefault(f => f.Key.Equals("Priority", StringComparison.OrdinalIgnoreCase)).Value;
        return value switch
        {
            null => "",
            ThnText text => text.Value,
            // The engine uses the text of the VM value (ThnScript.cs:349).
            ThnNumber number => number.Value.ToString(CultureInfo.CurrentCulture),
            _ => ThnWriter.Value(value),
        };
    }

    private static EntityTypes? EntityType(ThnTable entity, SymbolNames names) => names.EntityType(entity["type"]);

    private static TimelineGroupKind GroupOf(EntityTypes? type) => type switch
    {
        EntityTypes.Camera or EntityTypes.Monitor => TimelineGroupKind.Cameras,
        EntityTypes.Deformable => TimelineGroupKind.Characters,
        EntityTypes.Compound => TimelineGroupKind.Props,
        EntityTypes.Light => TimelineGroupKind.Lights,
        EntityTypes.PSys => TimelineGroupKind.Effects,
        EntityTypes.Sound => TimelineGroupKind.Sounds,
        EntityTypes.Marker => TimelineGroupKind.Markers,
        EntityTypes.MotionPath => TimelineGroupKind.Paths,
        EntityTypes.Scene => TimelineGroupKind.Scene,
        _ => TimelineGroupKind.Other,
    };

    /// <summary>The kind of an entity type.</summary>
    internal static ObjectKind KindOf(EntityTypes? type) => type switch
    {
        EntityTypes.Camera => ObjectKinds.Thn.Camera,
        EntityTypes.Monitor => ObjectKinds.Thn.Monitor,
        EntityTypes.Deformable => ObjectKinds.Thn.Character,
        EntityTypes.Compound => ObjectKinds.Thn.Prop,
        EntityTypes.Light => ObjectKinds.Thn.Light,
        EntityTypes.PSys => ObjectKinds.Thn.Effect,
        EntityTypes.Sound => ObjectKinds.Thn.Sound,
        EntityTypes.Marker => ObjectKinds.Thn.Marker,
        EntityTypes.MotionPath => ObjectKinds.Thn.Path,
        EntityTypes.Scene => ObjectKinds.Thn.Scene,
        _ => ObjectKinds.Thn.Other,
    };

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";

    private static string Invariant(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>The place of a bar in the last packed model.</summary>
    private sealed record PackedBar(float Time, float Length, int SubRow);

    /// <summary>What the packing of a build knows of the models before it.</summary>
    /// <param name="Pinned">The events of the previous model while a gesture is open, or null.</param>
    /// <param name="Packed">The bars of the last packed model, or null for a new open.</param>
    /// <param name="Time">The time of the packing of the build.</param>
    private sealed record SubRowMemory(IReadOnlyDictionary<ThnElementId, TimelineEvent>? Pinned,
        IReadOnlyDictionary<ThnElementId, PackedBar>? Packed)
    {
        public TimeSpan Time { get; set; }
    }

    /// <summary>
    /// The types of the entities and events, as the engine reads them (ThnTypes.cs:41-78): a THN name or its text gives
    /// the value of the name, and a number gives its integer part. A name of another group is not a type.
    /// </summary>
    private sealed class SymbolNames(IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        // Each value of the environment has one name (see ThornEvaluator).
        private readonly Dictionary<long, string> eventNames = symbols
            .Where(s => s.Value.Group == nameof(EventTypes))
            .ToDictionary(s => s.Value.Value, s => s.Key);

        public EntityTypes? EntityType(ThnValue? value) =>
            Number(value, nameof(EntityTypes)) is { } number ? (EntityTypes)number : null;

        public (string Name, EventTypes? Kind) EventType(ThnValue value)
        {
            if (Number(value, nameof(EventTypes)) is not { } number)
                return (ThnWriter.Value(value), null);
            var name = (value as ThnSymbol)?.Name ?? eventNames.GetValueOrDefault(number) ?? ThnWriter.Value(value);
            return (name, (EventTypes)number);
        }

        private long? Number(ThnValue? value, string group) => value switch
        {
            ThnNumber number => (long)number.Value,
            ThnSymbol symbol when symbols.TryGetValue(symbol.Name, out var info) && info.Group == group => info.Value,
            ThnText text when symbols.TryGetValue(text.Value, out var info) && info.Group == group => info.Value,
            _ => null,
        };
    }
}
