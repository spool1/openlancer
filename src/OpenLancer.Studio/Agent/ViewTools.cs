using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Agent;

/// <summary>A panel of the editor that <c>capture_panel</c> captures (section 7.7 of the MCP1 design).</summary>
public enum AgentPanel
{
    Scene,
    Mission,
    Timeline,
    Inspector,
    Hierarchy,
    Console,
}

/// <summary>A view that <c>open_view</c> opens: a system in the Scene, a mission in the Mission tab or a cutscene in the
/// Cutscene tab.</summary>
public enum ViewKind
{
    System,
    Mission,
    Cutscene,
}

/// <summary>Where the editor selects the item of <c>select_items</c>.</summary>
public enum SelectionPlace
{
    /// <summary>The Scene selects the item, and the Inspector shows its fields.</summary>
    Scene,

    /// <summary>Only the Inspector shows the item, for example a loadout, or an object of a system that the Scene does
    /// not show.</summary>
    Inspector,

    /// <summary>The Mission tab selects the item.</summary>
    Mission,

    /// <summary>The Cutscene tab selects the item.</summary>
    Cutscene,
}

/// <summary>The state of the game data of the editor, as <c>studio_status</c> gives it (shared step SH8).</summary>
public enum AgentDataLoad
{
    /// <summary>The editor has the game data of the saved files, and no load runs.</summary>
    Ready,

    /// <summary>The editor loads the game data, for example again after a save. The loaded data does not have the items
    /// of the last save yet.</summary>
    Reloading,
}

/// <summary>What <c>open_view</c> opened.</summary>
/// <param name="Ref">The reference of the system, the mission script or the cutscene.</param>
public sealed record OpenedView(ViewKind Kind, string Ref);

/// <summary>
/// The item of <c>select_items</c>, after the check of its reference: a section of the edit view, with the row of a line
/// of a mission section, a mission script, a cutscene, or an element of a cutscene. Exactly one of
/// <see cref="Section"/>, <see cref="Script"/>, <see cref="Cutscene"/> and <see cref="Element"/> is set.
/// </summary>
/// <param name="Ref">The canonical reference of the item.</param>
public sealed record SelectTarget(string Ref)
{
    public SectionRef? Section { get; init; }

    /// <summary>The nickname of the system whose file holds <see cref="Section"/>, or null for a section of another
    /// file.</summary>
    public string? System { get; init; }

    /// <summary>True when <see cref="Section"/> is a section of a mission script.</summary>
    public bool InMission { get; init; }

    /// <summary>The row of a line of a mission section, or null for the whole section.</summary>
    public int? Row { get; init; }

    /// <summary>The install path of a mission script.</summary>
    public string? Script { get; init; }

    /// <summary>The install path of a cutscene.</summary>
    public string? Cutscene { get; init; }

    public ThnRef? Element { get; init; }
}

/// <summary>The selection after <c>select_items</c>.</summary>
/// <param name="Ref">The reference of the selected item.</param>
/// <param name="Place">Where the editor selects the item: a center tab, or only the Inspector.</param>
/// <param name="Shown">True when the user sees the selection after the call. False when the tab of the item does not
/// show. That tab then selects the item when the user shows it.</param>
/// <param name="Framed">True when the view moved to show the item.</param>
public sealed record SelectionView(string Ref, SelectionPlace Place, bool Shown, bool Framed);

/// <summary>An item that a <c>render_scene</c> image frames.</summary>
/// <param name="Ref">The reference of the call, for the error of an item that the Scene does not show.</param>
/// <param name="Section">The section of the item, or null for the whole system.</param>
public sealed record SceneShotFrame(string Ref, SectionRef? Section);

/// <summary>A call of <c>render_scene</c> after the check of its arguments.</summary>
/// <param name="System">The nickname of the system.</param>
/// <param name="Camera">The camera of the call, or null to frame <see cref="Frame"/>.</param>
/// <param name="Frame">The items to frame, each one an item of the system. An empty list frames the objects of the
/// whole system.</param>
/// <param name="Zones">True to show the zone volumes and the patrol paths.</param>
/// <param name="MaxVisible">The most rows of the visible items.</param>
public sealed record SceneShotRequest(string System, ShotCamera? Camera, IReadOnlyList<SceneShotFrame> Frame, int Width,
    int Height, bool Zones, int MaxVisible);

/// <summary>The camera of an image, in the form of the arguments of <c>render_scene</c>.</summary>
/// <param name="Position">The position of the camera in meters: X, Y and Z.</param>
/// <param name="LookAt">The point that the camera looks at.</param>
/// <param name="Fov">The horizontal angle of the whole image, in degrees.</param>
public sealed record ShotView(float[] Position, float[] LookAt, float Fov)
{
    public static ShotView Of(ShotCamera camera) =>
        new([camera.Position.X, camera.Position.Y, camera.Position.Z], [camera.LookAt.X, camera.LookAt.Y, camera.LookAt.Z],
            MathF.Round(camera.Fov, 2));
}

/// <summary>An item that an image shows, with its place in the image.</summary>
/// <param name="Ref">The reference of the item: <c>obj:</c> or <c>zone:</c>.</param>
/// <param name="X">The distance of the center from the left edge, in pixels.</param>
/// <param name="Y">The distance of the center from the top edge, in pixels.</param>
/// <param name="Radius">The radius of the bounding sphere in the image, in pixels.</param>
/// <param name="Distance">The distance from the camera along the view direction, in meters.</param>
public sealed record VisibleRow(string Ref, float X, float Y, float Radius, float Distance)
{
    /// <summary>The row of an item at a place, with a precision that fits an image.</summary>
    public static VisibleRow Of(string reference, ShotPoint point) =>
        new(reference, MathF.Round(point.X, 1), MathF.Round(point.Y, 1), MathF.Round(point.Radius, 1),
            MathF.Round(point.Distance));
}

/// <summary>The result of <c>render_scene</c>, beside its image.</summary>
/// <param name="System">The <c>system:</c> reference.</param>
/// <param name="Zones">True when the image shows the zone volumes and the patrol paths.</param>
/// <param name="Visible">The items in the view of the image, the largest first.</param>
/// <param name="VisibleTotal">The number of all items in the view of the image.</param>
public sealed record SceneShotView(string System, int Width, int Height, ShotView Camera, bool Zones,
    IReadOnlyList<VisibleRow> Visible, int VisibleTotal);

/// <summary>A call of <c>render_cutscene</c> after the check of its arguments.</summary>
/// <param name="Path">The install path of the cutscene.</param>
/// <param name="Time">The script time in seconds.</param>
/// <param name="Camera">The name of a CAMERA entity, or null for the active camera.</param>
public sealed record CutsceneShotRequest(string Path, double Time, string? Camera, int Width, int Height);

/// <summary>The result of <c>render_cutscene</c>, beside its image.</summary>
/// <param name="Cutscene">The <c>thn:</c> reference.</param>
/// <param name="Time">The script time of the image in seconds: the last step of the preview at or before the time of
/// the call.</param>
/// <param name="Camera">The name of the camera that the image looks through.</param>
/// <param name="Context">The preview context of the image, as the Cutscene tab names it.</param>
/// <param name="WithoutSystem">True when the image shows a space script without its system.</param>
public sealed record CutsceneShotView(string Cutscene, float Time, string Camera, int Width, int Height, string Context,
    bool WithoutSystem);

/// <summary>The result of <c>capture_panel</c>, beside its image.</summary>
public sealed record PanelShotView(AgentPanel Panel, int Width, int Height);

/// <summary>
/// The views of the editor for the view tools (section 7.7 of the MCP1 design): the Scene, the tabs, the selection, the
/// offscreen images and the capture of a panel. Only the editor host has them. Each member runs in the session part of a
/// call, on the UI thread. An image member gives a tail: the image ends in a later frame, and the tail does not touch the
/// session (decision D765).
/// </summary>
/// <remarks>A member gives a refusal as an <see cref="EditException"/>, also from its tail.</remarks>
public interface IAgentViews
{
    /// <summary>Ready, or reloading while the editor loads the game data. A reload after a save waits for a load that
    /// runs, and it counts as reloading while it waits.</summary>
    AgentDataLoad DataLoad { get; }

    /// <summary>True when the loaded game data has the system or the base with the nickname. The Scene, the images of a
    /// system and Play show only such an item.</summary>
    /// <param name="kind"><see cref="ItemRefKind.System"/> or <see cref="ItemRefKind.Base"/>.</param>
    bool Loaded(ItemRefKind kind, string nickname);

    /// <summary>Opens a system in the Scene, a mission in the Mission tab or a cutscene in the Cutscene tab, and shows
    /// the tab.</summary>
    /// <param name="target">The nickname of the system, or the install path of the script or of the cutscene.</param>
    void Open(ViewKind kind, string target);

    /// <summary>
    /// Selects an item. With <paramref name="frame"/>, the editor opens the view of the item, shows its tab and moves the
    /// view to it. The view is the Scene camera, the graph of the Mission tab or the free camera of the Cutscene tab.
    /// Without it, the open views, the tabs and the cameras stay.
    /// </summary>
    SelectionView Select(SelectTarget target, bool frame);

    /// <summary>Renders a system from a camera. The camera of the user does not move, and the Scene keeps its
    /// system.</summary>
    Func<CancellationToken, Task<(SceneShotView View, ToolImage Image)>> RenderScene(SceneShotRequest request);

    /// <summary>Renders a cutscene at a time. The preview of the user does not change.</summary>
    Func<CancellationToken, Task<(CutsceneShotView View, ToolImage Image)>> RenderCutscene(CutsceneShotRequest request);

    /// <summary>Captures a panel as the user sees it, after the next frame.</summary>
    /// <param name="maxSide">The most pixels on each side of the image. A larger panel gives a smaller image.</param>
    Func<CancellationToken, Task<(PanelShotView View, ToolImage Image)>> CapturePanel(AgentPanel panel, int maxSide);
}

/// <summary>
/// The view tools of the editor host (section 7.7 of the MCP1 design): <c>open_view</c>, <c>select_items</c>,
/// <c>render_scene</c>, <c>render_cutscene</c> and <c>capture_panel</c>. A handler checks the arguments and resolves the
/// references on the session. The views of the editor then do the work. No tool changes the data. <c>open_view</c> and
/// <c>select_items</c> change what the user sees.
/// </summary>
/// <remarks>A view of an item of a tool group that the user hides gives <c>not_available</c>, as the tools of that group
/// do (decision D725).</remarks>
internal static class ViewTools
{
    /// <summary>The width of an image when the call gives none.</summary>
    public const int DefaultWidth = 1024;

    /// <summary>The height of an image when the call gives none.</summary>
    public const int DefaultHeight = 640;

    /// <summary>The smallest side of an image, in pixels.</summary>
    public const int MinSide = 16;

    public static ToolStep OpenView(ToolCall call)
    {
        var args = call.Args;
        var given = new[] { "system", "mission", "cutscene" }.Where(args.Has).ToList();
        if (given.Count != 1)
            throw ToolInput.Bad(given.Count == 0 ? "system" : given[1], AgentTexts.Error("open_view_one"));
        var session = call.Session;
        var text = args.Text(given[0])!;
        ViewKind kind;
        string target;
        string reference;
        switch (given[0])
        {
            case "system":
                RequireGroup(call, ToolGroup.Scene);
                (kind, target) = (ViewKind.System, SceneQueries.System(session, text).Nickname);
                RefuseWhileReloading(call, ItemRefKind.System, target, "system");
                reference = SceneQueries.SystemRef(target);
                break;
            case "mission":
                RequireGroup(call, ToolGroup.Missions);
                var script = Existing(session, ScriptRef.Parse(text));
                (kind, target, reference) = (ViewKind.Mission, script.Path, script.ToString());
                break;
            default:
                RequireGroup(call, ToolGroup.Cutscenes);
                (kind, target) = (ViewKind.Cutscene, ItemRefs.ResolveCutscene(session, ItemRefs.Parse(text)));
                reference = CutsceneQueries.Ref(target);
                break;
        }
        Views(call, "open_view").Open(kind, target);
        return ToolResult.Of(new OpenedView(kind, reference));
    }

    public static ToolStep SelectItems(ToolCall call)
    {
        var refs = call.Args.Texts("refs")!;
        // The editor has one selection until milestone M3b adds the selection of many items.
        if (refs.Count > 1)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, "OneItem", AgentTexts.Error("select_one"))
            {
                Field = "refs",
            });
        }
        var target = Target(call.Session, refs[0]);
        if (GroupOf(target) is { } group)
            RequireGroup(call, group);
        var frame = call.Args.Flag("frame");
        // A frame opens the system of the item in the Scene.
        if (frame && target.System is { } system)
            RefuseWhileReloading(call, ItemRefKind.System, system, "refs");
        return ToolResult.Of(Views(call, "select_items").Select(target, frame));
    }

    public static ToolStep RenderScene(ToolCall call)
    {
        var args = call.Args;
        var session = call.Session;
        var (width, height) = Size(call);
        var system = args.Text("system") is { } named ? SceneQueries.System(session, named).Nickname
            : call.Host.View?.System is { } open ? ItemRefs.Parse(open).Name
            : throw new EditException(new EditError(EditErrorKind.Refused, "NoSystem",
                AgentTexts.Error("render_no_system")));
        RefuseWhileReloading(call, ItemRefKind.System, system, "system");
        ShotCamera? camera = null;
        if (args.Group("camera") is { } lens)
        {
            if (args.Has("frame"))
                throw ToolInput.Bad("frame", AgentTexts.Error("render_camera_or_frame"));
            var position = lens.Point("position")!.Value;
            var lookAt = lens.Point("look_at")!.Value;
            if (Vector3.Distance(position, lookAt) < 0.01f)
                throw ToolInput.Bad("camera.look_at", AgentTexts.Error("render_look_at_position"));
            var fov = lens.Number("fov") is { } given ? (float)given : ShotCamera.SceneFov(width / (float)height);
            camera = new ShotCamera(position, lookAt, fov);
        }
        var refs = args.Texts("frame") ?? [];
        if (refs.Count > call.Limits.MaxListRows)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, "FrameItems",
                AgentTexts.Error("limit_frame", ("max", call.Limits.MaxListRows))) { Field = "frame" });
        }
        var frame = refs.Select(r => Frame(session, system, r)).ToList();
        return Image(Views(call, "render_scene").RenderScene(
            new SceneShotRequest(system, camera, frame, width, height, args.Flag("zones"), call.Limits.MaxListRows)));
    }

    public static ToolStep RenderCutscene(ToolCall call)
    {
        var args = call.Args;
        var session = call.Session;
        var (width, height) = Size(call);
        var path = ItemRefs.ResolveCutscene(session, ItemRefs.Parse(args.Text("cutscene")!));
        var duration = session.Cutscene(path)!.Duration;
        var time = args.Number("time") ?? 0;
        if (time > duration)
        {
            throw ToolInput.Bad("time", AgentTexts.Error("render_time_after_end",
                ("duration", Math.Round(duration, 3))));
        }
        return Image(Views(call, "render_cutscene").RenderCutscene(
            new CutsceneShotRequest(path, time, args.Text("camera"), width, height)));
    }

    public static ToolStep CapturePanel(ToolCall call)
    {
        var panel = Enum.GetValues<AgentPanel>().First(p => AgentTools.Snake(p.ToString()) == call.Args.Text("panel"));
        var group = panel switch
        {
            AgentPanel.Scene => ToolGroup.Scene,
            AgentPanel.Mission => ToolGroup.Missions,
            AgentPanel.Timeline => ToolGroup.Cutscenes,
            _ => (ToolGroup?)null,
        };
        if (group is { } hidden)
            RequireGroup(call, hidden);
        return Image(Views(call, "capture_panel").CapturePanel(panel, call.Limits.RenderPixels));
    }

    /// <summary>The detail code of a view or a play that waits for the reload of the game data.</summary>
    public const string DataReloadingDetail = "DataReloading";

    /// <summary>
    /// Refuses a view or a play of a system or a base while the editor reloads the game data (shared step SH8). The
    /// refusal applies only when the saved files have the item and the loaded game data does not have it yet. An example
    /// is a system that the last save added. The reload then loads it. An item that only the unsaved edits have keeps the
    /// refusal of the views, because the reload does not load it (decision O21). The host without a window has no
    /// views and no reload.
    /// </summary>
    /// <param name="field">The argument that names the item, for the error.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/> with the detail
    /// <see cref="DataReloadingDetail"/>.</exception>
    internal static void RefuseWhileReloading(ToolCall call, ItemRefKind kind, string nickname, string field)
    {
        if (call.Host.Views is not { DataLoad: AgentDataLoad.Reloading } views || views.Loaded(kind, nickname) ||
            !Saved(call.Session, kind, nickname))
        {
            return;
        }
        throw new EditException(new EditError(EditErrorKind.Refused, DataReloadingDetail,
            AgentTexts.Error("data_reloading")) { Field = field });
    }

    /// <summary>The error of a system of W that the loaded game data does not have. An example is a new system of the
    /// active mod before the save. The views of the editor give it.</summary>
    /// <param name="field">The argument that names the system, for the error.</param>
    internal static EditException NotLoaded(string nickname, string field) => new(new EditError(EditErrorKind.Refused,
        "NotLoaded", AgentTexts.Error("view_not_loaded", ("system", nickname))) { Field = field });

    /// <summary>True when the saved files have the <c>[system]</c> or the <c>[Base]</c> of <c>universe.ini</c> with the
    /// nickname: the section was in W at the open of the file or at the last save.</summary>
    private static bool Saved(EditSession session, ItemRefKind kind, string nickname) =>
        session.UniversePath is { } universe && session.SavedSection(new SectionRef(universe,
            new SectionIdentity(kind == ItemRefKind.System ? "system" : "Base", nickname))) != null;

    /// <summary>The tail of an image call: it waits for the image, then gives the result with the image.</summary>
    private static ToolStep Image<T>(Func<CancellationToken, Task<(T View, ToolImage Image)>> shot) where T : notnull =>
        ToolStep.Then(async cancel =>
        {
            var (view, image) = await shot(cancel).ConfigureAwait(false);
            return ToolResult.Of(view, image);
        });

    /// <summary>A script of the edit view, with its install path as the session stores it.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the edit view has no such script.</exception>
    private static ScriptRef Existing(EditSession session, ScriptRef script) =>
        new(session.Missions.Model(script.Path).Script);

    /// <summary>The views of the host. The tool table gives <c>not_available</c> to a host without a window before the
    /// handler runs, so this is a guard.</summary>
    private static IAgentViews Views(ToolCall call, string tool) => call.Host.Views ?? throw new EditException(
        new EditError(EditErrorKind.NotAvailable, "EditorOnly", AgentTexts.Error("editor_only", ("tool", tool))));

    /// <summary>Refuses a view of an item of a tool group that the user hides (decision D725 of the MCP1
    /// design).</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotAvailable"/> with the detail
    /// <c>GroupHidden</c>.</exception>
    private static void RequireGroup(ToolCall call, ToolGroup group)
    {
        if (call.Host.HiddenGroups.Contains(group))
            throw new EditException(AgentTools.GroupHidden(group));
    }

    /// <summary>The tool group of an item of <c>select_items</c>, or null for an item of no group, for example a
    /// loadout.</summary>
    private static ToolGroup? GroupOf(SelectTarget target) =>
        target.Element != null || target.Cutscene != null ? ToolGroup.Cutscenes
        : target.Script != null || target.InMission ? ToolGroup.Missions
        : target.System != null ? ToolGroup.Scene
        : null;

    /// <summary>The size of the image of a call: the arguments <c>width</c> and <c>height</c>, or 1024 by 640.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/> for a side above the render limit (section 8.4
    /// of the MCP1 design).</exception>
    private static (int Width, int Height) Size(ToolCall call)
    {
        var max = call.Limits.RenderPixels;
        foreach (var side in new[] { "width", "height" })
        {
            if (call.Args.Whole(side) > max)
            {
                throw new EditException(new EditError(EditErrorKind.Limit, "RenderSize",
                    AgentTexts.Error("limit_render", ("max", max))) { Field = side });
            }
        }
        return ((int)call.Args.Whole("width")!, (int)call.Args.Whole("height")!);
    }

    /// <summary>The item of a reference of <c>select_items</c>.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a kind that the editor cannot
    /// select, or the error of a reference that does not resolve.</exception>
    private static SelectTarget Target(EditSession session, string text)
    {
        var reference = ItemRefs.Parse(text);
        switch (reference.Kind)
        {
            case ItemRefKind.Script:
                var script = Existing(session, new ScriptRef(reference.Path!));
                return new SelectTarget(script.ToString()) { Script = script.Path };
            case ItemRefKind.Cutscene:
                var path = ItemRefs.ResolveCutscene(session, reference);
                return new SelectTarget(CutsceneQueries.Ref(path)) { Cutscene = path };
            case ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent:
                var element = ItemRefs.ResolveCutsceneElement(session, reference);
                return new SelectTarget(ItemRefs.Of(session, element)) { Element = element };
            case ItemRefKind.Label or ItemRefKind.Instance or ItemRefKind.Act or ItemRefKind.Prefab or ItemRefKind.Group
                or ItemRefKind.Asset:
                throw ToolInput.Bad("refs", AgentTexts.Error("select_kind", ("ref", text)));
            default:
                var section = ItemRefs.ResolveSection(session, reference);
                var canonical = reference.Kind == ItemRefKind.Line
                    ? ItemRefs.Format(new ItemReference(ItemRefKind.Line, "")
                    {
                        Path = section.Path,
                        Section = section.Id,
                        Row = reference.Row,
                    })
                    : ItemRefs.Canonical(session, section);
                return new SelectTarget(canonical)
                {
                    Section = section,
                    Row = reference.Kind == ItemRefKind.Line ? reference.Row : null,
                    System = session.IsSystemFile(section.Path) ? session.SystemNicknameOf(section.Path) : null,
                    InMission = SectionKeys.IsMissionFile(section.Path),
                };
        }
    }

    /// <summary>An item to frame: a reference of an item of the system of the image, or the <c>system:</c> reference of
    /// that system.</summary>
    /// <param name="system">The nickname of the system of the image.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a reference of another kind or
    /// of an item of another system, or the error of a reference that does not resolve.</exception>
    private static SceneShotFrame Frame(EditSession session, string system, string text)
    {
        var reference = ItemRefs.Parse(text);
        if (reference.Kind == ItemRefKind.System)
        {
            var named = SceneQueries.System(session, text).Nickname;
            return named.Equals(system, StringComparison.OrdinalIgnoreCase)
                ? new SceneShotFrame(text, null)
                : throw OtherSystem(text, named, system);
        }
        if (reference.Kind is not (ItemRefKind.Object or ItemRefKind.Zone or ItemRefKind.Light or ItemRefKind.Nebula
            or ItemRefKind.Field or ItemRefKind.Section))
        {
            throw ToolInput.Bad("frame", AgentTexts.Error("render_frame_kind", ("ref", text)));
        }
        var section = ItemRefs.ResolveSection(session, reference);
        if (!session.IsSystemFile(section.Path) || session.SystemNicknameOf(section.Path) is not { } owner)
            throw ToolInput.Bad("frame", AgentTexts.Error("render_frame_kind", ("ref", text)));
        return owner.Equals(system, StringComparison.OrdinalIgnoreCase)
            ? new SceneShotFrame(text, section)
            : throw OtherSystem(text, owner, system);
    }

    /// <summary>The error of an item to frame that is in another system than the image.</summary>
    private static EditException OtherSystem(string reference, string owner, string system) =>
        new(new EditError(EditErrorKind.InvalidArgument, "OtherSystem", AgentTexts.Error("render_frame_other_system",
            ("ref", reference), ("other", owner), ("system", system)))
        {
            Field = "frame",
            Target = reference,
        });
}
