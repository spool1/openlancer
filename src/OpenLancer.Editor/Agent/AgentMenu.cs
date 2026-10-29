using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>
/// The menu "Agent" of the editor (decision D742) and its dialogs: Start and Stop of the agent server, the data notice
/// at the first start (decision D735), the Connect dialog with the text of <c>.mcp.json</c>, the agent settings and the
/// segment of the status bar (section 10 of the MCP1 design). The menu items of the agent edits, for example Pause, come
/// with the edit tools.
/// </summary>
internal sealed class AgentMenu(EditorWindow window, AgentController controller)
{
    /// <summary>The id of the popup that a click on the status bar segment opens.</summary>
    private const string PopupId = "##agent-menu";

    private readonly ConnectDialog connect = new();

    /// <summary>The typed values of the settings fields that are outside their range, by field label. The setting keeps
    /// its value, and the field shows the typed value with the reason (UI rule 2).</summary>
    private readonly Dictionary<string, int> drafts = [];

    private bool openPopup;

    /// <summary>True while a dialog of the menu is open.</summary>
    public bool DialogOpen => connect.IsOpen;

    /// <summary>The items of the menu "Agent" of the menu bar.</summary>
    public void Menu()
    {
        var settings = window.Settings.Agent;
        var running = controller.State is AgentServerState.On or AgentServerState.Starting;
        if (Theme.IconMenuItem(EditorIcons.Play, "Start agent server", !running))
            Start();
        ImGui.SetItemTooltip("Lets an agent program on this computer read the data of the open profile. The server accepts " +
                             "connections from this computer only.");
        if (Theme.IconMenuItem(EditorIcons.Stop, "Stop agent server", running))
            controller.Stop();
        ImGui.SetItemTooltip("Stops the agent server at once.");
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.Link, "Connect an agent...", true))
            connect.Show(running ? controller.Port : settings.Port);
        ImGui.SetItemTooltip("Shows the text for the file .mcp.json of an agent project.");
        if (Theme.IconMenuItem(EditorIcons.Terminal, "Show agent messages", true))
            window.ShowConsoleSearch(AgentController.Source);
        ImGui.SetItemTooltip("Shows the messages of the agent server in the Console.");
        var startWithEditor = settings.StartWithEditor;
        // ImGui draws the check mark of the setting at the right, so the icon must not look like a check mark.
        if (ImGui.MenuItem($"{EditorIcons.PlayCircle}   Start the agent server with the editor", "", ref startWithEditor))
        {
            settings.StartWithEditor = startWithEditor;
            window.Settings.Save();
        }
        ImGui.SetItemTooltip("Starts the agent server when the editor starts.");
        ImGui.Separator();
        if (Theme.BeginIconMenu(EditorIcons.Cog, "Agent settings"))
        {
            Settings(running);
            ImGui.EndMenu();
        }
    }

    /// <summary>The segment text of the status bar.</summary>
    public string StatusText => controller.StatusText;

    /// <summary>Opens the menu as a popup at the mouse, for a click on the status bar segment.</summary>
    public void OpenPopup() => openPopup = true;

    /// <summary>Draws the popup of the status bar and the dialogs. Call it each frame.</summary>
    public void Draw()
    {
        if (openPopup)
        {
            ImGui.OpenPopup(PopupId);
            openPopup = false;
        }
        if (ImGui.BeginPopup(PopupId))
        {
            Menu();
            ImGui.EndPopup();
        }
        connect.Draw();
    }

    /// <summary>Starts the server. At the first start, the data notice asks first.</summary>
    public void Start()
    {
        var settings = window.Settings.Agent;
        if (settings.NoticeShown)
        {
            controller.Start();
            return;
        }
        window.Choice.Show("Start the agent server",
            "The agent reads the game data and the mod data of this profile. It also reads the Console messages and " +
            "images of the editor panels. The agent program can send this data to its model service. The server accepts " +
            "connections from this computer only.",
            [
                new Choice("Start server", () =>
                {
                    settings.NoticeShown = true;
                    window.Settings.Save();
                    controller.Start();
                }),
                new Choice("Cancel", () => { }),
            ]);
    }

    /// <summary>The agent settings: the port, the setting of agent Play (user decision of 2026-10-20 of the M6c design),
    /// the tool groups and the limits (section 10 of the MCP1 design).</summary>
    private void Settings(bool running)
    {
        var settings = window.Settings.Agent;
        ImGui.BeginDisabled(running);
        if (RangeField("Agent server port", running
                ? "Stop the agent server to change its port."
                : "The port of the agent server on this computer. Use the same port in .mcp.json.",
                settings.Port, (1, 65535), v => settings.Port = v))
        {
            window.Settings.Save();
        }
        ImGui.EndDisabled();

        // The setting has an effect only when an agent tool can start Play (UI rule 4).
        if (AgentCanPlay)
        {
            var autoSave = settings.AutoSavePlay;
            if (ImGui.Checkbox(AutoSavePlayLabel, ref autoSave))
            {
                settings.AutoSavePlay = autoSave;
                window.Settings.Save();
            }
            ImGui.SetItemTooltip(AutoSavePlayTip);
        }

        ImGui.SeparatorText("Agent tool groups");
        ImGui.SetItemTooltip("The agent can use the tools of the selected groups. A tool of a cleared group tells the agent " +
                             "that you cleared the group.");
        var hidden = settings.HiddenToolGroups;
        foreach (var group in AgentTools.All.Select(t => t.Group).Distinct().Where(g => g != ToolGroup.Studio))
        {
            var shown = !hidden.Contains(group);
            if (ImGui.Checkbox(group.ToString(), ref shown))
            {
                settings.SetHidden(group, !shown);
                window.Settings.Save();
            }
            ImGui.SetItemTooltip(GroupTip(group));
        }

        if (ImGui.CollapsingHeader("Advanced"))
            Limits(settings);
    }

    /// <summary>The agent tools that start Play: <c>play</c> of MCP1-2 and <c>mission_play</c> of MCP1-3.</summary>
    internal static readonly IReadOnlyList<string> PlayTools = ["play", "mission_play"];

    /// <summary>True when the tool list of this build has a tool that starts Play. The setting of agent Play shows only
    /// then, because it has no effect without such a tool.</summary>
    internal static bool AgentCanPlay => PlayTools.Any(name => AgentTools.Find(name) != null);

    /// <summary>The label of the setting of the agent play rule (user decision of 2026-10-20 of the M6c design).</summary>
    internal const string AutoSavePlayLabel = "Auto-save and continue when an MCP agent starts Play";

    internal const string AutoSavePlayTip =
        "When an agent starts Play and the active mod has unsaved edits, the editor saves them and plays. Off: the editor " +
        "asks you first, and Cancel gives the agent an error. Your own F5 and Ctrl+F5 always save first.";

    /// <summary>The tooltip of the check box of a tool group.</summary>
    internal static string GroupTip(ToolGroup group) => group switch
    {
        ToolGroup.Scene => "The tools of the star systems, their items, the assets and the prop groups.",
        ToolGroup.Missions => "The tools of the mission scripts.",
        ToolGroup.Cutscenes => "The tools of the cutscene scripts.",
        ToolGroup.Play => "The tools that start and stop a play in OpenLancer.",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    /// <summary>The fields of the limits of one agent call (decision D724).</summary>
    private void Limits(AgentSettings settings)
    {
        ImGui.TextDisabled("Agent limits");
        ImGui.SetItemTooltip("The largest work that one agent call can do.");
        var limits = settings.Limits;
        var changed = false;
        changed |= RangeField("Rows of a page without a limit",
            "The rows of a list result when the agent gives no limit. A larger list gives more pages.", limits.ListRows,
            (1, limits.MaxListRows), v => limits = limits with { ListRows = v });
        changed |= RangeField("Largest page limit",
            "The largest limit that the agent can ask for in one list result. It is also the most items that one image " +
            "frames and the most rows of the visible items of an image.",
            limits.MaxListRows, AgentLimits.MaxListRowsRange, v => limits = limits with { MaxListRows = v });
        changed |= RangeField("Size of a result (KB)",
            "The most text of one result. A larger result asks the agent for pages.", limits.ResultKilobytes,
            AgentLimits.ResultKilobytesRange, v => limits = limits with { ResultKilobytes = v });
        changed |= RangeField("Calls each second", "The most calls that the server takes in one second.",
            limits.CallsPerSecond, AgentLimits.CallsPerSecondRange, v => limits = limits with { CallsPerSecond = v });
        changed |= RangeField("Wait for the editor (s)",
            "How long a call waits while you edit a field or a dialog is open.", limits.WaitSeconds,
            AgentLimits.WaitSecondsRange, v => limits = limits with { WaitSeconds = v });
        changed |= RangeField("Time of a check or an image (s)",
            "How long a check of a retail target or an image can take. A longer call gives the agent an error.",
            limits.TailSeconds, AgentLimits.TailSecondsRange, v => limits = limits with { TailSeconds = v });
        changed |= RangeField("Size of a request (MB)",
            "The largest request. The change applies at the next start of the server.", limits.RequestMegabytes,
            AgentLimits.RequestMegabytesRange, v => limits = limits with { RequestMegabytes = v });
        changed |= RangeField("Largest side of an image (pixels)",
            "The most pixels on each side of an image of a system or of a cutscene. A larger panel capture becomes " +
            "smaller. A large image takes more time in a frame of the editor.", limits.RenderPixels,
            AgentLimits.RenderPixelsRange, v => limits = limits with { RenderPixels = v });
        changed |= RangeField("Items of one edit",
            "The most items that one agent call deletes or copies. A larger change is not one gesture.", limits.EditItems,
            AgentLimits.EditItemsRange, v => limits = limits with { EditItems = v });
        changed |= RangeField("Sections of one edit",
            "The most sections that one agent edit changes, adds or removes. A larger edit changes nothing and gives the " +
            "agent an error.", limits.EditSections, AgentLimits.EditSectionsRange, v => limits = limits with { EditSections = v });
        changed |= RangeField("Steps of one batch",
            "The most steps of one agent batch. The editor runs a batch in one frame.", limits.BatchSteps,
            AgentLimits.BatchStepsRange, v => limits = limits with { BatchSteps = v });
        changed |= RangeField("Legs of one route",
            "The most legs of one patrol or trade route that the agent places. Each leg is one zone.", limits.RouteLegs,
            AgentLimits.RouteLegsRange, v => limits = limits with { RouteLegs = v });
        changed |= RangeField("Time of an export (min)",
            "How long an export of the agent can take. A longer export gives the agent an error. A write of the packages " +
            "that started goes on to its end.", limits.ExportMinutes, AgentLimits.ExportMinutesRange,
            v => limits = limits with { ExportMinutes = v });
        if (changed)
        {
            settings.Limits = limits.Clamped();
            window.Settings.Save();
        }
    }

    /// <summary>
    /// A whole-number field of the agent settings with its range. A typed value in the range changes the setting. A
    /// typed value outside the range stays in the field, and the field shows the reason in red. The setting then keeps
    /// its value (UI rule 2).
    /// </summary>
    /// <returns>True when the setting changed.</returns>
    private bool RangeField(string label, string tooltip, int value, (int Min, int Max) range, Action<int> set)
    {
        var shown = drafts.TryGetValue(label, out var draft) ? draft : value;
        ImGui.SetNextItemWidth(MathF.Truncate(120 * ImGuiHelper.Scale));
        var typed = ImGui.InputInt(label, ref shown, 0);
        var rangeText = $"Use a value from {range.Min} to {range.Max}.";
        ImGui.SetItemTooltip($"{tooltip} {rangeText}");
        var changed = false;
        if (typed)
        {
            if (shown >= range.Min && shown <= range.Max)
            {
                drafts.Remove(label);
                set(shown);
                changed = true;
            }
            else
            {
                drafts[label] = shown;
            }
        }
        if (drafts.ContainsKey(label))
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.ExclamationTriangle}  {rangeText} The setting keeps {value}.");
        return changed;
    }

    /// <summary>The text of <c>.mcp.json</c> for Claude Code: the HTTP server and the token helper.</summary>
    internal static string McpJson(int serverPort, string oltool) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["openlancer"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"http://127.0.0.1:{serverPort}{AgentServer.Endpoint}",
                ["headersHelper"] = $"\"{oltool}\" mcp token",
            },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The Connect dialog: the text of <c>.mcp.json</c> with the real port and the real folder of oltool, and
    /// a Copy button (section 6.3 of the MCP1 design).</summary>
    private sealed class ConnectDialog : ModalDialog
    {
        private string text = "";
        private int port;

        protected override string Title => "Connect an agent";

        public void Show(int serverPort)
        {
            port = serverPort;
            text = McpJson(serverPort, ProgramPaths.Oltool);
            Open();
        }

        protected override void Body()
        {
            ImGui.TextWrapped("Add this text to the file .mcp.json in the folder of your agent project. Then start the agent " +
                              "program in that folder.");
            ImGui.Text($"Server address: http://127.0.0.1:{port}{AgentServer.Endpoint}");
            var size = new Vector2(MathF.Truncate(460 * ImGuiHelper.Scale), ImGui.GetTextLineHeight() * 10);
            ImGui.InputTextMultiline("##mcp-json", ref text, 4096, size, ImGuiInputTextFlags.ReadOnly);
            if (Button("Copy", primary: true))
                ImGui.SetClipboardText(text);
            ImGui.SameLine();
            if (Button("Close"))
                Close();
        }
    }
}
