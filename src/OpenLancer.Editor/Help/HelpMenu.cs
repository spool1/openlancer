using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Help;

/// <summary>What a click on an item of the Help menu does.</summary>
internal enum HelpAction
{
    /// <summary>Opens the link of <see cref="HelpItem.Target"/> in the browser.</summary>
    WebLink,

    /// <summary>Opens the text file of <see cref="HelpItem.Target"/>.</summary>
    TextFile,

    /// <summary>Opens the folder of <see cref="HelpItem.Target"/> in the file manager.</summary>
    Folder,

    /// <summary>Shows the Keys window.</summary>
    Keys,

    /// <summary>Copies the header of <see cref="ReportHeader"/>, tells the user in the Console, and opens the form of
    /// <see cref="HelpItem.Target"/>.</summary>
    Report,

    /// <summary>Shows About OpenLancer.</summary>
    About,
}

/// <summary>One item of the Help menu.</summary>
/// <param name="Icon">The glyph of the item.</param>
/// <param name="Label">The label. It ends with "..." when the item opens a window of the editor.</param>
/// <param name="Tooltip">What the item does.</param>
/// <param name="Action">What a click does.</param>
/// <param name="Target">The link, the file or the folder that a click opens. Null for an item that shows a window.</param>
/// <param name="Unavailable">Why the item is off, or null when it is on.</param>
/// <param name="StartsGroup">True when a separator comes before the item.</param>
internal sealed record HelpItem(char Icon, string Label, string Tooltip, HelpAction Action, string? Target = null,
    string? Unavailable = null, bool StartsGroup = false);

/// <summary>
/// What the items of the Help menu need from the editor. <see cref="HelpMenu"/> implements it. The tests give a fake,
/// so that they can check what each item opens.
/// </summary>
internal interface IHelpMenuHost
{
    /// <summary>The report of the linked game of the open profile, or null.</summary>
    GameReport? Game { get; }

    /// <summary>True when the open profile has a linked game without a report: its check runs or did not
    /// finish.</summary>
    bool GameCheckPending { get; }

    /// <summary>Opens a web link in the browser.</summary>
    void OpenWebLink(string url);

    /// <summary>Opens a text file in the program of the system for text files.</summary>
    void OpenTextFile(string path);

    /// <summary>Opens a folder in the file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Puts a text on the clipboard.</summary>
    void CopyText(string text);

    /// <summary>Writes an Info line to the Console.</summary>
    void Info(string text);

    /// <summary>Shows the Keys window.</summary>
    void ShowKeys();

    /// <summary>Shows About OpenLancer.</summary>
    void ShowAbout();
}

/// <summary>
/// The Help menu, the last menu of the menu bar as in Unity. <see cref="Items"/> gives its items and the target of
/// each item, and <see cref="Run"/> does what an item does. <see cref="HelpLinks"/> gives the targets, and
/// <see cref="ReportHeader"/> gives the text that Report a problem copies.
/// </summary>
internal sealed class HelpMenu(EditorWindow window) : IHelpMenuHost
{
    /// <summary>The Console line after Report a problem copied the header.</summary>
    internal const string ReportCopied =
        "The editor copied the version, the commit, the game and the Windows version. Paste the text into the first " +
        "field of the bug report form in the browser.";

    private readonly AboutDialog about = new();
    private readonly KeysWindow keys = new();

    /// <summary>True while About OpenLancer shows.</summary>
    public bool DialogOpen => about.IsOpen;

    /// <summary>The items of the Help menu, in their order, with the target of each item.</summary>
    /// <param name="build">The version facts of the editor. Documentation opens the help pages of this build.</param>
    /// <param name="editorFolder">The folder of the editor program, for example <c>AppContext.BaseDirectory</c>.</param>
    public static IReadOnlyList<HelpItem> Items(BuildVersion build, string editorFolder)
    {
        var readme = HelpLinks.GettingStarted(editorFolder);
        return
        [
            new HelpItem(EditorIcons.Book, "Documentation",
                "Opens the help pages of this version of OpenLancer in the browser.", HelpAction.WebLink,
                HelpLinks.Documentation(build)),
            new HelpItem(EditorIcons.QuestionCircle, "Getting started",
                "Opens README-FIRST.txt: the first steps, the system requirements and what this version can do.",
                HelpAction.TextFile, readme.Path, readme.Reason),
            new HelpItem(EditorIcons.Keyboard, "Keys...",
                "Shows the keys of the editor, the Scene, the Hierarchy and the cutscene preview.", HelpAction.Keys),
            new HelpItem(EditorIcons.Comments, "Discord",
                "Opens the invite of the OpenLancer Discord server, where you can ask for help.", HelpAction.WebLink,
                HelpLinks.Discord, StartsGroup: true),
            new HelpItem(EditorIcons.Bug, "Report a problem",
                "Copies the version, the commit, the game and the Windows version. Then opens the bug report form in " +
                "the browser.", HelpAction.Report, HelpLinks.ReportForm),
            new HelpItem(EditorIcons.FolderOpen, "Open log folder",
                "Opens the folder of the log files of the editor and the game. When the editor or the game starts, " +
                "it moves the log of its last run to the file that ends in .log.previous.txt.", HelpAction.Folder,
                HelpLinks.LogFolder),
            new HelpItem(EditorIcons.InfoCircle, "About OpenLancer...",
                "Shows the version, the commits and the license of OpenLancer.", HelpAction.About, StartsGroup: true),
        ];
    }

    /// <summary>Does what a click on an item does.</summary>
    /// <param name="item">An item of <see cref="Items"/>.</param>
    /// <param name="host">The editor, or the fake of a test.</param>
    /// <param name="build">The version facts for the header of Report a problem.</param>
    public static void Run(HelpItem item, IHelpMenuHost host, BuildVersion build)
    {
        switch (item.Action)
        {
            case HelpAction.WebLink:
                host.OpenWebLink(item.Target!);
                break;
            case HelpAction.TextFile:
                host.OpenTextFile(item.Target!);
                break;
            case HelpAction.Folder:
                host.OpenFolder(item.Target!);
                break;
            case HelpAction.Keys:
                host.ShowKeys();
                break;
            case HelpAction.Report:
                host.CopyText(ReportHeader.Text(build, host.Game, host.GameCheckPending,
                    RuntimeInformation.OSDescription));
                host.Info(ReportCopied);
                host.OpenWebLink(item.Target!);
                break;
            case HelpAction.About:
                host.ShowAbout();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Action, "The Help menu has no such action.");
        }
    }

    /// <summary>Draws the items of the Help menu.</summary>
    public void Menu()
    {
        var build = BuildInfo.Current;
        foreach (var item in Items(build, AppContext.BaseDirectory))
        {
            if (item.StartsGroup)
            {
                ImGui.Separator();
            }

            if (Theme.IconMenuItem(item.Icon, item.Label, item.Unavailable == null))
            {
                Run(item, this, build);
            }

            ImGui.SetItemTooltip(item.Unavailable ?? item.Tooltip);
        }
    }

    /// <summary>Draws the windows of the menu.</summary>
    public void Draw()
    {
        about.Draw();
        keys.Draw();
    }

    GameReport? IHelpMenuHost.Game => ProfileGame is { } root ? window.Links.ReportOf(root) : null;

    bool IHelpMenuHost.GameCheckPending =>
        ProfileGame is { } root && window.Links.ReportOf(root) == null &&
        (window.Links.IsChecking(root) || window.Links.Find(root) != null);

    void IHelpMenuHost.OpenWebLink(string url) => FolderActions.OpenWebLink(window, url);

    void IHelpMenuHost.OpenTextFile(string path) => FolderActions.OpenTextFile(window, path);

    void IHelpMenuHost.OpenFolder(string path) => FolderActions.Open(window, path);

    void IHelpMenuHost.CopyText(string text) => ImGui.SetClipboardText(text);

    void IHelpMenuHost.Info(string text) => window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource, text);

    void IHelpMenuHost.ShowKeys() => keys.Open();

    void IHelpMenuHost.ShowAbout() => about.Show();

    /// <summary>The game folder of the open profile, or null without a profile.</summary>
    private string? ProfileGame => window.Session?.Profile.BaseFolder;
}
