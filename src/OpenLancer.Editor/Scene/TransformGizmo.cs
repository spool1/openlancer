using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using LibreLancer;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Editor.Scene;

/// <summary>The move steps of the snap menu of a gizmo, and the setting that holds the chosen step.</summary>
/// <param name="Move">The steps of the menu.</param>
/// <param name="Get">Reads the chosen step from the settings.</param>
/// <param name="Set">Writes the chosen step to the settings.</param>
internal sealed record GizmoSteps(float[] Move, Func<EditorSettings, float> Get, Action<EditorSettings, float> Set)
{
    /// <summary>The steps of a star system, in game units.</summary>
    public static readonly GizmoSteps System = new([1, 10, 50, 100, 500, 1000, 5000, 10000], s => s.MoveStep,
        (s, v) => s.MoveStep = v);

    /// <summary>The steps of a room or a cutscene, in meters.</summary>
    public static readonly GizmoSteps Room = new([0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1, 5, 10],
        s => s.CutsceneMoveStep, (s, v) => s.CutsceneMoveStep = v);
}

/// <summary>
/// A transform gizmo: the ImGuizmo handles on the item of the selection, the tool keys and the toolbar items. One drag
/// of a handle changes the item in W and adds one entry to the edit history (see <see cref="GizmoDragFlow"/>). The
/// rules for the mouse and the keys are in <see cref="GizmoInput"/>. The Scene drags an object, a zone or a light, and
/// the Cutscene view drags an element of the open cutscene.
/// </summary>
/// <remarks>
/// The editor clears the drag state of ImGuizmo with <c>ImGuizmo.Enable</c>, which fork commit OL-0019 exports. A
/// <c>cimgui.dll</c> from before that patch does not export it. Then the gizmo stays off, so that the end of a drag
/// cannot stop the editor.
/// </remarks>
internal sealed class TransformGizmo
{
    /// <summary>The number of toolbar items of the gizmo: three tools, the space, the snap and the snap
    /// steps.</summary>
    public const int ToolbarItemCount = 6;

    /// <summary>The Console message when <c>cimgui.dll</c> does not export <see cref="EnableExport"/>.</summary>
    internal const string NativeMessage = "cimgui.dll is older than fork commit OL-0019. Run build.ps1 BuildNatives " +
                                          "(docs/UPSTREAM.md), then build the editor again.";

    /// <summary>The native function that fork commit OL-0019 adds.</summary>
    internal const string EnableExport = "igGuizmoEnable";

    // The snap steps of the step menu.
    private static readonly float[] RotateSteps = [1, 5, 10, 15, 30, 45, 90];
    private static readonly float[] SizeSteps = [1, 10, 50, 100, 500, 1000, 5000];

    private readonly EditorWindow window;
    private readonly Func<EditorSession, object, (IGizmoTarget? Target, string? Reason)> resolve;
    private readonly GizmoSteps steps;
    private readonly GizmoDragFlow flow = new();
    private readonly bool available;

    // The target of the selection, and what it was made from. A change of W changes the version of the session.
    private (EditorSession? Session, object? Key, int Version, string? NoEditReason) targetKey;
    private IGizmoTarget? target;
    private string? targetReason;

    // The target of this frame, and the item that the gizmo showed for in the last frame.
    private IGizmoTarget? current;
    private object? shown;

    /// <param name="resolve">Gives the target of the item of the selection, or null with the reason when the gizmo
    /// cannot change it. Its item argument is never null.</param>
    /// <param name="steps">The move steps of the snap menu.</param>
    public TransformGizmo(EditorWindow window, Func<EditorSession, object, (IGizmoTarget? Target, string? Reason)> resolve,
        GizmoSteps steps)
    {
        this.window = window;
        this.resolve = resolve;
        this.steps = steps;
        available = NativeLibrary.TryLoad("cimgui", typeof(ImGuizmo).Assembly, null, out var library) &&
                    ExportsEnable(library);
        if (!available)
        {
            window.Log.Add(ConsoleSeverity.Error, EditorWindow.EditorSource, NativeMessage);
        }
    }

    /// <summary>True while a drag is open. Undo, redo, the recovery file and the model loads of the scene wait for its
    /// end. A save ends it first.</summary>
    public bool IsDragging => flow.State == GizmoDragState.Open;

    /// <summary>True when a native library exports the function of fork commit OL-0019.</summary>
    internal static bool ExportsEnable(IntPtr library) => NativeLibrary.TryGetExport(library, EnableExport, out _);

    /// <summary>The snap of a drag in this frame: the snap toggle, the steps, and Ctrl.</summary>
    private GizmoSnap Snap
    {
        get
        {
            var settings = window.Settings;
            return new GizmoSnap(settings.Snap, steps.Get(settings), settings.RotateStep, settings.SizeStep)
                .WithCtrl(ImGui.GetIO().KeyCtrl);
        }
    }

    /// <summary>
    /// The target of the item of the selection, or null with the reason when the gizmo cannot change it. The target is
    /// made again when the item, W or the edit state changes.
    /// </summary>
    /// <param name="item">The item, for example a section of the open system, or null.</param>
    public IGizmoTarget? Target(object? item, out string? reason)
    {
        var session = window.Session;
        var key = (session, item, session?.Version ?? 0, session?.NoEditReason);
        if (!Equals(key, targetKey))
        {
            targetKey = key;
            target = null;
            targetReason = null;
            if (session != null && item != null)
            {
                (target, targetReason) = resolve(session, item);
            }
        }

        reason = targetReason;
        return target;
    }

    /// <summary>
    /// Step 1 of the frame (section 10.2 of the design): ends, cancels or clears the drag before the frame reads the
    /// mouse.
    /// </summary>
    /// <param name="selected">The item of the selection, or null.</param>
    public void BeginFrame(object? selected)
    {
        if (available)
        {
            var cancel = ImGui.IsKeyPressed(ImGuiKey.Escape, false) || ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (flow.Start(ImGuizmo.IsUsing(), ImGui.IsMouseDown(ImGuiMouseButton.Left), cancel, selected))
            {
                Reset();
            }
        }

        // After the end or the cancel, so that the target has the lines that W holds now.
        current = Target(selected, out _);
    }

    /// <summary>Step 2 of the frame: what gets the mouse in this frame (see <see cref="GizmoInput.Owner"/>).</summary>
    /// <param name="buttonWasActive">The scene button was active in the last frame.</param>
    public SceneMouse Owner(bool buttonWasActive)
    {
        if (!available)
        {
            return SceneMouse.Button;
        }

        return GizmoInput.Owner(flow.State, buttonWasActive, ImGuizmo.IsOver(),
            current != null && Equals(current.Key, shown), ImGui.GetIO().KeyAlt,
            ImGui.IsMouseDown(ImGuiMouseButton.Right), ImGui.IsMouseDown(ImGuiMouseButton.Middle));
    }

    /// <summary>Step 6 of the frame: W, E and R select a tool, and X changes between Local and World (see
    /// <see cref="GizmoInput.Keys"/>).</summary>
    /// <param name="sceneHoveredOrFocused">The scene window is hovered or has the focus.</param>
    public void Keys(bool sceneHoveredOrFocused)
    {
        var io = ImGui.GetIO();
        if (!GizmoInput.ToolKeysEnabled(flow.State, sceneHoveredOrFocused, io.WantTextInput,
                ImGui.IsMouseDown(ImGuiMouseButton.Right), io.KeyCtrl, io.KeyAlt))
        {
            return;
        }

        var settings = window.Settings;
        (settings.Tool, settings.Space) = GizmoInput.Keys(settings.Tool, settings.Space, current,
            key => ImGui.IsKeyPressed(key, false));
    }

    /// <summary>
    /// Step 7 of the frame: draws the gizmo over the markers and the labels, and gives its result to the drag. A drag
    /// opens on the frame where ImGuizmo starts to drag with the left button down.
    /// </summary>
    public void Draw(ICamera camera, SceneProjection projection)
    {
        shown = null;
        var settings = window.Settings;
        if (!available || window.Session == null || !flow.Prepare(current, settings.Tool, settings.Space))
        {
            return;
        }

        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(projection.Origin.X, projection.Origin.Y, projection.Size.X, projection.Size.Y);
        var view = camera.View;
        var projectionMatrix = camera.Projection;
        ImGuizmo.Manipulate(ref view, ref projectionMatrix, flow.Operation, flow.Mode, ref flow.Shadow, out _);
        // Behind the camera, Manipulate draws nothing and takes no input (ImGuizmo.cpp:2521-2527).
        if (projection.Project(flow.Shadow.Translation, out _, out _))
        {
            shown = flow.Key;
        }

        if (flow.After(ImGuizmo.IsUsing(), ImGui.IsMouseDown(ImGuiMouseButton.Left), window.Commands, Snap,
                out var problem))
        {
            Reset();
        }

        if (problem != null)
        {
            window.Log.Add(problem.Severity, EditController.Source, problem.Message, problem.Target as SectionRef);
        }
    }

    /// <summary>
    /// A forced end: commits an open drag with the line of its last frame, and clears the drag state of ImGuizmo (see
    /// <see cref="GizmoDragFlow.End"/>).
    /// </summary>
    public void End()
    {
        flow.End();
        if (available)
        {
            Reset();
        }

        shown = null;
    }

    /// <summary>
    /// Draws the toolbar items of the gizmo: the move, rotate and scale tools, Local or World, the snap toggle and the
    /// menu of the snap steps. The caller puts <see cref="ImGui.SameLine()"/> before the first item.
    /// </summary>
    /// <param name="selected">The item of the selection, or null.</param>
    public void Toolbar(object? selected)
    {
        var settings = window.Settings;
        var item = Target(selected, out var reason);
        var idle = flow.State == GizmoDragState.None;
        var off = available ? "" : $"\n\n{NativeMessage}";
        var first = true;
        foreach (var (tool, key) in GizmoInput.ToolKeys)
        {
            if (!first)
            {
                ImGui.SameLine();
            }

            first = false;
            var disabled = GizmoInput.ToolDisabled(item, tool);
            var why = disabled ?? reason;
            var tooltip = $"{ToolText(tool, key)}{(why == null ? "" : $"\n\n{why}")}{off}";
            var on = settings.Tool == tool;
            if (ToolbarItems.Toggle($"tool-{tool}", ToolIcon(tool), tooltip, on, idle && disabled == null) && !on)
            {
                settings.Tool = tool;
            }
        }

        ImGui.SameLine();
        var hasSpace = GizmoInput.HasSpace(settings.Tool);
        var local = GizmoInput.SpaceOf(settings.Tool, settings.Space) == GizmoSpace.Local;
        var spaceTip = !hasSpace
            ? "Local: a scale always uses the axes of the item."
            : local
                ? $"Local ({GizmoInput.SpaceKey}): the gizmo moves and turns the item along its own axes. Click " +
                  "for World."
                : $"World ({GizmoInput.SpaceKey}): the gizmo moves and turns the item along the world axes. " +
                  "Click for Local.";
        if (ToolbarItems.Button("space", local ? EditorIcons.CubeSolid : EditorIcons.Globe, spaceTip,
                idle && hasSpace))
        {
            settings.Space = GizmoInput.Other(settings.Space);
        }

        ImGui.SameLine();
        settings.Snap = ToolbarItems.Toggle("snap", EditorIcons.Magnet,
            $"Snap: a drag moves by {steps.Get(settings):0.###}, turns by {settings.RotateStep:0.###} degrees and " +
            $"scales by {settings.SizeStep:0.###}. Hold Ctrl during a drag to snap when this is off, or to not " +
            "snap when it is on.", settings.Snap);
        ImGui.SameLine();
        if (ToolbarItems.Button("snap-steps", EditorIcons.CaretDown, "Snap steps"))
        {
            ImGui.OpenPopup("##snap-steps");
        }

        // The menu opens below the button.
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2 * ImGuiHelper.Scale),
            ImGuiCond.Appearing);
        if (ImGui.BeginPopup("##snap-steps"))
        {
            EditorTheme.PushMenuColors();
            steps.Set(settings, StepMenu("Move step", steps.Move, steps.Get(settings), ""));
            settings.RotateStep = StepMenu("Rotate step", RotateSteps, settings.RotateStep, " degrees");
            settings.SizeStep = StepMenu("Size step", SizeSteps, settings.SizeStep, "");
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }
    }

    /// <summary>Clears the drag state of ImGuizmo. A new drag needs a new click (ImGuizmo.cpp:1615-1621), so this
    /// cannot start a drag while the left button is down.</summary>
    private static void Reset()
    {
        ImGuizmo.Enable(false);
        ImGuizmo.Enable(true);
    }

    private static char ToolIcon(GizmoTool tool) => tool switch
    {
        GizmoTool.Move => EditorIcons.ArrowsAlt,
        GizmoTool.Rotate => EditorIcons.SyncAlt,
        _ => EditorIcons.ExpandAlt,
    };

    private static string ToolText(GizmoTool tool, ImGuiKey key) => tool switch
    {
        GizmoTool.Move => $"Move ({key}): drag an arrow to move the item. The drag changes pos.",
        GizmoTool.Rotate => $"Rotate ({key}): drag a ring to turn the item. The drag changes rotate.",
        _ => $"Scale ({key}): drag a handle to change the size of a zone. The drag changes size.",
    };

    /// <summary>A group of the step menu. Returns the selected step.</summary>
    private static float StepMenu(string title, float[] steps, float value, string unit)
    {
        ImGui.TextDisabled(title);
        foreach (var step in steps)
        {
            if (ImGui.MenuItem($"{step:#,0.##}{unit}##{title}{step}", "", step == value))
            {
                value = step;
            }
        }

        return value;
    }
}
