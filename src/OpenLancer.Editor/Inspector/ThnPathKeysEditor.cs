using System;
using System.Numerics;
using ImGuiNET;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The path keys editor of a MOTION_PATH entity (section 7.5 of the M8 design): a table of the points and the
/// orientations of <c>path_data</c>, which the editor reads as the engine does (see <see cref="ThnMotionPath"/>). An
/// edit of a value writes the whole text again with the number format of the writer. A path that no edit changed keeps
/// its text. A click on a key selects it for the gizmo of the Cutscene view.
/// </summary>
internal sealed class ThnPathKeysEditor(EditorWindow window, PropertyGrid grid)
{
    private static readonly PropertyGrid.ComponentLabel[] OrientationLabels =
    [
        new("W", EditorTheme.TextDim), new("X", EditorTheme.AxisX), new("Y", EditorTheme.AxisY),
        new("Z", EditorTheme.AxisZ),
    ];

    /// <summary>Draws the keys of a path, or the text with the problem when the text is not a path.</summary>
    /// <param name="field">The row state of <c>pathprops.path_data</c>.</param>
    public void Draw(ThnRef entity, ThnFieldState field)
    {
        if (!grid.BeginSection("Thn.Entity.PathKeys", EditorIcons.Route, "Path keys"))
        {
            return;
        }

        if (field.Value is not ThnText text)
        {
            grid.Text("Keys", "The path has no path_data.");
            grid.EndSection();
            return;
        }

        ThnPathKeys keys;
        try
        {
            keys = ThnMotionPath.Parse(text.Value, ThornEvaluator.Instance);
        }
        catch (FormatException e)
        {
            grid.SetNextNote(ErrorText.Of(e));
            grid.ThnTextRow("path_data", field, null, value => ThnFieldParse.Of(new ThnText(value)), "Not set");
            grid.EndSection();
            return;
        }

        var workspace = window.Cutscenes;
        grid.Text("Kind", $"{(keys.Closed ? "Closed" : "Open")} path, {keys.Keys.Length} keys" +
                          (keys.HasOrientation ? " with orientations" : ""));
        for (var i = 0; i < keys.Keys.Length; i++)
        {
            var index = i;
            var key = keys.Keys[i];
            var selected = workspace.SelectedPathKey == (entity, i);
            grid.Row($"Key {i + 1}", () =>
            {
                if (ImGui.Selectable(selected ? "Selected for the gizmo" : "Select for the gizmo", selected))
                {
                    workspace.SelectedPathKey = selected ? null : (entity, index);
                }
            });
            var point = field with { Kind = ThnKeyKind.Vector, Value = ThnPose.Vector(key.Point), Line = 2 * i };
            grid.ThnComponentRow("  Point", point, null, PropertyGrid.Axes, value => PointParse(keys, index, value),
                FieldDrag.KindOf(point.Kind, point.Path.Text));
            if (key.Orientation is { } orientation)
            {
                grid.ThnComponentRow("  Orientation",
                    field with { Kind = ThnKeyKind.Quaternion, Value = ThnPose.QOrient(orientation), Line = 2 * i + 1 },
                    null, OrientationLabels, value => OrientationParse(keys, index, value));
            }
        }

        grid.EndSection();
    }

    /// <summary>The path text that a new point of a key gives.</summary>
    internal static ThnFieldParse PointParse(ThnPathKeys keys, int index, string text)
    {
        var parse = ThnFieldEdit.Parse(ThnKeyKind.Vector, text, ThornEvaluator.Instance);
        return ThnPose.Vector(parse.Value) is { } point
            ? ThnFieldParse.Of(new ThnText(ThnMotionPath.Write(keys.With(index, keys.Keys[index] with { Point = point }))))
            : parse;
    }

    /// <summary>The path text that a new orientation of a key gives, as <c>{ w, x, y, z }</c>.</summary>
    internal static ThnFieldParse OrientationParse(ThnPathKeys keys, int index, string text)
    {
        var parse = ThnFieldEdit.Parse(ThnKeyKind.Quaternion, text, ThornEvaluator.Instance);
        return ThnPose.QOrient(parse.Value) is { } orientation
            ? ThnFieldParse.Of(new ThnText(ThnMotionPath.Write(keys.With(index,
                keys.Keys[index] with { Orientation = Quaternion.Normalize(orientation) }))))
            : parse;
    }
}
