using System;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>
/// The first-run screen on the Welcome page (section 7 of the M11a design): link the game folder, check it, and make a
/// profile with an empty mod that opens ready to edit. Screen A asks for the folder, B checks it, C shows the game and
/// the new profile, and D tells why a folder holds no game. Each screen has a title, a subtitle, a body and a footer
/// with one primary action (<see cref="CardLayout"/>).
/// </summary>
internal sealed class FirstRunCard(EditorWindow window)
{
    // Sizes in unscaled points.
    private const float LinkGap = 16;
    private const float AreaGlyphSize = 28;

    private readonly NewProfileForm form = new(window);
    private Screen screen = Screen.Link;
    private string folder = "";
    private GameReport? report;
    private CancellationTokenSource? cancel;

    private enum Screen
    {
        Link,
        Check,
        Found,
        NoGame,
    }

    /// <summary>True after Skip. The normal Welcome card then shows until the next start.</summary>
    public bool Skipped { get; private set; }

    /// <summary>Checks a folder from the folder picker or from a drop on the window: screen B, then C or D.</summary>
    public void Check(string path)
    {
        cancel?.Cancel();
        var token = new CancellationTokenSource();
        cancel = token;
        folder = path;
        screen = Screen.Check;
        window.Links.Detect(path, token.Token, found =>
        {
            if (token != cancel)
            {
                return;
            }

            report = found;
            // A source build is a play target, not the base of a profile (section 8.5 of the M11 design).
            screen = found.CanBeBase ? Screen.Found : Screen.NoGame;
            if (found.CanBeBase)
            {
                form.Reset([found]);
            }
        });
    }

    /// <summary>Draws the screen in the Welcome card.</summary>
    public void Draw()
    {
        switch (screen)
        {
            case Screen.Check:
                CheckScreen();
                break;
            case Screen.Found when report != null:
                FoundScreen(report);
                break;
            case Screen.NoGame when report != null:
                NoGameScreen(report);
                break;
            default:
                LinkScreen();
                break;
        }
    }

    /// <summary>Screen A: link the game. The area in the middle has the primary button.</summary>
    private void LinkScreen()
    {
        CardLayout.Title("Link your Freelancer game",
            "OpenLancer needs the files of your own copy of Freelancer (2003).");
        CardLayout.Divider();
        CardLayout.Note(EditorIcons.InfoCircle, EditorTheme.AccentText, "OpenLancer does not include game files.");
        CardLayout.Note(EditorIcons.Folder, EditorTheme.AccentText,
            "Select the folder of your game. This folder contains the EXE and DATA folders.",
            @"Example: C:\Program Files (x86)\Microsoft Games\Freelancer");
        CardLayout.Note(EditorIcons.ShieldAlt, EditorTheme.AccentText,
            "OpenLancer reads the files in this folder. It does not change them.");
        CardLayout.Gap();
        if (CardLayout.BeginArea("##folder"))
        {
            CardLayout.CenteredGlyph(EditorIcons.FolderOpen, EditorTheme.TextDim, AreaGlyphSize);
            CardLayout.Gap();
            var select = new CardButton($"{EditorIcons.Folder}  Select game folder...", Primary: true);
            CardLayout.Center(CardLayout.ButtonWidth(select.Label));
            if (CardLayout.Button(select))
            {
                ChooseFolder();
            }

            const string hint = "You can also drag the game folder to this window.";
            ImGui.PushFont(null, EditorTheme.CaptionSize);
            CardLayout.Center(ImGui.CalcTextSize(hint).X);
            ImGui.TextDisabled(hint);
            ImGui.PopFont();
        }

        CardLayout.EndArea();
        CardLayout.SmallSpace();
        CardLayout.Caption(
            "OpenLancer can identify Freelancer 1.0 and 1.1, with or without JFLP, and Freelancer: HD Edition.");
        CardLayout.Divider();
        if (ImGui.TextLink("Open a profile file..."))
        {
            window.OpenProfileDialog();
        }

        ImGui.SameLine(0, MathF.Truncate(LinkGap * ImGuiHelper.Scale));
        SkipLink();
    }

    /// <summary>Screen B: the check runs.</summary>
    private void CheckScreen()
    {
        CardLayout.Title("Game folder check", "OpenLancer reads the files in this folder. This can take some seconds.");
        CardLayout.Divider();
        CardLayout.Folder(folder);
        CardLayout.SmallSpace();
        ImGui.ProgressBar(-(float)ImGui.GetTime(), new Vector2(ImGui.GetContentRegionAvail().X, 0), "");
        ImGuiHelper.AnimatingElement();
        CardLayout.Divider();
        if (CardLayout.Buttons(false, new CardButton("Cancel")) == 0)
        {
            cancel?.Cancel();
            cancel = null;
            screen = Screen.Link;
        }
    }

    /// <summary>Screen C: the game, its notes and the new profile.</summary>
    private void FoundScreen(GameReport found)
    {
        CardLayout.Title(found.Severity == GameSeverity.Ok ? "Game found" : "Game found, with warnings",
            "OpenLancer found a game in this folder. Make a profile for it.");
        CardLayout.Divider();
        GameReportView.Status(found, found.Name, found.DataText);
        CardLayout.SmallSpace();
        // The check read the folder right now, so it exists.
        if (CardLayout.FolderWithOpenLink(found.Root!, true))
        {
            FolderActions.Open(window, GameLinks.Source, found.Root!);
        }

        CardLayout.Gap();
        var more = GameReportView.Notes(found);
        if (found is { Data: DataState.NotChanged, Findings.Count: 0, Reference: { } reference })
        {
            CardLayout.Note(EditorIcons.CheckCircle, EditorTheme.Success,
                $"The INI files are the same as the {GameDetector.DataName(reference)} files.");
        }

        if (window.Links.Find(found.Root!) != null)
        {
            CardLayout.Note(EditorIcons.Link, EditorTheme.AccentText,
                "OpenLancer has a link to this game already. You can make one more profile for it.");
        }

        GameReportView.Details(found, more);
        CardLayout.Gap();
        CardLayout.Heading("New profile");
        form.Draw();
        CardLayout.Divider();
        var create = new CardButton("Create profile and open", Primary: true, Enabled: form.CanCreate);
        switch (CardLayout.Buttons(false, create, new CardButton("Select another folder")))
        {
            case 0:
                form.Create();
                break;
            case 1:
                ChooseFolder();
                break;
        }
    }

    /// <summary>Screen D: no game in the folder. The status row tells why.</summary>
    private void NoGameScreen(GameReport found)
    {
        CardLayout.Title(found.IsGame ? "No profile for this game" : "No game in this folder",
            "Select the folder that contains the EXE and DATA folders of the game.");
        CardLayout.Divider();
        GameReportView.Status(found, found.Name, found.Error ?? GameTexts.SourceBuildNoBase);
        CardLayout.SmallSpace();
        if (CardLayout.FolderWithOpenLink(found.Folder, !found.FolderMissing))
        {
            FolderActions.Open(window, GameLinks.Source, found.Folder);
        }

        GameReportView.Details(found, GameReportView.Notes(found));
        CardLayout.Divider();
        SkipLink();
        var select = new CardButton($"{EditorIcons.Folder}  Select another folder", Primary: true);
        if (CardLayout.Buttons(true, select) == 0)
        {
            ChooseFolder();
        }
    }

    private void SkipLink()
    {
        if (ImGui.TextLink("Skip"))
        {
            cancel?.Cancel();
            Skipped = true;
        }

        ImGui.SetItemTooltip("Shows the normal Welcome page for this start.");
    }

    private void ChooseFolder() => FolderActions.Choose(window, GameLinks.Source, Check);
}
