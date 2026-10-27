using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>The raw INI block: the text of a section and the commands of an edit of it (section 13 of the M21
/// design).</summary>
public sealed class RawSectionEditTests
{
    private static readonly SectionRef Station = Li01("Li01_01");

    private static string Text(EditSession session, SectionRef section) => RawSectionEdit.Format(session.Get(section)!);

    private static string Lines(EditSession session, SectionRef section, string name) =>
        FieldEdit.Format(session.Get(section)!.Lines(name));

    [Fact]
    public void TheTextOfASectionGivesNoCommand()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var text = Text(session, Station);

        Assert.Equal("[Object]\nnickname = Li01_01\narchetype = station1\nloadout = lo_station\nreputation = li_n_grp\n" +
                     "base = Li01_01_Base\nvoice = pilot_f", text);
        var plan = RawSectionEdit.Plan(session, Station, text, text);
        Assert.Null(plan.Edit);
        Assert.Empty(plan.Problems);
        // Empty lines, spaces and the letter case of a name are no change.
        plan = RawSectionEdit.Plan(session, Station, text, text.Replace("loadout = lo_station", "\n  LOADOUT=lo_station  \n"));
        Assert.Null(plan.Edit);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void AChangedValueAndARemovedLineAreOneHistoryEntry()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var start = Text(session, Station);
        var end = start.Replace("loadout = lo_station", "loadout = lo_solar").Replace("\nvoice = pilot_f", "");

        var plan = RawSectionEdit.Plan(session, Station, start, end);
        Assert.Empty(plan.Problems);
        session.Execute(plan.Edit!.Command!);

        Assert.Equal("lo_solar", Lines(session, Station, "loadout"));
        Assert.Empty(session.Get(Station)!.Lines("voice"));
        Assert.Equal("Edit INI of [Object] nickname = Li01_01", session.UndoLabel);
        session.Undo();
        Assert.Equal("lo_station", Lines(session, Station, "loadout"));
        Assert.Equal("pilot_f", Lines(session, Station, "voice"));
        Assert.False(session.CanUndo);
    }

    /// <summary>The raw block commits its plan through the command runner, as one entry with the edit origin User. The
    /// plan names the section that it changes.</summary>
    [Fact]
    public void ARawEditRunsThroughTheCommandRunner()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var start = Text(session, Station);

        var plan = RawSectionEdit.Plan(session, Station, start, start.Replace("voice = pilot_f", "voice = pilot_m"));
        var result = FakeCommandHost.Runner(session).Run(plan.Edit!);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal([Station], plan.Edit!.Changes);
        Assert.Equal("pilot_m", Lines(session, Station, "voice"));
        Assert.Equal("Edit INI of [Object] nickname = Li01_01", session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
    }

    [Theory]
    [InlineData("[Solar]", 1, "One block holds one section.")]
    [InlineData("[Object]\n[Object]", 2, "One block holds one section.")]
    [InlineData("[Object]\n; note", 2, "A comment is not kept.")]
    [InlineData("[Object]\nno equals sign", 2, "A line must be 'name = values' or 'name ='.")]
    [InlineData("[Object]\nnickname = Li01_99", 2, "The nickname finds the section, so this block cannot change it.")]
    [InlineData("[Object]\npos = 1, 2", 2, "A 'pos' line needs 3 values")]
    public void ATextThatIsNotASectionStopsThePlanWithItsLine(string replace, int line, string message)
    {
        using var ws = Create();
        var session = ws.Session("act");
        var start = Text(session, Station);
        var end = replace.StartsWith("[Object]\nnickname")
            ? start.Replace("[Object]\nnickname = Li01_01", replace)
            : start.Replace("[Object]", replace);

        var plan = RawSectionEdit.Plan(session, Station, start, end);

        Assert.Null(plan.Edit);
        var problem = Assert.Single(plan.Problems);
        Assert.Equal(line, problem.Line);
        Assert.StartsWith(message, problem.Message);
    }

    [Fact]
    public void ALockedKeyStopsThePlan()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var system = new SectionRef(UniversePath, new SectionIdentity("system", "Li01"));
        var start = Text(session, system);

        var plan = RawSectionEdit.Plan(session, system, start, start.Replace("li01.ini", "other.ini"));

        Assert.Contains("moves all its contents", Assert.Single(plan.Problems).Message);
    }

    /// <summary>A list error does not stop a raw edit: the value goes into W, and the rows show the error.</summary>
    [Fact]
    public void AListErrorDoesNotStopThePlan()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var start = Text(session, Station);

        var plan = RawSectionEdit.Plan(session, Station, start, start.Replace("lo_station", "li_no_such_loadout"));
        session.Execute(plan.Edit!.Command!);

        Assert.Equal("li_no_such_loadout", Lines(session, Station, "loadout"));
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(session.CheckLists(Station)).Severity);
    }

    /// <summary>The plan is three-way: a change of W of a changed name after the start stops it, so the edit never
    /// undoes a change that the user did not see. A change of another name does not.</summary>
    [Fact]
    public void AChangeOfWWhileTheUserTypesStopsThePlanOnlyForAChangedName()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var start = Text(session, Station);
        var end = start.Replace("lo_station", "lo_solar");

        session.Execute(EditCommand.SetLinesFromText(Station, "voice", ["other_voice"]));
        var plan = RawSectionEdit.Plan(session, Station, start, end);
        session.Execute(plan.Edit!.Command!);
        Assert.Equal("other_voice", Lines(session, Station, "voice"));
        Assert.Equal("lo_solar", Lines(session, Station, "loadout"));

        session.Execute(EditCommand.SetLinesFromText(Station, "loadout", ["lo_shared"]));
        plan = RawSectionEdit.Plan(session, Station, start, start.Replace("lo_station", "lo_station2"));
        Assert.Null(plan.Edit);
        Assert.Equal("The section changed while you typed. Copy your text, and edit again.",
            Assert.Single(plan.Problems).Message);
    }

    /// <summary>Decision D116: the order of different names stays the file order. A new name goes at the end.</summary>
    [Fact]
    public void AMovedNameKeepsTheFileOrder()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var start = Text(session, Station);
        var end = start.Replace("\nloadout = lo_station", "") + "\nloadout = lo_station\npos = 1, 2, 3";

        session.Execute(RawSectionEdit.Plan(session, Station, start, end).Edit!.Command!);

        Assert.Equal(["nickname", "archetype", "loadout", "reputation", "base", "voice", "pos"],
            session.Get(Station)!.Entries.Select(e => e.Name));
    }
}
