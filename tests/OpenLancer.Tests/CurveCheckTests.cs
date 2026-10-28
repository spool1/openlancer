using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// Decision D34 as a list rule (decision D111): the curve rule reads the <c>[IGraph]</c> sections of W, so it runs
/// before the game data loads. A light of the active mod that names a curve that the engine cannot load is an error
/// that stops the save. The engine throws when it loads such a light, so a saved mod with it stops each later load of
/// the profile.
/// </summary>
public sealed class CurveCheckTests
{
    private const string Li01 = """
        [Object]
        nickname = Li01_depot
        pos = -38675, 0, -27705
        archetype = depot

        [LightSource]
        nickname = Light_curve
        pos = 1, 2, 3
        atten_curve = known_curve

        [LightSource]
        nickname = Light_point
        pos = 0, 0, 0
        """;

    /// <summary>The engine keeps only FLOAT graphs. It finds a curve only when exactly one graph has the name, without
    /// letter case, and it needs the points of the graph.</summary>
    private const string Graphs = """
        [IGraph]
        nickname = known_curve
        type = FLOAT
        point = 0, 1
        point = 1, 0.5
        point = 2, 0

        [IGraph]
        nickname = no_points
        type = FLOAT

        [IGraph]
        nickname = twice
        type = FLOAT
        point = 0, 1

        [IGraph]
        nickname = TWICE
        type = FLOAT
        point = 0, 1

        [IGraph]
        nickname = color_curve
        type = COLOR
        point = 0, 1, 1, 1
        """;

    private const string OtherPath = @"DATA\UNIVERSE\SYSTEMS\LI01\lights.ini";
    private const string St01Path = @"DATA\UNIVERSE\SYSTEMS\ST01\st01.ini";
    private const string GraphPath = @"DATA\igraph.ini";

    private static readonly SectionRef Point = SystemWorkspace.Li01Section("LightSource", "Light_point");

    /// <summary>The workspace with the curves, a file of St01 whose light names an unknown curve in the base data, and
    /// a file that is not a system file with such a light.</summary>
    private static TestWorkspace Create(bool graphs = true)
    {
        var ws = SystemWorkspace.Create(Li01);
        if (graphs)
        {
            ws.WriteBase(@"EXE\freelancer.ini", SystemWorkspace.FreelancerIni + "\nigraph = igraph.ini\n");
            ws.WriteBase(GraphPath, Graphs);
        }
        const string bad = "[LightSource]\nnickname = Light_bad\npos = 0, 0, 0\natten_curve = no_such_curve\n";
        ws.WriteBase(St01Path, bad);
        ws.WriteBase(OtherPath, bad);
        return ws;
    }

    [Fact]
    public void TheCurveListHoldsTheFloatGraphsOfW()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var curves = session.Lists.List("curve")!;
        // The last section with a name gives the entry.
        Assert.Equal(["known_curve", "no_points", "TWICE"], curves.Entries.Select(e => e.Value));
        // A COLOR graph with the name is not in the list.
        Assert.Null(curves.Find("color_curve"));
        Assert.True(curves.Find("KNOWN_CURVE")!.Usable);
        Assert.False(curves.Find("no_points")!.Usable);
        // A repeated FLOAT name makes the entry unusable.
        Assert.False(curves.Find("twice")!.Usable);
    }

    [Fact]
    public void AnUnknownCurveOfTheActiveModRefusesTheSaveAndNamesTheLightTheCurveAndTheFile()
    {
        using var ws = Create();
        var session = ws.Session("act");
        Assert.Empty(session.SaveProblems());

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve"]));

        var curve = Assert.Single(session.SaveProblems());
        Assert.Equal(DiagnosticSeverity.Error, curve.Severity);
        Assert.Equal(SystemWorkspace.Li01Path, curve.Source, ignoreCase: true);
        Assert.Equal((Point, "atten_curve"), (curve.Target, curve.Key));
        Assert.StartsWith("[LightSource] nickname = Light_point: 'atten_curve = no_such_curve': no curve has the name " +
                          "'no_such_curve'.", curve.Message);
        Assert.EndsWith("The game stops loading with this value.", curve.Message);
        var refused = session.GateRefusal();
        Assert.NotNull(refused);
        Assert.False(refused.Saved);
        var result = session.Save();
        Assert.False(result.Saved);
        Assert.DoesNotContain(TestWorkspace.Files(ws.ModFolder("act"), ".olsave"), f => f.Key.StartsWith("patches"));

        // An undo leaves the file edited, and W of the light has no curve again.
        session.Undo();
        Assert.Contains(SystemWorkspace.Li01Path, session.EditedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(session.SaveProblems());
        Assert.Null(session.GateRefusal());
    }

    [Theory]
    [InlineData("no_points", "has no 'point' line.")]
    [InlineData("twice", "2 sections have the name 'TWICE', so the game finds none of them.")]
    [InlineData("color_curve", "no curve has the name 'color_curve'.")]
    public void ACurveThatTheEngineCannotLoadIsAnError(string name, string problem)
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", [name]));

        var error = Assert.Single(session.SaveProblems());
        Assert.Contains(problem, error.Message);
        Assert.Equal(error, Assert.Single(session.CheckLists(Point)));
    }

    [Fact]
    public void TheCheckReadsTheCurveAsTheEngineDoesWithoutLetterCase()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["KNOWN_CURVE"]));
        Assert.Empty(session.CheckLists(Point));
        // The engine reads only the last line that has a value.
        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve", "known_curve", ""]));
        Assert.DoesNotContain(session.CheckLists(Point), p => p.Severity == DiagnosticSeverity.Error);
        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["known_curve", "no_such_curve"]));
        Assert.Single(session.CheckLists(Point));
        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve", ""]));
        Assert.Single(session.CheckLists(Point));
    }

    /// <summary>The engine reads no light of a file that is not a system file. A curve of the base data is a warning
    /// (decision D108): the active mod did not set it, so the save goes on.</summary>
    [Fact]
    public void OnlyTheLinesOfTheActiveModCanStopTheSave()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var other = new SectionRef(OtherPath, new SectionIdentity("LightSource", "Light_bad"));
        var st01 = new SectionRef(St01Path, new SectionIdentity("LightSource", "Light_bad"));

        session.Execute(EditCommand.SetLinesFromText(other, "pos", ["1, 1, 1"]));
        Assert.Empty(session.CheckLists(other));
        Assert.Empty(session.SaveProblems());

        session.Execute(EditCommand.SetLinesFromText(st01, "pos", ["1, 1, 1"]));
        var warning = Assert.Single(session.CheckLists(st01));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("The value comes from the base data.", warning.Message);
        Assert.Empty(session.SaveProblems());
        Assert.True(session.Save().Saved);
    }

    /// <summary>The check reads W, so it needs no game data. A profile without the curve file has no curve list, and
    /// the check gives no result (section 8.3 of the M21 design).</summary>
    [Fact]
    public void WithoutTheCurveFileTheListIsNotAvailable()
    {
        using var ws = Create(graphs: false);
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Point, "atten_curve", ["no_such_curve"]));

        Assert.Null(session.Lists.List("curve"));
        Assert.Empty(session.CheckLists(Point));
        Assert.Empty(session.SaveProblems());
    }
}
