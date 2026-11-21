using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The checks of the <c>[system]</c> and <c>[Base]</c> sections of <c>universe.ini</c>: three errors that stop the save
/// and five warnings, each with its text. A problem of a line that the active mod does not write is a warning. The data
/// is invented.
/// </summary>
public sealed class UniverseChecksTests
{
    private static readonly SectionRef Av01 = NewSystemWorkspace.System("Av01");

    /// <summary>Adds a <c>[system]</c> to <c>universe.ini</c> with lines in their order. A comma splits the
    /// values.</summary>
    private static void AddSystem(EditSession session, params (string Name, string Text)[] lines) =>
        session.Execute(EditCommand.AddSection(ListWorkspace.UniversePath, "system",
            lines.Select(l => ListWorkspace.Line(l.Name, l.Text.Split(", "))).ToList()));

    private static Diagnostic One(EditSession session, SectionRef section, string start) =>
        Assert.Single(ObjectComponents.Checks(session, section), p => p.Message.StartsWith(start));

    [Fact]
    public void AFileLineBeforeTheNicknameLineIsAnErrorThatStopsTheSave()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        AddSystem(session, ("file", @"systems\Li01\li01.ini"), ("nickname", "Av01"), ("pos", "1, 1"));

        var problem = One(session, Av01, "The [system] Av01");
        var saved = session.Save();

        Assert.Equal((DiagnosticSeverity.Error, "file"), (problem.Severity, problem.Key));
        Assert.Equal("The [system] Av01 has its file line before its nickname line. The game then loads the system under " +
                     "another id. In a text editor, put the nickname line first in the file of its mod. Then open the profile " +
                     "again.", problem.Message);
        Assert.Empty(SteLint.Problems(problem.Message));
        Assert.False(saved.Saved);
        Assert.Contains(saved.Problems, p => p.Severity == DiagnosticSeverity.Error && p.Message == problem.Message);
    }

    /// <summary>Two systems with one nickname in another letter case are an error of the save gate. The system of the
    /// base game gets a warning.</summary>
    [Fact]
    public void TwoSystemsWithOneNicknameAreAnErrorOfTheSaveGate()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        var second = NewSystemWorkspace.System("LI01", 1);
        AddSystem(session, ("nickname", "LI01"), ("file", @"systems\Li02\li02.ini"), ("pos", "1, 1"));

        var problem = One(session, second, "The system LI01 has the same");
        var gate = session.SaveProblems().Where(p => p.Severity == DiagnosticSeverity.Error).ToList();

        Assert.Equal((DiagnosticSeverity.Error, "nickname"), (problem.Severity, problem.Key));
        Assert.Equal("The system LI01 has the same nickname as Li01. The game ignores the letter case of a nickname. The " +
                     "game drops one of the two systems. Give one system another nickname.", problem.Message);
        var error = Assert.Single(gate);
        Assert.Equal((second, "nickname", problem.Message), (error.Target, error.Key, error.Message));
        Assert.Equal(DiagnosticSeverity.Warning,
            One(session, NewSystemWorkspace.System("Li01"), "The system Li01 has the same").Severity);

        // Two sections with one spelling name the nickname once.
        session.Undo();
        AddSystem(session, ("nickname", "Li01"), ("file", @"systems\Li02\li02.ini"), ("pos", "1, 1"));
        var same = One(session, NewSystemWorkspace.System("Li01", 1), "Two [system] sections");
        Assert.Equal("Two [system] sections have the nickname Li01. The game drops one of the two systems. Give one system " +
                     "another nickname.", same.Message);
        Assert.Empty(SteLint.Problems(same.Message));
    }

    /// <summary>Two different nicknames with one FLHash give one error for each system: the FLHash error of the data
    /// lists. The universe checks do not report the same cause again.</summary>
    [Fact]
    public void TwoNicknamesWithOneFlHashGiveOneErrorForEachSystem()
    {
        const string first = "Bew9fowohqb";
        const string second = "Y54361bk";
        Assert.Equal(FLHash.CreateID(first), FLHash.CreateID(second));
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        AddSystem(session, ("nickname", first), ("file", @"systems\Li01\li01.ini"), ("pos", "1, 1"));
        AddSystem(session, ("nickname", second), ("file", @"systems\Li02\li02.ini"), ("pos", "2, 2"));

        var gate = session.SaveProblems().Where(p => p.Severity == DiagnosticSeverity.Error).ToList();

        Assert.Equal(2, gate.Count);
        foreach (var (name, other) in new[] { (first, second), (second, first) })
        {
            var system = NewSystemWorkspace.System(name);
            var error = Assert.Single(gate, p => system.Equals(p.Target));
            Assert.Equal("nickname", error.Key);
            Assert.Contains($"the name '{name}' has the FLHash of '{other}'.", error.Message);
            var agent = Assert.Single(AgentRows.DiagnosticsOf(session, system), p => p.Severity == DiagnosticSeverity.Error);
            Assert.Equal(error.Message, agent.Message);
        }
        Assert.False(session.Save().Saved);
    }

    /// <summary>A <c>file</c> that names no file is an error. The unsaved new file of a new system counts as a
    /// file.</summary>
    [Fact]
    public void AFileThatNamesNoFileIsAnErrorAndAnUnsavedNewFileCounts()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        Assert.DoesNotContain(ObjectComponents.Checks(session, Av01), p => p.Severity == DiagnosticSeverity.Error);

        session.Execute(EditCommand.SetLinesFromText(Av01, "file", [@"..\MODS\act\SYSTEMS\Av09\Av09.ini"]));

        var problem = One(session, Av01, "The system Av01 names the file");
        Assert.Equal((DiagnosticSeverity.Error, "file"), (problem.Severity, problem.Key));
        Assert.Equal(@"The system Av01 names the file ..\MODS\act\SYSTEMS\Av09\Av09.ini, and the mod has no such file. The " +
                     "game loads the system without its objects and zones, and the game can crash.", problem.Message);
        Assert.Contains(session.SaveProblems(), p => p.Message == problem.Message);
    }

    /// <summary>The save-error strip of the Inspector counts an error of a system, and the raw INI block marks the
    /// line of its key.</summary>
    [Fact]
    public void TheInspectorCountsAnErrorOfASystemAndTheRawBlockMarksItsLine()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        Assert.Equal(0, InspectorPanel.SaveErrorCount(session, Av01, session.Get(Av01)!));

        session.Execute(EditCommand.SetLinesFromText(Av01, "file", [@"..\MODS\act\SYSTEMS\Av09\Av09.ini"]));
        Assert.Equal(1, InspectorPanel.SaveErrorCount(session, Av01, session.Get(Av01)!));

        AddSystem(session, ("file", @"systems\Li01\li01.ini"), ("nickname", "Av02"), ("pos", "2, 2"));
        var av02 = NewSystemWorkspace.System("Av02");
        var order = One(session, av02, "The [system] Av02");
        var marks = SectionExtras.SectionProblems(session, av02, session.Get(av02)!).ToList();
        // Line 1 of the raw text is the header, so the file line is line 2.
        Assert.Contains(marks, m => m.Line == 2 && m.Problem.Message == order.Message && !m.BaseGame);
        Assert.Equal(1, InspectorPanel.SaveErrorCount(session, av02, session.Get(av02)!));
    }

    [Fact]
    public void APlaceOffTheMapAndTwoSystemsAtOnePlaceAreWarnings()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        AddSystem(session, ("nickname", "Av01"), ("file", @"systems\Li01\li01.ini"), ("pos", "16, 3"));
        AddSystem(session, ("nickname", "Av02"), ("file", @"systems\Li02\li02.ini"), ("pos", "7, 8"));

        var off = One(session, Av01, "The nav map shows");
        var same = One(session, NewSystemWorkspace.System("Av02"), "The systems Av02 and Li01");

        Assert.Equal((DiagnosticSeverity.Warning, "pos"), (off.Severity, off.Key));
        Assert.Equal("The nav map shows the system Av01 off the map. Use a position from 0 to 15 on each axis.", off.Message);
        Assert.Equal((DiagnosticSeverity.Warning, "pos"), (same.Severity, same.Key));
        Assert.Equal("The systems Av02 and Li01 are at the same place on the nav map. Their stars overlap.", same.Message);
    }

    /// <summary>A system that no jump gate or jump hole of another system reaches gets a warning. Two systems that name
    /// one file get a warning too.</summary>
    [Fact]
    public void ASystemWithoutAConnectionAndTwoSystemsWithOneFileAreWarnings()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        AddSystem(session, ("nickname", "Av01"), ("file", @"systems\Li01\li01.ini"), ("pos", "1, 1"));

        var alone = One(session, Av01, "No jump gate");
        var shared = One(session, Av01, "The systems Av01 and Li01 use");

        Assert.Equal((DiagnosticSeverity.Warning, "nickname"), (alone.Severity, alone.Key));
        Assert.Equal("No jump gate or jump hole leads to Av01. The player can only go there with a mission or a new game " +
                     "start.", alone.Message);
        Assert.Equal((DiagnosticSeverity.Warning, "file"), (shared.Severity, shared.Key));
        Assert.Equal(@"The systems Av01 and Li01 use the same file systems\Li01\li01.ini. The game loads its objects twice.",
            shared.Message);
        Assert.DoesNotContain(ObjectComponents.Checks(session, NewSystemWorkspace.System("Li02")),
            p => p.Message.StartsWith("No jump gate"));
        Assert.True(session.Save().Saved);
    }

    /// <summary>A <c>[Base]</c> after the <c>[system]</c> of its system gets a warning. The scene workspace has its
    /// bases after its systems.</summary>
    [Fact]
    public void ABaseAfterTheSystemOfItsSystemIsAWarning()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var late = new SectionRef(ListWorkspace.UniversePath, new SectionIdentity("Base", "Li01_01_Base"));

        var problem = One(session, late, "[Base] Li01_01_Base");

        Assert.Equal((DiagnosticSeverity.Warning, "system"), (problem.Severity, problem.Key));
        Assert.Equal("[Base] Li01_01_Base comes after the [system] of Li01. The game does not link the base to its station. " +
                     "In a text editor, move the [Base] before the [system] in the file of its mod. Then open the profile again.",
            problem.Message);
        Assert.Empty(SteLint.Problems(problem.Message));
        using var ordered = NewSystemWorkspace.Create();
        var good = ordered.Session("act");
        Assert.DoesNotContain(ObjectComponents.Checks(good, late), p => p.Message.StartsWith("[Base]"));
    }

    /// <summary>A problem of a line of the base game is a warning, also when the active mod changes another line of
    /// the system. So the save continues.</summary>
    [Fact]
    public void AProblemOfALineOfTheBaseGameIsAWarning()
    {
        using var ws = NewSystemWorkspace.Create();
        ws.WriteBase(ListWorkspace.UniversePath, """
            [Base]
            nickname = Li01_01_Base
            system = Li01
            [system]
            file = systems\Li01\li01.ini
            nickname = Li01
            pos = 7, 8
            [system]
            nickname = Li02
            file = systems\Li02\li09.ini
            pos = 6, 9
            """);
        var session = ws.Session("act");
        var li01 = NewSystemWorkspace.System("Li01");
        var li02 = NewSystemWorkspace.System("Li02");
        session.Execute(EditCommand.SetLinesFromText(li01, "pos", ["2, 2"]));
        session.Execute(EditCommand.SetLinesFromText(li02, "pos", ["3, 3"]));

        Assert.Equal(DiagnosticSeverity.Warning, One(session, li01, "The [system] Li01").Severity);
        Assert.Equal(DiagnosticSeverity.Warning, One(session, li02, "The system Li02 names the file").Severity);
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
    }

    /// <summary>The agent sees the problems of a system: get_system and check with the scope item give a place off the
    /// nav map as a warning.</summary>
    [Fact]
    public void GetSystemAndTheCheckOfTheItemGiveTheWarningsOfASystem()
    {
        using var ws = NewSystemWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));
        host.Session!.Execute(EditCommand.SetLinesFromText(NewSystemWorkspace.System("Li02"), "pos", ["20, 3"]));
        const string text = "The nav map shows the system Li02 off the map. Use a position from 0 to 15 on each axis.";

        var system = Ok(host, "get_system", """{ "system": "Li02" }""");
        var check = Ok(host, "check", """{ "scope": "item", "ref": "system:Li02" }""");

        var problem = Assert.Single(system["problems"]!.AsArray(), p => (string?)p!["message"] == text)!;
        Assert.Equal("warning", (string?)problem["severity"]);
        Assert.Equal("system:Li02", (string?)problem["ref"]);
        var pos = system["fields"]!.AsArray().Single(f => (string?)f!["key"] == "pos")!;
        Assert.Contains(pos["problems"]!.AsArray(), p => (string?)p!["message"] == text);
        Assert.Contains(check["problems"]!.AsArray(), p => (string?)p!["message"] == text);
        Assert.Equal(0, (int)check["errors"]!);
    }
}
