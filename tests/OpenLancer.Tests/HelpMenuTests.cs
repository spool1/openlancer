using System;
using System.IO;
using System.Linq;
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
        Assert.Equal("https://github.com/spool1/openlancer/tree/v1.0.0-beta.1/docs",
            HelpLinks.Documentation(BuildInfo.Parse("1.0.0-beta.1+abc1234", null)));
        Assert.Equal("https://github.com/spool1/openlancer/tree/v1.0.0/docs",
            HelpLinks.Documentation(BuildInfo.Parse("1.0.0", null)));
        Assert.Equal($"https://github.com/spool1/openlancer/tree/{Hash}/docs",
            HelpLinks.Documentation(BuildInfo.Parse($"1.0.0-dev+{Hash}", null)));
        Assert.Equal("https://github.com/spool1/openlancer/tree/master/docs",
            HelpLinks.Documentation(BuildInfo.Parse("1.0.0-dev", null)));
        Assert.Equal($"https://github.com/spool1/openlancer/tree/{BuildInfo.DocsRef}/docs",
            HelpLinks.Documentation(BuildInfo.Current));
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
    public void GettingStartedOpensReadmeFirstNextToTheEditor()
    {
        var path = Path.Combine(ws.Root, "README-FIRST.txt");
        File.WriteAllText(path, "Start here.");

        var file = HelpLinks.GettingStarted(ws.Root);

        Assert.Equal(Path.GetFullPath(path), file.Path);
        Assert.Null(file.Reason);
        Assert.True(FolderActions.IsTextFile(file.Path));
    }

    [Fact]
    public void GettingStartedIsOffWithItsReasonInABuildWithoutReadmeFirst()
    {
        var file = HelpLinks.GettingStarted(ws.Root);

        Assert.Equal(HelpLinks.NoReadmeFirst, file.Reason);
        Assert.False(FolderActions.IsTextFile(file.Path));
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

        var header = ReportHeader.Text(BuildInfo.Parse($"1.0.0-dev+{Hash}", EngineHash), game,
            RuntimeInformation.OSDescription);

        Assert.Equal($"OpenLancer 1.0.0-dev\nCommit: {Hash}\nGame: HD Edition 0.7.1\n" +
                     $"Windows: {RuntimeInformation.OSDescription}", header);
        AssertPrivate(header, "HD Edition folder");
    }

    [Fact]
    public void TheHeaderOfASourceBuildHasNoFolderName()
    {
        var game = Report(GameKind.SourceBuild, "1.1", Path.Combine(ws.Root, "my-own-build"));

        var header = ReportHeader.Text(BuildInfo.Parse("1.0.0-beta.1", null), game, RuntimeInformation.OSDescription);

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
            var header = ReportHeader.Text(BuildInfo.Current, game, RuntimeInformation.OSDescription);
            Assert.Contains($"\nGame: {ReportHeader.NoGame}\n", header);
            AssertPrivate(header, "empty folder");
        }
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
        foreach (var (input, action) in SceneView.MouseControls)
            Assert.Contains(new KeyRow(KeysWindow.SceneMouse, input, action + "."), rows);
        foreach (var key in EditorKeys.Hierarchy)
            Assert.Contains(new KeyRow(KeysWindow.Hierarchy, key.Chord.Text, key.Action), rows);
        foreach (var command in Enum.GetValues<PreviewKey>().Where(k => k != PreviewKey.None))
        {
            var (_, chord, action) = PreviewKeys.Table.Single(r => r.Command == command);
            Assert.Contains(new KeyRow(KeysWindow.Preview, chord.Text, action), rows);
        }

        Assert.Equal(EditorKeys.Global.Length + EditorKeys.Scene.Length + GizmoInput.ToolKeys.Length + 1 +
                     SceneView.MouseControls.Length + EditorKeys.Hierarchy.Length + PreviewKeys.Table.Length,
            rows.Count);
        Assert.Equal([KeysWindow.Global, KeysWindow.Scene, KeysWindow.SceneMouse, KeysWindow.Hierarchy, KeysWindow.Preview],
            rows.Select(r => r.Group).Distinct());
    }

    [Fact]
    public void TheKeysWindowShowsTheKeysOfTheEditorTheSceneTheHierarchyAndThePreview()
    {
        string[] expected =
        [
            "Ctrl+S", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "F5", "Ctrl+F5", "Shift+F5", "F2", "Ctrl+D", "Delete", "F", "W",
            "E", "R", "X", "Esc", "Up arrow", "Down arrow", "Enter", "Ctrl+C", "Space", "Home", "End", "Left arrow",
            "Right arrow", "Shift+Left arrow", "Shift+Right arrow",
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
        Assert.All(SceneView.MouseControls, c => Assert.Contains(c, SceneView.Controls));
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

    private static GameReport Report(GameKind kind, string? version, string folder) =>
        new(folder, kind == GameKind.NotAGame ? null : folder, kind, version, VersionProof.Hash, false, null, null,
            null, null, null, DataState.NotCompared, null, 0, 0, [], [], []);
}
