using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.ImUI;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The camera of the Cutscene view (section 7.6 of the M8 design).</summary>
internal enum CutsceneViewMode
{
    /// <summary>The view of the active THN camera, with the letterbox of the game.</summary>
    Shot,

    /// <summary>The free fly camera of the editor, with the cameras, markers, lights and paths on top.</summary>
    Free,
}

/// <summary>
/// The Cutscene tab beside the Scene tab (section 7.6 of the M8 design). Shot mode shows the view of the active THN
/// camera with the letterbox of the game. Free mode shows the scene with the fly camera of the editor and the overlays
/// of the entities, and a click selects an entity. The toolbar has the cutscene list, the context, the mode, Align
/// view to camera, Set camera from view, the gizmo tools, the screen ratio and the debug readout. A play button and a
/// scrub bar are below the view. The Preview controls are in the toolbar of the Timeline.
/// </summary>
/// <remarks>
/// In Free mode, the gizmo edits the start pose of the selected entity, the target pose of a spatial animation with one
/// target, or a key of a motion path (see <see cref="ThnGizmoTarget"/>). Events can move an entity later, so while the
/// playhead is not at 0, the gizmo draws on a ghost of the start pose. The ghost reads W, so it follows a drag at once.
/// </remarks>
internal sealed class CutsceneView
{
    /// <summary>The id of the panel. The layout reports its focus by this id.</summary>
    public const string PanelId = "cutscene";

    /// <summary>The scrub bar shows the first 600 s of a long ambient script. The time field takes any time.</summary>
    private const double ScrubSeconds = 600;

    // Sizes in unscaled points.
    private const float ContextWidth = 260;
    private const float AspectWidth = 70;
    private const float TimeFieldWidth = 90;
    private const float BannerPadding = 6;
    private const float ProgressHeight = 3;
    private const float ClickSlop = 4;

    /// <summary>The distance of the free camera from an entity that it frames, in meters.</summary>
    private const float FrameDistance = 4;

    private static readonly ShotAspect[] Aspects = [ShotAspect.Window, ShotAspect.FourThree, ShotAspect.SixteenNine];

    private static readonly uint GhostColor = EditorTheme.ToAbgr(EditorTheme.TextBright with { W = 0.85f });

    private readonly EditorWindow window;
    private readonly CutsceneWorkspace workspace;
    private readonly CutscenePicker picker;
    private readonly TransformGizmo gizmo;
    private readonly SceneCamera freeCamera = new(CameraScale.Room);
    private readonly LensCamera lens = new();

    // The lenses of a space script in its system: the THN camera with the planes of the space camera, and the sky
    // camera of the starsphere with the pose of the view.
    private readonly LensCamera shotLens = new();
    private readonly LensCamera skyLens = new();
    private readonly CutsceneOverlay overlay = new();
    private readonly ScrubGesture scrub = new();
    private ShotAspect aspect = ShotAspect.Window;
    private CutsceneViewMode mode = CutsceneViewMode.Shot;
    private float fovH = LensCamera.DefaultFovH;
    private bool readout;
    private bool names;

    // The steps of the meter at the last frame, for the steps of this frame.
    private long meterSteps;
    private long frameSteps;

    // The text of the time field, and whether the user edits it.
    private string timeText = "";
    private bool timeActive;

    // The visible player whose draw failed, the exception and the script time of the frame. The view does not draw that
    // player again, and a banner shows the message. The next player draws again.
    private (CutscenePlayer Player, Exception Problem, double Time)? drawFailure;

    // The draw failure that the Console has.
    private Exception? reportedDraw;

    // The cutscene for which the free camera has a place, and the cutscene whose camera the view checked.
    private string? placedFor;
    private string? checkedCamera;

    // The free view took a click or a drag in the last frame, where it started, and the entity under the mouse.
    private bool buttonActive;
    private Vector2 dragStart;
    private object? hovered;

    /// <summary>The controls of the keys and of the mouse, for the help popover.</summary>
    public static readonly (string Input, string Action)[] Controls =
    [
        ("Space", "Play or pause the preview"),
        ("Home and End", "Go to the start or the end"),
        ("Left and Right", "One frame back or forward (1/60 s)"),
        ("Shift + Left and Shift + Right", "One second back or forward"),
        ("F5 and Shift + F5", "Play and Stop of the game, as in each panel"),
        ("Right mouse drag", "Free: look around"),
        ("W A S D, Q and E", "Free: fly, while the right mouse button is down"),
        ("Wheel, while flying", "Free: set the fly speed"),
        ("Alt + left mouse drag", "Free: orbit"),
        ("Middle mouse drag", "Free: pan"),
        ("Wheel", "Free: zoom"),
        ("Click", "Free: select an entity"),
        ("F", "Free: focus on the selected entity"),
        ("W and E", "Free: move or rotate tool of the gizmo, while the right mouse button is up"),
        ("X", "Free: Local or World axes of the gizmo"),
        ("Ctrl + gizmo drag", "Free: snap, or no snap when the snap is on"),
    ];

    public CutsceneView(EditorWindow window, CutsceneWorkspace workspace)
    {
        this.window = window;
        this.workspace = workspace;
        picker = new CutscenePicker(window, workspace);
        gizmo = new TransformGizmo(window, (session, item) => item is ThnGizmoKey key
            ? (ThnGizmoTarget.Of(session.Edits, key, session.NoEditReason, out var reason), reason)
            : (null, null), GizmoSteps.Room);
    }

    /// <summary>True while a drag of the gizmo is open. Undo and redo wait for its end. A save ends it first.</summary>
    public bool IsDragging => gizmo.IsDragging;

    /// <summary>Ends an open drag of the gizmo with the pose of its last frame (a forced end).</summary>
    public void EndDrag() => gizmo.End();

    /// <summary>Forgets the list of the game data and the place of the free camera. Call it when the profile
    /// closes.</summary>
    public void Reset()
    {
        gizmo.End();
        picker.Reset();
        drawFailure = null;
        reportedDraw = null;
        placedFor = null;
        checkedCamera = null;
        hovered = null;
    }

    /// <summary>Draws the items of the toolbar strip.</summary>
    public void Toolbar()
    {
        var scale = ImGuiHelper.Scale;
        picker.Draw(CutscenePicker.Width * scale);
        ImGui.SameLine();
        ContextMenu(ContextWidth * scale);
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextDisabled(EditorIcons.InfoCircle.ToString());
        WrappedTooltip(ContextInfo);
        // The toggle has an effect only when the stage of a system loaded behind the script (Editor UI rule 4).
        if (workspace.Stage != null)
        {
            ImGui.SameLine();
            window.Settings.CutsceneScriptObjects = ToolbarItems.Toggle("script-objects", EditorIcons.Cubes,
                ScriptObjectsTooltip, window.Settings.CutsceneScriptObjects);
        }

        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        var on = workspace.Transport != null;
        if (ToolbarItems.Toggle("shot", EditorIcons.Video,
                "Shot: the view of the active camera of the cutscene, with the letterbox of the game.",
                mode == CutsceneViewMode.Shot, on) && mode != CutsceneViewMode.Shot)
        {
            mode = CutsceneViewMode.Shot;
        }

        ImGui.SameLine();
        if (ToolbarItems.Toggle("free", EditorIcons.ArrowsAlt,
                "Free: fly through the scene. The cameras, markers, lights and paths show on top, and a click selects " +
                "an entity.", mode == CutsceneViewMode.Free, on) && mode != CutsceneViewMode.Free)
        {
            mode = CutsceneViewMode.Free;
        }

        ImGui.SameLine();
        var target = AlignTarget();
        if (ToolbarItems.Button("align", EditorIcons.Eye,
                "Align view to camera: put the free camera at the pose and the field of view of the selected camera, " +
                "or of the active camera when no camera is selected, at the playhead.", target != null))
        {
            Align(target!);
        }

        ImGui.SameLine();
        var camera = CameraToSet(out var cameraReason);
        if (ToolbarItems.Button("set-camera", EditorIcons.Video,
                "Set camera from view: write the pose of the free camera into the start pose of the selected camera, " +
                $"in one edit.{(cameraReason != null ? $"\n\n{cameraReason}" : "")}", camera != null))
        {
            window.Commands.Execute(ThnGizmoTarget.SetStartPose(camera!, new ThnPoseValue(freeCamera.Position,
                freeCamera.Orientation), "Set camera from view"));
        }

        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        gizmo.Toolbar(mode == CutsceneViewMode.Free ? GizmoKey(out _) : null);
        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        AspectMenu(AspectWidth * scale);
        ImGui.SameLine();
        names = ToolbarItems.Toggle("names", EditorIcons.Tag,
            "Free: show the names of all entities. The selected entity, the entity under the mouse and the active " +
            "camera always show their names.", names);
        ImGui.SameLine();
        readout = ToolbarItems.Toggle("readout", EditorIcons.TachometerAlt,
            "Debug readout: the build time, the step time and the steps in each frame", readout);
        ImGui.SameLine();
        if (ToolbarItems.Button("help", EditorIcons.QuestionCircle, "Preview keys and Free controls"))
        {
            ImGui.OpenPopup("##preview-keys");
        }

        if (ImGui.BeginPopup("##preview-keys"))
        {
            KeysTable();
            ImGui.EndPopup();
        }
    }

    /// <summary>Draws the view and the scrub bar, and reads the mouse over the view.</summary>
    public unsafe void Draw(double elapsed)
    {
        if (window.Session?.GameData == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (workspace.Path == null)
        {
            PanelStates.Message("No cutscene is open.", "Open a cutscene in the Cutscenes tab or in the list of the toolbar.");
            return;
        }

        if (workspace.Transport is not { } transport || workspace.Clock is not { } clock)
        {
            PanelStates.Message($"{System.IO.Path.GetFileName(workspace.Path)} has no preview.",
                workspace.Problem ?? "The preview cannot start.");
            return;
        }

        var player = workspace.Visible;
        if (drawFailure is { } failure && !ReferenceEquals(failure.Player, player))
        {
            drawFailure = null;
        }
        else if (drawFailure is { } failed && !ReferenceEquals(failed.Problem, reportedDraw))
        {
            // The render callback keeps the failure. The report comes in the next frame, outside the callback.
            reportedDraw = failed.Problem;
            workspace.ReportDrawFailure(failed.Problem, failed.Time);
        }

        // A script without a camera shows nothing in Shot mode, so it opens in Free mode (section 7.6).
        if (player != null && checkedCamera != workspace.Path)
        {
            checkedCamera = workspace.Path;
            if (player.CameraName == null)
            {
                mode = CutsceneViewMode.Free;
            }
        }

        // A new context starts a new meter, so its first frame counts from 0.
        frameSteps = Math.Max(0, workspace.Meter.Steps - meterSteps);
        meterSteps = workspace.Meter.Steps;

        var avail = ImGui.GetContentRegionAvail();
        var barHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(64, MathF.Floor(avail.X)), Math.Max(64, MathF.Floor(avail.Y - barHeight)));
        var panel = new Rectangle((int)origin.X, (int)origin.Y, (int)size.X, (int)size.Y);
        var shot = ShotViewport.Of(panel, aspect, workspace.Plan?.Cutscene != null);

        // The panel is black outside the viewport, as the screen of the game is outside its letterbox.
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0, 0, 0, 1)));
        var seconds = clock.DrawSeconds;
        if (mode == CutsceneViewMode.Shot)
        {
            workspace.Viewport = shot.Viewport;
            hovered = null;
            buttonActive = false;
            drawList.AddCallback((_, _) => DrawScene(shot.Viewport, shot.ScreenAspect, seconds, null), IntPtr.Zero);
            ImGui.Dummy(size);
        }
        else
        {
            workspace.Viewport = panel;
            Free(player, panel, shot, origin, size, seconds, elapsed);
        }

        Overlays(drawList, shot, origin, size, clock, player);
        ScrubBar(transport, clock);
    }

    /// <summary>
    /// Shows an entity in Free mode: the free camera moves to look at it from 4 m, in its direction of view, with the
    /// field of view of the editor. The Hierarchy calls it on a double-click.
    /// </summary>
    public void Frame(ThnRef entity)
    {
        if (workspace.Visible is not { } player || workspace.Element(entity) is not { } element)
        {
            return;
        }

        mode = CutsceneViewMode.Free;
        Place(player);
        if (player.Find(TimelineModel.EntityName(element.Table)) is { } obj)
        {
            fovH = LensCamera.DefaultFovH;
            freeCamera.Focus(obj.Pose.Position, FrameDistance);
        }
    }

    /// <summary>
    /// The camera that Align view to camera uses: the selected entity when it is a camera of the scene, else the active
    /// camera of the shot. Null without one.
    /// </summary>
    private PreviewObject? AlignTarget()
    {
        if (workspace.Visible is not { } player)
        {
            return null;
        }

        if (workspace.Selected is { Kind: ThnElementKind.Entity } selected && workspace.Element(selected) is { } element &&
            player.Find(TimelineModel.EntityName(element.Table)) is { FovH: not null } camera)
        {
            return camera;
        }

        return player.CameraName is { } name ? player.Find(name) : null;
    }

    /// <summary>
    /// The camera that Set camera from view writes: the selected entity when it is a CAMERA of the open cutscene, in
    /// Free mode, and the gizmo can change its start pose (see <see cref="ThnGizmoTarget.Of"/>). Null with the reason
    /// otherwise.
    /// </summary>
    private ThnRef? CameraToSet(out string? reason)
    {
        reason = null;
        if (window.Session is not { } session || workspace.Selected is not { Kind: ThnElementKind.Entity } selected ||
            workspace.Element(selected) is not { } element ||
            ThnSchema.TypeOf(ThnElementKind.Entity, element.Table, CutsceneWorkspace.Symbols) != "CAMERA")
        {
            reason = "Select a CAMERA entity first.";
            return null;
        }

        if (mode != CutsceneViewMode.Free)
        {
            reason = "Go to Free mode first, and fly the free camera to the view that you want.";
            return null;
        }

        return ThnGizmoTarget.Of(session.Edits, new ThnGizmoKey(selected), session.NoEditReason, out reason) != null
            ? selected
            : null;
    }

    /// <summary>
    /// The item of the gizmo for the selection: the entity, the target pose of a spatial animation with one target,
    /// the second target of a spatial animation with two targets, or the selected key of a motion path. Null with the
    /// reason when the gizmo cannot change the selection.
    /// </summary>
    private ThnGizmoKey? GizmoKey(out string? reason)
    {
        reason = null;
        if (workspace.Selected is not { } selected || workspace.Working is not { } working)
        {
            return null;
        }

        if (ThnGizmoTarget.Subject(working, selected, CutsceneWorkspace.Symbols, out reason) is not { } subject)
        {
            return null;
        }

        return workspace.SelectedPathKey is { } key && key.Path.Equals(subject)
            ? new ThnGizmoKey(subject, key.Key)
            : new ThnGizmoKey(subject);
    }

    /// <summary>Align view to camera: Free mode with the pose and the field of view of a camera (decision D73).</summary>
    private void Align(PreviewObject camera)
    {
        mode = CutsceneViewMode.Free;
        placedFor = workspace.Path;
        freeCamera.SetPose(camera.Pose.Position, camera.Pose.Orientation);
        fovH = camera.FovH ?? LensCamera.DefaultFovH;
    }

    /// <summary>
    /// Gives the free camera a place for a new cutscene: the pose of the active camera, or else a view of all
    /// entities.
    /// </summary>
    private void Place(CutscenePlayer player)
    {
        if (placedFor == workspace.Path)
        {
            return;
        }

        placedFor = workspace.Path;
        fovH = LensCamera.DefaultFovH;
        if (player.CameraName is { } name && player.Find(name) is { } camera)
        {
            Align(camera);
            return;
        }

        var points = workspace.Timeline?.Groups.SelectMany(g => g.Rows).Select(r => player.Find(r.Name))
            .OfType<PreviewObject>().Select(o => o.Pose.Position).ToList() ?? [];
        var center = points.Count == 0 ? Vector3.Zero : points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Count;
        var radius = points.Count == 0 ? 10 : points.Max(p => Vector3.Distance(p, center));
        freeCamera.Frame(center, Math.Max(radius, 2));
    }

    /// <summary>
    /// The Free mode: the fly camera, the scene through it in the whole panel, the overlays, and the click that selects
    /// an entity.
    /// </summary>
    private unsafe void Free(CutscenePlayer? player, Rectangle panel, ShotViewport shot, Vector2 origin, Vector2 size,
        double seconds, double elapsed)
    {
        if (player != null)
        {
            Place(player);
        }

        // The frame order of the Scene (section 10.2 of the M2 design): the drag of the gizmo ends first, then the gizmo
        // or the view button gets the mouse.
        var key = GizmoKey(out _);
        gizmo.BeginFrame(key);
        var owner = gizmo.Owner(buttonActive);
        bool viewHovered = false, active = false, activated = false, deactivated = false;
        if (owner == SceneMouse.Button)
        {
            ImGui.InvisibleButton("##free", size,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            viewHovered = ImGui.IsItemHovered();
            active = ImGui.IsItemActive();
            activated = ImGui.IsItemActivated();
            deactivated = ImGui.IsItemDeactivated();
        }
        else
        {
            // A Dummy takes no click, so ImGuizmo can start its drag (ImGuizmo.cpp:1615-1621).
            ImGui.Dummy(size);
        }

        var viewKeys = GizmoInput.ReadsViewKeys(owner, viewHovered, ImGui.IsWindowHovered(),
            ImGui.IsMouseHoveringRect(origin, origin + size));
        freeCamera.HandleInput(window.Keyboard, viewKeys || buttonActive, active, elapsed);
        buttonActive = active;
        gizmo.Keys(ImGui.IsWindowHovered() || ImGui.IsWindowFocused());

        var screenAspect = aspect == ShotAspect.Window ? size.X / size.Y : shot.ScreenAspect;
        lens.Update(size.X, size.Y, freeCamera.Position, freeCamera.Orientation, fovH, screenAspect, freeCamera.ZRange);
        // The starsphere of a system behind a space script draws with the pose of the free camera.
        skyLens.Update(size.X, size.Y, freeCamera.Position, freeCamera.Orientation, fovH, screenAspect,
            StarspherePass.SkyRange);
        var projection = new SceneProjection(lens, freeCamera.ZRange.X, origin, size, ImGuiHelper.Scale);
        ImGui.GetWindowDrawList().AddCallback((_, _) => DrawScene(panel, screenAspect, seconds, lens), IntPtr.Zero);
        if (player == null || workspace.Timeline is not { } timeline)
        {
            return;
        }

        overlay.Update(projection, player, timeline, window.Selection);
        if (activated && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            dragStart = ImGui.GetMousePos();
        }

        // A click without a drag selects the entity under the mouse, or the cutscene on empty space.
        if (deactivated && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyAlt &&
            Vector2.Distance(dragStart, ImGui.GetMousePos()) <= ClickSlop * ImGuiHelper.Scale)
        {
            window.Selection = overlay.Markers.HitTest(ImGui.GetMousePos()) ?? workspace.Root;
        }

        hovered = viewHovered && !active ? overlay.Markers.HitTest(ImGui.GetMousePos()) : null;
        var shotRatio = (float)shot.Viewport.Width / shot.Viewport.Height;
        overlay.Draw(projection, window.Selection, hovered, player.CameraName, shot.ScreenAspect, shotRatio, names);
        if (hovered is ThnRef entity && overlay.Find(entity) is { } obj)
        {
            ImGui.SetItemTooltip($"{obj.Name}\nClick to select it.");
        }

        Ghost(projection, key, player);
        gizmo.Draw(lens, projection);

        if ((viewKeys || window.IsFocused(PanelId)) && !ImGui.GetIO().WantTextInput &&
            ImGui.IsKeyPressed(ImGuiKey.F, false) && workspace.Selected is { Kind: ThnElementKind.Entity } selected)
        {
            Frame(selected);
        }
    }

    /// <summary>
    /// The ghost of the pose that the gizmo edits: a ring at the pose and a line from the object in the shown frame,
    /// while the shown pose is not the pose of the gizmo. That is when the playhead is not at 0, or when the gizmo edits
    /// a target pose or a path key. The ghost reads W through the target.
    /// </summary>
    private void Ghost(SceneProjection projection, ThnGizmoKey? key, CutscenePlayer player)
    {
        if (key is not { } item || window.Session is not { } session ||
            ThnGizmoTarget.Of(session.Edits, item, null, out _) is not { } target)
        {
            return;
        }

        var shown = workspace.Element(item.Element) is { } element && ThnScriptModel.NameOf(element.Table) is { } name
            ? player.Find(name)?.Pose.Position
            : null;
        if (target.Part == ThnGizmoPart.StartPose && (workspace.Transport?.Time ?? 0) <= 0)
        {
            return;
        }

        if (!projection.Project(target.Pose.Position, out var screen, out _))
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(projection.Origin, projection.Origin + projection.Size, true);
        var scale = ImGuiHelper.Scale;
        drawList.AddCircle(screen, 9 * scale, GhostColor, 0, 1.5f * scale);
        drawList.AddCircleFilled(screen, 2 * scale, GhostColor);
        if (shown is { } now)
        {
            CutsceneOverlay.Segment(drawList, projection, now, target.Pose.Position, GhostColor, scale);
        }

        drawList.AddText(screen + new Vector2(11 * scale, -ImGui.GetTextLineHeight() / 2), GhostColor,
            target.Part switch
            {
                ThnGizmoPart.EventPose => "target pose",
                ThnGizmoPart.PathKey => target.Label,
                _ => "start pose",
            });
        drawList.PopClipRect();
    }

    /// <summary>The info icon of the context (Editor UI rule 10, section 3.3 of the cutscene fixes plan).</summary>
    internal const string ContextInfo =
        "The context sets how the preview plays the script. Room: the base room, as the game builds it. Fidget: a " +
        "stand-in character plays the motions of the script. In space: the system of the mission line. The game makes " +
        "none of the script objects in space and shows the mission objects in their place. The preview shows the script " +
        "objects as stand-ins. Script only: the script alone. The context is a preview setting. It writes nothing to the " +
        "game files.";

    /// <summary>The tooltip of the toggle "Script objects".</summary>
    internal const string ScriptObjectsTooltip =
        "Script objects: show the ships, stations, effects and characters that the script makes. The game makes none of " +
        "them in space. It shows the mission objects in their place.";

    /// <summary>
    /// The context menu: the contexts that fit the cutscene, the suggested one first. The rooms and the fidgets come
    /// first, then the systems of the mission lines under "In space", then the script alone.
    /// </summary>
    private void ContextMenu(float width)
    {
        ImGui.SetNextItemWidth(width);
        ImGui.BeginDisabled(workspace.Context == null);
        EditorTheme.PushMenuColors();
        var label = workspace.Context is { } current ? ContextLabel(current) : "No context";
        var style = ImGui.GetStyle();
        var preview = TextFit.CutRight(label, width - ImGui.GetFrameHeight() - 2 * style.FramePadding.X);
        if (ImGui.BeginCombo("##context", preview))
        {
            var space = false;
            for (var i = 0; i < workspace.Choices.Count; i++)
            {
                var choice = workspace.Choices[i];
                var row = choice.Label;
                if (choice.Kind == PreviewContextKind.Space)
                {
                    if (!space)
                    {
                        ImGui.TextDisabled("In space");
                        space = true;
                    }

                    row = choice.System is { } system ? SpaceRow(system, SystemName(system)) : "No system";
                }

                if (ImGui.Selectable($"{row}##context{i}",
                        workspace.Context != null && PreviewContext.Same(choice, workspace.Context)))
                {
                    workspace.SetContext(choice);
                }

                if (choice.Kind == PreviewContextKind.Space)
                {
                    WrappedTooltip(SpaceRowTooltip(choice.Use));
                }
            }

            ImGui.EndCombo();
        }
        else
        {
            WrappedTooltip(
                "The context of the preview. It is a room that plays the script, the fidget of a room NPC, the system of " +
                $"a mission line, or the script alone. {label}.");
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
    }

    /// <summary>The label of a context in the toolbar: a Space context shows the name of its system (decision
    /// D652).</summary>
    private string ContextLabel(PreviewContext context) =>
        context is { Kind: PreviewContextKind.Space, System: { } system } ? $"Space: {SystemName(system)}" : context.Label;

    /// <summary>The name of a system of the loaded data, or its nickname when it has no name.</summary>
    private string SystemName(string system) =>
        window.Session is { GameData: { } data } session &&
        session.ItemName(data.Items.Systems.Get(system)) is { Length: > 0 } name
            ? name
            : system;

    /// <summary>The row of a system in the context menu (section 3.3 of the cutscene fixes plan). It shows the name and
    /// the nickname, or only the nickname when the system has no name.</summary>
    /// <param name="name">The name of the system, or its nickname when it has no name.</param>
    internal static string SpaceRow(string system, string name) =>
        name.Equals(system, StringComparison.OrdinalIgnoreCase) ? system : $"{name} ({system})";

    /// <summary>The tooltip of a Space row (section 3.3 of the cutscene fixes plan). It tells which mission line plays
    /// the script in the system, the rule that found the system, and the problem of the line.</summary>
    internal static string SpaceRowTooltip(SpaceUse? use)
    {
        if (use == null)
        {
            return "The game plays this script in space.";
        }

        var plays = use.Trigger.Length > 0
            ? $"The trigger {use.Trigger} plays this script."
            : $"A trigger without a nickname in {use.Line.File} plays this script.";
        var found = use.SystemRule switch
        {
            SpaceSystemRule.TriggerKey => $"{plays} It runs only in {use.System}.",
            SpaceSystemRule.Condition when use.SystemSource?.StartsWith("the system ", StringComparison.Ordinal) == true =>
                $"{plays} Its condition names {use.SystemSource}.",
            SpaceSystemRule.Condition => $"{plays} Its condition names {use.SystemSource}, which is in {use.System}.",
            SpaceSystemRule.ActivatingTrigger =>
                $"{plays} The trigger {use.SystemSource} starts it and runs only in {use.System}.",
            _ => null,
        };
        return found == null ? use.NoSystemText : use.Problem != null ? $"{found} {use.Problem}" : found;
    }

    /// <summary>A tooltip of the last item that wraps at a readable width.</summary>
    private static void WrappedTooltip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.Text(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void AspectMenu(float width)
    {
        ImGui.SetNextItemWidth(width);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##aspect", ShotViewport.Name(aspect), ImGuiComboFlags.NoArrowButton))
        {
            foreach (var choice in Aspects)
            {
                if (ImGui.Selectable(ShotViewport.Name(choice), choice == aspect))
                {
                    aspect = choice;
                }
            }

            ImGui.EndCombo();
        }
        else
        {
            ImGui.SetItemTooltip("The screen ratio of the Shot view: the ratio of the panel, or of a game screen.");
        }

        EditorTheme.PopMenuColors();
    }

    /// <summary>
    /// Draws the visible player into a viewport. The ImGui draw list calls it while it renders. The player can change
    /// between the frame and the render, so the callback reads it then. In Shot mode, a scene without a camera draws
    /// nothing, and the overlays show the message (section 7.6).
    /// </summary>
    /// <param name="camera">The free camera, or null for the THN camera of the shot.</param>
    /// <remarks>
    /// The ImGui renderer calls this method through a native callback, and an exception that leaves it ends the process
    /// (ImDrawList_AddCallback.cs:20-29). So the method keeps each exception for the banner of the view.
    /// </remarks>
    private void DrawScene(Rectangle viewport, float screenAspect, double seconds, ICamera? camera)
    {
        if (workspace.Visible is not { } player || camera == null && player.CameraName == null ||
            ReferenceEquals(drawFailure?.Player, player))
        {
            return;
        }

        // A player in the world of the stage reads the stage when the callback runs.
        var stage = player.InHostWorld ? workspace.Stage : null;
        var context = window.RenderContext;
        var clearColor = context.ClearColor;
        context.PushViewport(viewport.X, viewport.Y, viewport.Width, viewport.Height);
        context.PushScissor(viewport, false);
        try
        {
            context.Cull = true;
            context.DepthEnabled = true;
            if (stage != null)
            {
                DrawStage(stage, player, viewport, screenAspect, seconds, camera);
            }
            else
            {
                player.Draw(seconds, viewport, screenAspect, camera);
            }
        }
        catch (Exception e)
        {
            drawFailure = (player, e, player.Time);
        }
        finally
        {
            context.PopViewport();
            context.PopScissor();
            context.ClearColor = clearColor;
            context.DepthEnabled = false;
            context.BlendMode = BlendMode.Normal;
            context.Cull = false;
        }
    }

    /// <summary>
    /// Draws a space script in its system (decision D643): the player updates the THN camera, and the stage draws the
    /// system and the objects of the player. Shot mode uses the pose and the field of view of the THN camera with the
    /// planes of the space camera. Free mode uses the free camera. The starsphere draws with a sky camera with the pose
    /// of the view.
    /// </summary>
    /// <param name="free">The free camera, or null for the THN camera of the shot.</param>
    private void DrawStage(SpaceStage stage, CutscenePlayer player, Rectangle viewport, float screenAspect,
        double seconds, ICamera? free)
    {
        player.Draw(seconds, viewport, screenAspect, free);
        if (free == null)
        {
            if (player.ShotLens() is not { } shot)
            {
                return;
            }

            shotLens.Update(viewport.Width, viewport.Height, shot.Position, shot.Orientation, shot.FovH, screenAspect,
                LensCamera.SpaceShotRange);
            skyLens.Update(viewport.Width, viewport.Height, shot.Position, shot.Orientation, shot.FovH, screenAspect,
                StarspherePass.SkyRange);
        }

        stage.Draw(player.ScriptObjects, window.Settings.CutsceneScriptObjects, free ?? shotLens, skyLens, viewport,
            seconds);
    }

    /// <summary>
    /// Draws the safe frame and the name of the active camera in Shot mode, the banners, the message of a scene without
    /// a camera, the effects tag and the readout.
    /// </summary>
    private void Overlays(ImDrawListPtr drawList, ShotViewport shot, Vector2 origin, Vector2 size, CutsceneClock clock,
        CutscenePlayer? player)
    {
        var scale = ImGuiHelper.Scale;
        var pad = MathF.Truncate(BannerPadding * scale);
        var viewport = shot.Viewport;
        var (min, max) = mode == CutsceneViewMode.Shot
            ? (new Vector2(viewport.X, viewport.Y), new Vector2(viewport.X + viewport.Width, viewport.Y + viewport.Height))
            : (origin, origin + size);
        drawList.PushClipRect(origin, origin + size, true);
        if (mode == CutsceneViewMode.Shot)
        {
            drawList.AddRect(min, max, ImGui.GetColorU32(EditorTheme.TextDim with { W = 0.6f }));
            if (player?.CameraName is { } camera)
            {
                Shadowed(drawList, min + new Vector2(pad, pad), EditorTheme.TextDim, $"{EditorIcons.Video}  {camera}");
            }
            else if (player != null)
            {
                var text = "This script has no camera.";
                var textSize = ImGui.CalcTextSize(text);
                Shadowed(drawList, (min + max - textSize) / 2, EditorTheme.Text, text);
            }
        }
        else
        {
            Shadowed(drawList, min + new Vector2(pad, pad), EditorTheme.TextDim,
                $"{EditorIcons.ArrowsAlt}  Free camera, {freeCamera.Speed.ToString("0.##", CultureInfo.InvariantCulture)} m/s");
        }

        var top = origin.Y;
        if (clock.Problem is { } problem)
        {
            top = Banner(drawList, origin, size.X, top, EditorTheme.Error,
                $"{EditorIcons.ExclamationTriangle}  The preview stopped: {problem.Message} The view keeps the last " +
                "frame.");
        }

        if (drawFailure is { } failure && ReferenceEquals(failure.Player, player))
        {
            top = Banner(drawList, origin, size.X, top, EditorTheme.Error,
                $"{EditorIcons.ExclamationTriangle}  The preview cannot draw this frame: {failure.Problem.Message}");
        }

        foreach (var banner in workspace.Banners())
        {
            top = Banner(drawList, origin, size.X, top, EditorTheme.Warning, $"{EditorIcons.InfoCircle}  {banner}");
        }

        var bottom = max.Y - pad - ImGui.GetTextLineHeight();
        if (workspace.EffectsStartAtPlayhead)
        {
            Shadowed(drawList, new Vector2(min.X + pad, bottom), EditorTheme.TextDim, "Effects start at the playhead");
        }

        if (clock.CatchingUp)
        {
            var text = workspace.UpdatingText;
            Shadowed(drawList, new Vector2(max.X - pad - ImGui.CalcTextSize(text).X, bottom), EditorTheme.Text, text);
        }

        if (readout)
        {
            Readout(drawList, new Vector2(origin.X + size.X - pad, origin.Y + pad), clock);
        }

        drawList.PopClipRect();
    }

    /// <summary>A strip of text over the top of the view. Returns the bottom of the strip.</summary>
    private static float Banner(ImDrawListPtr drawList, Vector2 origin, float width, float top, Vector4 color,
        string text)
    {
        var pad = MathF.Truncate(BannerPadding * ImGuiHelper.Scale);
        var wrap = width - 2 * pad;
        var textSize = ImGui.CalcTextSize(text, null, false, wrap);
        var bottom = top + textSize.Y + 2 * pad;
        drawList.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + width, bottom),
            ImGui.GetColorU32(EditorTheme.Shadow));
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(origin.X + pad, top + pad),
            ImGui.GetColorU32(color), text, null, wrap);
        return bottom;
    }

    /// <summary>The debug readout of section 8.5 at the top right of the view.</summary>
    private void Readout(ImDrawListPtr drawList, Vector2 topRight, CutsceneClock clock)
    {
        var meter = workspace.Meter;
        var step = meter.Steps > 0 ? meter.StepTime.TotalMilliseconds / meter.Steps : 0;
        string[] lines =
        [
            $"Build {workspace.LastBuild.TotalMilliseconds:F1} ms, {workspace.Builds} builds",
            $"Step {step:F3} ms, {step / CutsceneClock.StepSeconds:F1} ms for each script second",
            $"Steps in this frame {frameSteps}",
            $"Shown {clock.Visible?.Time ?? 0:F3} s, playhead {clock.Target:F3} s" +
            (clock.CatchingUp ? $", catch-up {clock.CatchUpTime:F3} s" : ""),
            $"References scan {workspace.LastScan.TotalMilliseconds:F0} ms",
        ];
        var position = topRight;
        foreach (var line in lines)
        {
            var width = ImGui.CalcTextSize(line).X;
            Shadowed(drawList, position - new Vector2(width, 0), EditorTheme.Text, line);
            position.Y += ImGui.GetTextLineHeight();
        }
    }

    /// <summary>A text with a dark shadow, so that it reads over the scene.</summary>
    private static void Shadowed(ImDrawListPtr drawList, Vector2 position, Vector4 color, string text)
    {
        drawList.AddText(position + Vector2.One, ImGui.GetColorU32(EditorTheme.Shadow), text);
        drawList.AddText(position, ImGui.GetColorU32(color), text);
    }

    /// <summary>
    /// The play button, a scrub bar over the first 600 s, a field that takes any time, and the progress of a
    /// catch-up. A drag back seeks at the release, so that the drag does not build in each frame.
    /// </summary>
    private void ScrubBar(PreviewTransport transport, CutsceneClock clock)
    {
        var fieldWidth = TimeFieldWidth * ImGuiHelper.Scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var end = transport.End;
        EditorTheme.PushFlatButtonColors();
        PreviewControls.PlayButton(transport);
        EditorTheme.PopFlatButtonColors();
        ImGui.SameLine();
        var shown = (float)scrub.Shown(transport);
        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - fieldWidth - spacing));
        if (ImGui.SliderFloat("##scrub", ref shown, 0, (float)Math.Min(end, ScrubSeconds), "%.3f s"))
        {
            scrub.Drag(transport, shown);
        }

        if (ImGui.IsItemDeactivated())
        {
            scrub.Release(transport);
        }

        var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        if (clock.CatchingUp && end > 0)
        {
            // A thin bar at the bottom of the slider shows the time of the player that catches up.
            var fraction = (float)Math.Clamp(clock.CatchUpTime / Math.Min(end, ScrubSeconds), 0, 1);
            var height = MathF.Max(1, MathF.Truncate(ProgressHeight * ImGuiHelper.Scale));
            ImGui.GetWindowDrawList().AddRectFilled(new Vector2(min.X, max.Y - height),
                new Vector2(min.X + (max.X - min.X) * fraction, max.Y), ImGui.GetColorU32(EditorTheme.Accent));
            ImGui.SetItemTooltip(workspace.UpdatingText);
        }
        else
        {
            ImGui.SetItemTooltip(string.Create(CultureInfo.InvariantCulture,
                $"The playhead. The script is {end:0.###} s long. The bar shows the first {ScrubSeconds:0} s, and " +
                $"the field takes any time."));
        }

        ImGui.SameLine();
        // A text field, because the number fields of this ImGui build do not take EnterReturnsTrue. The text follows
        // the playhead while the field is not active.
        if (!timeActive)
        {
            timeText = transport.Time.ToString("0.000", CultureInfo.InvariantCulture);
        }

        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.InputText("##time", ref timeText, 32,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsDecimal) &&
            double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed) &&
            double.IsFinite(typed))
        {
            transport.Seek(typed);
        }

        timeActive = ImGui.IsItemActive();

        ImGui.SetItemTooltip(clock.CatchingUp
            ? workspace.UpdatingText
            : "The playhead in seconds. Type a time and press Enter.");
    }

    private static void KeysTable()
    {
        ImGui.Text("Preview keys, while the Timeline or the Cutscene panel has the focus, and Free controls");
        ImGui.Separator();
        if (!ImGui.BeginTable("##keys", 2, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        foreach (var (input, action) in Controls)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(input);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(action);
        }

        ImGui.EndTable();
    }
}
