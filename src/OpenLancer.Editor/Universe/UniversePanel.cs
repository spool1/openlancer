using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
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
}

/// <summary>
/// The read-only Universe panel: the systems of the edit view on the nav map grid, and their jump connections as lines.
/// A pair of jump gates is a solid line, and a pair of jump holes is a dashed line. A player lock makes a line red, and
/// an arrow points along a jump whose other end does not go back. A dot marks a new system that the mod has not saved.
/// A click selects a system, as a row of the Systems table of the Database does, and a double-click opens it in the
/// Scene. The panel takes no edit. The agent tool <c>capture_panel</c> gives an image of it.
/// </summary>
internal sealed class UniversePanel(EditorWindow window)
{
    /// <summary>The id of the panel in the layout. The agent tool <c>capture_panel</c> names the panel with it.</summary>
    public const string PanelId = "universe";

    /// <summary>The text of the info icon of the panel (AGENTS.md UI rule 10).</summary>
    internal static readonly HelpText Help = new("Universe",
        "A map of the systems of the edit view on the nav map grid, with their jump connections. The panel is " +
        "read-only. A solid line is a pair of jump gates, and a dashed line is a pair of jump holes. A red line has a " +
        "player lock in a new game. An arrow points along a jump whose other end does not go back. A blue dot marks a " +
        "new system that the mod has not saved. A click selects a system, and a double-click opens it in the Scene.",
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
    private const float LabelGap = 3;
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
        var selected = SelectedSystem(session.Edits);

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(canvasMin, canvasMin + canvasSize, true);
        Grid(drawList, frame);
        foreach (var line in model.Lines)
        {
            Line(drawList, frame, model, line, selected);
        }

        // The labels use the caption font.
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        foreach (var system in model.Systems)
        {
            System(drawList, frame, system, selected, ReferenceEquals(system, target));
        }

        ImGui.PopFont();

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

    /// <summary>The nickname of the selected system: a system of the loaded game data or a <c>[system]</c> of the edit
    /// view. Null for each other selection.</summary>
    private string? SelectedSystem(EditSession edits) => window.Selection switch
    {
        StarSystem system => system.Nickname,
        SectionRef section when edits.IsSystemSection(section) => section.Id.Key,
        _ => null,
    };

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

    /// <summary>Draws a line between two placed systems. The lines of the selected system are wider.</summary>
    private static void Line(ImDrawListPtr drawList, UniverseFrame frame, UniverseModel model, UniverseMapLine line,
        string? selected)
    {
        if (model.System(line.From)?.Place is not { } from || model.System(line.To)?.Place is not { } to)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var (start, end) = UniverseFrame.Segment(line, frame.ToScreen(from), frame.ToScreen(to), LineGap * scale);
        var color = EditorTheme.ToAbgr(line.PlayerLocked ? EditorTheme.Error : ObjectKinds.JumpGate.Color);
        var touches = selected != null && (line.From.Equals(selected, StringComparison.OrdinalIgnoreCase) ||
                                           line.To.Equals(selected, StringComparison.OrdinalIgnoreCase));
        var width = (touches ? SelectedLineWidth : LineWidth) * scale;
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

    /// <summary>Draws the dot of a placed system and its label in the current font. A ring marks the selected system and
    /// the system under the mouse, and a blue dot marks a new system.</summary>
    private static void System(ImDrawListPtr drawList, UniverseFrame frame, UniverseMapSystem system, string? selected,
        bool hovered)
    {
        if (system.Place is not { } place)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var center = frame.ToScreen(place);
        var radius = DotRadius * scale;
        drawList.AddCircleFilled(center, radius, ObjectKinds.Sun.Abgr);
        if (system.Nickname.Equals(selected, StringComparison.OrdinalIgnoreCase))
        {
            drawList.AddCircle(center, RingRadius * scale, ImGui.GetColorU32(EditorTheme.AccentText), 0, 2 * scale);
        }
        else if (hovered)
        {
            drawList.AddCircle(center, RingRadius * scale, ImGui.GetColorU32(EditorTheme.TextBright), 0, scale);
        }

        if (system.IsNew)
        {
            var mark = MarkRadius * scale;
            drawList.AddCircleFilled(center + new Vector2(radius, -radius), mark, ImGui.GetColorU32(EditorTheme.AccentText));
        }

        var size = ImGui.CalcTextSize(system.Label);
        drawList.AddText(new Vector2(MathF.Round(center.X - size.X / 2), MathF.Round(center.Y + radius + LabelGap * scale)),
            ImGui.GetColorU32(EditorTheme.Text), system.Label);
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
