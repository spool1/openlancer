using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The key rule of the save gate (decision D508 of the console filter design): a field-check error or a missing required
/// key that the base game has too does not block the save. It becomes a base-game warning. Each error that a mod causes
/// still blocks.
/// </summary>
public sealed class SaveGateBaseProblemTests
{
    /// <summary>The objects of Li01 with a base error: <c>bad</c> has a value that is not a number in <c>pos</c>, and
    /// <c>loose</c> has no <c>archetype</c>.</summary>
    private const string Li01Text = """
        [Object]
        nickname = Li01_01
        archetype = station1
        [Object]
        nickname = bad
        archetype = station1
        pos = 1, 2, x
        [Object]
        nickname = loose
        pos = 0, 0, 0
        [LightSource]
        nickname = light1
        pos = 0, 0, 0
        atten_curve = curve1
        """;

    private static readonly SectionRef Bad = Li01("bad");
    private static readonly SectionRef Loose = Li01("loose");
    private static readonly SectionRef Light = Li01Section("LightSource", "light1");
    private static readonly SectionRef Curve = new(@"DATA\igraph.ini", new SectionIdentity("IGraph", "curve1"));
    private static readonly SectionRef Ammo = new(EquipmentPath, new SectionIdentity("Munition", "ammo1"));

    private static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null)
    {
        var ws = ListWorkspace.Create(lowerFiles);
        ws.WriteBase(Li01Path, Li01Text);
        return ws;
    }

    /// <summary>A base error in <c>pos</c>, and the active mod changes another key: the save completes with a
    /// base-game warning. The warning keeps its section and its key, and the Console hides it by default.</summary>
    [Fact]
    public void ABaseErrorInAKeyThatNoModChangesBecomesAWarning()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var error = Assert.Single(session.Check(Bad), p => p.Severity == DiagnosticSeverity.Error);
        Assert.True(session.IsBaseGameProblem(error));

        session.Execute(EditCommand.SetLinesFromText(Bad, "loadout", ["lo_shared"]));

        var warning = Assert.Single(session.SaveProblems());
        Assert.Equal((DiagnosticSeverity.Warning, "pos", Bad), (warning.Severity, warning.Key, warning.Target));
        Assert.Equal($"{error.Message} {EditSession.BaseGameSaveSuffix}", warning.Message);
        Assert.Equal(MessageOrigin.BaseGame, warning.Origin);
        Assert.Null(session.GateRefusal());
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Contains(warning, saved.Problems);
        Assert.Equal(MessageOrigin.BaseGame, Assert.Single(SaveReport.Lines(saved, ws.ModFolder("act")),
            l => l.Message == warning.Message).Origin);
    }

    /// <summary>A missing required key of the base, and the active mod changes another key: the list error becomes a
    /// base-game warning too.</summary>
    [Fact]
    public void ABaseMissingRequiredKeyBecomesAWarning()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Loose, "loadout", ["lo_shared"]));

        var warning = Assert.Single(session.SaveProblems());
        Assert.Equal((DiagnosticSeverity.Warning, "archetype"), (warning.Severity, warning.Key));
        Assert.True(session.Save().Saved);
    }

    /// <summary>Review finding CF3 (a): the active mod writes the key again with a new line that gives the same message.
    /// The key then has the origin of the active mod, so the error blocks.</summary>
    [Fact]
    public void ANewLineOfTheActiveModWithTheSameMessageBlocks()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var before = Assert.Single(session.Check(Bad), p => p.Severity == DiagnosticSeverity.Error);

        session.Execute(EditCommand.SetLinesFromText(Bad, "pos", ["3, 4, x"]));

        var error = Assert.Single(session.SaveProblems());
        Assert.Equal((DiagnosticSeverity.Error, before.Message), (error.Severity, error.Message));
        // The base game gives the same message, so the origin does not say that the base game does not give it.
        var origin = session.SaveProblemOrigin(error);
        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.ActiveMod), origin);
        Assert.Equal("Origin: mod 'act'. Mod 'act' is the active mod, and a check of its data finds the problem.",
            origin.Text);
        Assert.False(session.Save().Saved);
    }

    /// <summary>The active mod adds the key that makes another key required: the base version has no such error, so the
    /// error blocks.</summary>
    [Fact]
    public void AKeyThatTheActiveModMakesRequiredBlocks()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Ammo, "motor", ["motor1"]));

        // The lines that name the munition become errors too (decision D82).
        var problems = session.SaveProblems();
        var error = Assert.Single(problems, p => p.Target == Ammo);
        Assert.Equal((DiagnosticSeverity.Error, "explosion_arch"), (error.Severity, error.Key));
        Assert.Equal(ValueOrigin.Base, session.Origin(Ammo, "explosion_arch").Origin);
        Assert.All(problems, p => Assert.Equal(DiagnosticSeverity.Error, p.Severity));
        Assert.False(session.Save().Saved);
    }

    /// <summary>Review finding CF3 (b): a list error of an unusable entry in a section that the active mod changes still
    /// blocks. The check reads another section, so the key rule never applies to it.</summary>
    [Fact]
    public void AListErrorOfAnUnusableEntryBlocks()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.RemoveName(Curve, "point"));
        session.Execute(EditCommand.SetLinesFromText(Light, "pos", ["5, 0, 0"]));

        var error = Assert.Single(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
        Assert.Equal("atten_curve", error.Key);
        Assert.Equal(ValueOrigin.Base, session.Origin(Light, "atten_curve").Origin);
        Assert.False(session.IsBaseGameProblem(error));
        Assert.False(session.Save().Saved);
    }

    /// <summary>Decision D518: a lower mod changes the section, so the base error blocks, as before.</summary>
    [Fact]
    public void ABaseErrorInASectionThatALowerModChangesBlocks()
    {
        using var ws = Create(lowerFiles: new() { [@"patches\" + Li01Path] = "[Object]\nnickname = bad\nloadout = lo_shared\n" });
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Bad, "reputation", ["li_n_grp"]));

        var error = Assert.Single(session.SaveProblems());
        Assert.Equal((DiagnosticSeverity.Error, "pos"), (error.Severity, error.Key));
        Assert.False(session.Save().Saved);
    }

    /// <summary>An error in a key that a lower mod writes blocks the save and has the origin of that mod (decision
    /// D518). The origin does not say that the base game does not give the message, because the save gate does not
    /// compare with the base game.</summary>
    [Fact]
    public void AnErrorInAKeyOfALowerModHasTheOriginOfThatMod()
    {
        using var ws = Create(lowerFiles: new() { [@"patches\" + Li01Path] = "[Object]\nnickname = bad\npos = 1, 2, y\n" });
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Bad, "reputation", ["li_n_grp"]));

        var error = Assert.Single(session.SaveProblems());
        Assert.Equal((DiagnosticSeverity.Error, "pos"), (error.Severity, error.Key));
        var origin = session.SaveProblemOrigin(error);
        Assert.Equal(MessageOrigin.Mod("lower", ModEvidence.Key), origin);
        Assert.Equal("Origin: mod 'lower'. Mod 'lower' changes this key, and a check of its data finds the problem.",
            origin.Text);
        Assert.False(session.Save().Saved);
    }

    /// <summary>The raw INI block shows a base-game field problem with the base-game look (decision D512). A list
    /// problem keeps its normal look, as on its row, also when it does not block the save (section 6 of the console
    /// filter design).</summary>
    [Fact]
    public void TheRawBlockGivesTheBaseGameLookOnlyToAFieldProblem()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var field = Assert.Single(SectionExtras.SectionProblems(session, Bad, session.Get(Bad)!), p => p.Problem.Key == "pos");
        var list = Assert.Single(SectionExtras.SectionProblems(session, Loose, session.Get(Loose)!),
            p => p.Problem.Key == "archetype");

        Assert.True(field.BaseGame);
        Assert.False(list.BaseGame);
        Assert.True(session.IsBaseGameProblem(list.Problem));
    }

    /// <summary>The same error in a new section of the active mod blocks, because the base game has no such
    /// section.</summary>
    [Fact]
    public void TheSameErrorInANewSectionOfTheActiveModBlocks()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.AddSection(Li01Path, "Object",
            [Line("nickname", "copy"), Line("archetype", "station1"), Line("pos", "1", "2", "x")]));

        var error = Assert.Single(session.SaveProblems());
        Assert.Equal((DiagnosticSeverity.Error, "pos", Li01("copy")), (error.Severity, error.Key, error.Target));
        Assert.False(session.Save().Saved);
    }
}
