using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Help;

/// <summary>One row of Help &gt; Keys.</summary>
/// <param name="Group">The group of the row, for example <see cref="KeysWindow.Global"/>.</param>
/// <param name="Keys">The keys, for example "Ctrl+S".</param>
/// <param name="Action">What the keys do.</param>
internal sealed record KeyRow(string Group, string Keys, string Action);

/// <summary>
/// Help &gt; Keys: a floating window with one table of the keys of the editor, as the Shortcuts window of Unity lists
/// them. The rows of the keys come from the lists that the key handlers read (<see cref="EditorKeys"/>,
/// <see cref="GizmoInput.KeyRows"/> and <see cref="PreviewKeys.Table"/>), so the table cannot drift from the keys. The
/// rows of the mouse come from the help popover of the Scene (<see cref="SceneView.MouseControls"/>).
/// </summary>
internal sealed class KeysWindow
{
    public const string Global = "Global";
    public const string Scene = "Scene";
    public const string SceneMouse = "Scene camera and mouse";
    public const string Hierarchy = "Hierarchy";
    public const string Preview = "Cutscene preview";

    private const string Title = "Keys";
    private static readonly Vector2 DefaultSize = new(680, 640);

    /// <summary>The note under the name of each group: where its keys work.</summary>
    private static readonly Dictionary<string, string> GroupNotes = new()
    {
        [Global] = "These keys work in each panel. The keys other than Ctrl+S wait while a text field is active, a field " +
                   "edit is open or a dialog shows.",
        [Scene] = "These keys act on the selected item of the open system. They work while the Scene or the Hierarchy " +
                  "has the focus and no text field is active. The keys of the gizmo tools work while the mouse is over " +
                  "the Scene or the Scene has the focus. They wait while the right mouse button is down.",
        [SceneMouse] = "These controls work while the mouse is over the Scene. While the right mouse button is down, " +
                       "W, A, S, D, Q and E fly the camera. The Free mode of the Cutscene tab has the same camera " +
                       "controls.",
        [Hierarchy] = "These keys work while the Hierarchy has the focus and no text field is active.",
        [Preview] = "These keys work while the Cutscene tab or the Timeline has the focus and no text field is active.",
    };

    private bool open;
    private bool focus;

    /// <summary>The rows of the table, in groups: the global keys, the Scene keys with the gizmo tools, the mouse
    /// controls of the Scene, the Hierarchy keys and the keys of the cutscene preview.</summary>
    public static IReadOnlyList<KeyRow> Rows { get; } = MakeRows();

    /// <summary>Shows the window and gives it the focus.</summary>
    public void Open()
    {
        open = true;
        focus = true;
    }

    /// <summary>Draws the window while it is open.</summary>
    public void Draw()
    {
        if (!open)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(DefaultSize * ImGuiHelper.Scale, ImGuiCond.FirstUseEver);
        if (focus)
        {
            ImGui.SetNextWindowFocus();
            focus = false;
        }

        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.NoCollapse))
        {
            foreach (var group in Rows.GroupBy(r => r.Group))
            {
                Group(group.Key, group);
            }
        }

        ImGui.End();
    }

    private static void Group(string name, IEnumerable<KeyRow> rows)
    {
        ImGui.SeparatorText(name);
        ImGui.TextWrapped(GroupNotes[name]);
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable($"##keys-{name}", 2, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("Keys", ImGuiTableColumnFlags.WidthFixed, 190 * ImGuiHelper.Scale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextWrapped(row.Keys);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(row.Action);
        }

        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static List<KeyRow> MakeRows()
    {
        var rows = new List<KeyRow>();
        rows.AddRange(EditorKeys.Global.Select(k => new KeyRow(Global, k.Chord.Text, k.Action)));
        rows.AddRange(EditorKeys.Scene.Select(k => new KeyRow(Scene, k.Chord.Text, k.Action)));
        rows.AddRange(GizmoInput.KeyRows.Select(k => new KeyRow(Scene, k.Keys, k.Action)));
        rows.AddRange(SceneView.MouseControls.Select(c => new KeyRow(SceneMouse, c.Input, c.Action + ".")));
        rows.AddRange(EditorKeys.Hierarchy.Select(k => new KeyRow(Hierarchy, k.Chord.Text, k.Action)));
        rows.AddRange(PreviewKeys.Table.Select(k => new KeyRow(Preview, k.Chord.Text, k.Action)));
        return rows;
    }
}
