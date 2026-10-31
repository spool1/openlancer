using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the mission studio on a copy of HD Edition 0.7.1 (section 18.3 of the M6a design). The scripts of
/// HD Edition are text files with comments. The tests need <c>OPENLANCER_HDE</c>, only read the folder and skip
/// without it. They hold no text of the scripts.
/// </summary>
public sealed class MissionHdeRetailTests(ITestOutputHelper output)
{
    private static readonly Lazy<MissionRunResult> Hde = new(() =>
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireHde(), "m6ahde");
        return MissionRun.Execute(workspace.Open().MissionInputs(EngineCoverage.Instance));
    });

    private static MissionRunResult Run()
    {
        GameDataPaths.RequireHde();
        return Hde.Value;
    }

    private static int Count(IEnumerable<MissionIndex> indexes, string key) =>
        indexes.Sum(i => i.Lines.Count(l => l.Trigger != null && l.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));

    private static int Comments(RawText raw) => raw.Lines.Count(l => l.TrimStart().StartsWith(';'));

    [Fact]
    public void TheIndexCountsOfTheHdScripts()
    {
        var run = Run();
        var slots = run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!).ToList();
        var indexes = slots.Select(a => a.Index).ToList();
        var triggers = indexes.SelectMany(i => i.Triggers).ToList();
        var comments = slots.Sum(a => Comments(a.RawText()));
        output.WriteLine($"triggers {triggers.Count}, actions {triggers.Sum(t => t.Actions.Count)}, conditions " +
                         $"{triggers.Sum(t => t.Conditions.Count)}, comments {comments}");

        Assert.Equal(14, slots.Count);
        Assert.Equal((2649, 12568, 2649), (triggers.Count, triggers.Sum(t => t.Actions.Count), triggers.Sum(t => t.Conditions.Count)));
        Assert.Equal((35, 66), (Count(indexes, "Act_Save"), Count(indexes, "Act_CallThorn")));
        Assert.Equal(100, triggers.Count(t => t.LinesBeforeNickname.Count > 0));
        Assert.Equal((1, 0), (indexes.Sum(i => i.Document.OfKind(MissionSectionKind.RandomEncounter).Count()),
            indexes.Sum(i => i.Document.OfKind(MissionSectionKind.MetaBehavior).Count())));
        Assert.Equal(815, comments);
    }

    /// <summary>"Other mission scripts" holds the two open single player scripts. Each has one trigger with three
    /// conditions, so only the last one counts (rule M03).</summary>
    [Fact]
    public void TheOpenSinglePlayerScriptsAreOtherScripts()
    {
        var run = Run();
        var others = run.Scripts.Where(s => s.Slot == null).ToList();

        Assert.Equal(2, others.Count);
        foreach (var script in others)
        {
            var analysis = run.Of(script.Path)!;
            var trigger = Assert.Single(analysis.Index.Triggers);
            Assert.Equal(3, trigger.Conditions.Count);
            Assert.Contains(analysis.Problems, p => p.Rule == "M03" && p.Origin!.IsBaseGame);
        }
        var rows = MissionSummary.Rows(run, showBaseGame: false);
        Assert.Equal(16, rows.Count);
        Assert.Equal(["No slot", "No slot"], rows.Skip(14).Select(r => r.Slot));
    }

    /// <summary>The raw text of each HD script is its file text with all comments, and each section round trips.</summary>
    [Fact]
    public void TheRawTextOfEachHdScriptKeepsItsCommentsAndRoundTrips()
    {
        var run = Run();

        Assert.Equal(16, run.Analyses.Count);
        foreach (var analysis in run.Analyses.Values)
        {
            var raw = analysis.RawText();
            var text = OpenLancer.Data.Ini.IniDocument.TextEncoding.GetString(analysis.Bytes);
            Assert.True(raw.IsFileText, $"{analysis.Script.Path}: {raw.CanonicalReason}");
            Assert.Equal(text.Split('\n').Count(l => l.TrimStart().StartsWith(';')), Comments(raw));
            Assert.Empty(raw.ReadOnlySections);
            Assert.All(analysis.Document.Sections, s => Assert.True(RawText.RoundTrips(analysis.Script.Path, s.Section)));
        }
    }

    [Fact]
    public void TheHdBaselineShowsNoProblemAndHasNoError()
    {
        var run = Run();
        var problems = run.Problems.ToList();
        output.WriteLine($"{problems.Count} base-game problems.");

        Assert.Equal(202, problems.Count);
        // One ship build names a pilot that the game data does not have (final finding 14 of MCP1-3).
        var build = Assert.Single(problems, p => p.Target?.Id.Type == "NPCShipArch");
        Assert.Equal(("M23", "pilot"), (build.Rule, build.Key));
        Assert.All(problems, p => Assert.True(p.Origin!.IsBaseGame, p.Message));
        Assert.DoesNotContain(problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.All(MissionSummary.Rows(run, showBaseGame: false), r => Assert.Equal((0, 0), (r.Errors, r.Warnings)));
    }
}
