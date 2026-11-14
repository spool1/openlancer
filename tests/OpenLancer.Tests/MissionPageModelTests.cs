using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The models of the mission pages of the Inspector (section 10 of the M6a design), on synthetic scripts.
/// </summary>
public sealed class MissionPageModelTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    private const string Full = Story + """

        [Trigger]
        nickname = t_more
        repeatable = true
        Cnd_True = no_params
        Act_SetTitle = 1234

        [NNObjective]
        nickname = o1
        type = ids, 3001

        [MsnShip]
        nickname = s1
        """;

    private static (MissionRunResult Result, MissionAnalysis Analysis) Run(string text)
    {
        using var folder = new MissionFolder();
        folder.Write(Script, text);
        var result = MissionRun.Execute(folder.Inputs());
        return (result, result.Of(Script)!);
    }

    /// <summary>The Mission page of a run with the texts of <see cref="TextOf"/>.</summary>
    private static MissionPageModel MissionPage(MissionRunResult result, MissionAnalysis analysis, bool showBaseGame = false,
        bool hidesOpenLancer = false, Func<string, string?>? commFile = null, Func<string, string?>? systemName = null) =>
        MissionPageModel.Mission(new PageContext(result, analysis, showBaseGame, hidesOpenLancer)
        {
            Text = TextOf,
            CommFile = commFile ?? (_ => null),
            SystemName = systemName,
        });

    private static string TextOf(int id) => id switch
    {
        1234 => "Invented title",
        3001 => "Invented objective",
        _ => "",
    };

    [Fact]
    public void TheMissionPageGivesItsNamedSections()
    {
        var (result, analysis) = Run(Full.Replace("[Mission]", "[Mission]\nmission_title = 5"));

        var page = MissionPage(result, analysis);

        Assert.Equal(["slot", "overview", "problems", "openlancer", "voice", "text", "files", "advanced"],
            page.Sections.Select(s => s.Id));
        Assert.Equal("Mission", page.Kind);
        Assert.StartsWith("M01a: ", page.Title);
        Assert.NotNull(page.Completeness);
        Assert.Equal(["Title", "Objective"], page.Section("text")!.Fields.Select(f => f.Label));
        Assert.Equal(["Invented title (1234)", "Invented objective (3001)"], page.Section("text")!.Fields.Select(f => f.Value));
        Assert.Equal("Start trigger", page.Section("overview")!.Fields.Single(f => f.Value == "t_start").Label);
        var advanced = page.Section("advanced")!;
        Assert.True(advanced.Advanced);
        // The help of a key that the game does not use comes from the rule data (decision D426).
        Assert.Equal(MissionSchema.Retail.Section("Mission")!.Key("mission_title")!.Help, Assert.Single(advanced.Fields).Help);
        Assert.StartsWith("The game does not use this value.", Assert.Single(advanced.Fields).Help);
    }

    /// <summary>The systems of the Overview are chips with the names of the systems. A
    /// click on a chip opens the system.</summary>
    [Fact]
    public void TheSystemsOfTheOverviewAreChips()
    {
        var (result, analysis) = Run(Full + "\n[MsnShip]\nnickname = s2\nsystem = sys_b\n\n[MsnSolar]\nnickname = sol1\n" +
                                     "system = sys_a\n");

        var page = MissionPage(result, analysis, systemName: system => system == "sys_a" ? "Invented System" : null);

        var systems = page.Section("overview")!.Fields.Single(f => f.Label == "Systems");
        Assert.Equal(["Invented System", "sys_b"], systems.Chips.Select(c => c.Text));
        Assert.Equal(new SystemLink("sys_a"), systems.Chips[0].Link);
        Assert.Null(systems.Chips[1].Link);
        Assert.Contains("(sys_a)", systems.Chips[0].Tooltip);
    }

    /// <summary>Each kind has its glyph and its info text, and a row of the Files
    /// section does what its tooltip says.</summary>
    [Fact]
    public void EachKindHasItsGlyphAndTheFilesRowsAreLinks()
    {
        var (result, analysis) = Run(Full);
        var page = MissionPage(result, analysis);
        var schema = MissionSchema.Retail;

        Assert.Equal(Editor.Ui.EditorIcons.Route, page.Glyph);
        Assert.Equal(Editor.Ui.EditorIcons.Bolt, MissionPageModel.KindOf(MissionItemKind.Trigger, null, schema).Glyph);
        Assert.Equal(schema.Help["otherSection"], MissionPageModel.KindOf(MissionItemKind.OtherSection, null, schema).Help);
        Assert.Equal("Spawn wave", MissionPageModel.KindOf(MissionItemKind.PrefabInstance, "spawnWave|t1", schema).Help.Title);
        Assert.Equal(Editor.Ui.EditorIcons.ExclamationTriangle, page.Section("problems")!.Glyph);
        var files = page.Section("files")!.Fields.ToDictionary(f => f.Label);
        Assert.IsType<BundleLink>(files["Script"].Link);
        Assert.EndsWith("Click to open the mission.", files["Script"].Help);
        Assert.Null(files["News and rumors"].Link);
        Assert.DoesNotContain("Double-click", files["News and rumors"].Help);
    }

    /// <summary>The OpenLancer section lists the gaps of the report of the data layer.</summary>
    [Fact]
    public void TheOpenLancerSectionListsTheGaps()
    {
        var (result, analysis) = Run(Full);

        var section = MissionPage(result, analysis).Section("openlancer")!;

        Assert.Equal(MissionSummary.OpenLancer(analysis.Index).Gaps.Select(g => (g.Name, g.Text)),
            section.Fields.Select(f => (f.Label, f.Value)));
    }

    [Fact]
    public void TheOpenLancerSectionHidesForAModWithoutOpenLancer()
    {
        var (result, analysis) = Run(Full);

        var page = MissionPage(result, analysis, hidesOpenLancer: true);

        Assert.Null(page.Section("openlancer"));
    }

    [Fact]
    public void TheProblemsFollowTheToggle()
    {
        var (result, analysis) = Run(Story + "\n[Trigger]\nnickname = t_x\nCnd_True = no_params\n");

        var hidden = MissionPage(result, analysis).Section("problems")!;
        var shown = MissionPage(result, analysis, showBaseGame: true).Section("problems")!;

        Assert.Empty(hidden.Fields);
        Assert.Equal(["1 base-game message is hidden."], hidden.Notes);
        var field = Assert.Single(shown.Fields);
        // The label is the place of the problem in plain words, not the rule id (UI rule 1).
        Assert.Equal(("Trigger t_x", true, DiagnosticSeverity.Warning), (field.Label, field.Muted, field.Severity));
        Assert.IsType<LineLink>(field.Link);
        Assert.Empty(shown.Notes);
    }

    [Fact]
    public void TheTriggerPageHasItsFieldsInTheTriggerSectionAndNotInAdvanced()
    {
        var (_, analysis) = Run(Full);
        var trigger = analysis.Index.Trigger("t_more")!;

        var page = MissionPageModel.For(new PageContext(Run(Full).Result, analysis, false, false),
            new MissionRef(Script, MissionItemKind.Trigger, trigger.Section.Ref));

        Assert.Equal("Trigger", page.Kind);
        Assert.Equal("t_more", page.Title);
        var section = page.Section("trigger")!;
        Assert.Equal("Trigger", section.Title);
        Assert.False(section.Advanced);
        Assert.Equal(["Starts with the mission", "Repeats"], section.Fields.Select(f => f.Label));
        // The schema marks both fields as hard, so their labels get the help glyph.
        Assert.Equal([true, true], section.Fields.Select(f => f.Hard));
        Assert.Equal(["No", "Yes"], section.Fields.Select(f => f.Value));
        // The Advanced section holds only the compact list of the lines, not the fields that decide when the trigger
        // runs.
        Assert.DoesNotContain(page.Section("advanced")!.Fields, f => f.Label is "Starts with the mission" or "Repeats");
        Assert.Equal(trigger.Section.Ref, page.Ini);
    }

    [Fact]
    public void EachItemKindGivesAPage()
    {
        var (_, analysis) = Run(Full);
        var ship = analysis.Document.OfKind(MissionSectionKind.Ship).Single();

        foreach (var kind in Enum.GetValues<MissionItemKind>())
        {
            var page = MissionPageModel.Item(analysis, new MissionRef(Script, kind, ship.Ref), false);
            Assert.False(string.IsNullOrEmpty(page.Kind), kind.ToString());
            Assert.NotNull(page.Section("problems"));
        }
        Assert.Equal("Mission ship", MissionPageModel.Item(analysis, new MissionRef(Script, MissionItemKind.Ship, ship.Ref),
            false).Kind);
    }

    [Fact]
    public void AMissingCommLineIsAnError()
    {
        var (result, analysis) = Run(Full + "\n[Trigger]\nnickname = t_comm\nCnd_True = no_params\n" +
                                     "Act_SendComm = s1, Player, comm_known\nAct_SendComm = s1, Player, comm_gone\n");
        string? CommFile(string name) => name == "comm_known" ? @"AUDIO\invented_voice.utf" : null;

        var voice = MissionPage(result, analysis, commFile: CommFile).Section("voice")!;
        var empty = MissionPage(Run(Full).Result, Run(Full).Analysis, commFile: CommFile)
            .Section("voice")!;

        Assert.Equal(["comm_known", "comm_gone"], voice.Fields.Select(f => f.Label));
        Assert.Equal(@"AUDIO\invented_voice.utf", voice.Fields[0].Value);
        Assert.Null(voice.Fields[0].Severity);
        Assert.Equal(("Missing", DiagnosticSeverity.Error), (voice.Fields[1].Value, voice.Fields[1].Severity));
        Assert.Equal(["The script names no comm line."], empty.Notes);
    }
}
