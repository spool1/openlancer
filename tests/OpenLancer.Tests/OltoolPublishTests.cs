using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;
using static OpenLancer.Tests.OltoolExportTests;

namespace OpenLancer.Tests;

/// <summary><c>oltool publish</c> (section 15 of the M11c design). The tests run the oltool of the test folder on a
/// fake Freelancer 1.0 game.</summary>
public sealed class OltoolPublishTests
{
    /// <summary>A profile with the mod <c>ol-test</c> for the target Freelancer 1.1 only, with a summary.</summary>
    private static string Ready(ExportGame game)
    {
        var profile = Profile(game, targets: ", \"targets\": [ \"vanilla-1.1\" ]");
        PublishSettings.Write(Path.Combine(game.ModsFolder, "ol-test"), PublishSettings.Empty with { Summary = "A test mod." });
        return profile;
    }

    [Fact]
    public async Task PrintsEachStep()
    {
        using var game = new ExportGame();
        var profile = Ready(game);

        var (exit, output, errors) = await RunAsync("publish", profile, "--mod", "ol-test");

        Assert.True(exit == 1, errors);
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("Mod ol-test 1.2.0 (ol-test): 3 of 6 steps complete", lines[0]);
        Assert.Equal("1. Display name: done", lines[1]);
        Assert.Equal("2. Summary: done", lines[2]);
        Assert.Equal("3. Version: done", lines[3]);
        Assert.Equal("4. Checks for each target: OpenLancer did not check Freelancer 1.1 yet. Check the current content.", lines[4]);
        Assert.StartsWith("5. Test play in the game: Play the current content in the real game once.", lines[5]);
        Assert.StartsWith("6. Export made: Export version 1.2.0.", lines[6]);
        Assert.Equal("Recommended:", lines[7]);
        Assert.Contains("- Write a description.", lines);
    }

    [Fact]
    public async Task JsonHasEachStep()
    {
        using var game = new ExportGame();
        var profile = Ready(game);

        var (exit, output, errors) = await RunAsync("publish", profile, "--mod", "ol-test", "--json");

        Assert.True(exit == 1, errors);
        var json = JsonNode.Parse(output)!;
        Assert.Equal("ol-test", json["mod"]!.GetValue<string>());
        Assert.Equal(3, json["complete"]!.GetValue<int>());
        Assert.Equal(6, json["total"]!.GetValue<int>());
        var steps = json["steps"]!.AsArray();
        Assert.Equal(["Display name", "Summary", "Version", "Checks for each target", "Test play in the game", "Export made"],
            steps.Select(s => s!["name"]!.GetValue<string>()));
        Assert.True(steps[0]!["complete"]!.GetValue<bool>());
        Assert.Single(steps[3]!["missing"]!.AsArray());
        Assert.Contains("Write a description.", json["recommended"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(2, (await RunAsync("publish", profile, "--mod", "none")).Exit);
        Assert.Equal(2, (await RunAsync("publish", profile)).Exit);
    }

    [Fact]
    public async Task ExitsZeroOnlyWhenAllStepsAreComplete()
    {
        using var game = new ExportGame();
        var profile = Ready(game);
        var mod = Path.Combine(game.ModsFolder, "ol-test");
        var root = Path.Combine(game.Game.Folder, "exports", "ol-test");

        var export = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1");
        var withoutPlay = await RunAsync("publish", profile, "--mod", "ol-test");
        var content = new ModFingerprint().Content(ModManifest.Load(mod), mod);
        PublishHistory.Update(new HistoryLocation(root, "ol-test", mod, []), "play",
            h => h.AddPlay(new HistoryPlay(DateTime.UtcNow, null, content, false, 0, true)));
        var (exit, output, errors) = await RunAsync("publish", profile, "--mod", "ol-test");

        Assert.True(export.Exit == 0, export.Errors);
        Assert.Equal(1, withoutPlay.Exit);
        Assert.Contains("5 of 6 steps complete", withoutPlay.Output);
        Assert.True(exit == 0, output + errors);
        Assert.Contains("6 of 6 steps complete", output);
    }
}
