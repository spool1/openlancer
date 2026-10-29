using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLancer.Data;
using OpenLancer.Data.Games;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor;

/// <summary>A profile in the list of recent profiles.</summary>
/// <param name="Path">The full path of the profile file.</param>
/// <param name="LastOpenedUtc">The time when the editor opened the profile for the last time.</param>
internal sealed record RecentProfile(string Path, DateTime LastOpenedUtc);

/// <summary>A linked game folder (decision D190). The check of each start finds the game again, so the entry keeps no
/// evidence.</summary>
/// <param name="Path">The full path of the game root, without a final separator. It is the key of the entry.</param>
/// <param name="LinkedUtc">The time of the link.</param>
/// <param name="LastName">The name of the game at the last check, for example "Freelancer 1.1 with JFLP". The editor
/// shows it until the check of this start ends.</param>
/// <param name="LastBuild">The build id of the game at the last check, or null.</param>
/// <param name="PlayCopy">The folder of the play copy of the game that the user picked (decision D309), or null for the
/// default folder next to the game.</param>
internal sealed record LinkedGame(string Path, DateTime LinkedUtc, string LastName, string? LastBuild,
    string? PlayCopy = null);

/// <summary>The play start of a mission script (section 5.2 of the M6c design, decision D762).</summary>
/// <param name="Trigger">The nickname of the trigger of the start, or an empty text for the mission start.</param>
/// <param name="Place">The start place: <c>suggested</c>, <c>camera</c>, or <c>base:</c> with the nickname of a base, as
/// the agent tools write it.</param>
internal sealed record PlayStartSetting(string Trigger, string Place)
{
    /// <summary>The play start of a script without a setting: the mission start at the suggested place.</summary>
    public static readonly PlayStartSetting Default = new("", RunJson.SuggestedPlace);

    /// <summary>The setting of a start and a place.</summary>
    public static PlayStartSetting Of(MissionStartPoint start, StartPlaceChoice place, string? dock) =>
        new(start.Trigger ?? "", RunJson.PlaceText(place, dock));

    /// <summary>The start of the setting.</summary>
    [JsonIgnore]
    public MissionStartPoint Start => Trigger.Trim().Length == 0 ? MissionStartPoint.MissionStart : MissionStartPoint.At(Trigger);

    /// <summary>The place choice and the base of the setting. A text that this editor does not know gives the
    /// suggestion.</summary>
    [JsonIgnore]
    public (StartPlaceChoice Place, string? Base) PlaceChoice =>
        RunJson.ParsePlace(Place) ?? (StartPlaceChoice.Suggested, null);
}

/// <summary>The choices of the Export dialog for one mod (milestone M11).</summary>
/// <param name="Targets">The ids of the checked targets.</param>
/// <param name="DropIn">True when the drop-in zip is checked.</param>
/// <param name="Flmm">True when the FLMM package is checked.</param>
/// <param name="Folder">The output folder.</param>
internal sealed record ExportChoices(IReadOnlyList<string> Targets, bool DropIn, bool Flmm, string Folder);

/// <summary>The mods that the list of the Mods window shows (section 10.2 of the M11c design).</summary>
internal enum ModsFilter
{
    ProfileAndWorkspace,
    Profile,
    Workspace,
}

/// <summary>A tab of the detail page of the Mods window.</summary>
internal enum ModsTab
{
    Settings,
    Preview,
    History,
}

/// <summary>The state of the Mods window that persists (section 10.1 of the M11c design). ImGui keeps the size and the
/// place of the window in <see cref="EditorSettings.ImGuiState"/>.</summary>
internal sealed class ModsWindowSettings
{
    public const float DefaultListWidth = 300;

    [JsonIgnore]
    public ModsFilter Filter { get; set; } = ModsFilter.ProfileAndWorkspace;

    [JsonIgnore]
    public ModsTab Tab { get; set; } = ModsTab.Settings;

    /// <summary>The full path of the selected mod folder, or an empty text.</summary>
    public string SelectedFolder { get; set; } = "";

    /// <summary>The width of the list, in unscaled points.</summary>
    public float ListWidth { get; set; } = DefaultListWidth;

    // The file holds the filter and the tab by name. A name that this editor does not know gives the default.
    [JsonInclude]
    [JsonPropertyName(nameof(Filter))]
    private string FilterName
    {
        get => Filter.ToString();
        set => Filter = EditorSettings.Named(value, ModsFilter.ProfileAndWorkspace);
    }

    [JsonInclude]
    [JsonPropertyName(nameof(Tab))]
    private string TabName
    {
        get => Tab.ToString();
        set => Tab = EditorSettings.Named(value, ModsTab.Settings);
    }
}

/// <summary>
/// The settings of the agent server of the editor (section 10 of the MCP1 design): the start with the editor, the
/// port, the data notice, the hidden tool groups and the limits of one agent call (decision D724).
/// </summary>
internal sealed class AgentSettings
{
    /// <summary>The default port of the agent server on 127.0.0.1 (decision D705).</summary>
    public const int DefaultPort = 47815;

    /// <summary>True when the editor starts the agent server at its start (decision D704).</summary>
    public bool StartWithEditor { get; set; }

    /// <summary>The one port of the server. The server never moves to another port.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>True after the user saw the data notice and started the server (decision D735).</summary>
    public bool NoticeShown { get; set; }

    /// <summary>The names of the tool groups that the user hides (decision D725).</summary>
    public List<string> HiddenGroups { get; set; } = [];

    /// <summary>
    /// True when a play request of an agent saves the unsaved edits of the active mod without a question (user decision
    /// of 2026-10-20 of the M6c design). It is off by default: the editor then asks the user first.
    /// </summary>
    public bool AutoSavePlay { get; set; }

    /// <summary>True when the editor follows each agent edit: it opens and frames its item (decision D719 of the MCP1
    /// design). It is on by default. A camera input or a selection of the user stops follow mode for the run of the
    /// editor, and this setting keeps the last value that the user clicked.</summary>
    public bool FollowAgent { get; set; } = true;

    public AgentLimits Limits { get; set; } = new();

    /// <summary>The hidden tool groups. A name that this editor does not know is ignored.</summary>
    [JsonIgnore]
    public IReadOnlySet<ToolGroup> HiddenToolGroups =>
        HiddenGroups.Select(n => Enum.TryParse<ToolGroup>(n, true, out var g) ? g : (ToolGroup?)null)
            .OfType<ToolGroup>().Where(g => g != ToolGroup.Studio).ToHashSet();

    /// <summary>Hides or shows a tool group.</summary>
    public void SetHidden(ToolGroup group, bool hidden)
    {
        HiddenGroups.RemoveAll(n => n.Equals(group.ToString(), StringComparison.OrdinalIgnoreCase));
        if (hidden)
        {
            HiddenGroups.Add(group.ToString());
        }
    }
}

/// <summary>How the Assets panel shows its tiles (decision D270 of the M23 design).</summary>
internal enum ThumbnailMode
{
    /// <summary>The engine renders a thumbnail of the model of each visible tile.</summary>
    Rendered,

    /// <summary>Each tile shows the glyph of its kind. A hover shows a rendered preview.</summary>
    IconsOnly,
}

/// <summary>
/// Editor preferences that persist between sessions: the UI scale, the panel sizes, the view options, the gizmo tool
/// and its snap, the Database category, the open Inspector sections, the ImGui table state, the recent profiles, the
/// active mod and the Play ship of each profile, the linked games, the workspace folder, the Mods window and the agent
/// server.
/// </summary>
internal sealed class EditorSettings
{
    /// <summary>The maximum number of recent profiles.</summary>
    public const int MaxRecentProfiles = 10;

    // A null value for a property that cannot be null, or a recent profile without a path or a time, throws a
    // JsonException. So a damaged file gives the defaults, and no setting is null.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name, "editor.json");

    // The default panel sizes, in unscaled points.
    public const float DefaultHierarchyWidth = 280;
    public const float DefaultInspectorWidth = 360;
    public const float DefaultBottomHeight = 260;
    // The category list fits its longest name, "Countermeasure", with its count and the scroll bar.
    public const float DefaultDatabaseCategoryWidth = 190;
    public const float DefaultConsoleDetailHeight = 80;
    public const float DefaultAssetsTreeWidth = 200;
    public const float DefaultAssetsTileSize = 96;
    public const string DefaultAssetsCategory = "solars/station";

    // The defaults of the gizmo: the move tool, the world axes and the snap steps of decision D27.
    private const GizmoTool DefaultTool = GizmoTool.Move;
    private const GizmoSpace DefaultSpace = GizmoSpace.World;
    private const float DefaultMoveStep = 100;
    private const float DefaultRotateStep = 15;
    private const float DefaultSizeStep = 100;

    // The move step of the gizmo of the Cutscene view, in meters.
    private const float DefaultCutsceneMoveStep = 0.1f;

    /// <summary>The user's UI scale. The display DPI scale multiplies it.</summary>
    public float UiScale { get; set; } = 1;

    // The panel sizes are unscaled, so they stay the same at each UI scale.
    public float HierarchyWidth { get; set; } = DefaultHierarchyWidth;
    public float InspectorWidth { get; set; } = DefaultInspectorWidth;
    public float BottomHeight { get; set; } = DefaultBottomHeight;
    public float DatabaseCategoryWidth { get; set; } = DefaultDatabaseCategoryWidth;

    /// <summary>The id of the Database category that shows, for example "ships" or "equipment/Gun".</summary>
    public string DatabaseCategory { get; set; } = "systems";

    /// <summary>The height of the details pane below the Console list.</summary>
    public float ConsoleDetailHeight { get; set; } = DefaultConsoleDetailHeight;

    /// <summary>
    /// True when the views show the messages and problems that the base game without mods has too: the Console and the
    /// views of the Mission Studio (decisions D519 of the console filter design and D527 of the M6a design). It is off
    /// by default, so the views show only what the mods cause.
    /// </summary>
    public bool ShowBaseGameMessages { get; set; }

    /// <summary>The share of the width of the Mission tab for the mission part beside the Scene, from 0.1 to 0.9
    /// (decision D522 of the M6a design).</summary>
    public float MissionSplit { get; set; } = Missions.MissionView.DefaultSplit;

    /// <summary>True when the Mission tab shows the Scene beside the mission (decision D522 of the M6a design).</summary>
    public bool MissionShowScene { get; set; } = true;

    /// <summary>The lens of the mission graph (section 6.5 of the M6a design).</summary>
    [JsonIgnore]
    public GraphLens MissionLens { get; set; } = GraphLens.Beats;

    // The file holds the lens by name. A name that this editor does not know gives the default.
    [JsonInclude]
    [JsonPropertyName(nameof(MissionLens))]
    private string MissionLensName
    {
        get => MissionLens.ToString();
        set => MissionLens = Named(value, GraphLens.Beats);
    }

    /// <summary>True when each node of the mission graph shows its condition line and its action lines ("Show lines",
    /// decision D523 of the M6a design).</summary>
    public bool MissionShowLines { get; set; }

    /// <summary>True when the Mission tab shows the library strip at the left of the graph (section 8.3 of the M6b
    /// design).</summary>
    public bool MissionShowLibrary { get; set; } = true;

    /// <summary>True when the Scene shows the items of the open mission (the toggle "Mission items", section 7.2 of the
    /// M6a design).</summary>
    public bool ShowMissionItems { get; set; } = true;

    /// <summary>The id of the category of the Assets panel that shows, for example "solars/station" (milestone
    /// M23).</summary>
    public string AssetsCategory { get; set; } = DefaultAssetsCategory;

    /// <summary>The width of the category tree of the Assets panel, in unscaled points.</summary>
    public float AssetsTreeWidth { get; set; } = DefaultAssetsTreeWidth;

    /// <summary>The size of a tile of the Assets panel, in unscaled points. The smallest size shows a list.</summary>
    public float AssetsTileSize { get; set; } = DefaultAssetsTileSize;

    /// <summary>
    /// How the Assets panel shows its tiles (decision D270 of the M23 design): with rendered thumbnails, or with the glyph
    /// of their kind and a rendered preview on hover.
    /// </summary>
    [JsonIgnore]
    public ThumbnailMode Thumbnails { get; set; } = ThumbnailMode.Rendered;

    // The file holds the mode by name. A name that this editor does not know gives the default.
    [JsonInclude]
    [JsonPropertyName(nameof(Thumbnails))]
    private string ThumbnailsName
    {
        get => Thumbnails.ToString();
        set => Thumbnails = Named(value, ThumbnailMode.Rendered);
    }

    /// <summary>The recent profiles, the newest first.</summary>
    public List<RecentProfile> RecentProfiles { get; set; } = [];

    /// <summary>The linked game folders, in the order of their links (decision D190).</summary>
    public List<LinkedGame> Games { get; set; } = [];

    /// <summary>
    /// The folder of new profiles and mods (decision D191), or an empty text for the default, <c>Documents\OpenLancer</c>.
    /// A change applies to new profiles only (decision D216).
    /// </summary>
    public string WorkspaceFolder { get; set; } = "";

    /// <summary>The workspace folder, or the default when it is empty.</summary>
    [JsonIgnore]
    public string WorkspaceOrDefault =>
        string.IsNullOrWhiteSpace(WorkspaceFolder) ? ModWorkspace.DefaultFolder : WorkspaceFolder;

    /// <summary>
    /// The active mod of each profile, by the full path of the profile file. An empty id means no active mod: the
    /// profile opens read-only. A profile that is not in the list opens with its last mod as the active mod.
    /// </summary>
    public Dictionary<string, string> ActiveMods { get; set; } = new(PathComparer);

    /// <summary>
    /// The ship package of Play for each profile, by the full path of the profile file (decision D15). A profile that is
    /// not in the list, or that has an empty value, plays with the package of a new multiplayer character.
    /// </summary>
    public Dictionary<string, string> PlayPackages { get; set; } = new(PathComparer);

    /// <summary>The choices of the Export dialog of each mod, by the full path of the mod folder.</summary>
    public Dictionary<string, ExportChoices> Exports { get; set; } = new(PathComparer);

    /// <summary>
    /// The play start of each mission script, by the full path of the profile file and the install path of the script
    /// (decision D762 of the M6c design). A script that is not in the list plays from the mission start. The play start
    /// is not data of the mod, so a change of it has no Undo entry.
    /// </summary>
    public Dictionary<string, Dictionary<string, PlayStartSetting>> PlayStarts { get; set; } = new(PathComparer);

    /// <summary>The state of the Mods window (milestone M11c).</summary>
    public ModsWindowSettings ModsWindow { get; set; } = new();

    /// <summary>The settings of the agent server (milestone MCP1).</summary>
    public AgentSettings Agent { get; set; } = new();

    /// <summary>The root of the linked game of the last Play in the game, or null. Ctrl+F5 plays in it (decision
    /// D324).</summary>
    public string? LastGameTarget { get; set; }

    public bool ShowMarkers { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowZones { get; set; } = true;
    public bool ShowLights { get; set; } = true;
    public bool ShowNebulae { get; set; } = true;

    /// <summary>
    /// The fog of the nebula that holds the camera. It is on by default, as the fog of the Unity Scene view: the scene
    /// then looks as the game does. When it is off, each nebula draws as seen from outside, so that the area of a
    /// nebula shows also from inside it.
    /// </summary>
    public bool ShowFog { get; set; } = true;
    public bool ShowStarsphere { get; set; } = true;
    public bool Wireframe { get; set; }

    /// <summary>
    /// The sound of the cutscene preview (decision D132 of the M8b design). It is on by default, as the sound of the
    /// Unity Game view is on until the user turns on Mute Audio.
    /// </summary>
    public bool PreviewSound { get; set; } = true;

    /// <summary>
    /// The toggle "Script objects" of the Cutscene tab in a space context (decision D645 of the space context design).
    /// It shows the objects that a space script makes as stand-ins for the mission objects. It is on by default.
    /// </summary>
    public bool CutsceneScriptObjects { get; set; } = true;

    /// <summary>The tool of the gizmo.</summary>
    [JsonIgnore]
    public GizmoTool Tool { get; set; } = DefaultTool;

    /// <summary>The axes of a move or a turn. A scale always uses the axes of the item.</summary>
    [JsonIgnore]
    public GizmoSpace Space { get; set; } = DefaultSpace;

    // The file holds the tool and the space by name. A name that this editor does not know, for example a tool of a
    // later editor, gives the default of that setting. So the other settings stay.
    [JsonInclude]
    [JsonPropertyName(nameof(Tool))]
    private string ToolName
    {
        get => Tool.ToString();
        set => Tool = Named(value, DefaultTool);
    }

    [JsonInclude]
    [JsonPropertyName(nameof(Space))]
    private string SpaceName
    {
        get => Space.ToString();
        set => Space = Named(value, DefaultSpace);
    }

    /// <summary>True when a drag of the gizmo snaps to the steps. Ctrl during a drag reverses it.</summary>
    public bool Snap { get; set; }

    // The snap steps of the gizmo: a distance, an angle in degrees and a size. Each step is above 0.
    public float MoveStep { get; set; } = DefaultMoveStep;
    public float RotateStep { get; set; } = DefaultRotateStep;
    public float SizeStep { get; set; } = DefaultSizeStep;

    /// <summary>The move step of the gizmo of the Cutscene view, in meters. A cutscene is room size.</summary>
    public float CutsceneMoveStep { get; set; } = DefaultCutsceneMoveStep;

    /// <summary>The snap of a drag of the gizmo, before Ctrl (see <see cref="GizmoSnap.WithCtrl"/>).</summary>
    [JsonIgnore]
    public GizmoSnap DragSnap => new(Snap, MoveStep, RotateStep, SizeStep);

    /// <summary>
    /// The open state of the Inspector sections that the user changed, by section key ("Type.Section"). A section that
    /// is not in the list has its default state.
    /// </summary>
    public Dictionary<string, bool> InspectorFoldouts { get; set; } = [];

    /// <summary>
    /// The ImGui state in the ImGui .ini text format: the column widths, order, visibility and sort of the tables. Null
    /// until ImGui saves it for the first time.
    /// </summary>
    public string? ImGuiState { get; set; }

    /// <summary>
    /// The only profile that an editor before the recent list kept. <see cref="Parse"/> moves it into
    /// <see cref="RecentProfiles"/>, so the settings never write it again.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("LastProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private string? LegacyLastProfile { get; set; }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // False for the settings of a run that must not change the settings file, for example the preview bench.
    private bool persistent = true;

    /// <summary>The default settings, which <see cref="Save"/> never writes. The preview bench uses them, so that it does
    /// not change the settings of the user.</summary>
    public static EditorSettings Transient() => new() { persistent = false };

    /// <summary>Reads the settings. A missing or damaged file gives the defaults.</summary>
    public static EditorSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? Parse(File.ReadAllText(FilePath), File.GetLastWriteTimeUtc(FilePath))
                : new EditorSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new EditorSettings();
        }
    }

    /// <summary>
    /// Reads the settings from their JSON text. The last profile of an older editor becomes the first recent profile,
    /// so the editor still opens it at the start. A gizmo tool or space that this editor does not know, or a snap step
    /// that is not a finite number above 0, gives the default of that setting only.
    /// </summary>
    /// <param name="savedUtc">The time when the file was saved. It is the time of a moved last profile.</param>
    /// <exception cref="JsonException">The text is not valid settings, for example a setting is null or has the wrong
    /// JSON type.</exception>
    internal static EditorSettings Parse(string json, DateTime savedUtc)
    {
        var settings = JsonSerializer.Deserialize<EditorSettings>(json, JsonOptions) ?? new EditorSettings();
        // The serializer does not check the items of a list for null.
        if (settings.RecentProfiles.Any(r => r is null || string.IsNullOrWhiteSpace(r.Path)))
        {
            throw new JsonException("The list of recent profiles has an entry without a path.");
        }

        if (settings.Games.Any(g => g is null || string.IsNullOrWhiteSpace(g.Path)))
        {
            throw new JsonException("The list of linked games has an entry without a path.");
        }

        // A drag with snapping needs a finite step above 0 (see GizmoDrag.Update). Another step, for example a step of
        // 0 from a hand edit, gives the default step. So the other settings stay.
        settings.MoveStep = ValidStep(settings.MoveStep, DefaultMoveStep);
        settings.RotateStep = ValidStep(settings.RotateStep, DefaultRotateStep);
        settings.SizeStep = ValidStep(settings.SizeStep, DefaultSizeStep);
        settings.CutsceneMoveStep = ValidStep(settings.CutsceneMoveStep, DefaultCutsceneMoveStep);
        settings.MissionSplit = float.IsFinite(settings.MissionSplit)
            ? Math.Clamp(settings.MissionSplit, 0.1f, 0.9f)
            : Missions.MissionView.DefaultSplit;

        if (settings.LegacyLastProfile is { Length: > 0 } last && settings.RecentProfiles.Count == 0)
        {
            settings.RecentProfiles.Add(new RecentProfile(last, savedUtc));
        }

        // A port outside the range of TCP gives the default port, and each limit goes into its allowed range.
        settings.Agent.Port = settings.Agent.Port is > 0 and <= 65535 ? settings.Agent.Port : AgentSettings.DefaultPort;
        settings.Agent.Limits = settings.Agent.Limits.Clamped();

        settings.LegacyLastProfile = null;
        // The serializer makes the dictionaries without the comparer of the paths.
        settings.ActiveMods = new Dictionary<string, string>(settings.ActiveMods, PathComparer);
        settings.PlayPackages = new Dictionary<string, string>(settings.PlayPackages, PathComparer);
        settings.Exports = new Dictionary<string, ExportChoices>(settings.Exports, PathComparer);
        settings.PlayStarts = settings.PlayStarts.Where(p => p.Value != null).ToDictionary(p => p.Key,
            p => new Dictionary<string, PlayStartSetting>(p.Value.Where(s => s.Value is { Trigger: not null, Place: not null }),
                StringComparer.OrdinalIgnoreCase), PathComparer);
        return settings;
    }

    /// <summary>The value of an enum name, or the fallback for a name that this editor does not know.</summary>
    internal static T Named<T>(string name, T fallback) where T : struct, Enum =>
        Enum.GetValues<T>().FirstOrDefault(v => v.ToString().Equals(name, StringComparison.OrdinalIgnoreCase), fallback);

    /// <summary>The step, or the fallback for a step that is not a finite number above 0.</summary>
    private static float ValidStep(float step, float fallback) => float.IsFinite(step) && step > 0 ? step : fallback;

    /// <summary>
    /// The id of the active mod of a profile: the stored id when the profile has that mod, else the last mod of the
    /// profile. Null means no active mod.
    /// </summary>
    /// <param name="modIds">The ids of the mods of the profile, in load order.</param>
    public string? ActiveModOf(string profilePath, IReadOnlyList<string> modIds)
    {
        if (ActiveMods.TryGetValue(profilePath, out var id) && (id.Length == 0 || modIds.Contains(id)))
        {
            return id.Length == 0 ? null : id;
        }

        return modIds.Count > 0 ? modIds[^1] : null;
    }

    /// <summary>Stores the active mod of a profile. Null stores no active mod.</summary>
    public void SetActiveMod(string profilePath, string? modId) => ActiveMods[profilePath] = modId ?? "";

    /// <summary>The nickname of the ship package of Play for a profile, or null for the default package.</summary>
    public string? PlayPackageOf(string profilePath) =>
        PlayPackages.TryGetValue(profilePath, out var package) && !string.IsNullOrWhiteSpace(package) ? package : null;

    /// <summary>The play start of a mission script of a profile, or the mission start with the suggested place.</summary>
    /// <param name="script">The install path of the script.</param>
    public PlayStartSetting PlayStartOf(string profilePath, string script) =>
        PlayStarts.TryGetValue(profilePath, out var scripts) && scripts.TryGetValue(script, out var start)
            ? start
            : PlayStartSetting.Default;

    /// <summary>Stores the play start of a mission script. The mission start with the suggested place removes the
    /// entry.</summary>
    public void SetPlayStart(string profilePath, string script, PlayStartSetting start)
    {
        if (!PlayStarts.TryGetValue(profilePath, out var scripts))
        {
            PlayStarts[profilePath] = scripts = new Dictionary<string, PlayStartSetting>(StringComparer.OrdinalIgnoreCase);
        }

        if (start.Trigger.Trim().Length == 0 && start.Place == RunJson.SuggestedPlace)
        {
            scripts.Remove(script);
        }
        else
        {
            scripts[script] = start;
        }
    }

    /// <summary>Moves the play start of a script to the new name of its trigger (section 5.2 of the M6c design).</summary>
    /// <returns>True when the play start changed.</returns>
    public bool RenamePlayStart(string profilePath, string script, string from, string to)
    {
        var start = PlayStartOf(profilePath, script);
        var point = start.Start;
        var renamed = point.Renamed(from, to);
        if (ReferenceEquals(renamed, point))
        {
            return false;
        }

        SetPlayStart(profilePath, script, start with { Trigger = renamed.Trigger! });
        return true;
    }

    /// <summary>The choices of the Export dialog for a mod folder, or null before the first export of the mod.</summary>
    public ExportChoices? ExportOf(string modFolder) => Exports.GetValueOrDefault(modFolder);

    /// <summary>Stores the choices of the Export dialog for a mod folder.</summary>
    public void SetExport(string modFolder, ExportChoices choices) => Exports[modFolder] = choices;

    /// <summary>
    /// Moves the keys of a renamed mod (section 12.3 of the M11c design): the choices of the Export dialog, the selected
    /// mod of the Mods window and the active mod of each profile of the rename. A saved output folder that is the old
    /// default folder <c>exports\&lt;old id&gt;</c> becomes the new default folder (finding R10).
    /// </summary>
    /// <param name="workspace">The workspace of the rename, which holds the <c>exports</c> folder.</param>
    public void RenameMod(RenamePlan plan, string workspace)
    {
        if (RenamedExport(plan, workspace) is { } choices)
        {
            Exports.Remove(plan.OldFolder);
            Exports[plan.NewFolder] = choices;
        }

        if (ModsWindow.SelectedFolder.Length > 0 && GameFolders.Same(ModsWindow.SelectedFolder, plan.OldFolder))
        {
            ModsWindow.SelectedFolder = plan.NewFolder;
        }

        foreach (var profile in RenamedActiveMods(plan))
        {
            ActiveMods[profile] = plan.NewId;
        }
    }

    /// <summary>The changes of <see cref="RenameMod"/> as lines of the plan of the Rename dialog (section 12.3 of the
    /// M11c design).</summary>
    public IReadOnlyList<string> RenameChanges(RenamePlan plan, string workspace)
    {
        var lines = new List<string>();
        if (RenamedExport(plan, workspace) is { } choices)
        {
            lines.Add("The editor keeps the choices of the Export dialog for the new mod folder.");
            if (choices.Folder != Exports[plan.OldFolder].Folder)
            {
                lines.Add($"The output folder of the Export dialog changes to {choices.Folder}.");
            }
        }

        lines.AddRange(RenamedActiveMods(plan)
            .Select(p => $"The active mod of the profile {p} changes to {plan.NewId}."));
        return lines;
    }

    /// <summary>The saved choices of the Export dialog after a rename, or null when the mod has none. A saved output
    /// folder that is the old default folder <c>exports\&lt;old id&gt;</c> becomes the new default folder.</summary>
    private ExportChoices? RenamedExport(RenamePlan plan, string workspace)
    {
        if (!Exports.TryGetValue(plan.OldFolder, out var choices))
        {
            return null;
        }

        var oldDefault = PublishHistory.FolderOf(workspace, plan.OldId);
        var folder = choices.Folder.Length > 0 && GameFolders.Same(choices.Folder, oldDefault)
            ? PublishHistory.FolderOf(workspace, plan.NewId)
            : choices.Folder;
        return choices with { Folder = folder };
    }

    /// <summary>The profiles of a rename whose saved active mod is the old id.</summary>
    private IEnumerable<string> RenamedActiveMods(RenamePlan plan) =>
        plan.Profiles.Where(p => ActiveMods.TryGetValue(p, out var id) && id == plan.OldId).ToList();

    /// <summary>Stores the ship package of Play for a profile. Null sets the default package again.</summary>
    public void SetPlayPackage(string profilePath, string? package)
    {
        if (package == null)
        {
            PlayPackages.Remove(profilePath);
        }
        else
        {
            PlayPackages[profilePath] = package;
        }
    }

    /// <summary>
    /// Puts a profile at the top of the recent list. The list keeps each path once and has at most
    /// <see cref="MaxRecentProfiles"/> entries.
    /// </summary>
    public void AddRecentProfile(string path, DateTime utcNow)
    {
        RemoveRecentProfile(path);
        RecentProfiles.Insert(0, new RecentProfile(path, utcNow));
        if (RecentProfiles.Count > MaxRecentProfiles)
        {
            RecentProfiles.RemoveRange(MaxRecentProfiles, RecentProfiles.Count - MaxRecentProfiles);
        }
    }

    public void RemoveRecentProfile(string path) => RecentProfiles.RemoveAll(r => PathComparer.Equals(r.Path, path));

    /// <summary>The serialized settings, as <see cref="Save"/> writes them.</summary>
    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Writes the settings. A failure only loses the preferences, so it does not stop the editor.</summary>
    public void Save()
    {
        if (!persistent)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, ToJson());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
