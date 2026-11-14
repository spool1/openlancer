using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OpenLancer.Data;
using OpenLancer.Data.Games;
using OpenLancer.Editor;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Help;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The Help menu: the text of About OpenLancer, the target of each item and the header of Report a problem. Also the
/// version in the window title and the rows of the Keys window.
/// </summary>
public sealed class HelpMenuTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string EngineHash = "fedcba9876543210fedcba9876543210fedcba98";

    private readonly TestWorkspace ws = new();

    public void Dispose() => ws.Dispose();

    [Fact]
    public void AboutShowsTheFactsOfADevelopmentBuild()
    {
        var text = AboutDialog.Text(BuildInfo.Parse($"1.0.0-dev+{Hash}", EngineHash));

        Assert.StartsWith("OpenLancer 1.0.0-dev (development build)\n", text);
        Assert.Contains($"\nCommit: {Hash}\n", text);
        Assert.Contains($"\nEngine commit: {EngineHash}\n", text);
        Assert.Contains("MIT License", text);
        Assert.Contains("NOTICE.txt", text);
        Assert.EndsWith(AboutDialog.Note, text);
    }

    [Fact]
    public void AboutShowsTheFactsOfARelease()
    {
        var text = AboutDialog.Text(BuildInfo.Parse("1.0.0-beta.1+abc1234", null));

        Assert.StartsWith("OpenLancer 1.0.0-beta.1\n", text);
        Assert.DoesNotContain("development", text);
        Assert.Contains("\nCommit: abc1234\n", text);
        Assert.Contains($"\nEngine commit: {AboutDialog.NoCommit}\n", text);
        Assert.Contains(AboutDialog.License, text);
        Assert.EndsWith(AboutDialog.Note, text);
    }

    [Fact]
    public void AboutHasTheNoteAndTheLicenseOfReadmeFirst()
    {
        var readme = File.ReadAllText(RepoFiles.Of("release", "README-FIRST.txt"));
        var note = Paragraphs(readme).Single(p => p.Contains("not affiliated", StringComparison.Ordinal));

        Assert.Equal(note, Merge(AboutDialog.Note));
        Assert.Contains(Merge(AboutDialog.License), Merge(readme));
    }

    [Fact]
    public void DocumentationOpensTheTagOfAReleaseAndTheCommitOfADevelopmentBuild()
    {
        Assert.Equal("https://github.com/spool1/openlancer/blob/v1.0.0-beta.1/README.md",
            HelpLinks.Documentation(BuildInfo.Parse("1.0.0-beta.1+abc1234", null)));
        Assert.Equal("https://github.com/spool1/openlancer/blob/v1.0.0/README.md",
            HelpLinks.Documentation(BuildInfo.Parse("1.0.0", null)));
        Assert.Equal($"https://github.com/spool1/openlancer/blob/{Hash}/README.md",
            HelpLinks.Documentation(BuildInfo.Parse($"1.0.0-dev+{Hash}", null)));
        Assert.Equal("https://github.com/spool1/openlancer/blob/master/README.md",
            HelpLinks.Documentation(BuildInfo.Parse("1.0.0-dev", null)));
        Assert.Equal($"https://github.com/spool1/openlancer/blob/{BuildInfo.DocsRef}/README.md",
            HelpLinks.Documentation(BuildInfo.Current));
    }

    /// <summary>Documentation opens a start page for a player: the README, which has the Help menu part and links the
    /// help pages. The docs folder has no start page, so GitHub would show a bare file list.</summary>
    [Fact]
    public void DocumentationOpensAStartPageThatLinksTheHelpPages()
    {
        var link = HelpLinks.Documentation(BuildInfo.Parse("1.0.0", null));
        var readme = File.ReadAllText(RepoFiles.Of(link[link.LastIndexOf('/')..].TrimStart('/')));

        Assert.Contains("## Help menu", readme);
        Assert.All(
            new[]
            {
                "GETTING_STARTED", "TROUBLESHOOTING", "GAME_LINK", "PLAY", "MOD_FORMAT", "MISSIONS", "CUTSCENES", "MCP",
            },
            page => Assert.Contains($"(docs/{page}.md)", readme));
        Assert.False(File.Exists(RepoFiles.Of("docs", "README.md")));
    }

    [Fact]
    public void DiscordOpensTheInviteOfTheReadme()
    {
        Assert.Equal("https://discord.gg/BUh3nucfmP", HelpLinks.Discord);
        Assert.Contains(HelpLinks.Discord, File.ReadAllText(RepoFiles.Of("README.md")));
    }

    [Fact]
    public void ReportAProblemOpensTheBugReportFormOfTheRepository()
    {
        var match = Regex.Match(HelpLinks.ReportForm, @"^https://github\.com/spool1/openlancer/issues/new\?template=(.+)$");

        Assert.True(match.Success, HelpLinks.ReportForm);
        Assert.True(File.Exists(RepoFiles.Of(".github", "ISSUE_TEMPLATE", match.Groups[1].Value)),
            $"The form {match.Groups[1].Value} is not in the repository.");
        Assert.Equal("bug_report.yml", match.Groups[1].Value);
    }

    [Fact]
    public void OpenLogFolderOpensTheFolderOfTheEditorAndGameLogs()
    {
        Assert.Equal(RunLogs.Folder, HelpLinks.LogFolder);
        Assert.Equal(HelpLinks.LogFolder, Path.GetDirectoryName(RunLogs.PathOf("OpenLancer.Editor")));
        Assert.Equal(HelpLinks.LogFolder, Path.GetDirectoryName(ProgramPaths.PlayLog));
    }

    [Fact]
    public void ReadMeFirstOpensReadmeFirstNextToTheEditor()
    {
        var path = Path.Combine(ws.Root, "README-FIRST.txt");
        File.WriteAllText(path, "Start here.");

        var file = HelpLinks.ReadmeFirstOf(ws.Root);

        Assert.Equal(Path.GetFullPath(path), file.Path);
        Assert.Null(file.Reason);
        Assert.True(FolderActions.IsTextFile(file.Path));
    }

    [Fact]
    public void ReadMeFirstIsOffWithItsReasonInABuildWithoutReadmeFirst()
    {
        var file = HelpLinks.ReadmeFirstOf(ws.Root);

        Assert.Equal(HelpLinks.NoReadmeFirst, file.Reason);
        Assert.False(FolderActions.IsTextFile(file.Path));
    }

    /// <summary>Each folder action takes the Console source of the view that asks. The Help menu gives its own source,
    /// so an item that does not open shows in the Console as a Help error, not as a Game link error.</summary>
    [Fact]
    public void TheHelpMenuGivesTheFolderActionsItsOwnConsoleSource()
    {
        var actions = typeof(FolderActions).GetMethods(BindingFlags.Public | BindingFlags.Static);
        var helpCalls = IlWalker.Calls(typeof(HelpMenu))
            .Where(c => c.Callee.DeclaringType == typeof(FolderActions))
            .Select(c => c.Caller)
            .Distinct()
            .ToList();
        var helpLiterals = IlWalker.Literals(typeof(HelpMenu)).ToList();

        Assert.All(actions, m => Assert.Contains(m.GetParameters(), p => p is { Name: "source" } &&
                                                                          p.ParameterType == typeof(string)));
        Assert.Empty(IlWalker.Literals(typeof(FolderActions)).Where(l => l.Text == GameLinks.Source));
        Assert.Equal(3, helpCalls.Count);
        Assert.All(helpCalls, m => Assert.Contains(helpLiterals, l => l.Method == m && l.Text == HelpMenu.Source));
    }

    [Fact]
    public void OnlyATextFileThatExistsOpens()
    {
        var program = Path.Combine(ws.Root, "tool.exe");
        File.WriteAllText(program, "");

        Assert.False(FolderActions.IsTextFile(program));
        Assert.False(FolderActions.IsTextFile(ws.Root));
        Assert.False(FolderActions.IsTextFile(Path.Combine(ws.Root, "missing.txt")));
    }

    [Fact]
    public void TheHeaderHasTheVersionTheCommitTheGameAndWindows()
    {
        var game = Report(GameKind.HdEdition, "0.7.1", Path.Combine(ws.Root, "HD Edition folder"));

        var header = ReportHeader.Text(BuildInfo.Parse($"1.0.0-dev+{Hash}", EngineHash), game, false,
            RuntimeInformation.OSDescription);

        Assert.Equal($"OpenLancer 1.0.0-dev\nCommit: {Hash}\nGame: HD Edition 0.7.1\n" +
                     $"Windows: {RuntimeInformation.OSDescription}", header);
        AssertPrivate(header, "HD Edition folder");
    }

    [Fact]
    public void TheHeaderOfASourceBuildHasNoFolderName()
    {
        var game = Report(GameKind.SourceBuild, "1.1", Path.Combine(ws.Root, "my-own-build"));

        var header = ReportHeader.Text(BuildInfo.Parse("1.0.0-beta.1", null), game, false,
            RuntimeInformation.OSDescription);

        Assert.Contains("\nGame: Source build\n", header);
        Assert.Contains($"\nCommit: {AboutDialog.NoCommit}\n", header);
        AssertPrivate(header, "my-own-build");
    }

    [Fact]
    public void TheHeaderWithoutALinkedGameSaysSo()
    {
        var notAGame = Report(GameKind.NotAGame, null, Path.Combine(ws.Root, "empty folder"));

        foreach (var game in new[] { null, notAGame })
        {
            var header = ReportHeader.Text(BuildInfo.Current, game, false, RuntimeInformation.OSDescription);
            Assert.Contains($"\nGame: {ReportHeader.NoGame}\n", header);
            AssertPrivate(header, "empty folder");
        }
    }

    /// <summary>The editor knows the kind and the version of the linked game only after its check. Before the end of
    /// the check, the header does not say that the profile has no linked game.</summary>
    [Fact]
    public void TheHeaderSaysWhenTheCheckOfTheLinkedGameDidNotFinish()
    {
        var build = BuildInfo.Current;
        var system = RuntimeInformation.OSDescription;
        var game = Report(GameKind.HdEdition, "0.7.1", Path.Combine(ws.Root, "HD Edition folder"));
        var notAGame = Report(GameKind.NotAGame, null, Path.Combine(ws.Root, "empty folder"));

        Assert.Contains($"\nGame: {ReportHeader.CheckPending}\n", ReportHeader.Text(build, null, true, system));
        Assert.Contains("\nGame: HD Edition 0.7.1\n", ReportHeader.Text(build, game, true, system));
        Assert.Contains($"\nGame: {ReportHeader.NoGame}\n", ReportHeader.Text(build, notAGame, true, system));
    }

    /// <summary>The items of the menu, in their order. A separator comes before Discord and before About
    /// OpenLancer.</summary>
    [Fact]
    public void TheMenuHasTheItemsOfTheIssue()
    {
        var items = HelpMenu.Items(BuildInfo.Current, ws.Root);

        Assert.Equal(
            ["Documentation", "Getting started", "Read me first", "Keys...", "Discord", "Report a problem",
                "Open log folder", "About OpenLancer..."],
            items.Select(i => i.Label));
        Assert.Equal(["Discord", "About OpenLancer..."], items.Where(i => i.StartsGroup).Select(i => i.Label));
        Assert.All(items, i => Assert.EndsWith(".", i.Tooltip));
    }

    /// <summary>A click on each item opens its target. Report a problem copies the header with the game of the open
    /// profile, tells the user in the Console, and then opens the form.</summary>
    [Fact]
    public void EachItemOpensItsTarget()
    {
        var readme = Path.Combine(ws.Root, HelpLinks.ReadmeFirst);
        File.WriteAllText(readme, "Start here.");
        var build = BuildInfo.Parse($"1.0.0-dev+{Hash}", EngineHash);
        var game = Report(GameKind.HdEdition, "0.7.1", Path.Combine(ws.Root, "HD Edition folder"));
        var header = ReportHeader.Text(build, game, false, RuntimeInformation.OSDescription);

        var calls = HelpMenu.Items(build, ws.Root).ToDictionary(i => i.Label, i =>
        {
            Assert.Null(i.Unavailable);
            var host = new RecordingHost { Game = game };
            HelpMenu.Run(i, host, build);
            return host.Calls;
        });

        Assert.Equal([$"web {HelpLinks.Repository}/blob/{Hash}/README.md"], calls["Documentation"]);
        Assert.Equal([$"web {HelpLinks.Repository}/blob/{Hash}/docs/GETTING_STARTED.md"], calls["Getting started"]);
        Assert.Equal([$"text {Path.GetFullPath(readme)}"], calls["Read me first"]);
        Assert.Equal(["keys"], calls["Keys..."]);
        Assert.Equal([$"web {HelpLinks.Discord}"], calls["Discord"]);
        Assert.Equal([$"copy {header}", $"info {HelpMenu.ReportCopied}", $"web {HelpLinks.ReportForm}"],
            calls["Report a problem"]);
        Assert.Equal([$"folder {RunLogs.Folder}"], calls["Open log folder"]);
        Assert.Equal(["about"], calls["About OpenLancer..."]);
    }

    [Fact]
    public void ReportAProblemCopiesTheStateOfAGameCheckThatDidNotFinish()
    {
        var build = BuildInfo.Current;
        var host = new RecordingHost { GameCheckPending = true };

        HelpMenu.Run(HelpMenu.Items(build, ws.Root).Single(i => i.Action == HelpAction.Report), host, build);

        Assert.Equal($"copy {ReportHeader.Text(build, null, true, RuntimeInformation.OSDescription)}", host.Calls[0]);
        Assert.Contains($"\nGame: {ReportHeader.CheckPending}\n", host.Calls[0]);
    }

    [Fact]
    public void ReadMeFirstIsOffInTheMenuOfABuildWithoutReadmeFirst()
    {
        var items = HelpMenu.Items(BuildInfo.Current, ws.Root);

        Assert.Equal(HelpLinks.NoReadmeFirst, items.Single(i => i.Label == "Read me first").Unavailable);
        Assert.Null(items.Single(i => i.Label == "Getting started").Unavailable);
    }

    /// <summary>
    /// Getting started opens the help page with the same name, at the commit or the tag of Documentation. So the name
    /// "Getting started" means one document in the menu and in the help pages. The page has the title of the item.
    /// </summary>
    [Fact]
    public void GettingStartedOpensThePageWithTheSameName()
    {
        var link = HelpLinks.GettingStarted(BuildInfo.Parse("1.0.0", null));
        var page = File.ReadAllLines(RepoFiles.Of("docs", "GETTING_STARTED.md"));

        Assert.Equal("https://github.com/spool1/openlancer/blob/v1.0.0/docs/GETTING_STARTED.md", link);
        Assert.Equal($"{HelpLinks.Repository}/blob/{BuildInfo.DocsRef}/docs/GETTING_STARTED.md",
            HelpLinks.GettingStarted(BuildInfo.Current));
        Assert.Equal("# Getting started", page[0]);
    }

    [Fact]
    public void TheWindowTitleHasTheVersion()
    {
        Assert.Equal("OpenLancer Editor 1.0.0-dev", EditorWindow.TitleText("1.0.0-dev", null, null, false, null, null));
        Assert.Equal("OpenLancer Editor 1.0.0-dev - sample [proof]* - New York (li01)",
            EditorWindow.TitleText("1.0.0-dev", "sample", "proof", true, "New York", "li01"));
        Assert.Equal("OpenLancer Editor 1.0.0-beta.1 - sample [read-only] - li01",
            EditorWindow.TitleText("1.0.0-beta.1", "sample", null, false, null, "li01"));
        Assert.StartsWith($"OpenLancer Editor {BuildInfo.Version}",
            EditorWindow.TitleText(BuildInfo.Version, null, null, false, null, null));
    }

    [Fact]
    public void TheKeysWindowHasARowForEachKeyOfTheEditor()
    {
        var rows = KeysWindow.Rows;

        foreach (var key in EditorKeys.Global)
            Assert.Contains(new KeyRow(KeysWindow.Global, key.Chord.Text, key.Action), rows);
        foreach (var key in EditorKeys.Scene)
            Assert.Contains(new KeyRow(KeysWindow.Scene, key.Chord.Text, key.Action), rows);
        foreach (var (tool, key) in GizmoInput.ToolKeys)
            Assert.Single(rows, r => r.Group == KeysWindow.Scene && r.Keys == KeyChord.KeyName(key) &&
                                     r.Action.Contains(tool.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Single(rows, r => r.Group == KeysWindow.Scene && r.Keys == KeyChord.KeyName(GizmoInput.SpaceKey));
        Assert.Single(rows, r => r.Group == KeysWindow.Scene && r.Keys == GizmoInput.SnapInput &&
                                 r.Action.StartsWith("Snap", StringComparison.Ordinal));
        foreach (var (input, action) in SceneView.MouseControls)
            Assert.Contains(new KeyRow(KeysWindow.SceneMouse, input, action + "."), rows);
        foreach (var key in EditorKeys.Hierarchy)
            Assert.Contains(new KeyRow(KeysWindow.Hierarchy, key.Chord.Text, key.Action), rows);
        foreach (var command in Enum.GetValues<PreviewKey>().Where(k => k != PreviewKey.None))
        {
            var (_, chord, action) = PreviewKeys.Table.Single(r => r.Command == command);
            Assert.Contains(new KeyRow(KeysWindow.Preview, chord.Text, action), rows);
        }

        Assert.Equal(EditorKeys.Global.Length + EditorKeys.Scene.Length + GizmoInput.KeyRows.Length +
                     SceneView.MouseControls.Length + EditorKeys.Hierarchy.Length + PreviewKeys.Table.Length,
            rows.Count);
        Assert.Equal(
            [KeysWindow.Global, KeysWindow.Scene, KeysWindow.SceneMouse, KeysWindow.Hierarchy, KeysWindow.Preview],
            rows.Select(r => r.Group).Distinct());
    }

    [Fact]
    public void TheKeysWindowShowsTheKeysOfTheEditorTheSceneTheHierarchyAndThePreview()
    {
        string[] expected =
        [
            "Ctrl+S", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "F5", "Ctrl+F5", "Shift+F5", "F2", "Ctrl+D", "Delete", "F", "W",
            "E", "R", "X", "Ctrl, while dragging a handle", "Esc", "Up arrow", "Down arrow", "Enter", "Ctrl+C", "Space",
            "Home", "End", "Left arrow", "Right arrow", "Shift+Left arrow", "Shift+Right arrow",
        ];

        Assert.Equal(expected.Order(),
            KeysWindow.Rows.Where(r => r.Group != KeysWindow.SceneMouse).Select(r => r.Keys).Order());
    }

    /// <summary>The Keys window shows the mouse controls and the fly keys of the Scene camera, as the help popover of
    /// the Scene does.</summary>
    [Fact]
    public void TheKeysWindowShowsTheCameraControlsOfTheScene()
    {
        var mouse = KeysWindow.Rows.Where(r => r.Group == KeysWindow.SceneMouse).ToList();

        Assert.Contains(mouse, r => r.Keys == "Right mouse drag");
        Assert.Contains(mouse, r => r.Keys == "W A S D, Q and E" && r.Action.Contains("right mouse button"));
        Assert.Contains(mouse, r => r.Keys == "Alt + left mouse drag");
        Assert.Contains(mouse, r => r.Keys == "Middle mouse drag");
        Assert.All(SceneView.MouseControls, c => Assert.Contains((c.Input, c.Action + "."), SceneView.Controls));
    }

    /// <summary>The help popovers of the Scene and of the cutscene preview give each key the name that the Keys window
    /// gives it (#55). The Scene popover also gives each key the action of the Keys window. So one key has one
    /// name.</summary>
    [Fact]
    public void TheHelpPopoversNameTheKeysAsTheKeysWindowDoes()
    {
        var mouse = SceneView.MouseControls.Select(c => (c.Input, Action: c.Action + ".")).ToList();
        var sceneKeys = SceneView.Controls.Except(mouse).Select(c => new KeyRow(KeysWindow.Scene, c.Input, c.Action))
            .ToList();

        Assert.All(sceneKeys, r => Assert.Contains(r, KeysWindow.Rows));
        Assert.Contains(sceneKeys, r => r.Keys == EditorKeys.Frame.Chord.Text);
        Assert.Contains(sceneKeys, r => r.Keys == GizmoInput.SnapInput);
        Assert.Contains(sceneKeys, r => r.Keys == EditorKeys.CancelDrag.Chord.Text);
        foreach (var (_, chord, action) in PreviewKeys.Table)
            Assert.Contains((chord.Text, action), CutsceneView.Controls);

        var names = KeysWindow.Rows.Select(r => r.Keys).ToHashSet();
        Assert.All(CutsceneView.Controls, c => Assert.True(
            names.Contains(c.Input) || c.Input.Split(" and ").All(names.Contains), c.Input));
        Assert.All(SceneView.Controls.Concat(CutsceneView.Controls),
            c => Assert.DoesNotContain("focus", c.Action, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Each paragraph of a text file, with its white space merged to single spaces.</summary>
    private static string[] Paragraphs(string text) =>
        Regex.Split(text.Replace("\r\n", "\n"), @"\n[ \t]*\n").Select(Merge).Where(p => p.Length > 0).ToArray();

    private static string Merge(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>The header has no path, no user name and no folder name of the game.</summary>
    private static void AssertPrivate(string header, string folderName)
    {
        Assert.DoesNotContain('\\', header);
        Assert.DoesNotContain('/', header);
        Assert.DoesNotContain(folderName, header);
        if (Environment.UserName.Length > 2)
            Assert.DoesNotContain(Environment.UserName, header, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fake editor of <see cref="HelpMenu.Run"/>: it writes each call as a line.</summary>
    private sealed class RecordingHost : IHelpMenuHost
    {
        public List<string> Calls { get; } = [];

        public GameReport? Game { get; init; }

        public bool GameCheckPending { get; init; }

        public void OpenWebLink(string url) => Calls.Add($"web {url}");

        public void OpenTextFile(string path) => Calls.Add($"text {path}");

        public void OpenFolder(string path) => Calls.Add($"folder {path}");

        public void CopyText(string text) => Calls.Add($"copy {text}");

        public void Info(string text) => Calls.Add($"info {text}");

        public void ShowKeys() => Calls.Add("keys");

        public void ShowAbout() => Calls.Add("about");
    }

    private static GameReport Report(GameKind kind, string? version, string folder) =>
        new(folder, kind == GameKind.NotAGame ? null : folder, kind, version, VersionProof.Hash, false, null, null,
            null, null, null, DataState.NotCompared, null, 0, 0, [], [], []);
}
