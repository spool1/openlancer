using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Editor;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The linked games in the settings (decision D190) and the workspace folder (decision D191).</summary>
public sealed class EditorSettingsGamesTests
{
    private static readonly DateTime Linked = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheGamesAndTheWorkspaceFolderRoundTrip()
    {
        var settings = EditorSettings.Transient();
        settings.Games.Add(new LinkedGame(@"C:\Games\Freelancer", Linked, "Freelancer 1.1 with JFLP", "vanilla-1.1"));
        settings.Games.Add(new LinkedGame(@"D:\HD", Linked, "Freelancer: HD Edition 0.7.1", null));
        settings.WorkspaceFolder = @"D:\Mods";

        var copy = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        Assert.Equal(settings.Games, copy.Games);
        Assert.Equal(@"D:\Mods", copy.WorkspaceFolder);
        Assert.Equal(@"D:\Mods", copy.WorkspaceOrDefault);
    }

    [Theory]
    [InlineData("""{ "Games": [ null ] }""")]
    [InlineData("""{ "Games": [ { "Path": "", "LinkedUtc": "2026-10-18T09:00:00Z", "LastName": "x" } ] }""")]
    [InlineData("""{ "Games": [ { "Path": null, "LinkedUtc": "2026-10-18T09:00:00Z", "LastName": "x" } ] }""")]
    public void AGameWithoutAPathMakesTheFileDamaged(string json)
    {
        Assert.ThrowsAny<JsonException>(() => EditorSettings.Parse(json, DateTime.UtcNow));
    }

    [Fact]
    public void AnEmptyWorkspaceGivesDocumentsOpenLancer()
    {
        var settings = EditorSettings.Parse("{}", DateTime.UtcNow);

        Assert.Empty(settings.Games);
        Assert.Equal("", settings.WorkspaceFolder);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenLancer"),
            settings.WorkspaceOrDefault);
        Assert.Equal(ModWorkspace.DefaultFolder, settings.WorkspaceOrDefault);
    }
}

/// <summary>
/// The game link controller (section 13 of the M11a design) without a window: a fake host, a queued UI thread and fake
/// game folders with fake build manifests.
/// </summary>
public sealed class GameLinksTests : IDisposable
{
    private readonly FakeGame game = new();
    private readonly Host host = new();
    private readonly KnownBuilds builds;
    private readonly GameLinks links;

    public GameLinksTests()
    {
        builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        links = new GameLinks(host, enabled: true, builds);
    }

    public void Dispose() => game.Dispose();

    private IReadOnlyList<string> Console => host.Log.Entries.Select(e => e.Message).ToList();

    private async Task<GameReport> LinkNowAsync(string folder)
    {
        GameReport? result = null;
        links.Link(folder, r => result = r);
        await host.Ui.RunUntilAsync(() => result != null, "the link");
        return result!;
    }

    /// <summary>A profile in a workspace folder of the test, with the base folder.</summary>
    private string Profile(string name, string baseFolder, string? folder = null)
    {
        folder ??= Path.Combine(game.Folder, "workspace", "profiles");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { @base = baseFolder, mods = Array.Empty<string>() }));
        host.Profiles.Add(path);
        return path;
    }

    [Fact]
    public async Task LinkAddsTheGameOnce()
    {
        var report = await LinkNowAsync(game.Root);
        await LinkNowAsync(game.Root + Path.DirectorySeparatorChar);
        await LinkNowAsync(game.PathOf("DATA"));
        if (OperatingSystem.IsWindows())
        {
            await LinkNowAsync(game.Root.ToUpperInvariant());
        }

        Assert.Equal("Freelancer 1.0", report.Name);
        var entry = Assert.Single(links.Games);
        Assert.Equal(game.Root, entry.Path);
        Assert.Equal("Freelancer 1.0", entry.LastName);
        Assert.Equal("vanilla-1.0", entry.LastBuild);
        Assert.Same(links.ReportOf(game.Root), links.ReportOf(game.Root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task AFolderWithoutAGameGetsNoLink()
    {
        var report = await LinkNowAsync(game.Folder + Path.DirectorySeparatorChar + "none");

        Assert.False(report.IsGame);
        Assert.Empty(links.Games);
    }

    [Fact]
    public async Task RelinkWithTheSameBuildRewritesEachProfileOfTheOldRoot()
    {
        await LinkNowAsync(game.Root);
        host.Settings.Games[0] = host.Settings.Games[0] with { PlayCopy = Path.Combine(game.Folder, "play") };
        var first = Profile("first", game.Root);
        var second = Profile("second", game.Root);
        var other = Profile("other", game.Folder);
        host.Reopen = first;
        var moved = Path.Combine(game.Folder, "Moved");
        Directory.Move(game.Root, moved);

        links.Relink(game.Root, moved);
        await host.Ui.RunUntilAsync(() => links.Games[0].Path == moved, "the relink");

        Assert.Equal(moved, ModProfile.BaseFolderOf(first));
        Assert.Equal(moved, ModProfile.BaseFolderOf(second));
        Assert.Equal(game.Folder, ModProfile.BaseFolderOf(other));
        Assert.Equal([first], host.Opened);
        Assert.Contains(Console, c => c.StartsWith($"Relink changed the base folder of the profile {first}"));
        Assert.Null(host.Asked);
        // The play copy of the old folder copies the old folder, so the new folder gets the default play copy.
        Assert.Null(links.Games[0].PlayCopy);
    }

    [Fact]
    public async Task RelinkToASourceBuildIsRefused()
    {
        await LinkNowAsync(game.Root);
        var profile = Profile("p", game.Root);
        using var source = new SourceBuildGame();

        links.Relink(game.Root, source.Root);
        await host.Ui.RunUntilAsync(() => links.ErrorOf(game.Root) != null, "the error");

        Assert.Equal(GameTexts.SourceBuildNoBase, links.ErrorOf(game.Root));
        Assert.Equal(game.Root, ModProfile.BaseFolderOf(profile));
        Assert.Equal(game.Root, Assert.Single(links.Games).Path);
    }

    [Fact]
    public async Task ASourceBuildLinksAsAPlayTarget()
    {
        using var source = new SourceBuildGame();

        var report = await LinkNowAsync(source.Root);

        Assert.Equal(GameKind.SourceBuild, report.Kind);
        Assert.False(report.CanBeBase);
        Assert.Equal(source.Root, Assert.Single(links.Games).Path);
        Assert.Equal("source build", Assert.Single(PlayTarget.Of([report])).Name);
    }

    [Fact]
    public void NewProfileOfASourceBuildTellsWhyAndNamesThePlayItem()
    {
        using var source = new SourceBuildGame();
        var sourceReport = source.Detect();
        var retail = game.Detect(builds);

        var block = GameLinkWindow.NewProfileBlock(sourceReport);

        Assert.Equal(GameTexts.SourceBuildNoBase, block);
        // The tooltip names the real item of the Play menu, "Play in <target>".
        Assert.Contains($"Play > Play in {Assert.Single(PlayTarget.Of([sourceReport])).Name}.", block);
        Assert.Null(GameLinkWindow.NewProfileBlock(retail));
        // A card without a game tells why in its state line.
        Assert.Null(GameLinkWindow.NewProfileBlock(null));
        Assert.Null(GameLinkWindow.NewProfileBlock(GameDetector.CheckRoot(Path.Combine(source.Folder, "missing"),
            cancel: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RelinkWithAnotherBuildAsksFirst()
    {
        await LinkNowAsync(game.Root);
        var profile = Profile("p", game.Root);
        using var other = new FakeGame();
        other.CodeFiles("1.1");

        links.Relink(game.Root, other.Root);
        await host.Ui.RunUntilAsync(() => host.Asked != null, "the question");

        Assert.Equal(game.Root, ModProfile.BaseFolderOf(profile));
        Assert.Contains("Unknown Freelancer version", host.Asked!.Value.Message);
        Assert.Contains("Freelancer 1.0", host.Asked.Value.Message);
        Assert.Equal(["Relink", "Cancel"], host.Asked.Value.Choices.Select(c => c.Label));

        host.Asked.Value.Choices[0].Action();

        Assert.Equal(other.Root, ModProfile.BaseFolderOf(profile));
        Assert.Equal(other.Root, Assert.Single(links.Games).Path);
    }

    [Fact]
    public async Task RelinkToAFolderWithoutAGameChangesNothing()
    {
        await LinkNowAsync(game.Root);
        var profile = Profile("p", game.Root);

        links.Relink(game.Root, game.Folder + Path.DirectorySeparatorChar + "none");
        await host.Ui.RunUntilAsync(() => links.ErrorOf(game.Root) != null, "the error");

        Assert.Equal(GameTexts.FolderMissing, links.ErrorOf(game.Root));
        Assert.Equal(game.Root, ModProfile.BaseFolderOf(profile));
        Assert.Equal(game.Root, Assert.Single(links.Games).Path);
    }

    [Fact]
    public async Task RelinkSkipsAProfileInAGameFolderInTheRepoOrWithAComment()
    {
        await LinkNowAsync(game.Root);
        using var moved = new FakeGame();
        var inGame = Profile("in-game", game.Root, moved.PathOf("profiles"));
        var repo = Path.Combine(game.Folder, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, RepoCheck.MarkerFile), "");
        var inRepo = Profile("in-repo", game.Root, Path.Combine(repo, "profiles"));
        var comment = Path.Combine(game.Folder, "workspace", "profiles", "comment.json");
        Directory.CreateDirectory(Path.GetDirectoryName(comment)!);
        File.WriteAllText(comment, $$"""{ // a comment{{"\n"}} "base": {{JsonSerializer.Serialize(game.Root)}}, "mods": [] }""");
        host.Profiles.Add(comment);
        var plain = Profile("plain", game.Root);

        links.Relink(game.Root, moved.Root);
        await host.Ui.RunUntilAsync(() => links.Games[0].Path == moved.Root, "the relink");

        Assert.Equal(game.Root, ModProfile.BaseFolderOf(inGame));
        Assert.Equal(game.Root, ModProfile.BaseFolderOf(inRepo));
        Assert.Equal(game.Root, ModProfile.BaseFolderOf(comment));
        Assert.Equal(moved.Root, ModProfile.BaseFolderOf(plain));
        const string skipped = "Relink did not change the profile";
        Assert.Contains(Console, c => c.StartsWith($"{skipped} {inGame}: The profile file is in a game folder."));
        Assert.Contains(Console, c => c.StartsWith($"{skipped} {inRepo}: The profile file is part of the OpenLancer repo"));
        Assert.Contains(Console, c => c.StartsWith($"{skipped} {comment}:"));
    }

    [Fact]
    public async Task TheUsedByListFindsProfilesOfAMissingGame()
    {
        await LinkNowAsync(game.Root);
        var profile = Profile("p", game.Root);
        Profile("other", game.Folder);
        Directory.Delete(game.Root, true);

        links.RefreshUsedBy();
        await host.Ui.RunUntilAsync(() => links.UsedBy(game.Root).Count > 0, "the used-by list");
        links.Check(game.Root);
        await host.Ui.RunUntilAsync(() => !links.IsChecking(game.Root), "the check");

        Assert.Equal([profile], links.UsedBy(game.Root));
        Assert.True(links.ReportOf(game.Root)!.FolderMissing);
        Assert.Equal("Not found", GameLook.State(links.ReportOf(game.Root), false));
    }

    [Fact]
    public async Task RemoveDeletesNoFile()
    {
        await LinkNowAsync(game.Root);
        Profile("p", game.Root);
        var before = FakeGame.Snapshot(game.Folder);

        links.Remove(game.Root);

        Assert.Empty(links.Games);
        Assert.Equal(before, FakeGame.Snapshot(game.Folder));
    }

    [Fact]
    public async Task OpeningAProfileOnAnUnlinkedGameLinksIt()
    {
        var path = Profile("p", game.Root);

        links.ProfileOpened(ModProfile.Load(path));
        await host.Ui.RunUntilAsync(() => links.Games.Count == 1, "the link");

        Assert.Equal(game.Root, links.Games[0].Path);
        Assert.Contains(Console, c => c == $"The profile p uses the game folder {game.Root}, so OpenLancer linked it.");
    }

    [Fact]
    public async Task NotOnANonGameBase()
    {
        var path = Profile("p", game.PathOf("DATA"));

        links.ProfileOpened(ModProfile.Load(path));
        await host.Ui.RunUntilAsync(() => links.ReportOf(game.PathOf("DATA")) != null, "the check");

        Assert.Empty(links.Games);
        Assert.Equal(GameTexts.NotAGameFolder, links.ReportOf(game.PathOf("DATA"))!.Error);
    }

    [Fact]
    public async Task NotInTheBench()
    {
        var bench = new GameLinks(host, enabled: false, builds);
        var path = Profile("p", game.Root);
        host.Settings.Games.Add(new LinkedGame(game.Root, DateTime.UtcNow, "Freelancer 1.0", "vanilla-1.0"));

        bench.ProfileOpened(ModProfile.Load(path));
        bench.CheckAll();
        await host.Ui.RunUntilQuietAsync(TimeSpan.FromMilliseconds(300));

        Assert.Null(bench.ReportOf(game.Root));
        Assert.False(bench.IsChecking(game.Root));
    }

    [Fact]
    public async Task AChangedBuildBetweenStartsWritesAConsoleLine()
    {
        host.Settings.Games.Add(new LinkedGame(game.Root, DateTime.UtcNow, "Freelancer 1.1", "vanilla-1.1"));

        links.CheckAll();
        await host.Ui.RunUntilAsync(() => links.ReportOf(game.Root) != null, "the check");

        Assert.Contains($"The game in {game.Root} changed. It was Freelancer 1.1. Now it is Freelancer 1.0.", Console);
        Assert.Equal(new LinkedGame(game.Root, host.Settings.Games[0].LinkedUtc, "Freelancer 1.0", "vanilla-1.0"),
            host.Settings.Games[0]);
    }

    [Fact]
    public void TheFirstRunShowsOnlyWithoutGamesExistingRecentProfilesACommandLineProfileOrProblem()
    {
        var normal = EditorOptions.Parse([]);
        var settings = EditorSettings.Transient();
        Assert.True(GameLinks.FirstRunApplies(normal, settings, _ => false));

        // A recent profile whose file is gone does not count.
        settings.AddRecentProfile(@"C:\gone.json", DateTime.UtcNow);
        Assert.True(GameLinks.FirstRunApplies(normal, settings, _ => false));
        Assert.False(GameLinks.FirstRunApplies(normal, settings, _ => true));

        Assert.False(GameLinks.FirstRunApplies(EditorOptions.Parse(["p.json"]), settings, _ => false));
        Assert.False(GameLinks.FirstRunApplies(EditorOptions.Parse(["--wrong"]), settings, _ => false));
        settings.Games.Add(new LinkedGame(game.Root, DateTime.UtcNow, "Freelancer 1.0", "vanilla-1.0"));
        Assert.False(GameLinks.FirstRunApplies(normal, settings, _ => false));
    }

    [Fact]
    public void NeverInTheBench()
    {
        var bench = EditorOptions.Parse(["p.json", EditorOptions.BenchOption, @"DATA\MISSIONS\M01A\m01a_intro.thn"]);

        Assert.False(GameLinks.FirstRunApplies(bench, EditorSettings.Transient(), _ => false));
    }

    /// <summary>The editor side of the game link.</summary>
    private sealed class Host : IGameLinkHost
    {
        public QueuedUIThread Ui { get; } = new();

        public EditorSettings Settings { get; } = EditorSettings.Transient();

        public ConsoleLog Log { get; } = new();

        public List<string> Profiles { get; } = [];

        public List<string> Opened { get; } = [];

        public string? Reopen { get; set; }

        public (string Title, string Message, IReadOnlyList<Choice> Choices)? Asked { get; private set; }

        public string? ProfileToReopen => Reopen;

        public IReadOnlyList<string> KnownProfiles() => Profiles.ToList();

        public void OpenProfile(string path) => Opened.Add(path);

        public void Ask(string title, string message, IReadOnlyList<Choice> choices) => Asked = (title, message, choices);

        public void QueueUIThread(Action work) => Ui.QueueUIThread(work);
    }
}
