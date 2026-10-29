using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.World;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The 2D layer over the 3D scene: the markers (<see cref="SceneMarkers"/>), the labels, and the corner brackets around
/// the selected and the hovered item. It also shows the caption of the pick stack.
/// </summary>
/// <param name="objects">The world object of each system object, for the model bounds of the brackets.</param>
internal sealed class SceneOverlay(EditorWindow window, IReadOnlyDictionary<SystemObject, GameObject> objects)
{
    // Sizes in unscaled points.
    private const float LabelGap = 4;
    private const float PillRounding = 3;
    private const float BracketPadding = 4;
    private const float BracketMinSize = 16;
    private const float BracketArm = 10;
    private const float SelectionThickness = 2;
    private const float HoverThickness = 1.5f;
    private const float HintMargin = 8;
    private const float PulseThickness = 2;

    // The pulse ring of an agent edit grows from the brackets to this factor of their radius in each period.
    private const float PulseGrowth = 0.6f;
    private const float PulsePeriod = 1;
    private static readonly Vector2 PillPadding = new(5, 1);
    private static readonly Vector2 ShadowOffset = new(1, 1);

    private static readonly uint ShadowColor = EditorTheme.ToAbgr(EditorTheme.Shadow);
    private static readonly uint PillColor = EditorTheme.ToAbgr(EditorTheme.Backdrop with { W = 180 / 255f });
    private static readonly uint LabelColor = EditorTheme.ToAbgr(EditorTheme.Text);
    private static readonly uint SelectedLabelColor = EditorTheme.ToAbgr(EditorTheme.TextBright);
    private static readonly uint SelectionColor = EditorTheme.ToAbgr(EditorTheme.Accent);
    private static readonly uint HoverColor = EditorTheme.ToAbgr(EditorTheme.Highlight);
    private static readonly Vector4 PulseColor = EditorTheme.Agent;

    private readonly List<SceneMarker> labelOrder = [];
    private readonly List<(Vector2 Min, Vector2 Max)> placedLabels = [];

    // The glyphs of the kinds with labels. A label does not cover the glyph of a kind with the same or a higher label
    // priority.
    private readonly List<(Vector2 Min, Vector2 Max, object Item, int Priority)> labelledGlyphs = [];

    // The label text and its size of each item, at the scale in labelScale.
    private readonly Dictionary<object, (string Text, Vector2 Size)> labels = [];
    private float labelScale;

    private SceneProjection projection;
    private SystemIndex? index;

    /// <summary>The markers of this frame. The picker finds the marker under the mouse in them.</summary>
    public SceneMarkers Markers { get; } = new();

    /// <summary>Gives the label of an item that has no name in the game data, for example an item of the open mission,
    /// or null.</summary>
    public Func<object, string?>? Labels { get; set; }

    /// <summary>The items that the cross-links of the Mission tab highlight, for example the gate of a lock line
    /// (section 7.3 of the M6a design). Each one gets brackets and its label.</summary>
    public IReadOnlyCollection<object> Highlighted { get; set; } = [];

    /// <summary>The items of the agent edits whose pulse runs, with the time since the start of each pulse (section 8.5 of
    /// the MCP1 design). The Scene sets them in each frame.</summary>
    public IReadOnlyList<(object Item, TimeSpan Age)> Pulses { get; set; } = [];

    /// <summary>Forgets the markers and the cached labels. Call it when a system opens or closes.</summary>
    public void Reset()
    {
        Markers.Clear();
        labels.Clear();
    }

    /// <summary>Forgets the cached labels, so that each label reads its name again. Call it when a name
    /// changes.</summary>
    public void ResetLabels() => labels.Clear();

    /// <summary>
    /// Projects the markers of the system for this frame. Call it after the camera update and before the picker and
    /// <see cref="Draw"/>.
    /// </summary>
    public void Update(SceneProjection sceneProjection, SystemIndex systemIndex)
    {
        projection = sceneProjection;
        index = systemIndex;
        Markers.Update(sceneProjection, systemIndex, window.Settings, window.Selection);
    }

    /// <summary>Draws the markers, the brackets, the labels and the caption into the current window, clipped to the
    /// scene.</summary>
    /// <param name="hovered">The item under the mouse, or null.</param>
    /// <param name="widget">The screen rectangle of the axis widget. Labels do not go there.</param>
    /// <param name="hint">The caption of the pick stack (see <see cref="PickCycle.Hint"/>), or null.</param>
    public void Draw(object? hovered, (Vector2 Min, Vector2 Max) widget, string? hint = null)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(projection.Origin, projection.Origin + projection.Size, true);
        // The zone diamonds draw below the glyphs. A zone is often at the position of an object, for example the
        // exclusion zone of a station, and the glyph of the object must stay readable.
        foreach (var marker in Markers.Items)
        {
            if (marker.Item is Zone)
            {
                DrawDiamond(drawList, marker);
            }
        }

        foreach (var marker in Markers.Items)
        {
            if (marker.Item is not Zone)
            {
                DrawGlyph(drawList, marker);
            }
        }

        foreach (var (item, age) in Pulses)
        {
            DrawPulse(drawList, item, age);
        }

        if (hovered != null && !Equals(hovered, window.Selection))
        {
            DrawBrackets(drawList, hovered, HoverColor, HoverThickness);
        }

        foreach (var item in Highlighted)
        {
            if (!Equals(item, window.Selection) && !Equals(item, hovered))
            {
                DrawBrackets(drawList, item, HoverColor, HoverThickness);
            }
        }

        if (window.Selection != null)
        {
            DrawBrackets(drawList, window.Selection, SelectionColor, SelectionThickness);
        }

        DrawLabels(drawList, hovered, widget);
        if (hint != null)
        {
            DrawHint(drawList, hint);
        }

        drawList.PopClipRect();
    }

    /// <summary>The caption of the pick stack at the bottom-left of the scene, on a dark pill.</summary>
    private void DrawHint(ImDrawListPtr drawList, string hint)
    {
        var scale = ImGuiHelper.Scale;
        var fontSize = Pixels(EditorTheme.CaptionSize);
        var padding = PillPadding * scale;
        var size = ImGuiHelper.Roboto.CalcTextSizeA(fontSize, float.MaxValue, 0, hint) + 2 * padding;
        var margin = HintMargin * scale;
        var min = new Vector2(MathF.Round(projection.Origin.X + margin),
            MathF.Round(projection.Origin.Y + projection.Size.Y - margin - size.Y));
        drawList.AddRectFilled(min, min + size, PillColor, PillRounding * scale);
        drawList.AddText(ImGuiHelper.Roboto, fontSize, min + padding, LabelColor, hint);
    }

    /// <summary>
    /// Projects points and adds them to the screen bounds. Returns false when a point is behind the camera, because
    /// the projection of such a point flips to the wrong side of the screen.
    /// </summary>
    private bool ProjectAll(ReadOnlySpan<Vector3> points, ref Vector2 min, ref Vector2 max)
    {
        foreach (var point in points)
        {
            if (!projection.Project(point, out var screen, out _))
            {
                return false;
            }

            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }

        return true;
    }

    private static void DrawGlyph(ImDrawListPtr drawList, SceneMarker marker) =>
        Glyphs.DrawShadowed(drawList, marker.Kind.Icon, Pixels(marker.Kind.MarkerSize), marker.Screen, marker.Color,
            marker.Alpha);

    /// <summary>A filled diamond as wide as the marker size, with a drop shadow.</summary>
    private static void DrawDiamond(ImDrawListPtr drawList, SceneMarker marker)
    {
        var half = Pixels(marker.Kind.MarkerSize) / 2;
        var center = marker.Screen;
        var shadow = center + ShadowOffset * ImGuiHelper.Scale;
        drawList.AddQuadFilled(shadow + new Vector2(0, -half), shadow + new Vector2(half, 0),
            shadow + new Vector2(0, half), shadow + new Vector2(-half, 0),
            ImGui.GetColorU32(ShadowColor, marker.Alpha));
        drawList.AddQuadFilled(center + new Vector2(0, -half), center + new Vector2(half, 0),
            center + new Vector2(0, half), center + new Vector2(-half, 0),
            ImGui.GetColorU32(marker.Color, marker.Alpha));
    }

    /// <summary>
    /// Draws four L-shaped corners around an item. The arms are 10 points long, or a quarter of the side for a small
    /// bracket.
    /// </summary>
    private void DrawBrackets(ImDrawListPtr drawList, object item, uint color, float thickness)
    {
        if (!BracketBounds(item, out var min, out var max))
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var center = (min + max) / 2;
        var half = Vector2.Max((max - min) / 2, new Vector2(BracketMinSize * scale / 2));
        min = center - half;
        max = center + half;
        var arm = Vector2.Min(new Vector2(BracketArm * scale), 2 * half * 0.25f);
        thickness *= scale;
        Corner(drawList, min, new Vector2(arm.X, arm.Y), color, thickness);
        Corner(drawList, new Vector2(max.X, min.Y), new Vector2(-arm.X, arm.Y), color, thickness);
        Corner(drawList, max, new Vector2(-arm.X, -arm.Y), color, thickness);
        Corner(drawList, new Vector2(min.X, max.Y), new Vector2(arm.X, -arm.Y), color, thickness);
    }

    /// <summary>
    /// The pulse ring of an item of an agent edit, in the agent color. A ring around the brackets fades over the time of
    /// the pulse. A wave grows from the ring once in each second.
    /// </summary>
    private void DrawPulse(ImDrawListPtr drawList, object item, TimeSpan age)
    {
        if (!BracketBounds(item, out var min, out var max))
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var radius = MathF.Max(Vector2.Distance(min, max) / 2, BracketMinSize * scale / 2) + BracketPadding * scale;
        var center = (min + max) / 2;
        var seconds = (float)age.TotalSeconds;
        var fade = Math.Clamp(1 - seconds / (float)AgentMarks.PulseTime.TotalSeconds, 0, 1);
        var cycle = seconds % PulsePeriod / PulsePeriod;
        var thickness = PulseThickness * scale;
        drawList.AddCircle(center, radius, ImGui.GetColorU32(PulseColor with { W = fade }), 0, thickness);
        drawList.AddCircle(center, radius * (1 + PulseGrowth * cycle),
            ImGui.GetColorU32(PulseColor with { W = fade * (1 - cycle) }), 0, thickness);
    }

    private static void Corner(ImDrawListPtr drawList, Vector2 corner, Vector2 arm, uint color, float thickness)
    {
        drawList.PathLineTo(corner + new Vector2(0, arm.Y));
        drawList.PathLineTo(corner);
        drawList.PathLineTo(corner + new Vector2(arm.X, 0));
        drawList.PathStroke(color, ImDrawFlags.None, thickness);
    }

    /// <summary>
    /// The screen bounds of an item for its brackets. A zone, a light, a nebula or a field has no shape on the screen, so
    /// its bounds are the box of its marker. A nebula or a field has its marker at the center of its zone. Returns false
    /// when the item is behind the camera or has no position.
    /// </summary>
    private bool BracketBounds(object item, out Vector2 min, out Vector2 max)
    {
        if (item is SystemObject obj)
        {
            return BracketBounds(obj, out min, out max);
        }

        Vector3? position = item switch
        {
            LightSource light => light.Light.Position,
            _ => SceneItems.ZoneOf(item)?.Position,
        };
        min = max = default;
        if (position == null)
        {
            // An item without a position in the system, for example an item of the open mission, has the box of its
            // marker.
            if (Markers.Find(item) is not { } marker)
            {
                return false;
            }

            var markerHalf = new Vector2(GlyphBoxHalf(marker.Kind));
            (min, max) = (marker.Screen - markerHalf, marker.Screen + markerHalf);
            return true;
        }

        if (index?.KindOf(item) is not { } kind || !projection.Project(position.Value, out var screen, out _))
        {
            return false;
        }

        var half = new Vector2(GlyphBoxHalf(kind));
        min = screen - half;
        max = screen + half;
        return true;
    }

    /// <summary>
    /// The screen bounds of an object for its brackets: the projected box of its model, or the projected radius of a
    /// sun. When markers show, the bounds also hold the glyph, so that the brackets never cut through it. An object
    /// with no model, or with a box corner behind the camera, gets the glyph box. Returns false when the object is
    /// behind the camera.
    /// </summary>
    private bool BracketBounds(SystemObject obj, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue);
        max = new Vector2(float.MinValue);
        if (!projection.Project(obj.Position, out var screen, out _))
        {
            return false;
        }

        var hasShape = false;
        Span<Vector3> points = stackalloc Vector3[BoundingBox.CornerCount];
        if (objects.TryGetValue(obj, out var gameObject) &&
            gameObject.RenderComponent is ModelRenderer { Model: { } model })
        {
            model.GetBoundingBox().GetCorners(points);
            var world = gameObject.Transform.Matrix();
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = Vector3.Transform(points[i], world);
            }

            hasShape = ProjectAll(points, ref min, ref max);
        }
        else if (obj.Star is { } star)
        {
            // A sun has no model. The camera right and up vectors give the screen size of its sphere.
            var view = projection.View;
            var right = new Vector3(view.M11, view.M21, view.M31) * star.Radius;
            var up = new Vector3(view.M12, view.M22, view.M32) * star.Radius;
            points[0] = obj.Position - right - up;
            points[1] = obj.Position + right - up;
            points[2] = obj.Position + right + up;
            points[3] = obj.Position - right + up;
            hasShape = ProjectAll(points[..4], ref min, ref max);
        }

        if (!hasShape || window.Settings.ShowMarkers)
        {
            var half = new Vector2(GlyphBoxHalf(ObjectKinds.Of(obj)));
            min = hasShape ? Vector2.Min(min, screen - half) : screen - half;
            max = hasShape ? Vector2.Max(max, screen + half) : screen + half;
        }

        return true;
    }

    /// <summary>Half the size of the glyph box of a kind: the glyph and the bracket padding, in pixels.</summary>
    private static float GlyphBoxHalf(ObjectKind kind) =>
        Pixels(kind.MarkerSize) / 2 + BracketPadding * ImGuiHelper.Scale;

    /// <summary>
    /// Draws the labels on dark pills. The selected and hovered objects get their labels first, then the kinds with a
    /// higher label priority, then the nearer objects. A label goes to the right of its glyph, or to the left when the
    /// right side is not free. A side is not free when the label would cover a label that is already placed, the axis
    /// widget, or the glyph of a kind with the same or a higher label priority. A label with no free side does not
    /// draw, except for the selected and the hovered object.
    /// </summary>
    /// <param name="widget">The screen rectangle of the axis widget.</param>
    private void DrawLabels(ImDrawListPtr drawList, object? hovered, (Vector2 Min, Vector2 Max) widget)
    {
        var scale = ImGuiHelper.Scale;
        if (labelScale != scale)
        {
            labels.Clear();
            labelScale = scale;
        }

        var showLabels = window.Settings.ShowLabels;
        labelOrder.Clear();
        foreach (var marker in Markers.Items)
        {
            if (Highlight(marker.Item, hovered) > 0 || (showLabels && marker.Kind.LabelPriority > 0))
            {
                labelOrder.Add(marker);
            }
        }

        labelOrder.Sort((a, b) =>
        {
            var order = Highlight(b.Item, hovered).CompareTo(Highlight(a.Item, hovered));
            if (order == 0)
            {
                order = b.Kind.LabelPriority.CompareTo(a.Kind.LabelPriority);
            }

            return order != 0 ? order : a.Depth.CompareTo(b.Depth);
        });

        placedLabels.Clear();
        placedLabels.Add(widget);
        labelledGlyphs.Clear();
        foreach (var marker in Markers.Items)
        {
            if (marker.Kind.LabelPriority > 0 && marker.Item is not Zone)
            {
                var half = new Vector2(Pixels(marker.Kind.MarkerSize) / 2);
                labelledGlyphs.Add((marker.Screen - half, marker.Screen + half, marker.Item,
                    marker.Kind.LabelPriority));
            }
        }

        var fontSize = Pixels(EditorTheme.CaptionSize);
        var padding = PillPadding * scale;
        var gap = LabelGap * scale;
        foreach (var marker in labelOrder)
        {
            var (text, textSize) = Label(marker.Item, fontSize);
            var pillSize = textSize + 2 * padding;
            var highlight = Highlight(marker.Item, hovered);
            // A highlighted object has brackets around its glyph, so its label moves out of them.
            var offset = (highlight > 0 ? GlyphBoxHalf(marker.Kind) : Pixels(marker.Kind.MarkerSize) / 2) + gap;
            // The label is centered vertically on the glyph.
            var top = MathF.Round(marker.Screen.Y - pillSize.Y / 2);
            var right = new Vector2(marker.Screen.X + offset, top);
            var left = new Vector2(marker.Screen.X - offset - pillSize.X, top);
            var rightInView = right.X + pillSize.X <= projection.Origin.X + projection.Size.X;
            // A label that the scene edge would cut shows a wrong name, so the left side is used only when it is in
            // view, or when the right side is not in view either.
            var leftUsable = left.X >= projection.Origin.X || !rightInView;
            Vector2 min;
            if (highlight > 0)
            {
                min = rightInView ? right : left;
            }
            else if (rightInView && IsFree(right, right + pillSize, marker))
            {
                min = right;
            }
            else if (leftUsable && IsFree(left, left + pillSize, marker))
            {
                min = left;
            }
            else
            {
                continue;
            }

            var max = min + pillSize;
            placedLabels.Add((min, max));
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(PillColor, marker.Alpha), PillRounding * scale);
            var color = highlight == 2 ? SelectedLabelColor : LabelColor;
            drawList.AddText(ImGuiHelper.Roboto, fontSize, min + padding, ImGui.GetColorU32(color, marker.Alpha), text);
        }
    }

    /// <summary>2 for the selected item, 1 for the hovered item and the items of the cross-links, and 0 for all other
    /// items.</summary>
    private int Highlight(object item, object? hovered) =>
        Equals(item, window.Selection) ? 2 : Equals(item, hovered) || Highlighted.Contains(item) ? 1 : 0;

    /// <summary>
    /// True when the label of a marker can go in a rectangle: it covers no placed label, not the axis widget, and no
    /// glyph of another item with the same or a higher label priority.
    /// </summary>
    private bool IsFree(Vector2 min, Vector2 max, SceneMarker marker)
    {
        foreach (var placed in placedLabels)
        {
            if (Overlaps(min, max, placed.Min, placed.Max))
            {
                return false;
            }
        }

        foreach (var glyph in labelledGlyphs)
        {
            if (glyph.Priority >= marker.Kind.LabelPriority && !Equals(glyph.Item, marker.Item) &&
                Overlaps(min, max, glyph.Min, glyph.Max))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Overlaps(Vector2 min, Vector2 max, Vector2 otherMin, Vector2 otherMax) =>
        min.X < otherMax.X && max.X > otherMin.X && min.Y < otherMax.Y && max.Y > otherMin.Y;

    /// <summary>
    /// The display name of an item and the size of the text. An item without a name shows its nickname. A nebula or a
    /// field without a name shows its file name without the extension (see <see cref="SceneItems.FallbackName"/>).
    /// </summary>
    private (string Text, Vector2 Size) Label(object item, float fontSize)
    {
        if (!labels.TryGetValue(item, out var label))
        {
            var text = Labels?.Invoke(item) ?? (window.Session?.ItemName(item) is { Length: > 0 } name ? name
                : SceneItems.FallbackName(item) ?? item.ToString() ?? "");
            label = (text, ImGuiHelper.Roboto.CalcTextSizeA(fontSize, float.MaxValue, 0, text));
            labels[item] = label;
        }

        return label;
    }

    /// <summary>A size in whole pixels at the current UI scale, so that text and glyphs stay sharp.</summary>
    private static float Pixels(float points) => MathF.Round(points * ImGuiHelper.Scale);
}
