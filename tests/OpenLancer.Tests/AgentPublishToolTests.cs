using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.OltoolExportTests;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>publish_checklist</c> and <c>export_mod</c> of section 7.6 of the MCP1 design. They read the saved files
/// of the active mod with the builders of the Mods window, the Export dialog and oltool. The tests run on a fake
/// Freelancer 1.0 game, as the tests of <c>oltool publish</c> and <c>oltool export</c> do.
/// </summary>
public sealed class AgentPublishToolTests
{
    /// <summary>A profile with the mod <c>ol-test</c> for the target Freelancer 1.1 only, with a summary, and an edit
    /// session of the mod.</summary>
    private static (string Profile, EditSession Session) Ready(ExportGame game)
    {
        var profile = Profile(game, targets: ", \"targets\": [ \"vanilla-1.1\" ]");
        PublishSettings.Write(Path.Combine(game.ModsFolder, "ol-test"), PublishSettings.Empty with { Summary = "A test mod." });
        return (profile, EditSession.Open(ModProfile.Load(profile), "ol-test", TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator));
    }

    /// <summary>The checklist parity test (review finding R12 of MCP1-2): the tool gives the steps and the recommended
    /// items of <c>oltool publish --json</c>.</summary>
    [Fact]
    public void TheChecklistGivesTheStepsOfOltoolPublish()
    {
        using var game = new ExportGame();
        var (profile, session) = Ready(game);
        using var _ = session;

        var tool = Ok(new TestAgentHost(session), "publish_checklist");
        var (exit, output, errors) = Run("publish", profile, "--mod", "ol-test", "--json");

        Assert.True(exit == 1, errors);
        var json = JsonNode.Parse(output)!;
        foreach (var name in new[] { "mod", "name", "version", "complete", "total", "recommended" })
            Assert.True(JsonNode.DeepEquals(json[name], tool[name]), name);
        Assert.True(JsonNode.DeepEquals(json["steps"], tool["steps"]), tool["steps"]!.ToJsonString());
        Assert.Equal(6, (int)tool["total"]!);
        Assert.Null(tool["note"]);
        Assert.Empty(tool["warnings"]!.AsArray());
    }

    /// <summary>The checklist reads the saved files and runs while the user pauses the agent edits, because it changes
    /// nothing. With unsaved edits, the result tells that the checklist does not count them. The editor host gives the
    /// game links of the editor.</summary>
    [Fact]
    public void TheChecklistReadsTheSavedFilesAlsoDuringPauseAndUsesTheGamesOfTheHost()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var report = game.Report();
        var host = new TestAgentHost(session)
        {
            TestGames = new AgentGames([report], [game.Root], [ExportTarget.Vanilla]),
            TestPaused = true,
        };
        session.Execute(EditCommand.SetLinesFromText(new SectionRef(ExportGame.GoodsPath, new SectionIdentity("Good", "lf_hull")),
            "price", ["1"]));

        var tool = Ok(host, "publish_checklist");

        var mod = session.ActiveMod!;
        var expected = PublishChecklist.Of(ChecklistInput.Read(mod, ModExport.DefaultFolder(session.Profile.FilePath, mod.Id),
            [report], [ExportTarget.Vanilla], out _));
        Assert.Equal(expected.Steps.Select(s => (s.Name, s.Complete, s.Missing.ToArray())),
            tool["steps"]!.AsArray().Select(s => ((string)s!["name"]!, (bool)s["complete"]!,
                s["missing"]!.AsArray().Select(m => (string)m!).ToArray())));
        Assert.Equal("The checklist reads the saved files. The active mod has unsaved edits, and the checklist does not " +
                     "count them.", (string?)tool["note"]);
    }
}
