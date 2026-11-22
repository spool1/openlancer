using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Universe;

/// <summary>
/// The fit of the map area of the Universe panel into its canvas: one scale for both axes, so the grid cells stay square,
/// and the area in the middle of the canvas. A larger y of the nav map is lower in the canvas, as on the nav map of the
/// game.
/// </summary>
/// <param name="Origin">The screen point of <paramref name="Min"/>.</param>
/// <param name="Scale">The pixels of one grid cell.</param>
/// <param name="Min">The smallest corner of the area with its margin, in grid cells.</param>
internal readonly record struct UniverseFrame(Vector2 Origin, float Scale, Vector2 Min)
{
    /// <summary>The space around the area, in grid cells. The labels under the dots at the edge need it.</summary>
    public const float Margin = 0.75f;

    /// <summary>The frame that shows the area of <paramref name="bounds"/> with its margin, zoomed to fit the
    /// canvas.</summary>
    public static UniverseFrame Fit((Vector2 Min, Vector2 Max) bounds, Vector2 canvasMin, Vector2 canvasSize)
    {
        var min = bounds.Min - new Vector2(Margin);
        var size = bounds.Max - bounds.Min + new Vector2(2 * Margin);
        var scale = MathF.Max(0, MathF.Min(canvasSize.X / size.X, canvasSize.Y / size.Y));
        return new UniverseFrame(canvasMin + (canvasSize - size * scale) / 2, scale, min);
    }

    /// <summary>The screen point of a place on the nav map grid.</summary>
    public Vector2 ToScreen(Vector2 place) => Origin + (place - Min) * Scale;

    /// <summary>The placed system whose dot is nearest to a screen point, when it is at most <paramref name="radius"/>
    /// pixels away, else null.</summary>
    public UniverseMapSystem? Hit(IEnumerable<UniverseMapSystem> systems, Vector2 point, float radius)
    {
        var frame = this;
        return systems.Where(s => s.Place != null)
            .Select(s => (System: s, Distance: Vector2.Distance(frame.ToScreen(s.Place!.Value), point)))
            .Where(s => s.Distance <= radius)
            .OrderBy(s => s.Distance)
            .Select(s => s.System)
            .FirstOrDefault();
    }

    /// <summary>
    /// The screen ends of a line, moved to the side by its offset. The side comes from the order of the two nicknames,
    /// not from the direction of the line. So two lines between the same two systems go to two sides also when they
    /// have two directions.
    /// </summary>
    /// <param name="gap">The pixels between two lines.</param>
    public static (Vector2 Start, Vector2 End) Segment(UniverseMapLine line, Vector2 from, Vector2 to, float gap)
    {
        var direction = to - from;
        if (line.Offset == 0 || direction.LengthSquared() == 0)
        {
            return (from, to);
        }

        var forward = string.CompareOrdinal(line.From.ToLowerInvariant(), line.To.ToLowerInvariant()) < 0;
        var along = Vector2.Normalize(forward ? direction : -direction);
        var side = new Vector2(-along.Y, along.X) * line.Offset * gap;
        return (from + side, to + side);
    }

    /// <summary>The dashes of a dashed line from one point to another point. The first dash starts at the start.</summary>
    /// <param name="dash">The length of a dash in pixels.</param>
    /// <param name="space">The pixels between two dashes.</param>
    public static IEnumerable<(Vector2 Start, Vector2 End)> Dashes(Vector2 start, Vector2 end, float dash, float space)
    {
        var length = Vector2.Distance(start, end);
        if (length == 0 || dash <= 0)
        {
            yield break;
        }

        var along = (end - start) / length;
        for (var at = 0f; at < length; at += dash + space)
        {
            yield return (start + along * at, start + along * MathF.Min(at + dash, length));
        }
    }

    /// <summary>
    /// The top left corner of the label of each system, or null for a label that does not show. The labels of
    /// <paramref name="first"/> always show under their dots. Each other label shows under its dot, else over it. That
    /// box must be in the canvas, and it must hit no label that shows and no dot of another system. The other labels take
    /// their places in the order of the systems. So at a small zoom, the labels do not cover each other or the dots, and
    /// the tooltip of a dot gives each name.
    /// </summary>
    /// <param name="dots">The screen point of the dot of each system, or null for a system without a place.</param>
    /// <param name="sizes">The size of the label of each system, in pixels.</param>
    /// <param name="dotRadius">The radius of a dot, in pixels.</param>
    /// <param name="offset">The pixels from the middle of a dot to its label.</param>
    /// <param name="canvas">The smallest and the largest corner of the canvas.</param>
    /// <param name="first">The indexes of the systems whose labels always show: the selected and the hovered
    /// system.</param>
    public static Vector2?[] Labels(IReadOnlyList<Vector2?> dots, IReadOnlyList<Vector2> sizes, float dotRadius,
        float offset, (Vector2 Min, Vector2 Max) canvas, IReadOnlyCollection<int> first)
    {
        var places = new Vector2?[dots.Count];
        var shown = new List<(Vector2 Min, Vector2 Max)>();
        foreach (var i in first)
        {
            if (dots[i] is { } dot)
            {
                places[i] = Below(dot, sizes[i], offset);
                shown.Add((places[i]!.Value, places[i]!.Value + sizes[i]));
            }
        }

        for (var i = 0; i < dots.Count; i++)
        {
            if (dots[i] is not { } dot || first.Contains(i))
            {
                continue;
            }

            foreach (var place in new[] { Below(dot, sizes[i], offset), Above(dot, sizes[i], offset) })
            {
                var box = (Min: place, Max: place + sizes[i]);
                if (Inside(box, canvas) && !shown.Any(s => Hits(s, box)) && !HitsDot(box, dots, i, dotRadius))
                {
                    places[i] = place;
                    shown.Add(box);
                    break;
                }
            }
        }

        return places;
    }

    private static Vector2 Below(Vector2 dot, Vector2 size, float offset) =>
        new(MathF.Round(dot.X - size.X / 2), MathF.Round(dot.Y + offset));

    private static Vector2 Above(Vector2 dot, Vector2 size, float offset) =>
        new(MathF.Round(dot.X - size.X / 2), MathF.Round(dot.Y - offset - size.Y));

    private static bool Inside((Vector2 Min, Vector2 Max) box, (Vector2 Min, Vector2 Max) canvas) =>
        box.Min.X >= canvas.Min.X && box.Min.Y >= canvas.Min.Y && box.Max.X <= canvas.Max.X && box.Max.Y <= canvas.Max.Y;

    private static bool Hits((Vector2 Min, Vector2 Max) a, (Vector2 Min, Vector2 Max) b) =>
        a.Min.X < b.Max.X && a.Max.X > b.Min.X && a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y;

    /// <summary>True when a box hits the dot of a system other than the system <paramref name="own"/>.</summary>
    private static bool HitsDot((Vector2 Min, Vector2 Max) box, IReadOnlyList<Vector2?> dots, int own, float radius)
    {
        var half = new Vector2(radius);
        for (var j = 0; j < dots.Count; j++)
        {
            if (j != own && dots[j] is { } dot && Hits(box, (dot - half, dot + half)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The read-only Universe panel: the systems of the edit view on the nav map grid, and their jump connections as lines.
/// A pair of jump gates is a solid line, and a pair of jump holes is a dashed line. A player lock makes a line red, and
/// an arrow points along a jump whose other end does not go back. A dot marks a new system that the mod has not saved.
/// A label shows only where it has space. A click selects a system, as a row of the Systems table of the Database does,
/// and a double-click opens it in the Scene. A selection in another view puts the ring on its system. The panel takes no
/// edit. The agent tool <c>capture_panel</c> gives an image of it.
/// </summary>
internal sealed class UniversePanel(EditorWindow window)
{
    /// <summary>The id of the panel in the layout. The agent tool <c>capture_panel</c> names the panel with it.</summary>
    public const string PanelId = "universe";

    /// <summary>The text of the info icon of the panel (AGENTS.md UI rule 10). The first paragraph is the legend, and
    /// the second paragraph tells the labels and the mouse.</summary>
    internal static readonly HelpText Help = new("Universe",
        "A read-only map of the systems of the edit view on the nav map grid, with their jump connections. A solid " +
        "line is a pair of jump gates or an airlock gate. A dashed line is a pair of jump holes. A red line has a " +
        "player lock in a new game. An arrow points along a jump whose other end does not go back. A blue dot marks a " +
        "new system that the mod has not saved.\n\n" +
        "A name shows only where it has space. Hold the mouse on a dot to see its name, or make the panel larger. A " +
        "click selects a system, and a double-click opens it in the Scene. The system of a selected object gets a ring.",
        "Nothing. The place of a system is the pos line of its [system] in universe.ini. Change it in the Inspector of " +
        "the system. The agent tools system_create and connection_create add systems and connections.");

    /// <summary>The empty state of a profile whose edit view has no systems.</summary>
    internal static readonly EmptyState NoSystems =
        new("The universe of this profile has no systems.", "Open a profile whose universe.ini has systems.");

    // Sizes in unscaled points.
    private const float DotRadius = 5;
    private const float HitRadius = 10;
    private const float RingRadius = 9;
    private const float LineWidth = 1.5f;
    private const float SelectedLineWidth = 3;
    private const float LineGap = 4;
    private const float Dash = 6;
    private const float DashSpace = 4;
    private const float ArrowSize = 6;
    private const float MarkRadius = 2.5f;
    // From the middle of a dot to its label. The label stays out of the ring.
    private const float LabelOffset = 11;
    private const float LabelPadding = 2;
    private const float NotePadding = 6;

    private UniverseMap map = new();

    /// <summary>Forgets the model. Call it when the profile closes.</summary>
    public void Reset() => map = new UniverseMap();

    /// <summary>Draws the items of the toolbar strip: the info icon and the count of the systems.</summary>
    public void Toolbar()
    {
        InfoIcon.Draw("universe-help", Help);
        if (window.Session is { GameData: { } data } session)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(CountText(ModelOf(session, data)));
        }
    }

    public void Draw()
    {
        if (window.Session is not { GameData: { } data } session)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        var model = ModelOf(session, data);
        if (model.Systems.Count == 0)
        {
            PanelStates.Message(NoSystems);
            return;
        }

        var canvasMin = ImGui.GetCursorScreenPos();
        var canvasSize = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
        ImGui.InvisibleButton("##universe-map", canvasSize);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var scale = ImGuiHelper.Scale;
        var frame = UniverseFrame.Fit(model.Bounds, canvasMin, canvasSize);
        var target = hovered ? frame.Hit(model.Systems, ImGui.GetMousePos(), HitRadius * scale) : null;
        // An item of the Scene gives the section that it shows.
        var selection = window.Selection is SystemObject or Zone or LightSource or Nebula or AsteroidField
            ? window.Scene.SectionOf(window.Selection)
            : window.Selection;
        var highlight = map.Highlight(session.Edits, selection);

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(canvasMin, canvasMin + canvasSize, true);
        Grid(drawList, frame);
        foreach (var line in model.Lines)
        {
            Line(drawList, frame, model, line, highlight.IsWide(line));
        }

        foreach (var system in model.Systems)
        {
            Dot(drawList, frame, system, highlight.HasRing(system), ReferenceEquals(system, target));
        }

        Labels(drawList, frame, model, (canvasMin, canvasMin + canvasSize), highlight, target);
        Note(drawList, model, canvasMin, canvasSize);
        drawList.PopClipRect();

        if (target == null)
        {
            return;
        }

        ItemTooltip.Wrapped(target.Tooltip);
        if (clicked && map.ItemOf(target.Nickname) is { } item)
        {
            window.Selection = item;
        }

        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            window.OpenSystem(target.Nickname, null);
        }
    }

    /// <summary>The text of the toolbar: the count of the systems, and of the new systems that the mod has not
    /// saved.</summary>
    internal static string CountText(UniverseModel model)
    {
        var systems = model.Systems.Count == 1 ? "1 system" : $"{model.Systems.Count} systems";
        var unsaved = model.Systems.Count(s => s.IsNew);
        return unsaved == 0 ? systems : $"{systems}, {unsaved} new";
    }

    private UniverseModel ModelOf(EditorSession session, GameDataManager data) =>
        map.Of(session.Edits, session.Texts, session.Version, data, nickname => data.Items.Systems.Get(nickname));

    /// <summary>Draws a line at each whole number of the nav map grid, and its border.</summary>
    private static void Grid(ImDrawListPtr drawList, UniverseFrame frame)
    {
        var inner = ImGui.GetColorU32(EditorTheme.InnerLine);
        for (var x = 0; x <= UniverseModel.GridWidth; x++)
        {
            drawList.AddLine(frame.ToScreen(new Vector2(x, 0)), frame.ToScreen(new Vector2(x, UniverseModel.GridHeight)),
                inner);
        }

        for (var y = 0; y <= UniverseModel.GridHeight; y++)
        {
            drawList.AddLine(frame.ToScreen(new Vector2(0, y)), frame.ToScreen(new Vector2(UniverseModel.GridWidth, y)),
                inner);
        }

        drawList.AddRect(frame.ToScreen(Vector2.Zero),
            frame.ToScreen(new Vector2(UniverseModel.GridWidth, UniverseModel.GridHeight)),
            ImGui.GetColorU32(EditorTheme.GridLine));
    }

    /// <summary>Draws a line between two placed systems. A highlighted line is wider.</summary>
    private static void Line(ImDrawListPtr drawList, UniverseFrame frame, UniverseModel model, UniverseMapLine line,
        bool wide)
    {
        if (model.System(line.From)?.Place is not { } from || model.System(line.To)?.Place is not { } to)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var (start, end) = UniverseFrame.Segment(line, frame.ToScreen(from), frame.ToScreen(to), LineGap * scale);
        var color = EditorTheme.ToAbgr(line.PlayerLocked ? EditorTheme.Error : ObjectKinds.JumpGate.Color);
        var width = (wide ? SelectedLineWidth : LineWidth) * scale;
        if (line.Dashed)
        {
            foreach (var (a, b) in UniverseFrame.Dashes(start, end, Dash * scale, DashSpace * scale))
            {
                drawList.AddLine(a, b, color, width);
            }
        }
        else
        {
            drawList.AddLine(start, end, color, width);
        }

        if (line.OneWay && Vector2.Distance(start, end) > 0)
        {
            // An arrow in the middle of the line points to the system that the jump goes to.
            var along = Vector2.Normalize(end - start);
            var side = new Vector2(-along.Y, along.X);
            var tip = (start + end) / 2 + along * (ArrowSize * scale / 2);
            var back = tip - along * (ArrowSize * scale);
            drawList.AddTriangleFilled(tip, back + side * (ArrowSize * scale / 2), back - side * (ArrowSize * scale / 2),
                color);
        }
    }

    /// <summary>Draws the dot of a placed system. A ring marks the highlighted system and the system under the mouse,
    /// and a blue dot marks a new system.</summary>
    private static void Dot(ImDrawListPtr drawList, UniverseFrame frame, UniverseMapSystem system, bool ring, bool hovered)
    {
        if (system.Place is not { } place)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var center = frame.ToScreen(place);
        var radius = DotRadius * scale;
        drawList.AddCircleFilled(center, radius, ObjectKinds.Sun.Abgr);
        if (ring)
        {
            drawList.AddCircle(center, RingRadius * scale, ImGui.GetColorU32(EditorTheme.AccentText), 0, 2 * scale);
        }
        else if (hovered)
        {
            drawList.AddCircle(center, RingRadius * scale, ImGui.GetColorU32(EditorTheme.TextBright), 0, scale);
        }

        if (system.IsNew)
        {
            drawList.AddCircleFilled(center + new Vector2(radius, -radius), MarkRadius * scale,
                ImGui.GetColorU32(EditorTheme.AccentText));
        }
    }

    /// <summary>
    /// Draws the labels in the caption font where they have space (see <see cref="UniverseFrame.Labels"/>). The labels of
    /// the highlighted system and of the system under the mouse always show. They draw last, on a box of the panel
    /// color, so that the lines and the dots do not cover them.
    /// </summary>
    private static void Labels(ImDrawListPtr drawList, UniverseFrame frame, UniverseModel model,
        (Vector2 Min, Vector2 Max) canvas, UniverseHighlight highlight, UniverseMapSystem? target)
    {
        var scale = ImGuiHelper.Scale;
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        var systems = model.Systems;
        var dots = systems.Select(s => s.Place is { } place ? frame.ToScreen(place) : (Vector2?)null).ToList();
        var sizes = systems.Select(s => ImGui.CalcTextSize(s.Label)).ToList();
        var first = Enumerable.Range(0, systems.Count)
            .Where(i => highlight.HasRing(systems[i]) || ReferenceEquals(systems[i], target))
            .ToList();
        var places = UniverseFrame.Labels(dots, sizes, DotRadius * scale, LabelOffset * scale, canvas, first);
        var text = ImGui.GetColorU32(EditorTheme.Text);
        for (var i = 0; i < systems.Count; i++)
        {
            if (places[i] is { } place && !first.Contains(i))
            {
                drawList.AddText(place, text, systems[i].Label);
            }
        }

        var padding = new Vector2(LabelPadding * scale);
        foreach (var i in first)
        {
            if (places[i] is { } place)
            {
                drawList.AddRectFilled(place - padding, place + sizes[i] + padding, ImGui.GetColorU32(EditorTheme.Panel),
                    LabelPadding * scale);
                drawList.AddText(place, ImGui.GetColorU32(EditorTheme.TextBright), systems[i].Label);
            }
        }

        ImGui.PopFont();
    }

    /// <summary>Names the systems without a place on the map, at the bottom left of the canvas.</summary>
    private static void Note(ImDrawListPtr drawList, UniverseModel model, Vector2 canvasMin, Vector2 canvasSize)
    {
        var unplaced = model.Unplaced.Select(s => s.Nickname).ToList();
        if (unplaced.Count == 0)
        {
            return;
        }

        var text = UnplacedText(unplaced);
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        var padding = NotePadding * ImGuiHelper.Scale;
        var size = ImGui.CalcTextSize(text);
        drawList.AddText(new Vector2(canvasMin.X + padding, canvasMin.Y + canvasSize.Y - padding - size.Y),
            ImGui.GetColorU32(EditorTheme.TextDim), text);
        ImGui.PopFont();
    }

    /// <summary>The note of the systems without a place on the map.</summary>
    internal static string UnplacedText(IReadOnlyList<string> nicknames) =>
        $"Not on the map because the [system] has no position: {string.Join(", ", nicknames)}.";
}
