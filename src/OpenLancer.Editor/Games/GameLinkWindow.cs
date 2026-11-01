using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>
/// The Game link window (section 8 of the M11a design, decision D202): a floating window with one card for each linked
/// game. A card shows the game, its state, its folder, its export target, its game program, its profiles and its play
/// copy (milestone M11). It has New profile, Check again, Relink and Remove link. A game with a play copy also has Open
/// play copy, Check play copy, Uninstall mods and Remove play copy. It follows the Installs page of the Unity Hub: the
/// primary button that links a game is at the top right.
/// </summary>
internal sealed class GameLinkWindow(EditorWindow window)
{
    private const string Title = "Game link";
    private static readonly Vector2 DefaultSize = new(680, 560);

    // Sizes in unscaled points.
    private const float CardPadding = 12;
    private const float CardRounding = 4;
    private const float CardGap = 8;
    private const float HeaderGap = 16;
    private const float EmptyGlyphSize = 40;

    /// <summary>A check is older than this when the window opens, so it runs again.</summary>
    private static readonly TimeSpan RecheckAge = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, (Vector2 Min, Vector2 Max)> cards = new(StringComparer.OrdinalIgnoreCase);
    private bool open;
    private bool focus;
    private string? linking;
    private string? linkError;

    /// <summary>True while the window shows.</summary>
    public bool IsOpen => open;

    /// <summary>Shows the window and gives it the focus. The checks that are older than 10 s run again.</summary>
    public void Open()
    {
        if (!open)
        {
            window.Links.CheckOlderThan(RecheckAge);
            window.Links.RefreshUsedBy();
            foreach (var game in window.Links.Games)
            {
                window.GamePlay.RefreshCopy(game.Path);
            }
        }

        open = true;
        focus = true;
    }

    /// <summary>Links the game of a folder, for example of a drop on the window. A folder without a game gets no card,
    /// and the row below the header shows why.</summary>
    public void Link(string folder)
    {
        Open();
        linking = folder;
        linkError = null;
        window.Links.Link(folder, report =>
        {
            linking = null;
            linkError = report.IsGame ? null : report.Error;
            window.Links.RefreshUsedBy();
        });
    }

    /// <summary>The linked game whose card is at the position, or null. A folder that the user drops on a card relinks
    /// that game.</summary>
    public string? CardAt(Vector2 position) => open
        ? cards.FirstOrDefault(c => position.X >= c.Value.Min.X && position.Y >= c.Value.Min.Y &&
                                    position.X <= c.Value.Max.X && position.Y <= c.Value.Max.Y).Key
        : null;

    /// <summary>Draws the window while it is open.</summary>
    public void Draw()
    {
        cards.Clear();
        if (!open)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(DefaultSize * scale, ImGuiCond.FirstUseEver);
        if (focus)
        {
            ImGui.SetNextWindowFocus();
            focus = false;
        }

        if (ImGui.Begin(Title, ref open, ImGuiWindowFlags.NoCollapse))
        {
            Content();
        }

        ImGui.End();
    }

    private void Content()
    {
        Header();
        var games = window.Links.Games;
        if (games.Count == 0)
        {
            Empty();
            return;
        }

        // The game of the open profile shows first.
        var openBase = window.Session?.Profile.BaseFolder;
        var gap = MathF.Truncate(CardGap * ImGuiHelper.Scale);
        foreach (var game in games.OrderBy(g => openBase != null && GameFolders.Same(g.Path, openBase) ? 0 : 1))
        {
            Card(game);
            ImGui.Dummy(new Vector2(0, gap));
        }
    }

    /// <summary>The header: what the window shows and the drop hint on the left, the primary button on the right. A
    /// link that runs or failed shows in a row below it.</summary>
    private void Header()
    {
        var link = new CardButton($"{EditorIcons.Link}  Link a game folder...", Primary: true);
        var textWidth = ImGui.GetContentRegionAvail().X - CardLayout.ButtonWidth(link.Label) -
                        MathF.Truncate(HeaderGap * ImGuiHelper.Scale);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(1, textWidth));
        CardLayout.Dim("OpenLancer reads your game files from these folders. It does not change files in them.");
        CardLayout.Caption("You can also drag a game folder to this window.");
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        if (CardLayout.Buttons(true, link) == 0)
        {
            FolderActions.Choose(window, GameLinks.Source, Link);
        }

        if (linking != null)
        {
            CardLayout.SmallSpace();
            CardLayout.Note(EditorIcons.CircleNotch, EditorTheme.TextDim, $"Checking {linking}...");
        }
        else if (linkError != null)
        {
            CardLayout.SmallSpace();
            CardLayout.Note(EditorIcons.TimesCircle, EditorTheme.Error, linkError);
        }

        CardLayout.Divider();
    }

    /// <summary>The page without a linked game: a large glyph and one line in the middle of the free space.</summary>
    private static void Empty()
    {
        const string text = "There is no linked game. Select your game folder to start.";
        var top = (ImGui.GetContentRegionAvail().Y - MathF.Truncate(EmptyGlyphSize * ImGuiHelper.Scale) -
                   ImGui.GetTextLineHeightWithSpacing()) / 2;
        ImGui.Dummy(new Vector2(0, MathF.Max(0, MathF.Truncate(top))));
        CardLayout.CenteredGlyph(EditorIcons.Link, EditorTheme.TextDim, EmptyGlyphSize);
        CardLayout.Center(ImGui.CalcTextSize(text).X);
        ImGui.TextDisabled(text);
    }

    /// <summary>The card of one linked game. Its rectangle takes a dropped folder for Relink.</summary>
    private void Card(LinkedGame game)
    {
        var scale = ImGuiHelper.Scale;
        ImGui.PushID(game.Path);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Section);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, MathF.Truncate(CardRounding * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(MathF.Truncate(CardPadding * scale)));
        var visible = ImGui.BeginChild("##card", Vector2.Zero,
            ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
        if (visible)
        {
            CardContent(game);
        }

        ImGui.EndChild();
        cards[game.Path] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        ImGui.PopID();
    }

    private void CardContent(LinkedGame game)
    {
        var links = window.Links;
        var report = links.ReportOf(game.Path);
        var checking = links.IsChecking(game.Path);
        GameReportView.Status(report, GameLook.Name(report, game), GameLook.State(report, checking));
        if (report is { Kind: GameKind.SourceBuild })
        {
            ImGui.SetItemTooltip(GameTexts.SourceBuildTooltip);
        }

        CardLayout.SmallSpace();
        if (CardLayout.FolderWithOpenLink(game.Path, report is not { FolderMissing: true }))
        {
            FolderActions.Open(window, GameLinks.Source, game.Path);
        }

        if (CardLayout.BeginFacts("##facts"))
        {
            if (report is { IsGame: true })
            {
                GameReportView.GameFacts(report);
            }

            CardLayout.FactLabel("Used by");
            UsedBy(links.UsedBy(game.Path));
            if (report is { Root: not null } && PlayTarget.Of([report]).Count > 0)
            {
                CardLayout.Fact("Play copy", CopyText(game.Path));
                ImGui.SetItemTooltip("Play in the game installs the mods into this folder, not into your game folder. " +
                                     "Many files in it are hard links to the files of your game.");
            }

            ImGui.EndTable();
        }

        if (report != null)
        {
            CardLayout.SmallSpace();
            GameReportView.Details(report, GameReportView.Notes(report));
        }

        if (links.ErrorOf(game.Path) is { } error)
        {
            CardLayout.Note(EditorIcons.TimesCircle, EditorTheme.Error, error);
        }

        CardLayout.Gap();
        Buttons(game, report, checking);
        CopyButtons(game, report);
    }

    /// <summary>The folder of the play copy of a game and its size on disk, or why it has none.</summary>
    private string CopyText(string root)
    {
        var info = window.GamePlay.CopyInfo(root);
        return info switch
        {
            null => window.GamePlay.CopyFolder(root),
            { Record: null } => $"{info.Folder} (not made yet)",
            { Record: var record } => $"{info.Folder} ({PlayCopyTexts.Size(info.Size)} on disk, {record.LinkCount} links" +
                                      (record.State == PlayCopyState.Complete ? ")" : ", not complete)"),
        };
    }

    /// <summary>The buttons of the play copy of a game: Open folder, Check play copy, Uninstall mods and Remove play
    /// copy. Remove asks first.</summary>
    private void CopyButtons(LinkedGame game, GameReport? report)
    {
        if (window.GamePlay.CopyInfo(game.Path) is not { Record: not null } info)
        {
            return;
        }

        var plays = window.GamePlay;
        var block = plays.CopyBlock(game.Path);
        CardLayout.SmallSpace();
        if (ImGui.Button("Open play copy"))
        {
            FolderActions.Open(window, GameLinks.Source, info.Folder);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(block != null);
        if (ImGui.Button("Check play copy"))
        {
            plays.CheckCopy(game.Path);
        }

        ItemTooltip.Show(block ?? "Check the links of the play copy and the installed files. The Console lists each " +
                         "difference.");
        ImGui.SameLine();
        if (ImGui.Button("Uninstall mods"))
        {
            plays.UninstallCopy(game.Path);
        }

        ItemTooltip.Show(block ?? "Give each file that Play in the game installed its version of the game again.");
        ImGui.SameLine();
        if (ImGui.Button("Remove play copy"))
        {
            window.Choice.Show("Remove play copy", PlayCopyRemoveQuestion(info.Folder),
            [
                new Choice("Remove", () => plays.RemoveCopy(game.Path)),
                new Choice("Cancel", () => { }),
            ]);
        }

        ItemTooltip.Show(block ?? "Delete the play copy and the saves in it. Your game folder does not change.");
        ImGui.EndDisabled();
    }

    /// <summary>The question of Remove play copy.</summary>
    internal static string PlayCopyRemoveQuestion(string folder) =>
        $"Remove the play copy {folder}? Your game folder does not change.";

    /// <summary>The tooltip of the disabled New profile of a game that cannot be the base of a profile, or null. A
    /// source build is a play target only. A card without a game tells why in its state line.</summary>
    internal static string? NewProfileBlock(GameReport? report) =>
        report is { IsGame: true, CanBeBase: false } ? GameTexts.SourceBuildNoBase : null;


    private void UsedBy(IReadOnlyList<string> profiles)
    {
        if (profiles.Count == 0)
        {
            ImGui.TextDisabled("no profile");
            return;
        }

        var clicked = CardLayout.LinkFlow(profiles.Select(p => Path.GetFileNameWithoutExtension(p)).ToList(), profiles);
        if (clicked >= 0)
        {
            window.OpenProfile(profiles[clicked]);
        }
    }

    /// <summary>The secondary buttons of a card. Remove link is at the right end, apart from the others.</summary>
    private void Buttons(LinkedGame game, GameReport? report, bool checking)
    {
        var links = window.Links;
        ImGui.BeginDisabled(report is not { CanBeBase: true });
        if (ImGui.Button("New profile...") && report != null)
        {
            window.ShowNewProfile([report]);
        }

        ImGui.EndDisabled();
        if (NewProfileBlock(report) is { } block)
        {
            ItemTooltip.Show(block);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(checking);
        if (ImGui.Button("Check again"))
        {
            links.Check(game.Path);
            links.RefreshUsedBy();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Relink..."))
        {
            FolderActions.Choose(window, GameLinks.Source, folder => links.Relink(game.Path, folder));
        }

        ImGui.SetItemTooltip("Select the new folder of this game, for example after you moved it. The profiles of this " +
                             "game then use the new folder. You can also drag the folder to this card.");
        const string remove = "Remove link";
        ImGui.SameLine();
        var width = ImGui.CalcTextSize(remove).X + 2 * ImGui.GetStyle().FramePadding.X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - width));
        if (ImGui.Button(remove))
        {
            var name = GameLook.Name(report, game);
            window.Choice.Show(remove, $"Remove the link to {name}? OpenLancer does not delete files. A profile " +
                                       "that uses this folder makes the link again when it opens.",
            [
                new Choice(remove, () => links.Remove(game.Path)),
                new Choice("Cancel", () => { }),
            ]);
        }
    }
}
