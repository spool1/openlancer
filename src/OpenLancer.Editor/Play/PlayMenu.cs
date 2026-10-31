using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>A system and its bases, for the Play at base list.</summary>
internal sealed record BaseGroup(StarSystem System, IReadOnlyList<Base> Bases);

/// <summary>
/// The Play controls of the main menu bar (section 11.1 of the M4 design): the Play menu, and three buttons in the
/// middle of the bar, as Unity has them. The buttons are Play, a down arrow that opens the Play menu, and Stop.
/// </summary>
/// <remarks>
/// A Play control only asks <see cref="PlayController"/> for a play. The request runs after the layout of the frame,
/// and it waits for the end of an open field edit or drag (section 9.1). So a click on Play while a field of the Inspector
/// is open ends the field, and the save has its edit.
/// </remarks>
internal sealed class PlayMenu(EditorWindow window)
{
    private const string MenuPopup = "##play-menu";

    // The Play at base list, made when the list opens.
    private IReadOnlyList<BaseGroup> bases = [];

    /// <summary>
    /// The bases by system, in the order of the systems of the game data, with the open system first. A base without a
    /// system of the game data is not in the list, because the play starts in the system of the base.
    /// </summary>
    /// <param name="open">The nickname of the open system, or null.</param>
    internal static IReadOnlyList<BaseGroup> BaseGroups(IEnumerable<StarSystem> systems, IEnumerable<Base> bases,
        string? open)
    {
        var bySystem = bases.Where(b => b.System != null).ToLookup(b => b.System!, StringComparer.OrdinalIgnoreCase);
        var groups = systems.Select(s => new BaseGroup(s, bySystem[s.Nickname].ToList())).Where(g => g.Bases.Count > 0)
            .ToList();
        var first = groups.FindIndex(g => g.System.Nickname.Equals(open, StringComparison.OrdinalIgnoreCase));
        if (first > 0)
        {
            var group = groups[first];
            groups.RemoveAt(first);
            groups.Insert(0, group);
        }

        return groups;
    }

    /// <summary>Plays from the Scene camera: the Play button, F5 and Play from camera. A running game closes first
    /// (section 9.9). The request keeps the camera. The start check of section 9.5 runs when the game starts (see
    /// <see cref="SpawnPoint.FromCamera"/>).</summary>
    public void PlayFromCamera()
    {
        if (CameraBlock() is { } reason)
        {
            Log(ConsoleSeverity.Warning, $"Play from camera is not available: {reason}");
            return;
        }

        Request(CameraStart(window.Scene.System!));
    }

    /// <summary>
    /// F5 and the Play button (decision D744 of the M6c design): in the Mission tab with an open mission, a mission play
    /// from the play start of the mission. Else Play from camera, as milestone M4 defines it.
    /// </summary>
    public void PlayF5()
    {
        if (OpenMission is { } script)
        {
            window.Missions.Plays.PlayFromPlayStart(script);
            return;
        }

        PlayFromCamera();
    }

    /// <summary>The script of the open mission while the Mission tab shows it, else null. F5 then plays the
    /// mission.</summary>
    private string? OpenMission =>
        window.CenterMode == CenterMode.Mission && window.Missions.OpenPrefab == null ? window.Missions.OpenPath : null;

    /// <summary>Stops the game, or ends it at once when Stop is Force stop: the Stop button, Shift+F5 and Stop. The game
    /// of Play in OpenLancer stops first. Else the game of the play copy stops (milestone M11). It is the stop request of
    /// the shared command layer (<see cref="PlayRequests.Stop"/>).</summary>
    public void Stop() => PlayRequests.Stop(window.Play, window.GamePlay);

    /// <summary>True while a game runs that Stop stops.</summary>
    private bool CanStop => window.Play.Status != PlayStatus.Idle || window.GamePlay.GameRuns;

    /// <summary>True when Stop ends the game that it stops at once.</summary>
    private bool ForceStop => window.Play.Status != PlayStatus.Idle ? window.Play.ForceStop : window.GamePlay.ForceStop;

    /// <summary>The items of the Play menu, in the menu bar and in the popup of the arrow button.</summary>
    public void Menu()
    {
        var play = window.Play;
        var block = CameraBlock();
        // F5 runs one of the two items: the mission play in the Mission tab with an open mission, else this one.
        if (ImGui.MenuItem($"{EditorIcons.Play}   Play from camera", OpenMission == null ? "F5" : "", false, block == null))
        {
            PlayFromCamera();
        }

        ImGui.SetItemTooltip(PlayTooltip(block));
        BaseMenu();
        MissionMenu();
        ImGui.Separator();
        ShipItems();
        ImGui.Separator();
        GameItems();
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.SyncAlt, "Reload game", play.CanReload))
        {
            play.Reload();
        }

        ImGui.SetItemTooltip(play.ReloadBlock ??
                             "The running game loads the mod files again and continues at the same place. Use it " +
                             "after a change outside the editor. A save reloads the game itself when its validation " +
                             "passes.");
        if (ImGui.MenuItem($"{EditorIcons.Stop}   {StopLabel(ForceStop)}", "Shift+F5", false, CanStop))
        {
            Stop();
        }

        ImGui.SetItemTooltip(StopTooltip());
    }

    /// <summary>Draws the Play, menu and Stop buttons in the middle of the main menu bar. Call it in the menu bar after
    /// the menus, while the menu colors are pushed, so that the Play menu of the arrow button gets them.</summary>
    public void Buttons()
    {
        var play = window.Play;
        var scale = ImGuiHelper.Scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = 3 * ToolbarItems.IconWidth + 2 * spacing;
        // In the middle of the window, or after the menus when the window is too narrow for that.
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), MathF.Truncate((ImGui.GetWindowWidth() - width) / 2)));
        EditorTheme.PushFlatButtonColors();

        // The Play button shows pressed while a game runs. A click then closes the game and plays again. In the Mission
        // tab, it plays the open mission from its play start, as F5 does.
        var running = play.Status != PlayStatus.Idle;
        var mission = OpenMission;
        var block = mission == null ? CameraBlock() : null;
        var tooltip = mission == null
            ? PlayTooltip(block)
            : $"Play the mission from its play start (F5): {window.Missions.Plays.StartNameOf(mission)}.";
        if (ToolbarItems.Toggle("play", EditorIcons.Play, tooltip, running, block == null) != running)
        {
            PlayF5();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("play-menu", EditorIcons.CaretDown, "Play menu"))
        {
            ImGui.OpenPopup(MenuPopup);
        }

        // The menu opens below the button.
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2 * scale),
            ImGuiCond.Appearing);
        if (ImGui.BeginPopup(MenuPopup))
        {
            Menu();
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("stop", EditorIcons.Stop, StopTooltip(), CanStop))
        {
            Stop();
        }

        EditorTheme.PopFlatButtonColors();
    }

    /// <summary>
    /// The submenu "Play mission" while a mission is open (section 5.2 of the M6c design): the play start, the mission
    /// start, one item for each checkpoint, and "Restart the mission" while a mission play runs. Only the Play button of a
    /// node, "Play from here" and "Set as play start" change the play start. The item of the play start shows F5 while F5
    /// runs it. The mission start and the checkpoints start at their suggested places, because the start place of the play
    /// start is for its own start.
    /// </summary>
    private void MissionMenu()
    {
        var plays = window.Missions.Plays;
        var script = window.Missions.OpenPath;
        var analysis = script == null ? null : window.Missions.Analysis(script);
        if (!ImGui.BeginMenu($"{EditorIcons.Play}   Play mission", analysis != null && window.Session?.GameData != null))
        {
            ImGui.SetItemTooltip(analysis == null ? "Open a mission in the Mission tab." : "Play a mission in OpenLancer.");
            return;
        }

        if (ImGui.MenuItem($"From the play start ({plays.StartNameOf(script!)})", OpenMission == script ? "F5" : ""))
        {
            plays.PlayFromPlayStart(script!);
        }

        ImGui.SetItemTooltip("Play the mission from its play start in OpenLancer.");
        if (ImGui.MenuItem("From the mission start"))
        {
            plays.Request(new MissionPlayRequest(script!, MissionStartPoint.MissionStart));
        }

        ImGui.SetItemTooltip(MissionStartTip);
        foreach (var checkpoint in DryRun.Checkpoints(analysis!.Index))
        {
            if (ImGui.MenuItem($"From checkpoint {RunOverlay.TriggerName(analysis, checkpoint.Nickname)}"))
            {
                plays.Request(new MissionPlayRequest(script!, MissionStartPoint.At(checkpoint.Nickname)));
            }

            ImGui.SetItemTooltip(CheckpointTip);
        }

        var restart = window.Play.RestartBlock;
        if (restart != null)
        {
            // The item shows only while a mission play runs.
            if (window.Play.Mission != null)
            {
                ImGui.MenuItem("Restart the mission", "", false, false);
                ItemTooltip.Show(restart);
            }
        }
        else if (ImGui.MenuItem("Restart the mission"))
        {
            window.Play.RestartMission();
        }

        if (restart == null)
        {
            ImGui.SetItemTooltip(RestartTip);
        }

        ImGui.EndMenu();
    }

    // The tooltips of the submenu "Play mission" (section 8.4 of the M6c design).
    internal const string MissionStartTip = "Play the mission from the start triggers in OpenLancer, at the suggested " +
                                            "start place. The play start does not change.";

    internal const string CheckpointTip = "Play the mission from this checkpoint in OpenLancer, as a load of a saved game " +
                                          "does, at the suggested start place. The play start does not change.";

    internal const string RestartTip = "Start the mission play again from its start, with the saved files. Unsaved " +
                                       "edits do not go to the game.";

    /// <summary>Why Play from camera cannot run now, or null when it can: the checks that the agent tool play shares
    /// (<see cref="PlayChecks.CameraBlock"/>).</summary>
    private string? CameraBlock() =>
        window.PlayCameraBlock(window.Scene.System is { } open ? CameraStart(open) : null);

    /// <summary>The start of Play from camera in the open system.</summary>
    private PlayStart.InSpace CameraStart(StarSystem open) =>
        new(open.Nickname, window.Scene.CameraPosition, window.Scene.CameraOrientation);

    private string PlayTooltip(string? block) =>
        block ?? (window.Play.Status != PlayStatus.Idle
            ? "Close the game and play again at the camera (F5)"
            : "Play from camera (F5): save the active mod, then fly where the Scene camera is");

    private string StopTooltip() => StopTip(ForceStop);

    /// <summary>The label of Stop: "Force stop" while Stop ends the game process at once, else "Stop". The Play menu and
    /// the banner of a mission play use it.</summary>
    internal static string StopLabel(bool force) => force ? "Force stop" : "Stop";

    /// <summary>The tooltip of Stop.</summary>
    internal static string StopTip(bool force) => force
        ? "Force stop (Shift+F5): the game does not respond, so this ends its process"
        : "Stop the game (Shift+F5)";

    /// <summary>
    /// The items of Play in the game (section 8.8 of the M11 design, decision D324). Each play target has an item. Host
    /// test server has an item for each play target with FLServer.exe. Join test server and Stop test server follow.
    /// </summary>
    private void GameItems()
    {
        var games = window.GamePlay;
        var targets = games.Targets;
        var last = games.LastTarget;
        var block = window.Session == null ? "No profile is open."
            : games.Busy ? "A game or the test server of a play copy runs, or Play in the game prepares one."
            : null;
        if (targets.Count == 0)
        {
            ImGui.MenuItem($"{EditorIcons.Rocket}   Play in the game", "Ctrl+F5", false, false);
            ItemTooltip.Show(GamePlay.NoTarget);
        }

        foreach (var target in targets)
        {
            if (ImGui.MenuItem($"{EditorIcons.Rocket}   Play in {target.Name}##{target.Root}",
                    target == last ? "Ctrl+F5" : "", false, block == null))
            {
                games.Play(target);
            }

            ItemTooltip.Show(block ?? $"Save the active mod, install the mods of the profile into the play copy of " +
                                      $"{target.Root}, and start its game. Your game folder does not change.");
        }

        var servers = targets.Where(t => t.HasServer).ToList();
        if (ImGui.BeginMenu($"{EditorIcons.SatelliteDish}   Host test server", servers.Count > 0 && block == null))
        {
            foreach (var target in servers)
            {
                if (ImGui.MenuItem($"{target.Name}##{target.Root}"))
                {
                    games.HostServer(target);
                }

                ImGui.SetItemTooltip($"Install the mods into the play copy of {target.Root}, and start its FLServer.");
            }

            ImGui.EndMenu();
        }
        else
        {
            ItemTooltip.Show(servers.Count == 0
                ? "Link a game folder with its FLServer.exe in File > Game link."
                : block ?? "Start FLServer of a play copy with the mods of the profile. Then join it from the game.");
        }

        if (ImGui.MenuItem($"{EditorIcons.SatelliteDish}   Join test server", "", false, games.JoinBlock == null))
        {
            games.JoinServer();
        }

        ItemTooltip.Show(games.JoinBlock ??
                         "Start the game of the play copy of the test server. Join the server in its multiplayer menu.");
        if (ImGui.MenuItem($"{EditorIcons.Stop}   {(games.ServerForceStop ? "Force stop test server" : "Stop test server")}",
                "", false, games.ServerRuns))
        {
            games.StopServer();
        }

        ItemTooltip.Show(games.ServerForceStop
            ? "FLServer saves its characters, which can take up to 60 seconds. Force stop ends it at once."
            : "Close FLServer. It saves its characters first.");
    }

    /// <summary>The Play at base list: the bases of the open system first, then the other bases by system (section
    /// 9.7).</summary>
    private void BaseMenu()
    {
        var data = window.Session?.GameData;
        if (!ImGui.BeginMenu($"{EditorIcons.Building}   Play at base", data != null))
        {
            if (data == null)
            {
                ImGui.SetItemTooltip("The game data of the profile did not load.");
            }

            return;
        }

        var open = window.Scene.System?.Nickname;
        if (ImGui.IsWindowAppearing())
        {
            bases = BaseGroups(data!.Items.Systems, data.Items.Bases, open);
        }

        for (var i = 0; i < bases.Count; i++)
        {
            var group = bases[i];
            if (i == 0 && group.System.Nickname.Equals(open, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextDisabled(Named(group.System));
                BaseItems(group);
                ImGui.Separator();
            }
            else if (ImGui.BeginMenu($"{Named(group.System)}##{group.System.Nickname}"))
            {
                BaseItems(group);
                ImGui.EndMenu();
            }
        }

        if (bases.Count == 0)
        {
            ImGui.MenuItem("The game data has no bases", false);
        }

        ImGui.EndMenu();
    }

    private void BaseItems(BaseGroup group)
    {
        foreach (var dock in group.Bases)
        {
            var name = window.Session?.ItemName(dock) is { Length: > 0 } text ? text : dock.Nickname;
            if (ImGui.MenuItem($"{name}##{dock.Nickname}", dock.Nickname))
            {
                Request(new PlayStart.AtBase(dock.Nickname));
            }
        }
    }

    /// <summary>The ship line and Use the default ship (section 11.2, decision D15).</summary>
    private void ShipItems()
    {
        var session = window.Session;
        var package = session == null ? null : window.Settings.PlayPackageOf(session.Profile.FilePath);
        var (text, tooltip) = PlayShips.ShipText(session, package);
        ImGui.MenuItem($"{EditorIcons.SpaceShuttle}   Ship: {text}", false);
        ImGui.SetItemTooltip(tooltip);
        if (Theme.IconMenuItem(EditorIcons.Undo, "Use the default ship", package != null))
        {
            window.UseForPlay(null);
        }
    }

    /// <summary>"New York (Li01)", or only the nickname when the system has no name.</summary>
    private string Named(StarSystem system) =>
        window.Session?.ItemName(system) is { Length: > 0 } name ? $"{name} ({system.Nickname})" : system.Nickname;

    /// <summary>Asks for a play after the checks that the agent tool play shares (<see cref="PlayChecks"/>).</summary>
    private void Request(PlayStart start)
    {
        if (window.PlayStartBlock(start) is { } reason)
        {
            Log(ConsoleSeverity.Warning, $"Play is not available: {reason}");
            return;
        }

        window.Play.Request(start, window.PlayPackage);
    }

    private void Log(ConsoleSeverity severity, string message) =>
        window.Log.Add(severity, PlayController.Source, message);
}
