using System.Numerics;
using ImGuiNET;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor;

/// <summary>
/// The agent server of the editor (milestone MCP1): the seam of the editor host of the agent tools. The members of the
/// seam are explicit, so the public surface of the window does not grow.
/// </summary>
internal sealed partial class EditorWindow : IAgentEditor
{
    private AgentController agentController = null!;
    private AgentMenu agentMenu = null!;
    private AgentRender agentRender = null!;
    private EditorAgentViews agentViews = null!;

    /// <summary>The agent server, its menu and its render service. The window makes them before its first
    /// frame.</summary>
    private void MakeAgent()
    {
        agentRender = new AgentRender(this);
        agentViews = new EditorAgentViews(this);
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

    SaveOutcome IAgentEditor.Save() => Edits.Save();

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
