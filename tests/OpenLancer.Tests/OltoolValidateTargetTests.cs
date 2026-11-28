using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary><c>oltool validate --target</c> on a fake Freelancer 1.0 (section 9.2 of the M11 design). The tests run the
/// oltool of the test folder. It only reads the game folder.</summary>
public sealed class OltoolValidateTargetTests : IDisposable
{
    private readonly ExportGame game = new();

    public OltoolValidateTargetTests() =>
        // Freelancer 1.0 by the file version of Common.dll, so the embedded file lists give the target vanilla-1.1.
        game.Game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(new Version(1, 0, 1223, 11)));

    public void Dispose() => game.Dispose();

    private static Task<(int Exit, string Output, string Errors)> RunAsync(params string[] arguments) =>
        OltoolExportTests.RunAsync(arguments);

    /// <summary>A profile on the fake game with one mod that patches the goods.</summary>
    private string Profile(string patch)
    {
        var mod = game.GoodsMod(patch);
        var profile = Path.Combine(game.Game.Folder, "profiles", "test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, JsonSerializer.Serialize(new { @base = game.Root, mods = new[] { mod.Folder } }));
        return profile;
    }

    [Fact]
    public async Task TheUsageNamesTheTargetForm()
    {
        var profile = Profile("[Good]\nnickname = g2\nprice = 300\n");

        var (exit, _, errors) = await RunAsync("validate");

        Assert.Equal(2, exit);
        Assert.Contains("validate <profile.json> --target <hde-0.7.1|vanilla-1.1> [--game <folder>] [--json]", errors);
        Assert.Equal(2, (await RunAsync("validate", profile, "--game", game.Root)).Exit);
        Assert.Equal(2, (await RunAsync("validate", profile, "--target", "vanilla-1.1", "--active", "m")).Exit);
        Assert.Equal(2, (await RunAsync("validate", profile, "--target", "unknown")).Exit);
        var openLancer = await RunAsync("validate", profile, "--target", "openlancer");
        Assert.Equal(2, openLancer.Exit);
        Assert.Contains("The target openlancer has no checks of the retail game. Run validate without --target.",
            openLancer.Errors);
        Assert.Equal(2, (await RunAsync("validate", profile, "--target", "vanilla-1.1", "--game", Path.Combine(game.Game.Folder, "none"))).Exit);
    }

    [Fact]
    public async Task AContentErrorGivesExitOne()
    {
        var profile = Profile("[Good]\nnickname = g2\nprice = 0\n");

        var (exit, output, errors) = await RunAsync("validate", profile, "--target", "vanilla-1.1");

        Assert.Equal(1, exit);
        Assert.Contains("Good 'g2': the price is 0. The game can crash when a player buys or sells the good.", errors);
        Assert.Contains("Validation failed: 1 errors", output);
        var json = await RunAsync("validate", profile, "--target", "vanilla-1.1", "--game", game.Root, "--json");
        Assert.Equal(1, json.Exit);
        Assert.Contains("the price is 0", JsonDocument.Parse(json.Output).RootElement[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CleanDataGivesExitZeroAndTheNoteOfAGameplayChange()
    {
        var profile = Profile("[Good]\nnickname = g2\nprice = 300\n");

        var (exit, output, errors) = await RunAsync("validate", profile, "--target", "vanilla-1.1", "--game", game.Root);

        Assert.True(exit == 0, errors);
        Assert.Contains($"Note: {TargetChecks.GameplayNote}", output);
        Assert.Contains("Validation OK", output);
    }

    [Fact]
    public async Task AGameWithoutTheTargetGivesExitOne()
    {
        var profile = Profile("[Good]\nnickname = g2\nprice = 300\n");

        var (exit, _, errors) = await RunAsync("validate", profile, "--target", "hde-0.7.1");

        Assert.Equal(1, exit);
        Assert.Contains("No linked game gives the target 'hde-0.7.1'. Give a game folder of Freelancer: HD Edition 0.7.1 " +
                        "with --game.", errors);
        Assert.DoesNotContain("File > Game link", errors);
    }
}
