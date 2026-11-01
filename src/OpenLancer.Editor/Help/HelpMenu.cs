using System;
using System.Runtime.InteropServices;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Help;

/// <summary>
/// The Help menu, the last menu of the menu bar as in Unity. Its items are Documentation, Getting started, Keys,
/// Discord, Report a problem, Open log folder and About OpenLancer. <see cref="HelpLinks"/> gives the target of each
/// item, and <see cref="ReportHeader"/> gives the text that Report a problem copies.
/// </summary>
internal sealed class HelpMenu(EditorWindow window)
{
    /// <summary>The Console line after Report a problem copied the header.</summary>
    internal const string ReportCopied =
        "The editor copied the version, the commit, the game and the Windows version. Paste the text into the first " +
        "field of the bug report form in the browser.";

    private readonly AboutDialog about = new();
    private readonly KeysWindow keys = new();

    /// <summary>True while About OpenLancer shows.</summary>
    public bool DialogOpen => about.IsOpen;

    /// <summary>The items of the Help menu.</summary>
    public void Menu()
    {
        if (Theme.IconMenuItem(EditorIcons.Book, "Documentation", true))
        {
            FolderActions.OpenWebLink(window, HelpLinks.Documentation(BuildInfo.Current));
        }

        ImGui.SetItemTooltip("Opens the help pages of this version of OpenLancer in the browser.");
        var readme = HelpLinks.GettingStarted(AppContext.BaseDirectory);
        if (Theme.IconMenuItem(EditorIcons.QuestionCircle, "Getting started", readme.Reason == null))
        {
            FolderActions.OpenTextFile(window, readme.Path);
        }

        ImGui.SetItemTooltip(readme.Reason ?? "Opens README-FIRST.txt: the first steps, the system requirements and " +
            "what this version can do.");
        if (Theme.IconMenuItem(EditorIcons.Keyboard, "Keys...", true))
        {
            keys.Open();
        }

        ImGui.SetItemTooltip("Shows the keys of the editor, the Scene and the cutscene preview.");
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.Comments, "Discord", true))
        {
            FolderActions.OpenWebLink(window, HelpLinks.Discord);
        }

        ImGui.SetItemTooltip("Opens the invite of the OpenLancer Discord server, where you can ask for help.");
        if (Theme.IconMenuItem(EditorIcons.Bug, "Report a problem", true))
        {
            ReportProblem();
        }

        ImGui.SetItemTooltip("Copies the version, the commit, the game and the Windows version. Then opens the bug " +
                             "report form in the browser.");
        if (Theme.IconMenuItem(EditorIcons.FolderOpen, "Open log folder", true))
        {
            FolderActions.Open(window, HelpLinks.LogFolder);
        }

        ImGui.SetItemTooltip("Opens the folder of the log files of the editor and the game. After a crash and a " +
                             "restart, the file that ends in .log.previous.txt has the log of the crash.");
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.InfoCircle, "About OpenLancer...", true))
        {
            about.Show();
        }

        ImGui.SetItemTooltip("Shows the version, the commits and the license of OpenLancer.");
    }

    /// <summary>Draws the windows of the menu.</summary>
    public void Draw()
    {
        about.Draw();
        keys.Draw();
    }

    /// <summary>Copies the header for the bug report to the clipboard, tells the user in the Console, and opens the
    /// form.</summary>
    private void ReportProblem()
    {
        var game = window.Session is { } session ? window.Links.ReportOf(session.Profile.BaseFolder) : null;
        ImGui.SetClipboardText(ReportHeader.Text(BuildInfo.Current, game, RuntimeInformation.OSDescription));
        window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource, ReportCopied);
        FolderActions.OpenWebLink(window, HelpLinks.ReportForm);
    }
}
