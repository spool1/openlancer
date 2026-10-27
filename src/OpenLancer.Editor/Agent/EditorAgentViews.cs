using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>
/// The views of the editor for the view tools of the agent server (section 7.7 of the MCP1 design): the open of a system,
/// a mission or a cutscene, the selection, and the images of the render service. Each member runs on the UI thread, in
/// the session part of a call. The tools act as the user acts in the editor, so a selection follows the rules of the
/// center tabs.
/// </summary>
internal sealed class EditorAgentViews(EditorWindow window) : IAgentViews
{
    public void Open(ViewKind kind, string target)
    {
        switch (kind)
        {
            case ViewKind.System:
                OpenSystem(target, "system");
                window.ShowPanel(SceneView.PanelId);
                break;
            case ViewKind.Mission:
                window.Missions.OpenMission(target, show: true);
                break;
            default:
                OpenCutscene(target, "cutscene");
                break;
        }
    }

    /// <summary>
    /// Selects an item as a click of the user does. The Cutscene tab selects an element of a cutscene, the Mission tab a
    /// section of a mission script, and the Scene an item of a system. Each other section shows in the Inspector. With
    /// <paramref name="frame"/>, the editor opens the view of the item, shows its tab and moves the view to it. Without
    /// it, the open views, the tabs and the cameras stay. An item of a system or a mission that is not open then shows
    /// only in the Inspector.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/> with the detail <c>NotOpen</c>: without
    /// <paramref name="frame"/>, a mission script or a cutscene that is not open, or an element of such a
    /// cutscene.</exception>
    public SelectionView Select(SelectTarget target, bool frame)
    {
        if (target.Element is { } element)
            return SelectInCutscene(target.Ref, element.Path, element, frame);
        if (target.Cutscene is { } path)
            return SelectInCutscene(target.Ref, path, null, frame);
        if (target.Script is { } script)
            return SelectInMission(target.Ref, script, null, -1, frame);

        var section = target.Section!;
        if (target.InMission)
            return SelectInMission(target.Ref, section.Path, section, target.Row ?? -1, frame);
        if (target.System is { } system && (frame || IsOpen(system)))
        {
            if (frame)
                OpenSystem(system, "refs");
            if (window.Scene.ItemOf(section) is { } item and not StarSystem)
            {
                if (frame)
                    window.ShowPanel(SceneView.PanelId);
                window.SelectInTab(CenterMode.Scene, item);
                if (frame)
                    window.Scene.Focus(item);
                return Selected(target.Ref, SelectionPlace.Scene, item, frame, frame);
            }
        }

        window.Selection = section;
        return new SelectionView(target.Ref, SelectionPlace.Inspector, true, false);
    }

    public Func<CancellationToken, Task<(SceneShotView View, ToolImage Image)>> RenderScene(SceneShotRequest request)
    {
        var stage = Stage(request.System);
        // The image and the places of the items show W, so the stage applies the last changes of W first.
        stage.Flush();
        var (width, height) = (request.Width, request.Height);
        var camera = request.Camera ?? ShotCamera.Frame(FrameSpheres(stage, request), width / (float)height);
        var image = AgentRender.RenderScene(stage, camera, width, height, request.Zones);
        var session = window.Session!.Edits;
        var (items, total) = camera.Visible(stage.ShotItems(request.Zones), width, height, request.MaxVisible);
        var rows = items
            .Select(i => stage.SectionOf(i.Item) is { } section ? VisibleRow.Of(ItemRefs.Canonical(session, section), i.Point) : null)
            .OfType<VisibleRow>()
            .ToList();
        var view = new SceneShotView(SceneQueries.SystemRef(stage.System.Nickname), width, height, ShotView.Of(camera),
            request.Zones, rows, total);
        return async cancel => (view, await image.WaitAsync(cancel).ConfigureAwait(false));
    }

    public Func<CancellationToken, Task<(CutsceneShotView View, ToolImage Image)>> RenderCutscene(
        CutsceneShotRequest request)
    {
        _ = Data();
        CutsceneShotPlan plan;
        try
        {
            plan = window.Cutscenes.ShotPlan(request.Path);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw Refused("NoPreview", AgentTexts.Error("render_no_preview", ("cutscene", request.Path),
                ("reason", ErrorText.Of(e))), "cutscene");
        }

        var job = window.AgentRender.RenderCutscene(plan, request.Time, request.Camera, request.Width, request.Height);
        var reference = CutsceneQueries.Ref(plan.Path);
        var context = plan.Context.Label;
        var withoutSystem = plan.Plan.System != null;
        return async cancel =>
        {
            using var registration = cancel.Register(job.Cancel);
            var done = await job.Result.WaitAsync(cancel).ConfigureAwait(false);
            return (new CutsceneShotView(reference, (float)Math.Round(done.Time, 4), done.Camera, request.Width,
                request.Height, context, withoutSystem), done.Image);
        };
    }

    public Func<CancellationToken, Task<(PanelShotView View, ToolImage Image)>> CapturePanel(AgentPanel panel,
        int maxSide)
    {
        var id = PanelIdOf(panel);
        if (!window.IsPanelShown(id))
            throw Refused("PanelHidden", AgentTexts.Error("capture_hidden", ("panel", id)), "panel");
        var image = window.AgentRender.Capture(id, maxSide);
        return async cancel =>
        {
            var png = await image.WaitAsync(cancel).ConfigureAwait(false);
            return (new PanelShotView(panel, png.Width, png.Height), png);
        };
    }

    /// <summary>The id of the panel of the layout that a panel of <c>capture_panel</c> names. The names of the tool are
    /// the ids of the layout.</summary>
    internal static string PanelIdOf(AgentPanel panel) => panel switch
    {
        AgentPanel.Scene => SceneView.PanelId,
        AgentPanel.Mission => MissionWorkspace.PanelId,
        AgentPanel.Timeline => TimelinePanel.PanelId,
        AgentPanel.Inspector => InspectorPanel.PanelId,
        AgentPanel.Hierarchy => HierarchyPanel.PanelId,
        _ => ConsolePanel.PanelId,
    };

    /// <summary>The stage of a system for an image. It is the stage of the Scene when the Scene shows the system, else a
    /// stage of the render service. The Scene keeps its system.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/>: no game data is loaded, or the loaded game
    /// data does not have the system yet.</exception>
    private SceneStage Stage(string nickname)
    {
        var data = Data();
        if (window.Scene.Stage is { } open && IsOpen(nickname))
            return open;
        var system = data.Items.Systems.Get(nickname) ?? throw NotLoaded(nickname, "system");
        return window.AgentRender.StageOf(system);
    }

    /// <summary>
    /// Selects a mission script or a section of a mission script in the Mission tab. With <paramref name="frame"/>, the
    /// tab opens the script and shows it, and its graph moves to the section. Without it, the script must be the open
    /// mission. A section that the Mission tab cannot show goes only to the Inspector. An example is a section of a
    /// script that the mission run did not read yet.
    /// </summary>
    /// <param name="row">The line of the section, or -1 for the whole section.</param>
    private SelectionView SelectInMission(string reference, string script, SectionRef? section, int row, bool frame)
    {
        var missions = window.Missions;
        if (frame)
        {
            if (section == null)
            {
                missions.OpenMission(script, show: true);
                return new SelectionView(reference, SelectionPlace.Mission, true, false);
            }

            // The Mission tab opens the script, shows it and frames the section in the graph (section 12.4 of the M6a
            // design).
            if (missions.GoTo(section, row))
                return new SelectionView(reference, SelectionPlace.Mission, true, true);
        }
        else if (!VfsPath.Comparer.Equals(missions.OpenPath, script))
        {
            if (section == null)
                throw NotOpen(reference);
        }
        else if (section == null || missions.IsScript(script))
        {
            object item = section == null ? missions.Root! : MissionRef.OfSection(section, row);
            window.SelectInTab(CenterMode.Mission, item);
            return Selected(reference, SelectionPlace.Mission, item, false, false);
        }

        window.Selection = section;
        return new SelectionView(reference, SelectionPlace.Inspector, true, false);
    }

    /// <summary>
    /// Selects a cutscene or an element of a cutscene in the Cutscene tab. With <paramref name="frame"/>, the tab opens
    /// the cutscene and shows it, and its free camera moves to an entity. Without it, the cutscene must be the open
    /// cutscene, so its preview keeps its playhead.
    /// </summary>
    private SelectionView SelectInCutscene(string reference, string path, ThnRef? element, bool frame)
    {
        if (frame)
            OpenCutscene(path, "refs");
        else if (!VfsPath.Comparer.Equals(window.Cutscenes.Path, path))
            throw NotOpen(reference);

        object item = element is { } selected ? selected : window.Cutscenes.Root!;
        window.SelectInTab(CenterMode.Cutscene, item);
        var framed = frame && element is { Kind: ThnElementKind.Entity };
        if (framed)
            window.FrameInCutscene(element!);
        return Selected(reference, SelectionPlace.Cutscene, item, frame, framed);
    }

    /// <summary>The selection of an item of a center tab. The user sees it when the tab shows the item now, or when the
    /// call showed the tab.</summary>
    /// <param name="tabShown">True when the call showed the tab of the item. The tab then selects the item when it shows
    /// in the next frame.</param>
    private SelectionView Selected(string reference, SelectionPlace place, object item, bool tabShown, bool framed) =>
        new(reference, place, tabShown || Equals(window.Selection, item), framed);

    /// <summary>
    /// The spheres that an image frames: the sphere of each item of the frame, and each object of the system for a
    /// <c>system:</c> reference or an empty frame.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the stage does not show an item of the
    /// frame.</exception>
    private static List<(Vector3, float)> FrameSpheres(SceneStage stage, SceneShotRequest request)
    {
        var spheres = new List<(Vector3, float)>();
        if (request.Frame.Count == 0 || request.Frame.Any(f => f.Section == null))
            spheres.AddRange(stage.ShotItems(false).Select(i => (i.Center, i.Radius)));
        foreach (var frame in request.Frame.Where(f => f.Section != null))
        {
            var sphere = stage.ItemOf(frame.Section!) is { } item ? stage.SphereOf(item) : null;
            spheres.Add(sphere ?? throw new EditException(new EditError(EditErrorKind.NotFound, "NotInScene",
                AgentTexts.Error("render_frame_none", ("system", request.System), ("ref", frame.Ref)))
            {
                Field = "frame",
                Target = frame.Ref,
            }));
        }

        return spheres;
    }

    /// <summary>Opens a system of the loaded game data in the Scene. The open system stays as it is.</summary>
    /// <param name="field">The argument that names the system, for the error.</param>
    private void OpenSystem(string nickname, string field)
    {
        var data = Data();
        if (IsOpen(nickname))
            return;
        var system = data.Items.Systems.Get(nickname) ?? throw NotLoaded(nickname, field);
        window.OpenSystem(system);
    }

    private bool IsOpen(string system) => window.Scene.System?.Nickname.Equals(system, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Opens a cutscene in the Cutscene tab and shows the tab and the Timeline. The open cutscene stays as it is,
    /// so its preview keeps its playhead.</summary>
    /// <param name="field">The argument that names the cutscene, for the error.</param>
    private void OpenCutscene(string path, string field)
    {
        if (VfsPath.Comparer.Equals(window.Cutscenes.Path, path))
        {
            window.ShowPanel(Cutscene.CutsceneView.PanelId);
            window.ShowPanel(TimelinePanel.PanelId);
            return;
        }

        if (!window.Cutscenes.OpenAndShow(path))
        {
            throw Refused("NoCutscene", AgentTexts.Error("view_cutscene_failed", ("cutscene", path),
                ("reason", window.Cutscenes.Problem ?? "")), field);
        }
    }

    /// <summary>The loaded game data of the open profile.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/> while no game data is loaded.</exception>
    private GameDataManager Data() => window.Session?.GameData ??
                                      throw Refused("NoGameData", AgentTexts.Error("render_no_game_data"));

    /// <summary>The error of a system of W that the loaded game data does not have. An example is a new system of the
    /// active mod before the save.</summary>
    private static EditException NotLoaded(string nickname, string field) =>
        Refused("NotLoaded", AgentTexts.Error("view_not_loaded", ("system", nickname)), field);

    /// <summary>The error of a selection without a frame of an item whose view is not open.</summary>
    private static EditException NotOpen(string reference) =>
        Refused("NotOpen", AgentTexts.Error("select_not_open", ("ref", reference)), "refs");

    private static EditException Refused(string detail, string message, string? field = null) =>
        new(new EditError(EditErrorKind.Refused, detail, message) { Field = field });
}
