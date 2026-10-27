using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The Mission tab (section 7.1 of the M6a design): the graph of the open mission, or its raw INI text, beside the
/// Scene. A splitter is between them. The graph toolbar is the strip of the tab, and the Scene toolbar is a strip above
/// the Scene part. In the raw view, the text has the colors of the INI mode and the marks of the checks. A cursor in a
/// section selects its item, and a selection of an item scrolls the text to its section. The user can edit the text
/// while the session takes edits (section 8.8 of the M6b design).
/// </summary>
internal sealed class MissionView : IDisposable
{
    // Sizes in unscaled points.
    private const float SplitterWidth = 6;
    private const float MinSceneTabWidth = 640;
    private const float MinPart = 160;
    private const float LensWidth = 150;
    private const float SearchWidth = 200;

    /// <summary>The share of the width of the mission part, by default 55 percent (decision D522).</summary>
    public const float DefaultSplit = 0.55f;

    private readonly EditorWindow window;
    private readonly MissionWorkspace missions;
    private readonly RawIniEditor raw = new(MissionWorkspace.Vocabulary);
    private readonly MissionLinks links;
    private readonly LibraryStrip library;
    private readonly RunPanel runPanel;

    // True while the Mission tab is narrower than LibraryStrip.MinTabWidth, from the last frame.
    private bool narrowTab;

    // True when the toggle opened the library strip in a narrow tab.
    private bool libraryInNarrowTab;
    private int lastCursor = -1;
    private object? lastSelection;
    private string? shownPath;

    // True when the search field of the graph takes the keyboard in the next frame: Ctrl+F, or after Enter.
    private bool focusSearch;

    public MissionView(EditorWindow window, MissionWorkspace missions)
    {
        this.window = window;
        this.missions = missions;
        State = new GraphViewState(window.Settings.MissionLens);
        Graph = new GraphView(window, missions, State);
        library = new LibraryStrip(window);
        Graph.Library = library;
        links = new MissionLinks(window, missions, Graph);
        runPanel = new RunPanel(window, missions, Graph);
        window.Scene.DoubleClicked += item =>
        {
            if (window.CenterMode == CenterMode.Mission && missions.Open is { } open)
            {
                links.SceneDoubleClicked(open, item);
            }
        };
    }

    /// <summary>The state of the graph of the session: frames, lines, drags.</summary>
    public GraphViewState State { get; }

    /// <summary>The graph part of the tab.</summary>
    public GraphView Graph { get; }

    /// <summary>The lens texts of section 13.1 of the M6a design.</summary>
    internal static string LensTip(GraphLens lens) => lens switch
    {
        GraphLens.Beats => "Shows each behavior prefab as one box. This is the default view.",
        GraphLens.Logic => "Shows all triggers and opens all frames.",
        GraphLens.Battle => "Shows spawns, orders, sides and combat conditions. Other triggers fade.",
        GraphLens.Conversation => "Shows comm lines, conversations and the triggers that wait for them. Other triggers fade.",
        GraphLens.Cutscene => "Shows space cutscenes, cameras and base scenes, in the order of the mission.",
        _ => "Shows objectives, titles, offers and log texts, in the order of the mission.",
    };

    public void Toolbar()
    {
        var result = missions.Result;
        var open = missions.OpenPath;
        var scale = ImGuiHelper.Scale;
        if (missions.OpenPrefab != null)
        {
            PrefabToolbar(result, scale);
            return;
        }

        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.BeginCombo("##mission", open != null ? Label(result, open) : "Mission"))
        {
            EditorTheme.PushMenuColors();
            foreach (var script in result?.Scripts.Where(s => s.State != MissionScriptState.Missing) ?? [])
            {
                if (ImGui.Selectable(Label(result, script.Path), string.Equals(script.Path, open, StringComparison.OrdinalIgnoreCase)))
                {
                    missions.OpenMission(script.Path, show: true);
                }
            }

            EditorTheme.PopMenuColors();
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip("Open another mission.");
        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        if (ToolbarItems.Toggle("graph", EditorIcons.ProjectDiagram, "Graph: show the graph of the mission.",
                !missions.ShowRaw))
        {
            missions.ShowRaw = false;
        }

        ImGui.SameLine();
        if (ToolbarItems.Toggle("raw", EditorIcons.Terminal, "Raw INI: show the whole script as text.", missions.ShowRaw))
        {
            missions.ShowRaw = true;
        }

        if (!missions.ShowRaw)
        {
            ImGui.SameLine();
            DryRunToggle();
            if (open != null)
            {
                ImGui.SameLine();
                PlayStartChip(open);
            }

            GraphToolbar(scale);
            EditToolbar();
        }

        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        var settings = window.Settings;
        var scene = ToolbarItems.Toggle("scene", EditorIcons.Globe, "Scene: show the Scene beside the graph.",
            settings.MissionShowScene);
        if (scene != settings.MissionShowScene)
        {
            settings.MissionShowScene = scene;
            settings.Save();
        }

        ImGui.SameLine();
        MissionsBrowser.BaseGameToggle(window, missions);
        if (missions.ReadOnlyNote is { } note)
        {
            ImGui.SameLine();
            Chip.Flow([new ChipItem("Read-only", ChipLook.Grey, note)]);
        }

        ImGui.SameLine();
        InfoIcon.Draw("help", MissionSchema.Retail.Help[missions.ShowRaw ? "rawIni" : "missionGraph"]);
    }

    /// <summary>
    /// The toolbar of prefab mode (section 8.6 of the M6b design): the back arrow, the path "{mission} > {prefab name}",
    /// and the items of the graph. The Scene toggle and the raw view do not show, because a template has no world and its
    /// file is an editor file.
    /// </summary>
    private void PrefabToolbar(MissionRunResult? result, float scale)
    {
        var model = missions.PrefabMode;
        if (ToolbarItems.Button("back", EditorIcons.ArrowLeft, "Back: leave prefab mode. Shortcut: Esc."))
        {
            missions.LeavePrefabMode();
            return;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        var mission = missions.OpenPath is { } path ? Label(result, path) : "No mission";
        ImGui.Text(PrefabBarTitle(mission, model?.Asset.Name ?? missions.OpenPrefab!.Id));
        GraphToolbar(scale);
        EditToolbar();
        ImGui.SameLine();
        InfoIcon.Draw("help", MissionPageModel.PrefabModeHelp);
    }

    /// <summary>The path of the bar of prefab mode: "{mission} > {prefab name}" (section 8.11 of the M6b design).</summary>
    internal static string PrefabBarTitle(string mission, string prefab) => $"{mission} > {prefab}";

    /// <summary>The bar of prefab mode at the top of the Mission tab: what a change does, or the banner of a prefab that
    /// cannot change with the button "Make variant" (section 8.6 of the M6b design).</summary>
    private void PrefabBar(PrefabModeModel model)
    {
        var mod = window.Session?.Edits.ActiveMod?.Id;
        ImGui.PushTextWrapPos(0);
        if (model.ReadOnlyReason is { } reason)
        {
            ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.Lock}  {reason}");
            if (missions.ReadOnlyNote == null && model.Template != null && window.Session?.Edits.Missions is { } editor)
            {
                ImGui.SameLine();
                // The run opens the variant in prefab mode, and the variant can change.
                if (ImGui.SmallButton("Make variant"))
                {
                    missions.Run(MissionPageModel.VariantBatch(editor.FreePrefabId, model.Asset.Ref, model.Asset.Name));
                }

                ImGui.SetItemTooltip(MissionPageModel.MakeVariantTip);
            }
        }
        else
        {
            // The text uses the text color. The violet is only for the marks of the overrides.
            ImGui.Text($"{EditorIcons.Cubes}  {model.BarText(mod)}");
        }

        ImGui.PopTextWrapPos();
    }

    /// <summary>The tooltip of the toggle "Dry run" (section 8.1 of the M6c design).</summary>
    internal const string DryRunTip = "Dry run: Run the trigger rules of the retail game on this graph, step by step. The " +
                                      "dry run has no game world. You decide what happens in the world.";

    /// <summary>The tooltip of the toggle "Dry run" while a mission play runs (section 6.3 of the M6c design).</summary>
    internal const string DryRunPlayTip = "Play runs. The graph shows the state of the triggers in the game. Stop the " +
                                          "game to use the dry run.";

    /// <summary>The toggle "Dry run": the Run panel shows at the right of the graph, and the graph shows the run
    /// states (section 4.1 of the M6c design). It shows its word beside a test glyph, so nobody takes it for the Play of
    /// the game. While a mission play runs, the graph shows the live view, and the toggle is off.</summary>
    private void DryRunToggle()
    {
        if (missions.MissionPlayRuns)
        {
            ImGui.BeginDisabled();
            ToolbarItems.LabelToggle("dry-run", EditorIcons.Flask, "Dry run", DryRunPlayTip, false);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(DryRunPlayTip);
            }

            return;
        }

        var on = missions.DryRuns.On;
        if (ToolbarItems.LabelToggle("dry-run", EditorIcons.Flask, "Dry run", DryRunTip, on) != on)
        {
            missions.DryRuns.On = !on;
        }
    }

    /// <summary>The tooltip of the chip of the play start (section 8.1 of the M6c design). The sentence about Ctrl+F5
    /// comes with the start in the linked game (milestone M6c-3).</summary>
    internal const string PlayStartTip = "F5 plays this mission from this start in OpenLancer. Click to see the start.";

    /// <summary>The first line of the tooltip of the warning chip after the chip of the play start.</summary>
    internal const string ChoiceProblemLead = "Play cannot start at the start place of the play start. F5 and \"Play from " +
                                              "here\" do not play, and no node shows a Play button.";

    /// <summary>
    /// The chip "Play start: {start}" of the toolbar (section 5.2 of the M6c design). A click selects the play start,
    /// and the Inspector shows the Play start page. The graph selects and frames the trigger of the play start (UI rule
    /// 6). When the place choice of the play start cannot work for any trigger, a warning chip follows with the reason,
    /// because no node of the graph then shows a Play button.
    /// </summary>
    private void PlayStartChip(string script)
    {
        if (ImGui.Button($"{EditorIcons.Play}  Play start: {missions.Plays.StartNameOf(script)}##play-start"))
        {
            window.Selection = MissionRef.PlayStartOf(script);
            missions.PendingGraphFrame = true;
        }

        ImGui.SetItemTooltip(PlayStartTip);
        if (window.StartChoiceProblem(missions.Plays.PlayStartOf(script)) is { } problem)
        {
            ImGui.SameLine();
            Chip.Flow([new ChipItem(EditorIcons.ExclamationTriangle.ToString(), ChipLook.GreyWarning)]);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            {
                ItemTooltip.Wrapped(ChoiceProblemTip(problem));
            }
        }
    }

    /// <summary>The tooltip of the warning chip after the chip of the play start: the lead and the reason.</summary>
    internal static string ChoiceProblemTip(string problem) => $"{ChoiceProblemLead}\n{problem}";

    /// <summary>The info icon of the banner of the rules (section 8.5 of the M6c design).</summary>
    internal static readonly HelpText RulesHelp = new("Rules differ",
        "The OpenLancer engine reads some trigger lines in another way than the retail game. The differences are in two " +
        "conditions, lines before the nickname, repeated nicknames, triggers without a condition and the order of " +
        "starts. They are also in restarts, repeatable triggers, Cnd_SystemEnter, Cnd_SpaceEnter with a system and the " +
        "COMPLETE state.", "Nothing.");

    /// <summary>The text of the banner of the rules (section 8.1 of the M6c design).</summary>
    internal const string RulesBanner = "OpenLancer runs the triggers with its own rules. In some cases they differ from the " +
                                        "retail game. The dry run shows the retail rules.";

    /// <summary>The kind of the play banner of the Mission tab (section 6.3 of the M6c design).</summary>
    internal enum PlayBanner
    {
        /// <summary>No mission play runs.</summary>
        None,

        /// <summary>The game runs a mission without a story slot, so the graph has no state of it.</summary>
        NoSlot,

        /// <summary>The graph shows another mission than the game runs.</summary>
        OtherMission,

        /// <summary>The graph shows the mission of the game.</summary>
        Play,
    }

    /// <summary>The text of the play banner while the game runs a mission without a story slot.</summary>
    internal const string NoSlotBanner = "The game runs a mission that has no story slot.";

    /// <summary>The text of the play banner while the graph shows another mission than the game runs.</summary>
    internal static string OtherMissionBanner(string liveSlot, string openSlot) =>
        $"The game runs {liveSlot}. The graph shows {openSlot}.";

    /// <summary>The text of the play banner while the graph shows the mission of the game.</summary>
    internal static string PlayBannerText(string slot, string start) =>
        $"Play runs {slot} from {start}. The graph shows the state of the triggers in the game.";

    /// <summary>
    /// The play banner of a mission play: none while no mission play runs, the banner of a mission without a story slot,
    /// the banner of another mission, or the banner of the play.
    /// </summary>
    /// <param name="liveScript">The script of the mission that the game runs, by its story slot, or null.</param>
    /// <param name="liveKnown">True when a mission event named the mission of the game.</param>
    /// <param name="playScript">The script of the start of the mission play.</param>
    /// <param name="openScript">The script that the graph shows.</param>
    internal static PlayBanner PlayBannerOf(bool playRuns, string? liveScript, bool liveKnown, string playScript,
        string openScript)
    {
        if (!playRuns)
        {
            return PlayBanner.None;
        }

        // Before the first mission event, the game runs the script of the start.
        var live = liveScript ?? (liveKnown ? null : playScript);
        return live == null ? PlayBanner.NoSlot
            : !string.Equals(live, openScript, StringComparison.OrdinalIgnoreCase) ? PlayBanner.OtherMission
            : PlayBanner.Play;
    }

    /// <summary>
    /// The banners of a mission play (section 6.3 of the M6c design): the play with a Stop button while the graph shows
    /// the mission of the game, or a banner with a button that opens the mission of the game. The banner of the rules of
    /// OpenLancer shows while a mission play runs (section 6.4).
    /// </summary>
    private void LiveBanners(MissionAnalysis analysis)
    {
        if (window.Play.Mission is not { } play)
        {
            return;
        }

        var kind = PlayBannerOf(missions.MissionPlayRuns, missions.LiveScript, missions.Live.Mission != null, play.Script,
            analysis.Script.Path);
        if (kind == PlayBanner.None)
        {
            return;
        }

        var size = new Vector2(ImGui.GetContentRegionAvail().X, EditorLayout.StripHeight);
        switch (kind)
        {
            case PlayBanner.NoSlot:
                EditorLayout.Strip("##play-banner", () => BannerText(EditorIcons.Play, NoSlotBanner), size);
                break;
            case PlayBanner.OtherMission:
                var live = missions.LiveScript ?? play.Script;
                var liveSlot = missions.Analysis(live)?.Script.Slot?.Slot ?? MissionFiles.Display(live);
                var openSlot = analysis.Script.Slot?.Slot ?? MissionFiles.Display(analysis.Script.Path);
                EditorLayout.Strip("##play-banner", () =>
                {
                    BannerText(EditorIcons.Play, OtherMissionBanner(liveSlot, openSlot));
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Open {liveSlot}"))
                    {
                        missions.OpenMission(live, show: true);
                    }
                }, size);
                break;
            default:
                EditorLayout.Strip("##play-banner", () =>
                {
                    BannerText(EditorIcons.Play, PlayBannerText(play.Slot, play.StartName));
                    ImGui.SameLine();
                    // The Stop of the Play menu: Force stop when the game does not respond.
                    var force = window.Play.ForceStop;
                    if (ImGui.SmallButton(PlayMenu.StopLabel(force)))
                    {
                        PlayRequests.Stop(window.Play, window.GamePlay);
                    }

                    ImGui.SetItemTooltip(PlayMenu.StopTip(force));
                }, size);
                break;
        }

        EditorLayout.Strip("##rules-banner", () =>
        {
            BannerText(EditorIcons.InfoCircle, RulesBanner);
            ImGui.SameLine();
            InfoIcon.Draw("rules-help", RulesHelp);
        }, size);
    }

    private static void BannerText(char glyph, string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(EditorTheme.MissionRun, glyph.ToString());
        ImGui.SameLine();
        ImGui.Text(text);
    }

    /// <summary>The edit items of the graph toolbar (section 8.11 of the M6b design): the toggle "Library" and the button
    /// "Add...".</summary>
    private void EditToolbar()
    {
        var settings = window.Settings;
        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        var before = LibraryStrip.Shows(settings.MissionShowLibrary, narrowTab, libraryInNarrowTab);
        var shown = ToolbarItems.Toggle("library", EditorIcons.Boxes,
            "Library: show the prefabs, events, actions and conditions that you can add.", before);
        if (shown != before)
        {
            // In a narrow tab, the toggle opens or closes the strip for this tab width only.
            var (setting, inNarrowTab) = LibraryStrip.Toggled(settings.MissionShowLibrary, narrowTab, shown);
            libraryInNarrowTab = inNarrowTab;
            if (setting != settings.MissionShowLibrary)
            {
                settings.MissionShowLibrary = setting;
                settings.Save();
            }
        }

        if (missions.ShownPath is { } path && missions.NoEditReason(path) == null)
        {
            ImGui.SameLine();
            if (ToolbarItems.Button("add", EditorIcons.Plus, "Add...: add a trigger, a prefab, an action or a condition. Shortcut: Space."))
            {
                Graph.OpenQuickAdd(path, new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y));
            }
        }
    }

    /// <summary>The items of the graph toolbar: the lens, "Show lines" and "Frame all".</summary>
    private void GraphToolbar(float scale)
    {
        var settings = window.Settings;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(LensWidth * scale);
        if (ImGui.BeginCombo("##lens", $"Lens: {settings.MissionLens}"))
        {
            EditorTheme.PushMenuColors();
            foreach (var lens in Enum.GetValues<GraphLens>())
            {
                if (ImGui.Selectable(lens.ToString(), lens == settings.MissionLens))
                {
                    settings.MissionLens = lens;
                    State.Lens = lens;
                    settings.Save();
                }

                ImGui.SetItemTooltip(LensTip(lens));
            }

            EditorTheme.PopMenuColors();
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip("Change what the graph shows. A lens does not change data.");
        ImGui.SameLine();
        var lines = ToolbarItems.Toggle("lines", EditorIcons.List,
            "Show lines: show the condition line and the action lines of each trigger. Off shows a summary.",
            settings.MissionShowLines);
        if (lines != settings.MissionShowLines)
        {
            settings.MissionShowLines = lines;
            settings.Save();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("frame-all", EditorIcons.CompressArrowsAlt,
                "Frame all: show the whole graph. Shortcut: A or Home."))
        {
            Graph.FrameAll();
        }

        ImGui.SameLine();
        SearchField(scale);
        ImGui.SameLine();
        SystemsMenu();
    }

    /// <summary>The search of the graph (section 6.6 of the M6a design): the results list under the field. Enter frames
    /// the next result, and Shift+Enter the one before.</summary>
    private void SearchField(float scale)
    {
        ImGui.SetNextItemWidth(SearchWidth * scale);
        if (focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        var text = Graph.SearchText;
        var enter = ImGui.InputTextWithHint("##graph-search", "Search triggers and values", ref text, 128,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.EscapeClearsAll);
        ImGui.SetItemTooltip("Search the nicknames and the values of the triggers. Enter frames the next result, and " +
                             "Shift+Enter the one before. Shortcut: Ctrl+F.");
        Graph.SearchText = text;
        if (enter)
        {
            Graph.NextResult(ImGui.GetIO().KeyShift ? -1 : 1);
            focusSearch = true;
        }
    }

    /// <summary>The menu "Systems": the systems of the mission. A system fades the triggers of the other
    /// systems.</summary>
    private void SystemsMenu()
    {
        var on = Graph.SystemFilter != null;
        if (ToolbarItems.Toggle("systems", EditorIcons.Filter, "Systems: fade the triggers of other systems.", on) != on)
        {
            ImGui.OpenPopup("##systems");
        }

        if (!ImGui.BeginPopup("##systems"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        if (ImGui.MenuItem("All systems", "", Graph.SystemFilter == null))
        {
            Graph.SystemFilter = null;
        }

        foreach (var (system, triggers) in missions.Shown is { } open ? MissionSummary.TriggerSystems(open.Index) : [])
        {
            if (ImGui.MenuItem(SystemItem(window.Session, system, triggers), "", string.Equals(Graph.SystemFilter, system,
                    StringComparison.OrdinalIgnoreCase)))
            {
                Graph.SystemFilter = system;
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The text of a system of a mission in a menu: "{system name} ({nickname}): {n} triggers" (section 13.1
    /// of the M6a design). The Systems menu of the graph and the menu "Mission systems" of the Scene use it.</summary>
    internal static string SystemItem(EditorSession? session, string nickname, int triggers)
    {
        var label = session?.SystemName(nickname) is { Length: > 0 } name ? $"{name} ({nickname})" : nickname;
        var count = triggers == 1 ? "1 trigger" : $"{triggers.ToString(System.Globalization.CultureInfo.InvariantCulture)} triggers";
        return $"{label}: {count}";
    }

    private static string Label(MissionRunResult? result, string path) =>
        result?.Scripts.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)) is
            { Slot: { } slot }
            ? $"{slot.Slot}: {MissionFiles.Display(path)}"
            : MissionFiles.Display(path);

    public void Draw(double elapsed)
    {
        if (window.Session?.GameData == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (missions.Result == null)
        {
            PanelStates.Loading("Checking the mission scripts");
            return;
        }

        if (missions.OpenPrefab != null)
        {
            PrefabPart();
            return;
        }

        if (missions.OpenPath is not { } path || missions.Analysis(path) is not { } analysis)
        {
            PanelStates.Message("No mission is open.", "Double-click a mission in the Missions tab.");
            return;
        }

        missions.SyncDryRun();

        var scale = ImGuiHelper.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var settings = window.Settings;
        // The Scene part closes in a narrow tab. The toolbar keeps its toggle.
        var withScene = settings.MissionShowScene && avail.X >= MinSceneTabWidth * scale;
        var gap = MathF.Truncate(SplitterWidth * scale);
        var partWidth = withScene
            ? MathF.Truncate(Math.Clamp(settings.MissionSplit, 0.1f, 0.9f) * (avail.X - gap))
            : avail.X;
        links.Update(analysis);

        if (ImGui.BeginChild("##mission-part", new Vector2(partWidth, avail.Y), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (missions.ShowRaw)
            {
                RawPart(path);
            }
            else
            {
                // The switch to the graph ends an open edit of the text.
                raw.EndEdit();
                GraphPart(analysis, avail.X / scale);
            }
        }

        ImGui.EndChild();
        if (!withScene)
        {
            // A frame in the Scene needs the Scene part, so a request ends while the part is closed.
            missions.PendingSceneFrame = null;
            return;
        }

        var split = settings.MissionSplit * Math.Max(1, avail.X - gap) / scale;
        var max = Math.Max(MinPart, (avail.X - gap) / scale - MinPart);
        if (Splitter.Draw("##split", origin + new Vector2(partWidth, 0), new Vector2(gap, avail.Y), true, ref split, 1,
                MinPart, max, DefaultSplit * (avail.X - gap) / scale))
        {
            settings.Save();
        }

        settings.MissionSplit = Math.Clamp(split * scale / Math.Max(1, avail.X - gap), 0.1f, 0.9f);
        ImGui.SetCursorScreenPos(origin + new Vector2(partWidth + gap, 0));
        if (ImGui.BeginChild("##scene", new Vector2(Math.Max(1, avail.X - partWidth - gap), avail.Y)))
        {
            ScenePart(analysis, elapsed);
        }

        ImGui.EndChild();
        // The connector lines draw on a child window after both parts. So they go over both parts and under the
        // popups. The child window takes no mouse input.
        if (!missions.ShowRaw)
        {
            ImGui.SetCursorScreenPos(origin);
            if (ImGui.BeginChild("##connector-lines", avail, ImGuiChildFlags.None,
                    ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings))
            {
                links.DrawLines((origin, origin + avail));
            }

            ImGui.EndChild();
        }
    }

    /// <summary>
    /// Prefab mode (section 8.6 of the M6b design): the bar, then the graph of the template sections with the library
    /// strip. The Scene part closes, because a template has no world (UI rule 4). A prefab that no mod has now, or whose
    /// file has a problem, leaves prefab mode.
    /// </summary>
    private void PrefabPart()
    {
        if (missions.PrefabMode is not { Analysis: { } analysis } model)
        {
            missions.LeavePrefabMode();
            return;
        }

        // A request of the Scene ends, because prefab mode has no Scene part.
        missions.PendingSceneFrame = null;
        PrefabBar(model);
        if (ImGui.BeginChild("##prefab-part", ImGui.GetContentRegionAvail(), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            GraphPart(analysis, ImGui.GetContentRegionAvail().X / ImGuiHelper.Scale);
        }

        ImGui.EndChild();
    }

    /// <summary>The graph part: the library strip, the graph of the mission, and the keys of the graph while it has the
    /// focus. The strip closes in a Mission tab that is narrower than <see cref="LibraryStrip.MinTabWidth"/>, and the
    /// toggle opens it again (section 8.3 of the M6b design).</summary>
    /// <param name="tabWidth">The width of the Mission tab, in points.</param>
    private void GraphPart(MissionAnalysis analysis, float tabWidth)
    {
        if (missions.PendingGraphFrame)
        {
            missions.PendingGraphFrame = false;
            missions.PendingLine = null;
            Graph.FrameSelection();
        }

        // The uses bar names the items of the open mission, so it does not show in prefab mode.
        if (missions.OpenPrefab == null)
        {
            links.UsesBar();
            LiveBanners(analysis);
        }

        var path = analysis.Script.Path;
        narrowTab = tabWidth < LibraryStrip.MinTabWidth;
        if (!narrowTab)
        {
            // A tab that gets narrow again closes the strip again.
            libraryInNarrowTab = false;
        }

        if (LibraryStrip.Shows(window.Settings.MissionShowLibrary, narrowTab, libraryInNarrowTab))
        {
            var avail = ImGui.GetContentRegionAvail();
            var width = MathF.Truncate(LibraryStrip.Width * ImGuiHelper.Scale);
            if (library.Draw(new Vector2(width, avail.Y), missions.NoEditReason(path), prefabs: missions.OpenPrefab == null) is { } item)
            {
                Graph.AddFromLibrary(path, item);
            }

            ImGui.SameLine(0, 0);
        }

        // The Run panel takes the right of the graph part while the dry run is on (section 4.1 of the M6c design), and
        // while the graph shows the live view of a mission play (section 6.3).
        var region = ImGui.GetContentRegionAvail();
        var panel = MathF.Min(MathF.Truncate(RunPanel.Width * ImGuiHelper.Scale), MathF.Max(1, region.X / 2));
        if (missions.OpenPrefab == null && missions.ShownLive(path) != null)
        {
            Graph.Draw(analysis, new Vector2(MathF.Max(1, region.X - panel), region.Y));
            ImGui.SameLine(0, 0);
            runPanel.DrawLive(analysis, new Vector2(panel, region.Y));
        }
        else if (missions.OpenPrefab == null && missions.DryRun is { } run)
        {
            Graph.Draw(analysis, new Vector2(MathF.Max(1, region.X - panel), region.Y));
            ImGui.SameLine(0, 0);
            runPanel.Draw(analysis, run, new Vector2(panel, region.Y));
        }
        else
        {
            Graph.Draw(analysis, region);
        }

        Keys(path);
    }

    /// <summary>The keys of the graph (decision D558). F frames the selection, and A or Home frame all. Ctrl+F goes to
    /// the search, and Esc ends the path view and the neighbor view. The node editor shortcuts stay off, and the edit
    /// keys of the graph come first: Delete, F2, Space, Ctrl+C, Ctrl+V and Ctrl+D (section 8.2 of the M6b design). The
    /// keys work only while the graph part or one of its child windows has the focus. So a key of the Scene part, for
    /// example A to fly left, does not move the graph.</summary>
    private void Keys(string path)
    {
        var io = ImGui.GetIO();
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) || io.WantTextInput)
        {
            return;
        }

        Graph.EditKeys(path);

        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false))
        {
            focusSearch = true;
            return;
        }

        if (io.KeyCtrl || io.KeyAlt)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            // Esc ends the path view or the neighbor view first, then it leaves prefab mode (section 8.6 of the M6b
            // design).
            if (Graph.HasFocus)
            {
                Graph.EndFocus();
            }
            else if (missions.OpenPrefab != null && !Graph.IsEditingText)
            {
                missions.LeavePrefabMode();
                return;
            }
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F3, false))
        {
            links.Step(io.KeyShift ? -1 : 1);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F, false))
        {
            Graph.FrameSelection();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.A, false) || ImGui.IsKeyPressed(ImGuiKey.Home, false))
        {
            Graph.FrameAll();
        }
    }

    /// <summary>The raw part: the whole script as text, with the colors and the marks of the checks.</summary>
    private void RawPart(string path)
    {
        if (missions.RawTextOf(path) is not { } text)
        {
            if (missions.RawTextError(path) is { } error)
            {
                PanelStates.Message("The text of this mission script cannot show.", error);
            }
            else
            {
                PanelStates.Loading("Reading the mission script");
            }

            return;
        }

        if (text.CanonicalReason is { } reason)
        {
            // The file text was not possible, so the user learns why the comments of the file do not show.
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(CanonicalNote(reason));
            ImGui.PopTextWrapPos();
        }

        missions.PendingGraphFrame = false;
        Follow(path, text);
        raw.DrawEditable("##raw", path, text, missions.MarksOf(path), ImGui.GetContentRegionAvail(), missions.NoEditReason(path),
            (start, end) => missions.CommitRaw(path, null, start, end), missions.Draft(path, null));
        // While the user types, the lines of the text do not match the sections of the analysis.
        if (!raw.IsEditing)
        {
            FollowCursor(path, text);
        }
    }

    /// <summary>True while the user edits the raw text of the script.</summary>
    public bool IsEditing => raw.IsEditing;

    /// <summary>Ends an open edit of the raw text, for example before a save.</summary>
    public void EndEdit() => raw.EndEdit();

    /// <summary>The Scene part: the Scene toolbar strip, the banner of an item in another system, and the Scene view.
    /// After the view, the Scene frames the world items of a pending request.</summary>
    private void ScenePart(MissionAnalysis analysis, double elapsed)
    {
        var width = ImGui.GetContentRegionAvail().X;
        EditorLayout.Strip("##scene-toolbar", window.Scene.Toolbar, new Vector2(width, EditorLayout.StripHeight));
        links.Banner(analysis);
        window.Scene.Draw(elapsed);
        links.FramePending(analysis);
    }

    /// <summary>The note of a raw view that shows the canonical text in place of the file text.</summary>
    /// <param name="reason">Why the file text was not possible, as a part of a sentence.</param>
    internal static string CanonicalNote(string reason) =>
        $"This view shows the text that the editor writes, without the comments of the file, because {reason}.";

    /// <summary>Scrolls the text to the line of a new selection or of a request of the Console.</summary>
    private void Follow(string path, RawText text)
    {
        if (!string.Equals(shownPath, path, StringComparison.OrdinalIgnoreCase))
        {
            shownPath = path;
            lastCursor = -1;
        }

        if (missions.PendingLine is var (target, entry) &&
            string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            missions.PendingLine = null;
            lastSelection = window.Selection;
            if (text.LineOf(target.Id, entry) is { } line)
            {
                raw.GoToLine(line);
                lastCursor = line;
            }

            return;
        }

        if (!Equals(window.Selection, lastSelection))
        {
            lastSelection = window.Selection;
            if (window.Selection is MissionRef { Section: { } section } item &&
                string.Equals(item.Script, path, StringComparison.OrdinalIgnoreCase) &&
                text.LineOf(section.Id, item.Row) is { } selectedLine && selectedLine != lastCursor)
            {
                raw.GoToLine(selectedLine);
                lastCursor = selectedLine;
            }
        }
    }

    /// <summary>A cursor in a section selects its item: the trigger, or the line of a trigger.</summary>
    private void FollowCursor(string path, RawText text)
    {
        var cursor = raw.CursorLine;
        if (cursor < 0 || cursor == lastCursor)
        {
            return;
        }

        lastCursor = cursor;
        var (section, entry) = text.At(cursor);
        if (section < 0 || missions.Analysis(path) is not { } analysis || section >= analysis.Document.Sections.Count)
        {
            return;
        }

        var item = MissionRef.OfSection(analysis.Document.Sections[section].Ref, entry);
        window.Selection = item;
        lastSelection = item;
    }

    /// <summary>Forgets the graph and its state, for example when the profile closes.</summary>
    public void Reset()
    {
        Graph.Reset();
        State.Reset();
        links.Reset();
    }

    public void Dispose()
    {
        raw.Dispose();
        Graph.Dispose();
    }
}
