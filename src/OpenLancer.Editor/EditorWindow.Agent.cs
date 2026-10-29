using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor;

/// <summary>
/// The agent server of the editor (milestone MCP1): the seam of the editor host of the agent tools. The members of the
/// seam are explicit, so the public surface of the window does not grow.
/// </summary>
internal sealed partial class EditorWindow : IAgentEditor, IFollowView
{
    private AgentController agentController = null!;
    private AgentMenu agentMenu = null!;
    private AgentRender agentRender = null!;
    private EditorAgentViews agentViews = null!;
    private EditorAgentPlay agentPlay = null!;

    /// <summary>The pulse rings and the robot glyphs of the agent edits (section 8.5 of the MCP1 design).</summary>
    internal AgentMarks AgentMarks { get; } = new(() => System.DateTime.UtcNow);

    /// <summary>Follow mode of the agent edits (decision D719).</summary>
    internal AgentFollow AgentFollow { get; private set; } = null!;

    /// <summary>The agent server, its menu and its render service. The window makes them before its first
    /// frame.</summary>
    private void MakeAgent()
    {
        agentRender = new AgentRender(this);
        agentViews = new EditorAgentViews(this);
        agentPlay = new EditorAgentPlay(this);
        AgentFollow = new AgentFollow(Settings.Agent, this);
        agentController = new AgentController(this, this, Activity,
            AgentDiscovery.PathOf(AgentDiscovery.DefaultFolder, System.Environment.ProcessId),
            AppHandler.LogFolder(Product.Name));
        agentMenu = new AgentMenu(this, agentController);
    }

    /// <summary>Starts the agent server at the start of the editor when the setting asks for it and the user saw the
    /// data notice (decisions D704 and D735).</summary>
    private void StartAgentWithEditor()
    {
        if (Settings.Agent is { StartWithEditor: true, NoticeShown: true })
        {
            agentController.Start();
        }
    }

    /// <summary>The text of the agent segment of the status bar, for example "Agent server: on, port 47815".</summary>
    public string AgentStatus => agentController.StatusText;

    /// <summary>True while the agent server runs.</summary>
    public bool AgentServerOn => agentController.State == AgentServerState.On;

    /// <summary>The offscreen render service of the agent server (milestone MCP1-1b).</summary>
    internal AgentRender AgentRender => agentRender;

    /// <summary>The place of a panel on the screen in this frame, in pixels, or null when the panel does not show (see
    /// <see cref="EditorLayout.PlaceOf"/>).</summary>
    public (Vector2 Min, Vector2 Size)? PanelPlace(string panelId) => layout.PlaceOf(panelId, ImGui.GetFrameCount());

    /// <summary>True when the panel shows in its group in the last frame. The Welcome screen shows no panel.</summary>
    internal bool IsPanelShown(string panelId) => Session != null && layout.IsShown(panelId);

    /// <summary>Opens the menu "Agent" as a popup, for a click on the status bar.</summary>
    public void OpenAgentMenu() => agentMenu.OpenPopup();

    /// <summary>Shows the Console tab with a search, for example the source of the agent messages.</summary>
    public void ShowConsoleSearch(string text)
    {
        if (Session == null)
        {
            return;
        }

        console.Search(text);
        layout.Show(ConsolePanel.PanelId);
    }

    /// <summary>What the editor does now: an agent call waits while the user edits, a dialog is open, the data loads
    /// or an edit of the user runs (section 5.5 of the MCP1 design).</summary>
    private EditorActivity Activity() => new(IsEditing || AssetDrag.IsDragging, DialogOpen, Session?.IsBusy == true,
        Session?.Edits is { } edits && (edits.OpenBatch != null || edits.InTransaction));

    EditSession? IAgentEditor.Edits => Session?.Edits;

    string? IAgentEditor.EditReason => Session?.Edits.ReadOnlyReason;

    string? IAgentEditor.RepoReason => Session?.PendingRepoReason;

    string IAgentEditor.Name(int ids) => Session?.Name(ids) ?? "";

    string IAgentEditor.InfoText(int ids) => Session?.InfoText(ids) ?? "";

    AgentSettings IAgentEditor.Settings => Settings.Agent;

    ConsoleLog IAgentEditor.Log => Log;

    IAgentViews IAgentEditor.Views => agentViews;

    IAgentPlay IAgentEditor.Play => agentPlay;

    SaveOutcome IAgentEditor.Save() => Edits.Save();

    string? IAgentEditor.ExportBlock => ExportBlock;

    bool IAgentEditor.Paused => agentController.Paused;

    AgentFollow IAgentEditor.Follow => AgentFollow;

    /// <summary>Follow mode of the agent edits: opens the system of a section in the Scene, shows the Scene, frames the
    /// item and selects it on request.</summary>
    bool IFollowView.ShowInScene(SectionRef section, string system, bool select)
    {
        if (Session?.GameData?.Items.Systems.Get(system) is not { } data)
        {
            return false;
        }

        if (!IsSceneSystem(system))
        {
            OpenSystem(data);
            if (!IsSceneSystem(system))
            {
                return false;
            }
        }

        // The agent call runs before the panels draw, so the Scene can apply the edit now.
        Scene.Flush();
        if (Scene.ItemOf(section) is not { } item || item is LibreLancer.Data.GameData.World.StarSystem)
        {
            return false;
        }

        ShowPanel(SceneView.PanelId);
        if (select)
        {
            SelectInTab(CenterMode.Scene, item);
        }

        Scene.Focus(item);
        return true;
    }

    private bool IsSceneSystem(string system) =>
        Scene.System?.Nickname.Equals(system, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Watches the input of the user in one frame for follow mode (decision D719): a click or a key that moves the
    /// selection, and a camera input of the Scene. Call it after the panels draw, with the selection and the camera pose
    /// before them.
    /// </summary>
    private void WatchFollow(object? selectionBefore, (Vector3, Quaternion, float) poseBefore)
    {
        var input = false;
        foreach (var button in new[] { ImGuiMouseButton.Left, ImGuiMouseButton.Right, ImGuiMouseButton.Middle })
        {
            input |= ImGui.IsMouseClicked(button) || ImGui.IsMouseReleased(button);
        }

        foreach (var key in SelectionKeys)
        {
            input |= ImGui.IsKeyPressed(key);
        }

        AgentFollow.Watch(new FollowFrame(selectionBefore, Selection, poseBefore, Scene.ViewPose, input));
    }

    /// <summary>The keys that move the selection of a list or a tree.</summary>
    private static readonly ImGuiKey[] SelectionKeys =
    [
        ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.LeftArrow, ImGuiKey.RightArrow, ImGuiKey.Home, ImGuiKey.End,
        ImGuiKey.PageUp, ImGuiKey.PageDown,
    ];

    /// <summary>True while the user pauses the agent edits (decision D734).</summary>
    public bool AgentPaused => agentController.Paused;

    /// <summary>True while an export of the agent prepares or writes. The Export dialog and the Mods window wait for
    /// it.</summary>
    internal bool AgentExportRuns => agentController.Exports.Running;

    AgentGames IAgentEditor.Games(OpenLancer.Data.Mods.ModManifest manifest) =>
        new(LinkedGameReports(), Mods.GameRoots(), Mods.PlayTargets(manifest));

    ValidationState? IAgentEditor.Validation => Edits.Validation;

    event System.Action<ValidationState>? IAgentEditor.Validated
    {
        add => Edits.Validated += value;
        remove => Edits.Validated -= value;
    }

    AgentView IAgentEditor.View => new(
        Scene.System is { } system ? ItemRefs.Format(new ItemReference(ItemRefKind.System, system.Nickname)) : null,
        Missions.OpenPath is { } script ? ItemRefs.Format(new ItemReference(ItemRefKind.Script, "") { Path = script }) : null,
        Cutscenes.Path is { } cutscene ? ItemRefs.Format(new ItemReference(ItemRefKind.Cutscene, "") { Path = cutscene }) : null);
}
